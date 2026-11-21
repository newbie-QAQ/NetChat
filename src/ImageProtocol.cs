using System;
using System.Text;

namespace NetChat
{
    /// <summary>一条 NetComm 消息 = 图片的一个分片</summary>
    internal sealed class Slice
    {
        public string MsgId;
        public int Index;
        public int Count;
        public int TotalBytes;
        public uint Crc;
        public string Name;
        public string Body;
    }

    /// <summary>
    /// 分片协议。NetComm 的 Receive() 只给字符串(收到的字节按 UTF-8 转文本),
    /// 所以二进制必须先 Base64 成纯 ASCII 才能安全穿过它。
    /// 文本格式: NI1|消息id|片号|总片数|总字节|CRC32|文件名|Base64
    /// 每片原始字节数取 3 的倍数, 这样除最后一片外 Base64 都不带 '=' 补位,
    /// 收端把各片 Base64 直接首尾相接就能整段解码。
    /// </summary>
    internal static class ImageProtocol
    {
        public const string Magic = "NI1";

        /// <summary>
        /// 头像分片的文件名前缀。收端看到这个名字就走头像缓存, 不进接收目录、不占图片预览,
        /// 用户名直接带在名字里, 不用再发一条额外的对应关系。
        /// </summary>
        public const string AvatarPrefix = "avatar_";

        /// <summary>取消通知: "NCX|消息id", 告诉对方别等这张图了</summary>
        public const string CancelMagic = "NCX";

        /// <summary>头像分片用的文件名</summary>
        public static string AvatarName(string userName)
        {
            return AvatarPrefix + SanitizeName(ProfileSetting.CleanName(userName)) + ".png";
        }

        /// <summary>从分片文件名取回用户名; 不是头像分片返回 null</summary>
        public static string AvatarUserName(string sliceName)
        {
            if (string.IsNullOrEmpty(sliceName)) return null;
            if (!sliceName.StartsWith(AvatarPrefix)) return null;

            string s = sliceName.Substring(AvatarPrefix.Length);
            int dot = s.LastIndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);

            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>图片通道。和聊天的 5 号分开, 免得大图的分片和聊天在重组器里互相干扰</summary>
        public const int Channel = 13;

        private const int HeaderReserve = 320;

        // NetComm 每片只装 15 字节, 且单条消息重组上限 8192 片(约 122880 字节)。
        // 这里按"最坏不压缩"留足余量: Base64 后 48000*4/3 = 64000, 加头约 64150,
        // 64150/15 ≈ 4277 片, 远低于 8192, 不会被丢。
        private const int HardMaxChunk = 48000;

        public static int ChunkBytes
        {
            get
            {
                int budget = NetCommBridge.MaxDataLength - HeaderReserve;
                int raw = (budget / 4) * 3;
                if (raw > HardMaxChunk) raw = HardMaxChunk;
                if (raw < 300) raw = 300;
                return raw;
            }
        }

        public static int ChunkCount(int totalBytes)
        {
            int chunk = ChunkBytes;
            if (chunk < 1) chunk = 1;
            return (totalBytes + chunk - 1) / chunk;
        }

        public static string Build(Slice s)
        {
            StringBuilder sb = new StringBuilder(s.Body.Length + 96);
            sb.Append(Magic).Append('|')
              .Append(s.MsgId).Append('|')
              .Append(s.Index).Append('|')
              .Append(s.Count).Append('|')
              .Append(s.TotalBytes).Append('|')
              .Append(s.Crc).Append('|')
              .Append(s.Name).Append('|')
              .Append(s.Body);
            return sb.ToString();
        }

        public static bool TryParse(string text, out Slice s)
        {
            s = null;
            if (string.IsNullOrEmpty(text)) return false;

            string[] parts = text.Split(new[] { '|' }, 8);
            if (parts.Length != 8) return false;
            if (parts[0] != Magic) return false;

            int index, count, total;
            uint crc;
            if (!int.TryParse(parts[2], out index)) return false;
            if (!int.TryParse(parts[3], out count)) return false;
            if (!int.TryParse(parts[4], out total)) return false;
            if (!uint.TryParse(parts[5], out crc)) return false;
            if (count <= 0 || count > 65535) return false;
            if (index < 0 || index >= count) return false;
            if (total <= 0) return false;
            if (parts[1].Length == 0 || parts[7].Length == 0) return false;

            s = new Slice
            {
                MsgId = parts[1],
                Index = index,
                Count = count,
                TotalBytes = total,
                Crc = crc,
                Name = parts[6],
                Body = parts[7]
            };
            return true;
        }

        public static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;

            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                {
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
                }
            }

            return ~crc;
        }

        /// <summary>文件名不能带 '|' 和路径分隔符, 否则会破坏分片头</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "image";

            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = c == '|' || c == '\r' || c == '\n' || c == '\\' || c == '/'
                    || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>';
                sb.Append(bad ? '_' : c);
            }

            if (sb.Length == 0) return "image";
            if (sb.Length > 96) return sb.ToString(0, 96);
            return sb.ToString();
        }
    }
}