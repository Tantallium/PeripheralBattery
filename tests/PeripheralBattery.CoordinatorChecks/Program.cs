using System.Collections.Concurrent;
using System.Diagnostics;
using PeripheralBattery.Models;
using PeripheralBattery.Services;

await EntryRunsOnWorkerAsync();
await CacheKeepsDeviceIdentityAsync();
await ProviderTimeoutIsCompletedFailureAsync();
await AutomaticRefreshAndSwitchAsync();
await AutomaticRefreshIsolatesSlowDeviceAsync();
await TimeoutKeepsOneNativeRequestAsync();
await ShutdownDoesNotWaitForNativeReturnAsync();
Console.WriteLine("PASS: all coordinator checks (including the real 15-second deadline).");

static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

static void Check(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException("CHECK FAILED: " + detail);
}

static BatteryReading Fresh(string key, string id, int percent, DateTimeOffset sampledAt) => new()
{
    Key = key,
    Name = key,
    DeviceId = id,
    Percent = percent,
    Transport = "test transport",
    Source = "test device",
    Charging = false,
    Connection = DeviceConnection.Connected,
    Freshness = ReadingFreshness.Fresh,
    SampledAt = sampledAt,
    CheckedAt = DateTimeOffset.UtcNow
};

static async Task AutomaticRefreshAndSwitchAsync()
{
    string[] keys = ["logitech", "mchose", "xbox"];
    var clock = Stopwatch.StartNew();
    var times = keys.ToDictionary(key => key, _ => new ConcurrentQueue<TimeSpan>());
    var thirdRead = keys.ToDictionary(key => key, _ => Signal<bool>());
    var resumedRead = keys.ToDictionary(key => key, _ => Signal<bool>());
    var providers = keys.Select(key => new FakeProvider(key, _ =>
    {
        times[key].Enqueue(clock.Elapsed);
        if (times[key].Count == 3) thirdRead[key].TrySetResult(true);
        if (times[key].Count == 5) resumedRead[key].TrySetResult(true);
        return Task.FromResult(Fresh(key, key + "-A", 80, DateTimeOffset.UtcNow));
    })).ToArray();
    await using var coordinator = new BatteryCoordinator(providers);
    coordinator.Start();
    Console.WriteLine("CHECK: observing two real 5-second automatic refresh intervals for all three devices...");
    await Task.WhenAll(thirdRead.Values.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(14));
    foreach (string key in keys)
    {
        var samples = times[key].ToArray();
        for (int i = 1; i < 3; i++)
            Check(samples[i] - samples[i - 1] >= TimeSpan.FromSeconds(4) &&
                samples[i] - samples[i - 1] < TimeSpan.FromSeconds(7),
                key + " did not refresh on the 5-second cadence.");
    }

    coordinator.AutoRefreshEnabled = false;
    var pausedCounts = providers.Select(provider => provider.Calls).ToArray();
    await Task.Delay(TimeSpan.FromSeconds(6));
    Check(providers.Select(provider => provider.Calls).SequenceEqual(pausedCounts),
        "Automatic queries continued while the switch was off.");
    await coordinator.RefreshAsync();
    Check(providers.Select(provider => provider.Calls).SequenceEqual(pausedCounts.Select(count => count + 1)),
        "Manual refresh did not work while automatic refresh was off.");
    coordinator.AutoRefreshEnabled = true;
    await Task.WhenAll(resumedRead.Values.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(7));
    Check(providers.All(provider => provider.MaximumConcurrent == 1), "Automatic/manual reads overlapped for one device.");
    Console.WriteLine("PASS: all devices refresh every 5 seconds; off pauses queries, manual still works, and on resumes.");
}

static async Task AutomaticRefreshIsolatesSlowDeviceAsync()
{
    var blockedRead = Signal<BatteryReading>();
    var thirdHealthyRead = Signal<bool>();
    int healthyCalls = 0;
    var slow = new FakeProvider("mchose", _ => blockedRead.Task);
    var healthy = new FakeProvider("xbox", _ =>
    {
        if (Interlocked.Increment(ref healthyCalls) == 3) thirdHealthyRead.TrySetResult(true);
        return Task.FromResult(Fresh("xbox", "controller-A", 90, DateTimeOffset.UtcNow));
    });
    await using var coordinator = new BatteryCoordinator(slow, healthy);
    try
    {
        coordinator.Start();
        await thirdHealthyRead.Task.WaitAsync(TimeSpan.FromSeconds(14));
        Check(slow.Calls == 1 && slow.MaximumConcurrent == 1,
            "Automatic refresh stacked calls on a busy device.");
        Console.WriteLine("PASS: a blocked device does not delay another device's 5-second automatic refresh.");
    }
    finally
    {
        coordinator.AutoRefreshEnabled = false;
        blockedRead.TrySetResult(Fresh("mchose", "keyboard-A", 60, DateTimeOffset.UtcNow));
    }
}

static async Task EntryRunsOnWorkerAsync()
{
    int callerThread = Environment.CurrentManagedThreadId;
    var entered = Signal<(int Thread, bool IsPool)>();
    var release = Signal<bool>();
    var provider = new FakeProvider("mchose", _ =>
    {
        entered.TrySetResult((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
        release.Task.GetAwaiter().GetResult(); // Intentionally simulates blocking native enumeration.
        return Task.FromResult(Fresh("mchose", "keyboard-A", 80, DateTimeOffset.UtcNow));
    });
    await using var coordinator = new BatteryCoordinator(provider);
    // Makes a regression fail instead of deadlocking the test itself.
    _ = Task.Run(async () => { await Task.Delay(2000); release.TrySetResult(true); });
    var stopwatch = Stopwatch.StartNew();
    Task refresh = coordinator.RefreshAsync();
    Check(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "RefreshAsync blocked its caller before returning a task.");
    var worker = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(worker.IsPool && worker.Thread != callerThread, "Provider entry did not run on a worker thread.");
    release.TrySetResult(true);
    await refresh.WaitAsync(TimeSpan.FromSeconds(3));
    Check(provider.MaximumConcurrent == 1, "Provider was entered concurrently.");
    Console.WriteLine("PASS: synchronous provider entry stays off the calling UI thread.");
}

static async Task CacheKeepsDeviceIdentityAsync()
{
    var sampledAt = DateTimeOffset.UtcNow.AddMinutes(-4);
    var responses = new Queue<BatteryReading>(new[]
    {
        Fresh("xbox", "XBOX-A", 42, sampledAt) with { Charging = true },
        new BatteryReading { Key = "xbox", Name = "xbox", Connection = DeviceConnection.Disconnected },
        new BatteryReading { Key = "xbox", Name = "xbox", DeviceId = "xbox-a", Connection = DeviceConnection.Unreachable },
        new BatteryReading { Key = "xbox", Name = "xbox", DeviceId = "XBOX-B", Connection = DeviceConnection.Unknown },
        Fresh("xbox", "XBOX-A", 42, sampledAt),
        new BatteryReading { Key = "xbox", Name = "xbox", Connection = DeviceConnection.Connected }
    });
    var provider = new FakeProvider("xbox", _ => Task.FromResult(responses.Dequeue()));
    await using var coordinator = new BatteryCoordinator(provider);
    await coordinator.RefreshAsync();
    await coordinator.RefreshAsync();
    var absent = coordinator.Snapshot().Single();
    Check(absent.Percent == 42 && absent.SampledAt == sampledAt && absent.DeviceId == "XBOX-A",
        "Missing-device fallback lost the original value, timestamp, or identity.");
    Check(absent.Freshness == ReadingFreshness.LastKnown && absent.Charging is null,
        "Missing-device fallback looked fresh or retained an old charging state.");
    await coordinator.RefreshAsync();
    var same = coordinator.Snapshot().Single();
    Check(same.Percent == 42 && same.SampledAt == sampledAt && same.DeviceId == "xbox-a",
        "Same-ID fallback did not preserve the old measurement.");
    await coordinator.RefreshAsync();
    var other = coordinator.Snapshot().Single();
    Check(other.DeviceId == "XBOX-B" && other.Percent is null && other.SampledAt is null &&
        other.Freshness == ReadingFreshness.None, "Another device inherited the old device's battery.");
    await coordinator.RefreshAsync();
    await coordinator.RefreshAsync();
    var unidentifiedConnected = coordinator.Snapshot().Single();
    Check(unidentifiedConnected.Percent is null,
        "A connected but unidentified device was treated as an absent device and inherited old data.");
    Console.WriteLine("PASS: historical merging preserves sample time/identity and never crosses device IDs.");
}

static async Task ProviderTimeoutIsCompletedFailureAsync()
{
    var provider = new FakeProvider("mchose", _ => throw new TimeoutException("device query timed out"));
    await using var coordinator = new BatteryCoordinator(provider);
    var stopwatch = Stopwatch.StartNew();
    await coordinator.RefreshAsync();
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "A provider's own timeout became a 15-second coordinator wait.");
    Check(!coordinator.Snapshot().Single().Detail.Contains("超过 15 秒", StringComparison.Ordinal),
        "A provider's own timeout was reported as a coordinator deadline.");
    await coordinator.RefreshAsync();
    Check(provider.Calls == 2, "A completed provider failure kept its gate locked.");
    Console.WriteLine("PASS: a provider's own TimeoutException is a completed failure and releases its gate.");
}

static async Task TimeoutKeepsOneNativeRequestAsync()
{
    var oldTime = DateTimeOffset.UtcNow.AddMinutes(-5);
    var lateTime = oldTime.AddMinutes(1);
    var stalled = Signal<BatteryReading>();
    var stalledEntered = Signal<bool>();
    var healthyUpdated = Signal<bool>();
    var latePublished = Signal<BatteryReading>();
    int slowCalls = 0;
    int freshLowEvents = 0;
    var slow = new FakeProvider("mchose", _ =>
    {
        int call = Interlocked.Increment(ref slowCalls);
        if (call == 1) return Task.FromResult(Fresh("mchose", "keyboard-A", 55, oldTime));
        if (call == 2) { stalledEntered.TrySetResult(true); return stalled.Task; }
        return Task.FromResult(Fresh("mchose", "keyboard-A", 8, DateTimeOffset.UtcNow));
    });
    var healthy = new FakeProvider("xbox", _ => Task.FromResult(Fresh("xbox", "controller-A", 90, DateTimeOffset.UtcNow)));
    await using var coordinator = new BatteryCoordinator(slow, healthy);
    coordinator.ReadingUpdated += reading =>
    {
        if (reading.Key == "xbox" && healthy.Calls >= 2) healthyUpdated.TrySetResult(true);
        if (reading.Key == "mchose" && reading.Detail.StartsWith("超时请求已返回", StringComparison.Ordinal))
            latePublished.TrySetResult(reading);
        if (reading.Key == "mchose" && reading.Percent <= 10 && reading.Freshness == ReadingFreshness.Fresh &&
            reading.Connection == DeviceConnection.Connected) Interlocked.Increment(ref freshLowEvents);
    };

    await coordinator.RefreshAsync(); // Prime historical values.
    var stopwatch = Stopwatch.StartNew();
    Task timedRefresh = coordinator.RefreshAsync();
    await stalledEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await healthyUpdated.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(!timedRefresh.IsCompleted, "The fake native query was not actually pending.");
    Console.WriteLine("CHECK: waiting for the production 15-second deadline while another provider has already updated...");
    await timedRefresh.WaitAsync(TimeSpan.FromSeconds(18));
    Check(stopwatch.Elapsed >= TimeSpan.FromSeconds(14) && stopwatch.Elapsed < TimeSpan.FromSeconds(18),
        "Refresh did not observe its bounded 15-second deadline.");
    var timeout = coordinator.Snapshot().Single(r => r.Key == "mchose");
    Check(timeout.Percent == 55 && timeout.SampledAt == oldTime && timeout.DeviceId == "keyboard-A" &&
        timeout.Freshness == ReadingFreshness.LastKnown && timeout.Connection == DeviceConnection.Unreachable,
        "Timeout discarded history or presented it as a new connected sample.");

    await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => coordinator.RefreshAsync())).WaitAsync(TimeSpan.FromSeconds(3));
    Check(slow.Calls == 2 && slow.MaximumConcurrent == 1, "Repeated refresh stacked native requests after timeout.");
    Check(healthy.Calls >= 3, "A stalled provider prevented other providers from refreshing.");

    stalled.TrySetResult(Fresh("mchose", "keyboard-A", 5, lateTime) with { Charging = true });
    var late = await latePublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(late.Percent == 5 && late.SampledAt == lateTime && late.Freshness == ReadingFreshness.LastKnown &&
        late.Connection == DeviceConnection.Unknown && late.Charging is null,
        "A late response was restamped, shown as online, or retained its old charging state.");
    Check(freshLowEvents == 0, "A late low reading became eligible for a fresh low-battery notification.");

    // Publication occurs just before the gate is released. Retry briefly to avoid
    // making this assertion depend on thread-pool continuation scheduling order.
    var retryDeadline = Stopwatch.StartNew();
    while (slow.Calls < 3 && retryDeadline.Elapsed < TimeSpan.FromSeconds(3))
    {
        await coordinator.RefreshAsync();
        if (slow.Calls < 3) await Task.Delay(10);
    }
    Check(slow.Calls == 3 && slow.MaximumConcurrent == 1, "The gate was not released after the native request completed.");
    Check(coordinator.Snapshot().Single(r => r.Key == "mchose").Freshness == ReadingFreshness.Fresh,
        "A subsequent successful request could not produce a new sample.");
    Check(freshLowEvents == 1, "The subsequent fresh low reading was not eligible for a normal notification.");
    Console.WriteLine("PASS: bounded waits, independent providers, single native request, late-data safety, and gate recovery.");
}

static async Task ShutdownDoesNotWaitForNativeReturnAsync()
{
    var entered = Signal<bool>();
    var release = Signal<BatteryReading>();
    var provider = new FakeProvider("mchose", _ => { entered.TrySetResult(true); return release.Task; });
    var coordinator = new BatteryCoordinator(provider);
    int published = 0;
    coordinator.ReadingUpdated += _ => Interlocked.Increment(ref published);
    Task refresh = coordinator.RefreshAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var stopwatch = Stopwatch.StartNew();
    await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    await refresh.WaitAsync(TimeSpan.FromSeconds(3));
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(3), "Quit waited for the uncooperative native query.");
    release.TrySetResult(Fresh("mchose", "keyboard-A", 5, DateTimeOffset.UtcNow));
    await Task.Delay(100);
    Check(published == 0 && coordinator.Snapshot().Count == 0, "A late response was published after shutdown.");
    Console.WriteLine("PASS: cancellation/quit does not wait for a stuck native request and ignores later results.");
}

sealed class FakeProvider(string key, Func<CancellationToken, Task<BatteryReading>> read) : IBatteryProvider
{
    private int calls;
    private int active;
    private int maximum;
    public string Key => key;
    public string Name => key;
    public int Calls => Volatile.Read(ref calls);
    public int MaximumConcurrent => Volatile.Read(ref maximum);
    public async Task<BatteryReading> ReadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        int now = Interlocked.Increment(ref active);
        int previous;
        do { previous = Volatile.Read(ref maximum); }
        while (now > previous && Interlocked.CompareExchange(ref maximum, now, previous) != previous);
        try { return await read(cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref active); }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
