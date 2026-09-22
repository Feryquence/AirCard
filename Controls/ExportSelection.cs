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
        readonly List<CheckBox> remoteChoices = new List<CheckBox>();
        readonly TextBlock count = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 0) };
        internal NoticeWindow Window { get; private set; }
        internal bool IncludeCache { get { return choices[0].IsChecked == true; } }
        internal string[] OriginalAssets { get { return choices.Skip(1).Where(c => c.IsChecked == true && !remoteChoices.Contains(c)).Select(c => (string)c.Tag).ToArray(); } }
        internal string[] RemoteAssets { get { return remoteChoices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray(); } }
        internal ExportSelection(CardResourceCatalog catalog)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var all = new Button { Content = "全选", Height = 32, MinWidth = 70, Margin = new Thickness(0, 0, 8, 0) };
            var none = new Button { Content = "全不选", Height = 32, MinWidth = 70 };
            all.Click += (s, e) => SelectAll(true); none.Click += (s, e) => SelectAll(false);
            tools.Children.Add(all); tools.Children.Add(none); panel.Children.Add(tools);
            var local = Section(panel, "本地文件", "从当前设备读取，未读取到的文件会跳过。");
            local.ToolTip = catalog.Description;
            Add(local, "Wallet 卡面缓存", null);
            foreach (string leaf in catalog.DeviceAssets)
                Add(local, leaf + (catalog.LocalAssets.Contains(leaf) ? "" : "（尝试读取设备副本）"), leaf);
            var divider = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 14) };
            divider.SetResourceReference(Border.BackgroundProperty, "EnvironmentToolWindowBorder");
            panel.Children.Add(divider);
            var online = Section(panel, "在线文件", "从 Apple 下载，保存前校验大小和 SHA-1。");
            online.ToolTip = "同名项同时勾选时保存在线原图。";
            foreach (var resource in catalog.RemoteAssets.Values.OrderBy(r => r.Name))
                remoteChoices.Add(Add(online, resource.Name + "（" + ((resource.Size + 1023) / 1024) + " KiB）", resource.Name, false));
            if (remoteChoices.Count == 0) online.Children.Add(new TextBlock { Text = "未发现可用的在线文件。", Foreground = Brushes.White });
            panel.Children.Add(count);
            Window = new NoticeWindow("选择导出内容", "请选择要导出的内容", "选择保存位置…", true, extraContent: panel, topSpacing: 8);
            UpdateCount();
        }
        static StackPanel Section(Panel parent, string title, string description)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
            content.Children.Add(new TextBlock { Text = description, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            parent.Children.Add(content); return content;
        }
        CheckBox Add(Panel panel, string title, string leaf, bool selected = true)
        {
            var check = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, Tag = leaf, IsChecked = selected, MinHeight = 26,
                Foreground = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center };
            check.Checked += (s, e) => UpdateCount(); check.Unchecked += (s, e) => UpdateCount();
            choices.Add(check); panel.Children.Add(check); return check;
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
