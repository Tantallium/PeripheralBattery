using System.Text.RegularExpressions;
using PeripheralBattery.Interop;
using PeripheralBattery.Models;

namespace PeripheralBattery.Providers;

/// <summary>
/// Read-only HID++ implementation for the user's observed 046D:C547 receiver
/// and the original PRO X SUPERLIGHT's C094 wired interface. No settings,
/// pairing, notification-enabling, or keep-alive commands are sent.
/// Protocol facts: Logitech/cpg-docs (ROOT/framing), Solaar feature inventory,
/// and fwupd's UnifiedBattery getCapabilities/getStatus definitions.
/// This implementation is original and requires real-device acceptance testing.
/// </summary>
public sealed class LogitechProvider : IBatteryProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ReceiverSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private Task<ScanResult>? _scanTask;
    private BatteryReading? _lastGood;
    private bool _disposed;

    public string Key => "logitech";
    public string Name => "罗技 GPW2";

    private sealed record ScanResult(IReadOnlyList<HidDeviceInfo> Devices, IReadOnlyList<string> Errors);

    public async Task<BatteryReading> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var acquired = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Native metadata calls run away from the UI. If an abnormal driver
            // stalls a scan, reuse that one task instead of accumulating workers.
            if (_scanTask is null || _scanTask.IsCompleted)
                _scanTask = Task.Run(() =>
                {
                    List<string> errors = [];
                    var devices = HidTransport.Enumerate(0x046D, diagnostic: errors.Add);
                    return new ScanResult(devices, errors);
                });
            var scan = await _scanTask.WaitAsync(TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
            var endpoints = scan.Devices.Where(IsSupportedEndpoint).ToArray();
            var groups = endpoints.GroupBy(x => GroupKey(x.DevicePath), StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (var old in _sessions.Keys.Except(groups.Select(x => x.Key), StringComparer.OrdinalIgnoreCase).ToArray())
            {
                _sessions[old].Dispose();
                _sessions.Remove(old);
            }
            if (groups.Length == 0)
                return Missing(scan.Errors.Count == 0 ? DeviceConnection.Disconnected : DeviceConnection.Unreachable,
                    scan.Errors.Count == 0 ? "未发现 GPW2 的接收器或有线电量接口。" : "HID 接口枚举未完成，无法确认设备状态。");

            List<string> failures = [];
            // Prefer cable telemetry when cable and receiver coexist; one UI card.
            foreach (var group in groups.OrderByDescending(g => g.First().ProductId == 0xC094))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (_sessions.TryGetValue(group.Key, out var oldSession) &&
                    (oldSession.IsBroken || !oldSession.HasSameEndpoints(group)))
                {
                    oldSession.Dispose();
                    _sessions.Remove(group.Key);
                }
                if (!_sessions.TryGetValue(group.Key, out var session))
                {
                    try
                    {
                        session = new ReceiverSession(group.ToArray());
                        _sessions.Add(group.Key, session);
                    }
                    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        failures.Add(ex.Message);
                        continue;
                    }
                }

                // C094 is the mouse itself. FF is never used as a battery target
                // on C547: there it addresses the receiver rather than its mouse.
                byte[] slots = session.IsWired ? [0xFF]
                    : session.PreferredSlot is { } known ? [known]
                    : [1, 2, 3, 4, 5, 6];
                foreach (var slot in slots)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var reading = await ReadSlotAsync(session, slot, deadline.Token).ConfigureAwait(false);
                        if (reading is null) continue;
                        session.PreferredSlot = slot;
                        _lastGood = reading;
                        return reading;
                    }
                    catch (Exception ex) when (ex is TimeoutException or IOException or HidppException)
                    {
                        failures.Add($"slot {slot}: {ex.Message}");
                        // Keep a known route on a sleeping mouse; do not sweep
                        // five unrelated slots on every scheduled refresh.
                    }
                }
            }
            var reason = failures.Count == 0 ? "设备未提供可用的电量特性。"
                : "接收器已找到，但鼠标暂无有效回应；可能休眠、忙碌或被其他程序占用。 " + failures[0];
            return Missing(DeviceConnection.Unreachable, reason);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            return Missing(DeviceConnection.Unreachable, "本次读取达到时间上限；保留最近一次有效电量。");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            return Missing(DeviceConnection.Unreachable, "暂时无法读取鼠标电量：" + ex.Message);
        }
        finally { if (acquired) _gate.Release(); }
    }

    private async Task<BatteryReading?> ReadSlotAsync(ReceiverSession session, byte slot, CancellationToken token)
    {
        var unified = await session.GetFeatureAsync(slot, 0x1004, token).ConfigureAwait(false);
        if (unified != 0)
        {
            // Function 0: levels/capabilities. Capability byte 1 bit 1 means
            // state-of-charge is supplied; absent capability must stay coarse.
            var caps = await session.RequestAsync(slot, unified, 0, [], token).ConfigureAwait(false);
            if (caps.Length < 6) throw new InvalidDataException("UnifiedBattery capability response is truncated.");
            var hasPercent = (caps[5] & 0x02) != 0;
            var status = await session.RequestAsync(slot, unified, 1, [], token).ConfigureAwait(false);
            if (status.Length < 7) throw new InvalidDataException("UnifiedBattery response is truncated.");
            if (hasPercent && status[4] > 100) throw new InvalidDataException("Battery percentage is outside 0..100.");
            int? percent = hasPercent ? status[4] : null;
            var level = percent is { } value ? LevelFromPercent(value) : status[5] switch
            {
                1 => BatteryLevel.Low, // Critical; never invent a numeric percentage.
                2 => BatteryLevel.Low,
                4 => BatteryLevel.Medium,
                8 => BatteryLevel.Full,
                _ => BatteryLevel.Unknown
            };
            bool? charging = status[6] switch { 0 => false, 1 or 2 => true, 3 => false, _ => null };
            var detail = status[6] switch
            {
                0 => "设备上报：使用电池。",
                1 => "设备上报：充电中。",
                2 => "设备上报：慢速充电。",
                3 => "设备上报：充电完成。",
                4 => "设备上报：充电错误。",
                _ => "设备上报了未识别的充电状态。"
            };
            if (!hasPercent) detail += " 固件只提供电量档位。";
            return Success(session, slot, percent, level, charging, "HID++ 0x1004", detail);
        }

        // Read-only legacy status fallback; 0 is the protocol's unknown-level
        // sentinel, so it must not be displayed as an empty battery.
        var legacy = await session.GetFeatureAsync(slot, 0x1000, token).ConfigureAwait(false);
        if (legacy == 0) return null;
        var response = await session.RequestAsync(slot, legacy, 0, [], token).ConfigureAwait(false);
        if (response.Length < 7 || response[4] > 100) throw new InvalidDataException("Invalid BatteryStatus response.");
        int? legacyPercent = response[4] == 0 ? null : response[4];
        bool? legacyCharging = response[6] switch { 0 or 3 => false, 1 or 2 or 4 => true, _ => null };
        return Success(session, slot, legacyPercent,
            legacyPercent is { } p ? LevelFromPercent(p) : BatteryLevel.Unknown,
            legacyCharging, "HID++ 0x1000", "设备上报的电量；此兼容特性的数字可能分档变化。");
    }

    private BatteryReading Success(ReceiverSession session, byte slot, int? percent,
        BatteryLevel level, bool? charging, string source, string detail)
    {
        var now = DateTimeOffset.UtcNow;
        return new BatteryReading
        {
            Key = Key, Name = Name,
            Transport = session.IsWired ? "USB 有线" : "LIGHTSPEED 2.4G",
            Percent = percent, Level = level, Charging = charging,
            Connection = DeviceConnection.Connected, Freshness = ReadingFreshness.Fresh,
            SampledAt = now, CheckedAt = now, Source = source,
            Detail = detail, DeviceId = $"046D:{session.ProductId:X4}/slot:{slot}"
        };
    }

    private BatteryReading Missing(DeviceConnection connection, string detail) =>
        (_lastGood ?? new BatteryReading { Key = Key, Name = Name, Transport = "LIGHTSPEED 2.4G", Source = "HID++" }) with
        {
            Connection = connection,
            Freshness = _lastGood is null ? ReadingFreshness.None : ReadingFreshness.LastKnown,
            Charging = null,
            CheckedAt = DateTimeOffset.UtcNow,
            Detail = detail
        };

    private static BatteryLevel LevelFromPercent(int value) => value switch
    {
        0 => BatteryLevel.Empty,
        <= 20 => BatteryLevel.Low,
        <= 70 => BatteryLevel.Medium,
        _ => BatteryLevel.Full
    };

    private static bool IsSupportedEndpoint(HidDeviceInfo device) =>
        device.VendorId == 0x046D && device.ProductId is 0xC547 or 0xC094 && device.UsagePage == 0xFF00 &&
        ((device.Usage == 1 && device.InputReportLength == 7 && device.OutputReportLength == 7) ||
         (device.Usage == 2 && device.InputReportLength == 20 && device.OutputReportLength == 20));

    private static string GroupKey(string path)
    {
        var noCollection = Regex.Replace(path, "&col[0-9a-f]{2}", "", RegexOptions.IgnoreCase);
        return Regex.Replace(noCollection, "&[0-9a-f]{4}(?=#\\{)", "", RegexOptions.IgnoreCase);
    }

    private sealed class ReceiverSession : IDisposable
    {
        private readonly List<HidTransport> _channels = [];
        private readonly Dictionary<(byte Slot, ushort Feature), byte> _features = [];
        private int _softwareId = Random.Shared.Next(1, 16);
        public byte? PreferredSlot { get; set; }
        public ushort ProductId { get; }
        public bool IsWired => ProductId == 0xC094;
        public bool IsBroken => _channels.Count == 0 || _channels.Any(x => x.HasFailed || x.IsDisposed);

        public ReceiverSession(HidDeviceInfo[] endpoints)
        {
            ProductId = endpoints[0].ProductId;
            try
            {
                foreach (var endpoint in endpoints.OrderByDescending(x => x.OutputReportLength))
                    _channels.Add(HidTransport.Open(endpoint));
            }
            catch { Dispose(); throw; }
        }

        public bool HasSameEndpoints(IEnumerable<HidDeviceInfo> devices) =>
            new HashSet<string>(_channels.Select(x => x.Device.DevicePath), StringComparer.OrdinalIgnoreCase)
                .SetEquals(devices.Select(x => x.DevicePath));

        public async Task<byte> GetFeatureAsync(byte slot, ushort feature, CancellationToken token)
        {
            if (_features.TryGetValue((slot, feature), out var index)) return index;
            var response = await RequestAsync(slot, 0, 0, [(byte)(feature >> 8), (byte)feature, 0], token).ConfigureAwait(false);
            if (response.Length < 5) throw new InvalidDataException("ROOT.GetFeature response is truncated.");
            index = response[4];
            _features[(slot, feature)] = index;
            return index;
        }

        public async Task<byte[]> RequestAsync(byte slot, byte feature, byte function,
            byte[] parameters, CancellationToken token)
        {
            if ((slot is < 1 or > 6) && !(IsWired && slot == 0xFF))
                throw new ArgumentOutOfRangeException(nameof(slot));
            if (function > 15 || parameters.Length > 3) throw new ArgumentOutOfRangeException(nameof(function));

            // Prefer the long output channel. A short-only request route is tried
            // once after timeout, while both collections keep listening.
            TimeoutException? timeout = null;
            foreach (var sender in _channels)
            {
                var sw = (byte)((Interlocked.Increment(ref _softwareId) % 15) + 1);
                var command = (byte)((function << 4) | sw);
                var output = new byte[sender.Device.OutputReportLength];
                output[0] = output.Length == 7 ? (byte)0x10 : (byte)0x11;
                output[1] = slot; output[2] = feature; output[3] = command;
                parameters.CopyTo(output, 4);
                try
                {
                    var response = await sender.ExchangeAsync(output,
                        frame => IsResponse(frame, slot, feature, command),
                        TimeSpan.FromMilliseconds(450), token, _channels).ConfigureAwait(false);
                    if (response[2] is 0xFF or 0x8F)
                        throw new HidppException($"HID++ {(response[2] == 0x8F ? "1.0" : "2.0")} error 0x{response[5]:X2}.");
                    return response;
                }
                catch (TimeoutException ex) { timeout = ex; }
            }
            throw timeout ?? new TimeoutException("No HID++ channel returned a matching reply.");
        }

        private static bool IsResponse(byte[] frame, byte slot, byte feature, byte command)
        {
            if (frame.Length != 7 && frame.Length != 20) return false;
            if ((frame[0] == 0x10 && frame.Length != 7)
                || (frame[0] == 0x11 && frame.Length != 20)
                || frame[0] is not (0x10 or 0x11) || frame[1] != slot) return false;
            if (frame[2] == feature && frame[3] == command) return true;
            if (frame[2] is 0xFF or 0x8F && frame[3] == feature && frame[4] == command)
            {
                // Some receivers emit transient HID++2 BUSY before the actual
                // long reply. HID++1 error 08 has different semantics.
                return !(frame[2] == 0xFF && frame[5] == 0x08);
            }
            return false;
        }

        public void Dispose()
        {
            foreach (var channel in _channels) channel.Dispose();
            _channels.Clear();
        }
    }

    private sealed class HidppException(string message) : Exception(message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false)) return;
        try
        {
            foreach (var session in _sessions.Values) session.Dispose();
            _sessions.Clear();
        }
        finally { _gate.Release(); }
    }
}
