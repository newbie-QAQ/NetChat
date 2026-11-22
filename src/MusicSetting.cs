using System;
using System.Collections.Generic;
using System.Globalization;
using tContentPatch;

namespace NetChat
{
    /// <summary>
    /// 点歌播放的设置, 存在模组目录的 musicSetting.json。
    /// 目前只有音量(0~1), 存成字符串列表, 和 imageSetting.json 一个写法。
    /// 加载器会自动发现 ModSetting 子类, 不需要手动注册。
    /// </summary>
    internal sealed class MusicSetting : ModSetting
    {
        private static MusicSetting self;

        public static float Volume = 0.8f;

        public override string FilePath => "musicSetting.json";

        public override Type DataType => typeof(List<string>);

        public override bool HasUI => false;

        public override void Load(object v)
        {
            self = this;

            List<string> list = v as List<string>;
            if (list != null && list.Count > 0)
            {
                float f;
                if (float.TryParse(list[0], NumberStyles.Float, CultureInfo.InvariantCulture, out f)) Volume = f;
            }

            Clamp();
            NeedSave = true;
            Save();
        }

        public override object GetSaveData()
        {
            Clamp();
            return new List<string> { Volume.ToString("0.##", CultureInfo.InvariantCulture) };
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
            if (Volume < 0f) Volume = 0f;
            if (Volume > 1f) Volume = 1f;
        }
    }
}
