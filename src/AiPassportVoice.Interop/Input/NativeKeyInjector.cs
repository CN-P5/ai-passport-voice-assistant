using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using AiPassportVoice.Core.Input;

namespace AiPassportVoice.Interop.Input;

/// <summary>
/// Windows 键盘注入。SendInput 优先，失败降级 keybd_event。
/// 默认在专用 MTA 线程执行，并挂接 WinSta0\Default（与 Python 版一致）；
/// 也可传入 dispatch 委托改用 UI 线程执行。
/// </summary>
public sealed class NativeKeyInjector : IKeyInjector, IDisposable
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const int UOI_NAME = 2;

    private readonly IReadOnlyList<KeyDef> _keys;
    private readonly string _mode;
    private readonly Action<string> _log;
    private readonly Action<Action>? _dispatch;
    private readonly BlockingCollection<Action>? _queue;
    private readonly Thread? _thread;

    private volatile bool _held;
    private volatile bool _active;
    private volatile bool _disposed;
    private bool _loggedSendOk;
    private static int _desktopLogged;
    private long _tapStartedTick;

    public NativeKeyInjector(IReadOnlyList<KeyDef> keys, string mode, Action<string> log,
        Action<Action>? dispatch = null)
    {
        _keys = keys;
        _mode = mode;
        _log = log;

        if (dispatch is not null)
        {
            _dispatch = dispatch;
            return;
        }

        // 不设置 STA：STA 会初始化 COM/隐藏窗口，导致 SetThreadDesktop 失效
        _queue = new BlockingCollection<Action>();
        _thread = new Thread(Worker) { IsBackground = true, Name = "key-injector" };
        _thread.Start();
    }

    public bool IsActive => _held || _active;

    /// <summary>置 true 时改用 keybd_event 注入（用于对照排查 SendInput 被拦的情况）。</summary>
    public static bool PreferKeybdEvent { get; set; }

    public void Start()
    {
        if (_mode == "hold")
        {
            if (_held) return;
            Run(() =>
            {
                _held = true;
                foreach (var k in _keys) SendKey(k, up: false);
                _log($"[key] {Describe()} 按下(hold, 开启语音输入)");
            });
        }
        else
        {
            if (_active) return;
            Run(() =>
            {
                _active = true;
                _tapStartedTick = Environment.TickCount64;
                foreach (var k in _keys) SendKey(k, up: false);
                Thread.Sleep(60);
                for (var i = _keys.Count - 1; i >= 0; i--) SendKey(_keys[i], up: true);
                _log($"[key] {Describe()} 点击(tap, 开启语音输入)");
            });
        }
    }

    public void Stop()
    {
        if (_mode == "hold")
        {
            if (!_held) return;
            Run(() =>
            {
                _held = false;
                for (var i = _keys.Count - 1; i >= 0; i--) SendKey(_keys[i], up: true);
                _log($"[key] {Describe()} 抬起(hold, 结束语音输入)");
            });
        }
        else
        {
            if (!_active) return;
            Run(() =>
            {
                _active = false;

                // 短按时补齐到 300ms，避免第二次点击被输入法当成双击而忽略
                var elapsed = Environment.TickCount64 - _tapStartedTick;
                if (elapsed < 300) Thread.Sleep((int)(300 - elapsed));

                Thread.Sleep(80);
                foreach (var k in _keys) SendKey(k, up: false);
                Thread.Sleep(60);
                for (var i = _keys.Count - 1; i >= 0; i--) SendKey(_keys[i], up: true);
                _log($"[key] {Describe()} 再次点击(tap, 结束语音输入)");
            });
        }
    }

    public void Click()
    {
        Run(() =>
        {
            foreach (var k in _keys) SendKey(k, up: false);
            Thread.Sleep(60);
            for (var i = _keys.Count - 1; i >= 0; i--) SendKey(_keys[i], up: true);
            _log($"[key] {Describe()} 点击(执行发送/确认)");
        });
    }

    private string Describe() => string.Join("+", _keys.Select(k => k.Name));

    private void Run(Action action)
    {
        if (_disposed) return;

        try
        {
            if (_dispatch is not null)
            {
                _dispatch(action);
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue!.Add(() =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            tcs.Task.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log($"[key] 注入失败: {ex.Message}");
        }
    }

    private void Worker()
    {
        AttachToDefaultDesktop();
        try
        {
            foreach (var action in _queue!.GetConsumingEnumerable())
                action();
        }
        catch (ObjectDisposedException) { }
    }

    private void AttachToDefaultDesktop()
    {
        var desk = OpenDesktopW("Default", 0, false, 0x01FF);
        if (desk == IntPtr.Zero)
        {
            if (Interlocked.Exchange(ref _desktopLogged, 1) == 0)
                _log($"[key] OpenDesktopW 失败(err={Marshal.GetLastWin32Error()})");
            return;
        }

        var ok = SetThreadDesktop(desk);

        // 仅启动时记录一次桌面挂接结果
        if (Interlocked.Exchange(ref _desktopLogged, 1) != 0) return;

        var il = GetIntegrityLevel();
        if (ok)
        {
            var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            _log($"[key] 桌面挂接: 进程IL={il} 会话={session} 窗口站={GetWindowStationName()} 桌面 {GetCurrentDesktopName()}");
        }
        else
        {
            _log($"[key] 桌面挂接失败(err={Marshal.GetLastWin32Error()})，进程IL={il}");
        }
    }

    private static string GetIntegrityLevel()
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out token))
                return "?";

            return ReadIntegrityLevel(token);
        }
        catch
        {
            return "?";
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static string ReadIntegrityLevel(IntPtr token)
    {
        GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out var size);
        if (size <= 0) return "?";

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 25, buf, size, out _))
                return "?";

            // TOKEN_MANDATORY_LABEL → SID_AND_ATTRIBUTES{ PSID; DWORD }；SID 最后一个 SubAuthority 即 IL
            var sid = Marshal.ReadIntPtr(buf);
            var count = Marshal.ReadByte(sid, 1);
            var last = Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
            return last switch
            {
                0x1000 => "Low",
                0x2000 => "Medium",
                0x3000 => "High",
                0x4000 => "System",
                _ => $"0x{last:X4}",
            };
        }
        catch
        {
            return "?";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string GetWindowStationName()
    {
        try
        {
            var h = GetProcessWindowStation();
            if (h == IntPtr.Zero) return "?";

            var sb = new StringBuilder(256);
            return GetUserObjectInformationW(h, UOI_NAME, sb, 512, out _) ? sb.ToString() : "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string GetCurrentDesktopName()
    {
        try
        {
            var h = GetThreadDesktop(GetCurrentThreadId());
            if (h == IntPtr.Zero) return "?";

            var sb = new StringBuilder(256);
            return GetUserObjectInformationW(h, UOI_NAME, sb, 512, out _) ? sb.ToString() : "?";
        }
        catch
        {
            return "?";
        }
    }

    private static void SendKeyCore(KeyDef key, bool up, Action<string> log, ref bool loggedOk)
    {
        var flags = (key.Scan != 0 ? KEYEVENTF_SCANCODE : 0)
                  | (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0)
                  | (up ? KEYEVENTF_KEYUP : 0);

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = key.Vk,
                    wScan = key.Scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };

        var size = Marshal.SizeOf<INPUT>();

        if (PreferKeybdEvent)
        {
            keybd_event((byte)(key.Vk & 0xFF), (byte)(key.Scan & 0xFF), flags, UIntPtr.Zero);
            if (!loggedOk)
            {
                loggedOk = true;
                log($"[key] keybd_event ok (vk=0x{key.Vk:X2})");
            }
            return;
        }

        var sent = SendInput(1, ref input, size);
        if (sent == 1)
        {
            if (!loggedOk)
            {
                loggedOk = true;
                log($"[key] SendInput ok (INPUT={size}B, vk=0x{key.Vk:X2}, scan=0x{key.Scan:X2})");
            }
            return;
        }

        var err = Marshal.GetLastWin32Error();
        log($"[key] SendInput 未生效(ret={sent}, err={err})，降级 keybd_event");
        keybd_event((byte)(key.Vk & 0xFF), (byte)(key.Scan & 0xFF), flags, UIntPtr.Zero);
    }

    private void SendKey(KeyDef key, bool up)
        => SendKeyCore(key, up, _log, ref _loggedSendOk);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_held)
            {
                _held = false;
                foreach (var k in _keys) SendKey(k, up: true);
            }
        }
        catch { }

        try { _queue?.CompleteAdding(); } catch { }
        try { _thread?.Join(1000); } catch { }
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, StringBuilder pvInfo,
        int nLength, out int lpnLengthNeeded);
}
