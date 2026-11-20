namespace AiPassportVoice.Core.Audio;

/// <summary>
/// 音频出口。把设备的 ADPCM 块解码后按实时节奏写入虚拟声卡。
/// </summary>
public interface IAudioSink
{
    /// <summary>声卡是否可用。不可用时设备会显示「音频未就绪」并拒绝按住说话。</summary>
    bool Enabled { get; }

    /// <summary>实际使用的设备名（诊断/日志用），不可用为 null。</summary>
    string? DeviceName { get; }

    /// <summary>写入一个 804B ADPCM 块。内部负责解码与排队。</summary>
    void WriteBlock(ReadOnlySpan<byte> adpcmBlock);

    /// <summary>丢弃队列中未播放的数据（会话开始时清场）。</summary>
    void Clear();

    /// <summary>等待队列排空，最多 timeout，然后清空剩余（结束说话前调用）。</summary>
    void Drain(TimeSpan timeout);
}
