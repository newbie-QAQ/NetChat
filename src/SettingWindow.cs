using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.UI;
using tContentPatch.Content.UI;
using tContentPatch.Content.UI.ModSet;

namespace NetChat
{
    /// <summary>
    /// 图片预览: 发出去或收到的那张图按比例画在框里。
    /// </summary>
    internal sealed class UIImagePreview : UIElement
    {
        public UIImagePreview()
        {
            Width.Precent = 1f;
            Height.Pixels = 120f;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();
            Texture2D tex = ImageTransfer.Preview;

            spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                new Rectangle((int)dim.X, (int)dim.Y, (int)dim.Width, (int)dim.Height), new Color(30, 30, 45) * 0.6f);

            if (tex == null || tex.IsDisposed)
            {
                Terraria.Utils.DrawBorderString(spriteBatch, "暂无图片", dim.Center(), Color.Gray, 0.8f, 0.5f, 0.5f);
                return;
            }

            float scale = Math.Min(dim.Width / tex.Width, dim.Height / tex.Height);
            if (scale > 8f) scale = 8f;
            if (scale <= 0f) return;

            Vector2 size = new Vector2(tex.Width, tex.Height) * scale;
            spriteBatch.Draw(tex, dim.Center() - size * 0.5f, null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

            Terraria.Utils.DrawBorderString(spriteBatch, ImageTransfer.PreviewName, new Vector2(dim.X + 2f, dim.Y + 2f), Color.White, 0.6f);
        }
    }

    /// <summary>
    /// 头像预览: 左边自己的, 右边最近收到的, 底下各标一行字, 右边再列一下收到过谁。
    /// </summary>
    internal sealed class UIAvatarPreview : UIElement
    {
        public UIAvatarPreview()
        {
            Width.Precent = 1f;
            Height.Pixels = 118f;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            CalculatedStyle dim = GetDimensions();

            float box = 100f;
            float top = dim.Y + 4f;

            DrawOne(spriteBatch, dim.X, top, box, Avatar.SelfPreview, "自己的头像");

            string right = string.IsNullOrEmpty(Avatar.LastName) ? "最近收到的头像" : Avatar.LastName;
            DrawOne(spriteBatch, dim.X + box + 12f, top, box, Avatar.LastReceived, right);

            Vector2 textPos = new Vector2(dim.X + box * 2f + 28f, top + 6f);
            Terraria.Utils.DrawBorderString(spriteBatch, "收到过 " + Avatar.Count + " 人", textPos, Color.White, 0.7f);
            Terraria.Utils.DrawBorderString(spriteBatch, Clip(Avatar.Names()), textPos + new Vector2(0f, 18f), Color.Gray, 0.6f);
            Terraria.Utils.DrawBorderString(spriteBatch, "存到: " + Avatar.Folder, textPos + new Vector2(0f, 36f), Color.Gray, 0.6f);
        }

        private static void DrawOne(SpriteBatch spriteBatch, float x, float y, float size, Texture2D tex, string label)
        {
            spriteBatch.Draw(TextureAssets.MagicPixel.Value, new Rectangle((int)x, (int)y, (int)size, (int)size),
                new Color(30, 30, 45) * 0.6f);

            if (tex != null && !tex.IsDisposed)
            {
                float scale = Math.Min(size / tex.Width, size / tex.Height);
                Vector2 drawSize = new Vector2(tex.Width, tex.Height) * scale;
                spriteBatch.Draw(tex, new Vector2(x + (size - drawSize.X) * 0.5f, y + (size - drawSize.Y) * 0.5f),
                    null, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            }

            Terraria.Utils.DrawBorderString(spriteBatch, label, new Vector2(x, y + size + 2f), Color.White, 0.65f);
        }

        /// <summary>名单太长就掐一下, 免得画出框外</summary>
        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 32 ? s : s.Substring(0, 32) + "...";
        }
    }

    /// <summary>
    /// 设置页: 聊天转发、用户名和头像、图片传输, 从快速按钮点开。
    /// 一行一项, 全用加载器自带的那些行控件, 和别的模组设置页对齐。
    /// </summary>
    internal sealed class SettingWindow : UIWindow
    {
        private const float ButtonWidth = 130f;
        private const float RowHeight = 30f;

        private readonly UIItemTextBox nameBox;
        private readonly UIItemTextBox avatarBox;
        private readonly UIItemTextBox imagePathBox;
        private readonly UIItemTextBox maxSideBox;
        private readonly UIItemTextBox qualityBox;
        private readonly UIItemTextBox slicesBox;

        private readonly UIItem lineChat;
        private readonly UIItem lineAvatar;
        private readonly UIItem lineImage;
        private readonly UIItem lineNet;
        private readonly UIItem linePresence;

        public SettingWindow() : base("通信示例 设置", 520, 640)
        {
            UIScrollViewer2 scroll = new UIScrollViewer2();
            scroll.Width.Precent = 1f;
            scroll.Height.Precent = 1f;
            scroll.ItemMargin = 2;
            Child.Append(scroll);

            // ---- 聊天 ----
            scroll.AddChild(Title("聊天"));
            scroll.AddChild(Switch("聊天只发给装了本模组的人",
                delegate { return ChatRelay.On; },
                delegate (bool v) { Setting.Set(v); },
                "开了之后聊天不再发给服务器, 这些话只在聊天室窗口里看得到"));
            nameBox = new UIItemTextBox(ProfileSetting.UserName, ProfileSetting.NameMaxLength, null, "用户名");
            nameBox.TextBoxWidth = new StyleDimension(0f, 0.62f);
            nameBox.OnLostFocus = delegate
            {
                ProfileSetting.UserName = ProfileSetting.CleanName(nameBox.GetText());
                nameBox.SetText(ProfileSetting.UserName);
                ProfileSetting.Persist();
            };
            scroll.AddChild(nameBox);

            // ---- 头像 ----
            scroll.AddChild(Title("头像"));
            scroll.AddChild(Switch("进世界自动发送头像",
                delegate { return ProfileSetting.AutoSendAvatar; },
                delegate (bool v) { ProfileSetting.AutoSendAvatar = v; ProfileSetting.Persist(); },
                "发出去的是 200x200 的方图, 对方存成同名文件"));
            avatarBox = new UIItemTextBox(ProfileSetting.AvatarPath, -1, null, "头像图片");
            avatarBox.TextBoxWidth = new StyleDimension(0f, 0.62f);
            avatarBox.OnLostFocus = delegate
            {
                ProfileSetting.AvatarPath = avatarBox.GetText().Trim();
                avatarBox.SetText(ProfileSetting.AvatarPath);
                ProfileSetting.Persist();
            };
            scroll.AddChild(avatarBox);
            scroll.AddChild(ButtonRow(
                Button("浏览...", delegate { Pick(avatarBox); }),
                Button("立刻发送头像", delegate { SendAvatar(); }),
                Button("打开头像文件夹", delegate { Avatar.OpenFolder(); })));
            scroll.AddChild(new UIAvatarPreview());

            // ---- 图片 ----
            scroll.AddChild(Title("图片"));
            imagePathBox = new UIItemTextBox(ImageSetting.LastPath, -1, null, "图片路径");
            imagePathBox.TextBoxWidth = new StyleDimension(0f, 0.62f);
            imagePathBox.OnLostFocus = delegate
            {
                ImageSetting.LastPath = imagePathBox.GetText().Trim();
                ImageSetting.Persist();
            };
            scroll.AddChild(imagePathBox);
            scroll.AddChild(ButtonRow(
                Button("浏览...", delegate { Pick(imagePathBox); }),
                Button("发送图片", delegate { SendImage(); }),
                Button("打开接收目录", delegate { ImageTransfer.OpenRecvFolder(); })));
            scroll.AddChild(new UIImagePreview());
            maxSideBox = new UIItemTextBox(ImageSetting.MaxSide.ToString(), -1, null, "最大边长");
            maxSideBox.TextBoxWidth = new StyleDimension(0f, 0.62f);
            maxSideBox.OnLostFocus = delegate
            {
                int v;
                if (int.TryParse(maxSideBox.GetText(), out v)) ImageSetting.MaxSide = v;
                ImageSetting.Persist();
                maxSideBox.SetText(ImageSetting.MaxSide.ToString());
            };
            scroll.AddChild(maxSideBox);
            qualityBox = new UIItemTextBox(ImageSetting.Quality.ToString(), -1, null, "质量");
            qualityBox.TextBoxWidth = new StyleDimension(0f, 0.62f);
            qualityBox.OnLostFocus = delegate
            {
                int v;
                if (int.TryParse(qualityBox.GetText(), out v)) ImageSetting.Quality = v;
                ImageSetting.Persist();
                qualityBox.SetText(ImageSetting.Quality.ToString());
            };
            scroll.AddChild(qualityBox);
            slicesBox = new UIItemTextBox(ImageSetting.Slices.ToString(), -1, null, "每帧发送片数");
            slicesBox.TextBoxWidth = new StyleDimension(0f, 0.62f);
            Hover(slicesBox, "1~64, 越大传得越快, 占带宽也越多; 默认 8");
            slicesBox.OnLostFocus = delegate
            {
                int v;
                if (!int.TryParse(slicesBox.GetText(), out v)) v = ImageSetting.Slices;
                ImageSetting.Slices = v;
                ImageSetting.Persist();
                ImageSetting.PushSlices();
                slicesBox.SetText(ImageSetting.Slices.ToString());
            };
            scroll.AddChild(slicesBox);

            // ---- 状态 ----
            scroll.AddChild(Title("状态"));
            lineChat = Line();
            lineAvatar = Line();
            lineImage = Line();
            lineNet = Line();
            linePresence = Line();
            scroll.AddChild(lineChat);
            scroll.AddChild(lineAvatar);
            scroll.AddChild(lineImage);
            scroll.AddChild(lineNet);
            scroll.AddChild(linePresence);

            OnOpen += Refresh;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (!IsOpen) return;

            lineChat.SetTitle(Clip("聊天: " + (ChatRelay.On ? "开" : "关")
                + "  已转 " + ChatRelay.Relayed
                + "  用户名 " + ProfileSetting.CurrentName()
                + (string.IsNullOrEmpty(ChatRelay.LastError) ? "" : "  错误[" + ChatRelay.LastError + "]")));
            lineAvatar.SetTitle(Clip("头像: " + Avatar.Status + "  " + Avatar.LastError));
            lineImage.SetTitle(Clip("图片: " + ImageTransfer.SendStatus + "  /  " + ImageTransfer.RecvStatus));
            lineNet.SetTitle(Clip("通信: " + (NetCommBridge.Ready ? "NetComm 就绪" : "NetComm 不可用: " + NetCommBridge.LastError)
                + "  netMode=" + Main.netMode));
            linePresence.SetTitle(Clip("聊天室: " + Presence.Status
                + "  记录 " + ChatRoom.Notes.Count + " 条"));
        }

        /// <summary>状态行一行写完, 太长会被画出框外, 所以掐一下</summary>
        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 60 ? s : s.Substring(0, 60) + "...";
        }

        private UIItem Line()
        {
            return new UIItem(null, " ");
        }

        private void SendAvatar()
        {
            string msg;
            Avatar.SendNow(out msg);
            Main.NewText("[NetComm] " + msg);
        }

        private void SendImage()
        {
            ImageSetting.LastPath = imagePathBox.GetText();
            ImageSetting.Persist();

            if (ImageTransfer.BeginSend(imagePathBox.GetText())) Main.NewText("[NetComm] " + ImageTransfer.LastInfo);
            else Main.NewText("[NetComm] " + ImageTransfer.LastError);
        }

        private void Pick(UIItemTextBox box)
        {
            // FilePicker 已经把回调丢回主线程, 这里直接填框并落盘
            FilePicker.Pick(box.GetText(), delegate (string path)
            {
                box.SetText(path);
                if (box.OnLostFocus != null) box.OnLostFocus();
            });
        }

        private void Refresh()
        {
            nameBox.SetText(ProfileSetting.UserName);
            avatarBox.SetText(ProfileSetting.AvatarPath);
            imagePathBox.SetText(ImageSetting.LastPath);
            maxSideBox.SetText(ImageSetting.MaxSide.ToString());
            qualityBox.SetText(ImageSetting.Quality.ToString());
            slicesBox.SetText(ImageSetting.Slices.ToString());
        }

        //------------------------------ 拼行的小工具 ------------------------------

        private static UIElement Title(string text)
        {
            return new UIItemTitle(null, text);
        }

        private static UIElement Switch(string text, Func<bool> get, Action<bool> set, string tip)
        {
            UIItemSwitch item = new UIItemSwitch(null, text);
            item.SetVal(get());
            item.OnValUpdate = set;
            Hover(item, tip);
            return item;
        }

        private static UIElement Button(string text, Action onClick)
        {
            UIButton1 button = new UIButton1(text, 0.8f);
            button.Width.Pixels = ButtonWidth;
            button.Height.Pixels = RowHeight;
            button.OnLeftClick += delegate
            {
                SoundEngine.PlaySound(12);
                onClick();
            };
            return button;
        }

        private static UIElement ButtonRow(params UIElement[] items)
        {
            UIStackPanel row = new UIStackPanel();
            row.Horizontal = true;
            row.ItemMargin = 6f;
            row.Width.Precent = 1f;
            row.Height.Pixels = RowHeight;

            foreach (UIElement item in items) row.Append(item);
            return row;
        }

        private static void Hover(UIElement item, string tip)
        {
            if (string.IsNullOrEmpty(tip)) return;

            item.OnUpdate += delegate
            {
                if (item.IsMouseHovering) Main.instance.MouseText(tip, 0, 0);
            };
        }
    }
}
