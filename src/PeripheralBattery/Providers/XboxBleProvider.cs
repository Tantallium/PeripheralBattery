using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using PeripheralBattery.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PeripheralBattery.Providers;

/// <summary>
/// Polls only an already paired Xbox controller's standard battery service.
/// Construct on the UI thread, or pass its SynchronizationContext: Microsoft's
/// BluetoothLEDevice.FromIdAsync API must be invoked on that thread. All waits
/// are asynchronous; the UI thread is never blocked. Call about once per minute.
///
/// No HID/gamepad input is opened, no pairing is performed, and no GATT writes
/// or MaintainConnection requests are made. Other BLE battery services (for
/// example Galaxy Buds) are excluded before a device handle is opened.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class XboxBleProvider : IBatteryProvider, IDisposable
{
    private static readonly Guid BatteryService = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelCharacteristic = new("00002a19-0000-1000-8000-00805f9b34fb");
    private static readonly string[] Properties =
    [
        "System.Devices.Aep.IsConnected",
        "System.Devices.HardwareIds",
        "System.Devices.DeviceInstanceId"
    ];

    // PnP uses VID_045E; BLE PnP uses Dev_VID&02045e (02 is the USB vendor-ID source).
    private static readonly Regex VendorPattern = new(
        @"(?:^|[\\#_&])VID[_&](?:02)?045E(?=$|[\\#_&])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ProductPattern = new(
        @"(?:^|[\\#_&])PID[_&]0B13(?=$|[\\#_&])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Key => "xbox";
    public string Name => "Xbox Series X 手柄";

    private const int MaximumCandidates = 4;
    private readonly object sync = new();
    private readonly SynchronizationContext? uiContext;
    private readonly TimeSpan timeout;
    private Task<BatteryReading>? inFlight;
    private BatteryReading? lastSuccessful;
    private bool disposed;

    public XboxBleProvider(SynchronizationContext? uiContext = null, TimeSpan? timeout = null)
    {
        this.uiContext = uiContext ?? SynchronizationContext.Current;
        this.timeout = timeout ?? TimeSpan.FromSeconds(12);
        if (this.timeout < TimeSpan.FromSeconds(1) || this.timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be between 1 and 30 seconds.");
    }

    /// <summary>
    /// Cancellation/timeout stops this caller's wait. Windows cannot reliably
    /// cancel a pending BLE connection operation, so its single in-flight task
    /// owns and eventually disposes its handles. Further polls reuse that task
    /// instead of accumulating native requests behind an unavailable device.
    /// </summary>
    public async Task<BatteryReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<BatteryReading> pending;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (uiContext is null)
                return Previous(DeviceConnection.Unknown, "请在桌面 UI 线程初始化蓝牙读取器。");
            if (inFlight is null || inFlight.IsCompleted)
                inFlight = ReadCoreAsync();
            pending = inFlight;
        }

        try
        {
            return await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Previous(DeviceConnection.Unknown, "蓝牙读取超时；保留上次电量，等待当前请求结束。");
        }
    }

    private async Task<BatteryReading> ReadCoreAsync()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Restrict to paired BLE endpoints, not every device on the system.
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(
                selector, Properties, DeviceInformationKind.AssociationEndpoint)
                .AsTask().ConfigureAwait(false);
            CheckBudget(started);

            string? previousId;
            lock (sync) previousId = lastSuccessful?.DeviceId;
            var candidates = devices.Where(IsXbox)
                .OrderByDescending(d => IsReportedConnected(d))
                .ThenByDescending(d => string.Equals(d.Id, previousId, StringComparison.Ordinal))
                .Take(MaximumCandidates)
                .ToArray();

            if (candidates.Length == 0)
                return Previous(DeviceConnection.Unknown, "未找到已配对的 Xbox 蓝牙手柄；请开启手柄并确认已配对。");

            BatteryReading? unavailable = null;
            foreach (var candidate in candidates)
            {
                CheckBudget(started);
                using BluetoothLEDevice? device = await OpenOnUiAsync(candidate.Id).ConfigureAwait(false);
                CheckBudget(started);
                if (device is null)
                {
                    unavailable = Previous(DeviceConnection.Unknown,
                        "无法访问 Xbox 蓝牙设备；请检查蓝牙和应用访问权限。", candidate.Id);
                    continue;
                }

                // IsConnected in device enumeration is only a sorting hint.
                // The live BluetoothLEDevice status determines the result.
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    unavailable = Previous(DeviceConnection.Disconnected,
                        "Xbox 手柄已断开或休眠。", candidate.Id);
                    continue;
                }

                return await ReadDeviceAsync(device, started).ConfigureAwait(false);
            }

            return unavailable ?? Previous(DeviceConnection.Unknown, "当前没有可读取的 Xbox 蓝牙手柄。");
        }
        catch (TimeoutException)
        {
            return Previous(DeviceConnection.Unknown, "蓝牙读取超时；本次没有取得新的电量。");
        }
        catch (OperationCanceledException)
        {
            return Previous(DeviceConnection.Unknown, "蓝牙操作已取消；本次没有取得新的电量。");
        }
        catch (Exception error)
        {
            return Previous(DeviceConnection.Unknown,
                $"蓝牙读取失败（0x{error.HResult:X8}）；本次没有取得新的电量。");
        }
    }

    private async Task<BatteryReading> ReadDeviceAsync(BluetoothLEDevice device, long started)
    {
        // Cached discovery is fine: only the service/characteristic descriptions
        // are cached. The actual battery value is ALWAYS read Uncached below.
        var services = await device.GetGattServicesForUuidAsync(BatteryService, BluetoothCacheMode.Cached)
            .AsTask().ConfigureAwait(false);
        try
        {
            CheckBudget(started);
            if (services.Status != GattCommunicationStatus.Success)
                return GattFailure(device, "发现电池服务", services.Status);

            if (services.Services.Count == 0)
            {
                // The device is known to be connected before this uncached discovery.
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    return Disconnected(device);
                services = await device.GetGattServicesForUuidAsync(BatteryService, BluetoothCacheMode.Uncached)
                    .AsTask().ConfigureAwait(false);
                CheckBudget(started);
                if (services.Status != GattCommunicationStatus.Success)
                    return GattFailure(device, "发现电池服务", services.Status);
            }

            if (services.Services.Count != 1)
                return Previous(CurrentConnection(device),
                    services.Services.Count == 0
                        ? "手柄没有可访问的标准电池服务（180F）。"
                        : "手柄报告了多个电池服务；为避免误报，本次不选择其中一个。",
                    device.DeviceId);

            GattDeviceService service = services.Services[0];
            var characteristics = await service.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristic, BluetoothCacheMode.Cached)
                .AsTask().ConfigureAwait(false);
            CheckBudget(started);
            if (characteristics.Status != GattCommunicationStatus.Success)
                return GattFailure(device, "发现电量特征", characteristics.Status);

            if (characteristics.Characteristics.Count == 0)
            {
                if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    return Disconnected(device);
                characteristics = await service.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristic, BluetoothCacheMode.Uncached)
                    .AsTask().ConfigureAwait(false);
                CheckBudget(started);
                if (characteristics.Status != GattCommunicationStatus.Success)
                    return GattFailure(device, "发现电量特征", characteristics.Status);
            }

            if (characteristics.Characteristics.Count != 1 ||
                (characteristics.Characteristics[0].CharacteristicProperties & GattCharacteristicProperties.Read) == 0)
                return Previous(CurrentConnection(device), "未找到唯一且可读的电量特征（2A19）。",
                    device.DeviceId);

            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return Disconnected(device);

            // The parameterless ReadValueAsync() reads Windows' cache and must
            // not be used as evidence of a new sample or of a live connection.
            var result = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask().ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
                return GattFailure(device, "读取电量", result.Status);
            if (result.Value is null || result.Value.Length != 1)
                return Previous(CurrentConnection(device), "电量报文长度无效；没有更新电量。",
                    device.DeviceId);

            using var reader = DataReader.FromBuffer(result.Value);
            int percent = reader.ReadByte();
            if (percent > 100)
                return Previous(CurrentConnection(device), "电量超出 0–100 范围；没有更新电量。",
                    device.DeviceId);

            var now = DateTimeOffset.UtcNow;
            var connection = CurrentConnection(device);
            var sample = new BatteryReading
            {
                Key = Key,
                Name = Name,
                Transport = "蓝牙",
                Source = "Bluetooth LE · 180F/2A19",
                DeviceId = device.DeviceId,
                Percent = percent,
                Charging = null,
                Connection = connection,
                Freshness = connection == DeviceConnection.Connected ? ReadingFreshness.Fresh : ReadingFreshness.LastKnown,
                SampledAt = now,
                CheckedAt = now,
                Detail = connection == DeviceConnection.Connected
                    ? "设备上报电量；本版本未读取蓝牙充电状态。"
                    : "刚取得电量后连接已断开；显示最后一次读数。"
            };
            lock (sync) lastSuccessful = sample;
            return sample;
        }
        finally
        {
            // If the caller timed out, this code still runs when Windows finishes
            // the pending operation. No next poll can start a competing operation.
            foreach (var service in services.Services)
                service.Dispose();
        }
    }

    private Task<BluetoothLEDevice?> OpenOnUiAsync(string deviceId)
    {
        var completion = new TaskCompletionSource<BluetoothLEDevice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext!.Post(async _ =>
        {
            try
            {
                lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
                // Invoke on the captured UI context, then await without blocking it.
                var operation = BluetoothLEDevice.FromIdAsync(deviceId);
                var device = await operation.AsTask().ConfigureAwait(false);
                completion.TrySetResult(device);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }, null);
        return completion.Task;
    }

    private static bool IsXbox(DeviceInformation device)
    {
        var ids = new List<string> { device.Id };
        foreach (string property in Properties.Skip(1))
        {
            if (!device.Properties.TryGetValue(property, out var value)) continue;
            if (value is string text) ids.Add(text);
            else if (value is IEnumerable<string> texts) ids.AddRange(texts);
        }
        return IsTargetIdentity(device.Name, ids);
    }

    internal static bool IsTargetIdentity(string? name, IEnumerable<string> identifiers)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            return true;
        // VID and PID must occur in the same identity string, not unrelated
        // properties. In particular Samsung 010075/a013 never passes this test.
        return identifiers.Any(id => VendorPattern.IsMatch(id) && ProductPattern.IsMatch(id));
    }

    private static bool IsReportedConnected(DeviceInformation device) =>
        device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var value) && value is true;

    private static DeviceConnection CurrentConnection(BluetoothLEDevice device) =>
        device.ConnectionStatus == BluetoothConnectionStatus.Connected
            ? DeviceConnection.Connected : DeviceConnection.Disconnected;

    private BatteryReading Disconnected(BluetoothLEDevice device) =>
        Previous(DeviceConnection.Disconnected, "Xbox 手柄已断开或休眠。", device.DeviceId);

    private BatteryReading GattFailure(BluetoothLEDevice device, string action, GattCommunicationStatus status) =>
        Previous(status == GattCommunicationStatus.Unreachable ? DeviceConnection.Unreachable : CurrentConnection(device),
            $"{action}失败（{status}）；本次没有取得新的电量。",
            device.DeviceId);

    private BatteryReading Previous(DeviceConnection connection, string detail, string? deviceId = null)
    {
        BatteryReading? previous;
        lock (sync) previous = lastSuccessful;
        if (deviceId is not null && previous?.DeviceId != deviceId) previous = null;
        return new BatteryReading
        {
            Key = Key,
            Name = Name,
            Transport = "蓝牙",
            Source = "Bluetooth LE · 180F/2A19",
            DeviceId = deviceId ?? previous?.DeviceId,
            Percent = previous?.Percent,
            Charging = null,
            Connection = connection,
            Freshness = previous?.Percent is not null ? ReadingFreshness.LastKnown : ReadingFreshness.None,
            SampledAt = previous?.SampledAt,
            CheckedAt = DateTimeOffset.UtcNow,
            Detail = detail
        };
    }

    private void CheckBudget(long started)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (Stopwatch.GetElapsedTime(started) >= timeout) throw new TimeoutException();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        // Never synchronously wait for a potentially pending Windows BLE call.
        lock (sync) disposed = true;
    }
}
