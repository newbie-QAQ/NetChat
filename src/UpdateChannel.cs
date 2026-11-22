using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Terraria;

namespace NetChat
{
    /// <summary>
    /// 作者专属的模组更新分发: 玩家遇到作者(带 author.flag 的客户端)就自动收到新版本。
    ///
    /// - 版本通告: 每次心跳 HERE 顺带发一条隐蔽信令 "NV|槽位|版本号", 收端记到在线列表上。
    ///   老版本不认识这个信令类型, 收到会静默忽略, 零兼容负担。
    /// - 作者判定: 模组目录下 author.flag 里存有 RSA 私钥的客户端才会推更新
    ///   (私钥只存在作者机器上, 复制个空文件或别人的模组目录当不了作者)。
    /// - 更新包: "NCUPD|版本|CRC32|RSA签名|Base64(Deflate后的dll)" 走 NetComm 的 20 号通道,
    ///   签名 = RSA-SHA256(整个 dll 字节), 公钥硬编码在下面。反编译拿到的只有公钥(验钞机),
    ///   没有私钥就伪造不出合法签名 —— 恶意推送在接收端被拒收。
    ///   版本不比自己新的直接忽略, 所以只有旧版本玩家会真正接收。
    /// - 接收全自动: 验签 → 校验 CRC + MZ 头 → 直接覆盖 Mods/NetChat/NetChat.dll(模组是整块读进
    ///   内存加载的, 文件没锁); 万一被锁就写 NetChat.dll.new 提示手动改名。重启游戏生效。
    /// - 防重发: 每个玩家槽位每轮连接只推一次, 收到回执 NCUPDOK 就标记"已送达"。
    /// </summary>
    internal static class UpdateChannel
    {
        /// <summary>本构建的版本号, 发版时手动改</summary>
        public const string Version = "1.2.3";

        /// <summary>作者的游戏名, 提醒文案里用</summary>
        public const string AuthorName = "新手QAQ";

        /// <summary>提醒文案里的 QQ 群号</summary>
        public const string QQGroup = "1137052535";

        /// <summary>提醒文案里的 GitHub 仓库地址</summary>
        public const string GithubUrl = "https://github.com/newbie-QAQ/NetChat/releases";

        /// <summary>给真老版本(收不到自动推送)的私聊提醒文案</summary>
        public static string OldVersionHint
        {
            get
            {
                string link = GithubUrl.Length > 0 ? "，或到 GitHub 下载最新版：" + GithubUrl : "";
                return "如果模组无法正常使用，请寻找" + AuthorName + "更新模组：QQ群" + QQGroup + link;
            }
        }

        /// <summary>验签用的 RSA 公钥(与作者机器 author.flag 里的私钥配对)</summary>
        private const string PublicKeyXml =
            "<RSAKeyValue><Modulus>vIJro+XSpg3vgPycBeqz4C6bjeWcJ8rESxsgTjR9ByvaJjQOXloLQTRVpmuIyt3azD7gfZux30IWV/ffA53Ww5KoD+Eqc4NyvDuMx30K6xXz996QgTKtYZiUDu6GGDv7VXBhyO8ljD"
            + "xf3Q/sdQdS8AoSZObebS7og2YTsWjLEhomzxmPMeyJlQzoQk/RtuL561t1qBCsVuriNIUlqbtJ7y/VNU"
            + "QslVoUHlqR4DEvCsyyRo1eJImIftPQu9f62BsZ6dYANgPM01ZYaNtvcqwDZZo7Tlj0ZMxATOUaZq+5Wh"
            + "5Uz9LjlCaYIq89NYFR8AHgptbDH9y7BmqtztC6uV8XEQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        /// <summary>更新包和回执走 NetComm 的 20 号通道(图片占用 13, 别撞)</summary>
        private const int Channel = 20;

        public const string UpdatePrefix = "NCUPD|";
        public const string OkPrefix = "NCUPDOK|";

        private static readonly List<int> attempted = new List<int>();

        /// <summary>已经私聊提醒过"该手动更新"的老版本玩家, 每轮连接每人一次</summary>
        private static readonly List<int> remindedSlots = new List<int>();

        /// <summary>已经提醒过"房间里有新版本"了, 一次连接提醒一次</summary>
        private static bool reminded;

        /// <summary>压缩打包好的更新包文本, 只在第一次要发时读自己的 dll</summary>
        private static byte[] package;

        /// <summary>缓存的签名私钥, 只在作者机器上能构造出来</summary>
        private static RSACryptoServiceProvider signKey;

        /// <summary>作者标记: author.flag 里必须是可用的 RSA 私钥才算作者端,
        /// 光放个空文件或复制别人的模组目录都当不了作者</summary>
        public static bool IsAuthor
        {
            get
            {
                if (signKey != null) return true;

                try
                {
                    string xml = File.ReadAllText(ModDir("author.flag"));
                    RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
                    rsa.FromXmlString(xml);
                    // 只认带私钥参数的: 公钥 xml 没有 D/P/Q, 签不了名
                    if (!xml.Contains("<D>")) return false;
                    signKey = rsa;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>给玩家的提醒文案</summary>
        public static string Reminder
        {
            get { return "如果模组无法正常使用，请寻找" + AuthorName + "更新模组（和作者在同一个服务器里会自动传给你）。"; }
        }

        /// <summary>模组目录下的文件路径(游戏根目录/Mods/NetChat/)</summary>
        private static string ModDir(string file)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "NetChat", file);
        }

        /// <summary>进世界/重载清状态</summary>
        public static void Reset()
        {
            attempted.Clear();
            remindedSlots.Clear();
            reminded = false;
        }

        /// <summary>
        /// 每帧巡检(作者端): 在线玩家里谁上线几秒了还没报过版本号(NetVersion 空),
        /// 就是装着没有更新通道的真老版本 —— 自动推送够不到他, 用私聊提醒一次。
        /// 私聊走老版本也认识的 WHISPER 通道, 对方过滤后只有本人能看到。
        /// </summary>
        public static void Poll()
        {
            if (!IsAuthor) return;

            for (int i = 0; i < Presence.Peers.Count; ++i)
            {
                Peer p = Presence.Peers[i];

                // 上线满 5 秒还没报版本才算老版本: 新版本玩家的 NV 跟着心跳走, 1 秒内就到
                if (p.Slot < 0 || p.Session.Length == 0) continue;
                if (p.NetVersion.Length > 0) continue;
                if (p.AgeSeconds < 5) continue;
                if (remindedSlots.Contains(p.Slot)) continue;

                // 广播出去大家都能收, 但老版本的私聊按会话 id 过滤, 只有本人显示
                bool sent = SignalLink.Send(SignalLink.KindWhisper,
                    Demo.WhisperPrefix + p.Session + "|" + ChatRelay.BuildBody(AuthorName, OldVersionHint));

                if (sent)
                {
                    remindedSlots.Add(p.Slot);
                    ChatRoom.AddSystem("已私聊提醒老版本玩家 " + p.User + "(" + p.Slot + ") 手动更新。");
                }
            }
        }

        /// <summary>收到版本通告 "NV|槽位|版本"。由 Demo 的信令路由调</summary>
        public static void OnVersion(int slot, string ver)
        {
            Presence.OnVersion(slot, ver);

            // 玩家视角: 房间里有比自己新的版本 → 提醒一次
            if (!reminded && Newer(ver, Version))
            {
                reminded = true;
                ChatRoom.AddSystem("房间里有 NetChat 新版本 " + ver + "。" + Reminder);
            }

            // 作者视角: 发现旧版本玩家就自动推一次(广播全员, 但只有旧版本会接收)
            if (IsAuthor && Newer(Version, ver) && !attempted.Contains(slot))
            {
                // 更新包和图片共用 NetComm 的重组器(按发送者区分), 别和自己正在发的图片撞车
                SendJob sending = ImageTransfer.Sending;
                if (sending != null && !sending.Done) return;

                if (SendPackage())
                {
                    // 广播一次就是发给所有人, 把现在已知的旧版本玩家都记成"推过了"
                    foreach (Peer p in Presence.Peers)
                    {
                        if (p.Slot >= 0 && Newer(Version, p.NetVersion) && !attempted.Contains(p.Slot))
                        {
                            attempted.Add(p.Slot);
                        }
                    }
                }
            }
        }

        /// <summary>收到回执 "NCUPDOK|版本", sender = 回执方的玩家槽位。由 Demo 调</summary>
        public static void OnOk(int sender, string ver)
        {
            if (!attempted.Contains(sender)) attempted.Add(sender);

            Peer peer = Presence.Find(sender.ToString());
            if (peer != null)
            {
                ChatRoom.AddSystem(peer.User + " 已收到 NetChat " + ver);
            }
        }

        /// <summary>版本号 a 是否比 b 新; 谁解析不出来都算不新, 宁可不发不收。
        /// 注意类里有个 Version 常量, 这里的 System.Version 要写全名</summary>
        private static bool Newer(string a, string b)
        {
            System.Version va, vb;
            if (!System.Version.TryParse(a ?? "", out va)) return false;
            if (!System.Version.TryParse(b ?? "", out vb)) return false;
            return va > vb;
        }

        /// <summary>打包并广播更新包。数据流: 读自己的 dll → Deflate → 对压缩数据签名 → Base64 →
        /// "NCUPD|版本|CRC|签名|..."。签名对象是压缩后的字节: CRC 保证解出来的就是签过的那份</summary>
        private static bool SendPackage()
        {
            try
            {
                if (package == null)
                {
                    byte[] dll = File.ReadAllBytes(ModDir("NetChat.dll"));

                    byte[] zip = Compress(dll);
                    if (zip == null) zip = dll;

                    byte[] sig = signKey.SignData(zip, "SHA256");
                    if (sig == null)
                    {
                        ChatRoom.AddSystem("更新包签名失败, 没发");
                        return false;
                    }

                    string text = UpdatePrefix + Version + "|" + ImageProtocol.Crc32(dll) + "|"
                        + Convert.ToBase64String(sig) + "|"
                        + Convert.ToBase64String(zip);

                    byte[] wire = Encoding.UTF8.GetBytes(text);
                    if (wire.Length > NetCommBridge.MaxDataLength)
                    {
                        ChatRoom.AddSystem("更新包太大(" + wire.Length + "字节), 超过 NetComm 上限, 没发");
                        return false;
                    }
                    package = wire;
                }

                return NetCommBridge.Send(Channel, package);
            }
            catch (Exception e)
            {
                ChatRoom.AddSystem("更新包没发出去: " + e.Message);
                return false;
            }
        }

        /// <summary>收到更新包。验签 → 校验(版本/CRC/MZ头)全过就直接写入, 重启生效。
        /// 验签是第一道门: 没有作者私钥的推送在这里就被拒收</summary>
        public static void OnPackage(string msg)
        {
            string[] parts = msg.Substring(UpdatePrefix.Length).Split('|');
            if (parts.Length != 4) return;

            // 不比自己新的直接忽略(同版本玩家收广播时都走这里)
            if (!Newer(parts[0], Version)) return;

            uint crc;
            if (!uint.TryParse(parts[1], out crc)) return;

            byte[] sig;
            try { sig = Convert.FromBase64String(parts[2]); }
            catch { return; }

            byte[] zip;
            try { zip = Convert.FromBase64String(parts[3]); }
            catch { return; }

            // 签名对象是压缩后的字节: 先验签再解压, 签不过的数据根本不进解压器
            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
            rsa.FromXmlString(PublicKeyXml);
            if (sig.Length < 1 || !rsa.VerifyData(zip, "SHA256", sig))
            {
                ChatRoom.AddSystem("收到一个 NetChat 更新包, 但签名无效, 已拒收。");
                return;
            }

            byte[] dll = Decompress(zip);
            if (dll == null) return;

            // MZ 头: 至少确认是 PE 文件再往磁盘写
            if (dll.Length < 2 || dll[0] != 0x4D || dll[1] != 0x5A) return;
            if (ImageProtocol.Crc32(dll) != crc) return;

            bool replaced;
            try
            {
                File.WriteAllBytes(ModDir("NetChat.dll"), dll);
                replaced = true;
            }
            catch
            {
                // 文件被占用等意外: 落成 .new 让用户手动改名
                try
                {
                    File.WriteAllBytes(ModDir("NetChat.dll.new"), dll);
                }
                catch { }
                replaced = false;
            }

            ChatRoom.AddSystem("收到 NetChat 新版本 " + parts[0]
                + (replaced ? "，重启游戏生效。" : "，但覆盖失败，已存为 NetChat.dll.new，请手动改名。")
                + Reminder);

            // 回执给作者(广播, 作者那边按发送者槽位记录)
            NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(OkPrefix + parts[0]));
        }

        /// <summary>Deflate 压缩; 压完不省就返回 null 用原数据</summary>
        private static byte[] Compress(byte[] data)
        {
            if (data.Length < 64) return null;

            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    {
                        ds.Write(data, 0, data.Length);
                    }

                    return ms.Length >= data.Length ? null : ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Decompress(byte[] data)
        {
            try
            {
                using (MemoryStream src = new MemoryStream(data, 0, data.Length, false))
                using (DeflateStream ds = new DeflateStream(src, CompressionMode.Decompress))
                using (MemoryStream dst = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int read;

                    while ((read = ds.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, read);
                        if (dst.Length > NetCommBridge.MaxDataLength) return null;
                    }

                    return dst.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
