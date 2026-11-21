using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Terraria;
using Terraria.GameContent.Drawing;
using Terraria.GameContent.NetModules;
using Terraria.Net;

namespace NetChat
{
    /// <summary>
    /// 隐蔽信令通道: 82 号粒子包, 类型固定 24 (ItemTransfer)。
    ///
    /// 隐形原理(反编译核实过):
    /// - 服务端对 82 包不校验, NetParticlesModule.Deserialize 里 netMode==2 直接原样广播;
    /// - 客户端 Spawn_ItemTransfer 用 UniqueInfoPiece 低 24 位当物品 ID 查表, ID 0 是 air,
    ///   ContentSamples.ItemsByType[0].IsAir 不成立 → 整个方法什么都不做。
    /// 所以把低 24 位保持为 0, 原版客户端收到我们的包 = 零显示、零痕迹,
    /// 没装模组的人完全看不见; 而且类型是合法的 24, 什么校验都能过。
    ///
    /// 打包: 21 字节载荷 = PositionInWorld(8) + MovementVector(8) + UniqueInfoPiece(4) + 玩家槽位(1)。
    /// UniqueInfoPiece 高字节放标记 0x80|kind —— 真人发的 ItemTransfer 高字节只有低 4 位
    /// 的 flags, 永远不会置 0x80, 这就是区分"我们的包"的依据。
    /// 每包有效数据 17 字节: [msgId][片号][总片数][数据14]。
    ///
    /// 可靠性(修"时好时坏"): 每条消息带序号, 2 秒没等到 ACK 整条重发, 最多 5 次;
    /// 收端对重复序号照样回 ACK 但不重复分发 —— 不管丢在哪一环都必然送达。
    ///
    /// 客户端侧 Deserialize 的 userId 是连接来源(永远=服务器), 拿不到真实发送者槽位,
    /// 所以识别消息由 HERE 正文自己带槽位。
    /// </summary>
    internal static class SignalLink
    {
        private const byte ParticleType = (byte)ParticleOrchestraType.ItemTransfer;   // 24

        /// <summary>标记位: UniqueInfoPiece 最高字节, 0x80|kind</summary>
        private const int MarkerHigh = 0x80;

        public const byte KindAck = 0;
        public const byte KindChat = 2;
        public const byte KindEmote = 3;
        public const byte KindWhisper = 4;

        /// <summary>版本通告("NV|槽位|版本号"), 和心跳 HERE 一起发; 老版本收到会静默忽略</summary>
        public const byte KindVer = 5;

        /// <summary>每包携带的数据字节数: 17 = 序号1 + 片号1 + 总片数1 + 数据14</summary>
        private const int DataPerPacket = 14;

        /// <summary>单条消息上限 210 字节 = 15 包, 聊天约 70 个汉字, 再长截断</summary>
        private const int MaxDataBytes = 210;

        private const int RetransmitFrames = 120;   // 2 秒
        private const int MaxTries = 5;
        private const int MaxPending = 24;

        private static ushort moduleId = ushort.MaxValue;
        private static byte nextMsgId = 1;
        private static byte salt;

        private sealed class OutMsg
        {
            public byte Id;
            public byte Kind;
            public byte[][] Frags;      // 每片 17 字节
            public int Tries;
            public int Timer;
        }

        private sealed class InMsg
        {
            public byte[][] Frags;
            public int Got;
            public int Created;     // 主游戏帧号, 攒超过 10 秒没齐就作废重建(防序号回绕污染)
        }

        private static readonly List<OutMsg> pending = new List<OutMsg>();
        private static readonly Dictionary<byte, InMsg> incoming = new Dictionary<byte, InMsg>();
        private static readonly List<byte> recentIds = new List<byte>();

        public static string LastError = "";

        /// <summary>收到完整消息: (kind, 正文, 发送者槽位-一直-1)。由 Demo 在 Initialize 里接线</summary>
        public static Action<byte, string, int> OnMessage;

        /// <summary>进世界/重载时清状态</summary>
        public static void Reset()
        {
            pending.Clear();
            incoming.Clear();
            recentIds.Clear();
            LastError = "";

            // 序号盐: 来自本机会话 id 的散列, 让不同玩家发出的裸序号在
            // 收端看起来互不相同 —— 不然大家都从 1 开始, 收端会把第二个人的消息当重复丢掉
            salt = (byte)Presence.Session.GetHashCode();
            if (salt == 0) salt = 0x5A;
        }

        /// <summary>排队一条信号, 主线程调用。超过 14 字节自动分片, 整条共享一个序号</summary>
        public static bool Send(byte kind, string payload)
        {
            if (Main.netMode != 1)
            {
                LastError = "只在联机客户端可用";
                return false;
            }

            if (pending.Count >= MaxPending)
            {
                LastError = "信令队列已满";
                return false;
            }

            byte[] data = Encoding.UTF8.GetBytes(payload ?? "");
            if (data.Length > MaxDataBytes)
            {
                // 按 UTF-8 截断, 不截半个字
                int cut = MaxDataBytes;
                while (cut > 0 && (data[cut] & 0xC0) == 0x80) cut--;
                byte[] trimmed = new byte[cut];
                Buffer.BlockCopy(data, 0, trimmed, 0, cut);
                data = trimmed;
            }

            int frags = Math.Max(1, (data.Length + DataPerPacket - 1) / DataPerPacket);
            byte id = nextMsgId++;
            if (nextMsgId == 0) nextMsgId = 1;

            // 上线前 XOR 盐: 收端按盐后序号去重/回ACK, 不同玩家的序号不会撞车
            byte wireId = (byte)(id ^ salt);

            byte[][] packets = new byte[frags][];
            for (int i = 0; i < frags; i++)
            {
                byte[] frag = new byte[3 + DataPerPacket];
                frag[0] = wireId;
                frag[1] = (byte)i;
                frag[2] = (byte)frags;
                int off = i * DataPerPacket;
                int n = Math.Min(DataPerPacket, data.Length - off);
                if (n > 0) Buffer.BlockCopy(data, off, frag, 3, n);
                packets[i] = frag;
            }

            pending.Add(new OutMsg { Id = wireId, Kind = kind, Frags = packets, Tries = MaxTries, Timer = RetransmitFrames });
            SendAll(packets, kind);
            LastError = "";
            return true;
        }

        /// <summary>Demo.UpdatePostfix 每帧调: 到点没 ACK 的整条重发</summary>
        public static void Update()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                OutMsg m = pending[i];
                if (--m.Timer > 0) continue;

                if (m.Tries <= 1)
                {
                    pending.RemoveAt(i);
                    LastError = "消息 #" + m.Id + " 重传 " + MaxTries + " 次未确认, 已放弃";
                    continue;
                }

                m.Tries--;
                m.Timer = RetransmitFrames;
                SendAll(m.Frags, m.Kind);
            }
        }

        /// <summary>
        /// NetParticlesModule.Deserialize 的前缀: 我们的包(类型24且高字节带标记)吞掉分发,
        /// 其它(含真人发的正常 ItemTransfer)倒回流位置交给原版。
        /// </summary>
        public static bool Deserialize_Prefix(BinaryReader reader, int userId)
        {
            try
            {
                long pos = reader.BaseStream.Position;
                int type = reader.ReadByte();
                if (type != ParticleType) { reader.BaseStream.Position = pos; return true; }

                byte[] rest = reader.ReadBytes(21);
                if (rest.Length < 21) { reader.BaseStream.Position = pos; return true; }

                int info = BitConverter.ToInt32(rest, 16);
                if ((info & unchecked((int)0x80000000)) == 0 || (info & 0xFFFFFF) != 0)
                {
                    reader.BaseStream.Position = pos;   // 真人的 ItemTransfer, 还给原版
                    return true;
                }

                HandlePacket(rest);
                return false;   // 吞掉。原版反正也什么都不做(物品ID=0)
            }
            catch
            {
                return true;    // 任何异常都交回原版, 免得网络处理跟着挂
            }
        }

        // ── 收端 ──

        private static void HandlePacket(byte[] rest)
        {
            byte kind = (byte)(rest[19] & 0x7F);

            // 还原 17 字节: rest[0..15] + rest[20]
            byte[] frag = new byte[17];
            Buffer.BlockCopy(rest, 0, frag, 0, 16);
            frag[16] = rest[20];

            byte msgId = frag[0];

            if (kind == KindAck)
            {
                // ACK 的序号放在数据区(frag[3]), SendOne 写进去的
                Ack(frag[3]);
                return;
            }

            // 无条件回 ACK —— 对端重发多半是上次 ACK 丢了
            SendOne(KindAck, new byte[] { msgId });

            // 只挡"已经完整分发过"的消息; ACK 上面已经回了
            if (recentIds.Contains(msgId))
            {
                return;
            }

            int total = frag[2];
            if (total <= 1)
            {
                // 去掉补零尾巴再分发, 不然短消息正文带 \0
                int len = DataPerPacket;
                while (len > 0 && frag[2 + len] == 0) len--;

                MarkSeen(msgId);
                Dispatch(kind, frag, 3, len);
                return;
            }

            // 多片攒包: 第一片绝不能进 recentIds, 不然后续片全被当重复丢掉,
            // 消息永远攒不齐 —— 重复片靠下面 Frags[i]==null 的幂等检查吸收
            InMsg msg;
            if (!incoming.TryGetValue(msgId, out msg)
                || msg.Frags.Length != total
                || Main.GameUpdateCount - msg.Created > 600)
            {
                msg = new InMsg { Frags = new byte[total][], Created = (int)Main.GameUpdateCount };
                incoming[msgId] = msg;
            }

            if (msg.Frags[frag[1]] == null)
            {
                byte[] part = new byte[DataPerPacket];
                Buffer.BlockCopy(frag, 3, part, 0, DataPerPacket);
                msg.Frags[frag[1]] = part;
                msg.Got++;
            }

            if (msg.Got < total) return;

            incoming.Remove(msgId);

            byte[] data = new byte[total * DataPerPacket];
            for (int i = 0; i < total; i++) Buffer.BlockCopy(msg.Frags[i], 0, data, i * DataPerPacket, DataPerPacket);

            // 去掉补零尾巴(载荷都是文本, 不含 0x00)
            int end = data.Length;
            while (end > 0 && data[end - 1] == 0) end--;

            MarkSeen(msgId);
            Dispatch(kind, data, 0, end);
        }

        /// <summary>标记一条消息"已完整分发过", 之后收到的重发片只补 ACK 不再分发</summary>
        private static void MarkSeen(byte id)
        {
            recentIds.Add(id);
            if (recentIds.Count > 64) recentIds.RemoveAt(0);
        }

        private static void Dispatch(byte kind, byte[] data, int off, int len)
        {
            string body = Encoding.UTF8.GetString(data, off, len);
            Action<byte, string, int> handler = OnMessage;
            if (handler != null) handler(kind, body, -1);
        }

        private static void Ack(byte id)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].Id == id)
                {
                    pending.RemoveAt(i);
                    return;
                }
            }
        }

        // ── 发端 ──

        private static void SendAll(byte[][] frags, byte kind)
        {
            for (int i = 0; i < frags.Length; i++) SendPacket(frags[i], kind);
        }

        /// <summary>不带确认的发送(ACK 用)</summary>
        private static void SendOne(byte kind, byte[] data)
        {
            byte[] frag = new byte[3 + DataPerPacket];
            frag[2] = 1;
            Buffer.BlockCopy(data, 0, frag, 3, Math.Min(data.Length, DataPerPacket));
            SendPacket(frag, kind);
        }

        /// <summary>
        /// 把 17 字节的片装进一个 82 号粒子包:
        /// type=24, 前 16 字节塞进两个 Vector2, 第 17 字节塞玩家槽位,
        /// UniqueInfoPiece 写 (0x80|kind)<<24 —— 低 24 位为 0 是隐形的关键。
        /// </summary>
        private static void SendPacket(byte[] frag, byte kind)
        {
            try
            {
                if (moduleId == ushort.MaxValue)
                    moduleId = NetManager.Instance.GetId<NetParticlesModule>();

                NetPacket packet = new NetPacket(moduleId, 32);
                packet.Writer.Write(ParticleType);
                packet.Writer.Write(frag, 0, 16);
                packet.Writer.Write(unchecked((int)((MarkerHigh | kind) << 24)));
                packet.Writer.Write(frag[16]);
                NetManager.Instance.SendToServer(packet);
            }
            catch (Exception e)
            {
                LastError = "发包失败: " + e.Message;
            }
        }
    }
}
