namespace PeripheralBattery.Models;

public enum DeviceConnection { Unknown, Connected, Disconnected, Unreachable }
public enum ReadingFreshness { None, Fresh, LastKnown }
public enum BatteryLevel { Unknown, Empty, Low, Medium, Full }

public sealed record BatteryReading
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Transport { get; init; } = "";
    public int? Percent { get; init; }
    public BatteryLevel Level { get; init; } = BatteryLevel.Unknown;
    public bool? Charging { get; init; }
    public DeviceConnection Connection { get; init; } = DeviceConnection.Unknown;
    public ReadingFreshness Freshness { get; init; } = ReadingFreshness.None;
    public DateTimeOffset? SampledAt { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Source { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? DeviceId { get; init; }
}

public interface IBatteryProvider : IAsyncDisposable
{
    string Key { get; }
    string Name { get; }
    Task<BatteryReading> ReadAsync(CancellationToken cancellationToken);
}
