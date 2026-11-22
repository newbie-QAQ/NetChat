using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using tContentPatch;
using tContentPatch.ModLoad;

namespace NetChat
{
    /// <summary>
    /// 反射访问 NetComm.dll 的 NetCommApi(Send / Receive / MaxDataLength)。
    ///
    /// 模组程序集是 Assembly.Load(byte[]) 载入的, 不在探测路径里, 编译期引用在运行期
    /// 解析不到, 所以按程序集名从已加载程序集里找。不引用、不修改 NetComm 本体。
    /// </summary>
    internal static class NetCommBridge
    {
        private const string AssemblyName = "NetComm";
        private const string TypeName = "NetComm.NetCommApi";
        private const int FallbackMaxDataLength = 122876;

        private static bool resolved;
        private static Type api;
        private static MethodInfo sendMethod;
        private static MethodInfo receiveMethod;
        private static PropertyInfo maxLengthProp;
        private static FieldInfo debugField;
        private static FieldInfo queueField;
        private static FieldInfo recField;
        private static FieldInfo lastSenderField;
        private static PropertyInfo slicesProp;
        private static MethodInfo commStatusMethod;

        /// <summary>绑定到了哪个 NetComm, 给诊断用</summary>
        public static string ResolvedInfo = "未解析";

        /// <summary>最近一次失败原因, 成功时为 null</summary>
        public static string LastError { get; private set; }

        private static void Resolve()
        {
            if (resolved) return;

            try
            {
                // F9 重载会连 NetComm 一起重载, 而 .NET 程序集不能卸载:
                // AppDomain 里新旧两份 NetComm 并存, 按名字扫描很可能摸到旧的死程序集,
                // 发送排进旧队列永远发不出, 接收也永远收不到 → 图片传输整个卡死。
                // 所以优先从加载器当前的模组列表里拿"正在服役"的那份。
                Assembly asm = FindCurrentNetComm();

                if (asm == null)
                {
                    // 兜底: 模组列表里没有(比如加载器接口变了)再按名字扫,
                    // 取最后一个匹配(新程序集通常后载入, 比第一个匹配更接近现役)
                    Assembly fallback = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name == AssemblyName) fallback = a;
                    }
                    asm = fallback;
                }

                if (asm == null)
                {
                    LastError = "没有找到已加载的 NetComm 模组, 请确认 Mods\\NetComm 已启用";
                    return;
                }

                Type t = asm.GetType(TypeName, false);
                if (t == null)
                {
                    LastError = "NetComm 里没有找到 " + TypeName;
                    return;
                }

                MethodInfo send = t.GetMethod("Send", new[] { typeof(int), typeof(byte[]) });
                MethodInfo receive = t.GetMethod("Receive", Type.EmptyTypes);
                if (send == null || receive == null)
                {
                    LastError = "NetComm API 签名不匹配, 可能是别的版本";
                    return;
                }

                api = t;
                sendMethod = send;
                receiveMethod = receive;
                maxLengthProp = t.GetProperty("MaxDataLength", BindingFlags.Public | BindingFlags.Static);
                debugField = t.GetField("Debug", BindingFlags.Public | BindingFlags.Static);
                queueField = t.GetField("queue", BindingFlags.NonPublic | BindingFlags.Static);
                slicesProp = t.GetProperty("SlicesPerFrame", BindingFlags.Public | BindingFlags.Static);

                // 收包器 rec 是 internal static, 里面 LastSender 记着刚重组完那条消息的发送者玩家槽位
                recField = t.GetField("rec", BindingFlags.NonPublic | BindingFlags.Static);
                if (recField != null)
                {
                    lastSenderField = recField.FieldType.GetField("LastSender", BindingFlags.Public | BindingFlags.Instance);
                }

                // NetComm.Comm.Status() (internal static): 发/收/各类丢弃计数, 诊断传输问题用
                Type commType = asm.GetType("NetComm.Comm", false);
                if (commType != null)
                {
                    commStatusMethod = commType.GetMethod("Status",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                }

                LastError = null;
                resolved = true;
                ResolvedInfo = asm.FullName;
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
        }

        /// <summary>从加载器当前模组列表里找现役的 NetComm 程序集; 没有返回 null</summary>
        private static Assembly FindCurrentNetComm()
        {
            try
            {
                List<ModObject> mods = ContentPatch.GetModObjects();
                if (mods == null) return null;

                foreach (ModObject m in mods)
                {
                    if (m.assembly != null && m.assembly.GetName().Name == AssemblyName) return m.assembly;
                }
            }
            catch { }

            return null;
        }

        public static bool Ready
        {
            get { Resolve(); return api != null; }
        }

        /// <summary>单条消息的字节上限(压缩前), 用来决定分片大小</summary>
        public static int MaxDataLength
        {
            get
            {
                Resolve();
                if (maxLengthProp == null) return FallbackMaxDataLength;
                try { return Convert.ToInt32(maxLengthProp.GetValue(null, null)); }
                catch { return FallbackMaxDataLength; }
            }
        }

        public static bool Debug
        {
            get
            {
                Resolve();
                if (debugField == null) return false;
                try { return (bool)debugField.GetValue(null); }
                catch { return false; }
            }
        }

        public static bool Send(int channel, byte[] data)
        {
            Resolve();
            if (sendMethod == null) return false;

            try { return (bool)sendMethod.Invoke(null, new object[] { channel, data }); }
            catch (Exception e)
            {
                LastError = (e.InnerException ?? e).Message;
                return false;
            }
        }

        /// <summary>取一条收到的文本, 没有返回 null</summary>
        public static string Receive()
        {
            Resolve();
            if (receiveMethod == null) return null;

            try { return (string)receiveMethod.Invoke(null, null); }
            catch { return null; }
        }

        /// <summary>
        /// 刚 Receive() 出来那条消息的发送者玩家槽位, 拿不到返回 -1。
        /// 只能紧跟在 Receive() 后面读一次: 重组器里这是"最后一条完成的消息"的单值,
        /// 一帧里同时完成多条时它只对应最后那条, 所以认人还是得靠消息自己带的会话 id。
        /// </summary>
        public static int LastSender()
        {
            Resolve();
            if (recField == null || lastSenderField == null) return -1;

            try { return Convert.ToInt32(lastSenderField.GetValue(recField.GetValue(null))); }
            catch { return -1; }
        }

        /// <summary>NetComm 待发队列里的分片数, 用来算发送进度; 读不到返回 -1</summary>
        public static int PendingSlices()
        {
            Resolve();
            if (queueField == null) return -1;

            try
            {
                if (queueField.GetValue(null) is ICollection c) return c.Count;
            }
            catch { }

            return -1;
        }

        /// <summary>清空 NetComm 待发队列(取消传输用); 成功 true</summary>
        public static bool ClearNetQueue()
        {
            Resolve();
            if (queueField == null) return false;

            try
            {
                object q = queueField.GetValue(null);
                if (q != null)
                {
                    // 直接反射调 Clear: 这里用接口类型编译会撞 CS1061(引用链里同名类型干扰)
                    MethodInfo clear = q.GetType().GetMethod("Clear", Type.EmptyTypes);
                    if (clear != null)
                    {
                        clear.Invoke(q, null);
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>NetComm 的收发统计字符串(丢弃计数等), 拿不到返回 null</summary>
        public static string CommStatus()
        {
            Resolve();
            if (commStatusMethod == null) return null;

            try { return (string)commStatusMethod.Invoke(null, null); }
            catch { return null; }
        }

        /// <summary>读 NetComm 的每帧片数设置; NetComm 太旧没有这项时返回 8(原版定值)</summary>
        public static int GetSlicesPerFrame()
        {
            Resolve();
            if (slicesProp == null) return 8;

            try { return Convert.ToInt32(slicesProp.GetValue(null, null)); }
            catch { return 8; }
        }

        /// <summary>设置 NetComm 的每帧片数; NetComm 太旧没有这项时返回 false</summary>
        public static bool SetSlicesPerFrame(int v)
        {
            Resolve();
            if (slicesProp == null) return false;

            try
            {
                slicesProp.SetValue(null, v, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}