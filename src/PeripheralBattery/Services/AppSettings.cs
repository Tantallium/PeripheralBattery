using System.Text.Json;
using Microsoft.Win32;

namespace PeripheralBattery.Services;

public sealed class AppSettings
{
    public bool AlertsEnabled { get; set; } = true;
    public bool AutoRefreshEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public int WarningPercent { get; set; } = 20;
    public int CriticalPercent { get; set; } = 10;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralBattery");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
            settings.WarningPercent = Math.Clamp(settings.WarningPercent, 5, 50);
            settings.CriticalPercent = Math.Clamp(settings.CriticalPercent, 1, settings.WarningPercent - 1);
            return settings;
        }
        catch (Exception e) { Log(e); return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
    public void ApplyStartup()
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (StartWithWindows)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法取得程序路径。请输入便携版所在路径后重试。");
            run.SetValue("PeripheralBattery", "\"" + executable + "\" --startup");
        }
        else run.DeleteValue("PeripheralBattery", false);
    }
    public static void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512_000) File.Delete(path);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + error + Environment.NewLine);
        }
        catch { }
    }
}
