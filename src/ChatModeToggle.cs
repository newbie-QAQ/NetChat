using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;

namespace NetChat
{
    /// <summary>
    /// 聊天模式切换按钮: 常显, 画在原版聊天框那一行的左边空位
    /// (原版输入框画在 (78, 屏高-36), 聊天记录从 (88, 屏高-58) 往上,
    /// 按钮所在的 (48, 屏高-35) 一行开着关着都留空, 不会盖字)。
    /// 点一下在 模组聊天(只发给装了本模组的人) 和 正常聊天 之间切换,
    /// 图标用物品 3863, 模组聊天开着时全彩、关了置灰。
    ///
    /// 贴图来源: 原版 AssetInitializer 启动时就把全部物品贴图加载好了,
    /// TextureAssets.Item[3863] 直接可用; 万一没有再自己 ImmediateLoad 请求。
    /// 注意 AssetRequestMode 枚举顺序是 AsyncLoad=0, ImmediateLoad=1。
    ///
    /// 贴图就绪前画一个色块占位, 按钮位置和点击始终有效。
    /// </summary>
    internal static class ChatModeToggle
    {
        private const int ItemId = 3863;
        private const float IconSize = 24f;

        private static Rectangle rect;
        private static Asset<Texture2D> texAsset;
        private static bool requestFailed;
        private static int retryFrames;

        public static void Draw(SpriteBatch spriteBatch)
        {
            if (Main.gameMenu) return;

            rect = new Rectangle(48, Main.screenHeight - 35, 26, 26);

            bool hovered = Main.mouseX >= rect.X && Main.mouseX < rect.Right
                && Main.mouseY >= rect.Y && Main.mouseY < rect.Bottom
                && !PlayerInput.IgnoreMouseInterface;

            if (hovered) Main.LocalPlayer.mouseInterface = true;

            if (texAsset == null)
            {
                // 首选: 原版启动时预载好的那份, 直接引用同一张贴图, 不再自己加载
                Asset<Texture2D> preloaded = ItemId >= 0 && ItemId < TextureAssets.Item.Length
                    ? TextureAssets.Item[ItemId]
                    : null;

                if (preloaded != null && preloaded.Value != null && !preloaded.Value.IsDisposed)
                {
                    texAsset = preloaded;
                }
                else
                {
                    // 兜底: ImmediateLoad(=1) 同步加载; 0 是 AsyncLoad, 进游戏后没人泵永远不完成
                    if (Main.Assets == null)
                    {
                        DrawFallback(spriteBatch, new Color(60, 90, 160));
                        return;
                    }

                    if (requestFailed && retryFrames > 0)
                    {
                        retryFrames--;
                        DrawFallback(spriteBatch, new Color(160, 60, 60));
                        return;
                    }

                    try
                    {
                        texAsset = Main.Assets.Request<Texture2D>("Images/Item_" + ItemId, (AssetRequestMode)1);
                        requestFailed = false;
                    }
                    catch
                    {
                        requestFailed = true;
                        retryFrames = 600;
                        DrawFallback(spriteBatch, new Color(160, 60, 60));
                        return;
                    }
                }
            }

            Texture2D tex = texAsset.Value;
            if (tex == null)
            {
                DrawFallback(spriteBatch, new Color(160, 140, 60));
                return;
            }

            if (tex.IsDisposed)
            {
                texAsset = null;
                requestFailed = false;
                DrawFallback(spriteBatch, new Color(160, 60, 60));
                return;
            }

            float scale = MathHelper.Min(IconSize / tex.Width, IconSize / tex.Height);
            Vector2 pos = rect.Center.ToVector2() - new Vector2(tex.Width, tex.Height) * scale * 0.5f;

            // 模组聊天开 = 全彩, 关 = 置灰
            Color tint = ChatRelay.On ? Color.White : new Color(120, 120, 120);
            spriteBatch.Draw(tex, pos, null, tint, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

            Click(hovered);
        }

        /// <summary>贴图没就绪时的占位: 色块照画, 位置和点击不变</summary>
        private static void DrawFallback(SpriteBatch spriteBatch, Color color)
        {
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, rect, color * 0.7f);
            Click(Main.mouseX >= rect.X && Main.mouseX < rect.Right
                && Main.mouseY >= rect.Y && Main.mouseY < rect.Bottom
                && !PlayerInput.IgnoreMouseInterface);
        }

        private static void Click(bool hovered)
        {
            // 原版聊天链接同款判定: 按着 + 松开同帧成立才算点了一下
            if (hovered && Main.mouseLeft && Main.mouseLeftRelease)
            {
                ChatRelay.On = !ChatRelay.On;
                SoundEngine.PlaySound(12);
            }
        }
    }
}
