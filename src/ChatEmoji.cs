using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI;

namespace NetChat
{
    /// <summary>
    /// 原版表情图集(Images/Extra_48)的取帧。
    /// 布局照抄 EmoteBubble.cs:226: 每个表情占图集里横排两帧(两帧动画并排),
    /// 列 = id * 2 % 8 + 动画帧, 行 = id / 4 + 1。
    /// </summary>
    internal static class ChatEmoji
    {
        public static int Count
        {
            get { return EmoteID.Count; }
        }

        private static Texture2D _sheet;

        /// <summary>表情图集, 原版进游戏就会加载, 拿不到返回 null</summary>
        public static Texture2D Sheet
        {
            get
            {
                try
                {
                    if (_sheet == null || _sheet.IsDisposed)
                    {
                        _sheet = TextureAssets.Extra[48].Value;
                    }
                }
                catch
                {
                    _sheet = null;
                }
                return _sheet;
            }
        }

        public static int Rows
        {
            get { return 2 + (Count - 1) / 4; }
        }

        public static bool IsValid(int id)
        {
            return id >= 0 && id < Count && Sheet != null;
        }

        /// <summary>表情 id 在图集里的源矩形; animFrame 0/1 两帧交替(原版每 8 tick 换一帧)</summary>
        public static Rectangle Frame(int id, int animFrame)
        {
            Texture2D tex = Sheet;
            int w = tex.Width / 8;
            int h = tex.Height / Rows;
            return new Rectangle((id * 2 % 8 + animFrame) * w, (id / 4 + 1) * h, w, h);
        }

        /// <summary>表情名: 先取本地化的 EmojiName(和原版 /emoji 一致), 拿不到退回内部名</summary>
        public static string Name(int id)
        {
            string key = "";

            try { key = EmoteID.Search.GetName(id); } catch { }

            if (string.IsNullOrEmpty(key)) return "#" + id;

            try
            {
                string localized = Terraria.Localization.Language.GetTextValue("EmojiName." + key);
                if (!string.IsNullOrEmpty(localized)) return localized;
            }
            catch { }

            return key;
        }
    }
}
