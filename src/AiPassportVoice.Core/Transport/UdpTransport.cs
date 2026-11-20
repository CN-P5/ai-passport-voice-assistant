using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AiPassportVoice.Core.Protocol;

namespace AiPassportVoice.Core.Transport;

/// <summary>
/// Wi-Fi UDP 通道传输层（单设备，直接对应设备固件 udp_audio.c 的对端角色）。
///
/// 协议契约（与 <see cref="VoiceProtocol"/> 一致，改一侧必须同步另一侧）：
///   端口 UDP 33333（双向同端口）
///   报文 = [1B type][payload]
///     0x01 AUDIO  设备→电脑 [seq][0x80] + 804B IMA ADPCM block
///     0x02 EVENT  双向      UTF-8 JSON 行
///     0x03 BEACON 电脑→设备 {"v":1,"role":"pc","port":33333}
///     0x04 PING   设备→电脑 空载荷，每 2s 一发，只用于判活
///
/// 角色：本端是服务端（bind 0.0.0.0:33333，每秒发 beacon），设备听到 beacon 后
/// 主动上行；本端由 ReceiveFrom 的来源地址学出设备地址，无需设备宣告。
/// </summary>
public sealed class UdpTransport : IDisposable
{
    private const int RecvTimeoutMs = 500;
    private const int StopJoinBudgetMs = 2000;
    private const string AnyAddressText = "0.0.0.0";

    private readonly int _port;
    private readonly string? _presetDeviceIp;
    private readonly Action<string> _log;

    private readonly byte[] _recvBuf = new byte[VoiceProtocol.RecvMax];
    private readonly object _gate = new();

    private Socket? _socket;
    private Thread? _reader;
    private Thread? _beacon;
    private volatile bool _stop = true;
    private bool _disposed;

    private IPEndPoint? _device;       // 由 ReceiveFrom 学出的设备地址
    private long _deviceLastTicks;     // 最近一次收到设备报文的 Stopwatch 时间戳
    private bool _lostLogged;          // 静默告警只打印一次
    private bool _sendDropLogged;      // 设备未知时下行丢弃只告警一次

    private string[] _beaconTargets = Array.Empty<string>();
    private bool _beaconSendErrLogged;

    /// <summary>
    /// port：UDP 端口（一般取 <see cref="VoiceProtocol.Port"/>）。
    /// deviceIp：可选的预置设备 IP（正常应留 null，由 recvfrom 学习）。
    /// log：日志回调，所有输出均带 "[udp]" 前缀。
    /// </summary>
    public UdpTransport(int port, string? deviceIp, Action<string> log)
    {
        _port = port;
        _presetDeviceIp = string.IsNullOrWhiteSpace(deviceIp) ? null : deviceIp.Trim();
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>是否已经学到设备地址（收到过任意一次设备报文）。</summary>
    public bool DeviceKnown
    {
        get
        {
            lock (_gate)
            {
                return _device is not null;
            }
        }
    }

    /// <summary>已学到的设备 "ip:port"；未学到时为空串。</summary>
    public string DeviceAddress
    {
        get
        {
            lock (_gate)
            {
                return _device?.ToString() ?? string.Empty;
            }
        }
    }

    /// <summary>EVENT 报文分发（payload 已剥掉首字节类型；在后台读线程触发，由调用方 marshal）。</summary>
    public event Action<byte[]>? EventPayload;

    /// <summary>AUDIO 报文分发（payload 已剥掉首字节类型；在后台读线程触发）。</summary>
    public event Action<byte[]>? AudioPayload;

    /// <summary>socket 读异常导致链路不可用（设备静默不会触发）。</summary>
    public event Action? DeviceLost;

    /// <summary>
    /// 打开 socket、起读线程与 beacon 线程。重复调用无副作用。
    /// 绑定失败会打印原因并抛出原始 SocketException（端口占用 / 防火墙）。
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_stop)
            return; // 已在运行

        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // 发送 beacon 需要广播能力。
            sock.EnableBroadcast = true;
            // 坑：不要设 SO_REUSEADDR —— Windows 上它语义等同 SO_REUSEPORT，
            // 会让第二个进程静默绑上同一端口并抢走设备报文，排查时极难定位。
            // UDP 没有 TIME_WAIT，正常重启不需要它。
            sock.Bind(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (SocketException ex)
        {
            sock.Dispose();
            Log($"绑定 UDP {AnyAddressText}:{_port} 失败: {ex.Message}" +
                "（确认端口未被占用、已在防火墙放行入站 UDP）");
            throw;
        }

        lock (_gate)
        {
            _device = null;
            _deviceLastTicks = 0;
            _lostLogged = false;
            _sendDropLogged = false;
        }

        _socket = sock;
        _stop = false;

        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "udp_reader",
        };
        _beacon = new Thread(BeaconLoop)
        {
            IsBackground = true,
            Name = "udp_beacon",
        };
        _reader.Start();
        _beacon.Start();

        Log($"已监听 {AnyAddressText}:{_port}，等待设备（请在设备上进入「AI语音」）");
    }

    /// <summary>下行 EVENT（[TEvent] + UTF-8 JSON）。未学到设备时丢弃并告警一次。</summary>
    public void SendEvent(ReadOnlySpan<byte> json)
    {
        var sock = _socket;
        IPEndPoint? device;
        lock (_gate)
        {
            device = _device;
        }

        if (sock is null || device is null || _stop)
        {
            if (!_sendDropLogged)
            {
                _sendDropLogged = true;
                Log("设备未知，下行 EVENT 丢弃（尚未学到设备地址）");
            }
            return;
        }

        var packet = new byte[1 + json.Length];
        packet[0] = VoiceProtocol.TEvent;
        json.CopyTo(packet.AsSpan(1));

        try
        {
            sock.SendTo(packet, device);
        }
        catch (SocketException ex)
        {
            Log($"发送失败({device}): {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Stop 与发送竞争，收束中直接忽略。
        }
    }

    /// <summary>停止 beacon、关 socket、收束线程（≤2s）。幂等，不触发 DeviceLost。</summary>
    public void Stop()
    {
        _stop = true;

        var sock = _socket;
        _socket = null;
        try
        {
            sock?.Dispose();
        }
        catch (Exception)
        {
            // 关闭中的异常无意义
        }

        var sw = Stopwatch.StartNew();
        JoinQuietly(_reader, sw);
        JoinQuietly(_beacon, sw);
        _reader = null;
        _beacon = null;

        lock (_gate)
        {
            _device = null;
            _deviceLastTicks = 0;
            _lostLogged = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    // -- 内部：线程收束 --

    private void JoinQuietly(Thread? thread, Stopwatch sw)
    {
        if (thread is null || !thread.IsAlive || thread == Thread.CurrentThread)
            return;
        var remaining = StopJoinBudgetMs - (int)sw.ElapsedMilliseconds;
        if (remaining > 0)
            thread.Join(remaining);
    }

    // -- 内部：读线程 --

    private void ReadLoop()
    {
        var sock = _socket;
        if (sock is null)
            return;

        try
        {
            sock.ReceiveTimeout = RecvTimeoutMs;
        }
        catch (Exception)
        {
            // socket 已被 Stop 关闭
            return;
        }

        var fatal = false;
        while (!_stop)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                n = sock.ReceiveFrom(_recvBuf, ref remote);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.TimedOut ||
                ex.SocketErrorCode == SocketError.WouldBlock ||
                ex.SocketErrorCode == SocketError.Interrupted)
            {
                // 超时即周期判活点（不拆链，只告警）
                CheckLink();
                continue;
            }
            catch (ObjectDisposedException)
            {
                // Stop 关闭 socket 的正常退出路径
                break;
            }
            catch (SocketException ex)
            {
                if (!_stop)
                {
                    fatal = true;
                    Log($"socket 读取异常: {ex.SocketErrorCode} {ex.Message}");
                }
                break;
            }

            if (_stop)
                break;
            if (n <= 0)
                continue;
            if (remote is not IPEndPoint from)
                continue;

            HandleDatagram(from, n);
        }

        if (!fatal)
            return;

        lock (_gate)
        {
            _device = null;
            _deviceLastTicks = 0;
        }
        try
        {
            DeviceLost?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"断连回调执行异常: {ex.Message}");
        }
    }

    private void HandleDatagram(IPEndPoint from, int length)
    {
        var type = _recvBuf[0];
        switch (type)
        {
            case VoiceProtocol.TBeacon:
                // 本机广播出去的回环，忽略。
                return;

            case VoiceProtocol.TPing:
                // 只用于判活，不派发。
                NoteDevice(from);
                return;

            case VoiceProtocol.TAudio:
            case VoiceProtocol.TEvent:
                NoteDevice(from);
                var payload = length > 1 ? _recvBuf.AsSpan(1, length - 1).ToArray() : Array.Empty<byte>();
                Dispatch(type, payload);
                return;

            default:
                Log($"未知报文类型 0x{type:x2} 来自 {from.Address}，忽略");
                return;
        }
    }

    private void Dispatch(byte type, byte[] payload)
    {
        try
        {
            if (type == VoiceProtocol.TAudio)
                AudioPayload?.Invoke(payload);
            else
                EventPayload?.Invoke(payload);
        }
        catch (Exception ex)
        {
            Log($"派发回调执行异常 (type=0x{type:x2}): {ex.Message}");
        }
    }

    /// <summary>记录/刷新设备地址（地址变化时打印，覆盖设备换 IP / 重连）。</summary>
    private void NoteDevice(IPEndPoint from)
    {
        Interlocked.Exchange(ref _deviceLastTicks, Stopwatch.GetTimestamp());
        lock (_gate)
        {
            if (_device is null)
            {
                _device = from;
                Log($"发现设备 {from}");
            }
            else if (!_device.Equals(from))
            {
                Log($"设备地址变化 {_device} → {from}");
                _device = from;
            }
        }
    }

    /// <summary>
    /// 判活：设备静默超过 link_timeout 只告警一次、绝不拆链（UDP 无连接，
    /// 设备侧会自动重连；拆 socket 会让电脑端必须手工重启）。
    /// 只有 socket 读异常才触发 DeviceLost。
    /// </summary>
    private void CheckLink()
    {
        bool known;
        long lastTicks;
        lock (_gate)
        {
            known = _device is not null;
            lastTicks = _deviceLastTicks;
        }

        if (!known || lastTicks == 0)
            return;

        var silentSec = Stopwatch.GetElapsedTime(lastTicks).TotalSeconds;
        if (silentSec <= VoiceProtocol.LinkTimeoutSec)
        {
            if (_lostLogged)
            {
                _lostLogged = false;
                Log("设备已恢复");
            }
            return;
        }

        if (!_lostLogged)
        {
            _lostLogged = true;
            Log($"设备静默超过 {VoiceProtocol.LinkTimeoutSec:0}s，保持监听等待其重连（beacon 继续发送）");
        }
    }

    // -- 内部：beacon 线程 --

    private void BeaconLoop()
    {
        var sock = _socket;
        if (sock is null)
            return;

        var json = Encoding.UTF8.GetBytes($"{{\"v\":1,\"role\":\"pc\",\"port\":{_port}}}");
        var packet = new byte[1 + json.Length];
        packet[0] = VoiceProtocol.TBeacon;
        json.CopyTo(packet, 1);

        _beaconTargets = ComputeTargets();
        Log($"beacon 目标: [{string.Join(", ", _beaconTargets)}]");

        var lastRefresh = Stopwatch.GetTimestamp();

        while (!_stop)
        {
            if (Stopwatch.GetElapsedTime(lastRefresh).TotalSeconds >= VoiceProtocol.BeaconRefreshSec)
            {
                lastRefresh = Stopwatch.GetTimestamp();
                var fresh = ComputeTargets();
                if (!SameTargets(fresh, _beaconTargets))
                {
                    Log($"网络变化，beacon 目标更新: [{string.Join(", ", fresh)}]");
                    _beaconTargets = fresh;
                }
            }

            var targets = _beaconTargets;
            IPEndPoint? device;
            lock (_gate)
            {
                device = _device;
            }

            foreach (var text in targets)
            {
                if (!IPEndPoint.TryParse(text, out var target))
                    continue;
                SendBeacon(sock, packet, target);
            }

            // 已学到的设备地址单播一份，确保换网后仍能直达。
            if (device is not null)
                SendBeacon(sock, packet, device);

            SleepBeforeNextBeacon();
        }
    }

    private void SendBeacon(Socket sock, byte[] packet, IPEndPoint target)
    {
        try
        {
            sock.SendTo(packet, target);
            _beaconSendErrLogged = false;
        }
        catch (SocketException ex)
        {
            if (!_beaconSendErrLogged)
            {
                _beaconSendErrLogged = true;
                Log($"beacon 发送失败({target.Address}): {ex.Message}");
            }
        }
        catch (ObjectDisposedException)
        {
            // Stop 关闭 socket
        }
    }

    /// <summary>分片睡眠，保证 Stop 能在一个分片内（100ms）收束线程。</summary>
    private void SleepBeforeNextBeacon()
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(VoiceProtocol.BeaconIntervalSec * Stopwatch.Frequency);
        while (!_stop && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// 受限广播 + 每个私网网段的定向广播（x.y.z.255）。
    ///
    /// 多网卡（网线 + Wi-Fi 同网段 / 代理 TUN）时，255.255.255.255 从哪个口出去
    /// 由系统路由决定，可能发错口导致设备收不到 beacon（实测踩过）；对每个私网
    /// 网段都发一份定向广播，哪个口通走哪个。每 30s 重算，换 Wi-Fi / 开关 VPN 后
    /// 广播段不会过期。
    /// </summary>
    private string[] ComputeTargets()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { IPAddress.Broadcast.ToString() };

        if (_presetDeviceIp is not null && IPEndPoint.TryParse($"{_presetDeviceIp}:{_port}", out var preset))
            set.Add(preset.ToString());

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try
                {
                    props = nic.GetIPProperties();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var unicast in props.UnicastAddresses)
                {
                    var addr = unicast.Address;
                    if (addr.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr))
                        continue;

                    var b = addr.GetAddressBytes();
                    if (b.Length != 4 || !IsPrivateV4(b))
                        continue;

                    set.Add(new IPAddress(new[] { b[0], b[1], b[2], (byte)255 }).ToString() + ":" + _port);
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log($"枚举网卡失败（仅用 255.255.255.255 广播）: {ex.Message}");
        }

        var list = set.ToArray();
        Array.Sort(list, StringComparer.Ordinal);
        return list;
    }

    private static bool IsPrivateV4(byte[] b)
    {
        if (b[0] == 10)
            return true;                                   // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return true;                                   // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168)
            return true;                                   // 192.168.0.0/16
        return false;
    }

    private static bool SameTargets(string[] a, string[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void Log(string message) => _log($"[udp] {message}");
}
