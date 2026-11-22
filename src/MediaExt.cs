using System;
using System.Collections.Generic;
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
        private static bool handlerAttached;
        private static Type mp3ReaderType;   // NAudio.Wave.Mp3FileReader
        private static Type waveFormatType;  // NAudio.Wave.WaveFormat

        /// <summary>按文件名缓存已字节加载的程序集: 同一文件永远返回同一实例。
        /// 探测时拿到的 Core 和 JIT 绑定时拿到的必须是同一份, 不然两份字节加载实例
        /// 的反射类型互不兼容(同名不同程序集实例), GetValue 会直接抛参数异常</summary>
        private static readonly Dictionary<string, Assembly> byteLoaded = new Dictionary<string, Assembly>();

        /// <summary>探测失败的具体原因(未装/加载失败/版本不对), 给面板显示用</summary>
        private static string failReason;

        /// <summary>扩展包是否可用(Mods/NetChat/NAudio.dll 存在且能加载)</summary>
        public static bool Available
        {
            get { Probe(); return mp3ReaderType != null; }
        }

        /// <summary>面板右上角那行字: 已启用 / 未装(原因)</summary>
        public static string StatusText
        {
            get
            {
                Probe();
                return mp3ReaderType != null
                    ? "扩展: 已启用"
                    : "扩展: 未装" + (failReason == null ? "" : " (" + failReason + ")");
            }
        }

        /// <summary>
        /// NAudio 程序集解析器: 默认绑定找不到的 NAudio.* 都从模组目录按字节加载。
        /// 常驻不摘(之后 JIT Mp3FileReader 方法体时还需要它来补依赖)。
        /// 用字节加载是为了绕开"同身份程序集已加载就返回旧实例"的坑——
        /// 游戏进程里哪怕有人用别的方式加载过同名程序集, 这里也保证拿到的是目录里这份。
        /// </summary>
        private static Assembly ResolveNAudio(object sender, ResolveEventArgs args)
        {
            try
            {
                string simple = new AssemblyName(args.Name).Name;
                if (!simple.StartsWith("NAudio")) return null;
                return LoadCached(simple);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按文件名字节加载模组目录里的 dll, 带缓存(重复请求返回同一实例)</summary>
        private static Assembly LoadCached(string simpleName)
        {
            Assembly cached;
            if (byteLoaded.TryGetValue(simpleName, out cached)) return cached;

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "NetChat");
            string p = Path.Combine(dir, simpleName + ".dll");
            if (!File.Exists(p)) return null;

            Assembly asm = Assembly.Load(File.ReadAllBytes(p));
            byteLoaded[simpleName] = asm;
            return asm;
        }

        private static void Probe()
        {
            if (probed) return;
            probed = true;

            List<string> log = new List<string>();

            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "NetChat");
                log.Add("dir=" + dir);
                if (!Directory.Exists(dir)) { failReason = "找不到 " + dir; return; }

                // 候选: 门面 NAudio.dll 优先, 然后目录里其它 NAudio*.dll(哪个有 Mp3FileReader 用哪个)
                List<string> candidates = new List<string>();
                string facade = Path.Combine(dir, "NAudio.dll");
                if (File.Exists(facade)) candidates.Add(facade);
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "NAudio*.dll"))
                    {
                        if (!candidates.Contains(f)) candidates.Add(f);
                    }
                }
                catch { }
                log.Add("candidates=" + string.Join(",", candidates.ConvertAll(Path.GetFileName).ToArray()));

                if (candidates.Count == 0) { failReason = "目录里没有 NAudio.dll"; return; }

                if (!handlerAttached)
                {
                    handlerAttached = true;
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveNAudio;
                }

                Exception last = null;

                // 逐个候选找两个类型: Mp3FileReader 定义在门面里; WaveFormat 在 Core 里,
                // 门面的类型转发在字节加载下解析不了(.NET Framework 实测 TypeLoadException),
                // 所以必须"各找各的" —— 哪个程序集自己有就用哪个的
                foreach (string dll in candidates)
                {
                    Assembly asm;
                    try { asm = LoadCached(Path.GetFileNameWithoutExtension(dll)); }
                    catch (Exception e)
                    {
                        last = e;
                        log.Add(Path.GetFileName(dll) + ": load err " + (e.InnerException ?? e).Message);
                        continue;
                    }

                    if (asm == null) { log.Add(Path.GetFileName(dll) + ": load err 文件读不到"); continue; }

                    if (mp3ReaderType == null)
                    {
                        Type r = FindType(asm, "NAudio.Wave.Mp3FileReader");
                        if (r != null) mp3ReaderType = r;
                    }

                    if (waveFormatType == null)
                    {
                        Type f = FindType(asm, "NAudio.Wave.WaveFormat");
                        if (f != null) waveFormatType = f;
                    }

                    log.Add(Path.GetFileName(dll) + ": reader=" + (mp3ReaderType != null)
                        + " fmt=" + (waveFormatType != null));

                    if (mp3ReaderType != null && waveFormatType != null)
                    {
                        failReason = null;
                        return;
                    }
                }

                failReason = last != null
                    ? "加载失败: " + (last.InnerException ?? last).Message
                    : "类型缺失(版本不对?)";
            }
            catch (Exception e)
            {
                failReason = "加载失败: " + (e.InnerException ?? e).Message;
                log.Add("probe err: " + (e.InnerException ?? e).Message);
            }
            finally
            {
                // 探测日志落盘, 现场排障用(每次启动覆盖)
                try
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "NetChat");
                    if (Directory.Exists(dir))
                    {
                        File.WriteAllLines(Path.Combine(dir, "ext_probe_log.txt"), log.ToArray());
                    }
                }
                catch { }
            }
        }

        /// <summary>取类型: GetType 失败(转发目标加载不了)再用 GetTypes 兜底</summary>
        private static Type FindType(Assembly asm, string fullName)
        {
            try
            {
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch { }

            try
            {
                foreach (Type t in asm.GetTypes())
                {
                    if (t != null && t.FullName == fullName) return t;
                }
            }
            catch (ReflectionTypeLoadException e)
            {
                if (e.Types != null)
                {
                    foreach (Type t in e.Types)
                    {
                        if (t != null && t.FullName == fullName) return t;
                    }
                }
            }
            catch { }

            return null;
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

                // 后面的反射全部按"运行时对象自己的类型"来, 不用探测阶段缓存的类型对象:
                // F9 重载等场景下进程里会同时存在新旧两份 NAudio 字节程序集,
                // JIT 绑定到的 Core 和探测时拿到的不一定是同一实例, 跨实例反射会抛
                // "对象与目标类型不匹配"。运行时自省天然同实例, 永远不会错配。
                Type readerType = reader.GetType();

                object fmt = readerType.GetProperty("WaveFormat").GetValue(reader, null);
                Type fmtType = fmt.GetType();

                sampleRate = (int)fmtType.GetProperty("SampleRate").GetValue(fmt, null);
                channels = (int)fmtType.GetProperty("Channels").GetValue(fmt, null);

                MethodInfo read = readerType.GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) });
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
                Presence.VerLog("扩展解码失败 " + Path.GetFileName(path) + ": " + error);
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
