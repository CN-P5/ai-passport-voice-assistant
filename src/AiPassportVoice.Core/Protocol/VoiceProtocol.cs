namespace AiPassportVoice.Core.Protocol;

/// <summary>
/// UDP 语音通道契约。与设备固件 device/main/apps/voice/udp_audio.h 逐字节对齐，改一侧必须同步另一侧。
/// </summary>
public static class VoiceProtocol
{
    public const int Port = 33333;

    public const byte TAudio = 0x01;
    public const byte TEvent = 0x02;
    public const byte TBeacon = 0x03;
    public const byte TPing = 0x04;

    /// <summary>AUDIO 载荷长度：[seq][0x80] + 804B ADPCM block。</summary>
    public const int AudioFrameLen = 806;
    public const int AdpcmBlockBytes = 804;

    public const int RecvMax = 4096;

    public const double BeaconIntervalSec = 1.0;
    public const double BeaconFastIntervalSec = 1.0;
    public const double BeaconRefreshSec = 30.0;
    public const double LinkTimeoutSec = 12.0;

    public const int PendingMax = 64;
}
