using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace NetChat
{
    internal sealed class SendJob
    {
        public string Name;
        public string MsgId;
        public int TotalBytes;
        public int ChunkCount;
        public int WireBytes;
        public int TotalQueued = -1;
        public DateTime Start;
        public bool Done;

        /// <summary>编码好的分片消息, 排到自己上场时才真正进 NetComm 队列</summary>
        public List<byte[]> Messages;

        /// <summary>头像分片, 发完的提示和图片分开说</summary>
        public bool IsAvatar;
    }

    internal sealed class RecvJob
    {
        public string MsgId;
        public string Name;
        public int Count;
        public int TotalBytes;
        public uint Crc;
        public string[] Parts;
        public int Got;
        public DateTime Last;
    }

    /// <summary>
    /// 发送与接收的全部状态。发送: 图片 -> 重编码 -> 分片 -> Base64 -> NetComm;
    /// 接收: NetComm -> 分片重组 -> 解码 -> 显示 + 落盘。
    ///
    /// 收包不在这里轮询: Demo 每帧把 NetComm 队列取空后按前缀分发过来, 免得两个地方抢队列。
    /// </summary>
    internal static class ImageTransfer
    {
        /// <summary>重编码后的图片字节上限, 超过就拒绝, 免得传到天荒地老</summary>
        public const int MaxImageBytes = 4 * 1024 * 1024;

        private const int MaxRecvJobs = 8;
        private const int RecvJobTimeoutSeconds = 30;

        /// <summary>发送队列深度: 一次最多点 5 张, 发完一张自动上下一张</summary>
        private const int MaxSendQueue = 5;

        public static SendJob Sending;
        public static RecvJob Receiving;

        /// <summary>排着队等上场的图片, 先点先发</summary>
        private static readonly Queue<SendJob> sendQueue = new Queue<SendJob>();

        public static Texture2D Preview;
        public static string PreviewName = "";

        public static string SendStatus = "就绪";
        public static string RecvStatus = "等待接收";
        public static string LastInfo = "";
        public static string LastError = "";

        private static readonly Dictionary<string, RecvJob> jobs = new Dictionary<string, RecvJob>();
        private static int msgCounter;

        /// <summary>图片还有没发完的(正在发或排队里还有): 点歌和更新推送必须等它,
        /// 不然同发送者的两条大消息在 NetComm 重组器里交织, 两边都坏</summary>
        public static bool IsBusy
        {
            get { return sendQueue.Count > 0 || (Sending != null && !Sending.Done); }
        }

        //------------------------------ 发送 ------------------------------

        /// <summary>能不能再排一张; 不行就把原因写进 LastError</summary>
        private static bool CanSend()
        {
            if (sendQueue.Count >= MaxSendQueue)
            {
                LastError = "发送队列已满(" + MaxSendQueue + " 张), 等前面发完";
                return false;
            }

            if (!NetCommBridge.Ready)
            {
                LastError = NetCommBridge.LastError ?? "NetComm 未就绪";
                return false;
            }

            if (Main.netMode != 1)
            {
                LastError = "只在联机客户端可用, 请先进服务器";
                return false;
            }

            // 点歌和图片共用 NetComm 的待发队列, 同时发会把两边的进度条都搅乱
            if (SongChannel.IsSending)
            {
                LastError = "歌曲还在发送, 等它传完再发图";
                return false;
            }

            return true;
        }

        public static bool BeginSend(string path)
        {
            if (!CanSend()) return false;

            byte[] data;
            string name;
            string info;
            string err;
            if (!ImageCodec.TryPrepare(path, ImageSetting.MaxSide, ImageSetting.Quality, out data, out name, out info, out err))
            {
                LastError = "读取图片失败: " + err;
                return false;
            }

            return SendPrepared(data, name, info, false);
        }

        /// <summary>发头像: 数据已经处理成正方形, 文件名里带着发送方的用户名</summary>
        public static bool BeginSendAvatar(byte[] data, string userName)
        {
            if (!CanSend()) return false;

            string info = Format(data == null ? 0 : data.Length) + " 方形头像";
            return SendPrepared(data, ImageProtocol.AvatarName(userName), info, true);
        }

        /// <summary>截图直发: 编码好的图片字节直接进发送队列, 不经过文件读取</summary>
        public static bool BeginSendRaw(byte[] data, string name, string info)
        {
            if (!CanSend()) return false;
            return SendPrepared(data, name, info, false);
        }

        /// <summary>编码好的图片字节 -> 分片 -> 排进 NetComm 队列; 图片和头像共用这一条</summary>
        private static bool SendPrepared(byte[] data, string name, string info, bool isAvatar)
        {
            if (data == null || data.Length == 0)
            {
                LastError = "要发的数据为空";
                return false;
            }

            if (data.Length > MaxImageBytes)
            {
                LastError = "重编码后 " + Format(data.Length) + ", 超过上限 " + Format(MaxImageBytes) + ", 请调小最大边长或质量";
                return false;
            }

            int chunk = ImageProtocol.ChunkBytes;
            int count = ImageProtocol.ChunkCount(data.Length);
            uint crc = ImageProtocol.Crc32(data);
            string msgId = NextMsgId();

            List<byte[]> messages = new List<byte[]>(count);
            int wire = 0;

            for (int i = 0; i < count; i++)
            {
                int off = i * chunk;
                int len = Math.Min(chunk, data.Length - off);

                byte[] part = new byte[len];
                Buffer.BlockCopy(data, off, part, 0, len);

                Slice slice = new Slice
                {
                    MsgId = msgId,
                    Index = i,
                    Count = count,
                    TotalBytes = data.Length,
                    Crc = crc,
                    Name = name,
                    Body = Convert.ToBase64String(part)
                };

                byte[] msg = Encoding.UTF8.GetBytes(ImageProtocol.Build(slice));
                messages.Add(msg);
                wire += msg.Length;
            }

            // 只编码入队, 排到这张上场时才真正进 NetComm 队列(多张图的分片不混在一个队列里)
            SendJob job = new SendJob
            {
                Name = name,
                MsgId = msgId,
                TotalBytes = data.Length,
                ChunkCount = count,
                WireBytes = wire,
                Messages = messages,
                Start = DateTime.Now,
                IsAvatar = isAvatar
            };

            sendQueue.Enqueue(job);
            // 上一张发完后 Sending 还挂着"已完成"的引用, 不能只判 null, 不然新图永远排队
            if (Sending == null || Sending.Done) StartNext();

            LastError = "";
            LastInfo = (job == Sending ? "已发出 " : "已排队 ") + name + " (" + info + ", " + Format(data.Length) + ", " + count + " 片)";

            // 发送者自己收不到(服务端转发会跳过发送者), 所以本地直接预览发出去的那张; 头像的预览归 Avatar 管
            if (!isAvatar && job == Sending) SetPreview(data, name);

            return true;
        }

        /// <summary>取队头的图上场: 分片排进 NetComm 队列开始发送</summary>
        private static void StartNext()
        {
            if (sendQueue.Count == 0) return;

            SendJob job = sendQueue.Dequeue();

            int failed = 0;
            foreach (byte[] msg in job.Messages)
            {
                if (!NetCommBridge.Send(ImageProtocol.Channel, msg)) failed++;
            }
            job.Messages = null;

            if (failed > 0)
            {
                job.Done = true;
                LastError = job.Name + " 有 " + failed + "/" + job.ChunkCount + " 片没排进发送队列, 这张作废";
                SendStatus = LastError;

                if (sendQueue.Count > 0) StartNext();
                return;
            }

            job.TotalQueued = NetCommBridge.PendingSlices();
            Sending = job;
            SendStatus = "发送中 0%  " + job.Name;
        }

        /// <summary>0~1 为进度, -1 表示算不出来</summary>
        public static float SendProgress()
        {
            SendJob job = Sending;
            if (job == null) return 0f;
            if (job.Done) return 1f;
            if (job.TotalQueued <= 0) return -1f;

            int now = NetCommBridge.PendingSlices();
            if (now < 0) return -1f;

            float p = 1f - (float)now / job.TotalQueued;
            if (p < 0f) return 0f;
            if (p > 1f) return 1f;
            return p;
        }

        /// <summary>每帧调, 推进发送进度并丢弃超时的接收任务</summary>
        public static void Update()
        {
            SendJob job = Sending;

            if (job != null && !job.Done)
            {
                int pending = NetCommBridge.PendingSlices();
                bool finished;
                bool stuck = false;

                if (pending >= 0)
                {
                    finished = pending <= 0;
                }
                else
                {
                    // 读不到队列长度就按"每帧 N 片、60 帧"估个时长兜底(N = 每帧片数设置)
                    double seconds = job.WireBytes / 15.0 / NetCommBridge.GetSlicesPerFrame() / 60.0 + 1.0;
                    finished = (DateTime.Now - job.Start).TotalSeconds > seconds;
                }

                // 看门狗: 排进队列的片 2 分钟还没发完, 强制放行。
                // F9 重载后如果 NetComm 绑到了旧程序集的死队列, pending 永远不减,
                // 没有这道闸 "上一张还在发送中" 会把后面所有发送全部挡死
                if (!finished && (DateTime.Now - job.Start).TotalSeconds > 120)
                {
                    finished = true;
                    stuck = true;
                }

                if (finished)
                {
                    job.Done = true;

                    if (stuck)
                    {
                        LastError = job.Name + " 长时间没发完, 已强制放行(很可能没传到, 请重发)";
                        SendStatus = "发送超时, 已放行";
                    }
                    else
                    {
                        SendStatus = (job.IsAvatar ? "已发完头像 " : "已发完 ") + job.Name;
                        Notify((job.IsAvatar ? "[头像] " : "[图片] ") + LastInfo);
                    }

                    if (job.IsAvatar) Avatar.OnSent();

                    // 这张完了, 队列里还有就接着发
                    if (sendQueue.Count > 0) StartNext();
                }
                else
                {
                    float p = SendProgress();
                    SendStatus = p < 0f
                        ? "发送中... " + job.Name
                        : "发送中 " + (int)(p * 100f) + "%  " + job.Name
                          + (sendQueue.Count > 0 ? "  (还有 " + sendQueue.Count + " 张排队)" : "");
                }
            }

            // 丢太久没动静的接收任务(对方中途退了)
            if (jobs.Count > 0)
            {
                List<string> dead = null;

                foreach (KeyValuePair<string, RecvJob> kv in jobs)
                {
                    if ((DateTime.Now - kv.Value.Last).TotalSeconds > RecvJobTimeoutSeconds)
                    {
                        if (dead == null) dead = new List<string>();
                        dead.Add(kv.Key);
                    }
                }

                if (dead != null)
                {
                    foreach (string key in dead) jobs.Remove(key);
                    if (Receiving != null && dead.Contains(Receiving.MsgId)) Receiving = null;
                }
            }
        }

        /// <summary>
        /// 取消发送: 清掉排队和正在发的那张, NetComm 队列里没发完的片也一并清掉,
        /// 再给对方发一条 NCX 通知, 让他把手头攒的片丢掉。
        /// </summary>
        public static void CancelSend()
        {
            int queued = sendQueue.Count;
            sendQueue.Clear();

            SendJob job = Sending;
            bool active = job != null && !job.Done;

            if (active)
            {
                NetCommBridge.ClearNetQueue();

                // 告诉对方别等了; 单条很短, 一定塞得进一条 NetComm 消息
                try
                {
                    NetCommBridge.Send(ImageProtocol.Channel,
                        Encoding.UTF8.GetBytes(ImageProtocol.CancelMagic + "|" + job.MsgId));
                }
                catch { }

                job.Done = true;
                SendStatus = "已取消 " + job.Name;
            }
            else if (queued > 0)
            {
                SendStatus = "已清空队列 " + queued + " 张";
            }
            else
            {
                SendStatus = "没有在发的图";
            }
        }

        //------------------------------ 接收 ------------------------------

        /// <summary>处理一条 NetComm 收到的图片分片(由 Demo 按前缀分发)</summary>
        public static void OnMessage(string text)
        {
            // "NCX|消息id" = 对方取消了这张图的发送, 丢掉手头攒到一半的片
            if (text != null && text.StartsWith(ImageProtocol.CancelMagic + "|", StringComparison.Ordinal))
            {
                string msgId = text.Substring(ImageProtocol.CancelMagic.Length + 1);

                if (jobs.Remove(msgId) && Receiving != null && Receiving.MsgId == msgId)
                {
                    Receiving = null;
                    RecvStatus = "对方取消了发送";
                }
                return;
            }

            Slice slice;
            if (!ImageProtocol.TryParse(text, out slice)) return;

            RecvJob job;
            if (!jobs.TryGetValue(slice.MsgId, out job))
            {
                if (jobs.Count >= MaxRecvJobs) DropOldestJob();

                job = new RecvJob
                {
                    MsgId = slice.MsgId,
                    Name = slice.Name,
                    Count = slice.Count,
                    TotalBytes = slice.TotalBytes,
                    Crc = slice.Crc,
                    Parts = new string[slice.Count],
                    Last = DateTime.Now
                };

                jobs[slice.MsgId] = job;
                Receiving = job;
            }

            job.Last = DateTime.Now;

            if (job.Parts[slice.Index] == null)
            {
                job.Parts[slice.Index] = slice.Body;
                job.Got++;
            }

            if (job.Got >= job.Count)
            {
                jobs.Remove(slice.MsgId);
                Complete(job);
            }
            else
            {
                RecvStatus = "接收中 " + (int)(job.Got * 100f / job.Count) + "%  " + job.Name;
            }
        }

        private static void Complete(RecvJob job)
        {
            try
            {
                StringBuilder sb = new StringBuilder(job.TotalBytes * 2);
                for (int i = 0; i < job.Count; i++) sb.Append(job.Parts[i]);

                byte[] data = Convert.FromBase64String(sb.ToString());

                if (data.Length != job.TotalBytes)
                {
                    LastError = "接收失败: 长度不符 " + data.Length + "/" + job.TotalBytes;
                    RecvStatus = LastError;
                    return;
                }

                if (ImageProtocol.Crc32(data) != job.Crc)
                {
                    LastError = "接收失败: CRC 校验不过";
                    RecvStatus = LastError;
                    return;
                }

                // 头像分片只进内存缓存和头像目录, 不占图片预览、也不进接收目录
                string avatarUser = ImageProtocol.AvatarUserName(job.Name);
                if (avatarUser != null)
                {
                    Receiving = null;
                    Avatar.OnReceived(avatarUser, data);
                    return;
                }

                string saved = SaveReceived(job.Name, data);
                SetPreview(data, job.Name);

                Receiving = null;
                LastError = "";
                LastInfo = "收到 " + job.Name + " (" + Format(data.Length) + ")" + (saved != null ? " 已存 " + saved : "");
                RecvStatus = "已收到 " + job.Name;

                Notify("[图片] " + LastInfo);
            }
            catch (Exception e)
            {
                LastError = "接收失败: " + e.Message;
                RecvStatus = LastError;
            }
        }

        private static void DropOldestJob()
        {
            string oldest = null;
            DateTime t = DateTime.MaxValue;

            foreach (KeyValuePair<string, RecvJob> kv in jobs)
            {
                if (kv.Value.Last < t) { t = kv.Value.Last; oldest = kv.Key; }
            }

            if (oldest != null)
            {
                jobs.Remove(oldest);
                if (Receiving != null && Receiving.MsgId == oldest) Receiving = null;
            }
        }

        //------------------------------ 显示 / 落盘 ------------------------------

        private static void SetPreview(byte[] data, string name)
        {
            GraphicsDevice device = Main.instance == null ? null : Main.instance.GraphicsDevice;
            string err;
            Texture2D tex = ImageCodec.Decode(device, data, out err);

            if (tex == null)
            {
                LastError = "显示失败: " + err;
                return;
            }

            Texture2D old = Preview;
            Preview = tex;
            PreviewName = name;

            if (old != null)
            {
                try { old.Dispose(); } catch { }
            }
        }

        private static string SaveReceived(string name, byte[] data)
        {
            try
            {
                string dir = ImageSetting.EnsureRecvFolder();
                if (dir == null) return null;

                string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + ImageProtocol.SanitizeName(name));
                File.WriteAllBytes(file, data);
                return file;
            }
            catch { return null; }
        }

        public static void OpenRecvFolder()
        {
            string dir = ImageSetting.EnsureRecvFolder();
            if (dir == null)
            {
                LastError = "创建接收目录失败";
                return;
            }

            try { Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (Exception e) { LastError = "打开目录失败: " + e.Message; }
        }

        //------------------------------ 杂项 ------------------------------

        /// <summary>进世界时清掉上一局的收发状态, 免得旧任务干扰</summary>
        public static void Reset()
        {
            jobs.Clear();
            sendQueue.Clear();
            Sending = null;
            Receiving = null;
            SendStatus = "就绪";
            RecvStatus = "等待接收";
            LastError = "";
        }

        /// <summary>
        /// F9 重载时用: 卸载跑在后台线程上, Preview 纹理不能跨线程 Dispose,
        /// 只把引用摘掉, 显存交给 GC/FNA 的终结器回收。
        /// </summary>
        public static void Release()
        {
            jobs.Clear();
            sendQueue.Clear();
            Sending = null;
            Receiving = null;
            Preview = null;
            PreviewName = "";
        }

        /// <summary>多行状态, 给面板和 stat 指令用</summary>
        public static string StatusBlock()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("通道 ").Append(ImageProtocol.Channel)
              .Append("  单条上限 ").Append(NetCommBridge.MaxDataLength)
              .Append("  每片原始 ").Append(ImageProtocol.ChunkBytes).AppendLine();
            sb.Append("NetComm: ").Append(NetCommBridge.Ready ? "就绪" : "不可用 - " + NetCommBridge.LastError)
              .Append("  netMode=").Append(Main.netMode)
              .Append("  绑定[").Append(NetCommBridge.ResolvedInfo).Append(']').AppendLine();
            sb.Append("发送: ").Append(SendStatus).AppendLine();
            sb.Append("接收: ").Append(RecvStatus).AppendLine();
            sb.Append("预览: ").Append(Preview == null ? "无" : PreviewName);
            if (!string.IsNullOrEmpty(LastInfo)) sb.AppendLine().Append(LastInfo);
            if (!string.IsNullOrEmpty(LastError)) sb.AppendLine().Append("错误: ").Append(LastError);
            return sb.ToString();
        }

        public static string Format(int bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0") + " KB";
            return (bytes / 1024f / 1024f).ToString("0.00") + " MB";
        }

        internal static string NextMsgId()
        {
            msgCounter = (msgCounter + 1) & 0xFFFF;
            return Environment.TickCount.ToString("X4") + msgCounter.ToString("X4");
        }

        /// <summary>往聊天栏说一句, 头像那边也要用, 所以不是 private</summary>
        internal static void Notify(string msg)
        {
            try
            {
                if (!Main.dedServ && !Main.gameMenu) Main.NewText(msg);
            }
            catch { }
        }
    }
}