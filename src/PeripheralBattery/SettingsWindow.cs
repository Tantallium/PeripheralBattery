using System.Windows;
using System.Windows.Controls;
using PeripheralBattery.Services;

namespace PeripheralBattery;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        Title = "提醒与启动"; Width = 360; Height = 320; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        var panel = new StackPanel { Margin = new Thickness(22) };
        var alerts = new CheckBox { Content = "低电量时提醒", IsChecked = settings.AlertsEnabled, Margin = new Thickness(0, 0, 0, 15) };
        var startup = new CheckBox { Content = "登录 Windows 后在托盘启动", IsChecked = settings.StartWithWindows, Margin = new Thickness(0, 0, 0, 18) };
        var warning = new TextBox { Text = settings.WarningPercent.ToString(), Width = 50, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 10) };
        var critical = new TextBox { Text = settings.CriticalPercent.ToString(), Width = 50, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 16) };
        panel.Children.Add(alerts); panel.Children.Add(startup);
        panel.Children.Add(new TextBlock { Text = "低电量阈值（5–50%）" }); panel.Children.Add(warning);
        panel.Children.Add(new TextBlock { Text = "再次提醒阈值（低于上项）" }); panel.Children.Add(critical);
        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right, Width = 80 };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(warning.Text, out int w) || !int.TryParse(critical.Text, out int c) || w < 5 || w > 50 || c < 1 || c >= w)
            { MessageBox.Show(this, "请输入有效阈值，第二项应低于第一项。", "设置"); return; }
            var candidate = new AppSettings
            {
                StartWithWindows = startup.IsChecked == true,
                AlertsEnabled = alerts.IsChecked == true,
                AutoRefreshEnabled = settings.AutoRefreshEnabled,
                WarningPercent = w,
                CriticalPercent = c
            };
            bool startupApplied = false;
            try
            {
                candidate.ApplyStartup();
                startupApplied = true;
                candidate.Save();
                settings.StartWithWindows = candidate.StartWithWindows;
                settings.AlertsEnabled = candidate.AlertsEnabled;
                settings.WarningPercent = candidate.WarningPercent;
                settings.CriticalPercent = candidate.CriticalPercent;
                DialogResult = true;
            }
            catch (Exception error)
            {
                if (startupApplied)
                {
                    try { settings.ApplyStartup(); }
                    catch (Exception rollbackError) { AppSettings.Log(rollbackError); }
                }
                AppSettings.Log(error);
                MessageBox.Show(this, "保存失败：" + error.Message, "设置");
            }
        };
        panel.Children.Add(save); Content = panel;
    }
}
