using System;

namespace NetChat
{
    /// <summary>音频文件的元信息, 发送前校验和接收前复核都用它</summary>
    internal sealed class AudioInfo
    {
        public int Seconds;        // 估算时长
        public int AvgKbps;        // 平均码率(千比特/秒)
        public int SampleRate;
        public int Channels;
        public int Bits;           // 位深, MP3 填 16
        public float ClipRatio = -1f;   // 满振幅样本占比, 只有 WAV 做; -1 = 没分析
    }

    /// <summary>
    /// 零依赖的音频文件解析: 只读文件头和原始字节, 不做任何解码。
    /// WAV 能拿到精确时长和波形削波率; MP3 只能从头几帧估时长和码率
    /// (完整解码要靠 MediaExt 的 NAudio 扩展)。
    /// </summary>
    internal static class AudioProbe
    {
        /// <summary>解析 WAV; 不是合法 PCM WAV 返回 null</summary>
        public static AudioInfo Wav(byte[] d)
        {
            if (d == null || d.Length < 44) return null;
            if (d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return null;
            if (d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E') return null;

            int pos = 12;
            int format = -1, channels = 0, rate = 0, byteRate = 0, bits = 0, dataLen = 0, dataOff = -1;

            // 走 chunk 链找 fmt 和 data
            while (pos + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
                int size = BitConverter.ToInt32(d, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > d.Length) return null;

                if (id == "fmt ")
                {
                    if (size < 16) return null;
                    format = BitConverter.ToUInt16(d, body) & 0xFFFF;
                    channels = BitConverter.ToUInt16(d, body + 2) & 0xFFFF;
                    rate = BitConverter.ToInt32(d, body + 4);
                    byteRate = BitConverter.ToInt32(d, body + 8);
                    bits = BitConverter.ToUInt16(d, body + 14) & 0xFFFF;
                }
                else if (id == "data")
                {
                    dataOff = body;
                    dataLen = size;
                }

                pos = body + size + (size & 1); // chunk 按 2 字节对齐
            }

            // PCM=1 或 WAVE_FORMAT_EXTENSIBLE=0xFFFE (extensible 的子格式不深究, 位深对了就放行)
            if (dataOff < 0 || dataLen <= 0) return null;
            if (format != 1 && format != 0xFFFE) return null;
            if (channels < 1 || channels > 2) return null;
            if (rate < 1 || byteRate < 1) return null;
            if (bits != 8 && bits != 16 && bits != 24) return null;

            AudioInfo info = new AudioInfo
            {
                SampleRate = rate,
                Channels = channels,
                Bits = bits,
                Seconds = dataLen / byteRate,
                AvgKbps = byteRate * 8 / 1000
            };

            // 削波率: 只分析 16bit PCM, 采样最多看 200 万个(步进跳着看)
            if (bits == 16)
            {
                int total = dataLen / 2;
                int step = Math.Max(1, total / 2000000);
                int count = 0, clipped = 0;

                for (int i = 0; i < total; i += step)
                {
                    int off = dataOff + i * 2;
                    if (off + 2 > d.Length) break;

                    short s = (short)(d[off] | (d[off + 1] << 8));
                    count++;
                    if (s >= 31032 || s <= -31032) clipped++; // 满量程 95% 以上算削波
                }

                if (count > 0) info.ClipRatio = (float)clipped / count;
            }

            return info;
        }

        /// <summary>解析 MP3(估时长); 完全找不到合法帧返回 null</summary>
        public static AudioInfo Mp3(byte[] d)
        {
            if (d == null || d.Length < 256) return null;

            int pos = 0;

            // 跳过 ID3v2 头
            if (d[0] == 'I' && d[1] == 'D' && d[2] == '3' && d.Length > 10)
            {
                int size = ((d[6] & 0x7F) << 21) | ((d[7] & 0x7F) << 14) | ((d[8] & 0x7F) << 7) | (d[9] & 0x7F);
                pos = 10 + size + ((d[5] & 0x10) != 0 ? 10 : 0);
            }

            // 找第一帧
            int frame = -1;
            int limit = Math.Min(d.Length - 4, pos + 65536);
            for (int i = pos; i < limit; i++)
            {
                if (d[i] == 0xFF && (d[i + 1] & 0xE0) == 0xE0 && ValidFrame(d, i)) { frame = i; break; }
            }

            if (frame < 0) return null;

            int versionBits = (d[frame + 1] >> 3) & 3;   // 11=MPEG1, 10=MPEG2, 00=MPEG2.5
            int bitrateIdx = (d[frame + 2] >> 4) & 15;
            int rateIdx = (d[frame + 2] >> 2) & 3;

            int[] rates1 = { 44100, 48000, 32000 };
            int[] rates2 = { 22050, 24000, 16000 };
            int[] rates25 = { 11025, 12000, 8000 };

            int rate;
            if (versionBits == 3) rate = rates1[rateIdx];
            else if (versionBits == 2) rate = rates2[rateIdx];
            else rate = rates25[rateIdx];

            int[] br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            int[] br2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
            int bitrate = (versionBits == 3 ? br1 : br2)[bitrateIdx];
            if (bitrate <= 0) return null;

            int samplesPerFrame = versionBits == 3 ? 1152 : 576;
            bool mono = ((d[frame + 3] >> 6) & 3) == 3;

            // Xing/Info 头里有总帧数的话能算出 VBR 的准确时长
            int sideInfo = versionBits == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            int xing = frame + 4 + sideInfo;
            int seconds;

            if (xing + 12 <= d.Length
                && (d[xing] == 'X' && d[xing + 1] == 'i' && d[xing + 2] == 'n' && d[xing + 3] == 'g'
                 || d[xing] == 'I' && d[xing + 1] == 'n' && d[xing + 2] == 'f' && d[xing + 3] == 'o')
                && (BitConverter.ToInt32(d, xing + 4) & unchecked((int)0x80000000)) != 0)
            {
                int frames = BitConverter.ToInt32(d, xing + 8);
                if (frames > 0)
                    seconds = (int)((long)frames * samplesPerFrame / rate);
                else
                    seconds = (int)((long)d.Length * 8 / (bitrate * 1000));
            }
            else
            {
                // 没有 Xing 就按首帧码率估(CBR 基本准, VBR 有偏差但校验够用)
                seconds = (int)((long)d.Length * 8 / (bitrate * 1000));
            }

            return new AudioInfo
            {
                SampleRate = rate,
                Channels = mono ? 1 : 2,
                Bits = 16,
                Seconds = seconds,
                AvgKbps = bitrate
            };
        }

        /// <summary>检查偏移处是不是一个合法的 MPEG Layer III 帧头</summary>
        private static bool ValidFrame(byte[] d, int i)
        {
            if (i + 4 > d.Length) return false;

            int versionBits = (d[i + 1] >> 3) & 3;   // 01 是保留
            int layerBits = (d[i + 1] >> 1) & 3;     // 00 是保留
            if (versionBits == 1 || layerBits != 1) return false; // 只认 Layer III

            int bitrateIdx = (d[i + 2] >> 4) & 15;
            int rateIdx = (d[i + 2] >> 2) & 3;
            if (bitrateIdx == 0 || bitrateIdx == 15) return false;
            if (rateIdx == 3) return false;

            int emphasis = d[i + 3] & 3;
            if (emphasis == 2) return false;

            return true;
        }
    }
}
