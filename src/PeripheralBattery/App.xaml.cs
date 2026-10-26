using System.Windows;
using PeripheralBattery.Services;

namespace PeripheralBattery;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\PeripheralBattery.Desktop.v02", out bool first);
        if (!first)
        {
            MessageBox.Show("外设电量已在运行，请点击任务栏通知区域的电池图标。", "外设电量");
            Shutdown();
            return;
        }
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            AppSettings.Log(error.Exception);
            MessageBox.Show("程序遇到错误，详情已记录到本机日志。\n" + error.Exception.Message, "外设电量");
            error.Handled = true;
            Shutdown(1);
        };
        MainWindow = new MainWindow(e.Args.Contains("--startup"));
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
