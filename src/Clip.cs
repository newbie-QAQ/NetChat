using System;
using System.Threading;

namespace NetChat
{
    /// <summary>
    /// 剪贴板和文件对话框都是 WinForms 的家伙, 要求调用线程是 STA。
    /// 游戏主线程是 MTA, 直接调 Clipboard.SetText 会时灵时不灵(被吞掉的异常),
    /// 这里统一派发到专用 STA 线程执行。
    /// </summary>
    internal static class Clip
    {
        /// <summary>把文本放进系统剪贴板; 最多等 1 秒, 成功返回 true</summary>
        public static bool SetText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            bool ok = false;

            Thread t = new Thread((ThreadStart)delegate
            {
                // 剪贴板偶尔被别的进程占着, 重试几次
                for (int i = 0; i < 4 && !ok; i++)
                {
                    try
                    {
                        System.Windows.Forms.Clipboard.SetDataObject(text, true);
                        ok = true;
                    }
                    catch { Thread.Sleep(80); }
                }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(1000);

            return ok;
        }

        /// <summary>
        /// 弹一个"选文件"对话框。对话框在 STA 线程上开自己的消息循环,
        /// 主线程阻塞等待(游戏画面会停住, 这是模态对话框的正常表现)。
        /// 返回选中的路径, 取消/失败返回 null。
        /// </summary>
        public static string PickFile(string title, string filter, string initialDir)
        {
            string picked = null;
            Exception err = null;

            Thread t = new Thread((ThreadStart)delegate
            {
                try
                {
                    using (System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog())
                    {
                        dlg.Title = title;
                        dlg.Filter = filter;
                        if (!string.IsNullOrEmpty(initialDir) && System.IO.Directory.Exists(initialDir))
                            dlg.InitialDirectory = initialDir;

                        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            picked = dlg.FileName;
                    }
                }
                catch (Exception e) { err = e; }
            });

            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (picked == null && err != null)
                ChatRoom.AddSystem("打开文件选择框失败: " + err.Message);

            return picked;
        }
    }
}
