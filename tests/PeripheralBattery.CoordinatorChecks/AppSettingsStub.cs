using System.Collections.Concurrent;

namespace PeripheralBattery.Services;

// Only logging is needed by the linked coordinator. No Windows registry, disk
// settings, HID, Bluetooth, or desktop UI is used by these checks.
public static class AppSettings
{
    public static ConcurrentQueue<Exception> Errors { get; } = new();
    public static void Log(Exception error) => Errors.Enqueue(error);
}
