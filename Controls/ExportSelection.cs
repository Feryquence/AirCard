using AirCard.Core;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AirCard.Controls
{
    internal sealed class ExportSelection
    {
        readonly List<CheckBox> choices = new List<CheckBox>();
        readonly TextBlock count = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
        internal NoticeWindow Window { get; private set; }
        internal bool IncludeCache { get { return choices[0].IsChecked == true; } }
        internal string[] OriginalAssets { get { return choices.Skip(1).Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray(); } }
        internal ExportSelection()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var all = new Button { Content = "全选", Height = 32, MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
            var none = new Button { Content = "全不选", Height = 32, MinWidth = 70 };
            all.Click += (s, e) => SelectAll(true); none.Click += (s, e) => SelectAll(false);
            tools.Children.Add(all); tools.Children.Add(none); panel.Children.Add(tools);
            Add(panel, "Wallet 卡面缓存", null);
            foreach (string leaf in WalletEngine.BatchArtworkAssets) Add(panel, leaf, leaf);
            panel.Children.Add(count);
            Window = new NoticeWindow("选择导出内容", "请选择要导出的资源，未读取到的文件会跳过。", "选择保存位置…", true, extraContent: panel);
            UpdateCount();
        }
        void Add(Panel panel, string title, string leaf)
        {
            var check = new CheckBox { Content = title, Tag = leaf, IsChecked = true, MinHeight = 26,
                Foreground = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center };
            check.Checked += (s, e) => UpdateCount(); check.Unchecked += (s, e) => UpdateCount();
            choices.Add(check); panel.Children.Add(check);
        }
        void SelectAll(bool value) { foreach (var check in choices) check.IsChecked = value; }
        void UpdateCount()
        {
            int selected = choices.Count(c => c.IsChecked == true);
            count.Text = selected == 0 ? "请至少选择一项。" : "已选择 " + selected + " / " + choices.Count + " 项";
            if (Window != null) Window.AcceptButton.IsEnabled = selected != 0;
        }
    }
}
