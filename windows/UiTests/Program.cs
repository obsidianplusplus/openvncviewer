using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVNCViewer;

internal static class Program
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread] static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenVNCViewer-ui-" + Guid.NewGuid());
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var main = new MainWindow(store); main.Show();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Check(main.FindName("SettingsButton") is Button && main.FindName("ProfileList") is ListBox, "connection manager not loaded");
            var options = new ViewerOptions
            {
                MaxFps = 45, Encoding = FrameEncoding.Raw, ColorDepth = ColorDepth.Rgb565,
                UseCopyRect = false, ScaleMode = DesktopScale.Custom, ScalePercent = 150,
                SmoothScaling = true, ViewOnly = true, AltAsCommand = false, SwapControlAlt = true,
                WheelSpeed = 4, ReverseWheel = true, Clipboard = ClipboardDirection.ReceiveOnly,
                ClipboardOnlyWhenFocused = false, PlayBell = true, ConnectTimeoutSeconds = 29,
                SharedSession = false, KeepAlive = false, AutoReconnect = true,
                ReconnectDelaySeconds = 7, ReconnectAttempts = 9, FullscreenOnConnect = true,
                HideSidebarInFullscreen = false, ShowStatistics = false, RememberWindow = false
            };
            var settings = new SettingsWindow(options) { Owner = main };
            settings.Dispatcher.BeginInvoke(new Action(() =>
            {
                settings.UpdateLayout();
                Check(settings.ReadOptions() == options, "settings controls lost option values");
                ((Slider)settings.FindName("Fps")).Value = 60;
                ((ComboBox)settings.FindName("ColorChoice")).SelectedIndex = 0;
                ((Button)settings.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }), DispatcherPriority.ApplicationIdle);
            Check(settings.ShowDialog() == true && settings.Result.MaxFps == 60 && settings.Result.ColorDepth == ColorDepth.FullColor, "apply button did not commit settings");
            // A profile saved using the actual UI must survive a new process/window.
            ((TextBox)main.FindName("Host")).Text = "192.0.2.10";
            ((TextBox)main.FindName("ProfileName")).Text = "测试连接";
            ((PasswordBox)main.FindName("Password")).Password = "temporary-test-password";
            ((CheckBox)main.FindName("SavePassword")).IsChecked = true;
            var save = FindButtons(main).First(b => b.Content?.ToString() == "保存连接");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Load().Profiles.Count == 1 && store.Load().Profiles[0].PasswordToken.Length > 0, "save profile button failed");
            Check(!File.ReadAllText(store.Path).Contains("temporary-test-password"), "UI saved plaintext password");
            ((CheckBox)main.FindName("SavePassword")).IsChecked = false;
            Check(store.Load().Profiles[0].PasswordToken.Length == 0, "unchecking password did not revoke credential");
            main.Close();
            var restored = new MainWindow(store); restored.Show(); restored.UpdateLayout();
            Check(((TextBox)restored.FindName("Host")).Text == "192.0.2.10", "selected connection not restored");
            Directory.CreateDirectory("artifacts/qa");
            Snapshot(restored, "artifacts/qa/main-window.png");
            var preview = new SettingsWindow(new()) { Owner = restored }; preview.Show(); preview.UpdateLayout();
            Snapshot(preview, "artifacts/qa/settings-window.png"); preview.Close(); restored.Close();
            Console.WriteLine("PASS: WPF windows, all settings roundtrip, apply, profile save/restore, password revocation");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
    static void Snapshot(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
