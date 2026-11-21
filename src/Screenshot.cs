using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Terraria;

namespace NetChat
{
    /// <summary>
    /// 游戏截图直发: 抓游戏窗口客户区, 按图片设置缩放编码,
    /// 直接进图片发送队列 —— 对接收方来说就是一张普通图片。
    ///
    /// 用 GDI CopyFromScreen 而不是 GetBackBufferData:
    /// Terraria 默认跑在 XNA 的 Reach 图形档, Reach 不支持读后备缓冲。
    /// 抓屏等下一帧的 Update 做(上一帧刚 Present 完), 时序和原来一样。
    /// </summary>
    internal static class Screenshot
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        private static bool pending;

        /// <summary>点按钮时调, 只是挂个标记</summary>
        public static void Request()
        {
            pending = true;
        }

        /// <summary>Demo.UpdatePostfix 每帧调</summary>
        public static void Update()
        {
            if (!pending) return;
            pending = false;
            Grab();
        }

        private static void Grab()
        {
            try
            {
                IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd == IntPtr.Zero) { Fail("找不到游戏窗口"); return; }

                RECT r;
                if (!GetClientRect(hwnd, out r)) { Fail("取窗口尺寸失败"); return; }

                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) { Fail("窗口客户区尺寸异常"); return; }
                if (w > 4096 || h > 4096) { Fail("窗口分辨率太大 " + w + "x" + h); return; }

                POINT tl = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(hwnd, ref tl)) { Fail("窗口坐标转换失败"); return; }

                // 屏幕上这一块现在显示的就是刚 Present 完的那一帧
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(tl.X, tl.Y, 0, 0, new Size(w, h));
                    }

                    byte[] data;
                    string info;
                    string error;

                    if (!ImageCodec.TryPrepareBitmap(bmp, ImageSetting.MaxSide, ImageSetting.Quality,
                        "screenshot_" + DateTime.Now.ToString("HHmmss") + ".jpg", out data, out info, out error))
                    {
                        Fail("编码失败: " + error);
                        return;
                    }

                    if (ImageTransfer.BeginSendRaw(data, "截图 " + DateTime.Now.ToString("HH:mm:ss"), "截图 " + info))
                    {
                        ChatRoom.AddSystem("截图已发送 (" + info + ", " + ImageTransfer.Format(data.Length) + ")");
                    }
                    else
                    {
                        Fail(ImageTransfer.LastError);
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        private static void Fail(string reason)
        {
            ChatRoom.AddSystem("截图没发出去: " + reason);
        }
    }
}
