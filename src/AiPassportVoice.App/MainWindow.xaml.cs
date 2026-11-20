using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiPassportVoice.App.Services;
using AiPassportVoice.App.ViewModels;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace AiPassportVoice.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _ready;
    private bool _logExpanded;

    public bool ReallyClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnHotkeyKeyDown;
        PreviewKeyUp += OnHotkeyKeyUp;
        Deactivated += (_, _) => CancelHotkeyRecording();
    }

    public void AttachViewModel(MainViewModel vm)
    {
        _vm = vm;
        vm.LogAppended += () => Dispatcher.BeginInvoke(() => LogScroll.ScrollToEnd());
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HotkeyText) && !_recordingHotkey)
                KeycapText.Text = vm.HotkeyText;
            if (e.PropertyName == nameof(MainViewModel.CableAlertVisible))
                Dispatcher.BeginInvoke(FitToContent);
        };
    }

    private MainViewModel Vm => _vm ??= (MainViewModel)DataContext;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        var app = (App)Application.Current;
        PortBox.Text = app.Config.UdpPort.ToString();
        IpBox.Text = app.Config.DeviceIp;
        MinimizeSwitch.IsChecked = app.Config.StartMinimized;
        LogFileSwitch.IsChecked = app.Config.LogToFile;
        ThemeCombo.SelectedIndex = app.Config.Theme switch
        {
            "light" => 2,
            "dark" => 1,
            _ => 0,
        };
        AutoStartSwitch.IsChecked = AutoStart.IsEnabled();
        KeycapText.Text = Vm.HotkeyText;
        _ready = true;
        Dispatcher.BeginInvoke(FitToContent);
    }

    /// <summary>按当前页面实际内容高度调整窗口，避免底部留白或内容被裁。</summary>
    private void FitToContent()
    {
        if (!_ready) return;
        var panel = Vm.IsSettings ? (FrameworkElement)SettingsPanel : MainPanel;
        panel.UpdateLayout();
        const double chrome = 124; // 标题栏 44 + 底部 60 + 面板上下边距 20
        Height = Math.Min(chrome + panel.ActualHeight, MaxHeight);
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnMinClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>直接收进托盘：语音转发继续进行，双击托盘图标可唤回。</summary>
    private void OnHideToTray(object sender, RoutedEventArgs e)
    {
        if (((App)Application.Current).Tray is null)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmExitDialog { Owner = this };
        dialog.ShowDialog();

        switch (dialog.Result)
        {
            case ConfirmExitDialog.Choice.Exit:
                ((App)Application.Current).ExitApp();
                break;

            case ConfirmExitDialog.Choice.Minimize:
                Hide();
                break;

            // Cancel：什么都不做
        }
    }

    private async void OnToggle(object sender, RoutedEventArgs e) => await Vm.ToggleAsync();

    private void OnRecheck(object sender, RoutedEventArgs e) => Vm.RecheckCable();

    private void OnDownloadCable(object sender, RoutedEventArgs e) => Vm.DownloadCable();

    private void OnTestAudio(object sender, RoutedEventArgs e) => Vm.TestAudio();

    private void OnDiagnose(object sender, RoutedEventArgs e) => Vm.Diagnose();

    private void OnOpenGithub(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/CN-P5/ai-passport-voice-assistant")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Vm.AppendLog($"[ui] 打开浏览器失败: {ex.Message}");
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Vm.SwitchPage(true);
        MainPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(FitToContent);
    }

    private void OnBackToMain(object sender, RoutedEventArgs e)
    {
        Vm.SwitchPage(false);
        SettingsPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(FitToContent);
    }

    private bool _recordingHotkey;
    private bool _hotkeyCommitted;
    private readonly List<string> _modKeys = new();

    private void OnEditHotkey(object sender, RoutedEventArgs e) => BeginHotkeyRecording();

    private void OnEditHotkey(object sender, MouseButtonEventArgs e) => BeginHotkeyRecording();

    private void BeginHotkeyRecording()
    {
        if (_recordingHotkey)
        {
            CancelHotkeyRecording();
            return;
        }

        _recordingHotkey = true;
        _hotkeyCommitted = false;
        _modKeys.Clear();
        KeycapText.Text = "请按下快捷键…（Esc 取消）";
        KeycapBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        Activate();
        Focus();
    }

    private void CancelHotkeyRecording()
    {
        if (!_recordingHotkey) return;
        _recordingHotkey = false;
        _modKeys.Clear();
        KeycapBox.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush2");
        KeycapText.Text = Vm.HotkeyText;
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelHotkeyRecording();
            return;
        }

        var name = ToKeyName(key);
        if (name is null) return;

        if (IsModifier(name))
        {
            if (!_modKeys.Contains(name)) _modKeys.Add(name);
            KeycapText.Text = string.Join(" + ", _modKeys.Select(Pretty)) + " + …";
            return;
        }

        var spec = _modKeys.Count > 0 ? string.Join("+", _modKeys.Append(name)) : name;
        CommitHotkey(spec);
    }

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey || _hotkeyCommitted) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = ToKeyName(key);
        if (name is null || !IsModifier(name)) return;

        // 只按了一个修饰键就松开 → 作为单键热键
        if (_modKeys.Count == 1 && _modKeys[0] == name)
            CommitHotkey(name);
    }

    private void CommitHotkey(string spec)
    {
        if (!Vm.ApplyHotkey(spec))
        {
            KeycapText.Text = "该组合无效，请重试";
            _modKeys.Clear();
            return;
        }

        _hotkeyCommitted = true;
        _recordingHotkey = false;
        _modKeys.Clear();
        KeycapBox.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush2");
        KeycapText.Text = Vm.HotkeyText;
        Vm.AppendLog($"[key] 热键已改为 {spec}");
    }

    private static bool IsModifier(string name) => name is
        "left-ctrl" or "right-ctrl" or "left-alt" or "right-alt" or
        "left-shift" or "right-shift" or "left-win" or "right-win";

    private static string? ToKeyName(Key key) => key switch
    {
        Key.LeftCtrl => "left-ctrl",
        Key.RightCtrl => "right-ctrl",
        Key.LeftShift => "left-shift",
        Key.RightShift => "right-shift",
        Key.LeftAlt => "left-alt",
        Key.RightAlt => "right-alt",
        Key.LWin => "left-win",
        Key.RWin => "right-win",
        Key.Space => "space",
        Key.Enter => "enter",
        Key.Tab => "tab",
        Key.Back => "backspace",
        Key.CapsLock => "caps",
        >= Key.F1 and <= Key.F12 => "f" + ((int)key - (int)Key.F1 + 1),
        >= Key.A and <= Key.Z => key.ToString().ToLowerInvariant(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((int)key - (int)Key.NumPad0).ToString(),
        _ => null,
    };

    private static string Pretty(string name) => name switch
    {
        "right-shift" => "右 Shift",
        "left-shift" => "左 Shift",
        "left-ctrl" => "左 Ctrl",
        "right-ctrl" => "右 Ctrl",
        "left-alt" => "左 Alt",
        "right-alt" => "右 Alt",
        "left-win" => "左 Win",
        "right-win" => "右 Win",
        "space" => "空格",
        "enter" => "回车",
        _ => name.Length == 1 ? name.ToUpperInvariant() : name,
    };

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var hold = ReferenceEquals(sender, HoldRadio);
        Vm.SetMode(hold ? "hold" : "tap");
    }

    private void OnToggleLog(object sender, RoutedEventArgs e)
    {
        _logExpanded = !_logExpanded;
        LogScroll.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        LogScroll.Height = _logExpanded ? 200 : 0;
        LogToggle.Content = _logExpanded ? "收起" : "展开";
        Dispatcher.BeginInvoke(() =>
        {
            FitToContent();
            if (_logExpanded) LogScroll.ScrollToEnd();
        });
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var app = (App)Application.Current;
        var theme = ThemeCombo.SelectedIndex switch
        {
            2 => "light",
            1 => "dark",
            _ => "system",
        };
        app.Config.Theme = theme;
        ApplyTheme(theme);
    }

    private static void ApplyTheme(string theme)
    {
        var useLight = theme == "light";
        var dict = new ResourceDictionary
        {
            Source = new Uri(useLight ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict;
        else merged.Add(dict);
    }

    private void OnLogFileClick(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var app = (App)Application.Current;
        app.Config.LogToFile = LogFileSwitch.IsChecked == true;
        FileLogger.Enabled = app.Config.LogToFile;
        try { app.Config.Save(app.ConfigPath); } catch { }
        Vm.AppendLog(FileLogger.Enabled
            ? $"[log] 已开启日志文件：{FileLogger.LogDirectory}"
            : "[log] 已关闭日志文件写入");
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var app = (App)Application.Current;
        app.Config.StartMinimized = MinimizeSwitch.IsChecked == true;
        try { app.Config.Save(app.ConfigPath); } catch { }
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try
        {
            AutoStart.Set(AutoStartSwitch.IsChecked == true);
        }
        catch (Exception ex)
        {
            Vm.AppendLog($"[cfg] 开机自启设置失败: {ex.Message}");
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ReallyClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
