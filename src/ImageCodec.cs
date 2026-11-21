using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace NetChat
{
    /// <summary>
    /// 图片编解码。发送前缩放+重编码(能大幅缩短传输时间),
    /// 收到后解成 XNA 贴图直接画在界面上。
    /// </summary>
    internal static class ImageCodec
    {
        /// <summary>读图 -> 缩放 -> 重编码; 有透明通道出 PNG, 否则出 JPEG</summary>
        public static bool TryPrepare(string path, int maxSide, int quality, out byte[] data, out string name, out string info, out string error)
        {
            data = null;
            name = null;
            info = null;
            error = null;

            if (string.IsNullOrEmpty(path)) { error = "路径为空"; return false; }
            path = path.Trim().Trim('"');
            if (!File.Exists(path)) { error = "文件不存在"; return false; }

            try
            {
                using (Image src = Image.FromFile(path))
                {
                    string fileName = ImageProtocol.SanitizeName(Path.GetFileNameWithoutExtension(path))
                        + (Image.IsAlphaPixelFormat(src.PixelFormat) ? ".png" : ".jpg");

                    return PrepareScaled(src, maxSide, quality, fileName, out data, out info, out error);
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>截图用: 位图已经在了, 走同样的缩放+编码管线; name 是完整文件名</summary>
        public static bool TryPrepareBitmap(Bitmap src, int maxSide, int quality, string name, out byte[] data, out string info, out string error)
        {
            return PrepareScaled(src, maxSide, quality, name, out data, out info, out error);
        }

        /// <summary>缩放+重编码的公共实现。PNG 还是 JPEG 由源图有没有透明通道决定</summary>
        private static bool PrepareScaled(Image src, int maxSide, int quality, string name, out byte[] data, out string info, out string error)
        {
            data = null;
            info = null;
            error = null;

            bool alpha = Image.IsAlphaPixelFormat(src.PixelFormat);
            int w = src.Width;
            int h = src.Height;
            if (w <= 0 || h <= 0) { error = "尺寸非法"; return false; }

            int nw = w;
            int nh = h;

            if (maxSide > 0 && (w > maxSide || h > maxSide))
            {
                double k = Math.Min((double)maxSide / w, (double)maxSide / h);
                nw = Math.Max(1, (int)Math.Round(w * k));
                nh = Math.Max(1, (int)Math.Round(h * k));
            }

            using (Bitmap bmp = new Bitmap(nw, nh, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, nw, nh));
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    if (alpha) bmp.Save(ms, ImageFormat.Png);
                    else SaveJpeg(bmp, ms, quality);
                    data = ms.ToArray();
                }
            }

            info = w + "x" + h;
            if (nw != w || nh != h) info += " -> " + nw + "x" + nh;
            info += alpha ? " PNG" : " JPG q" + quality;

            if (data.Length == 0) { error = "重编码后为空"; return false; }
            return true;
        }

        /// <summary>
        /// 读头像 -> 等比缩放后居中放进 side x side 的透明画布 -> 一律出 PNG。
        /// 固定成正方形是为了让收端不用管原图比例, 画出来都一样大。
        /// </summary>
        public static bool TryPrepareAvatar(string path, int side, out byte[] data, out string info, out string error)
        {
            data = null;
            info = null;
            error = null;

            if (string.IsNullOrEmpty(path)) { error = "头像路径为空"; return false; }
            path = path.Trim().Trim('"');
            if (!File.Exists(path)) { error = "头像文件不存在"; return false; }

            try
            {
                using (Image src = Image.FromFile(path))
                {
                    int w = src.Width;
                    int h = src.Height;
                    if (w <= 0 || h <= 0) { error = "头像尺寸非法"; return false; }

                    double k = Math.Min((double)side / w, (double)side / h);
                    if (k > 1.0) k = 1.0;

                    int nw = Math.Max(1, (int)Math.Round(w * k));
                    int nh = Math.Max(1, (int)Math.Round(h * k));
                    int x = (side - nw) / 2;
                    int y = (side - nh) / 2;

                    using (Bitmap bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.CompositingMode = CompositingMode.SourceOver;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.Clear(Color.Transparent);
                            g.DrawImage(src, new Rectangle(x, y, nw, nh));
                        }

                        using (MemoryStream ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            data = ms.ToArray();
                        }
                    }

                    info = w + "x" + h + " -> " + side + "x" + side;
                    if (data.Length == 0) { error = "头像编码后为空"; return false; }
                    return true;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void SaveJpeg(Bitmap bmp, Stream output, int quality)
        {
            if (quality < 1) quality = 1;
            if (quality > 100) quality = 100;

            ImageCodecInfo codec = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
            {
                if (c.FormatID == ImageFormat.Jpeg.Guid) { codec = c; break; }
            }

            if (codec == null)
            {
                bmp.Save(output, ImageFormat.Jpeg);
                return;
            }

            using (EncoderParameters p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bmp.Save(output, codec, p);
            }
        }

        /// <summary>编码后的图片字节 -> XNA 贴图, 失败返回 null</summary>
        public static Texture2D Decode(GraphicsDevice device, byte[] data, out string error)
        {
            error = null;

            if (device == null) { error = "GraphicsDevice 为空"; return null; }
            if (data == null || data.Length == 0) { error = "数据为空"; return null; }

            try
            {
                using (MemoryStream ms = new MemoryStream(data, false))
                using (Bitmap bmp = new Bitmap(ms))
                {
                    int w = bmp.Width;
                    int h = bmp.Height;
                    if (w <= 0 || h <= 0) { error = "尺寸非法"; return null; }
                    if (w > 4096 || h > 4096) { error = "图片太大 " + w + "x" + h; return null; }

                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    byte[] raw;
                    int stride;
                    try
                    {
                        stride = Math.Abs(bd.Stride);
                        raw = new byte[stride * h];
                        Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
                    }
                    finally
                    {
                        bmp.UnlockBits(bd);
                    }

                    XnaColor[] pixels = new XnaColor[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        int dst = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = row + x * 4;
                            pixels[dst + x] = XnaColor.FromNonPremultiplied(raw[i + 2], raw[i + 1], raw[i], raw[i + 3]);
                        }
                    }

                    Texture2D tex = new Texture2D(device, w, h, false, SurfaceFormat.Color);
                    tex.SetData(pixels);
                    return tex;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }
    }
}