using System;
using Terraria;
using Terraria.Chat;
using Terraria.Chat.Commands;

namespace NetChat
{
    /// <summary>
    /// 聊天转发: 开关打开后, 你发的聊天只走 NetComm 通道, 房间里装了本模组的玩家才能收到。
    ///
    /// 原版聊天走的是 ChatHelper.SendChatMessageFromClient -> NetManager.SendToServer,
    /// 所以只能挂在这里。这里把普通聊天 Consume 掉(原版不再发出、也不再本地显示),
    /// 把发言记进聊天室列表; /指令(CommandId 不是默认的 Say)原样放行。
    ///
    /// NetComm 是客户端发给服务端后由服务端转发给其它客户端(会跳过发送者),
    /// 所以自己那句也要记一条, 否则聊天室里看不到自己说的话。
    ///
    /// 带用户名的消息写成 "CHAT2|用户名|正文", 收到的只进聊天室, 不再往原版聊天栏打一行。
    /// 旧的 "CHAT|正文" 还在收, 按没有用户名处理。
    /// </summary>
    internal static class ChatRelay
    {
        public static bool On;
        public static int Relayed;
        public static string LastError = "";

        /// <summary>普通聊天的 CommandId; 指令会被 CreateOutgoingMessage 换成各自的 CommandId</summary>
        private static readonly ChatCommandId SayId = ChatCommandId.FromType<SayChatCommand>();

        /// <summary>挂在 ChatHelper.SendChatMessageFromClient 上的 Prefix, 主线程调用</summary>
        public static void SendChatMessageFromClient_Prefix(ChatMessage message)
        {
            if (message == null || message.IsConsumed) return;

            if (!On) return;

            // 只接管普通聊天, /指令保持原样
            if (!message.CommandId.Equals(SayId)) return;

            string text = message.Text;
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                string user = ProfileSetting.CurrentName();

                // 选了私聊对象时, 输入的一切都只发给他(原版也不发, 防止私聊漏到公屏)
                Peer whisper = ChatRoom.WhisperTarget;
                if (whisper != null)
                {
                    message.Consume();

                    if (Presence.Peers.IndexOf(whisper) < 0)
                    {
                        LastError = "私聊对象已离线, 没有发出";
                        ChatRoom.AddSystem("私聊发送失败: " + Presence.DisplayName(whisper) + " 已离线");
                        return;
                    }

                    if (Demo.SendWhisper(user, whisper.Session, Presence.DisplayName(whisper), text))
                    {
                        Relayed++;
                        LastError = "";
                    }
                    else
                    {
                        LastError = "私聊发送被拒";
                    }
                    return;
                }

                if (Demo.SendChat(user, text))
                {
                    Relayed++;
                    LastError = "";

                    // 屏蔽原版: 不再发给服务端, 也不显示在原版聊天栏
                    message.Consume();

                    // 服务端不会转回给发送者, 所以自己记一条
                    ChatRoom.Add(user, text, true);
                }
                else
                {
                    LastError = "发送被拒 (netMode=" + Main.netMode + ")";
                }
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
        }

        /// <summary>拼一条带用户名的消息体: "用户名|正文"</summary>
        public static string BuildBody(string user, string text)
        {
            return (user ?? "") + "|" + (text ?? "");
        }

        /// <summary>拆消息体; 没有竖线的旧消息按无用户名处理</summary>
        public static void ParseBody(string body, out string user, out string text)
        {
            user = "";
            text = body ?? "";

            int i = text.IndexOf('|');
            if (i < 0) return;

            user = text.Substring(0, i);
            text = text.Substring(i + 1);
        }
    }
}