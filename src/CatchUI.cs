using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.UI;
using Utils = Terraria.Utils;

namespace NetChat
{
    /// <summary>聊天室左下侧的抓人游戏按钮: 点开/收起游戏面板</summary>
    internal sealed class UICatchToggle : UIElement
    {
        /// <summary>按钮图标用的物品 id: 3123 = PDA</summary>
        private const int IconItem = 3123;

        private readonly Action onClick;

        public UICatchToggle(Action onClick)
        {
            this.onClick = onClick;
            Width.Pixels = 70f;
            Height.Pixels = 70f;
            VAlign = 1f;
            Left.Set(-314f, 1f);
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (onClick != null) onClick();
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();
            Rectangle box = new Rectangle((int)dim.X, (int)dim.Y, (int)dim.Width, (int)dim.Height);
            bool hover = box.Contains(Main.mouseX, Main.mouseY) && !PlayerInput.IgnoreMouseInterface;

            if (hover)
            {
                Main.LocalPlayer.mouseInterface = true;
                spriteBatch.Draw(TextureAssets.MagicPixel.Value, box, new Color(90, 110, 170) * 0.3f);
                Main.instance.MouseText("抓人小游戏 (发起/加入, 抓捕者黑暗视野+轮廓提示)", 0, 0);
            }

            // 纯物品图标: 3123 = PDA, 等比缩放居中; 有游戏时染红提示
            Texture2D tex = TextureAssets.Item[IconItem].Value;
            float scale = Math.Min(dim.Width, dim.Height) * 0.7f / Math.Max(tex.Width, tex.Height);
            Vector2 center = dim.Position() + new Vector2(dim.Width, dim.Height) * 0.5f;
            spriteBatch.Draw(tex, center, null, CatchGame.HasGame ? new Color(255, 150, 150) : Color.White,
                0f, new Vector2(tex.Width, tex.Height) * 0.5f, scale, SpriteEffects.None, 0f);
        }
    }

    /// <summary>
    /// 抓人游戏面板: 发起 / 加入 / 开始 / 结束 + 轮次和倒计时显示。
    /// 画法和点歌面板同款; 按住任意空白处可全屏拖动。
    /// </summary>
    internal sealed class UICatchPanel : UIElement
    {
        private const float Row = 24f;

        private bool open;
        private readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();

        /// <summary>正在拖动面板</summary>
        private bool dragging;
        private Vector2 dragOff;

        public UICatchPanel()
        {
            Width.Pixels = 300f;
            Height.Pixels = 170f;
            Left.Pixels = 780f - Width.Pixels - 8f;
            Top.Pixels = 460f - Height.Pixels - 162f;
        }

        public void Toggle()
        {
            open = !open;
            hits.Clear();
        }

        public void Close()
        {
            if (open)
            {
                open = false;
                hits.Clear();
            }
        }

        public override bool ContainsPoint(Vector2 point)
        {
            if (!open) return false;

            CalculatedStyle dim = GetDimensions();
            return point.X >= dim.X && point.X <= dim.X + dim.Width
                && point.Y >= dim.Y && point.Y <= dim.Y + dim.Height;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            if (!open) return;

            hits.Clear();

            CalculatedStyle dim = GetDimensions();

            // 拖动进行中: 跟随鼠标, 钳在全屏范围
            if (dragging && Main.mouseLeft)
            {
                Left.Pixels = Main.mouseX + dragOff.X;
                Top.Pixels = Main.mouseY + dragOff.Y;
                ClampToScreen();
                Main.LocalPlayer.mouseInterface = true;
            }
            else
            {
                dragging = false;
            }

            ChatDraw.DrawPanel(spriteBatch, dim, "抓人游戏");

            float y = dim.Y + 26f;
            float x = dim.X + 8f;
            float w = dim.Width - 16f;

            if (CatchGame.Playing)
            {
                string role = CatchGame.ImCatcher ? "你是抓捕者(黑暗中, 鼠标处看轮廓)" : "你是逃亡者, 别被贴脸!";
                Utils.DrawBorderString(spriteBatch, role,
                    new Vector2(x, y), CatchGame.ImCatcher ? new Color(255, 150, 150) : new Color(150, 200, 255), 0.8f);
                y += Row;

                Utils.DrawBorderString(spriteBatch,
                    "第 " + CatchGame.RoundNo + " 轮 · 抓捕者: " + CatchGame.CatcherName,
                    new Vector2(x, y), new Color(165, 170, 195), 0.8f);
                y += Row;

                Utils.DrawBorderString(spriteBatch, "剩余 " + CatchGame.TimeText,
                    new Vector2(x, y), new Color(255, 210, 120), 1.0f);
                y += Row;

                if (CatchGame.IsHost || CatchGame.ImCatcher)
                {
                    Button(spriteBatch, x, y, 110f, "结束游戏",
                        delegate { CatchGame.StopGame(); }, new Color(230, 140, 140));
                }
            }
            else if (CatchGame.InLobby)
            {
                Utils.DrawBorderString(spriteBatch,
                    "等待玩家加入 (当前 " + CatchGame.MemberCount + " 人, 至少 2 人)",
                    new Vector2(x, y), new Color(165, 170, 195), 0.8f);
                y += Row;

                if (CatchGame.Joined)
                {
                    Utils.DrawBorderString(spriteBatch, "已加入, 抓捕者: " + CatchGame.CatcherName,
                        new Vector2(x, y), new Color(150, 200, 255), 0.8f);
                }
                y += Row;

                if (CatchGame.IsHost)
                {
                    Button(spriteBatch, x, y, 110f, "开始游戏",
                        delegate { CatchGame.Start(); }, new Color(140, 230, 140));
                    Button(spriteBatch, x + 118f, y, 80f, "取消",
                        delegate { CatchGame.StopGame(); }, new Color(230, 140, 140));
                }
                else if (!CatchGame.Joined)
                {
                    Button(spriteBatch, x, y, 110f, "加入游戏",
                        delegate { CatchGame.Join(); }, new Color(140, 230, 140));
                }
            }
            else
            {
                Utils.DrawBorderString(spriteBatch, "没有进行中的游戏",
                    new Vector2(x, y), Color.Gray, 0.8f);
                y += Row;

                Utils.DrawBorderString(spriteBatch,
                    "发起者=首轮抓捕者; 每轮2分钟, 抓到谁谁下一轮抓",
                    new Vector2(x, y), Color.Gray, 0.7f);
                y += Row - 6f;

                Button(spriteBatch, x, y, 130f, "发起游戏 (我来抓)",
                    delegate { CatchGame.HostOpen(); }, new Color(255, 150, 150));
            }

            // 处理点击
            if (!dragging && Main.mouseLeft && Main.mouseLeftRelease)
            {
                foreach (KeyValuePair<Rectangle, Action> h in hits)
                {
                    if (h.Key.Contains(Main.mouseX, Main.mouseY))
                    {
                        h.Value();
                        SoundEngine.PlaySound(12);
                        break;
                    }
                }
            }

            // 按住空白处开始拖动(按钮不触发), 拖动范围放开到全屏
            if (Main.mouseLeft && Main.mouseLeftRelease && !dragging)
            {
                bool overHit = false;
                foreach (KeyValuePair<Rectangle, Action> h in hits)
                {
                    if (h.Key.Contains(Main.mouseX, Main.mouseY)) { overHit = true; break; }
                }

                if (!overHit)
                {
                    dragging = true;
                    dragOff = new Vector2(Left.Pixels - Main.mouseX, Top.Pixels - Main.mouseY);
                }
            }
        }

        /// <summary>拖动后钳回屏幕范围(面板坐标相对父容器, 换算成屏幕坐标钳制后再换算回来)</summary>
        private void ClampToScreen()
        {
            if (Parent == null) return;

            Vector2 po = Parent.GetDimensions().Position();
            float ax = po.X + Left.Pixels;
            float ay = po.Y + Top.Pixels;

            ax = Math.Max(0f, Math.Min(ax, Main.screenWidth - Width.Pixels));
            ay = Math.Max(0f, Math.Min(ay, Main.screenHeight - Height.Pixels));

            Left.Pixels = ax - po.X;
            Top.Pixels = ay - po.Y;
        }

        private void Button(SpriteBatch spriteBatch, float x, float y, float w, string label, Action onClick, Color tint)
        {
            Rectangle rect = new Rectangle((int)x, (int)y, (int)w, 18);
            bool hover = rect.Contains(Main.mouseX, Main.mouseY) && !PlayerInput.IgnoreMouseInterface;

            if (hover) Main.LocalPlayer.mouseInterface = true;

            spriteBatch.Draw(TextureAssets.MagicPixel.Value, rect,
                hover ? new Color(90, 110, 170) * 0.85f : new Color(60, 70, 110) * 0.65f);
            Utils.DrawBorderString(spriteBatch, label,
                new Vector2(x + w * 0.5f - ChatText.Width(label) * 0.4f, y + 1.5f), tint, 0.8f);

            hits.Add(new KeyValuePair<Rectangle, Action>(rect, onClick));
        }
    }
}
