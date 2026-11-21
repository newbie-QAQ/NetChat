using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;
using tContentPatch;
using tContentPatch.ModLoad;

namespace NetChat
{
    /// <summary>
    /// 快速按钮上的一个图标: 整张贴缩到固定大小, 悬停给一句话, 点一下走回调。
    /// 原版那张聊天气泡只有一格, 所以不用管帧序。
    /// </summary>
    internal sealed class UIButtonIcon : UIElement
    {
        private readonly Texture2D tex;
        private readonly string tip;
        private readonly Action onClick;

        public UIButtonIcon(Texture2D tex, float size, string tip, Action onClick)
        {
            this.tex = tex;
            this.tip = tip;
            this.onClick = onClick;

            Width.Pixels = size;
            Height.Pixels = size;

            OnLeftClick += delegate
            {
                SoundEngine.PlaySound(12);
                if (onClick != null) onClick();
            };

            OnUpdate += delegate
            {
                if (IsMouseHovering) Main.instance.MouseText(Tip(), 0, 0);
            };
        }

        private string Tip()
        {
            if (ChatRoom.Unread > 0) return tip + " (未读 " + ChatRoom.Unread + ")";
            return tip;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();

            if (tex == null || tex.IsDisposed)
            {
                spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                    new Rectangle((int)dim.X, (int)dim.Y, (int)dim.Width, (int)dim.Height), Color.Gray * 0.5f);
            }
            else
            {
                float scale = Math.Min(dim.Width / tex.Width, dim.Height / tex.Height);
                Vector2 size = new Vector2(tex.Width, tex.Height) * scale;
                Vector2 pos = dim.Center() - size * 0.5f;

                Color color = IsMouseHovering ? Color.White : Color.Gray;
                spriteBatch.Draw(tex, pos, null, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }

            // 未读角标: 红底白字钉在右上角, 超 99 显示 99+
            int unread = ChatRoom.Unread;
            if (unread > 0)
            {
                string label = unread > 99 ? "99+" : unread.ToString();
                float badgeW = ChatText.Width(label) * 0.7f + 6f;
                Rectangle badge = new Rectangle((int)(dim.X + dim.Width - badgeW), (int)(dim.Y + 1f), (int)badgeW, 12);

                spriteBatch.Draw(TextureAssets.MagicPixel.Value, badge, new Color(210, 40, 40) * 0.95f);
                Terraria.Utils.DrawBorderString(spriteBatch, label, new Vector2(badge.X + 3f, badge.Y + 1f), Color.White, 0.65f);
            }
        }
    }

    /// <summary>
    /// 把两个页面挂到「快速按钮」模组的按钮条上: 相机是设置页, 气泡是聊天室。
    /// 用反射调 QuickButton.Add, 不硬依赖它的 dll; 没装就只在装载日志里说一句。
    /// </summary>
    internal static class QuickButtonLink
    {
        private const string AssemblyName = "QuickButton";
        private const string TypeName = "QuickButton.QuickButton.QuickButton";

        /// <summary>按钮的编号, 写在 QuickButton 的 buttonSet.json 里可以调位置</summary>
        private const string SettingsKey = "NetChat.OpenSettings";
        private const string ChatKey = "NetChat.OpenChatRoom";

        private const string IconPath = "Images/UI/Camera_1";

        private static MethodInfo addMethod;

        public static void Register()
        {
            if (Main.dedServ) return;

            try
            {
                ModObject qb = ContentPatch.GetModObjects()?
                    .FirstOrDefault(i => i.assembly?.GetName().Name == AssemblyName);

                addMethod = qb?.assembly?.GetType(TypeName)?.GetMethod("Add", BindingFlags.Static | BindingFlags.Public);
                if (addMethod == null)
                {
                    ContentPatch.PrintTry("没挂到快速按钮: 没找到已启用的 QuickButton, 设置页和聊天室都打不开");
                    return;
                }

                Texture2D camera = Main.Assets.Request<Texture2D>(IconPath).Value;

                UIImage settings = new UIImage(camera);
                settings.Width.Pixels = 32f;
                settings.Height.Pixels = 32f;
                settings.ScaleToFit = true;
                settings.OnUpdate += delegate
                {
                    if (settings.IsMouseHovering) Main.instance.MouseText("通信示例设置", 0, 0);
                };
                settings.OnLeftClick += delegate
                {
                    SoundEngine.PlaySound(12);
                    SettingUI.ToggleSettings();
                };
                addMethod.Invoke(null, new object[] { SettingsKey, settings });

                UIButtonIcon chat = new UIButtonIcon(TextureAssets.Chat.Value, 32f, "模组聊天室",
                    delegate { SettingUI.ToggleChat(); });
                addMethod.Invoke(null, new object[] { ChatKey, chat });

                ContentPatch.PrintTry("已挂到快速按钮: 相机是设置页, 气泡是聊天室");
            }
            catch (Exception e)
            {
                ContentPatch.PrintTry("挂到快速按钮失败: " + e.Message);
            }
        }
    }
}
