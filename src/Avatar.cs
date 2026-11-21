using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace NetChat
{
    /// <summary>
    /// 用户名和头像的收发。
    ///
    /// 发: 头像先处理成正方形, 再借用图片那套分片协议发给同房间的人, 名字里就带上发送方的用户名,
    /// 所以不用额外发一条对应关系。进世界时自动发一次; 队列被图片占用就隔一会儿再试。
    /// 收: 解码成贴图先放内存字典, 同时在游戏根目录的 NetChat头像 里落一份, 重启还在。
    ///
    /// 只在主线程调(进世界、每帧更新、面板按钮都在主线程)。
    /// </summary>
    internal static class Avatar
    {
        /// <summary>头像边长, 收发双方都是这个尺寸</summary>
        public const int Side = 200;

        private const int RetryFrames = 90;
        private const int MaxTries = 40;

        /// <summary>有人加入补发头像的最小间隔, 免得刚进世界时对每条 HERE 都触发一遍</summary>
        private const int JoinCooldownSeconds = 45;

        private static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        /// <summary>上一次请求广播头像的时间, 给加入补发当冷却用</summary>
        private static DateTime lastBroadcastRequest = DateTime.MinValue;

        /// <summary>还有多少帧去试下一次广播; 负数表示没有待广播的头像</summary>
        private static int waitFrames = -1;

        private static int triesDone;

        /// <summary>自己发出去的头像, 发送者收不到自己的包, 所以本地解码一份</summary>
        public static Texture2D SelfPreview;

        /// <summary>最近收到的别人头像和它的用户名</summary>
        public static Texture2D LastReceived;
        public static string LastName = "";

        public static string Status = "还没发过头像";
        public static string LastError = "";

        /// <summary>收到的头像落盘的位置: 游戏根目录下的 NetChat头像</summary>
        public static string Folder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NetChat头像"); }
        }

        public static int Count
        {
            get { return cache.Count; }
        }

        /// <summary>进世界时清掉上一局的贴图, 显卡设备可能已经重建过; 落盘的文件不动</summary>
        public static void Reset()
        {
            waitFrames = -1;
            triesDone = 0;
            LastError = "";
            LastName = "";
            Status = "还没发过头像";

            foreach (KeyValuePair<string, Texture2D> kv in cache) DisposeOld(kv.Value);
            cache.Clear();

            DisposeOld(SelfPreview);
            SelfPreview = null;

            DisposeOld(LastReceived);
            LastReceived = null;
        }

        /// <summary>
        /// F9 重载时用: 卸载跑在后台线程上, 纹理不能跨线程 Dispose,
        /// 只把引用摘掉, 显存交给 GC/FNA 的终结器回收。
        /// </summary>
        public static void ReleaseTextures()
        {
            waitFrames = -1;
            cache.Clear();
            SelfPreview = null;
            LastReceived = null;
        }

        //------------------------------ 发送 ------------------------------

        /// <summary>进世界时叫一次, 之后由 Update 重试到发出去</summary>
        public static void RequestBroadcast()
        {
            lastBroadcastRequest = DateTime.Now;
            LastError = "";

            if (!ProfileSetting.AutoSendAvatar)
            {
                waitFrames = -1;
                Status = "自动发送头像是关的";
                return;
            }

            if (Main.netMode != 1)
            {
                waitFrames = -1;
                Status = "单机不发头像, 联机才发";
                return;
            }

            if (string.IsNullOrEmpty(ProfileSetting.CurrentName()))
            {
                waitFrames = -1;
                Status = "没有可用的用户名";
                return;
            }

            // 路径没填或文件不在就别白跑一分钟
            string path = ProfileSetting.AvatarPath;
            if (string.IsNullOrEmpty(path))
            {
                waitFrames = -1;
                Status = "没填头像路径";
                return;
            }

            if (!File.Exists(path.Trim().Trim('"')))
            {
                waitFrames = -1;
                Status = "头像文件不存在";
                return;
            }

            waitFrames = 0;
            triesDone = 0;
            Status = "准备发头像...";
        }

        /// <summary>设置页按钮: 不管开关, 立刻发一次</summary>
        public static bool SendNow(out string msg)
        {
            bool ok = TrySend();
            msg = ok ? Status : LastError;
            return ok;
        }

        /// <summary>
        /// 检测到有人加入聊天室时叫一次: 新人没收到过进世界那波头像, 补发一遍。
        /// 45 秒冷却, 进世界时对已有玩家们的第一批 HERE 不会重复触发。
        /// </summary>
        public static void RequestOnJoin()
        {
            if ((DateTime.Now - lastBroadcastRequest).TotalSeconds < JoinCooldownSeconds) return;

            RequestBroadcast();
        }

        /// <summary>每帧推进待发的重试, 图片还在发就等它腾出队列</summary>
        public static void Update()
        {
            if (waitFrames < 0) return;

            if (waitFrames > 0)
            {
                waitFrames--;
                return;
            }

            if (TrySend())
            {
                waitFrames = -1;
                return;
            }

            triesDone++;
            if (triesDone >= MaxTries)
            {
                waitFrames = -1;
                Status = "头像没发出去: " + LastError;
            }
            else
            {
                waitFrames = RetryFrames;
            }
        }

        private static bool TrySend()
        {
            string user = ProfileSetting.CurrentName();
            if (string.IsNullOrEmpty(user))
            {
                LastError = "没有可用的用户名";
                return false;
            }

            byte[] data;
            string info;
            string err;
            if (!ImageCodec.TryPrepareAvatar(ProfileSetting.AvatarPath, Side, out data, out info, out err))
            {
                LastError = err;
                Status = "头像没准备好: " + err;
                return false;
            }

            if (!ImageTransfer.BeginSendAvatar(data, user))
            {
                LastError = ImageTransfer.LastError;
                return false;
            }

            LastError = "";
            SetSelfPreview(data);
            Status = "排队发自己头像: " + user + " (" + info + ", " + ImageTransfer.Format(data.Length) + ")";
            return true;
        }

        /// <summary>分片全部排进队列并送完后由 ImageTransfer 回调</summary>
        public static void OnSent()
        {
            Status = "已把头像发给同房间: " + ProfileSetting.CurrentName();
        }

        private static void SetSelfPreview(byte[] data)
        {
            string err;
            Texture2D tex = ImageCodec.Decode(Device, data, out err);
            if (tex == null) return;

            Texture2D old = SelfPreview;
            SelfPreview = tex;
            DisposeOld(old);
        }

        //------------------------------ 接收 ------------------------------

        /// <summary>收齐一条头像分片后由 ImageTransfer 回调</summary>
        public static void OnReceived(string user, byte[] data)
        {
            if (string.IsNullOrEmpty(user)) return;

            string err;
            Texture2D tex = ImageCodec.Decode(Device, data, out err);

            if (tex == null)
            {
                LastError = "头像解码失败: " + err;
                ImageTransfer.RecvStatus = LastError;
                return;
            }

            Texture2D old;
            if (cache.TryGetValue(user, out old)) cache[user] = tex;
            else cache[user] = tex;

            DisposeOld(old);

            LastReceived = tex;
            LastName = user;
            LastError = "";
            Status = "收到 " + user + " 的头像 (" + ImageTransfer.Format(data.Length) + ")";
            ImageTransfer.RecvStatus = "已收到 " + user + " 的头像";

            string saved = Save(user, data);
            ImageTransfer.Notify("[头像] 收到 " + user + " 的头像" + (saved != null ? ", 已存 " + saved : ""));
        }

        private static string Save(string user, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, ImageProtocol.SanitizeName(user) + ".png");
                File.WriteAllBytes(file, data);
                return file;
            }
            catch { return null; }
        }

        public static Texture2D Get(string user)
        {
            Texture2D tex;
            return user != null && cache.TryGetValue(user, out tex) ? tex : null;
        }

        /// <summary>收到过的用户名, 逗号连起来</summary>
        public static string Names()
        {
            if (cache.Count == 0) return "还没有";

            List<string> list = new List<string>(cache.Keys);
            list.Sort(StringComparer.Ordinal);
            return string.Join(", ", list.ToArray());
        }

        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Process.Start("explorer.exe", "\"" + Folder + "\"");
            }
            catch (Exception e) { LastError = "打开目录失败: " + e.Message; }
        }

        //------------------------------ 杂项 ------------------------------

        private static GraphicsDevice Device
        {
            get { return Main.instance == null ? null : Main.instance.GraphicsDevice; }
        }

        private static void DisposeOld(Texture2D tex)
        {
            if (tex == null) return;
            try { tex.Dispose(); } catch { }
        }
    }
}
