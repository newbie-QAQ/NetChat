using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Terraria;

namespace NetChat
{
    /// <summary>
    /// 选图片的对话框跑在独立 STA 线程上, 免得卡住主线程(像素画那套也是这么做的)。
    /// 选完把路径丢回主线程再交给调用方。
    /// </summary>
    internal static class FilePicker
    {
        private const string FilterText = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";

        private static bool browsing;

        public static void Pick(string startPath, Action<string> onPicked)
        {
            if (browsing) return;
            browsing = true;

            Thread thread = new Thread(delegate ()
            {
                try
                {
                    using (OpenFileDialog dialog = new OpenFileDialog())
                    {
                        dialog.Title = "选择图片";
                        dialog.Filter = FilterText;

                        if (!string.IsNullOrEmpty(startPath))
                        {
                            try { dialog.InitialDirectory = Path.GetDirectoryName(startPath); }
                            catch { }
                        }

                        if (dialog.ShowDialog() != DialogResult.OK) return;

                        string file = dialog.FileName;
                        Requests.Post(delegate { if (onPicked != null) onPicked(file); });
                    }
                }
                catch (Exception e)
                {
                    string msg = e.Message;
                    Requests.Post(delegate { Main.NewText("[NetComm] 打开文件对话框失败: " + msg); });
                }
                finally
                {
                    browsing = false;
                }
            });

            thread.Name = "NetChat FilePicker";
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);

            try
            {
                thread.Start();
            }
            catch
            {
                browsing = false;
            }
        }
    }
}
