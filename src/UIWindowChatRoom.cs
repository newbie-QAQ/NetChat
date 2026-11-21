using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.UI;
using Terraria.UI.Chat;
using tContentPatch.Content.UI;

// 本文件同时 using 了 Terraria 和 tContentPatch.Content.UI, 两边都有 Utils,
// 这里全部指 Terraria.Utils(下面用到的只有 DrawBorderString)
using Utils = Terraria.Utils;

namespace NetChat
{
    /// <summary>
    /// 聊天室里用的量字/断行。
    /// 量字走 ChatManager.GetStringSize(游戏自己的方法), 不直接调 ReLogic 的 MeasureString:
    /// 工程引用的那份 ReLogic 和运行期内嵌的那份签名不一样, 直接调会抛 MissingMethodException。
    /// </summary>
    internal static class ChatText
    {
        public const float LineHeight = 17f;

        public static float Width(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            return ChatManager.GetStringSize(FontAssets.MouseText.Value, s, Vector2.One).X;
        }

        /// <summary>英文按词断, 中文按字断, 换行符按硬换行</summary>
        public static List<string> Wrap(string text, float maxWidth)
        {
            List<string> lines = new List<string>();

            if (string.IsNullOrEmpty(text))
            {
                lines.Add("");
                return lines;
            }

            StringBuilder line = new StringBuilder();
            float lineW = 0f;

            foreach (string token in Tokenize(text))
            {
                if (token == "\n")
                {
                    lines.Add(line.ToString());
                    line.Length = 0;
                    lineW = 0f;
                    continue;
                }

                float w = Width(token);

                if (lineW + w > maxWidth && line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Length = 0;
                    lineW = 0f;

                    // 断完行首不挂空格
                    if (token == " ") continue;
                }

                line.Append(token);
                lineW += w;
            }

            if (line.Length > 0) lines.Add(line.ToString());
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        private static List<string> Tokenize(string text)
        {
            List<string> list = new List<string>();
            StringBuilder word = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '\n')
                {
                    if (word.Length > 0) { list.Add(word.ToString()); word.Length = 0; }
                    list.Add("\n");
                    continue;
                }

                // 0x2E80 之后基本是中日韩那一堆, 一个字就能断一行
                if (c == ' ' || c >= 0x2E80)
                {
                    if (word.Length > 0) { list.Add(word.ToString()); word.Length = 0; }
                    list.Add(c.ToString());
                }
                else
                {
                    word.Append(c);
                }
            }

            if (word.Length > 0) list.Add(word.ToString());
            return list;
        }
    }

    internal static class ChatDraw
    {
        public const float Box = 28f;

        public static void DrawPanel(SpriteBatch spriteBatch, CalculatedStyle dim, string title)
        {
            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)dim.X, (int)dim.Y, (int)dim.Width, (int)dim.Height), new Color(20, 20, 30) * 0.55f);

            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)dim.X, (int)dim.Y + 22, (int)dim.Width, 1), new Color(90, 110, 170) * 0.7f);

            Utils.DrawBorderString(spriteBatch, title, new Vector2(dim.X + 8f, dim.Y + 5f), Color.White, 0.8f);
        }

        /// <summary>头像框, 没头像就是空的深灰方块</summary>
        public static void DrawAvatar(SpriteBatch spriteBatch, Vector2 pos, Texture2D tex)
        {
            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)pos.X, (int)pos.Y, (int)Box, (int)Box), new Color(30, 30, 45) * 0.7f);

            if (tex == null || tex.IsDisposed) return;

            float scale = Math.Min(Box / tex.Width, Box / tex.Height);
            Vector2 size = new Vector2(tex.Width, tex.Height) * scale;
            spriteBatch.Draw(tex, pos + new Vector2((Box - size.X) * 0.5f, (Box - size.Y) * 0.5f),
                null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        /// <summary>把原版表情图集的一帧画成 size x size 的方块</summary>
        public static void DrawEmote(SpriteBatch spriteBatch, Vector2 pos, int emoteId, float size, bool animated = true)
        {
            if (!ChatEmoji.IsValid(emoteId)) return;

            // 原版 EmoteBubble.Update 的节奏: 每 8 tick 在两帧间交替
            int animFrame = animated ? (int)(Main.GameUpdateCount / 8) % 2 : 0;
            Texture2D tex = ChatEmoji.Sheet;
            Rectangle frame = ChatEmoji.Frame(emoteId, animFrame);
            float scale = Math.Min(size / frame.Width, size / frame.Height);
            spriteBatch.Draw(tex, pos, frame, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }

    /// <summary>左列: 打了招呼(所以装了本模组)的人, 自己置顶</summary>
    internal sealed class UIPeerList : UIElement
    {
        private const float RowHeight = 40f;

        public UIPeerList()
        {
            Width.Pixels = 244f;
            Height.Set(-26f, 1f);
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();
            ChatDraw.DrawPanel(spriteBatch, dim, "在线玩家 " + (Presence.Peers.Count + 1) + " 人");

            int rows = Math.Max(1, (int)((dim.Height - 26f) / RowHeight));
            int total = Presence.Peers.Count + 1;

            for (int i = 0; i < rows && i < total; i++)
            {
                Vector2 pos = new Vector2(dim.X + 8f, dim.Y + 28f + i * RowHeight);

                if (i == 0)
                {
                    string me = ProfileSetting.CurrentName();
                    ChatDraw.DrawAvatar(spriteBatch, pos, Avatar.SelfPreview != null ? Avatar.SelfPreview : Avatar.Get(me));
                    Utils.DrawBorderString(spriteBatch, me + " (我)", pos + new Vector2(ChatDraw.Box + 6f, 0f), Color.White, 0.85f);
                    Utils.DrawBorderString(spriteBatch, "打招呼 " + Presence.SentHello + " 次",
                        pos + new Vector2(ChatDraw.Box + 6f, 15f), Color.Gray, 0.7f);
                }
                else
                {
                    Peer peer = Presence.Peers[i - 1];
                    string name = Presence.DisplayName(peer);
                    bool selected = ChatRoom.WhisperTarget == peer;

                    // 点一行选/取消私聊对象; 高亮底子提示当前选中
                    Rectangle rowRect = new Rectangle((int)dim.X, (int)(dim.Y + 28f + (i - 1) * RowHeight), (int)dim.Width, (int)RowHeight);
                    bool hover = rowRect.Contains(Main.mouseX, Main.mouseY) && !PlayerInput.IgnoreMouseInterface;
                    if (hover)
                    {
                        Main.LocalPlayer.mouseInterface = true;
                        spriteBatch.Draw(TextureAssets.MagicPixel.Value, rowRect, new Color(90, 110, 170) * 0.25f);

                        if (Main.mouseLeft && Main.mouseLeftRelease) ChatRoom.ToggleWhisper(peer);
                    }

                    if (selected)
                    {
                        spriteBatch.Draw(TextureAssets.MagicPixel.Value, rowRect, new Color(255, 150, 200) * 0.15f);
                    }

                    ChatDraw.DrawAvatar(spriteBatch, pos, Avatar.Get(name));
                    Utils.DrawBorderString(spriteBatch, name, pos + new Vector2(ChatDraw.Box + 6f, 0f),
                        selected ? new Color(255, 150, 200) : Color.White, 0.85f);

                    string sub = selected ? "私聊对象 (再点取消)"
                        : (string.IsNullOrEmpty(peer.RoleName) ? "槽位 " + peer.Slot : peer.RoleName);
                    Utils.DrawBorderString(spriteBatch, sub + "  " + peer.AgeSeconds + "秒前",
                        pos + new Vector2(ChatDraw.Box + 6f, 15f), Color.Gray, 0.7f);
                }
            }
        }
    }

    /// <summary>右列: 模组聊天记录, 每条前面是头像、用户名和时间</summary>
    internal sealed class UIChatLog : UIElement
    {
        private const float HeadHeight = ChatText.LineHeight;
        private const float RowGap = 5f;

        private int wheelPrev;
        private float scrollUp;
        private float prevTotal;

        /// <summary>本帧画出来的消息命中区, 用来做悬停高亮和右键复制</summary>
        private readonly List<Note> hitNotes = new List<Note>();
        private readonly List<Rectangle> hitRects = new List<Rectangle>();

        /// <summary>刚复制过的消息和时刻, 两秒内悬停在上面显示"已复制"</summary>
        private Note copiedNote;
        private DateTime copiedAt = DateTime.MinValue;

        public UIChatLog()
        {
            Left.Pixels = 252f;
            Width.Set(-260f, 1f);
            Height.Set(-26f, 1f);
            wheelPrev = Mouse.GetState().ScrollWheelValue;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            int wheel = Mouse.GetState().ScrollWheelValue;
            int delta = wheel - wheelPrev;
            wheelPrev = wheel;

            if (delta != 0 && IsMouseHovering)
            {
                scrollUp += delta / 3f;
            }
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();
            ChatDraw.DrawPanel(spriteBatch, dim, "聊天记录 " + ChatRoom.Notes.Count + " 条 (最多留 " + ChatRoom.MaxNotes + " 条)");

            float textWidth = dim.Width - ChatDraw.Box - 22f;
            float viewTop = dim.Y + 26f;
            float viewHeight = dim.Height - 30f;

            hitNotes.Clear();
            hitRects.Clear();

            float total = 0f;
            for (int i = 0; i < ChatRoom.Notes.Count; i++)
            {
                total += HeightOf(ChatRoom.Notes[i], textWidth) + RowGap;
            }

            // 往上翻的时候, 新到的消息不该把正在看的那几行顶走
            if (total > prevTotal && scrollUp > 1f) scrollUp += total - prevTotal;
            prevTotal = total;

            float maxUp = Math.Max(0f, total - viewHeight);
            if (scrollUp > maxUp) scrollUp = maxUp;
            if (scrollUp < 0f) scrollUp = 0f;

            // 不满一屏时 start 会是 0, 从第一条开始排; start 是视口上方滚掉的内容量, 光标要从 -start 起步
            float start = Math.Max(0f, total - viewHeight - scrollUp);
            float cursor = -start;
            float clipTop = viewTop;
            float clipBottom = viewTop + viewHeight;

            for (int i = 0; i < ChatRoom.Notes.Count; i++)
            {
                Note note = ChatRoom.Notes[i];
                float height = HeightOf(note, textWidth);

                float rowTop = viewTop + cursor;
                cursor += height + RowGap;

                if (rowTop + height < clipTop) continue;
                if (rowTop > clipBottom) break;

                // 同一个人短时间内连发: 只有第一条画头像和名字, 后面的只画正文
                bool showHead = i == 0 || !Mergeable(ChatRoom.Notes[i - 1], note);
                DrawNote(spriteBatch, note, new Vector2(dim.X + 8f, rowTop), textWidth, clipTop, clipBottom, showHead);

                // 可见的整条都记进命中区(裁剪出上沿的不记, 免得点到压扁的那半条)
                if (rowTop >= clipTop && rowTop + height <= clipBottom)
                {
                    hitNotes.Add(note);
                    hitRects.Add(new Rectangle((int)dim.X, (int)rowTop, (int)dim.Width, (int)(height + RowGap)));
                }
            }

            HandleCopy(spriteBatch, dim);
            DrawTransferBar(spriteBatch, dim);

            string tip = scrollUp > 1f
                ? "往上翻了 " + (int)scrollUp + " 像素, 滚回底部就跟得上最新"
                : "跟着最新一条";
            Utils.DrawBorderString(spriteBatch, tip, new Vector2(dim.X + 8f, viewTop + viewHeight + 2f), Color.Gray, 0.7f);
        }

        /// <summary>悬停高亮 + 复制正文: 悬停时右端出现"复制"按钮, 左键或右键都能复制</summary>
        private void HandleCopy(SpriteBatch spriteBatch, CalculatedStyle dim)
        {
            if (hitNotes.Count == 0) return;

            int hover = -1;
            for (int i = 0; i < hitRects.Count; i++)
            {
                if (hitRects[i].Contains(Main.mouseX, Main.mouseY)) { hover = i; break; }
            }

            bool showCopied = copiedNote != null
                && (DateTime.Now - copiedAt).TotalSeconds < 2.0
                && hover >= 0 && hitNotes[hover] == copiedNote;

            if (hover >= 0)
            {
                Note note = hitNotes[hover];
                bool copyable = !note.IsSystem && note.Emote < 0 && !string.IsNullOrEmpty(note.Text);

                // 悬停高亮: 一条浅色底
                spriteBatch.Draw(TextureAssets.MagicPixel.Value, hitRects[hover], new Color(90, 110, 170) * 0.18f);

                if (copyable)
                {
                    Main.LocalPlayer.mouseInterface = true;

                    Rectangle btn = new Rectangle(hitRects[hover].Right - 38, hitRects[hover].Y + 1, 36, 14);
                    bool hoverBtn = btn.Contains(Main.mouseX, Main.mouseY);

                    spriteBatch.Draw(TextureAssets.MagicPixel.Value, btn,
                        hoverBtn ? new Color(90, 110, 170) * 0.85f : new Color(60, 70, 110) * 0.7f);
                    Utils.DrawBorderString(spriteBatch, "复制", new Vector2(btn.X + 8f, btn.Y + 0.5f), Color.White, 0.75f);

                    bool click = (Main.mouseLeft && Main.mouseLeftRelease && hoverBtn)
                              || (Main.mouseRight && Main.mouseRightRelease && !hoverBtn);

                    if (click)
                    {
                        if (Clip.SetText(note.Text))
                        {
                            copiedNote = note;
                            copiedAt = DateTime.Now;
                            SoundEngine.PlaySound(12);
                        }
                    }

                    string tipText = showCopied ? "已复制" : "点击复制";
                    Main.instance.MouseText(tipText, 0, 0);
                }
            }
        }

        /// <summary>
        /// 面板底部固定的传输进度条: 自己发图显示"发送 xx%", 对方发图显示"对方发送 xx%",
        /// 自己在发时右侧多一个取消按钮。
        /// </summary>
        private void DrawTransferBar(SpriteBatch spriteBatch, CalculatedStyle dim)
        {
            SendJob send = ImageTransfer.Sending;
            bool sending = send != null && !send.Done;
            bool receiving = ImageTransfer.Receiving != null;

            if (!sending && !receiving) return;

            float barY = dim.Y + dim.Height - 18f;
            float barW = dim.Width - (sending ? 46f : 8f);
            Rectangle back = new Rectangle((int)(dim.X + 8f), (int)barY, (int)barW, 14);

            spriteBatch.Draw(TextureAssets.MagicPixel.Value, back, new Color(10, 10, 20) * 0.75f);

            float p;
            string label;
            Color fill;

            if (sending)
            {
                p = ImageTransfer.SendProgress();
                label = "发送 " + (p < 0f ? "..." : (int)(p * 100f) + "%") + "  " + send.Name;
                fill = new Color(90, 160, 255);
            }
            else
            {
                RecvJob recv = ImageTransfer.Receiving;
                p = recv.Count <= 0 ? 0f : (float)recv.Got / recv.Count;
                label = "对方发送图片 " + (int)(p * 100f) + "%  " + recv.Name;
                fill = new Color(120, 220, 140);
            }

            if (p > 0f)
            {
                if (p > 1f) p = 1f;
                spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                    new Rectangle(back.X, back.Y, (int)(back.Width * p), back.Height), fill * 0.5f);
            }

            Utils.DrawBorderString(spriteBatch, label, new Vector2(back.X + 4f, barY + 1f), Color.White, 0.75f);

            // 取消按钮: 只管自己这边的发送
            if (sending)
            {
                Rectangle btn = new Rectangle((int)(dim.X + dim.Width - 42f), (int)barY - 1, 40, 16);
                bool hoverBtn = btn.Contains(Main.mouseX, Main.mouseY) && !PlayerInput.IgnoreMouseInterface;

                spriteBatch.Draw(TextureAssets.MagicPixel.Value, btn,
                    hoverBtn ? new Color(200, 80, 80) * 0.8f : new Color(120, 60, 60) * 0.7f);
                Utils.DrawBorderString(spriteBatch, "取消", new Vector2(btn.X + 9f, btn.Y + 1f),
                    hoverBtn ? Color.White : new Color(255, 190, 190), 0.8f);

                if (hoverBtn)
                {
                    Main.LocalPlayer.mouseInterface = true;

                    if (Main.mouseLeft && Main.mouseLeftRelease)
                    {
                        ImageTransfer.CancelSend();
                        SoundEngine.PlaySound(12);
                    }
                }
            }
        }

        private void DrawNote(SpriteBatch spriteBatch, Note note, Vector2 pos, float textWidth, float clipTop, float clipBottom, bool showHead)
        {
            // 系统提示: 灰字不缩进, 不带头像不画名字
            if (note.IsSystem)
            {
                string[] sysLines = Lines(note, textWidth);
                for (int i = 0; i < sysLines.Length; i++)
                {
                    float y = pos.Y + i * ChatText.LineHeight;
                    if (y < clipTop || y + ChatText.LineHeight > clipBottom) continue;

                    Utils.DrawBorderString(spriteBatch, sysLines[i], new Vector2(pos.X, y), new Color(165, 170, 195), 0.85f);
                }
                return;
            }

            // 半条滚出上沿时只画露全的那几样, 免得压到面板标题; 连发合并时也跳过
            if (showHead && pos.Y >= clipTop)
            {
                ChatDraw.DrawAvatar(spriteBatch, pos, ChatRoom.AvatarFor(note));

                string head;
                Color nameColor;

                if (note.IsPrivate)
                {
                    head = note.Mine ? "[私聊→" + note.TargetName + "]"
                                     : "[私聊] " + (string.IsNullOrEmpty(note.User) ? "(没名字)" : note.User);
                    nameColor = new Color(255, 150, 200);
                }
                else
                {
                    head = string.IsNullOrEmpty(note.User) ? "(没名字)" : note.User;
                    nameColor = note.Mine ? new Color(120, 220, 255) : Color.White;
                }

                Utils.DrawBorderString(spriteBatch, head, pos + new Vector2(ChatDraw.Box + 6f, 0f), nameColor, 0.85f);
                Utils.DrawBorderString(spriteBatch, note.Clock,
                    pos + new Vector2(ChatDraw.Box + 12f + ChatText.Width(head), 1f), Color.Gray, 0.7f);
            }

            string[] lines = Lines(note, textWidth);
            for (int i = 0; i < lines.Length; i++)
            {
                float y = pos.Y + HeadHeight + i * ChatText.LineHeight;
                if (y < clipTop || y + ChatText.LineHeight > clipBottom) continue;

                Utils.DrawBorderString(spriteBatch, lines[i], new Vector2(pos.X + ChatDraw.Box + 6f, y), Color.White, 0.9f);
            }

            if (note.Emote >= 0)
            {
                ChatDraw.DrawEmote(spriteBatch, new Vector2(pos.X + ChatDraw.Box + 6f, pos.Y + HeadHeight), note.Emote, 22f);
            }
        }

        /// <summary>两条能不能算同人连发: 同一发送者、都是文字消息、同为(非)私聊、间隔 3 分钟内。合并只是不重复画头像和名字。</summary>
        private static bool Mergeable(Note prev, Note note)
        {
            if (prev == null || note == null) return false;
            if (prev.IsSystem || note.IsSystem) return false;
            if (prev.Emote >= 0 || note.Emote >= 0) return false;
            if (prev.IsPrivate != note.IsPrivate) return false;
            if (prev.Mine != note.Mine || prev.User != note.User) return false;
            return (note.Time - prev.Time).TotalMinutes < 3.0;
        }

        private static string[] Lines(Note note, float textWidth)
        {
            if (note.WrappedLines == null || note.WrappedWidth != textWidth)
            {
                note.WrappedWidth = textWidth;
                note.WrappedLines = ChatText.Wrap(note.Text, textWidth).ToArray();
            }
            return note.WrappedLines;
        }

        /// <summary>表情消息没有正文, 就一行图标; 系统提示没有名字行</summary>
        private static float HeightOf(Note note, float textWidth)
        {
            if (note.IsSystem) return Lines(note, textWidth).Length * ChatText.LineHeight;
            if (note.Emote >= 0) return HeadHeight + 24f;
            return HeadHeight + Lines(note, textWidth).Length * ChatText.LineHeight;
        }
    }

    /// <summary>底下那行提示, 右边留出表情按钮的位置</summary>
    internal sealed class UIChatFooter : UIElement
    {
        public UIChatFooter()
        {
            Left.Pixels = 8f;
            Width.Set(-86f, 1f);
            Height.Pixels = 70f;
            VAlign = 1f;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();

            if (ChatRoom.WhisperTarget != null)
            {
                Utils.DrawBorderString(spriteBatch,
                    "私聊中 → " + Presence.DisplayName(ChatRoom.WhisperTarget) + " (消息只发给他, 点玩家列表再点一下取消)",
                    new Vector2(dim.X, dim.Y + dim.Height - 34f), new Color(255, 150, 200), 0.75f);
            }

            string text = "说话还是按 Enter; 点在线玩家开私聊; 要进这个列表得先开「聊天只发给装了本模组的人」; 打招呼: " + Presence.Status;
            Utils.DrawBorderString(spriteBatch, text, new Vector2(dim.X, dim.Y + dim.Height - 18f), Color.Gray, 0.75f);
        }
    }

    /// <summary>聊天室右下角的表情按钮: 点开/收起表情面板</summary>
    internal sealed class UIEmojiToggle : UIElement
    {
        private readonly Action onClick;

        public UIEmojiToggle(Action onClick)
        {
            this.onClick = onClick;
            Width.Pixels = 70f;
            Height.Pixels = 70f;
            VAlign = 1f;
            Left.Set(-78f, 1f);
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (onClick != null) onClick();
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();
            ChatDraw.DrawEmote(spriteBatch, dim.Position() + new Vector2((dim.Width - 50f) * 0.5f, (dim.Height - 50f) * 0.5f), 0, 50f, animated: false);
        }
    }

    /// <summary>
    /// 表情选择面板: 聊天室右下角弹出, 网格摆原版全部 151 个表情,
    /// 滚轮翻页, 点一个发一个。收起时不画也不吃鼠标。
    /// </summary>
    internal sealed class UIEmojiPicker : UIElement
    {
        private const int Cols = 8;
        private const float Cell = 22f;
        private const int VisibleRows = 10;

        private bool open;
        private int wheelPrev;
        private float scrollUp;

        public UIEmojiPicker()
        {
            Width.Pixels = Cols * Cell + 10f;
            Height.Pixels = VisibleRows * Cell + 26f;
            Left.Pixels = 780f - Width.Pixels - 8f;
            Top.Pixels = 460f - Height.Pixels - 78f;
            wheelPrev = Mouse.GetState().ScrollWheelValue;
        }

        public void Toggle()
        {
            open = !open;
        }

        /// <summary>点歌面板要打开时, 把表情面板收起(两个面板都占右下角)</summary>
        public void Close()
        {
            open = false;
        }

        /// <summary>收起时把整个面板从命中测试里摘掉, 免得挡住下面的聊天记录</summary>
        public override bool ContainsPoint(Vector2 point)
        {
            if (!open) return false;

            CalculatedStyle dim = GetDimensions();
            return point.X >= dim.X && point.X <= dim.X + dim.Width
                && point.Y >= dim.Y && point.Y <= dim.Y + dim.Height;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (!open) return;

            int wheel = Mouse.GetState().ScrollWheelValue;
            int delta = wheel - wheelPrev;
            wheelPrev = wheel;

            if (delta != 0 && IsMouseHovering) scrollUp += delta / 4f;

            int totalRows = (ChatEmoji.Count + Cols - 1) / Cols;
            float maxUp = Math.Max(0f, totalRows - VisibleRows);
            if (scrollUp > maxUp) scrollUp = maxUp;
            if (scrollUp < 0f) scrollUp = 0f;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            if (!open) return;

            CalculatedStyle dim = GetDimensions();
            ChatDraw.DrawPanel(spriteBatch, dim, "表情 (点击发送, 滚轮翻页)");

            int totalRows = (ChatEmoji.Count + Cols - 1) / Cols;
            int startRow = (int)scrollUp;
            float viewTop = dim.Y + 24f;
            int hoverId = HoveredEmote(dim);

            for (int row = startRow; row < totalRows; row++)
            {
                float y = viewTop + (row - scrollUp) * Cell;
                if (y + Cell > dim.Y + dim.Height) break;

                for (int col = 0; col < Cols; col++)
                {
                    int id = row * Cols + col;
                    if (id >= ChatEmoji.Count) break;

                    Vector2 pos = new Vector2(dim.X + 5f + col * Cell, y);

                    if (id == hoverId)
                    {
                        spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                            new Rectangle((int)pos.X, (int)pos.Y, (int)Cell - 2, (int)Cell - 2), new Color(90, 110, 170) * 0.8f);
                    }

                    ChatDraw.DrawEmote(spriteBatch, pos + new Vector2(2f, 2f), id, Cell - 6f);
                }
            }

            if (hoverId >= 0)
            {
                Main.instance.MouseText("#" + hoverId + " " + ChatEmoji.Name(hoverId), 0, 0);
            }
        }

        /// <summary>鼠标悬停在第几个表情上, 没有返回 -1</summary>
        private int HoveredEmote(CalculatedStyle dim)
        {
            if (!IsMouseHovering) return -1;

            Vector2 mouse = Main.MouseScreen;
            float x = mouse.X - (dim.X + 5f);
            float y = mouse.Y - (dim.Y + 24f);

            if (x < 0f || y < 0f) return -1;

            int col = (int)(x / Cell);
            int row = (int)((y + scrollUp * Cell) / Cell);
            if (x - col * Cell >= Cell || y - (row - scrollUp) * Cell >= Cell) return -1;
            if (col >= Cols || row < 0) return -1;

            int id = row * Cols + col;
            return id < ChatEmoji.Count ? id : -1;
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (!open) return;

            int id = HoveredEmote(GetDimensions());
            if (id < 0) return;

            open = false;
            Demo.SendEmote(ProfileSetting.CurrentName(), id);
        }
    }

    /// <summary>聊天室窗口: 左边在线玩家, 右边聊天记录</summary>
    internal sealed class UIWindowChatRoom : UIWindow
    {
        public UIWindowChatRoom() : base("模组聊天室", 780, 460)
        {
            UIPeerList peers = new UIPeerList();
            peers.Top.Pixels = 2f;

            UIChatLog log = new UIChatLog();
            log.Top.Pixels = 2f;

            UIEmojiPicker picker = new UIEmojiPicker();
            UIMusicPanel musicPanel = new UIMusicPanel();

            UIEmojiToggle toggle = new UIEmojiToggle(delegate { picker.Toggle(); musicPanel.Toggle(); });
            UIMusicToggle musicToggle = new UIMusicToggle(delegate { musicPanel.Toggle(); picker.Close(); });
            UICameraToggle cameraToggle = new UICameraToggle(delegate { Screenshot.Request(); });

            Child.Append(peers);
            Child.Append(log);
            Child.Append(new UIChatFooter());
            Child.Append(toggle);
            Child.Append(picker);
            Child.Append(musicToggle);
            Child.Append(musicPanel);
            Child.Append(cameraToggle);

            OnOpen += ChatRoom.MarkRead;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (IsOpen) ChatRoom.MarkRead();
        }
    }
}
