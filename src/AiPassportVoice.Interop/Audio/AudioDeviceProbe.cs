using System.Runtime.InteropServices;

namespace AiPassportVoice.Interop.Audio;

public enum CableState
{
    /// <summary>CABLE Input 可打开，CABLE Output 存在。</summary>
    Ready,
    /// <summary>完全没有 CABLE 端点，判定未安装。</summary>
    NotInstalled,
    /// <summary>端点存在但无法以 16kHz 单声道打开，或 CABLE Output 缺失。</summary>
    Unusable,
}

public sealed record CableStatus(CableState State, string? OutputName, int OutputIndex, string Detail)
{
    public bool IsReady => State == CableState.Ready;
}

/// <summary>
/// 音频端点探测。零依赖：waveOut/waveIn 的 MME 枚举。
/// 用于启动检测 VB-CABLE 与列出可选输出设备。
/// </summary>
public static class AudioDeviceProbe
{
    private const uint WAVE_FORMAT_PCM = 1;

    public static IReadOnlyList<string> EnumerateOutputs()
    {
        var shortNames = EnumerateMmeOutputs();
        if (shortNames.Count == 0) return shortNames;

        // MME 的 szPname 只有 32 字符，名字会被硬截断；用 WASAPI 端点全名按前缀补齐。
        List<string> full;
        try { full = EnumerateWasapiNames(0); }
        catch { full = new List<string>(); }

        return full.Count == 0
            ? shortNames
            : shortNames.Select(n => ResolveFullName(n, full)).ToList();
    }

    private static List<string> EnumerateMmeOutputs()
    {
        var list = new List<string>();
        var count = waveOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (waveOutGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WAVEOUTCAPS>()) == 0)
                list.Add(caps.szPname ?? string.Empty);
        }
        return list;
    }

    private static string ResolveFullName(string shortName, List<string> fullNames)
    {
        if (string.IsNullOrEmpty(shortName)) return shortName;
        var match = fullNames
            .Where(n => n.StartsWith(shortName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Length)
            .FirstOrDefault();
        return match ?? shortName;
    }

    public static IReadOnlyList<string> EnumerateInputs()
    {
        var list = new List<string>();
        var count = waveInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (waveInGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WAVEINCAPS>()) == 0)
                list.Add(caps.szPname ?? string.Empty);
        }
        return list;
    }

    /// <summary>返回第一个名称包含 substr 的输出设备索引，找不到为 -1。</summary>
    public static int FindOutputIndex(string substr)
    {
        var outputs = EnumerateOutputs();
        for (var i = 0; i < outputs.Count; i++)
            if (outputs[i].Contains(substr, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public static int FindInputIndex(string substr)
    {
        var inputs = EnumerateInputs();
        for (var i = 0; i < inputs.Count; i++)
            if (inputs[i].Contains(substr, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>尝试以 16kHz / 单声道 / 16bit 打开输出端点。</summary>
    public static bool CanOpenOutput(int deviceIndex, int sampleRate = 16000, short channels = 1, short bits = 16)
    {
        if (deviceIndex < 0) return false;
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = (ushort)WAVE_FORMAT_PCM,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = (ushort)bits,
            nBlockAlign = (ushort)(channels * bits / 8),
            nAvgBytesPerSec = (uint)(sampleRate * channels * bits / 8),
            cbSize = 0,
        };
        var result = waveOutOpen(out var h, (uint)deviceIndex, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) return false;
        waveOutClose(h);
        return true;
    }

    /// <summary>检测 VB-CABLE：输出 CABLE Input + 输入 CABLE Output + 16k 可打开。</summary>
    public static CableStatus ProbeCable(string outputSubstr = "CABLE Input")
    {
        if (string.IsNullOrWhiteSpace(outputSubstr))
            outputSubstr = "CABLE Input";

        var outputs = EnumerateOutputs();
        var outIdx = -1;
        for (var i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].Contains(outputSubstr, StringComparison.OrdinalIgnoreCase))
            {
                outIdx = i;
                break;
            }
        }

        if (outIdx < 0)
        {
            var anyCable = outputs.Any(n => n.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
            return new CableStatus(CableState.NotInstalled, null, -1,
                anyCable ? "仅发现部分 CABLE 端点，可能安装不完整" : "未发现 CABLE Input 输出端点");
        }

        var inIdx = FindInputIndex("CABLE Output");
        if (inIdx < 0)
            return new CableStatus(CableState.Unusable, outputs[outIdx], outIdx, "缺少 CABLE Output 录音端点");

        if (!CanOpenOutput(outIdx))
            return new CableStatus(CableState.Unusable, outputs[outIdx], outIdx, "端点存在，但无法以 16kHz 单声道打开");

        return new CableStatus(CableState.Ready, outputs[outIdx], outIdx, "CABLE Input 可用");
    }

    // ---------------- WASAPI 端点全名（仅枚举，不播放） ----------------

    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int STGM_READ = 0;

    private static readonly PROPERTYKEY PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    private static List<string> EnumerateWasapiNames(int dataFlow)
    {
        var names = new List<string>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(dataFlow, DEVICE_STATE_ACTIVE, out collection) != 0 || collection is null)
                return names;

            if (collection.GetCount(out var count) != 0) return names;

            for (var i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IPropertyStore? store = null;
                try
                {
                    if (collection.Item(i, out device) != 0 || device is null) continue;
                    if (device.OpenPropertyStore(STGM_READ, out store) != 0 || store is null) continue;

                    var key = PkeyDeviceFriendlyName;
                    if (store.GetValue(ref key, out var value) != 0) continue;

                    try
                    {
                        var name = value.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
                    }
                    finally
                    {
                        value.Clear();
                    }
                }
                finally
                {
                    if (store is not null) Marshal.ReleaseComObject(store);
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr p1;
        public IntPtr p2;

        private const ushort VT_LPWSTR = 31;

        public string? GetString()
            => vt == VT_LPWSTR && p1 != IntPtr.Zero ? Marshal.PtrToStringUni(p1) : null;

        public void Clear() => PropVariantClear(ref this);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveOutGetDevCapsW(UIntPtr uDeviceID, out WAVEOUTCAPS pwc, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveInGetDevCapsW(UIntPtr uDeviceID, out WAVEINCAPS pwic, uint cbwic);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WAVEFORMATEX lpFormat,
        IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    internal static extern uint waveOutClose(IntPtr hWaveOut);
}
