using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PeripheralBattery.Models;
using PeripheralBattery.Providers;
using PeripheralBattery.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PeripheralBattery;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DeviceCard> _cards = new()
    {
        new("logitech", "罗技 GPW2", "LIGHTSPEED 接收器"),
        new("mchose", "迈从 K99 V2", "2.4G 接收器"),
        new("xbox", "Xbox Series X 手柄", "蓝牙")
    };
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Dictionary<string, int> _alertStages = new();
    private readonly bool _startHidden;
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _deviceChangeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private BatteryCoordinator? _coordinator;
    private bool _exitRequested;

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        AutoRefreshCheckBox.IsChecked = _settings.AutoRefreshEnabled;
        _startHidden = startHidden;
        foreach (var card in _cards) card.SetThresholds(_settings.WarningPercent, _settings.CriticalPercent);
        DeviceList.ItemsSource = _cards;
        MaxHeight = Math.Max(540, SystemParameters.WorkArea.Height - 30);
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        _coordinator = new BatteryCoordinator(new LogitechProvider(), new MchoseProvider(), new XboxBleProvider(new DispatcherSynchronizationContext(Dispatcher)))
        { AutoRefreshEnabled = _settings.AutoRefreshEnabled };
        _coordinator.ReadingUpdated += reading => Dispatcher.BeginInvoke(() => ApplyReading(reading));
        SetupTray();
        _ageTimer.Tick += (_, _) => { foreach (var card in _cards) card.RefreshAge(); };
        _ageTimer.Start();
        _deviceChangeTimer.Tick += async (_, _) => { _deviceChangeTimer.Stop(); if (_settings.AutoRefreshEnabled) await RefreshNowAsync(); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _coordinator.Start();
        if (_startHidden) Hide();
    }
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开外设电量", null, (_, _) => Dispatcher.BeginInvoke(ShowPanel));
        menu.Items.Add("刷新", null, (_, _) => Dispatcher.BeginInvoke(async () => await RefreshNowAsync()));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync()));
        _tray = new Forms.NotifyIcon { Visible = true, Text = "外设电量 · 正在读取", ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(ShowPanel); };
        UpdateTray();
    }
    private void ShowPanel()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        foreach (var card in _cards) card.RefreshAge();
    }
    private void ApplyReading(BatteryReading reading)
    {
        if (_exitRequested) return;
        _cards.FirstOrDefault(c => c.Reading.Key == reading.Key)?.Update(reading);
        LastCheckText.Text = "最近检查 " + reading.CheckedAt.ToLocalTime().ToString("HH:mm:ss");
        UpdateTray();
        CheckAlert(reading);
    }
    private void CheckAlert(BatteryReading reading)
    {
        if (!_settings.AlertsEnabled || reading.Freshness != ReadingFreshness.Fresh || reading.Connection != DeviceConnection.Connected) return;
        int stage = 0;
        if (reading.Charging != true)
        {
            if (reading.Percent.HasValue)
                stage = reading.Percent <= _settings.CriticalPercent ? 2 : reading.Percent <= _settings.WarningPercent ? 1 : 0;
            else stage = reading.Level == BatteryLevel.Empty ? 2 : reading.Level == BatteryLevel.Low ? 1 : 0;
        }
        _alertStages.TryGetValue(reading.Key, out int previous);
        if (reading.Charging == true || reading.Percent > _settings.WarningPercent + 3 || reading.Level == BatteryLevel.Full)
            _alertStages[reading.Key] = 0;
        else if (stage > previous)
        {
            _alertStages[reading.Key] = stage;
            string value = reading.Percent.HasValue ? reading.Percent + "%" : "低电量";
            _tray?.ShowBalloonTip(5000, reading.Name + " 电量不足", "当前电量 " + value + "，请准备充电。", Forms.ToolTipIcon.Warning);
        }
    }
    private void UpdateTray()
    {
        if (_tray is null) return;
        string summary = string.Join("\n", _cards.Select(c => c.Name.Replace("罗技 ", "").Replace("迈从 ", "").Replace("Series X 手柄", "手柄") + "：" + c.Value + " " + c.Status));
        _tray.Text = summary.Length > 120 ? summary[..120] : summary;
        var valid = _cards.Select(c => c.Reading).Where(r => r.Freshness == ReadingFreshness.Fresh && r.Connection == DeviceConnection.Connected && r.Percent.HasValue).ToArray();
        int? lowest = valid.Length > 0 ? valid.Min(r => r.Percent!.Value) : null;
        var color = !lowest.HasValue ? Drawing.Color.FromArgb(130, 141, 159) : lowest <= _settings.CriticalPercent ? Drawing.Color.FromArgb(211, 73, 73) : lowest <= _settings.WarningPercent ? Drawing.Color.FromArgb(189, 123, 19) : Drawing.Color.FromArgb(64, 99, 218);
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Drawing.Pen(color, 2.5f);
            using var brush = new Drawing.SolidBrush(color);
            graphics.DrawRectangle(pen, 3, 7, 23, 18);
            graphics.FillRectangle(brush, 27, 12, 3, 8);
            if (lowest.HasValue) graphics.FillRectangle(brush, 6, 10, 17f * lowest.Value / 100f, 12);
            else graphics.FillEllipse(brush, 12, 13, 6, 6);
        }
        IntPtr handle = bitmap.GetHicon();
        Drawing.Icon icon;
        try { using var borrowed = Drawing.Icon.FromHandle(handle); icon = (Drawing.Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
        var old = _trayIcon;
        _trayIcon = icon;
        _tray.Icon = icon;
        old?.Dispose();
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
    private async Task RefreshNowAsync()
    {
        if (_coordinator is null || _exitRequested || !RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "读取中…";
        try { await _coordinator.RefreshAsync(); }
        catch (OperationCanceledException) { }
        finally { if (!_exitRequested) { RefreshButton.IsEnabled = true; RefreshButton.Content = "刷新"; } }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshNowAsync();
    private void AutoRefresh_Click(object sender, RoutedEventArgs e)
    {
        bool previous = _settings.AutoRefreshEnabled;
        _settings.AutoRefreshEnabled = AutoRefreshCheckBox.IsChecked == true;
        try { _settings.Save(); }
        catch (Exception error)
        {
            _settings.AutoRefreshEnabled = previous;
            AutoRefreshCheckBox.IsChecked = previous;
            AppSettings.Log(error);
            MessageBox.Show(this, "自动刷新设置保存失败：" + error.Message, "设置");
            return;
        }
        if (_coordinator is not null) _coordinator.AutoRefreshEnabled = _settings.AutoRefreshEnabled;
        if (!_settings.AutoRefreshEnabled) _deviceChangeTimer.Stop();
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _alertStages.Clear();
            foreach (var card in _cards) card.SetThresholds(_settings.WarningPercent, _settings.CriticalPercent);
            UpdateTray();
        }
    }
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticWindow(_coordinator?.Snapshot() ?? Array.Empty<BatteryReading>()) { Owner = this };
        dialog.ShowDialog();
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0219 && (wparam.ToInt64() == 0x8000 || wparam.ToInt64() == 0x8004 || wparam.ToInt64() == 0x0007))
        { _deviceChangeTimer.Stop(); _deviceChangeTimer.Start(); }
        return IntPtr.Zero;
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => { _deviceChangeTimer.Stop(); _deviceChangeTimer.Start(); });
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested) { e.Cancel = true; Hide(); }
    }
    private async void Exit_Click(object sender, RoutedEventArgs e) => await ExitAsync();
    private async Task ExitAsync()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _ageTimer.Stop();
        _deviceChangeTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_coordinator is not null) await _coordinator.DisposeAsync();
        _tray?.Dispose();
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
