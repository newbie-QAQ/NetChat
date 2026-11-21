using System;
using System.Collections.Generic;
using Terraria;
using tContentPatch;

namespace NetChat
{
    /// <summary>
    /// 用户名和头像的设置, 存在模组目录的 profileSetting.json。
    /// 和 imageSetting.json 一样存成字符串数组 [用户名, 头像路径, 自动发送头像], 不再引新格式。
    /// 用户名留空时, 发送方用当前角色名顶上。
    /// HasUI = false: 入口走快速按钮打开的设置页。
    /// </summary>
    internal sealed class ProfileSetting : ModSetting
    {
        private static ProfileSetting self;

        public static string UserName = "";
        public static string AvatarPath = "";
        public static bool AutoSendAvatar = true;

        /// <summary>用户名最多这么长, 太长会把聊天行撑坏</summary>
        public const int NameMaxLength = 32;

        public override string FilePath => "profileSetting.json";

        public override Type DataType => typeof(List<string>);

        public override bool HasUI => false;

        public override void Load(object v)
        {
            self = this;

            List<string> list = v as List<string>;
            if (list != null)
            {
                if (list.Count > 0) UserName = list[0] ?? "";
                if (list.Count > 1) AvatarPath = list[1] ?? "";
                if (list.Count > 2) AutoSendAvatar = list[2] != "0";
            }

            NeedSave = true;
            Save();
        }

        public override object GetSaveData()
        {
            return new List<string>
            {
                CleanName(UserName) ?? "",
                AvatarPath ?? "",
                AutoSendAvatar ? "1" : "0"
            };
        }

        /// <summary>改完立刻落盘</summary>
        public static void Persist()
        {
            if (self == null) return;
            self.NeedSave = true;
            self.Save();
        }

        /// <summary>发送聊天和头像时用的名字: 配置里没填就用当前角色名</summary>
        public static string CurrentName()
        {
            string s = CleanName(UserName);
            if (!string.IsNullOrEmpty(s)) return s;

            try
            {
                if (Main.myPlayer >= 0 && Main.myPlayer < Main.maxPlayers)
                {
                    return CleanName(Main.player[Main.myPlayer].name);
                }
            }
            catch { }

            return "";
        }

        /// <summary>去掉首尾空格、竖线和换行, 竖线是分片头和聊天头的分隔符</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = c == '|' || c == '\r' || c == '\n' || c == '\t';
                if (!bad) sb.Append(c);
            }

            string s = sb.ToString().Trim();
            if (s.Length > NameMaxLength) s = s.Substring(0, NameMaxLength);
            return s;
        }
    }
}
