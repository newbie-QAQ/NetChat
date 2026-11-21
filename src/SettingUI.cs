using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.UI;
using tContentPatch;
using tContentPatch.Content.UI;

namespace NetChat
{
    /// <summary>
    /// 一个窗口一套 UserInterface。设置页和聊天室各一份, 互不遮挡, 可以同时开着。
    /// </summary>
    internal sealed class UIHost
    {
        private readonly Func<UIWindow> factory;

        private UserInterface ui;
        private UIState state;
        private UIWindow window;

        public UIHost(Func<UIWindow> factory)
        {
            this.factory = factory;
        }

        public bool IsOpen
        {
            get { return window != null && window.IsOpen; }
        }

        public void Toggle()
        {
            Ensure();
            if (window == null) return;

            if (window.IsOpen) window.Close();
            else window.Open(state);
        }

        public void Update(GameTime gameTime)
        {
            if (ui == null) return;
            ui.Update(gameTime);
        }

        public void Draw()
        {
            if (ui == null) return;
            ui.Draw(Main.spriteBatch, Main.gameTimeCache);
        }

        private void Ensure()
        {
            if (window != null) return;

            ui = new UserInterface();
            state = new UIState();
            ui.SetState(state);
            window = factory();
        }
    }

    /// <summary>
    /// 两个窗口的门面: 谁都能调 Toggle, 绘制由 Demo 插进界面层。
    /// 画的那一层整个包在 try 里 —— 这个环境里一个 SpriteBatch 层抛异常会连带整层 UI 不见,
    /// 报错还只报一次, 免得每帧刷屏。
    /// </summary>
    internal static class SettingUI
    {
        private static readonly UIHost Settings = new UIHost(delegate { return new SettingWindow(); });
        private static readonly UIHost Chat = new UIHost(delegate { return new UIWindowChatRoom(); });

        private static long lastErrorTick;

        public static bool ChatIsOpen
        {
            get { return Chat.IsOpen; }
        }

        public static void ToggleSettings()
        {
            Settings.Toggle();
        }

        public static void ToggleChat()
        {
            Chat.Toggle();
        }

        /// <summary>输入框吃按键要在这个阶段, 和框架其它窗口一样</summary>
        public static void Update(GameTime gameTime)
        {
            if (Main.dedServ) return;

            Settings.Update(gameTime);
            Chat.Update(gameTime);
        }

        public static void AddLayer(List<GameInterfaceLayer> layers)
        {
            if (Main.dedServ || layers == null) return;

            int index = layers.FindIndex(l => l.Name == "Vanilla: Inventory");
            if (index == -1) return;

            layers.Insert(index, new LegacyGameInterfaceLayer(
                "StaticTile.NetChat: Windows",
                delegate
                {
                    Draw();
                    return true;
                },
                InterfaceScaleType.UI));
        }

        private static void Draw()
        {
            // 三个部分分开包, 出错时能看出来是哪块抛的
            Try("设置页", delegate { Settings.Draw(); });
            Try("聊天室", delegate { Chat.Draw(); });
            Try("聊天模式按钮", delegate { ChatModeToggle.Draw(Main.spriteBatch); });
        }

        private static void Try(string label, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Exception ex = e.InnerException ?? e;
                string at = "";
                if (ex.StackTrace != null)
                {
                    int nl = ex.StackTrace.IndexOf('\n');
                    at = nl > 0 ? ex.StackTrace.Substring(0, nl).Trim() : ex.StackTrace.Trim();
                }

                long now = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                if (now - lastErrorTick < 5000) return;

                lastErrorTick = now;
                ContentPatch.PrintTry("画" + label + "出错: " + ex.GetType().Name + ": " + ex.Message + " @ " + at);
            }
        }
    }
}
