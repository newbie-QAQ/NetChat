using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace NetChat
{
    /// <summary>
    /// 游戏截图直发: 把后备缓冲(玩家眼前这一帧)抓下来, 按图片设置缩放编码,
    /// 直接进图片发送队列 —— 对接收方来说就是一张普通图片。
    ///
    /// 抓屏必须等一帧完整画完之后做, 所以点击只挂个标记,
    /// 等下一帧的 Update(上一帧刚 Present 完)再真正抓, 避开渲染中途读缓冲。
    /// </summary>
    internal static class Screenshot
    {
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
                GraphicsDevice gd = Main.instance == null ? null : Main.instance.GraphicsDevice;
                if (gd == null) return;

                int w = gd.PresentationParameters.BackBufferWidth;
                int h = gd.PresentationParameters.BackBufferHeight;
                if (w <= 0 || h <= 0) { Fail("后备缓冲尺寸异常"); return; }
                if (w > 4096 || h > 4096) { Fail("窗口分辨率太大 " + w + "x" + h); return; }

                XnaColor[] pixels = new XnaColor[w * h];
                gd.GetBackBufferData(pixels);

                // XNA 颜色灌进 GDI 位图(BGRA 字节序)
                byte[] raw = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    int o = i * 4;
                    raw[o] = pixels[i].B;
                    raw[o + 1] = pixels[i].G;
                    raw[o + 2] = pixels[i].R;
                    raw[o + 3] = pixels[i].A;
                }

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        Marshal.Copy(raw, 0, bd.Scan0, raw.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(bd);
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
