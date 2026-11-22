using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.UI;
using tContentPatch;

namespace NetChat
{
    /// <summary>
    /// NetComm 的收发中枢。
    ///
    /// 三个要点:
    /// 1. Send 只是"入队", 真正的发送由 NetComm 自己每帧 8 片地做, 所以大包要等。
    /// 2. Receive 必须每帧取空 —— 它内部队列只有 32 格, 满了挤掉最旧的。
    /// 3. 收端只拿到 string, 所以二进制要先 Base64。
    ///
    /// 所有 NetComm 调用都放在主线程(UpdatePostfix / Requests 里), 避免和
    /// NetComm 自己的收发线程抢那个非线程安全的队列。
    /// </summary>
    public class Demo : PatchMain
    {
        /// <summary>带用户名的聊天标记, 收端据此把它当聊天显示而不是调试信息</summary>
        public const string ChatPrefix = "CHAT2|";

        /// <summary>旧版不带用户名的标记, 收到还认</summary>
        public const string ChatPrefixOld = "CHAT|";

        /// <summary>表情消息: "EMO|用户名|表情id", 显示成图标</summary>
        public const string EmotePrefix = "EMO|";

        /// <summary>私聊消息: "WHISPER|目标会话id|用户名|正文", 收端按会话 id 过滤</summary>
        public const string WhisperPrefix = "WHISPER|";

        public override void Initialize()
        {
            // 隐蔽信令(82号隐形粒子包)的分发路由
            SignalLink.OnMessage = SignalReceived;

            // F9 重载模组时人已经在世界里, OnEnterWorld 不会再触发, 这里补一遍
            // 进世界初始化: 清状态 + 立即发 HERE 检测在线玩家 + 头像广播
            if (!Main.gameMenu && Main.netMode == 1
                && Main.myPlayer >= 0 && Main.myPlayer < Main.player.Length
                && Main.player[Main.myPlayer] != null && Main.player[Main.myPlayer].active)
            {
                OnEnterWorld();
            }
        }

        public override void OnEnterWorld()
        {
            ImageTransfer.Reset();
            Avatar.Reset();
            ChatRoom.OnEnterWorld();
            Presence.OnEnterWorld();
            SignalLink.Reset();
            UpdateChannel.Reset();
            SongChannel.Reset();
            Avatar.RequestBroadcast();
        }

        public override void UpdatePostfix(GameTime gameTime)
        {
            Requests.Run();

            // 每帧取空
            string msg;
            while ((msg = NetCommBridge.Receive()) != null)
            {
                // 发送者槽位跟着刚取出的这条走, 必须在 Receive 之后马上读
                Handle(msg, NetCommBridge.LastSender());
            }

            ImageTransfer.Update();
            Avatar.Update();
            Presence.Update();
            SignalLink.Update();
            UpdateChannel.Poll();
            Screenshot.Update();
            SongChannel.Update();
            SongPlayer.Update();
        }

        /// <summary>设置页的输入框在 Update 阶段吃按键, 和框架其它窗口一样</summary>
        public override void UpdateUIStatesPostfix(GameTime gameTime)
        {
            SettingUI.Update(gameTime);
        }

        /// <summary>把设置页窗口画在物品栏那一层前面</summary>
        public override void SetupDrawInterfaceLayersPostfix(List<GameInterfaceLayer> layers)
        {
            SettingUI.AddLayer(layers);
        }

        private static void Handle(string msg, int sender)
        {
            if (msg.StartsWith(ChatPrefix))
            {
                string user, text;
                ChatRelay.ParseBody(msg.Substring(ChatPrefix.Length), out user, out text);
                ChatRoom.Add(user, text, false);
                return;
            }

            if (msg.StartsWith(EmotePrefix))
            {
                string user, idText;
                ChatRelay.ParseBody(msg.Substring(EmotePrefix.Length), out user, out idText);

                int emote;
                if (int.TryParse(idText, out emote) && ChatEmoji.IsValid(emote))
                {
                    ChatRoom.AddEmote(user, emote, false);
                }
                return;
            }

            if (msg.StartsWith(ChatPrefixOld))
            {
                // 旧版客户端发的, 没有用户名
                ChatRoom.Add("", msg.Substring(ChatPrefixOld.Length), false);
                return;
            }

            if (msg.StartsWith(ImageProtocol.Magic + "|"))
            {
                ImageTransfer.OnMessage(msg);
                return;
            }

            // 点歌: 邀请(SRQ)/回执(SRQOK 接受, SRQNO 拒绝) 和 音频分片
            // 注意回执不是 "SRQ|" 开头, 之前只路由 SRQ 导致发送端永远等不到确认
            if (msg.StartsWith(SongChannel.OfferMagic + "|")
                || msg.StartsWith(SongChannel.OkMagic + "|")
                || msg.StartsWith(SongChannel.NoMagic + "|"))
            {
                SongChannel.OnMessage(msg, sender);
                return;
            }

            if (msg.StartsWith(SongChannel.SliceMagic + "|"))
            {
                SongChannel.OnSliceMessage(msg);
                return;
            }

            if (msg.StartsWith(UpdateChannel.UpdatePrefix))
            {
                UpdateChannel.OnPackage(msg);
                return;
            }

            if (msg.StartsWith(UpdateChannel.OkPrefix))
            {
                UpdateChannel.OnOk(sender, msg.Substring(UpdateChannel.OkPrefix.Length));
            }
        }

        /// <summary>转发一条聊天, 带上发送方用的用户名。走隐蔽信令通道, 故意不打"已排队"</summary>
        public static bool SendChat(string user, string text)
        {
            return SignalLink.Send(SignalLink.KindChat, ChatPrefix + ChatRelay.BuildBody(user, text));
        }

        /// <summary>
        /// 发一条私聊。走隐蔽信令, 正文里带目标会话 id;
        /// 广播大家都能收到, 但别人的客户端按会话 id 过滤, 只有目标本人会显示。
        /// </summary>
        public static bool SendWhisper(string user, string targetSession, string targetName, string text)
        {
            if (!SignalLink.Send(SignalLink.KindWhisper, WhisperPrefix + targetSession + "|" + ChatRelay.BuildBody(user, text)))
            {
                Say("失败: 私聊没发出去, " + SignalLink.LastError);
                return false;
            }

            // 自己记一条, TargetName 只用来显示
            ChatRoom.AddPrivate(user, text, true, targetName);
            return true;
        }

        /// <summary>发一个表情, 走隐蔽信令通道。点击表情面板时调用, 已经在主线程</summary>
        public static void SendEmote(string user, int emote)
        {
            if (!ChatEmoji.IsValid(emote))
            {
                Say("失败: 表情编号无效 " + emote);
                return;
            }

            if (!SignalLink.Send(SignalLink.KindEmote, EmotePrefix + ChatRelay.BuildBody(user, emote.ToString())))
            {
                Say("失败: 表情没发出去, " + SignalLink.LastError);
                return;
            }

            // 服务端不转回给发送者, 自己记一条
            ChatRoom.AddEmote(user, emote, true);
        }

        /// <summary>隐蔽信令的分发路由: 聊天/表情/私聊/版本通告。识别(HERE)走弹幕信令, 不经过这里</summary>
        private static void SignalReceived(byte kind, string body, int slot)
        {
            if (kind == SignalLink.KindVer)
            {
                // "NV|槽位|版本号" 或催报 "RV|目标槽位|催报者槽位"
                if (body.StartsWith("NV|", StringComparison.Ordinal))
                {
                    string rest = body.Substring(3);
                    int bar = rest.IndexOf('|');
                    if (bar > 0)
                    {
                        int verSlot;
                        if (int.TryParse(rest.Substring(0, bar), out verSlot))
                        {
                            UpdateChannel.OnVersion(verSlot, rest.Substring(bar + 1));
                        }
                    }
                }
                else if (body.StartsWith("RV|", StringComparison.Ordinal))
                {
                    // 被点到名的客户端立刻补报自己的版本号; 真老版本不认识 RV, 自然不回
                    string rest = body.Substring(3);
                    int bar = rest.IndexOf('|');
                    if (bar > 0)
                    {
                        int target, requester;
                        if (int.TryParse(rest.Substring(0, bar), out target)
                            && int.TryParse(rest.Substring(bar + 1), out requester)
                            && target.ToString() == Presence.Session)
                        {
                            UpdateChannel.OnVersionRequest(requester);
                        }
                    }
                }
                return;
            }

            if (kind == SignalLink.KindChat)
            {
                if (body.StartsWith(ChatPrefix, StringComparison.Ordinal))
                {
                    string user, text;
                    ChatRelay.ParseBody(body.Substring(ChatPrefix.Length), out user, out text);
                    ChatRoom.Add(user, text, false);
                }
                return;
            }

            if (kind == SignalLink.KindWhisper)
            {
                if (!body.StartsWith(WhisperPrefix, StringComparison.Ordinal)) return;

                // "WHISPER|目标会话|用户名|正文"; 广播大家都收得到, 只处理发给自己的
                string rest = body.Substring(WhisperPrefix.Length);
                int bar = rest.IndexOf('|');
                if (bar < 0) return;

                if (rest.Substring(0, bar) != Presence.Session) return;

                string wUser, wText;
                ChatRelay.ParseBody(rest.Substring(bar + 1), out wUser, out wText);
                ChatRoom.AddPrivate(wUser, wText, false);
                return;
            }

            if (kind == SignalLink.KindEmote)
            {
                if (body.StartsWith(EmotePrefix, StringComparison.Ordinal))
                {
                    string user, idText;
                    ChatRelay.ParseBody(body.Substring(EmotePrefix.Length), out user, out idText);

                    int emote;
                    if (int.TryParse(idText, out emote) && ChatEmoji.IsValid(emote))
                    {
                        ChatRoom.AddEmote(user, emote, false);
                    }
                }
            }
        }

        /// <summary>打印到游戏聊天栏, 任意线程可调</summary>
        public static void Say(string s)
        {
            Requests.Post(delegate { Main.NewText("[NetChat] " + s); });
        }
    }
}
