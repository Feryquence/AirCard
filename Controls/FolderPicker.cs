using System;
using System.Windows;
using System.Windows.Interop;

namespace AirCard.Controls
{
    internal static class FolderPicker
    {
        sealed class Owner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public Owner(Window window) { Handle = new WindowInteropHelper(window).EnsureHandle(); }
        }
        internal static string Select(Window owner)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择导出位置，将自动创建 AirCard_output_卡片Hash 文件夹";
                dialog.ShowNewFolderButton = true;
                return dialog.ShowDialog(new Owner(owner)) == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }
    }
}
