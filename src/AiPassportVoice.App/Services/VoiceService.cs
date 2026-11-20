using System.Text;
using System.Text.Json;
using AiPassportVoice.Core.Config;
using AiPassportVoice.Core.Forwarding;
using AiPassportVoice.Core.Input;
using AiPassportVoice.Core.Transport;
using AiPassportVoice.Interop.Audio;
using AiPassportVoice.Interop.Input;

namespace AiPassportVoice.App.Services;

/// <summary>
/// 组装传输/状态机/声卡/键盘注入，并负责 pc.status 上报与健康检查。
/// </summary>
public sealed class VoiceService : IDisposable
{
    private readonly AppConfig _config;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private UdpTransport? _transport;
    private Forwarder? _forwarder;
    private WaveOutAudioSink? _sink;
    private NativeKeyInjector? _injector;
    private NativeKeyInjector? _enterInjector;
    private System.Threading.Timer? _timer;
    private int _tick;

    public VoiceService(AppConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
    }

    public bool Running { get; private set; }
    public bool DeviceOnline => _transport?.DeviceKnown ?? false;
    public string DeviceAddress => _transport?.DeviceAddress ?? "";
    public CableStatus Cable { get; private set; } = new(CableState.NotInstalled, null, -1, "尚未检测");
    public string HotkeyDisplay { get; private set; } = "";
    public bool AudioReady => _sink?.Enabled ?? false;
    public string? AudioDeviceName => _sink?.DeviceName;

    /// <summary>最近音频块的实时电平（0..1），静音时为 0。</summary>
    public double AudioLevel => _sink is WaveOutAudioSink w ? w.Level : 0;

    /// <summary>每秒通知 UI 刷新状态。</summary>
    public event Action? Tick;

    public void Start()
    {
        lock (_gate)
        {
            if (Running) return;

            Cable = AudioDeviceProbe.ProbeCable(_config.OutputSubstr);

            NativeKeyInjector.PreferKeybdEvent = _config.InjectApi == "keybd_event";

            IReadOnlyList<KeyDef> keys;
            try
            {
                keys = KeySpec.Resolve(_config.HotkeyKey);
            }
            catch (Exception ex)
            {
                _log($"[key] 热键配置无效: {ex.Message}，回退 right-ctrl");
                keys = KeySpec.Resolve("right-ctrl");
            }

            HotkeyDisplay = string.Join("+", keys.Select(k => k.Name));

            _sink = Cable.IsReady && Cable.OutputIndex >= 0
                ? new WaveOutAudioSink(Cable.OutputIndex, Cable.OutputName ?? "CABLE Input", _log)
                : null;
            if (_sink is { Enabled: false })
            {
                _sink.Dispose();
                _sink = null;
            }

            _injector = new NativeKeyInjector(keys, _config.HotkeyMode, _log);
            _enterInjector = new NativeKeyInjector(KeySpec.Resolve("enter"), "tap", _log);
            _forwarder = new Forwarder(_injector, _enterInjector, _sink, _log, _config.HotkeyMode != "hold");

            _transport = new UdpTransport(
                _config.UdpPort,
                string.IsNullOrWhiteSpace(_config.DeviceIp) ? null : _config.DeviceIp.Trim(),
                _log);
            _transport.EventPayload += p => _forwarder?.OnEvent(p);
            _transport.AudioPayload += p => _forwarder?.OnAudio(p);
            _transport.DeviceLost += () => _forwarder?.OnDisconnect();
            _transport.Start();

            _timer = new System.Threading.Timer(OnTimer, null, 200, 200);
            Running = true;
            _log("[link] 转发服务已启动，等待设备按键/音频");
        }
    }

    private void OnTimer(object? _)
    {
        try
        {
            _forwarder?.CheckHealth();
            _tick++;
            if (_tick % 5 == 0)
            {
                ReportStatus();
                Tick?.Invoke();
            }
        }
        catch { }
    }

    private void ReportStatus()
    {
        if (_transport is not { DeviceKnown: true } || _forwarder is null) return;
        var st = _forwarder.Status;
        var json = JsonSerializer.Serialize(new
        {
            type = "pc.status",
            audioReady = st.AudioReady,
            hotkeyReady = st.HotkeyReady,
        });
        _transport.SendEvent(Encoding.UTF8.GetBytes(json + "\n"));
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!Running) return;
            Running = false;

            _timer?.Dispose();
            _timer = null;

            try { _transport?.Stop(); } catch { }
            _transport?.Dispose();
            _transport = null;

            try { _forwarder?.Finish(false); } catch { }
            _forwarder = null;

            _injector?.Dispose();
            _injector = null;
            _enterInjector?.Dispose();
            _enterInjector = null;

            _sink?.Dispose();
            _sink = null;

            _log("[link] 转发服务已停止");
        }
    }

    /// <summary>重新检测声卡；运行中则整体重启以重建音频出口。</summary>
    public CableStatus RecheckCable()
    {
        var wasRunning = Running;
        if (wasRunning) Stop();
        Cable = AudioDeviceProbe.ProbeCable(_config.OutputSubstr);
        _log(Cable.IsReady
            ? $"[audio] 已检测到 {Cable.OutputName}，路由就绪"
            : $"[audio] 声卡不可用: {Cable.Detail}");
        if (wasRunning) Start();
        return Cable;
    }

    /// <summary>热键或模式变更：保存并重启服务。</summary>
    public bool ApplyHotkey(string spec, string mode)
    {
        try
        {
            _ = KeySpec.Resolve(spec);
        }
        catch (Exception ex)
        {
            _log($"[key] 热键无效: {ex.Message}");
            return false;
        }
        if (mode is not ("hold" or "tap")) return false;

        var wasRunning = Running;
        if (wasRunning) Stop();
        _config.HotkeyKey = spec;
        _config.HotkeyMode = mode;
        if (wasRunning) Start();
        return true;
    }

    public void Dispose() => Stop();
}
