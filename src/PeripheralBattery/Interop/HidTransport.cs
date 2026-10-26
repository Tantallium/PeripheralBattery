using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery.Interop;

public sealed record HidDeviceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    int FeatureReportLength);

/// <summary>
/// Shared, overlapped HID access restricted to vendor-defined collections.
/// Each open endpoint has exactly one input reader; requests register a matcher
/// before writing and never read keyboard/mouse input collections.
/// No feature reports or configuration helpers are exposed.
/// </summary>
public sealed class HidTransport : IDisposable, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, byte> OpenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly Task _reader;
    private PendingRequest? _pending;
    private Exception? _readerFault;
    private int _disposed;

    public HidDeviceInfo Device { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public bool HasFailed => Volatile.Read(ref _readerFault) is not null;

    private sealed class PendingRequest(Func<byte[], bool> matcher)
    {
        public Func<byte[], bool> Matcher { get; } = matcher;
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private HidTransport(HidDeviceInfo device, SafeFileHandle handle)
    {
        Device = device;
        _handle = handle;
        // Buffer size 1 disables stream buffering; HID input retains report boundaries.
        _stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
        _reader = Task.Run(ReadLoopAsync);
    }

    public static HidTransport Open(HidDeviceInfo device)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows HID is required.");
        if (device.UsagePage < 0xFF00)
            throw new ArgumentException("Only vendor-defined HID collections may be opened.", nameof(device));
        if (device.InputReportLength is < 1 or > 4096 || device.OutputReportLength is < 1 or > 4096)
            throw new ArgumentException("A bounded input/output report endpoint is required.", nameof(device));
        if (!OpenPaths.TryAdd(device.DevicePath, 0))
            throw new InvalidOperationException("This HID endpoint already has a reader in this process.");

        SafeFileHandle? handle = null;
        try
        {
            handle = Native.CreateFile(device.DevicePath, 0xC0000000,
                0x01 | 0x02, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open shared HID endpoint.");
            return new HidTransport(device, handle);
        }
        catch
        {
            handle?.Dispose();
            OpenPaths.TryRemove(device.DevicePath, out _);
            throw;
        }
    }

    /// <summary>
    /// Enumerates cached HID metadata only. Filters the path before opening a
    /// metadata handle; no manufacturer/product/serial string calls are made.
    /// Per-endpoint failures are reported through diagnostic and do not end the scan.
    /// Presence here says nothing about a receiver's wireless peripheral.
    /// </summary>
    public static IReadOnlyList<HidDeviceInfo> Enumerate(
        ushort? vendorId = null, ushort? productId = null, Action<string>? diagnostic = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows HID is required.");
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x02 | 0x10);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate HID interfaces.");
        List<HidDeviceInfo> found = [];
        try
        {
            for (uint i = 0; i < 4096; i++)
            {
                var data = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) diagnostic?.Invoke($"HID enumeration error {error}.");
                    break;
                }
                IntPtr detail = IntPtr.Zero;
                try
                {
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                    if (size is < 8 or > 1048576) throw new IOException("Invalid HID detail length.");
                    detail = Marshal.AllocHGlobal((int)size);
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (string.IsNullOrEmpty(path)) continue;
                    if (vendorId.HasValue && !path.Contains($"vid_{vendorId:X4}", StringComparison.OrdinalIgnoreCase)) continue;
                    if (productId.HasValue && !path.Contains($"pid_{productId:X4}", StringComparison.OrdinalIgnoreCase)) continue;

                    using var handle = Native.CreateFile(path, 0, 0x01 | 0x02, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var attr = new Native.Attributes { Size = Marshal.SizeOf<Native.Attributes>() };
                    if (!Native.HidD_GetAttributes(handle, ref attr)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (vendorId.HasValue && attr.VendorId != vendorId) continue;
                    if (productId.HasValue && attr.ProductId != productId) continue;
                    if (!Native.HidD_GetPreparsedData(handle, out var preparsed)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        var status = Native.HidP_GetCaps(preparsed, out var caps);
                        if (status != 0x00110000) throw new IOException($"HidP_GetCaps: 0x{status:X8}.");
                        found.Add(new(path, attr.VendorId, attr.ProductId, attr.VersionNumber,
                            caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength, caps.FeatureLength));
                    }
                    finally { Native.HidD_FreePreparsedData(preparsed); }
                }
                catch (Exception ex) { diagnostic?.Invoke($"HID interface {i}: {ex.Message}"); }
                finally { if (detail != IntPtr.Zero) Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return found;
    }

    /// <summary>
    /// Sends one complete output report and accepts only a matching reply.
    /// additionalReaders supports Logitech's separate short/long collections:
    /// both readers watch the same request, but only this endpoint writes.
    /// All waiting, including the request gate, is bounded by timeout.
    /// </summary>
    public async Task<byte[]> ExchangeAsync(byte[] output, Func<byte[], bool> matcher,
        TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyList<HidTransport>? additionalReaders = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(matcher);
        if (output.Length != Device.OutputReportLength)
            throw new ArgumentException($"Expected {Device.OutputReportLength} output bytes including report ID.", nameof(output));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive and no more than ten seconds.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        var acquired = false;
        var pending = new PendingRequest(matcher);
        List<HidTransport> registered = [];
        try
        {
            await _exchangeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            ThrowIfUnavailable();
            var readers = (additionalReaders ?? []).Prepend(this).Distinct().ToArray();
            foreach (var reader in readers)
            {
                reader.Register(pending);
                registered.Add(reader);
            }
            using var cancellation = deadline.Token.Register(() => pending.Completion.TrySetCanceled(deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
            var writeTask = _stream.WriteAsync(output.AsMemory(), deadline.Token).AsTask();
            try { await writeTask.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch
            {
                // Do not reuse an endpoint with a potentially outstanding write.
                if (!writeTask.IsCompleted) Dispose();
                _ = writeTask.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            throw new TimeoutException("HID reply timed out; the device may be asleep, busy, or unavailable.");
        }
        finally
        {
            foreach (var reader in registered) reader.Unregister(pending);
            if (acquired) _exchangeGate.Release();
        }
    }

    private void Register(PendingRequest pending)
    {
        lock (_pendingLock)
        {
            ThrowIfUnavailable();
            if (_pending is not null) throw new InvalidOperationException("An HID response is already pending on this endpoint.");
            _pending = pending;
        }
    }

    private void Unregister(PendingRequest pending)
    {
        lock (_pendingLock) { if (ReferenceEquals(_pending, pending)) _pending = null; }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_readerFault is { } fault) throw new IOException("The HID reader is unavailable.", fault);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[Device.InputReportLength];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var count = await _stream.ReadAsync(buffer.AsMemory(), _shutdown.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("The HID endpoint closed.");
                PendingRequest? pending;
                lock (_pendingLock) pending = _pending;
                if (pending is null || pending.Completion.Task.IsCompleted) continue;
                var report = buffer.AsSpan(0, count).ToArray();
                try { if (pending.Matcher(report)) pending.Completion.TrySetResult(report); }
                catch (Exception ex) { pending.Completion.TrySetException(ex); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && _shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Volatile.Write(ref _readerFault, ex);
            lock (_pendingLock) _pending?.Completion.TrySetException(new IOException("HID read failed.", ex));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        lock (_pendingLock) _pending?.Completion.TrySetException(new ObjectDisposedException(nameof(HidTransport)));
        if (!_handle.IsClosed && !_handle.IsInvalid) Native.CancelIoEx(_handle, IntPtr.Zero);
        _stream.Dispose();
        OpenPaths.TryRemove(Device.DevicePath, out _);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        // A defective device driver must not hold up closing the application.
        try { await _reader.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Attributes { public int Size; public ushort VendorId, ProductId, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Caps
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkNodes, InputButtons, InputValues, InputIndices;
            public ushort OutputButtons, OutputValues, OutputIndices;
            public ushort FeatureButtons, FeatureValues, FeatureIndices;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid guid, uint index, ref InterfaceData detail);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr deviceInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    }
}
