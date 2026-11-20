using System.Runtime.InteropServices;
using AiPassportVoice.Core.Adpcm;
using AiPassportVoice.Core.Audio;

namespace AiPassportVoice.Interop.Audio;

/// <summary>
/// waveOut (winmm) 音频出口。零第三方依赖。
/// 解码 ADPCM → 3200B PCM 块 → 3 个缓冲轮转写入虚拟声卡(CABLE Input)。
/// 队列有界，满则丢最旧；空闲补静音维持实时节拍；结束前 Drain。
/// </summary>
public sealed class WaveOutAudioSink : IAudioSink, IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int Bits = 16;
    private const int PcmBlockBytes = AdpcmDecoder.BlockSamples * Channels * Bits / 8; // 3200
    private const int SlotCount = 3;

    private int _levelMilli;
    private long _lastBlockTick;
    private const int QueueCapacity = 5;
    private const uint WHDR_DONE = 0x00000001;

    private readonly Action<string> _log;
    private readonly object _qLock = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly byte[] _silence = new byte[PcmBlockBytes];
    private readonly IntPtr[] _dataPtr = new IntPtr[SlotCount];
    private readonly IntPtr[] _hdrPtr = new IntPtr[SlotCount];
    private readonly bool[] _firstUse = new bool[SlotCount];

    private IntPtr _hwo = IntPtr.Zero;
    private Thread? _writer;
    private volatile bool _stop;
    private volatile bool _enabled;

    public bool Enabled => _enabled;
    public string? DeviceName { get; }

    public WaveOutAudioSink(int deviceIndex, string deviceName, Action<string> log)
    {
        _log = log;
        DeviceName = deviceName;

        if (deviceIndex < 0)
        {
            _log("[audio] 未指定输出设备，麦克风通道未就绪");
            return;
        }

        var fmt = new AudioDeviceProbe.WAVEFORMATEX
        {
            wFormatTag = 1, // WAVE_FORMAT_PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = Bits,
            nBlockAlign = Channels * Bits / 8,
            nAvgBytesPerSec = SampleRate * Channels * Bits / 8,
            cbSize = 0,
        };

        var res = AudioDeviceProbe.waveOutOpen(out var h, (uint)deviceIndex, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (res != 0)
        {
            _log($"[audio] 打开 [{deviceIndex}] '{deviceName}' 失败 (mmresult={res})");
            return;
        }
        _hwo = h;

        try
        {
            PrepareSlots();
        }
        catch (Exception ex)
        {
            _log($"[audio] 初始化缓冲失败: {ex.Message}");
            CleanupNative();
            return;
        }

        _enabled = true;
        _writer = new Thread(Writer) { IsBackground = true, Name = "audio-writer" };
        _writer.Start();
        _log($"[audio] 麦克风路由就绪 → '{deviceName}'");
    }

    public void WriteBlock(ReadOnlySpan<byte> adpcmBlock)
    {
        if (!_enabled) return;

        byte[] pcm;
        try
        {
            pcm = AdpcmDecoder.DecodeToPcmBytes(adpcmBlock);
        }
        catch (Exception ex)
        {
            _log($"[audio] 解码失败: {ex.Message}");
            return;
        }

        if (pcm.Length != PcmBlockBytes) return;

        // 电平：整块 RMS，只做原子写入，不进队列锁，不影响转发
        double sum = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += (double)s * s;
        }

        var rms = Math.Sqrt(sum / (pcm.Length / 2)) / 32768.0;
        Interlocked.Exchange(ref _levelMilli, (int)Math.Min(1000, rms * 8000));
        Interlocked.Exchange(ref _lastBlockTick, Environment.TickCount64);

        lock (_qLock)
        {
            if (_queue.Count >= QueueCapacity)
                _queue.Dequeue();     // 丢最旧，保延迟
            _queue.Enqueue(pcm);
        }
    }

    /// <summary>最近一块音频的 RMS 电平（0..1）。超过 250ms 无新块视为静音。</summary>
    public double Level
    {
        get
        {
            var age = Environment.TickCount64 - Interlocked.Read(ref _lastBlockTick);
            if (age > 250) return 0;
            return Interlocked.CompareExchange(ref _levelMilli, 0, 0) / 1000.0;
        }
    }

    public void Clear()
    {
        lock (_qLock) _queue.Clear();
        if (_hwo != IntPtr.Zero)
        {
            try
            {
                waveOutReset(_hwo);
                for (var i = 0; i < SlotCount; i++) _firstUse[i] = true;
            }
            catch { }
        }
    }

    public void Drain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            bool empty;
            lock (_qLock) empty = _queue.Count == 0;

            var allDone = true;
            for (var i = 0; i < SlotCount; i++)
            {
                if (!_firstUse[i] && !IsSlotDone(i)) { allDone = false; break; }
            }

            if (empty && allDone) return;
            Thread.Sleep(5);
        }
        Clear();
    }

    private void Writer()
    {
        var idx = 0;
        while (!_stop)
        {
            byte[] data;
            bool got;
            lock (_qLock)
            {
                got = _queue.Count > 0;
                data = got ? _queue.Dequeue() : _silence;
            }

            // 等该缓冲播放完毕
            var waited = 0;
            while (!_stop && !_firstUse[idx] && !IsSlotDone(idx))
            {
                Thread.Sleep(3);
                waited += 3;
                if (waited > 1500)
                {
                    _log("[audio] 缓冲长时间未完成，重置");
                    try { waveOutReset(_hwo); } catch { }
                    for (var i = 0; i < SlotCount; i++) _firstUse[i] = true;
                    break;
                }
            }
            if (_stop) break;

            Marshal.Copy(data, 0, _dataPtr[idx], PcmBlockBytes);
            var r = waveOutWrite(_hwo, _hdrPtr[idx], (uint)Marshal.SizeOf<WAVEHDR>());
            if (r != 0)
            {
                _log($"[audio] waveOutWrite 失败 (mmresult={r})");
                _enabled = false;
                break;
            }

            _firstUse[idx] = false;
            idx = (idx + 1) % SlotCount;
        }
    }

    private void PrepareSlots()
    {
        var hdrSize = Marshal.SizeOf<WAVEHDR>();
        for (var i = 0; i < SlotCount; i++)
        {
            _dataPtr[i] = Marshal.AllocHGlobal(PcmBlockBytes);
            _hdrPtr[i] = Marshal.AllocHGlobal(hdrSize);
            var hdr = new WAVEHDR
            {
                lpData = _dataPtr[i],
                dwBufferLength = (uint)PcmBlockBytes,
            };
            Marshal.StructureToPtr(hdr, _hdrPtr[i], false);
            var r = waveOutPrepareHeader(_hwo, _hdrPtr[i], (uint)hdrSize);
            if (r != 0)
                throw new InvalidOperationException($"waveOutPrepareHeader={r}");
            _firstUse[i] = true;
        }
    }

    private bool IsSlotDone(int index) =>
        (Marshal.PtrToStructure<WAVEHDR>(_hdrPtr[index]).dwFlags & WHDR_DONE) != 0;

    private void CleanupNative()
    {
        if (_hwo == IntPtr.Zero) return;
        var hdrSize = Marshal.SizeOf<WAVEHDR>();
        try { waveOutReset(_hwo); } catch { }
        for (var i = 0; i < SlotCount; i++)
        {
            if (_hdrPtr[i] != IntPtr.Zero)
            {
                try { waveOutUnprepareHeader(_hwo, _hdrPtr[i], (uint)hdrSize); } catch { }
                Marshal.FreeHGlobal(_hdrPtr[i]);
                _hdrPtr[i] = IntPtr.Zero;
            }
            if (_dataPtr[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_dataPtr[i]);
                _dataPtr[i] = IntPtr.Zero;
            }
        }
        AudioDeviceProbe.waveOutClose(_hwo);
        _hwo = IntPtr.Zero;
    }

    public void Dispose()
    {
        _stop = true;
        _enabled = false;
        try { _writer?.Join(1000); } catch { }
        CleanupNative();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr hwo);
}
