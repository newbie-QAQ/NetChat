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
    /// <summary>
    /// 调试构建开关: 仅本地测试 dll 设为 true。
    /// 发布前必须翻回 false 重新编译 —— 正式版不应出现调试面板。
    /// </summary>
    internal static class DebugBuild
    {
        /// <summary>true=本地调试 dll(带调试面板); 发布前必须翻回 false</summary>
        public const bool Enabled = false;
    }

    /// <summary>聊天室左下角的调试按钮: 打开/收起调试面板</summary>
    internal sealed class UIDebugToggle : UIElement
    {
        private readonly Action onClick;

        public UIDebugToggle(Action onClick)
        {
            this.onClick = onClick;
            Width.Pixels = 70f;
            Height.Pixels = 70f;
            VAlign = 1f;
            Left.Set(-386f, 1f);
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
                spriteBatch.Draw(TextureAssets.MagicPixel.Value, box, new Color(170, 130, 90) * 0.3f);
                Main.instance.MouseText("调试面板 (仅调试 dll): 强开游戏/模拟抓到/预览渲染", 0, 0);
            }

            Utils.DrawBorderString(spriteBatch, "调试",
                dim.Position() + new Vector2((dim.Width - ChatText.Width("调试") * 1.1f) * 0.5f, 10f),
                Color.Yellow, 1.1f);
        }
    }

    /// <summary>调试面板: 抓人小游戏状态查看 + 强控按钮; 画法和抓人面板同款, 可全屏拖动</summary>
    internal sealed class UIDebugPanel : UIElement
    {
        private const float Row = 24f;

        private bool open;
        private readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();

        private bool dragging;
        private Vector2 dragOff;

        public UIDebugPanel()
        {
            Width.Pixels = 320f;
            Height.Pixels = 200f;
            Left.Pixels = 780f - Width.Pixels - 90f;
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

            ChatDraw.DrawPanel(spriteBatch, dim, "调试 (仅本机生效)");

            float y = dim.Y + 26f;
            float x = dim.X + 8f;
            float w = dim.Width - 16f;

            // 状态行
            string state = !CatchGame.HasGame ? "无游戏"
                : CatchGame.Playing ? "进行中 第" + CatchGame.RoundNo + "轮 抓捕者:" + CatchGame.CatcherName
                : "大厅 " + CatchGame.MemberCount + "人";
            Utils.DrawBorderString(spriteBatch, state, new Vector2(x, y), Color.Yellow, 0.75f);
            y += Row;

            Utils.DrawBorderString(spriteBatch,
                "预览渲染: " + (CatchGame.DebugPreview ? "开" : "关") + " · 剩余 " + CatchGame.TimeText,
                new Vector2(x, y), Color.Gray, 0.75f);
            y += Row;

            // 按钮两列
            float col2 = x + w * 0.5f;

            Button(spriteBatch, x, y, w * 0.5f - 6f, "单人强开(我抓)",
                delegate { CatchGame.DebugStartSolo(); }, new Color(255, 160, 120));
            Button(spriteBatch, col2, y, w * 0.5f - 6f, "模拟抓到",
                delegate { CatchGame.DebugForceCaught(); }, new Color(255, 160, 120));
            y += Row;

            Button(spriteBatch, x, y, w * 0.5f - 6f, "预览开/关",
                delegate { CatchGame.DebugPreview = !CatchGame.DebugPreview; }, new Color(150, 210, 255));
            Button(spriteBatch, col2, y, w * 0.5f - 6f, "上/摘黑buff",
                delegate { CatchGame.DebugToggleBuff(); }, new Color(150, 210, 255));
            y += Row;

            Button(spriteBatch, x, y, w * 0.5f - 6f, "强制重置",
                delegate { CatchGame.DebugReset(); }, new Color(230, 140, 140));
            y += Row;

            // 提示
            Utils.DrawBorderString(spriteBatch, "只改本机状态, 不广播信令", new Vector2(x, y), Color.Gray, 0.7f);

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

            // 按住空白处拖动(按钮不触发), 全屏范围
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
