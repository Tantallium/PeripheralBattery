using PeripheralBattery.Models;

namespace PeripheralBattery.Services;

public sealed class BatteryCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly IBatteryProvider[] _providers;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new();
    private readonly Dictionary<string, BatteryReading> _readings = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private int _disposed;
    private volatile bool _autoRefreshEnabled = true;
    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set => _autoRefreshEnabled = value;
    }
    public event Action<BatteryReading>? ReadingUpdated;
    public BatteryCoordinator(params IBatteryProvider[] providers)
    {
        _providers = providers;
        foreach (var p in providers) _gates[p.Key] = new SemaphoreSlim(1, 1);
    }
    public IReadOnlyList<BatteryReading> Snapshot()
    {
        lock (_sync) return _providers.Where(p => _readings.ContainsKey(p.Key)).Select(p => _readings[p.Key]).ToArray();
    }
    public void Start() => _loop ??= Task.Run(LoopAsync);
    private Task LoopAsync() => Task.WhenAll(_providers.Select(PollProviderAsync));
    private async Task PollProviderAsync(IBatteryProvider provider)
    {
        try
        {
            // Each device owns its timer so a slow HID/BLE read cannot delay
            // another device. An in-flight read is still protected by its gate.
            using var timer = new PeriodicTimer(RefreshInterval);
            await ReadOneAsync(provider);
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
                if (AutoRefreshEnabled) await ReadOneAsync(provider);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception e) { AppSettings.Log(e); }
    }
    public async Task RefreshAsync()
    {
        if (_shutdown.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
        await Task.WhenAll(_providers.Select(ReadOneAsync));
    }
    private async Task ReadOneAsync(IBatteryProvider provider)
    {
        var gate = _gates[provider.Key];
        if (!await gate.WaitAsync(0, _shutdown.Token)) return;
        bool releaseHere = true;
        try
        {
            if (_shutdown.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;

            // Providers can do synchronous native enumeration before their first
            // await. Always enter them on a worker, including manual UI refresh.
            // Xbox marshals only FromIdAsync back to its captured UI context.
            Task<BatteryReading> work = Task.Run(() => ReadProviderAsync(provider), _shutdown.Token);
            try
            {
                var reading = await work.WaitAsync(ReadTimeout, _shutdown.Token).ConfigureAwait(false);
                Publish(provider, reading);
            }
            catch (TimeoutException)
            {
                Publish(provider, Unavailable(provider, "读取超过 15 秒；保留上次电量，等待当前请求结束。"));
                // A timeout bounds this refresh, not the native operation. Keep
                // the gate until that operation ends so repeated clicks cannot
                // accumulate competing HID/BLE requests or blocked workers.
                releaseHere = false;
                _ = FinishLateAsync(provider, work, gate);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                releaseHere = false;
                _ = FinishLateAsync(provider, work, gate);
            }
            catch (Exception e)
            {
                AppSettings.Log(e);
                Publish(provider, Unavailable(provider, "本次读取失败：" + e.Message));
            }
        }
        finally { if (releaseHere) gate.Release(); }
    }

    private async Task<BatteryReading> ReadProviderAsync(IBatteryProvider provider)
    {
        try { return await provider.ReadAsync(_shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // A provider's own TimeoutException is a completed failure, not an
            // indication that the coordinator's 15-second deadline expired.
            AppSettings.Log(e);
            return Unavailable(provider, "本次读取失败：" + e.Message);
        }
    }

    private async Task FinishLateAsync(IBatteryProvider provider, Task<BatteryReading> work, SemaphoreSlim gate)
    {
        try
        {
            var reading = await work.ConfigureAwait(false);
            // Never turn a late response into a new online sample/notification.
            // Keep the provider's actual measurement time; do not stamp it now.
            Publish(provider, reading with
            {
                Connection = DeviceConnection.Unknown,
                Charging = null,
                Percent = reading.SampledAt.HasValue ? reading.Percent : null,
                Level = reading.SampledAt.HasValue ? reading.Level : BatteryLevel.Unknown,
                Freshness = reading.SampledAt.HasValue ? ReadingFreshness.LastKnown : ReadingFreshness.None,
                CheckedAt = DateTimeOffset.UtcNow,
                Detail = "超时请求已返回；此处保留它的历史读数，下一次读取确认连接状态。 " + reading.Detail
            });
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception e)
        {
            AppSettings.Log(e);
            Publish(provider, Unavailable(provider, "之前的读取已结束但未取得电量；将自动重试。"));
        }
        finally { gate.Release(); }
    }

    private static BatteryReading Unavailable(IBatteryProvider provider, string detail) => new()
    {
        Key = provider.Key,
        Name = provider.Name,
        Connection = DeviceConnection.Unreachable,
        Detail = detail
    };

    private void Publish(IBatteryProvider provider, BatteryReading reading)
    {
        lock (_sync)
        {
            if (_shutdown.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
            if (reading.Freshness != ReadingFreshness.Fresh && !reading.SampledAt.HasValue &&
                _readings.TryGetValue(provider.Key, out var old) && old.SampledAt.HasValue)
            {
                bool deviceAbsent = string.IsNullOrWhiteSpace(reading.DeviceId) &&
                    reading.Connection != DeviceConnection.Connected;
                bool sameDevice = !string.IsNullOrWhiteSpace(reading.DeviceId) &&
                    !string.IsNullOrWhiteSpace(old.DeviceId) &&
                    string.Equals(reading.DeviceId, old.DeviceId, StringComparison.OrdinalIgnoreCase);
                if (deviceAbsent || sameDevice)
                    reading = reading with
                    {
                        DeviceId = deviceAbsent ? old.DeviceId : reading.DeviceId,
                        Percent = old.Percent,
                        Level = old.Level,
                        Charging = null,
                        SampledAt = old.SampledAt,
                        Freshness = ReadingFreshness.LastKnown,
                        Transport = string.IsNullOrWhiteSpace(reading.Transport) ? old.Transport : reading.Transport,
                        Source = string.IsNullOrWhiteSpace(reading.Source) ? old.Source : reading.Source
                    };
            }
            _readings[provider.Key] = reading;
        }
        try { ReadingUpdated?.Invoke(reading); }
        catch (Exception e) { AppSettings.Log(e); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _shutdown.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception e) { AppSettings.Log(e); }
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        foreach (var provider in _providers)
        {
            // Also keep synchronous native cleanup away from the UI thread. A
            // defective driver cannot make the explicit Quit path wait forever.
            var dispose = Task.Run(async () =>
            {
                try { await provider.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) { AppSettings.Log(e); }
            });
            try { await dispose.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception e) { AppSettings.Log(e); }
        }
    }
}
