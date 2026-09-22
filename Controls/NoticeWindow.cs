using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace AirCard.Controls
{
    // Uses ConnectDeveloperTool's FirmwarePublishDialog frame and caption resources.
    internal sealed class NoticeWindow : Window
    {
        internal bool SecondarySelected { get; private set; }
        internal NoticeWindow(string title, string message, string accept = "确定", bool cancel = false, string secondary = null)
        {
            Title = title; Width = secondary == null ? 460 : 560; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Style = (Style)Application.Current.FindResource(typeof(Window));
            Foreground = Brushes.White;
            WindowChrome.SetWindowChrome(this, new WindowChrome {
                CaptionHeight = 32, ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
            });

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var caption = new DockPanel { LastChildFill = true, Style = (Style)FindResource("DeveloperDialogCaptionStyle") };
            var close = new Button { Width = 40, Height = 30, ToolTip = "关闭", Style = (Style)FindResource("DeveloperCaptionButtonStyle") };
            var glyph = new Path { Data = Geometry.Parse("M 0,0 L 9,9 M 9,0 L 0,9"), StrokeThickness = 1.2, Width = 10, Height = 10 };
            glyph.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { Source = close }); close.Content = glyph;
            AutomationProperties.SetName(close, "关闭" + title);
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right); caption.Children.Add(close);
            var heading = new TextBlock { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            heading.SetBinding(TextBlock.TextProperty, new Binding("Title") { Source = this });
            caption.Children.Add(heading); layout.Children.Add(caption);

            var body = new StackPanel { Margin = new Thickness(20) };
            body.Children.Add(new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(100, Math.Min(440, SystemParameters.WorkArea.Height - 180)),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, LineHeight = 21 }
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var okay = ActionButton(accept); okay.IsDefault = true;
            okay.Click += (s, e) => DialogResult = true; buttons.Children.Add(okay);
            if (secondary != null)
            {
                var alternative = ActionButton(secondary); alternative.Margin = new Thickness(8, 0, 0, 0);
                alternative.Click += (s, e) => { SecondarySelected = true; DialogResult = true; }; buttons.Children.Add(alternative);
            }
            if (cancel)
            {
                var later = ActionButton("取消"); later.IsCancel = true; later.Margin = new Thickness(8, 0, 0, 0);
                later.Click += (s, e) => DialogResult = false; buttons.Children.Add(later);
            }
            body.Children.Add(buttons); Grid.SetRow(body, 1); layout.Children.Add(body);
            Content = new Border { Style = (Style)FindResource("DeveloperDialogFrameStyle"), Child = layout };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }
        static Button ActionButton(string text)
        {
            return new Button { Content = text, MinWidth = 85, Height = 32, Padding = new Thickness(12, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center };
        }
    }
}
