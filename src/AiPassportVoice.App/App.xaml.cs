using System.Diagnostics;
using System.IO;
using System.Threading;
using AiPassportVoice.App.Services;
using AiPassportVoice.App.Tray;
using AiPassportVoice.App.ViewModels;
using AiPassportVoice.Core.Config;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AiPassportVoice.App;

public partial class App : Application
{
    private const string MutexName = "AiPassportVoice.SingleInstance";
    private const string ShowEventName = "AiPassportVoice.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private volatile bool _exiting;

    public MainViewModel? ViewModel { get; private set; }
    public MainWindow? Shell { get; private set; }
    public TrayIcon? Tray { get; private set; }
    public AppConfig Config { get; private set; } = new();
    public string ConfigPath { get; private set; } = string.Empty;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已有实例：通知它显示窗口，然后安静退出
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show("AI Passport 语音已在运行（请查看系统托盘，必要时点开折叠区）。",
                    "AI Passport 语音", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ConfigPath = Path.Combine(AppContext.BaseDirectory, "voice-config.json");
        var firstRun = !File.Exists(ConfigPath);
        Config = AppConfig.Load(ConfigPath);

        ViewModel = new MainViewModel(Config, ConfigPath, Dispatcher);
        ViewModel.StartService();   // 先启动转发，窗口一出现就是就绪状态

        Shell = new MainWindow { DataContext = ViewModel };
        Shell.AttachViewModel(ViewModel);
        Shell.Show();

        Tray = new TrayIcon(
            show: () => Dispatcher.Invoke(ShowMainWindow),
            exit: () => Dispatcher.Invoke(ExitApp),
            isRunning: () => ViewModel?.IsRunning ?? false,
            toggleForwarding: () => Dispatcher.BeginInvoke(new Action(ToggleForwardingFromTray)),
            isAutoStart: AutoStart.IsEnabled,
            setAutoStart: enabled => Dispatcher.Invoke(() => AutoStart.Set(enabled)));

        // 监听「第二次启动」请求，唤出已有窗口
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (!_exiting)
            {
                try
                {
                    if (!_showEvent.WaitOne(500)) continue;
                    Dispatcher.BeginInvoke(ShowMainWindow);
                }
                catch
                {
                    break;
                }
            }
        })
        { IsBackground = true, Name = "show-listener" };
        listener.Start();

        // 首次运行一定显示窗口，避免用户找不到托盘；之后才按配置最小化
        if (!firstRun && Config.StartMinimized)
        {
            Shell.Hide();
            Tray.ShowBalloon("AI Passport 语音", "已最小化到托盘，双击托盘图标可重新打开。");
        }

        Config.Save(ConfigPath);   // 首次运行落盘，生成可编辑的配置文件
    }

    /// <summary>
    /// 若当前进程是管理员权限，用 explorer 以普通权限重启自己并返回 true。
    /// 普通权限下托盘图标才能正常显示，打开浏览器等联动也不会被拒。
    /// </summary>
    private async void ToggleForwardingFromTray()
    {
        if (ViewModel is not null)
            await ViewModel.ToggleAsync();
    }

    public void ShowMainWindow()
    {
        if (Shell is null) return;
        Shell.Show();
        if (Shell.WindowState == System.Windows.WindowState.Minimized)
            Shell.WindowState = System.Windows.WindowState.Normal;
        Shell.Activate();
        Shell.Topmost = true;
        Shell.Topmost = false;
    }

    public void ExitApp()
    {
        _exiting = true;
        Tray?.Dispose();
        Tray = null;
        ViewModel?.Dispose();
        ViewModel = null;
        if (Shell is not null)
        {
            Shell.ReallyClose = true;
            Shell.Close();
        }
        Shutdown();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _exiting = true;
        Tray?.Dispose();
        ViewModel?.Dispose();
        _showEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
