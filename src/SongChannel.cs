using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Terraria;

namespace NetChat
{
    /// <summary>曲库里的歌(收到的歌落盘后的一条记录)</summary>
    internal sealed class SongItem
    {
        public string Name;
        public string Path;
        public int Seconds;
        public int Bytes;
        public string Ext;
        public DateTime Added;
    }

    /// <summary>收到的一条点歌邀请, 等玩家在点歌面板里接收或拒绝</summary>
    internal sealed class SongOffer
    {
        public string MsgId;
        public int SenderSlot;
        public string SenderName;
        public string Name;
        public string Ext;
        public int Bytes;
        public int Seconds;
        public DateTime At;
    }

    internal sealed class RecvSong
    {
        public string MsgId;
        public string Name;
        public string Ext;
        public int Count;
        public int TotalBytes;
        public uint Crc;
        public string[] Parts;
        public int Got;
        public DateTime Last;

        /// <summary>上次发 SNACK(催对方补传缺片)的时间</summary>
        public DateTime LastNack;

        /// <summary>已经催了几次</summary>
        public int Nacks;
    }

    /// <summary>
    /// 点歌: 音频文件互传 + 接收确认 + 恶意音频检测 + 本地曲库。
    ///
    /// 流程(先问后传, 音频比图片大得多, 不能像图片那样直接怼过来):
    ///   发端  选文件 -> 校验(格式/大小/时长/码率/削波率) -> 发邀请 SRQ
    ///   收端  邀请进点歌面板, 玩家点 [接收] -> 回 SRQOK, 点 [拒绝] -> 回 SRQNO
    ///   发端  收到 SRQOK -> 分片走 NetComm 21 号通道发数据; 45 秒没等到回执就放弃
    ///   收端  攒片 -> CRC -> 复检(削波率) -> 落盘进曲库 -> 聊天室提示
    ///
    /// 恶意检测(收发两端都做):
    ///   格式白名单 mp3/wav, 大小上限(MP3 5MB / WAV 8MB), 时长上限 10 分钟
    ///   WAV: 头参数越界拒收; 削波率(满振幅样本占比)超标 = "音频密度过大" 拒收
    ///   MP3: 码率越界拒收; 有扩展包时解码后同样做削波率; 没有就靠"先问后传"的人工确认兜底
    /// </summary>
    internal static class SongChannel
    {
        public const int Channel = 21;

        public const string OfferMagic = "SRQ";
        public const string OkMagic = "SRQOK";
        public const string NoMagic = "SRQNO";
        public const string SliceMagic = "NS1";

        /// <summary>接收端催补传: SNACK|msgId|已有片位图</summary>
        public const string SnackMagic = "SNACK";

        /// <summary>接收端确认收齐: SDONE|msgId</summary>
        public const string DoneMagic = "SDONE";

        /// <summary>接收端彻底放弃: SABRT|msgId</summary>
        public const string AbortMagic = "SABRT";

        public const int MaxMp3Bytes = 5 * 1024 * 1024;
        public const int MaxWavBytes = 8 * 1024 * 1024;
        public const int MaxSeconds = 600;

        private const int MaxOffers = 4;
        private const int OfferTimeoutSeconds = 45;

        /// <summary>数据还在 NetComm 队列里的最长等待(队列异常不清空时兜底放行)</summary>
        private const int SendWatchdogSeconds = 150;

        /// <summary>数据发完(队列清空)后等多久的 SDONE 确认; 期间靠 SNACK 补传缺片</summary>
        private const int ConfirmTimeoutSeconds = 300;

        /// <summary>最多补传几轮(防永远补不完)</summary>
        private const int MaxResendRounds = 30;

        /// <summary>接收端: 数据静默多久开始催补传(SNACK)</summary>
        private const int NackAfterSeconds = 8;

        /// <summary>接收端: 两次 SNACK 的最小间隔</summary>
        private const int NackGapSeconds = 8;

        /// <summary>接收端: 一首歌最多催几次</summary>
        private const int NackMaxRounds = 20;

        /// <summary>接收端: 催了补传也补不上来, 这么久之后才认定传输彻底中断</summary>
        private const int RecvHardTimeoutSeconds = 120;

        private const int MaxRecvJobs = 2;
        private const float ClipLimit = 0.55f;

        //------------------------------ 发送端 ------------------------------

        private sealed class PendingSend
        {
            public string MsgId;
            public string Name;
            public string Ext;
            public byte[] Data;
            public uint Crc;
            public int Count;
            public DateTime OfferAt;
            public DateTime DataStart;
            public int TotalQueued;
            public bool GotOk;

            /// <summary>NetComm 待发队列已清空(不代表对方收齐了)</summary>
            public bool Drained;

            /// <summary>最近一次发送活动(开始/补传), 判对方是否还在线用</summary>
            public DateTime LastActivity;

            /// <summary>已经按对方 SNACK 补传了几轮</summary>
            public int ResendRounds;

            /// <summary>对方已确认收齐(SDONE)</summary>
            public bool DoneConfirmed;
        }

        private static PendingSend outgoing;

        /// <summary>正在发歌(邀请阶段或传数据阶段都算)</summary>
        public static bool IsSending
        {
            get { return outgoing != null; }
        }

        public static string SendStatus = "就绪";

        //------------------------------ 接收端 ------------------------------

        /// <summary>等玩家处理的点歌邀请</summary>
        public static readonly List<SongOffer> Offers = new List<SongOffer>();

        /// <summary>已经接收、正在下载的歌</summary>
        public static RecvSong Receiving;

        public static bool IsReceiving
        {
            get { return Receiving != null; }
        }

        public static string RecvStatus = "等待接收";

        private static readonly HashSet<string> accepted = new HashSet<string>();
        private static readonly List<SongItem> library = new List<SongItem>();
        private static bool libraryScanned;

        /// <summary>收到但没认领的音频分片计数(诊断用)</summary>
        private static int slDropped;

        //------------------------------ 发送 ------------------------------

        /// <summary>发起点歌: 校验文件, 合法就发邀请。返回 null = 成功, 其他 = 失败原因</summary>
        public static string BeginSend(string path)
        {
            if (Main.netMode != 1) return "只在联机客户端可用, 请先进服务器";
            if (!NetCommBridge.Ready) return "NetComm 未就绪";
            if (outgoing != null) return "上一首还在处理中";
            if (ImageTransfer.Sending != null && !ImageTransfer.Sending.Done) return "图片还在发送, 等它传完";
            if (IsReceiving) return "还在接收别人的歌, 等它传完";

            if (string.IsNullOrEmpty(path)) return "路径为空";

            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext != ".mp3" && ext != ".wav") return "只支持 MP3 和 WAV";

            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception e) { return "读文件失败: " + e.Message; }

            int cap = ext == ".mp3" ? MaxMp3Bytes : MaxWavBytes;
            if (data.Length > cap) return "文件 " + ImageTransfer.Format(data.Length) + ", 超过上限 " + ImageTransfer.Format(cap);

            AudioInfo info;
            if (ext == ".wav")
            {
                info = AudioProbe.Wav(data);
                if (info == null) return "不是合法的 PCM WAV 文件";
                if (info.SampleRate < 8000 || info.SampleRate > 48000) return "采样率 " + info.SampleRate + " 越界";
                if (info.AvgKbps < 32 || info.AvgKbps > 1600) return "码率越界";
                if (info.ClipRatio >= ClipLimit)
                    return "波形密度异常(满振幅占比 " + (int)(info.ClipRatio * 100) + "%), 疑似恶意音频, 拒发";
            }
            else
            {
                info = AudioProbe.Mp3(data);
                if (info == null) return "解析不出合法的 MP3 帧";
                if (info.AvgKbps < 8 || info.AvgKbps > 320) return "码率 " + info.AvgKbps + "kbps 越界";
            }

            if (info.Seconds < 1 || info.Seconds > MaxSeconds)
                return "时长 " + info.Seconds + " 秒, 超出 1~" + MaxSeconds + " 秒";

            // 有扩展包时 MP3 也做削波分析(解码后才有波形)
            if (ext == ".mp3" && MediaExt.Available)
            {
                int sr, ch;
                byte[] pcm;
                string err;
                if (MediaExt.DecodeMp3(path, out sr, out ch, out pcm, out err))
                {
                    float clip = MediaExt.ClipRatio(pcm);
                    if (clip >= ClipLimit)
                        return "波形密度异常(满振幅占比 " + (int)(clip * 100) + "%), 疑似恶意音频, 拒发";
                }
            }

            string msgId = ImageTransfer.NextMsgId();
            string name = ImageProtocol.SanitizeName(Path.GetFileName(path));

            string offer = OfferMagic + "|" + msgId + "|" + name + "|" + ext.TrimStart('.')
                + "|" + data.Length + "|" + info.Seconds;

            if (!NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(offer)))
                return "邀请没发出去, " + (NetCommBridge.LastError ?? "NetComm 拒绝");

            outgoing = new PendingSend
            {
                MsgId = msgId,
                Name = name,
                Ext = ext.TrimStart('.'),
                Data = data,
                OfferAt = DateTime.Now
            };

            SendStatus = "邀请已发出, 等对方确认: " + name;
            return null;
        }

        /// <summary>对方点了接收: 开始传数据</summary>
        private static void StartData(PendingSend p)
        {
            int count = (p.Data.Length + ImageProtocol.ChunkBytes - 1) / ImageProtocol.ChunkBytes;
            p.Count = count;
            p.Crc = ImageProtocol.Crc32(p.Data);

            int failed = 0;
            for (int i = 0; i < count; i++)
            {
                byte[] msg = BuildSliceMessage(p, i);
                if (msg == null || !NetCommBridge.Send(Channel, msg)) failed++;
            }

            if (failed > 0)
            {
                ChatRoom.AddSystem("点歌 " + p.Name + " 有 " + failed + "/" + count + " 片没排进队列, 作废");
                outgoing = null;
                SendStatus = "发送失败";
                return;
            }

            p.GotOk = true;
            p.DataStart = DateTime.Now;
            p.LastActivity = DateTime.Now;
            p.TotalQueued = NetCommBridge.PendingSlices();
            SendStatus = "传输中 0%  " + p.Name;
            Presence.VerLog("开始传数据 " + p.Name + " msgs=" + count
                + " 队列=" + p.TotalQueued + " (netcomm=" + NetCommBridge.CommStatus() + ")");
        }

        /// <summary>按序号组装一条 NS1 音频分片消息; 序号越界返回 null</summary>
        private static byte[] BuildSliceMessage(PendingSend p, int index)
        {
            int chunk = ImageProtocol.ChunkBytes;
            int off = index * chunk;
            if (off < 0 || off >= p.Data.Length) return null;

            int len = Math.Min(chunk, p.Data.Length - off);
            byte[] part = new byte[len];
            Buffer.BlockCopy(p.Data, off, part, 0, len);

            string msg = SliceMagic + "|" + p.MsgId + "|" + index + "|" + p.Count
                + "|" + p.Data.Length + "|" + p.Crc + "|" + p.Name
                + "|" + Convert.ToBase64String(part);

            return Encoding.UTF8.GetBytes(msg);
        }

        //------------------------------ 接收 ------------------------------

        /// <summary>收到点歌邀请</summary>
        private static void OnOffer(string[] parts, int senderSlot)
        {
            if (parts.Length != 6) return;

            string msgId = parts[1];
            string name = parts[2];
            string ext = parts[3].ToLowerInvariant();
            int bytes, seconds;

            if (!int.TryParse(parts[4], out bytes) || !int.TryParse(parts[5], out seconds)) return;
            if (msgId.Length == 0 || msgId.Length > 16) return;
            if (ext != "mp3" && ext != "wav") { Refuse(msgId); return; }

            int cap = ext == "mp3" ? MaxMp3Bytes : MaxWavBytes;
            string bad = null;
            if (bytes <= 0 || bytes > cap) bad = "大小超限";
            else if (seconds < 1 || seconds > MaxSeconds) bad = "时长超限";
            else if (ext == "mp3" && bytes * 8 / Math.Max(1, seconds) > 320000) bad = "码率越界";
            else if (ext == "wav" && bytes * 8 / Math.Max(1, seconds) > 1600000) bad = "码率越界";
            else if (bytes * 8 / Math.Max(1, seconds) < 8000) bad = "码率越界";

            if (bad != null)
            {
                Refuse(msgId);
                ChatRoom.AddSystem("拒绝了一个异常音频(" + bad + "): " + name);
                return;
            }

            if (Offers.Count >= MaxOffers) Offers.RemoveAt(0);

            Offers.Add(new SongOffer
            {
                MsgId = msgId,
                SenderSlot = senderSlot,
                SenderName = NameForSlot(senderSlot),
                Name = name,
                Ext = ext,
                Bytes = bytes,
                Seconds = seconds,
                At = DateTime.Now
            });

            ChatRoom.AddSystem(NameForSlot(senderSlot) + " 想给你点歌《" + name + "》("
                + ImageTransfer.Format(bytes) + ", " + (seconds / 60) + ":" + (seconds % 60).ToString("00")
                + ") — 到点歌面板里接收");
            SoundPing();
        }

        /// <summary>拒绝一条没通过校验的邀请</summary>
        private static void Refuse(string msgId)
        {
            try { NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(NoMagic + "|" + msgId)); }
            catch { }
        }

        public static void Accept(string msgId)
        {
            SongOffer offer = FindOffer(msgId);
            if (offer == null) return;

            if (IsReceiving)
            {
                ChatRoom.AddSystem("已经有歌在传了, 先等这首完成");
                return;
            }

            Offers.Remove(offer);
            accepted.Add(msgId);

            RecvSong job = new RecvSong
            {
                MsgId = msgId,
                Name = offer.Name,
                Ext = offer.Ext,
                TotalBytes = offer.Bytes,
                Parts = new string[(offer.Bytes + ImageProtocol.ChunkBytes - 1) / ImageProtocol.ChunkBytes],
                Last = DateTime.Now
            };
            job.Count = job.Parts.Length;

            Receiving = job;
            RecvStatus = "接收中 0%  " + job.Name;

            bool okSent = NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(OkMagic + "|" + msgId));
            if (!okSent) ChatRoom.AddSystem("接受回执没发出去: " + (NetCommBridge.LastError ?? "NetComm 拒绝"));
            Presence.VerLog("接受点歌 " + offer.Name + " msgId=" + msgId + " msgs=" + job.Count
                + " 回执sent=" + okSent);
        }

        public static void Decline(string msgId)
        {
            SongOffer offer = FindOffer(msgId);
            if (offer != null) Offers.Remove(offer);

            accepted.Remove(msgId);
            Refuse(msgId);
        }

        /// <summary>收到一条音频分片; 只有接收过的 msgId 才认</summary>
        private static void OnSlice(string[] parts)
        {
            if (parts.Length != 8) return;
            if (parts[0] != SliceMagic) return;

            if (Receiving == null || parts[1] != Receiving.MsgId)
            {
                slDropped++;
                return;
            }

            int index, count, total;
            uint crc;
            if (!int.TryParse(parts[2], out index)) { slDropped++; return; }
            if (!int.TryParse(parts[3], out count)) { slDropped++; return; }
            if (!int.TryParse(parts[4], out total)) { slDropped++; return; }
            if (!uint.TryParse(parts[5], out crc)) { slDropped++; return; }
            if (index < 0 || index >= Receiving.Count) { slDropped++; return; }
            if (count != Receiving.Count || total != Receiving.TotalBytes) { slDropped++; return; }

            RecvSong job = Receiving;
            job.Last = DateTime.Now;

            if (job.Got == 0) Presence.VerLog("首片到达 " + job.Name + " idx=" + index);

            // CRC 以第一片报的为准, 后面片的对不上直接丢
            if (job.Crc == 0) job.Crc = crc;
            else if (job.Crc != crc) return;

            if (job.Parts[index] == null)
            {
                job.Parts[index] = parts[7];
                job.Got++;
            }

            if (job.Got >= job.Count) Complete(job);
            else RecvStatus = "接收中 " + (int)(job.Got * 100f / job.Count) + "%  " + job.Name;
        }

        private static void Complete(RecvSong job)
        {
            try
            {
                StringBuilder sb = new StringBuilder(job.TotalBytes * 2);
                for (int i = 0; i < job.Count; i++) sb.Append(job.Parts[i]);

                byte[] data = Convert.FromBase64String(sb.ToString());

                if (data.Length != job.TotalBytes)
                {
                    FailRecv("长度不符 " + data.Length + "/" + job.TotalBytes);
                    return;
                }

                if (ImageProtocol.Crc32(data) != job.Crc)
                {
                    FailRecv("CRC 校验不过");
                    return;
                }

                // 复检: 拿到手的字节再验一遍, 邀请里报的参数和实际数据对不上就扔
                AudioInfo info = job.Ext == "wav" ? AudioProbe.Wav(data) : AudioProbe.Mp3(data);
                if (info == null)
                {
                    FailRecv("内容不是合法的 " + job.Ext.ToUpperInvariant());
                    return;
                }

                if (info.Seconds < 1 || info.Seconds > MaxSeconds)
                {
                    FailRecv("实际时长 " + info.Seconds + " 秒越界");
                    return;
                }

                if (info.ClipRatio >= 0)
                {
                    if (info.ClipRatio >= ClipLimit)
                    {
                        FailRecv("波形密度异常(满振幅占比 " + (int)(info.ClipRatio * 100) + "%), 已丢弃");
                        return;
                    }
                }
                else if (job.Ext == "mp3" && MediaExt.Available)
                {
                    // WAV 没做出来削波率才会走这; 扩展包在就把 MP3 也验一遍
                    string err;
                    int sr, ch;
                    byte[] pcm;
                    string tmp = Path.Combine(EnsureMusicFolder() ?? Path.GetTempPath(), "ncaudit.tmp");
                    if (EnsureMusicFolder() != null)
                    {
                        File.WriteAllBytes(tmp, data);
                        if (MediaExt.DecodeMp3(tmp, out sr, out ch, out pcm, out err))
                        {
                            float clip = MediaExt.ClipRatio(pcm);
                            if (clip >= ClipLimit)
                            {
                                try { File.Delete(tmp); } catch { }
                                FailRecv("波形密度异常(满振幅占比 " + (int)(clip * 100) + "%), 已丢弃");
                                return;
                            }
                        }
                    }
                }

                string dir = EnsureMusicFolder();
                if (dir == null) { FailRecv("曲库目录创建失败"); return; }

                string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + job.Name);
                File.WriteAllBytes(file, data);

                Library.Insert(0, new SongItem
                {
                    Name = job.Name,
                    Path = file,
                    Seconds = info.Seconds,
                    Bytes = data.Length,
                    Ext = job.Ext,
                    Added = DateTime.Now
                });

                Receiving = null;
                accepted.Remove(job.MsgId);
                RecvStatus = "已收到 " + job.Name;
                ChatRoom.AddSystem("收到点歌《" + job.Name + "》(" + (info.Seconds / 60) + ":"
                    + (info.Seconds % 60).ToString("00") + "), 已进曲库, 打开点歌面板播放");
                SoundPing();

                // 通知对方"收齐了"; 这条确认可能丢, Update 里会补发几次
                try { NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(DoneMagic + "|" + job.MsgId)); } catch { }
                doneAcks.Add(new DoneAck { MsgId = job.MsgId, Sent = DateTime.Now });
                Presence.VerLog("收齐并发送 SDONE " + job.Name + " " + job.MsgId);
            }
            catch (Exception e)
            {
                FailRecv(e.Message);
            }
        }

        private static void FailRecv(string why)
        {
            if (Receiving != null)
            {
                accepted.Remove(Receiving.MsgId);
                SendAbort(Receiving.MsgId);
                Presence.VerLog("接收失败 " + Receiving.Name + ": " + why
                    + " got=" + Receiving.Got + "/" + Receiving.Count);
            }
            Receiving = null;
            RecvStatus = "接收失败: " + why;
            ChatRoom.AddSystem("点歌接收失败: " + why);
        }

        /// <summary>告诉对方别再传了(补传也救不回来/彻底放弃)</summary>
        private static void SendAbort(string msgId)
        {
            try { NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(AbortMagic + "|" + msgId)); }
            catch { }
        }

        /// <summary>催对方补传缺片: 位图第 i 位 = 已收到第 i 条消息</summary>
        private static void SendNack(RecvSong job)
        {
            byte[] mask = new byte[(job.Count + 7) / 8];
            for (int i = 0; i < job.Count; i++)
            {
                if (job.Parts[i] != null) mask[i >> 3] |= (byte)(1 << (i & 7));
            }

            bool ok = NetCommBridge.Send(Channel,
                Encoding.UTF8.GetBytes(SnackMagic + "|" + job.MsgId + "|" + Convert.ToBase64String(mask)));

            Presence.VerLog("SNACK -> 缺 " + (job.Count - job.Got) + " 片 sent=" + ok
                + " (第 " + job.Nacks + " 次催)");
        }

        //------------------------------ 回执(发给别人的) ------------------------------

        /// <summary>对方接受了你点的歌</summary>
        private static void OnOk(string msgId)
        {
            if (outgoing == null || outgoing.MsgId != msgId || outgoing.GotOk) return;
            StartData(outgoing);
        }

        /// <summary>对方拒绝了你点的歌</summary>
        private static void OnNo(string msgId)
        {
            if (outgoing == null || outgoing.MsgId != msgId) return;

            ChatRoom.AddSystem("对方拒绝了你点的歌《" + outgoing.Name + "》");
            outgoing = null;
            SendStatus = "对方拒绝";
        }

        //------------------------------ 曲库 ------------------------------

        /// <summary>曲库(收到的歌); 第一次访问时扫一遍目录, 把以前收过的也捡回来</summary>
        public static List<SongItem> Library
        {
            get
            {
                if (!libraryScanned) ScanLibrary();
                return library;
            }
        }

        private static void ScanLibrary()
        {
            libraryScanned = true;

            string dir = EnsureMusicFolder();
            if (dir == null) return;

            try
            {
                List<SongItem> found = new List<SongItem>();

                foreach (string file in Directory.GetFiles(dir))
                {
                    string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
                    if (ext != ".mp3" && ext != ".wav") continue;

                    AudioInfo info = ext == ".wav" ? AudioProbe.Wav(File.ReadAllBytes(file)) : AudioProbe.Mp3(File.ReadAllBytes(file));
                    if (info == null) continue;

                    found.Add(new SongItem
                    {
                        Name = Path.GetFileName(file),
                        Path = file,
                        Seconds = info.Seconds,
                        Bytes = (int)new FileInfo(file).Length,
                        Ext = ext.TrimStart('.'),
                        Added = File.GetLastWriteTime(file)
                    });
                }

                found.Sort((a, b) => b.Added.CompareTo(a.Added));
                library.AddRange(found);
            }
            catch { }
        }

        public static void Delete(SongItem song)
        {
            if (song == null) return;

            try { File.Delete(song.Path); } catch { }
            Library.Remove(song);

            if (SongPlayer.Current == song) SongPlayer.Stop();
        }

        /// <summary>曲库目录: 我的文档\My Games\Terraria\NetChat\music</summary>
        public static string MusicFolder
        {
            get { return Path.Combine(Main.SavePath, "NetChat", "music"); }
        }

        public static string EnsureMusicFolder()
        {
            try
            {
                Directory.CreateDirectory(MusicFolder);
                return MusicFolder;
            }
            catch { return null; }
        }

        //------------------------------ 每帧 / 杂项 ------------------------------

        /// <summary>Demo.UpdatePostfix 每帧调: 推进发送进度, 清超时的邀请, 驱动补传与确认</summary>
        public static void Update()
        {
            DateTime now = DateTime.Now;

            // 发送端: 邀请超时
            PendingSend p = outgoing;

            if (p != null && !p.GotOk)
            {
                if ((now - p.OfferAt).TotalSeconds > OfferTimeoutSeconds)
                {
                    ChatRoom.AddSystem("点歌邀请 45 秒没等到回执, 已放弃《" + p.Name + "》");
                    outgoing = null;
                    SendStatus = "邀请超时";
                }
                else
                {
                    SendStatus = "等待对方确认 " + (int)(OfferTimeoutSeconds - (now - p.OfferAt).TotalSeconds) + "s  " + p.Name;
                }
            }

            // 发送端: 数据阶段 —— 队列清空不等于对方收齐, 以对方的 SDONE 为准
            if (p != null && p.GotOk)
            {
                int pending = NetCommBridge.PendingSlices();

                if (!p.Drained && pending <= 0)
                {
                    p.Drained = true;
                    Presence.VerLog("发送队列已清空 " + p.Name + ", 等对方确认 (netcomm=" + NetCommBridge.CommStatus() + ")");
                }

                bool finished = p.DoneConfirmed
                    || p.ResendRounds >= MaxResendRounds
                    || (now - p.LastActivity).TotalSeconds > ConfirmTimeoutSeconds;

                bool stuck = !p.DoneConfirmed && finished;

                if (finished)
                {
                    outgoing = null;

                    if (p.DoneConfirmed)
                    {
                        SendStatus = "已发完 " + p.Name;
                    }
                    else
                    {
                        SendStatus = "发送未完成, 已放弃 " + p.Name;
                        ChatRoom.AddSystem("点歌《" + p.Name + "》对方迟迟没确认收齐, 已放弃(" 
                            + (p.ResendRounds >= MaxResendRounds ? "补传轮次用尽" : "确认超时") + ")");
                    }
                    Presence.VerLog("发送结束 " + p.Name + " confirmed=" + p.DoneConfirmed + " rounds=" + p.ResendRounds);
                }
                else
                {
                    float prog = p.TotalQueued > 0 ? 1f - (float)Math.Min(pending, p.TotalQueued) / p.TotalQueued : 0f;
                    SendStatus = p.Drained
                        ? "等待对方确认收齐 " + p.Name
                        : "传输中 " + (int)(prog * 100f) + "%  " + p.Name;
                }
            }

            // 发送端: 数据还在队列里卡死(NetComm 异常)兜底
            if (p != null && p.GotOk && !p.Drained
                && (now - p.DataStart).TotalSeconds > SendWatchdogSeconds && NetCommBridge.PendingSlices() > 0)
            {
                NetCommBridge.ClearNetQueue();
                p.Drained = true;
                Presence.VerLog("发送队列 150 秒没清空, 强制放行 " + p.Name);
            }

            // 接收端: 邀请过期(2 分钟没人理就撤下)
            for (int i = Offers.Count - 1; i >= 0; i--)
            {
                if ((now - Offers[i].At).TotalMinutes > 2) Offers.RemoveAt(i);
            }

            // 接收端: 缺片催补传 / 彻底中断判定
            if (Receiving != null)
            {
                RecvSong job = Receiving;
                double idle = (now - job.Last).TotalSeconds;

                if (job.Got == 0 && idle > 30)
                {
                    FailRecv("对方一直没开始传输");
                }
                else if (job.Got > 0 && job.Got < job.Count)
                {
                    if (idle >= NackAfterSeconds && job.Nacks < NackMaxRounds
                        && (now - job.LastNack).TotalSeconds >= NackGapSeconds)
                    {
                        SendNack(job);
                        job.LastNack = now;
                        job.Nacks++;
                    }

                    if (idle > RecvHardTimeoutSeconds)
                    {
                        FailRecv("补传后还是没等到剩余数据(对方可能退了)");
                    }
                }
            }

            // 发送端: 已收齐的确认 SDONE 防丢, 补发几次
            for (int i = doneAcks.Count - 1; i >= 0; i--)
            {
                DoneAck d = doneAcks[i];
                if (d.Times >= 3 || d.Sent.AddSeconds(6) < now) { doneAcks.RemoveAt(i); continue; }

                if ((now - d.Sent).TotalSeconds >= 2)
                {
                    try { NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(DoneMagic + "|" + d.MsgId)); } catch { }
                    d.Times++;
                    d.Sent = now;
                }
            }
        }

        /// <summary>SDONE 补发条目: 接收端收齐后通知发送端, 防这条确认本身丢了</summary>
        private sealed class DoneAck
        {
            public string MsgId;
            public DateTime Sent;
            public int Times;
        }

        private static readonly List<DoneAck> doneAcks = new List<DoneAck>();

        /// <summary>收到的一条 NetComm 点歌消息(由 Demo 按前缀分发)</summary>
        public static void OnMessage(string text, int senderSlot)
        {
            if (string.IsNullOrEmpty(text)) return;

            string[] parts = text.Split('|');
            if (parts.Length < 2) return;

            if (parts[0] == OfferMagic) OnOffer(parts, senderSlot);
            else if (parts[0] == OkMagic) OnOk(parts[1]);
            else if (parts[0] == NoMagic) OnNo(parts[1]);
            else if (parts[0] == SnackMagic && parts.Length >= 3) OnNack(parts[1], parts[2]);
            else if (parts[0] == DoneMagic) OnDone(parts[1]);
            else if (parts[0] == AbortMagic) OnAbort(parts[1]);
        }

        /// <summary>对方催补传: 位图里没置位的片重新入队</summary>
        private static void OnNack(string msgId, string maskB64)
        {
            if (outgoing == null || outgoing.MsgId != msgId || !outgoing.GotOk) return;

            byte[] mask;
            try { mask = Convert.FromBase64String(maskB64); }
            catch { return; }

            int resent = 0;
            for (int i = 0; i < outgoing.Count; i++)
            {
                bool has = (i >> 3) < mask.Length && (mask[i >> 3] & (1 << (i & 7))) != 0;
                if (has) continue;

                byte[] msg = BuildSliceMessage(outgoing, i);
                if (msg != null && NetCommBridge.Send(Channel, msg)) resent++;
            }

            outgoing.ResendRounds++;
            outgoing.LastActivity = DateTime.Now;
            outgoing.Drained = false;   // 又有片进了队列, 重新等队列清空
            Presence.VerLog("SNACK: 补传 " + resent + "/" + (outgoing.Count - CountMask(mask, outgoing.Count))
                + " 条消息(第 " + outgoing.ResendRounds + " 轮)");
        }

        private static int CountMask(byte[] mask, int count)
        {
            int n = 0;
            for (int i = 0; i < count && (i >> 3) < mask.Length; i++)
            {
                if ((mask[i >> 3] & (1 << (i & 7))) != 0) n++;
            }
            return n;
        }

        /// <summary>对方确认收齐: 这才算发送完成</summary>
        private static void OnDone(string msgId)
        {
            if (outgoing == null || outgoing.MsgId != msgId) return;

            outgoing.DoneConfirmed = true;
            Presence.VerLog("收到 SDONE " + msgId);
        }

        /// <summary>对方彻底放弃了接收: 立刻止损</summary>
        private static void OnAbort(string msgId)
        {
            if (outgoing == null || outgoing.MsgId != msgId) return;

            ChatRoom.AddSystem("对方中断了接收, 点歌《" + outgoing.Name + "》已停止");
            Presence.VerLog("收到 SABRT " + msgId);
            outgoing = null;
            SendStatus = "对方中断";
        }

        /// <summary>收到的一条音频分片</summary>
        public static void OnSliceMessage(string text)
        {
            // 限 8 段: 歌名里万一带竖线, base64 尾段也不会被切碎
            OnSlice(text.Split(new[] { '|' }, 8));
        }

        private static SongOffer FindOffer(string msgId)
        {
            foreach (SongOffer o in Offers)
            {
                if (o.MsgId == msgId) return o;
            }
            return null;
        }

        private static string NameForSlot(int slot)
        {
            foreach (Peer peer in Presence.Peers)
            {
                if (peer.Slot == slot) return Presence.DisplayName(peer);
            }
            return "(玩家" + slot + ")";
        }

        /// <summary>点歌有动静时的提示音(和消息提示音共用)</summary>
        private static void SoundPing()
        {
            try
            {
                if (!Main.dedServ) Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.Chat);
            }
            catch { }
        }

        /// <summary>进世界清状态</summary>
        public static void Reset()
        {
            outgoing = null;
            SendStatus = "就绪";
            Offers.Clear();
            accepted.Clear();
            Receiving = null;
            RecvStatus = "等待接收";
            doneAcks.Clear();
            slDropped = 0;
            SongPlayer.Stop();
        }
    }
}
