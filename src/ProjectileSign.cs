using System;
using Terraria;
using Terraria.DataStructures;

namespace NetChat
{
    /// <summary>
    /// HERE 弹幕信令 v5: 用一发"暗号弹幕"当识别消息。
    ///
    /// 发端: 本地 spawn 一发暗号弹幕(owner=自己), 立即发 27 号包同步 + 29 号包 kill,
    /// 然后把本地的摘除 —— 全程没有任何残留。
    /// 收端: 在 Projectile.Update 的前缀里认出暗号弹幕(类型匹配), 直接读
    /// owner 属性 —— 它就是发送者的玩家槽位, 网络层原生传输(27 包的 ProjectileKey
    /// 低 8 位 = Spawner, 收端赋给 owner), 不用自己在正文里带序号。
    /// 认出后把弹幕就地吞掉, 不渲染不 AI。
    ///
    /// 对没装模组的客户端: 服务端会先广播 27(创建)再广播 29(kill), 那发弹幕
    /// 存活不到一帧就被删, 几乎看不见。TShock 对 27/29 的校验(owner 必须是自己、
    /// 非 hostile 弹幕)天然满足。
    /// </summary>
    internal static class ProjectileSign
    {
        /// <summary>
        /// 暗号弹幕类型: 857 = FirstFractal(天顶分形)。
        /// 普通玩家基本生成不出来, 而且配合三重 ai 暗号,
        /// 正常游戏流程出现的 857 弹幕不可能撞上暗号。857 自身 alpha=255 全透明。
        /// </summary>
        public const int SignType = 857;

        /// <summary>
        /// 三重暗号值, 藏在 ai[0]/ai[1]/ai[2](27 包非零原样传输):
        /// 三个 ai 全部等于它才算暗号命中。
        /// </summary>
        public const float SignAi = 7777f;

        public static string LastError = "";
        public static int Sent;
        public static int Received;

        /// <summary>发一发暗号弹幕, 主线程调用(Presence.Update)</summary>
        public static bool SendHello(int slot)
        {
            if (Main.netMode != 1)
            {
                LastError = "netMode=" + Main.netMode + ", 不在线";
                return false;
            }

            try
            {
                Player p = Main.player[Main.myPlayer];
                if (p == null || !p.active)
                {
                    LastError = "本地玩家不在场";
                    return false;
                }

                // 生成位置 = 发送者自己脚下: 最合法的坐标, 服务端(含 TShock)不可能
                // 按"出界"拒收。弹幕 857 是 alpha=255 全透明, 就算别的客户端有
                // 一帧的存活窗口也看不见任何东西; 而且马上就发 29 号包 kill
                float x = p.Center.X;
                float y = p.Center.Y;

                int n = Projectile.NewProjectile(new SignalSource(),
                    x, y, 0f, 0f, SignType, 0, 0f, Main.myPlayer, SignAi, SignAi, SignAi);
                if (n < 0 || n >= Main.projectile.Length || !Main.projectile[n].active)
                {
                    LastError = "弹幕分配失败";
                    return false;
                }

                Projectile proj = Main.projectile[n];

                // 27 = 弹幕同步: 服务端创建后广播给其他客户端(owner = 我的槽位)
                NetMessage.SendData(27, -1, -1, null, proj.whoAmI);

                // 29 = 弹幕 kill(参数要传 key 而不是数组索引, 原版 Projectile.Kill 同款):
                // 位置传 NaN, 收端直接摘除不播击碎特效
                NetMessage.SendData(29, -1, -1, null, proj.key, float.NaN, float.NaN);

                proj.active = false;   // 本地立刻消失

                Sent++;
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 收端: Projectile.Update 的 Prefix, 主线程。
        /// 暗号弹幕在这里被认出并吞掉; 其它弹幕一律 true 放行。
        /// </summary>
        public static bool Update_Prefix(Projectile __instance)
        {
            if (Main.netMode != 1 || __instance.type != SignType) return true;

            // 三重暗号: ai[0]/ai[1]/ai[2] 全等于暗号值才算; 正常玩家打出的 857 永远不匹配
            if (__instance.ai[0] != SignAi || __instance.ai[1] != SignAi || __instance.ai[2] != SignAi)
            {
                return true;
            }

            if (__instance.owner == Main.myPlayer) return true;

            int slot = __instance.owner;
            __instance.active = false;   // 就地吞掉: 不更新不渲染

            try
            {
                if (slot >= 0 && slot < Main.player.Length && Main.player[slot] != null && Main.player[slot].active)
                {
                    Received++;
                    Presence.OnChatHello(slot);
                }
                else
                {
                    LastError = "槽位 " + slot + " 不在场";
                }
            }
            catch
            {
                // 识别炸了也不能影响弹幕系统
            }
            return false;
        }

        /// <summary>IEntitySource 是空接口, 挂个来源标记就行</summary>
        private sealed class SignalSource : IEntitySource
        {
            public string Context { get { return "NetChat/ProjectileSign"; } }
        }
    }
}
