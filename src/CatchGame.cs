using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;

namespace NetChat
{
    /// <summary>
    /// 抓人小游戏(纯信令, 无服务器端):
    ///
    /// 流程: 面板发起(发起者=首轮抓捕者) -> 其他玩家加入 -> 发起者点开始 ->
    ///       每轮 2 分钟, 抓捕者上 80 号 Blackout debuff + 自绘全屏黑幕(身边留圆形视野孔);
    ///       补偿: 抓捕者鼠标周围 5 格直径内, 方块画蓝色描边、玩家画红色描边(本地算, 无信令);
    ///       贴脸(40px)判定抓到 -> 被抓者成为下一轮抓捕者, 立刻开下一轮;
    ///       超时 = 逃亡者胜。游戏进行中全客户端不绘制地图玩家头像(补丁挂 MapHeadRenderer)。
    ///
    /// 信令(走隐蔽信令 KindGame, 正文自带发送者会话因为 SignalLink 拿不到真实槽位):
    ///   CG|发送者会话|OPEN                发起
    ///   CG|发送者会话|JOIN                加入
    ///   CG|发送者会话|START               发起者开始
    ///   CG|发送者会话|CAUGHT|被抓会话      抓到(任何客户端都能判定, 先到先得)
    ///   CG|发送者会话|TIMEUP              本轮超时(超时的客户端广播)
    ///   CG|发送者会话|STOP                手动结束(发起者/抓捕者)
    ///
    /// 绘制挂点: Main.DrawInterface_1_1_DrawEmoteBubblesInWorld 的 postfix ——
    /// 该层刚用 GameViewMatrix.ZoomMatrix 开了批次(坐标=世界坐标- screenPosition),
    /// 我们借这个批次画黑幕和描边, 画完批次保持原状态, 下一层(实体标记)无缝接手。
    /// 黑暗来自光照(buff 80)+自绘黑幕, 描边画在黑幕之上, 所以黑暗中只剩轮廓提示。
    /// </summary>
    internal static class CatchGame
    {
        public const string BodyPrefix = "CG|";

        private const int BlackoutBuff = 80;          // 原版 BuffID.Blackout
        private const int BlackoutTicks = RoundSeconds * 60;
        public const int RoundSeconds = 120;          // 每轮 2 分钟
        public const float CatchDistance = 40f;       // 贴脸距离(约 2.5 格)
        public const float ScanRadius = 40f;          // 提示窗口半径(5 格直径 = 80px)
        private const float HoleRadius = 170f;        // 抓捕者视野孔半径

        private enum Phase { Idle, Lobby, Playing }
        private static Phase phase;

        private static int hostSlot = -1;             // 发起者(=首轮抓捕者)
        private static int catcherSlot = -1;          // 当前轮抓捕者
        private static readonly List<int> members = new List<int>();   // 参战名单(含抓捕者)
        private static int round;
        private static float timeLeft;
        private static int detectTimer;
        private static int buffTimer;
        private static bool endSent;

        // ── UI 只读状态 ──

        public static bool HasGame { get { return phase != Phase.Idle; } }
        public static bool InLobby { get { return phase == Phase.Lobby; } }
        public static bool Playing { get { return phase == Phase.Playing; } }
        public static bool IsHost { get { return hostSlot == Main.myPlayer; } }
        public static bool Joined { get { return members.Contains(Main.myPlayer); } }
        public static bool ImCatcher { get { return phase == Phase.Playing && catcherSlot == Main.myPlayer; } }
        public static int MemberCount { get { return members.Count; } }
        public static int RoundNo { get { return round; } }

        public static string CatcherName { get { return Name(catcherSlot); } }

        public static string TimeText
        {
            get
            {
                int t = Math.Max(0, (int)timeLeft);
                return (t / 60) + ":" + (t % 60).ToString("00");
            }
        }

        private static string Name(int slot)
        {
            if (slot < 0 || slot >= Main.player.Length) return "?";
            Player p = Main.player[slot];
            return (p != null && p.active && !string.IsNullOrEmpty(p.name)) ? p.name : "玩家" + slot;
        }

        // ── 对外动作(UI 按钮调用): 本地立即生效 + 广播 ──

        public static void HostOpen()
        {
            if (phase != Phase.Idle) return;
            ApplyOpen(Main.myPlayer);
            Send("OPEN");
        }

        public static void Join()
        {
            if (phase != Phase.Lobby || members.Contains(Main.myPlayer)) return;
            ApplyJoin(Main.myPlayer);
            Send("JOIN");
        }

        public static void Start()
        {
            if (phase != Phase.Lobby || hostSlot != Main.myPlayer) return;
            if (members.Count < 2)
            {
                ChatRoom.AddSystem("至少要 2 名玩家加入才能开始抓人");
                return;
            }
            Send("START");
            BeginRound(1, hostSlot);
        }

        public static void StopGame()
        {
            if (phase == Phase.Idle) return;
            if (phase == Phase.Playing && hostSlot != Main.myPlayer && catcherSlot != Main.myPlayer) return;
            Send("STOP");
            EndGame("游戏被 " + Name(Main.myPlayer) + " 结束");
        }

        // ── 状态推进 ──

        private static void ApplyOpen(int slot)
        {
            phase = Phase.Lobby;
            hostSlot = slot;
            catcherSlot = slot;
            members.Clear();
            members.Add(slot);
            round = 0;
            ChatRoom.AddSystem("【抓人】" + Name(slot) + " 发起了抓人游戏, 在游戏面板点「加入游戏」参加");
        }

        private static void ApplyJoin(int slot)
        {
            members.Add(slot);
            ChatRoom.AddSystem("【抓人】" + Name(slot) + " 加入了抓人游戏 (当前 " + members.Count + " 人)");
        }

        private static void BeginRound(int r, int newCatcher)
        {
            round = r;
            catcherSlot = newCatcher;
            timeLeft = RoundSeconds;
            endSent = false;
            phase = Phase.Playing;
            buffTimer = 1;
            RefreshBuff();
            ChatRoom.AddSystem("【抓人】第 " + r + " 轮开始: 抓捕者是 " + Name(newCatcher)
                + ", 限时 " + RoundSeconds + " 秒");
        }

        private static void ApplyCaught(int caught)
        {
            if (phase != Phase.Playing || caught == catcherSlot || !members.Contains(caught)) return;

            int old = catcherSlot;
            BeginRound(round + 1, caught);
            ChatRoom.AddSystem("【抓人】" + Name(old) + " 抓住了 " + Name(caught)
                + ", 换 " + Name(caught) + " 来抓!");
        }

        private static void EndGame(string why)
        {
            phase = Phase.Idle;
            round = 0;
            hostSlot = catcherSlot = -1;
            RefreshBuff();
            ChatRoom.AddSystem("【抓人】" + why);
        }

        /// <summary>信号正文: "发送者会话|命令|参数..."</summary>
        public static void OnSignal(string body)
        {
            string[] p = body.Split('|');
            if (p.Length < 2) return;

            int sender;
            if (!int.TryParse(p[0], out sender)) return;
            string cmd = p[1];

            if (cmd == "OPEN")
            {
                if (phase == Phase.Idle) ApplyOpen(sender);
            }
            else if (cmd == "JOIN")
            {
                if (phase == Phase.Lobby && !members.Contains(sender)) ApplyJoin(sender);
            }
            else if (cmd == "START")
            {
                if (phase == Phase.Lobby && sender == hostSlot && members.Count >= 2)
                    BeginRound(1, hostSlot);
            }
            else if (cmd == "CAUGHT")
            {
                int caught;
                if (p.Length > 2 && int.TryParse(p[2], out caught)) ApplyCaught(caught);
            }
            else if (cmd == "TIMEUP")
            {
                if (phase == Phase.Playing) EndGame("时间到! 逃亡者获胜!");
            }
            else if (cmd == "STOP")
            {
                if (phase != Phase.Idle) EndGame("游戏被 " + Name(sender) + " 结束");
            }
        }

        private static void Send(string cmd)
        {
            SignalLink.Send(SignalLink.KindGame, BodyPrefix + Presence.Session + "|" + cmd);
        }

        // ── 每帧 ──

        public static void Update()
        {
            if (phase == Phase.Idle) return;

            // 掉线清理: 槽位失活的成员移出; 抓捕者/发起者掉线直接散局
            for (int i = members.Count - 1; i >= 0; i--)
            {
                if (!SlotAlive(members[i]))
                {
                    ChatRoom.AddSystem("【抓人】" + Name(members[i]) + " 掉线了");
                    members.RemoveAt(i);
                }
            }

            if (phase == Phase.Lobby)
            {
                if (!SlotAlive(hostSlot))
                {
                    phase = Phase.Idle;
                    hostSlot = catcherSlot = -1;
                    ChatRoom.AddSystem("【抓人】发起者掉线, 游戏解散");
                }
                return;
            }

            if (!SlotAlive(catcherSlot))
            {
                Send("STOP");
                EndGame("抓捕者离开了, 游戏结束");
                return;
            }

            timeLeft -= 1f / 60f;

            // 超时: 广播一次, 自己也立即结束(别的客户端收到 TIMEUP 走同一条路)
            if (timeLeft <= 0 && !endSent)
            {
                endSent = true;
                Send("TIMEUP");
                EndGame("时间到! 逃亡者获胜!");
                return;
            }

            // buff 维持(被服务器/药水清掉时自动补上)
            if (--buffTimer <= 0)
            {
                buffTimer = 120;
                RefreshBuff();
            }

            // 贴脸判定: 所有客户端都跑, 谁先广播谁生效, 重复的会被状态挡掉
            if (--detectTimer > 0) return;
            detectTimer = 5;

            Player cp = Main.player[catcherSlot];
            if (cp == null || !cp.active || cp.dead) return;

            for (int i = 0; i < members.Count; i++)
            {
                int s = members[i];
                if (s == catcherSlot) continue;

                Player ep = Main.player[s];
                if (ep == null || !ep.active || ep.dead) continue;

                if (Vector2.Distance(cp.Center, ep.Center) < CatchDistance)
                {
                    Send("CAUGHT|" + s);
                    ApplyCaught(s);
                    break;
                }
            }
        }

        private static bool SlotAlive(int slot)
        {
            return slot >= 0 && slot < Main.player.Length
                && Main.player[slot] != null && Main.player[slot].active;
        }

        public static void Reset()
        {
            phase = Phase.Idle;
            members.Clear();
            hostSlot = catcherSlot = -1;
            round = 0;
            endSent = false;
            timeLeft = 0f;

            // F9 重载/退世界时人在游戏里当着抓捕者: 摘掉 debuff
            if (Main.netMode == 1 && !Main.gameMenu) RefreshBuff();
        }

        /// <summary>想让抓捕者有 80 号 debuff, 不想则摘掉; 两边状态都对齐</summary>
        private static void RefreshBuff()
        {
            Player me = Main.player[Main.myPlayer];
            if (me == null) return;

            int idx = -1;
            for (int i = 0; i < me.buffType.Length; i++)
            {
                if (me.buffType[i] == BlackoutBuff) { idx = i; break; }
            }

            if (ImCatcher && idx < 0) me.AddBuff(BlackoutBuff, BlackoutTicks, false);
            if (!ImCatcher && idx >= 0) me.DelBuff(idx);
        }

        // ── 绘制(抓捕者): 黑幕 + 鼠标轮廓提示 ──
        // 挂在 Main.DrawInterface_1_1_DrawEmoteBubblesInWorld 的 postfix 上,
        // 此时主批次已用 GameViewMatrix.ZoomMatrix 打开, 坐标 = 世界坐标 - screenPosition。

        public static void PostDrawEmoteBubbles()
        {
            bool active = phase == Phase.Playing && catcherSlot == Main.myPlayer;
            if (!active && !DebugPreview) return;

            Player me = Main.player[Main.myPlayer];
            if (me == null || !me.active) return;

            DrawDarkness(Main.spriteBatch, me);
            DrawScan(Main.spriteBatch);
        }

        /// <summary>世界坐标 -> 界面世界层批次坐标(和原版实体标记同款换算)</summary>
        private static Vector2 ToBatch(Vector2 world)
        {
            Vector2 v = world - Main.screenPosition;
            if (Main.player[Main.myPlayer].gravDir == -1f) v.Y = Main.screenHeight - v.Y;
            return v;
        }

        private static void DrawDarkness(SpriteBatch sb, Player me)
        {
            Texture2D px = TextureAssets.MagicPixel.Value;

            float zoomX = Math.Max(0.01f, Main.GameViewMatrix.Zoom.X);
            float zoomY = Math.Max(0.01f, Main.GameViewMatrix.Zoom.Y);
            float zw = Main.screenWidth / zoomX;
            float zh = Main.screenHeight / zoomY;

            Vector2 hole = ToBatch(me.Center);
            float r = HoleRadius;
            const float Band = 12f;
            Color black = Color.Black;

            // 逐横带铺黑, 身边圆形视野孔留空(带宽 12px 的阶梯近似圆, 视觉足够平滑)
            // 源矩形 (0,0,1,1): 见 Line() 注释, MagicPixel 不是 1x1 贴图
            for (float y = -Band; y < zh + Band; y += Band)
            {
                float dy = y + Band * 0.5f - hole.Y;
                float ad = Math.Abs(dy);

                if (ad >= r)
                {
                    sb.Draw(px, new Vector2(-20f, y), new Rectangle(0, 0, 1, 1), black, 0f, Vector2.Zero,
                        new Vector2(zw + 40f, Band), SpriteEffects.None, 0f);
                }
                else
                {
                    float half = (float)Math.Sqrt(r * r - dy * dy);
                    float left = hole.X - half;
                    float right = hole.X + half;

                    if (left > -20f)
                        sb.Draw(px, new Vector2(-20f, y), new Rectangle(0, 0, 1, 1), black, 0f, Vector2.Zero,
                            new Vector2(left + 20f, Band), SpriteEffects.None, 0f);
                    if (right < zw + 20f)
                        sb.Draw(px, new Vector2(right, y), new Rectangle(0, 0, 1, 1), black, 0f, Vector2.Zero,
                            new Vector2(zw + 20f - right, Band), SpriteEffects.None, 0f);
                }
            }

            // 视野孔边缘一圈淡蓝, 让抓捕者知道自己能看多远
            Ring(sb, px, hole, r, 32, 2f, new Color(140, 190, 255) * 0.45f);
        }

        private static void DrawScan(SpriteBatch sb)
        {
            Texture2D px = TextureAssets.MagicPixel.Value;
            Vector2 m = ToBatch(Main.MouseWorld);
            Color blue = new Color(90, 170, 255) * 0.9f;
            Color red = new Color(255, 80, 80) * 0.9f;

            // 提示窗口轮廓
            Ring(sb, px, m, ScanRadius, 24, 1.5f, Color.White * 0.55f);

            // 蓝色: 圆内方块描边
            int t0x = (int)((Main.MouseWorld.X - ScanRadius) / 16f) - 1;
            int t0y = (int)((Main.MouseWorld.Y - ScanRadius) / 16f) - 1;
            int t1x = (int)((Main.MouseWorld.X + ScanRadius) / 16f) + 1;
            int t1y = (int)((Main.MouseWorld.Y + ScanRadius) / 16f) + 1;

            for (int tx = t0x; tx <= t1x; tx++)
            {
                for (int ty = t0y; ty <= t1y; ty++)
                {
                    if (tx < 0 || ty < 0 || tx >= Main.maxTilesX || ty >= Main.maxTilesY) continue;

                    Tile t = Main.tile[tx, ty];
                    if (t == null || !t.active()) continue;

                    RectOutline(sb, px, ToBatch(new Vector2(tx * 16f, ty * 16f)), 16f, 16f, 1.2f, blue);
                }
            }

            // 红色: 圆内玩家描边(包括自己指到自己时的逃亡者轮廓)
            for (int i = 0; i < Main.player.Length; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active || p.dead) continue;
                if (Vector2.Distance(p.Center, Main.MouseWorld) > ScanRadius + 24f) continue;

                RectOutline(sb, px, ToBatch(p.position), p.width, p.height, 2f, red);
            }
        }

        private static void Ring(SpriteBatch sb, Texture2D px, Vector2 c, float radius, int segs, float thick, Color col)
        {
            Vector2 prev = c + new Vector2(radius, 0f);
            for (int i = 1; i <= segs; i++)
            {
                double a = Math.PI * 2.0 * i / segs;
                Vector2 next = c + new Vector2((float)Math.Cos(a) * radius, (float)Math.Sin(a) * radius);
                Line(sb, px, prev, next, thick, col);
                prev = next;
            }
        }

        private static void Line(SpriteBatch sb, Texture2D px, Vector2 a, Vector2 b, float thick, Color col)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len < 0.01f) return;

            // 源矩形必须取 (0,0,1,1): MagicPixel 不是 1x1 贴图, 不限源的话
            // 缩放(长度x厚度)会把整张贴图放大 N 倍, 画出来全是粗条
            sb.Draw(px, a, new Rectangle(0, 0, 1, 1), col, (float)Math.Atan2(d.Y, d.X), Vector2.Zero,
                new Vector2(len, thick), SpriteEffects.None, 0f);
        }

        private static void RectOutline(SpriteBatch sb, Texture2D px, Vector2 tl, float w, float h, float thick, Color col)
        {
            Line(sb, px, tl, tl + new Vector2(w, 0f), thick, col);
            Line(sb, px, tl + new Vector2(0f, h), tl + new Vector2(w, h), thick, col);
            Line(sb, px, tl, tl + new Vector2(0f, h), thick, col);
            Line(sb, px, tl + new Vector2(w, 0f), tl + new Vector2(w, h), thick, col);
        }

        // ── 调试(仅本地调试 dll 使用: 只改本机状态, 一律不广播; DebugBuild.Enabled=false 的发布版不会暴露入口) ──

        /// <summary>调试预览: 不进游戏状态也画黑幕+鼠标轮廓, 测渲染效果用</summary>
        public static bool DebugPreview;

        /// <summary>调试: 单人强开, 本机直接进入第 1 轮, 自己当抓捕者</summary>
        public static void DebugStartSolo()
        {
            members.Clear();
            members.Add(Main.myPlayer);
            hostSlot = Main.myPlayer;
            BeginRound(1, Main.myPlayer);
        }

        /// <summary>调试: 模拟抓到 —— 世界里离抓捕者最近的其他玩家变成新抓捕者(本地)</summary>
        public static void DebugForceCaught()
        {
            if (phase != Phase.Playing) return;

            Player cp = Main.player[catcherSlot];
            int best = -1;
            float bestD = float.MaxValue;

            for (int i = 0; i < Main.player.Length; i++)
            {
                Player p = Main.player[i];
                if (p == null || !p.active || p.dead || i == catcherSlot) continue;

                float d = cp != null ? Vector2.Distance(cp.Center, p.Center) : 0f;
                if (d < bestD) { bestD = d; best = i; }
            }

            if (best < 0) return;
            if (!members.Contains(best)) members.Add(best);
            ApplyCaught(best);
        }

        /// <summary>调试: 给自己上/摘 80 号 buff, 纯测黑暗效果(和游戏逻辑无关)</summary>
        public static void DebugToggleBuff()
        {
            Player me = Main.player[Main.myPlayer];
            if (me == null) return;

            int idx = -1;
            for (int i = 0; i < me.buffType.Length; i++)
            {
                if (me.buffType[i] == BlackoutBuff) { idx = i; break; }
            }

            if (idx >= 0) me.DelBuff(idx);
            else me.AddBuff(BlackoutBuff, 60 * 60, false);
        }

        /// <summary>调试: 本机强制散局(不广播)</summary>
        public static void DebugReset()
        {
            if (phase == Phase.Idle) return;
            EndGame("调试: 本机强制重置");
        }

        // ── 补丁方法(ThisMod.AddPatch 注册) ──

        /// <summary>游戏进行中全客户端不画地图玩家头像(全屏地图/小地图/离屏头像指示共用这一个入口)</summary>
        public static bool MapHead_Prefix()
        {
            return !Playing;
        }
    }
}
