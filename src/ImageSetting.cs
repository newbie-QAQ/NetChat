using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using tContentPatch;

namespace NetChat
{
    /// <summary>
    /// 图片传输的设置, 存在模组目录的 imageSetting.json。
    /// HasUI = false: 入口统一走快速按钮打开的设置页。
    /// 存成 [最大边长, 质量, 上次路径] 三个字符串, 免得为改一个值动到聊天的 setting.json。
    /// </summary>
    internal sealed class ImageSetting : ModSetting
    {
        private static ImageSetting self;

        public static int MaxSide = 320;
        public static int Quality = 75;
        public static string LastPath = "";

        /// <summary>每帧发送片数(1~64), 推给 NetComm 控制图片传输速度; 默认 8 和老版本一致</summary>
        public static int Slices = 8;

        public override string FilePath => "imageSetting.json";

        public override Type DataType => typeof(List<string>);

        public override bool HasUI => false;

        public override void Load(object v)
        {
            self = this;

            List<string> list = v as List<string>;
            if (list != null)
            {
                int n;
                if (list.Count > 0 && int.TryParse(list[0], out n)) MaxSide = n;
                if (list.Count > 1 && int.TryParse(list[1], out n)) Quality = n;
                if (list.Count > 2) LastPath = list[2] ?? "";
                if (list.Count > 3 && int.TryParse(list[3], out n)) Slices = n;
            }

            Clamp();
            PushSlices();
            NeedSave = true;
            Save();
        }

        public override object GetSaveData()
        {
            Clamp();
            return new List<string> { MaxSide.ToString(), Quality.ToString(), LastPath ?? "", Slices.ToString() };
        }

        /// <summary>改完设置立刻落盘</summary>
        public static void Persist()
        {
            if (self == null) return;
            self.NeedSave = true;
            self.Save();
        }

        private static void Clamp()
        {
            if (MaxSide < 0) MaxSide = 0;
            if (MaxSide > 2048) MaxSide = 2048;
            if (Quality < 1) Quality = 1;
            if (Quality > 100) Quality = 100;
            if (Slices < 1) Slices = 1;
            if (Slices > 64) Slices = 64;
        }

        /// <summary>把每帧片数推给 NetComm(太旧的 NetComm 没这项, 静默失败)</summary>
        public static void PushSlices()
        {
            NetCommBridge.SetSlicesPerFrame(Slices);
        }

        /// <summary>接收目录: 我的文档\My Games\Terraria\NetChat\recv</summary>
        public static string RecvFolder
        {
            get { return Path.Combine(Main.SavePath, "NetChat", "recv"); }
        }

        public static string EnsureRecvFolder()
        {
            try
            {
                Directory.CreateDirectory(RecvFolder);
                return RecvFolder;
            }
            catch { return null; }
        }
    }
}