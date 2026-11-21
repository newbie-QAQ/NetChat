using System;
using System.Collections.Generic;
using Terraria;

namespace NetChat
{
    /// <summary>一个装了本模组的在线玩家</summary>
    internal sealed class Peer
    {
        public string Session = "";
        public string User = "";
        public string RoleName = "";
        public int Slot = -1;
        public DateTime LastSeen;

        /// <summary>对方报上来的 NetChat 版本号("1.1"), 没收到过就是空串</summary>
        public string NetVersion = "";

        /// <summary>已经向他广播过"进入了聊天室"; 摘除时只有广播过的人才报"离开"</summary>
        public bool Announced;

        /// <summary>距今多少秒没收到他的心跳</summary>
        public int AgeSeconds
        {
            get { return (int)(DateTime.Now - LastSeen).TotalSeconds; }
        }
    }

    /// <summary>
    /// 谁装了本模组: 原版没法问, 只能自己打招呼。
    ///
    /// HERE 走弹幕暗号(ProjectileSign): 一发 owner=自己的特殊弹幕, 网络层原生
    /// 带着发送者槽位, 发出后立即 kill 几乎不留痕迹。进世界发一条, 之后每 30 秒
    /// 心跳一次, 90 秒收不到就把他摘掉。
    /// 另外每秒比一次原版 Main.player 的在场状态: 有人进出房间时立刻补发一条 HERE
    /// (新人马上能收到我), 同时把槽位已经空掉的人当场摘掉, 不用等心跳超时。
    /// 没做 BYE: 客户端退房那一刻 Send 已经发不出去了, 靠槽位判活反而更快更准。
    ///
    /// 会话 id 就是玩家的槽位序号, 服务器上唯一且收发两端都能算出来。
    /// </summary>
    internal static class Presence
    {
        private const int HeartbeatFrames = 60 * 30;
        private const int RetryFrames = 300;
        private const int ScanFrames = 60;

        /// <summary>有人进出时补发 HERE 的最小间隔, 免得进进出出把队列刷满</summary>
        private const int HelloGapFrames = 120;

        /// <summary>超过这些秒没收心跳就当离线</summary>
        public const int TimeoutSeconds = 90;

        /// <summary>
        /// 进世界后的安定期(秒): 这段时间里收到的 HERE 都算"本来就在房间里的老玩家",
        /// 不报"进入了聊天室"。别人发现我进来后 1~3 秒内就会回 HERE, 10 秒足够;
        /// 真有新人恰好在安定期里进来就不报, 属于可接受的漏报。
        /// </summary>
        private const int SettleFrames = 60 * 10;

        public static readonly List<Peer> Peers = new List<Peer>();

        public static string Status = "还没发过 HERE";
        public static string LastError = "";
        public static int SentHello;
        public static int ReceivedHello;

        private static int heartbeatFrames = HeartbeatFrames;
        private static int sinceHello = HeartbeatFrames;
        private static int scanFrames;
        private static int lastRoomCount = -1;
        private static bool inWorld;
        private static int settleFrames;

        /// <summary>本次连接的身份 = 自己的玩家槽位序号; 信令、私聊路由都用它</summary>
        public static string Session
        {
            get { return Main.myPlayer.ToString(); }
        }

        public static void OnEnterWorld()
        {
            Peers.Clear();
            ChatRoom.WhisperTarget = null;

            // 第一帧就发, 后面按心跳走
            heartbeatFrames = HeartbeatFrames;
            scanFrames = 0;
            lastRoomCount = -1;
            settleFrames = SettleFrames;
            inWorld = true;
            LastError = "";
            Status = "准备发 HERE...";
        }

        public static void OnLeaveWorld()
        {
            Peers.Clear();
            ChatRoom.WhisperTarget = null;
            inWorld = false;
            Status = "不在房间里, 不记人";
        }

        public static void Update()
        {
            if (Main.netMode != 1)
            {
                if (inWorld) OnLeaveWorld();
                return;
            }

            if (!inWorld) OnEnterWorld();

            sinceHello++;
            heartbeatFrames++;
            if (settleFrames > 0) settleFrames--;
            if (heartbeatFrames >= HeartbeatFrames)
            {
                if (SendHello()) heartbeatFrames = 0;
                else heartbeatFrames = HeartbeatFrames - RetryFrames;
            }

            scanFrames++;
            if (scanFrames >= ScanFrames)
            {
                scanFrames = 0;

                int count = CountActive();
                if (lastRoomCount >= 0 && count != lastRoomCount && sinceHello >= HelloGapFrames)
                {
                    // 有人进或退: 立刻再检测一次, 补发 HERE 并把走掉的摘掉
                    SendHello();
                    Status = "房间人数变了, 已重打招呼";
                }
                lastRoomCount = count;

                Prune();
            }
        }

        private static bool SendHello()
        {
            // 走弹幕暗号: 一发 owner=自己的特殊弹幕, 网络层原生带发送者槽位
            if (!ProjectileSign.SendHello(Main.myPlayer))
            {
                LastError = "HERE 没发出去: " + ProjectileSign.LastError;
                return false;
            }

            LastError = "";
            SentHello++;
            sinceHello = 0;
            Status = "已打招呼, 在线 " + Peers.Count + " 人";

            // 顺带报一下版本号, 给作者端的更新分发和"房间里有新版本"提醒用
            SignalLink.Send(SignalLink.KindVer, "NV|" + Main.myPlayer + "|" + UpdateChannel.Version);
            return true;
        }

        /// <summary>收到版本通告: NV|槽位|版本。槽位没打过招呼就先建个影子条目, 等 HERE 来补全</summary>
        public static void OnVersion(int slot, string ver)
        {
            Peer peer = Find(slot.ToString());
            if (peer == null)
            {
                peer = new Peer { Session = slot.ToString(), Slot = slot };
                Peers.Add(peer);
            }

            peer.NetVersion = ver ?? "";
            peer.LastSeen = DateTime.Now;
        }

        /// <summary>收到一条聊天信令 HERE, slot = 发送者的玩家槽位</summary>
        public static void OnChatHello(int slot)
        {
            Player p = Main.player[slot];
            string name = p == null ? "" : ProfileSetting.CleanName(p.name);

            // 会话 id = 槽位序号
            string id = slot.ToString();
            Peer peer = Find(id);
            bool isNew = peer == null;
            if (isNew)
            {
                peer = new Peer { Session = id };
                Peers.Add(peer);

                // 新面孔 = 有人刚加入, 补发一次自己的头像给他
                Avatar.RequestOnJoin();
            }

            peer.User = name;
            peer.RoleName = "";
            peer.Slot = slot;
            peer.LastSeen = DateTime.Now;

            // 安定期外的新面孔才报"进入", 安定期里的是进房时就在的老玩家
            if (isNew && settleFrames <= 0)
            {
                peer.Announced = true;
                ChatRoom.AddSystem(DisplayName(peer) + " 进入了聊天室");
            }

            ReceivedHello++;
            Status = "在线 " + Peers.Count + " 人";
        }

        /// <summary>按会话 id 找人, 没有返回 null</summary>
        public static Peer Find(string id)
        {
            for (int i = 0; i < Peers.Count; i++)
            {
                if (Peers[i].Session == id) return Peers[i];
            }
            return null;
        }

        private static void Prune()
        {
            for (int i = Peers.Count - 1; i >= 0; i--)
            {
                Peer peer = Peers[i];

                bool timeout = peer.AgeSeconds > TimeoutSeconds;
                bool gone = peer.Slot >= 0 && peer.Slot < Main.player.Length
                    && Main.player[peer.Slot] != null && !Main.player[peer.Slot].active;

                if (timeout || gone)
                {
                    // 广播过"进入"的人才报"离开", 免得安定期里静默记录的人退房时凭空冒一条
                    if (peer.Announced) ChatRoom.AddSystem(DisplayName(peer) + " 离开了聊天室");

                    if (ChatRoom.WhisperTarget == peer) ChatRoom.WhisperTarget = null;
                    Peers.RemoveAt(i);
                }
            }
        }

        private static int CountActive()
        {
            int count = 0;
            for (int i = 0; i < Main.player.Length; i++)
            {
                if (Main.player[i] != null && Main.player[i].active) count++;
            }
            return count;
        }

        /// <summary>列表里显示的名字: 优先配置里的用户名</summary>
        public static string DisplayName(Peer peer)
        {
            if (peer == null) return "";
            if (!string.IsNullOrEmpty(peer.User)) return peer.User;
            return peer.RoleName;
        }
    }
}
