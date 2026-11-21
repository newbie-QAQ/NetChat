using System;
using System.Collections.Generic;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.NetModules;
using tContentPatch;
using tContentPatch.Patch;

namespace NetChat
{
    /// <summary>
    /// 指令跑在加载器的指令控制台线程上, 而 NetComm 的队列不是线程安全的,
    /// 所以指令只投递请求, 由 Demo 在主线程 UpdatePostfix 时执行。
    /// </summary>
    internal static class Requests
    {
        private static readonly Queue<Action> queue = new Queue<Action>();

        public static void Post(Action action)
        {
            if (action == null) return;
            lock (queue) queue.Enqueue(action);
        }

        public static void Run()
        {
            while (true)
            {
                Action action;
                lock (queue)
                {
                    if (queue.Count == 0) return;
                    action = queue.Dequeue();
                }

                try { action(); }
                catch (Exception e) { ContentPatch.PrintTry("NetChat 出错: " + e.Message); }
            }
        }
    }

    /// <summary>模组入口: 注册聊天钩子, 并把设置页挂到快速按钮</summary>
    public class ThisMod : Mod
    {
        public override void AddPatch(IAddPatch addPatch)
        {
            // 发端: 模组聊天开关打开时把聊天转进隐蔽信令
            TryAdd(addPatch, typeof(ChatHelper).GetMethod("SendChatMessageFromClient"),
                typeof(ChatRelay).GetMethod("SendChatMessageFromClient_Prefix"), "聊天转发");

            // 收端: 82 号粒子包里的隐蔽信令(类型24+标记字节)不进原版粒子系统, 分发给各功能
            TryAdd(addPatch, typeof(NetParticlesModule).GetMethod("Deserialize"),
                typeof(SignalLink).GetMethod("Deserialize_Prefix"), "粒子信令");

            // 收端: 暗号弹幕(type=857 + 三重 ai 暗号 + owner=别人)在更新前认出并吞掉, owner 即发送者槽位
            TryAdd(addPatch, typeof(Projectile).GetMethod("Update", new Type[] { typeof(int) }),
                typeof(ProjectileSign).GetMethod("Update_Prefix"), "弹幕暗号");
        }

        /// <summary>挂补丁, 一个失败不影响其它</summary>
        private static void TryAdd(IAddPatch addPatch, System.Reflection.MethodBase original,
            System.Reflection.MethodInfo prefix, string tag)
        {
            try
            {
                addPatch.AddPrefix(original, prefix);
            }
            catch (Exception e)
            {
                ContentPatch.PrintTry("NetChat 补丁挂载失败[" + tag + "]: " + e.Message);
            }
        }

        public override void Loaded()
        {
            QuickButtonLink.Register();

            ContentPatch.PrintTry("NetChat 已加载。快速按钮里: 相机=设置页, 气泡=聊天室, 物品图标=聊天模式开关");
        }

        public override void Unload()
        {
            // F9 重载: 卸载跑在后台线程上, 纹理不能在这里 Dispose, 只摘引用让 GC 兜底
            Avatar.ReleaseTextures();
            ImageTransfer.Release();
        }
    }
}
