using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBattery.Interop;
using PeripheralBattery.Models;

namespace PeripheralBattery.Providers;

/// <summary>
/// K99 V2 receiver telemetry. Commands and model discriminator were checked against
/// the official M HUB bundle purify.es-CxLZ1Htw.js, SHA256
/// b64fdf6d0f85aa9548c8d7ddb43a7f1b39e4d662762d5e3f359e69a600fbdfae.
/// Only the model read (0x05) and battery read (0x4A) are sent. In particular,
/// this provider never opens the receiver's separate FF04 feature collection.
/// </summary>
public sealed class MchoseProvider : IBatteryProvider
{
    private const ushort VendorId = 0x3554;
    private const ushort ProductId = 0xFA09;
    private const byte ReportId = 0x13;
    private const byte ReadModelCommand = 0x05;
    private const byte ReadBatteryCommand = 0x4A;
    private const int ReportLength = 20;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
    private static readonly byte[] K99V2Signature = { 0x03, 0x00, 0x00, 0x00, 0x01, 0xDF };

    public string Key => "mchose";
    public string Name => "迈从 K99 V2";

    public async Task<BatteryReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? devicePath = null;
        string stage = "枚举接收器";
        try
        {
            // VID/PID identify a shared Compx receiver family, not a keyboard model.
            // Restrict the HID collection before making either documented read request.
            var metadataErrors = new List<string>();
            var devices = HidTransport.Enumerate(VendorId, ProductId, metadataErrors.Add)
                .Where(device => device.VendorId == VendorId && device.ProductId == ProductId
                    && device.UsagePage == 0xFF02 && device.Usage == 0x0002
                    && device.InputReportLength == ReportLength
                    && device.OutputReportLength == ReportLength
                    && device.FeatureReportLength == 0)
                .GroupBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            if (devices.Length == 0 && metadataErrors.Count != 0)
                return Unavailable(DeviceConnection.Unreachable,
                    "接收器描述信息未能完整读取：" + metadataErrors[0]);
            if (devices.Length == 0)
                return Unavailable(DeviceConnection.Disconnected,
                    "未找到匹配的 2.4G 接收器接口（3554:FA09 / FF02:0002）。");
            if (devices.Length != 1)
                return Unavailable(DeviceConnection.Unknown,
                    "找到多个相同协议接收器，暂未选择设备；请仅连接目标 K99 V2 接收器后重试。");

            devicePath = devices[0].DevicePath;
            stage = "打开接收器";
            await using var transport = HidTransport.Open(devices[0]);

            // Revalidate on every fresh handle, including after re-pairing or hot-plug.
            // M HUB calls this static six-byte model discriminator 'getPassWord'.
            stage = "读取键盘型号";
            var modelResponse = await transport.ExchangeAsync(
                MakeReadRequest(ReadModelCommand),
                response => IsResponse(response, ReadModelCommand, 6),
                QueryTimeout, cancellationToken).ConfigureAwait(false);

            if (!modelResponse.AsSpan(5, K99V2Signature.Length).SequenceEqual(K99V2Signature))
            {
                var signature = Convert.ToHexString(modelResponse.AsSpan(5, K99V2Signature.Length));
                return Unavailable(DeviceConnection.Unknown,
                    "接收器已回应，但型号签名 " + signature
                    + " 未匹配 K99 V2；已停止后续查询。", devicePath);
            }

            stage = "读取电量";
            var batteryResponse = await transport.ExchangeAsync(
                MakeReadRequest(ReadBatteryCommand),
                response => IsResponse(response, ReadBatteryCommand, 2)
                    && response[5] <= 100,
                QueryTimeout, cancellationToken).ConfigureAwait(false);

            // Official little-endian parser: low nibble fullStatus, high nibble chargeStatus.
            // Preserve unrecognized status values instead of assuming a charging state.
            int percent = batteryResponse[5];
            int fullStatus = batteryResponse[6] & 0x0F;
            int chargeStatus = batteryResponse[6] >> 4;
            bool? charging = chargeStatus switch { 0 => false, 1 => true, _ => null };
            var now = DateTimeOffset.UtcNow;
            string statusDetail = charging switch
            {
                true => "正在充电",
                false when fullStatus == 1 => "设备报告电池已满",
                false => "电池供电",
                _ => "充电状态暂无法识别"
            };

            return new BatteryReading
            {
                Key = Key,
                Name = Name,
                Transport = "2.4G 接收器",
                Percent = percent,
                Level = BatteryLevel.Unknown,
                Charging = charging,
                Connection = DeviceConnection.Connected,
                Freshness = ReadingFreshness.Fresh,
                SampledAt = now,
                CheckedAt = now,
                Source = "MCHOSE HID 0x13 / 0x4A",
                Detail = "K99 V2 型号已核验；设备估算电量；" + statusDetail
                    + "（状态 0x" + batteryResponse[6].ToString("X2") + "）。",
                DeviceId = devicePath
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return Unavailable(DeviceConnection.Unreachable,
                stage + "超时，未收到有效回复；键盘可能休眠、断开，或接口正被其他程序使用。",
                devicePath);
        }
        catch (OperationCanceledException)
        {
            return Unavailable(DeviceConnection.Unreachable,
                stage + "未完成；未取得有效电量，请稍后重试。", devicePath);
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(DeviceConnection.Unreachable,
                "接收器接口暂不可访问，请关闭 M HUB 客户端或网页驱动后重试。", devicePath);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception
            or InvalidOperationException)
        {
            return Unavailable(DeviceConnection.Unreachable,
                stage + "失败：" + exception.Message, devicePath);
        }
    }

    private BatteryReading Unavailable(DeviceConnection connection, string detail,
        string? devicePath = null) => new()
    {
        Key = Key,
        Name = Name,
        Transport = "2.4G 接收器",
        Connection = connection,
        Freshness = ReadingFreshness.None,
        CheckedAt = DateTimeOffset.UtcNow,
        Source = "MCHOSE HID",
        Detail = detail,
        DeviceId = devicePath
    };

    // The Windows WriteFile buffer includes the report ID. WebHID sends it separately.
    private static byte[] MakeReadRequest(byte command)
    {
        if (command != ReadModelCommand && command != ReadBatteryCommand)
            throw new ArgumentOutOfRangeException(nameof(command));
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = command;
        report[2] = 1;
        report[ReportLength - 1] = Checksum(report);
        return report;
    }

    private static bool IsResponse(byte[] report, byte command, byte minimumPayloadLength)
    {
        // M HUB parses the required payload prefix and permits trailing fields.
        // Check that prefix is present and the declared payload fits this frame;
        // do not assume that all firmware revisions return only the parsed bytes.
        return report.Length == ReportLength
            && report[0] == ReportId
            && report[1] == command
            && report[2] == 1
            && report[3] == 0
            && report[4] >= minimumPayloadLength
            && report[4] <= ReportLength - 6
            && report[ReportLength - 1] == Checksum(report);
    }

    private static byte Checksum(byte[] report)
    {
        int sum = 0;
        for (int index = 0; index < ReportLength - 1; index++) sum += report[index];
        return unchecked((byte)sum);
    }

    // Each read owns and closes its handle; the application's refresh coordinator
    // serializes reads and retains last-known readings when this provider is unavailable.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
