using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PeripheralBattery.Interop;
using PeripheralBattery.Models;
using PeripheralBattery.Providers;

// Offline checks link the actual production source. They never call ReadAsync,
// Enumerate, Open, or a native method. Test-only receiver objects bypass the
// hardware-opening constructor and are passed only to the pure Success builder.
internal static class Program
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<string, (int Passed, int Failed)> Totals = [];
    private static readonly List<string> Failures = [];
    private static readonly Type MchoseType = typeof(MchoseProvider);
    private static readonly Type LogitechType = typeof(LogitechProvider);
    private static readonly Type SessionType = LogitechType.GetNestedType("ReceiverSession", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Production ReceiverSession was not found.");

    private static int Main()
    {
        try
        {
            CheckMchose();
            CheckLogitechEndpoints();
            CheckLogitechReplies();
            CheckReadingSemantics();
        }
        catch (Exception ex)
        {
            Failures.Add("Harness failed: " + ex);
        }
        foreach (var (group, count) in Totals)
            Console.WriteLine($"{group}: {count.Passed} passed, {count.Failed} failed");
        foreach (var failure in Failures) Console.WriteLine("FAIL: " + failure);
        Console.WriteLine($"TOTAL: {Totals.Values.Sum(v => v.Passed)} passed, {Failures.Count} failed");
        Console.WriteLine("NATIVE/HARDWARE CALLS: none; package sources disabled; production source linked directly.");
        Console.WriteLine("COVERAGE LIMIT: actual USB exchanges, cancellation, G HUB coexistence, hotplug and firmware behavior require hardware.");
        Console.WriteLine("COVERAGE LIMIT: MCHOSE model mismatch branch and charging-nibble interpretation are inside ReadAsync; framing, signature constant and actual battery filter are checked.");
        Console.WriteLine("COVERAGE LIMIT: Logitech capability/status decoding and correlated-error-to-exception dispatch are coupled to I/O; pure result/unknown/stale semantics are checked, not the parser branch.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void CheckMchose()
    {
        const string group = "MCHOSE framing and request vectors";
        var make = Method(MchoseType, "MakeReadRequest", PrivateStatic);
        var checksum = Method(MchoseType, "Checksum", PrivateStatic);
        var modelQuery = Invoke<byte[]>(make, null, (byte)0x05);
        var batteryQuery = Invoke<byte[]>(make, null, (byte)0x4A);
        Check(group, "model query is exact 20-byte protocol vector", () =>
            modelQuery.SequenceEqual(Convert.FromHexString("1305010000000000000000000000000000000019")));
        Check(group, "battery query is exact 20-byte protocol vector", () =>
            batteryQuery.SequenceEqual(Convert.FromHexString("134A01000000000000000000000000000000005E")));
        Check(group, "model checksum 0x19", () => Invoke<byte>(checksum, null, modelQuery) == 0x19);
        Check(group, "battery checksum 0x5E", () => Invoke<byte>(checksum, null, batteryQuery) == 0x5E);
        Check(group, "configuration command cannot be built", () =>
            Throws<ArgumentOutOfRangeException>(() => Invoke<byte[]>(make, null, (byte)0x4B)));

        byte[] signature = [0x03, 0x00, 0x00, 0x00, 0x01, 0xDF];
        var declaredSignature = (byte[])(MchoseType.GetField("K99V2Signature", PrivateStatic)?.GetValue(null)
            ?? throw new InvalidOperationException("Model signature constant is missing."));
        Check(group, "K99 V2 model discriminator matches official vector", () => declaredSignature.SequenceEqual(signature));
        var model = MchoseResponse(0x05, signature);
        var battery = MchoseResponse(0x4A, [73, 0x10]);
        Check(group, "model response framing accepted", () => MchoseMatches(model, 0x05, 6));
        Check(group, "battery response framing accepted", () => MchoseMatches(battery, 0x4A, 2));

        foreach (var length in new[] { 6, 10, 14 })
            Check(group, $"model payload {length} with extension accepted", () => MchoseMatches(MchoseResponse(0x05, signature, length), 0x05, 6));
        foreach (var length in new[] { 2, 6, 14 })
            Check(group, $"battery payload {length} with extension accepted", () => MchoseMatches(MchoseResponse(0x4A, [73, 0x10], length), 0x4A, 2));
        foreach (var length in new[] { 0, 1, 5, 15, 255 })
            Check(group, $"invalid model declared length {length} rejected", () => !MchoseMatches(MchoseResponse(0x05, signature, length), 0x05, 6));
        foreach (var length in new[] { 0, 1, 15, 255 })
            Check(group, $"invalid battery declared length {length} rejected", () => !MchoseMatches(MchoseResponse(0x4A, [73, 0x10], length), 0x4A, 2));

        foreach (var length in new[] { 0, 1, 5, 19, 21, 64 })
            Check(group, $"raw report length {length} rejected without exception", () =>
            {
                var frame = new byte[length];
                battery.AsSpan(0, Math.Min(length, battery.Length)).CopyTo(frame);
                return !MchoseMatches(frame, 0x4A, 2);
            });
        Check(group, "wrong report ID rejected with valid checksum", () => !MchoseMatches(ChangeMchose(battery, 0, 0x12), 0x4A, 2));
        Check(group, "wrong command rejected with valid checksum", () => !MchoseMatches(ChangeMchose(battery, 1, 0x05), 0x4A, 2));
        Check(group, "wrong packet count rejected with valid checksum", () => !MchoseMatches(ChangeMchose(battery, 2, 2), 0x4A, 2));
        Check(group, "wrong packet index rejected with valid checksum", () => !MchoseMatches(ChangeMchose(battery, 3, 1), 0x4A, 2));
        Check(group, "damaged checksum rejected", () => !MchoseMatches(ChangeMchose(battery, 19, (byte)(battery[19] ^ 0x80), false), 0x4A, 2));
        Check(group, "damaged payload rejected by checksum", () => !MchoseMatches(ChangeMchose(battery, 5, 74, false), 0x4A, 2));

        // Reflect the compiler's actual non-capturing lambda from ReadAsync.
        // This reaches the production response[5] <= 100 guard outside IsResponse.
        var liveBatteryFilter = FindMchoseResponseLambda(battery);
        const string policyGroup = "MCHOSE actual battery acceptance filter";
        foreach (byte percent in new byte[] { 0, 1, 73, 99, 100 })
            Check(policyGroup, $"valid {percent}% accepted", () => liveBatteryFilter(MchoseResponse(0x4A, [percent, 0])));
        foreach (byte percent in new byte[] { 101, 127, 254, 255 })
            Check(policyGroup, $"invalid {percent}% rejected by actual ReadAsync lambda", () => !liveBatteryFilter(MchoseResponse(0x4A, [percent, 0])));
        Check(policyGroup, "valid battery extension reaches actual filter", () => liveBatteryFilter(MchoseResponse(0x4A, [100, 0x01], 14)));
        Check(policyGroup, "model report cannot satisfy battery filter", () => !liveBatteryFilter(model));
        Check(policyGroup, "illegal payload is framing-valid but policy-invalid", () =>
        {
            var frame = MchoseResponse(0x4A, [101, 0]);
            return MchoseMatches(frame, 0x4A, 2) && !liveBatteryFilter(frame);
        });
        Check(policyGroup, "different model remains framing-valid (identity guard is separate)", () =>
        {
            var other = MchoseResponse(0x05, [0x03, 0, 0, 0, 1, 0xDE]);
            return MchoseMatches(other, 0x05, 6) && !other.AsSpan(5, 6).SequenceEqual(declaredSignature);
        });
    }

    private static void CheckLogitechEndpoints()
    {
        const string group = "Logitech endpoint selection and grouping";
        var key = Method(LogitechType, "GroupKey", PrivateStatic);
        // Synthetic receiver instances preserve the Windows path shape without a real device identifier.
        const string shortPath = @"\\?\hid#vid_046d&pid_c547&mi_02&col01#9&0abc123&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string longPath = @"\\?\hid#vid_046d&pid_c547&mi_02&col02#9&0abc123&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var shortKey = Invoke<string>(key, null, shortPath);
        Check(group, "synthetic short/long paths group as one receiver", () => shortKey == Invoke<string>(key, null, longPath));
        Check(group, "different receiver instance remains separate", () => shortKey != Invoke<string>(key, null, longPath.Replace("0abc123", "11b22cc")));
        Check(group, "different physical interface remains separate", () => shortKey != Invoke<string>(key, null, longPath.Replace("mi_02", "mi_03")));
        Check(group, "collection normalization handles uppercase", () => string.Equals(shortKey, Invoke<string>(key, null, longPath.ToUpperInvariant()), StringComparison.OrdinalIgnoreCase));

        var supported = Method(LogitechType, "IsSupportedEndpoint", PrivateStatic);
        var shortInfo = new HidDeviceInfo(shortPath, 0x046D, 0xC547, 0x0402, 0xFF00, 1, 7, 7, 0);
        var longInfo = new HidDeviceInfo(longPath, 0x046D, 0xC547, 0x0402, 0xFF00, 2, 20, 20, 0);
        Check(group, "actual 7-byte FF00:1 accepted", () => Invoke<bool>(supported, null, shortInfo));
        Check(group, "actual 20-byte FF00:2 accepted", () => Invoke<bool>(supported, null, longInfo));
        Check(group, "C094 wired vendor endpoint accepted", () => Invoke<bool>(supported, null, longInfo with { ProductId = 0xC094 }));
        foreach (var (name, invalid) in new (string, HidDeviceInfo)[]
        {
            ("keyboard collection", longInfo with { UsagePage = 1, Usage = 6 }),
            ("mouse input collection", longInfo with { UsagePage = 1, Usage = 2 }),
            ("consumer controls", longInfo with { UsagePage = 0x0C, Usage = 1 }),
            ("wrong vendor", longInfo with { VendorId = 0x3554 }),
            ("unverified receiver PID", longInfo with { ProductId = 0xC539 }),
            ("wrong vendor usage", longInfo with { Usage = 3 }),
            ("wrong input length", longInfo with { InputReportLength = 64 }),
            ("wrong output length", longInfo with { OutputReportLength = 0 }),
            ("mixed short/long lengths", shortInfo with { OutputReportLength = 20 })
        })
            Check(group, name + " rejected", () => !Invoke<bool>(supported, null, invalid));
    }

    private static void CheckLogitechReplies()
    {
        const string group = "Logitech reply correlation";
        byte[] longReply = [0x11, 1, 6, 0x1A, 91, 8, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] shortReply = [0x10, 1, 6, 0x1A, 91, 8, 1];
        Check(group, "valid long battery response matches", () => LogiMatches(longReply));
        Check(group, "valid short response matches", () => LogiMatches(shortReply));
        foreach (var (name, index, value) in new (string, int, byte)[]
        {
            ("wrong slot", 1, 2), ("receiver itself", 1, 0xFF),
            ("wrong feature index", 2, 7), ("wrong function", 3, 0x2A),
            ("wrong software ID", 3, 0x1B), ("unsolicited battery notification", 3, 0x10),
            ("invalid report ID", 0, 0x12)
        })
            Check(group, name + " rejected", () => !LogiMatches(Change(longReply, index, value)));
        foreach (var length in new[] { 0, 1, 3, 4, 6 })
            Check(group, $"truncated {length}-byte reply rejected", () => !LogiMatches(longReply.Take(length).ToArray()));
        // These are adversarial length checks, not copies of the current parser.
        Check(group, "long report ID with only 7 bytes rejected", () => !LogiMatches(longReply.Take(7).ToArray()));
        Check(group, "long report ID with 19 bytes rejected", () => !LogiMatches(longReply.Take(19).ToArray()));
        Check(group, "short report ID with extra byte rejected", () => !LogiMatches(shortReply.Concat(new byte[] { 0 }).ToArray()));
        Check(group, "ROOT feature 0 reply matches own request", () => LogiMatches([0x10, 1, 0, 0x0A, 6, 0, 1], 1, 0, 0x0A));

        // Correlated errors intentionally match: RequestAsync converts them to
        // exceptions. They must not be confused with a successful battery reply.
        byte[] error2 = [0x11, 1, 0xFF, 6, 0x1A, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] error1 = [0x10, 1, 0x8F, 6, 0x1A, 0x08, 0];
        Check(group, "correlated HID++2 hard error recognized for exception path", () => LogiMatches(error2));
        Check(group, "correlated HID++1 unknown-device error recognized", () => LogiMatches(error1));
        Check(group, "HID++2 transient BUSY ignored while waiting", () => !LogiMatches(Change(error2, 5, 0x08)));
        Check(group, "unrelated error software ID rejected", () => !LogiMatches(Change(error2, 4, 0x1B)));
        Check(group, "unrelated error feature rejected", () => !LogiMatches(Change(error2, 3, 7)));
        Check(group, "unrelated error slot rejected", () => !LogiMatches(Change(error1, 1, 2)));
        Check(group, "wireless notification rejected", () => !LogiMatches([0x10, 1, 0x41, 0, 0, 0, 0]));
    }

    private static void CheckReadingSemantics()
    {
        const string group = "Logitech value and freshness semantics";
        var level = Method(LogitechType, "LevelFromPercent", PrivateStatic);
        foreach (var (percent, expected) in new (int, BatteryLevel)[]
        {
            (0, BatteryLevel.Empty), (1, BatteryLevel.Low), (20, BatteryLevel.Low),
            (21, BatteryLevel.Medium), (70, BatteryLevel.Medium),
            (71, BatteryLevel.Full), (100, BatteryLevel.Full)
        })
            Check(group, $"{percent}% level classification", () => Invoke<BatteryLevel>(level, null, percent) == expected);

        var provider = new LogitechProvider(); // Managed state only; no ReadAsync call.
        var session = RuntimeHelpers.GetUninitializedObject(SessionType);
        var success = Method(LogitechType, "Success", PrivateInstance);
        var fresh = Invoke<BatteryReading>(success, provider, session, (byte)1, (int?)91,
            BatteryLevel.Full, (bool?)true, "test-vector", "test");
        Check(group, "successful value retains percentage and charging", () => fresh.Percent == 91 && fresh.Charging == true);
        Check(group, "successful reading has a sample timestamp", () => fresh.SampledAt.HasValue && fresh.Freshness == ReadingFreshness.Fresh);
        var coarse = Invoke<BatteryReading>(success, provider, session, (byte)1, null,
            BatteryLevel.Full, null, "test-vector", "test");
        Check(group, "coarse full level never fabricates 100%", () => coarse.Percent is null && coarse.Level == BatteryLevel.Full);
        Check(group, "unknown charging remains nullable", () => coarse.Charging is null);
        var unknown = Invoke<BatteryReading>(success, provider, session, (byte)1, null,
            BatteryLevel.Unknown, null, "test-vector", "test");
        Check(group, "unknown level remains unknown, not empty", () => unknown.Percent is null && unknown.Level == BatteryLevel.Unknown);

        var missing = Method(LogitechType, "Missing", PrivateInstance);
        var firstFailure = Invoke<BatteryReading>(missing, provider, DeviceConnection.Unreachable, "timeout");
        Check(group, "first failure has no fabricated sample", () => firstFailure.Percent is null && firstFailure.SampledAt is null && firstFailure.Freshness == ReadingFreshness.None);
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var last = fresh with { SampledAt = timestamp, CheckedAt = timestamp };
        LogitechType.GetField("_lastGood", PrivateInstance)!.SetValue(provider, last);
        var stale = Invoke<BatteryReading>(missing, provider, DeviceConnection.Unreachable, "timeout");
        Check(group, "timeout preserves last percentage", () => stale.Percent == 91);
        Check(group, "timeout preserves original sample timestamp", () => stale.SampledAt == timestamp);
        Check(group, "timeout changes freshness to LastKnown", () => stale.Freshness == ReadingFreshness.LastKnown);
        Check(group, "timeout is Unreachable, not Disconnected", () => stale.Connection == DeviceConnection.Unreachable);
        Check(group, "timeout clears the formerly true charging flag", () => stale.Charging is null);
        Check(group, "failed check timestamp advances independently", () => stale.CheckedAt > timestamp);
        var removed = Invoke<BatteryReading>(missing, provider, DeviceConnection.Disconnected, "receiver removed");
        Check(group, "receiver removal preserves stale sample with disconnected state", () => removed.Percent == 91 && removed.SampledAt == timestamp && removed.Connection == DeviceConnection.Disconnected && removed.Freshness == ReadingFreshness.LastKnown);
        provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); // Empty session dictionary: no hardware.
    }

    private static Func<byte[], bool> FindMchoseResponseLambda(byte[] acceptedFrame)
    {
        foreach (var nested in MchoseType.GetNestedTypes(BindingFlags.NonPublic))
        foreach (var method in nested.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var args = method.GetParameters();
            if (!method.Name.Contains("<ReadAsync>", StringComparison.Ordinal) || method.ReturnType != typeof(bool)
                || args.Length != 1 || args[0].ParameterType != typeof(byte[])) continue;
            var target = method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(nested);
            if (Invoke<bool>(method, target, acceptedFrame)) return frame => Invoke<bool>(method, target, frame);
        }
        throw new InvalidOperationException("Actual ReadAsync battery acceptance lambda was not found; do not substitute a copied predicate.");
    }

    private static bool MchoseMatches(byte[] frame, byte command, byte minimum) =>
        Invoke<bool>(Method(MchoseType, "IsResponse", PrivateStatic), null, frame, command, minimum);
    private static bool LogiMatches(byte[] frame, byte slot = 1, byte feature = 6, byte command = 0x1A) =>
        Invoke<bool>(Method(SessionType, "IsResponse", PrivateStatic), null, frame, slot, feature, command);

    private static byte[] MchoseResponse(byte command, byte[] payload, int? declaredLength = null)
    {
        var frame = new byte[20];
        frame[0] = 0x13; frame[1] = command; frame[2] = 1; frame[3] = 0;
        frame[4] = (byte)(declaredLength ?? payload.Length);
        payload.CopyTo(frame, 5);
        frame[19] = (byte)(frame.Take(19).Sum(value => value) & 0xFF);
        return frame;
    }

    private static byte[] Change(byte[] source, int index, byte value)
    {
        var result = (byte[])source.Clone(); result[index] = value; return result;
    }
    private static byte[] ChangeMchose(byte[] source, int index, byte value, bool updateChecksum = true)
    {
        var result = Change(source, index, value);
        if (updateChecksum) result[19] = (byte)(result.Take(19).Sum(b => b) & 0xFF);
        return result;
    }
    private static MethodInfo Method(Type type, string name, BindingFlags flags) =>
        type.GetMethod(name, flags) ?? throw new InvalidOperationException($"Method {type.Name}.{name} not found.");
    private static T Invoke<T>(MethodInfo method, object? target, params object?[] args)
    {
        try { return (T)method.Invoke(target, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; } catch (T) { return true; }
    }
    private static void Check(string group, string description, Func<bool> check)
    {
        var counts = Totals.GetValueOrDefault(group);
        try
        {
            if (!check()) throw new InvalidOperationException("Expected assertion was false.");
            Totals[group] = (counts.Passed + 1, counts.Failed);
        }
        catch (Exception ex)
        {
            Totals[group] = (counts.Passed, counts.Failed + 1);
            Failures.Add(group + " / " + description + ": " + ex.Message);
        }
    }
}
