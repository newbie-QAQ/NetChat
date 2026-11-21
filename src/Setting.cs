using System;
using tContentPatch;

namespace NetChat
{
    /// <summary>
    /// 开关的持久化。放在 tContentPatch 的模组设置体系里, 存在模组目录的 setting.json。
    /// HasUI = false: 不在加载器的模组设置页里重复出现, 入口统一走快速按钮打开的设置页。
    /// </summary>
    internal sealed class Setting : ModSetting
    {
        private static Setting self;

        public override string FilePath => "setting.json";

        public override Type DataType => typeof(bool);

        public override bool HasUI => false;

        public override void Load(object v)
        {
            self = this;

            if (v == null)
            {
                ChatRelay.On = false;
                NeedSave = true;
                Save();
            }
            else
            {
                ChatRelay.On = (bool)v;
            }
        }

        public override object GetSaveData()
        {
            return ChatRelay.On;
        }

        /// <summary>改开关并立刻落盘</summary>
        public static void Set(bool on)
        {
            if (ChatRelay.On == on) return;

            ChatRelay.On = on;

            if (self == null) return;
            self.NeedSave = true;
            self.Save();
        }
    }
}