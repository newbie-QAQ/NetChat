using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.UI;
using Terraria.Audio;
using Utils = Terraria.Utils;

namespace NetChat
{
    /// <summary>聊天室右下角的截图按钮: 一键把当前画面发到聊天室</summary>
    internal sealed class UICameraToggle : UIElement
    {
        private readonly Action onClick;

        public UICameraToggle(Action onClick)
        {
            this.onClick = onClick;
            Width.Pixels = 66f;
            Height.Pixels = 70f;
            VAlign = 1f;
            Left.Set(-230f, 1f);
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
                Main.instance.MouseText("截图直发 (按图片设置缩放)", 0, 0);
            }

            Utils.DrawBorderString(spriteBatch, "◉",
                dim.Position() + new Vector2((dim.Width - ChatText.Width("◉") * 1.4f) * 0.5f, 6f),
                hover ? new Color(150, 220, 255) : Color.White, 1.4f);

            Utils.DrawBorderString(spriteBatch, "截图",
                dim.Position() + new Vector2((dim.Width - ChatText.Width("截图") * 0.75f) * 0.5f, dim.Height - 24f),
                Color.Gray, 0.75f);
        }
    }

    /// <summary>聊天室右下角的点歌按钮: 点开/收起点歌面板</summary>
    internal sealed class UIMusicToggle : UIElement
    {
        private readonly Action onClick;

        public UIMusicToggle(Action onClick)
        {
            this.onClick = onClick;
            Width.Pixels = 66f;
            Height.Pixels = 70f;
            VAlign = 1f;
            Left.Set(-154f, 1f);
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
                Main.instance.MouseText("点歌 (音频互发, 需对方确认)", 0, 0);
            }

            bool glow = SongChannel.IsSending || SongChannel.IsReceiving || SongChannel.Offers.Count > 0;
            string label = glow ? "♪!" : "♪";
            Utils.DrawBorderString(spriteBatch, label,
                dim.Position() + new Vector2((dim.Width - ChatText.Width(label) * 1.6f) * 0.5f, 6f),
                glow ? new Color(255, 210, 120) : Color.White, 1.6f);

            Utils.DrawBorderString(spriteBatch, "点歌",
                dim.Position() + new Vector2((dim.Width - ChatText.Width("点歌") * 0.75f) * 0.5f, dim.Height - 24f),
                Color.Gray, 0.75f);
        }
    }

    /// <summary>
    /// 点歌面板: 发送音频 / 处理收到的点歌邀请 / 曲库列表 / 播放控制。
    /// 和表情面板一个画法: 收起时不画也不吃鼠标。
    /// </summary>
    internal sealed class UIMusicPanel : UIElement
    {
        private const float Row = 24f;

        private bool open;
        private readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();

        /// <summary>正在拖音量滑条</summary>
        private bool volDragging;

        /// <summary>正在拖标题栏移动整个面板</summary>
        private bool dragTitle;
        private Vector2 dragOff;

        public UIMusicPanel()
        {
            Width.Pixels = 340f;
            Height.Pixels = 350f;
            Left.Pixels = 780f - Width.Pixels - 8f;
            Top.Pixels = 460f - Height.Pixels - 84f;
        }

        public void Toggle()
        {
            open = !open;
            hits.Clear();
        }

        /// <summary>表情面板要打开时, 把点歌面板收起(两个面板都占右下角)</summary>
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

            // 拖动进行中: 跟随鼠标, 范围钳在全屏(不再局限聊天窗口)
            if (dragTitle && Main.mouseLeft)
            {
                Left.Pixels = Main.mouseX + dragOff.X;
                Top.Pixels = Main.mouseY + dragOff.Y;
                ClampToScreen();
                Main.LocalPlayer.mouseInterface = true;
            }
            else
            {
                dragTitle = false;
            }

            ChatDraw.DrawPanel(spriteBatch, dim, "点歌");

            float y = dim.Y + 26f;
            float x = dim.X + 8f;
            float w = dim.Width - 16f;

            // 第一行: 发送音频 + 参数提示
            Button(spriteBatch, x, y, 110f, "发送音频...", delegate
            {
                string picked = Clip.PickFile("选一首歌发给在线玩家",
                    "音频文件 (*.mp3;*.wav)|*.mp3;*.wav|所有文件 (*.*)|*.*",
                    ImageSetting.LastPath);
                if (picked == null) return;

                string err = SongChannel.BeginSend(picked);
                if (err != null) ChatRoom.AddSystem("点歌失败: " + err);
            }, Color.White);

            Utils.DrawBorderString(spriteBatch, "MP3≤5MB / WAV≤8MB / ≤10分钟",
                new Vector2(x + 118f, y + 3f), Color.Gray, 0.7f);
            y += Row + 4f;

            // 收到的邀请
            if (SongChannel.Offers.Count > 0)
            {
                y = SectionTitle(spriteBatch, x, y, w, "收到的点歌邀请");
                float panelBottom = dim.Y + dim.Height - 64f;

                foreach (SongOffer offer in SongChannel.Offers.ToArray())
                {
                    if (y > panelBottom) break;

                    string who = string.IsNullOrEmpty(offer.SenderName) ? "(?)" : offer.SenderName;
                    Utils.DrawBorderString(spriteBatch, who + " 《" + Truncate(offer.Name, 16) + "》",
                        new Vector2(x, y), new Color(255, 210, 120), 0.8f);
                    Utils.DrawBorderString(spriteBatch, ImageTransfer.Format(offer.Bytes),
                        new Vector2(x + w - 76f, y), Color.Gray, 0.7f);
                    y += Row - 6f;

                    string msgId = offer.MsgId;
                    Button(spriteBatch, x, y, 64f, "接收", delegate { SongChannel.Accept(msgId); }, new Color(140, 230, 140));
                    Button(spriteBatch, x + 72f, y, 64f, "拒绝", delegate { SongChannel.Decline(msgId); }, new Color(230, 140, 140));
                    y += Row;
                }
                y += 4f;
            }

            // 传输状态
            string status = null;
            if (SongChannel.IsSending) status = "发: " + SongChannel.SendStatus;
            else if (SongChannel.IsReceiving) status = "收: " + SongChannel.RecvStatus;
            else if (SongChannel.SendStatus != "就绪") status = SongChannel.SendStatus;

            if (status != null)
            {
                Utils.DrawBorderString(spriteBatch, Truncate(status, 46), new Vector2(x, y), new Color(150, 200, 255), 0.75f);
                y += Row - 4f;
            }

            // 曲库
            y = SectionTitle(spriteBatch, x, y, w, "曲库 " + SongChannel.Library.Count + " 首");
            float listBottom = dim.Y + dim.Height - 58f;   // 底部留出音量滑条和播放状态两行

            List<SongItem> lib = SongChannel.Library;
            for (int i = 0; i < lib.Count; i++)
            {
                if (y > listBottom) break;

                SongItem song = lib[i];
                bool current = SongPlayer.Current == song;
                Color tint = current ? new Color(255, 210, 120) : Color.White;

                Utils.DrawBorderString(spriteBatch,
                    (i + 1) + ". " + Truncate(song.Name, 18) + "  "
                    + (song.Seconds / 60) + ":" + (song.Seconds % 60).ToString("00"),
                    new Vector2(x, y), tint, 0.78f);

                string playLabel = current && SongPlayer.IsPlaying ? "停止" : "播放";
                SongItem s = song;
                Button(spriteBatch, x + w - 96f, y - 2f, 42f, playLabel, delegate
                {
                    if (SongPlayer.Current == s && SongPlayer.IsPlaying) SongPlayer.Stop();
                    else if (SongPlayer.Current == s) SongPlayer.Toggle();
                    else
                    {
                        string err = SongPlayer.Play(s);
                        if (err != null) ChatRoom.AddSystem(err);
                    }
                }, tint);

                Button(spriteBatch, x + w - 48f, y - 2f, 42f, "删", delegate { SongChannel.Delete(s); },
                    new Color(230, 140, 140));

                y += Row;
            }

            // 音量滑条: 按住拖动, 播放中即时生效(改 Volume 会同步落盘)
            float barY = dim.Y + dim.Height - 48f;
            float barX = x + 34f;
            float barW = w - 104f;
            Rectangle bar = new Rectangle((int)barX, (int)barY, (int)barW, 10);
            bool barHover = bar.Contains(Main.mouseX, Main.mouseY) && !PlayerInput.IgnoreMouseInterface;

            if (barHover && Main.mouseLeft && Main.mouseLeftRelease) volDragging = true;
            if (volDragging && Main.mouseLeft)
            {
                SongPlayer.Volume = Math.Max(0f, Math.Min(1f, (Main.mouseX - barX) / barW));
                Main.LocalPlayer.mouseInterface = true;
            }
            else
            {
                volDragging = false;
            }

            if (barHover) Main.LocalPlayer.mouseInterface = true;

            Utils.DrawBorderString(spriteBatch, "音量", new Vector2(x, barY - 1f), Color.Gray, 0.75f);
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, bar, new Color(45, 55, 90) * 0.95f);
            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle(bar.X, bar.Y, (int)(barW * SongPlayer.Volume), bar.Height),
                volDragging ? new Color(160, 210, 255) * 0.95f : new Color(120, 180, 255) * 0.9f);
            Utils.DrawBorderString(spriteBatch, (int)Math.Round(SongPlayer.Volume * 100f) + "%",
                new Vector2(barX + barW + 10f, barY - 1f), Color.White, 0.75f);

            // 底部: 播放状态 + 扩展包状态
            if (SongPlayer.Current != null)
            {
                Utils.DrawBorderString(spriteBatch,
                    "♪ " + Truncate(SongPlayer.Current.Name, 22) + "  " + SongPlayer.Position(),
                    new Vector2(x, dim.Y + dim.Height - 24f), new Color(255, 210, 120), 0.75f);
            }

            // 扩展包状态: 放标题行右端, 离底部按钮行远一点(之前放底部会和按钮挤在一起)
            string extStatus = MediaExt.StatusText;
            float extWidth = ChatText.Width(extStatus) * 0.7f;
            Utils.DrawBorderString(spriteBatch, extStatus,
                new Vector2(dim.X + dim.Width - 10f - extWidth, dim.Y + 7f),
                MediaExt.Available ? new Color(140, 230, 140) : new Color(255, 150, 150), 0.7f);

            // 处理点击(拖动标题/滑条的那一下不当作按钮点击)
            if (!dragTitle && !volDragging && Main.mouseLeft && Main.mouseLeftRelease)
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

            // 按住面板任意空白处(按钮/滑条不触发)开始拖动
            if (Main.mouseLeft && Main.mouseLeftRelease && !dragTitle && !volDragging && !barHover)
            {
                bool overHit = false;
                foreach (KeyValuePair<Rectangle, Action> h in hits)
                {
                    if (h.Key.Contains(Main.mouseX, Main.mouseY)) { overHit = true; break; }
                }

                if (!overHit)
                {
                    dragTitle = true;
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

        /// <summary>画一个按钮并登记点击(不推进行坐标, 由调用方自己排)</summary>
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

        private float SectionTitle(SpriteBatch spriteBatch, float x, float y, float w, string title)
        {
            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)x, (int)y, (int)w, 1), new Color(90, 110, 170) * 0.5f);
            Utils.DrawBorderString(spriteBatch, title, new Vector2(x, y + 4f), new Color(165, 170, 195), 0.78f);
            return y + 22f;
        }

        private static string Truncate(string s, int chars)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= chars) return s ?? "";
            return s.Substring(0, chars) + "…";
        }
    }
}
