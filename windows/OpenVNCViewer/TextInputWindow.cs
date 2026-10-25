// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
namespace OpenVNCViewer;
public sealed class TextInputWindow : Window
{
    readonly TextBox text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 10000, MinHeight = 180 };
    readonly Slider delay = new() { Minimum = 0, Maximum = 100, Value = 10, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 180, Margin = new(12, 0, 12, 0) };
    public string Text => text.Text;
    public int DelayMilliseconds => (int)delay.Value;
    public TextInputWindow()
    {
        Background = System.Windows.Media.Brushes.White; Foreground = System.Windows.Media.Brushes.Black;
        Title = "发送文本"; Width = 620; Height = 430; MinWidth = 600; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new DockPanel { Margin = new(18) }; Content = panel;
        var hint = new TextBlock { Text = "输入或粘贴文本，包括使用中文输入法。最多 10000 字符；远端必须支持 Unicode 按键。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "按键间隔 (ms)", VerticalAlignment = VerticalAlignment.Center }); footer.Children.Add(delay);
        var send = new Button { Content = "发送", Padding = new(18, 6, 18, 6), Margin = new(0, 0, 8, 0), IsDefault = true };
        send.Click += (_, _) => DialogResult = true; footer.Children.Add(send);
        footer.Children.Add(new Button { Content = "取消", Padding = new(18, 6, 18, 6), IsCancel = true });
        panel.Children.Add(text); Loaded += (_, _) => text.Focus();
    }
}
