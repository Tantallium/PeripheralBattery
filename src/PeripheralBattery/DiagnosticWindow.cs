using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PeripheralBattery.Models;

namespace PeripheralBattery;

public sealed class DiagnosticWindow : Window
{
    public DiagnosticWindow(IReadOnlyList<BatteryReading> readings)
    {
        Title = "诊断信息"; Width = 620; Height = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var save = new Button { Content = "导出诊断" };
        var close = new Button { Content = "关闭", Margin = new Thickness(0) };
        close.Click += (_, _) => Close();
        footer.Children.Add(save); footer.Children.Add(close); panel.Children.Add(footer);
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI") };
        text.Text = string.Join("\n\n", readings.Select(r => $"{r.Name}\n连接：{r.Connection} · 读数：{r.Freshness}\n{r.Detail}\n有效电量时间：{r.SampledAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无"}"));
        if (readings.Count == 0) text.Text = "首次读取尚未完成，请稍候再查看。";
        save.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { FileName = "battery-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", Filter = "JSON 诊断报告|*.json" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());
                var payload = new { appVersion = "0.2.3", exportedAt = DateTimeOffset.Now, osVersion = Environment.OSVersion.VersionString, readings };
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(payload, options));
                MessageBox.Show(this, "诊断文件已保存。", "诊断信息");
            }
            catch (Exception error) { MessageBox.Show(this, "保存失败：" + error.Message, "诊断信息"); }
        };
        panel.Children.Add(text); Content = panel;
    }
}
