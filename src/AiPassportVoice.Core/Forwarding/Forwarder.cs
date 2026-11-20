using System.Diagnostics;
using System.Text.Json;
using AiPassportVoice.Core.Audio;
using AiPassportVoice.Core.Input;
using AiPassportVoice.Core.Protocol;

namespace AiPassportVoice.Core.Forwarding;

/// <summary>回报给设备的 PC 就绪状态。设备依据 audioReady 决定是否允许按住说话。</summary>
public sealed record PcStatus(bool AudioReady, bool HotkeyReady);

/// <summary>
/// 热键 + 麦克风转发核心：设备按键事件 → 系统热键，设备音频 → 虚拟声卡。
/// 语义与 windows/hotkey_forwarder.py 的 Forwarder 逐条对齐。
/// 线程安全：UDP 收包线程、UI 线程、健康检查定时器都可能调用，状态字段全部加锁。
/// </summary>
public sealed class Forwarder
{
    /// <summary>回车去抖窗口：设备可能连发多次下键。</summary>
    private const double EnterDebounceSeconds = 0.35;

    /// <summary>音频静默超时。voice.end 丢包时靠它释放热键，与链路心跳无关。</summary>
    private const double AudioSilenceTimeoutSeconds = 2.5;

    /// <summary>tap 模式由 voice.end 控制结束，静音超时只作兜底，放宽到 30s 避免说话中途被切断。</summary>
    private const double TapSilenceTimeoutSeconds = 30;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan StaleReleaseDelay = TimeSpan.FromMilliseconds(50);

    private readonly IKeyInjector _injector;
    private readonly IKeyInjector _enterInjector;
    private readonly IAudioSink? _sink;
    private readonly Action<string> _log;
    private readonly bool _tapMode;

    /// <summary>保护全部可变状态字段（读取也要走它）。</summary>
    private readonly object _stateLock = new();

    /// <summary>串行化 voice.start/voice.end/key.action/finish 这类状态迁移，避免并发重入。</summary>
    private readonly object _gate = new();

    private bool _active;
    private int _audioBlocks;
    private int? _lastSeq;
    private double _lastAudioAt;
    private double _lastEnterAt = double.NegativeInfinity;

    public Forwarder(IKeyInjector injector, IKeyInjector enterInjector, IAudioSink? sink,
        Action<string> log, bool tapMode = false)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _enterInjector = enterInjector ?? throw new ArgumentNullException(nameof(enterInjector));
        _sink = sink;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tapMode = tapMode;
    }

    /// <summary>是否处于「设备正在说话」状态（热键按住 / 点击已开启）。</summary>
    public bool Active
    {
        get { lock (_stateLock) return _active; }
    }

    /// <summary>本次会话已写入声卡的音频块数（诊断用）。</summary>
    public int AudioBlocks
    {
        get { lock (_stateLock) return _audioBlocks; }
    }

    /// <summary>
    /// 回报给设备的就绪状态。hotkeyReady 恒为 true——注入器在构造时已解析好键位，
    /// 对应 Python 的 bool(self.injector._keys)。
    /// </summary>
    public PcStatus Status => new(AudioReady: _sink is not null && _sink.Enabled, HotkeyReady: true);

    /// <summary>事件通道载荷（JSON）。异常、null、非对象一律忽略。</summary>
    public void OnEvent(ReadOnlySpan<byte> jsonPayload)
    {
        string? name;
        string? action = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonPayload.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("event", out var eventEl) || eventEl.ValueKind != JsonValueKind.String)
                return;
            name = eventEl.GetString();
            if (root.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.String)
                action = actionEl.GetString();
        }
        catch (JsonException)
        {
            return;
        }
        catch (ArgumentException)
        {
            return;
        }

        switch (name)
        {
            case "voice.start":
            {
                lock (_gate)
                {
                    lock (_stateLock)
                    {
                        if (_active)
                            return;
                    }

                    // 音频通道未就绪时不开启：否则按住说话没有任何声音进输入法。
                    if (_sink is not null && !_sink.Enabled)
                        return;

                    _log("[evt] 设备开始说话(voice.start)");

                    lock (_stateLock)
                    {
                        _audioBlocks = 0;
                        _lastSeq = null;
                        _lastAudioAt = Monotonic();
                    }

                    _sink?.Clear();
                    _injector.Start();

                    // 热键按下之后再开闸，避免热键还没生效就写音频。
                    lock (_stateLock)
                        _active = true;
                }

                break;
            }

            case "voice.end":
            {
                lock (_gate)
                {
                    int blocks;
                    lock (_stateLock) blocks = _audioBlocks;
                    _log($"[evt] 设备结束说话(voice.end),收到音频块={blocks}");
                    FinishCore(drain: true);
                }

                break;
            }

            case "device.hello":
                _log("[evt] 设备上线(device.hello)");
                break;

            case "key.action" when action == "enter":
            {
                lock (_gate)
                {
                    var now = Monotonic();
                    lock (_stateLock)
                    {
                        if (now - _lastEnterAt < EnterDebounceSeconds)
                        {
                            _log("[evt] 忽略重复的下键回车");
                            return;
                        }

                        _lastEnterAt = now;
                    }

                    // 下键既是「发送」也是状态对账点：voice.end 丢包时固件已是 READY，
                    // 而 PC 还以为在录音——先抬键，否则会变成 Shift+Enter 或让输入法一直听。
                    bool stale;
                    lock (_stateLock)
                        stale = _active || _injector.IsActive;

                    if (stale)
                    {
                        _log("[evt] 下键对账: 先结束残留语音状态");
                        FinishCore(drain: false);
                        Thread.Sleep(StaleReleaseDelay);
                    }

                    _enterInjector.Click();
                }

                break;
            }

            // status / agent.* 等其余事件与本程序无关，忽略。
        }
    }

    /// <summary>音频通道载荷：[seq][0x80] + 804B ADPCM block。</summary>
    public void OnAudio(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != VoiceProtocol.AudioFrameLen || payload[1] != 0x80)
            return;

        bool firstBlock = false;
        lock (_stateLock)
        {
            if (!_active)
                return;

            var seq = payload[0];
            if (_lastSeq is int last)
            {
                // 只在序号「向前但未跳太远」时接受：丢弃重复包与陈旧乱序包（8 位回绕）。
                var delta = (seq - last) & 0xFF;
                if (delta == 0 || delta >= 128)
                    return;
            }

            _lastSeq = seq;
            _lastAudioAt = Monotonic();

            _sink?.WriteBlock(payload[2..]);
            _audioBlocks++;
            firstBlock = _audioBlocks == 1;
        }

        if (firstBlock)
            _log($"[audio] 已收到设备首个音频块并写入 {_sink?.DeviceName ?? "虚拟声卡"}");
    }

    /// <summary>链路断开：热键必须抬起来，否则会卡住一个按下的修饰键。</summary>
    public void OnDisconnect()
    {
        _log("[link] 设备断开,热键复位;等待 beacon 自动重连...");
        Finish(drain: false);
    }

    /// <summary>心跳里调用：设备已在说但音频静默/声卡掉线 → 停音并释放热键。</summary>
    public void CheckHealth()
    {
        var timeout = _tapMode ? TapSilenceTimeoutSeconds : AudioSilenceTimeoutSeconds;

        bool interrupted;
        lock (_stateLock)
        {
            interrupted = _active &&
                          (Monotonic() - _lastAudioAt > timeout ||
                           (_sink is not null && !_sink.Enabled));
        }

        if (!interrupted)
            return;

        _log("[link] 语音通道中断，停止传音并释放快捷键");
        Finish(drain: false);
    }

    /// <summary>结束本次说话：先排空/清空音频，再抬热键（保证最后一块声音先出去）。</summary>
    public void Finish(bool drain)
    {
        lock (_gate)
            FinishCore(drain);
    }

    /// <summary>调用方必须持有 _gate。</summary>
    private void FinishCore(bool drain)
    {
        lock (_stateLock)
            _active = false;

        try
        {
            if (_sink is not null)
            {
                if (drain)
                    _sink.Drain(DrainTimeout);
                else
                    _sink.Clear();
            }
        }
        finally
        {
            // 无论声卡抛什么异常，热键都必须抬起来。
            _injector.Stop();
        }
    }

    /// <summary>单调时钟秒数，等价于 Python 的 time.monotonic()。</summary>
    private static double Monotonic() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
}
