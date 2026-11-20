using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using AiPassportVoice.App.Services;
using AiPassportVoice.Core.Config;
using AiPassportVoice.Core.Input;
using AiPassportVoice.Interop.Audio;

namespace AiPassportVoice.App.ViewModels;

public sealed record LogItem(string Time, string Text);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppConfig _config;
    private readonly string _configPath;
    private readonly Dispatcher _dispatcher;
    private readonly VoiceService _service;
    private readonly DispatcherTimer _meterTimer;
    private readonly Random _rng = new();

    private bool _isOnline;
    private bool _isRunning;
    private bool _isSettings;
    private bool _isHoldMode;
    private string _statusText = "等待设备";
    private string _statusSub = "正在启动…";
    private string _hotkeyText = "右 Shift";
    private string _hotkeyInput = "right-shift";
    private bool _cableAlertVisible;
    private string _cableAlertText = "";
    private string _cableStateText = "检测中";
    private string? _selectedDevice;
    private bool _suppressDeviceSave;

    public MainViewModel(AppConfig config, string configPath, Dispatcher dispatcher)
    {
        _config = config;
        _configPath = configPath;
        _dispatcher = dispatcher;
        FileLogger.Enabled = config.LogToFile;
        _service = new VoiceService(config, AppendLog);
        _service.Tick += () => _dispatcher.BeginInvoke(RefreshState);

        HotkeyInput = config.HotkeyKey;
        _isHoldMode = config.HotkeyMode != "tap";
        for (var i = 0; i < 12; i++) MeterLevels.Add(4);
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _meterTimer.Tick += (_, _) => UpdateMeter();
        _meterTimer.Start();
        RefreshDevices();
        RefreshState();
    }

    public ObservableCollection<string> AudioDevices { get; } = new();
    public ObservableCollection<LogItem> Logs { get; } = new();
    public ObservableCollection<double> MeterLevels { get; } = new();

    public bool IsOnline { get => _isOnline; private set => Set(ref _isOnline, value); }
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool IsSettings { get => _isSettings; private set => Set(ref _isSettings, value); }
    public bool IsHoldMode => _isHoldMode;
    public bool IsTapMode => !_isHoldMode;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusSub { get => _statusSub; private set => Set(ref _statusSub, value); }
    public string HotkeyText { get => _hotkeyText; private set => Set(ref _hotkeyText, value); }
    public string CableStateText { get => _cableStateText; private set => Set(ref _cableStateText, value); }
    public bool CableAlertVisible { get => _cableAlertVisible; private set => Set(ref _cableAlertVisible, value); }
    public string CableAlertText { get => _cableAlertText; private set => Set(ref _cableAlertText, value); }
    public string ToggleText => IsRunning ? "停止转发" : "开始转发";
    public string DisconnectText => IsRunning ? "断开" : "重连";
    public bool CableReady => !CableAlertVisible;
    public bool CanToggle => IsRunning || CableReady;
    public string Version => "1.0.0 · net10-wpf";

    public string HotkeyInput
    {
        get => _hotkeyInput;
        set => Set(ref _hotkeyInput, value);
    }

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            if (_suppressDeviceSave) return;
            if (!string.IsNullOrWhiteSpace(value))
                _config.OutputSubstr = value!;
        }
    }

    public void StartService()
    {
        try
        {
            _service.Start();
        }
        catch (Exception ex)
        {
            AppendLog($"[link] 启动失败: {ex.Message}");
        }
        RefreshState();
    }

    public async Task ToggleAsync()
    {
        var wasRunning = _service.Running;
        try
        {
            await Task.Run(() =>
            {
                if (wasRunning) _service.Stop();
                else _service.Start();
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[link] 切换失败: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"无法{(wasRunning ? "停止" : "启动")}转发：{ex.Message}\n\n请检查 UDP {_config.UdpPort} 是否被其他程序占用。",
                "AI Passport 语音", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        RefreshState();
    }

    public void SetMode(string mode)
    {
        if (mode is not ("hold" or "tap")) return;
        if ((mode == "hold") == _isHoldMode) return;
        _isHoldMode = mode == "hold";
        OnPropertyChanged(nameof(IsHoldMode));
        OnPropertyChanged(nameof(IsTapMode));
        _service.ApplyHotkey(_config.HotkeyKey, mode);
        Save();
        RefreshState();
    }

    public bool ApplyHotkey(string spec)
    {
        if (!_service.ApplyHotkey(spec, _config.HotkeyMode))
            return false;
        _config.HotkeyKey = spec;
        Save();
        RefreshState();
        return true;
    }

    public void RecheckCable()
    {
        AppendLog("[audio] 正在重新检测声卡…");
        _service.RecheckCable();
        RefreshDevices();
        RefreshState();
    }

    public void DownloadCable()
    {
        const string url = "https://vb-audio.com/Cable/";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{url}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                AppendLog($"[audio] 打开浏览器失败: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"无法自动打开浏览器，请手动访问：\n{url}",
                    "AI Passport 语音", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
        }
        AppendLog("[audio] 已用默认浏览器打开 VB-CABLE 下载页，安装后点「重新检测」");
    }

    public void TestAudio()
    {
        AppendLog(CableReady
            ? "[audio] 测试：请对设备按住 UP 说话，观察输入法是否收到语音"
            : "[audio] 声卡未就绪，无法测试；请先安装 VB-CABLE");
    }

    public void Diagnose()
    {
        AppendLog("── 诊断 ──");
        AppendLog($"[diag] 运行状态: {(_service.Running ? "运行中" : "已停止")}");
        AppendLog($"[diag] 声卡: {_service.Cable.Detail}");
        var outs = AudioDeviceProbe.EnumerateOutputs();
        AppendLog($"[diag] 输出设备 {outs.Count} 个");
        foreach (var d in outs) AppendLog($"  · {d}");
        var ins = AudioDeviceProbe.EnumerateInputs();
        AppendLog($"[diag] 输入设备 {ins.Count} 个");
        foreach (var d in ins) AppendLog($"  · {d}");
    }

    public void SwitchPage(bool settings)
    {
        IsSettings = settings;
    }

    public void AppendLog(string line)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }
        FileLogger.Write(line);
        Logs.Add(new LogItem(DateTime.Now.ToString("HH:mm:ss"), line));
        while (Logs.Count > 300) Logs.RemoveAt(0);
        LogAppended?.Invoke();
    }

    private void RefreshState()
    {
        IsOnline = _service.DeviceOnline;
        IsRunning = _service.Running;
        StatusText = IsOnline ? "已连接" : (IsRunning ? "等待设备" : "已停止");
        StatusSub = IsOnline
            ? $"AI Passport · {_service.DeviceAddress}"
            : (IsRunning ? "转发已启动 · 等待设备连接" : "转发已暂停");

        HotkeyText = DescribeHotkey(_config.HotkeyKey);

        var cable = _service.Cable;
        CableAlertVisible = !cable.IsReady;
        CableStateText = cable switch
        {
            { State: CableState.Ready } => "已就绪",
            { State: CableState.Unusable } => "不可用",
            _ => "未安装",
        };
        CableAlertText = cable.State switch
        {
            CableState.NotInstalled => "未检测到虚拟声卡 VB-CABLE。设备音频需经它进入输入法，安装后请重启本程序。",
            CableState.Unusable => $"虚拟声卡不可用：{cable.Detail}",
            _ => "",
        };

        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(DisconnectText));
        OnPropertyChanged(nameof(CableReady));
        OnPropertyChanged(nameof(CanToggle));
    }

    private void RefreshDevices()
    {
        var current = _config.OutputSubstr;
        _suppressDeviceSave = true;
        try
        {
            AudioDevices.Clear();
            foreach (var d in AudioDeviceProbe.EnumerateOutputs())
                AudioDevices.Add(d);
            SelectedDevice = AudioDevices.FirstOrDefault(d => d.Contains(current, StringComparison.OrdinalIgnoreCase))
                             ?? AudioDevices.FirstOrDefault();
        }
        finally
        {
            _suppressDeviceSave = false;
        }
    }

    private static string DescribeHotkey(string spec)
    {
        try
        {
            var keys = KeySpec.Resolve(spec);
            return string.Join(" + ", keys.Select(k => Pretty(k.Name)));
        }
        catch
        {
            return spec;
        }
    }

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
        "escape" => "Esc",
        _ => name.Length == 1 ? name.ToUpperInvariant() : name,
    };

    private static readonly double[] MeterShape =
    {
        0.50, 0.72, 0.92, 1.00, 0.85, 0.78, 0.82, 0.95, 1.00, 0.88, 0.66, 0.48,
    };

    private double _displayLevel;

    private void UpdateMeter()
    {
        var target = IsRunning && IsOnline && CableReady ? _service.AudioLevel : 0;

        // 起音快、回落慢，避免竖条抖动
        _displayLevel += (target - _displayLevel) * (target > _displayLevel ? 0.70 : 0.28);
        if (_displayLevel < 0.01) _displayLevel = 0;

        for (var i = 0; i < MeterLevels.Count; i++)
        {
            var shape = i < MeterShape.Length ? MeterShape[i] : 0.6;
            MeterLevels[i] = 4 + _displayLevel * shape * 26;
        }
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _service.Dispose();
    }

    private void Save()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex) { AppendLog($"[cfg] 保存配置失败: {ex.Message}"); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LogAppended;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
