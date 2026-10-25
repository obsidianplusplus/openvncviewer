// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
namespace OpenVNCViewer;
public partial class SettingsWindow : Window
{
    public ViewerOptions Result { get; private set; }
    public SettingsWindow(ViewerOptions options)
    {
        Result = options.Normalize(); InitializeComponent(); LoadOptions(Result);
    }
    void LoadOptions(ViewerOptions o)
    {
        Fps.Value = o.MaxFps; EncodingChoice.SelectedIndex = (int)o.Encoding;
        ColorChoice.SelectedIndex = (int)o.ColorDepth; CopyRect.IsChecked = o.UseCopyRect;
        ScaleChoice.SelectedIndex = (int)o.ScaleMode; ScaleValue.Value = o.ScalePercent;
        Smooth.IsChecked = o.SmoothScaling; ReadOnly.IsChecked = o.ViewOnly;
        AltCommand.IsChecked = o.AltAsCommand; SwapModifiers.IsChecked = o.SwapControlAlt;
        WheelValue.Value = o.WheelSpeed; InvertWheel.IsChecked = o.ReverseWheel;
        ClipboardChoice.SelectedIndex = (int)o.Clipboard; ClipboardFocus.IsChecked = o.ClipboardOnlyWhenFocused;
        Bell.IsChecked = o.PlayBell; TimeoutValue.Value = o.ConnectTimeoutSeconds;
        Shared.IsChecked = o.SharedSession; KeepAlive.IsChecked = o.KeepAlive;
        Reconnect.IsChecked = o.AutoReconnect; RetryDelay.Value = o.ReconnectDelaySeconds;
        RetryCount.Value = o.ReconnectAttempts; Fullscreen.IsChecked = o.FullscreenOnConnect;
        HideSidebar.IsChecked = o.HideSidebarInFullscreen; Statistics.IsChecked = o.ShowStatistics;
        RememberSize.IsChecked = o.RememberWindow;
    }
    public ViewerOptions ReadOptions() => new ViewerOptions
    {
        MaxFps = (int)Fps.Value, Encoding = (FrameEncoding)EncodingChoice.SelectedIndex,
        ColorDepth = (ColorDepth)ColorChoice.SelectedIndex, UseCopyRect = CopyRect.IsChecked == true,
        ScaleMode = (DesktopScale)ScaleChoice.SelectedIndex, ScalePercent = (int)ScaleValue.Value,
        SmoothScaling = Smooth.IsChecked == true, ViewOnly = ReadOnly.IsChecked == true,
        AltAsCommand = AltCommand.IsChecked == true, SwapControlAlt = SwapModifiers.IsChecked == true,
        WheelSpeed = (int)WheelValue.Value, ReverseWheel = InvertWheel.IsChecked == true,
        Clipboard = (ClipboardDirection)ClipboardChoice.SelectedIndex,
        ClipboardOnlyWhenFocused = ClipboardFocus.IsChecked == true, PlayBell = Bell.IsChecked == true,
        ConnectTimeoutSeconds = (int)TimeoutValue.Value, SharedSession = Shared.IsChecked == true,
        KeepAlive = KeepAlive.IsChecked == true, AutoReconnect = Reconnect.IsChecked == true,
        ReconnectDelaySeconds = (int)RetryDelay.Value, ReconnectAttempts = (int)RetryCount.Value,
        FullscreenOnConnect = Fullscreen.IsChecked == true, HideSidebarInFullscreen = HideSidebar.IsChecked == true,
        ShowStatistics = Statistics.IsChecked == true, RememberWindow = RememberSize.IsChecked == true
    }.Normalize();
    void Apply(object sender, RoutedEventArgs e) { Result = ReadOptions(); DialogResult = true; }
    void Reset(object sender, RoutedEventArgs e) => LoadOptions(new());
    void Preset(object sender, RoutedEventArgs e)
    {
        string mode = ((Button)sender).Tag?.ToString() ?? "balanced";
        Fps.Value = mode == "economy" ? 15 : mode == "smooth" ? 60 : 30;
        EncodingChoice.SelectedIndex = 0; ColorChoice.SelectedIndex = mode == "economy" ? 1 : 0;
        CopyRect.IsChecked = true; Smooth.IsChecked = false;
    }
    void ScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomScaleRow != null) CustomScaleRow.IsEnabled = ScaleChoice.SelectedIndex == 2;
    }
}
