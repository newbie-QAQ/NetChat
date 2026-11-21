using System;
using System.IO;
using System.Reflection;

namespace NetChat
{
    /// <summary>
    /// 点歌扩展包(NAudio.dll)的反射桥。
    ///
    /// 主模组编译时不引用 NAudio —— 同一份 NetChat.dll, 模组目录里放了 NAudio.dll 的
    /// 就是"Q群完整版", 没放的就是"GitHub 普通版", 运行时自动探测, 功能自动升级:
    /// - MP3 完整解码成 PCM(走 Windows 系统自带的解码器, 不带任何原生文件)
    /// - 解码出来的波形能做削波率分析(恶意音频检测覆盖 MP3)
    /// - 播放走 DynamicSoundEffectInstance(和游戏音乐混音, 不抢 Media 频道)
    /// 加载失败(文件损坏/版本不配)就退回普通版路径, 不会崩。
    /// </summary>
    internal static class MediaExt
    {
        private static bool probed;
        private static Type mp3ReaderType;   // NAudio.Wave.Mp3FileReader
        private static Type waveFormatType;  // NAudio.Wave.WaveFormat

        /// <summary>扩展包是否可用(Mods/NetChat/NAudio.dll 存在且能加载)</summary>
        public static bool Available
        {
            get { Probe(); return mp3ReaderType != null; }
        }

        private static void Probe()
        {
            if (probed) return;
            probed = true;

            try
            {
                string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "NetChat", "NAudio.dll");
                if (!File.Exists(dll)) return;

                Assembly asm = Assembly.LoadFrom(dll);
                Type reader = asm.GetType("NAudio.Wave.Mp3FileReader", false);
                Type fmt = asm.GetType("NAudio.Wave.WaveFormat", false);

                if (reader != null && fmt != null)
                {
                    mp3ReaderType = reader;
                    waveFormatType = fmt;
                }
            }
            catch
            {
                mp3ReaderType = null;
            }
        }

        /// <summary>
        /// 整个 MP3 解码成 16bit PCM(解码器是 Windows 系统自带的, NAudio 只是壳)。
        /// 大小上限 150MB PCM, 防止解压炸弹; 超了算失败。
        /// </summary>
        public static bool DecodeMp3(string path, out int sampleRate, out int channels, out byte[] pcm, out string error)
        {
            sampleRate = 0;
            channels = 0;
            pcm = null;
            error = null;

            if (!Available) { error = "扩展包未安装"; return false; }

            object reader = null;

            try
            {
                reader = Activator.CreateInstance(mp3ReaderType, path);

                object fmt = mp3ReaderType.GetProperty("WaveFormat").GetValue(reader, null);
                sampleRate = (int)waveFormatType.GetProperty("SampleRate").GetValue(fmt, null);
                channels = (int)waveFormatType.GetProperty("Channels").GetValue(fmt, null);

                MethodInfo read = mp3ReaderType.GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) });
                if (read == null) { error = "扩展包版本不匹配"; return false; }

                using (MemoryStream dst = new MemoryStream())
                {
                    byte[] buf = new byte[64 * 1024];
                    const long HardCap = 150L * 1024 * 1024;

                    while (true)
                    {
                        int n = (int)read.Invoke(reader, new object[] { buf, 0, buf.Length });
                        if (n <= 0) break;

                        dst.Write(buf, 0, n);
                        if (dst.Length > HardCap) { error = "解码出来的数据过大, 疑似构造文件, 已拒绝"; return false; }
                    }

                    pcm = dst.ToArray();
                }

                if (pcm.Length == 0) { error = "解码结果为空"; return false; }
                return true;
            }
            catch (Exception e)
            {
                error = (e.InnerException ?? e).Message;
                return false;
            }
            finally
            {
                try { if (reader is IDisposable d) d.Dispose(); } catch { }
            }
        }

        /// <summary>16bit PCM 的削波率(满量程 95% 以上的样本占比); pcm 空返回 -1</summary>
        public static float ClipRatio(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2) return -1f;

            int total = pcm.Length / 2;
            int step = Math.Max(1, total / 2000000);
            int count = 0, clipped = 0;

            for (int i = 0; i < total; i += step)
            {
                int off = i * 2;
                short s = (short)(pcm[off] | (pcm[off + 1] << 8));
                count++;
                if (s >= 31032 || s <= -31032) clipped++;
            }

            return count > 0 ? (float)clipped / count : -1f;
        }
    }
}
