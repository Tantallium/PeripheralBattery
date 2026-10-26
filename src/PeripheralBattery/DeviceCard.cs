using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PeripheralBattery.Models;

namespace PeripheralBattery;

public sealed class DeviceCard : INotifyPropertyChanged
{
    private int _warningPercent = 20;
    private int _criticalPercent = 10;
    public BatteryReading Reading { get; private set; }
    public DeviceCard(string key, string name, string transport)
        => Reading = new BatteryReading { Key = key, Name = name, Transport = transport };
    public string Name => Reading.Name;
    public string Transport => Reading.Transport;
    public string Value => Reading.Percent.HasValue ? Reading.Percent.Value.ToString() + "%" : Reading.Level switch
    { BatteryLevel.Full => "满", BatteryLevel.Medium => "中等", BatteryLevel.Low => "低", BatteryLevel.Empty => "空", _ => "—" };
    public double BarValue => Reading.Percent ?? Reading.Level switch
    { BatteryLevel.Full => 100, BatteryLevel.Medium => 65, BatteryLevel.Low => 25, BatteryLevel.Empty => 0, _ => 0 };
    public string Status => Reading.Connection switch
    {
        DeviceConnection.Disconnected => "已断开",
        DeviceConnection.Unreachable => "暂未响应",
        _ when Reading.Freshness == ReadingFreshness.LastKnown => "上次读数",
        _ when Reading.Charging == true => "充电中",
        _ when Reading.Freshness == ReadingFreshness.Fresh => "已连接",
        _ => "等待电量"
    };
    public string Updated
    {
        get
        {
            if (!Reading.SampledAt.HasValue) return "尚无有效读数";
            var age = DateTimeOffset.UtcNow - Reading.SampledAt.Value;
            string text = age.TotalSeconds < 60 ? "刚刚更新" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} 分钟前" : $"{(int)age.TotalHours} 小时前";
            return Reading.Freshness == ReadingFreshness.LastKnown ? "上次有效电量 · " + text : text;
        }
    }
    public string Hint => Reading.Freshness == ReadingFreshness.LastKnown ? "保留上次有效电量，等待设备重新回应。" : Reading.Connection switch
    {
        DeviceConnection.Disconnected => "连接或打开设备后会自动重试。",
        DeviceConnection.Unreachable => "唤醒设备后可点刷新；详细原因见诊断信息。",
        _ when Reading.Freshness == ReadingFreshness.None => string.IsNullOrWhiteSpace(Reading.Source)
            ? "首次读取可能需要几秒钟。" : "暂未取得有效电量；详细原因见诊断信息。",
        _ when Reading.Key == "xbox" => "蓝牙上报电量；充电状态暂不可用。",
        _ => ""
    };
    public Brush Accent => new SolidColorBrush((Color)ColorConverter.ConvertFromString(
        Reading.Freshness != ReadingFreshness.Fresh ? "#9AA4B2" : Reading.Charging == true ? "#16846B" : Reading.Percent <= _criticalPercent ? "#D34949" : Reading.Percent <= _warningPercent ? "#BD7B13" : "#4063DA"));
    public double ReadingOpacity => Reading.Freshness == ReadingFreshness.LastKnown ? 0.55 : 1;
    public string Diagnostic => string.IsNullOrWhiteSpace(Reading.Detail) ? "尚未读取。" : Reading.Detail;
    public void Update(BatteryReading reading) { Reading = reading; Notify(); }
    public void SetThresholds(int warningPercent, int criticalPercent)
    { _warningPercent = warningPercent; _criticalPercent = criticalPercent; OnPropertyChanged(nameof(Accent)); }
    public void RefreshAge() { OnPropertyChanged(nameof(Updated)); }
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
