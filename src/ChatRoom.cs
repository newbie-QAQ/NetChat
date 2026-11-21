using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;

namespace NetChat
{
    internal sealed class Note
    {
        public DateTime Time;
        public string User = "";
        public string Text = "";
        public bool Mine;

        /// <summary>= -1 是文字消息; >=0 是原版表情编号, Text 留空</summary>
        public int Emote = -1;

        /// <summary>系统提示(进/出房间), 灰字不带头像</summary>
        public bool IsSystem;

        /// <summary>私聊消息, 名字用粉色, 自己这边还带对方名字</summary>
        public bool IsPrivate;

        /// <summary>自己发的私聊里记的对方名字, 只用来显示</summary>
        public string TargetName = "";

        /// <summary>正文按当时的行宽拆好的行, 窗口拉宽或改尺寸才重算, 免得每帧量字</summary>
        public string[] WrappedLines;
        public float WrappedWidth = -1f;

        public string Clock
        {
            get { return Time.ToString("HH:mm"); }
        }
    }

    /// <summary>
    /// 聊天室的消息历史。模组聊天只进这里, 不再往原版聊天栏打一行。
    /// 只保留最近 MaxNotes 条, 再多就丢最老的(界面不需要无上限的历史, 想留全文用 ncdemo stat)。
    /// </summary>
    internal static class ChatRoom
    {
        public const int MaxNotes = 400;

        public static readonly List<Note> Notes = new List<Note>();

        /// <summary>聊天室窗口没开时收到的条数, 打开就清零</summary>
        public static int Unread;

        private static DateTime lastSound = DateTime.MinValue;

        /// <summary>当前私聊对象, null 是没有; 点在线玩家列表切换</summary>
        public static Peer WhisperTarget;

        public static void Add(string user, string text, bool mine)
        {
            if (string.IsNullOrEmpty(text)) return;

            Note note = new Note
            {
                Time = DateTime.Now,
                User = user ?? "",
                Text = text,
                Mine = mine
            };

            Commit(note);

            if (!mine) { Unread++; Sound(); Notify(); }
        }

        /// <summary>加一条私聊。收到的 mine=false 会飘字提醒; 自己发的记一条 mine=true</summary>
        public static void AddPrivate(string user, string text, bool mine, string targetName = "")
        {
            if (string.IsNullOrEmpty(text)) return;

            Note note = new Note
            {
                Time = DateTime.Now,
                User = user ?? "",
                Text = text,
                Mine = mine,
                IsPrivate = true,
                TargetName = targetName ?? ""
            };

            Commit(note);

            if (!mine) { Unread++; Sound(); Notify((string.IsNullOrEmpty(user) ? "有人" : user) + " 给你发来私聊！"); }
        }

        /// <summary>加一条系统提示(进出房间), 灰字, 计入未读但不飘字</summary>
        public static void AddSystem(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            Commit(new Note { Time = DateTime.Now, User = "", Text = text, IsSystem = true });
            Unread++;
        }

        /// <summary>公共入库逻辑: 追加 + 超限丢最老的</summary>
        private static void Commit(Note note)
        {
            Notes.Add(note);

            if (Notes.Count > MaxNotes)
            {
                Notes.RemoveAt(0);
            }
        }

        /// <summary>点在线玩家切换私聊对象, 点同一个再取消</summary>
        public static void ToggleWhisper(Peer peer)
        {
            WhisperTarget = (peer != null && WhisperTarget == peer) ? null : peer;
        }

        /// <summary>加一条表情消息(发送方本地记一条 mine, 收到的走 mine=false)</summary>
        public static void AddEmote(string user, int emote, bool mine)
        {
            Note note = new Note
            {
                Time = DateTime.Now,
                User = user ?? "",
                Emote = emote,
                Mine = mine
            };

            Commit(note);

            if (!mine) { Unread++; Sound(); Notify(); }
        }

        public static void MarkRead()
        {
            Unread = 0;
        }

        public static void Clear()
        {
            Notes.Clear();
            Unread = 0;
            WhisperTarget = null;
        }

        /// <summary>进世界换一个房间就清掉, 免得上一房间的话混进来</summary>
        public static void OnEnterWorld()
        {
            Clear();
        }

        /// <summary>自己发的用本地那份头像, 别人发的按用户名取收到的头像</summary>
        public static Texture2D AvatarFor(Note note)
        {
            if (note == null) return null;
            if (note.Mine) return Avatar.SelfPreview != null ? Avatar.SelfPreview : Avatar.Get(note.User);
            return Avatar.Get(note.User);
        }

        /// <summary>
        /// 收到别人的消息且聊天室窗口没开时, 头顶飘一条提示。
        /// 用原版 CombatText, 纯本地特效不发包; 窗口开着就不飘, 免得看得到消息还提示。
        /// </summary>
        private static void Notify(string text)
        {
            if (SettingUI.ChatIsOpen) return;

            Player p = Main.player[Main.myPlayer];
            if (p == null || !p.active) return;

            CombatText.NewText(new Rectangle((int)p.position.X, (int)p.position.Y, p.width, p.height),
                new Color(120, 220, 255), text);
        }

        private static void Notify()
        {
            Notify("收到一条消息！");
        }

        /// <summary>
        /// 收到消息的提示音(原版聊天音效)。
        /// 1 秒内只响一次: 有人连发一串消息时不至于滴滴滴轰炸耳朵。
        /// </summary>
        private static void Sound()
        {
            if ((DateTime.Now - lastSound).TotalSeconds < 1.0) return;
            lastSound = DateTime.Now;

            try
            {
                if (!Main.dedServ) Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.Chat);
            }
            catch { }
        }
    }
}
