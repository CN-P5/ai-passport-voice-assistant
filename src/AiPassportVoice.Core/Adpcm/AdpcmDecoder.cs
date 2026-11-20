namespace AiPassportVoice.Core.Adpcm;

/// <summary>
/// IMA ADPCM 4:1 解码。与固件 main/apps/voice/adpcm.c 及 Python 版 windows/adpcm.py 逐字节对齐。
/// 每块自带 predictor/index，块独立可解，丢一块只损失 100ms。
/// </summary>
public static class AdpcmDecoder
{
    public const int HeaderBytes = 4;
    public const int BlockSamples = 1600;   // 100ms @ 16kHz
    public const int BlockBytes = HeaderBytes + BlockSamples / 2; // 804

    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    private static readonly sbyte[] IndexDelta =
    {
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8,
    };

    private const int IndexMax = 88;

    /// <summary>解码完整块(1600 样本)。数据不足返回空数组。</summary>
    public static short[] DecodeBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockBytes)
            return Array.Empty<short>();

        var predictor = data[0] | (data[1] << 8);
        if (predictor > short.MaxValue)
            predictor -= 0x10000;   // int16 LE 符号还原

        var index = Math.Clamp((int)data[2], 0, IndexMax);
        var samples = new short[BlockSamples];
        samples[0] = (short)predictor;

        var r = HeaderBytes;
        for (var i = 1; i < BlockSamples; i++)
        {
            int code;
            if ((i & 1) == 1)
            {
                code = data[r] & 0x0F;          // 低半字节先
            }
            else
            {
                code = (data[r] >> 4) & 0x0F;
                r++;
            }

            var step = StepTable[index];
            var diffq = step >> 3;
            if ((code & 4) != 0) diffq += step;
            if ((code & 2) != 0) diffq += step >> 1;
            if ((code & 1) != 0) diffq += step >> 2;

            predictor += (code & 8) != 0 ? -diffq : diffq;
            predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
            index = Math.Clamp(index + IndexDelta[code], 0, IndexMax);

            samples[i] = (short)predictor;
        }

        return samples;
    }

    /// <summary>解码为小端 int16 PCM 字节(直接可写声卡)。</summary>
    public static byte[] DecodeToPcmBytes(ReadOnlySpan<byte> data)
    {
        var samples = DecodeBlock(data);
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[i * 2] = (byte)(samples[i] & 0xFF);
            pcm[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }
        return pcm;
    }
}
