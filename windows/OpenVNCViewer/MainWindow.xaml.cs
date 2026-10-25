// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace OpenVNCViewer;

public partial class MainWindow : Window
{
    readonly SettingsStore store;
    readonly AppSettings state;
    ViewerOptions options;
    ViewerOptions liveOptions;
    string? editingProfileId;
    bool loading, closing, fullscreen, sessionRunning;
    WindowState windowBeforeFullscreen;
    RfbClient? client;
    CancellationTokenSource? lifetime;
    Channel<Action<RfbClient>>? input;
    WriteableBitmap? bitmap;
    DesktopLayout layout;
    long generation = -1, frames, lastFrames, lastBytes, epoch;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastPaint, lastStats, lastClipboard;
    string clipboardEcho = "";
    int buttons, lastPointerX, lastPointerY;
    Point? pendingPointer;
    readonly Dictionary<Key, int> pressed = [];
    public MainWindow() : this(new SettingsStore()) { }
    public MainWindow(SettingsStore settingsStore)
    {
        loading = true; store = settingsStore; state = store.Load();
        options = state.Defaults.Normalize(); liveOptions = options;
        InitializeComponent();
        if (options.RememberWindow)
        {
            Width = Math.Min(state.WindowWidth, SystemParameters.WorkArea.Width);
            Height = Math.Min(state.WindowHeight, SystemParameters.WorkArea.Height);
            if (state.Maximized) WindowState = WindowState.Maximized;
        }
        SidebarToggle.IsChecked = state.SidebarVisible;
        RefreshProfiles(state.SelectedProfileId);
        loading = false;
        if (ProfileList.SelectedItem is ConnectionProfile profile) LoadProfile(profile);
        ApplyVisualOptions(); UpdateProfileSummary();
        CompositionTarget.Rendering += Render;
        Closing += OnClosing;
    }
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        closing = true;
        if (options.RememberWindow)
        {
            state.WindowWidth = RestoreBounds.Width; state.WindowHeight = RestoreBounds.Height;
            state.Maximized = fullscreen ? windowBeforeFullscreen == WindowState.Maximized : WindowState == WindowState.Maximized;
        }
        state.SidebarVisible = SidebarToggle.IsChecked == true; state.Defaults = options;
        Persist(); Stop(); CompositionTarget.Rendering -= Render;
    }
    bool Persist()
    {
        try { store.Save(state); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Notice.Text = "设置未保存：" + ex.Message; return false; }
    }
    void RefreshProfiles(string? selected = null)
    {
        bool old = loading; loading = true;
        ProfileList.ItemsSource = null; ProfileList.ItemsSource = state.Profiles;
        ProfileList.SelectedItem = state.Profiles.FirstOrDefault(p => p.Id == selected);
        loading = old;
    }
    void LoadProfile(ConnectionProfile profile)
    {
        loading = true; editingProfileId = profile.Id;
        ProfileName.Text = profile.Name; Host.Text = profile.Host; Port.Text = profile.Port.ToString();
        User.Text = profile.Username; Password.Password = PasswordProtector.Unprotect(profile.PasswordToken);
        SavePassword.IsChecked = profile.PasswordToken.Length > 0;
        options = profile.Options.Normalize(); state.SelectedProfileId = profile.Id;
        loading = false;
        if (!sessionRunning) { liveOptions = options; ApplyVisualOptions(); }
        UpdateProfileSummary();
        if (profile.PasswordToken.Length > 0 && Password.Password.Length == 0)
            Notice.Text = "已保存的密码无法在当前 Windows 账号读取，请重新输入。";
    }
    void ProfileSelected(object s, SelectionChangedEventArgs e)
    { if (!loading && ProfileList.SelectedItem is ConnectionProfile p) LoadProfile(p); }
    void ProfileDoubleClick(object s, MouseButtonEventArgs e)
    { if (ProfileList.SelectedItem is ConnectionProfile) Connect(s, e); }
    void NewProfile(object s, RoutedEventArgs e)
    {
        loading = true; ProfileList.SelectedItem = null; editingProfileId = null; state.SelectedProfileId = "";
        ProfileName.Clear(); Host.Clear(); Port.Text = "5900"; User.Clear(); Password.Clear(); SavePassword.IsChecked = false;
        options = state.Defaults.Normalize(); loading = false;
        if (!sessionRunning) { liveOptions = options; ApplyVisualOptions(); }
        UpdateProfileSummary(); Host.Focus();
    }
    bool TryEndpoint(out int port)
    {
        if (!string.IsNullOrWhiteSpace(Host.Text) && int.TryParse(Port.Text, out port) && port is >= 1 and <= 65535) return true;
        port = 5900; Notice.Text = "请输入服务器地址，以及 1–65535 之间的端口。"; return false;
    }
    void SaveProfile(object s, RoutedEventArgs e)
    {
        if (!TryEndpoint(out int port)) return;
        if (editingProfileId == null && state.Profiles.Count >= 50) { Notice.Text = "最多保存 50 个连接，请先删除不用的连接。"; return; }
        string token;
        try { token = SavePassword.IsChecked == true ? PasswordProtector.Protect(Password.Password) : ""; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        { Notice.Text = "无法加密密码，连接未保存：" + ex.Message; return; }
        var profile = new ConnectionProfile
        {
            Id = editingProfileId ?? Guid.NewGuid().ToString("N"), Name = ProfileName.Text.Trim(),
            Host = Host.Text.Trim(), Port = port, Username = User.Text, PasswordToken = token, Options = options
        };
        int index = state.Profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0) state.Profiles[index] = profile; else state.Profiles.Add(profile);
        editingProfileId = profile.Id; state.SelectedProfileId = profile.Id; state.Defaults = options;
        RefreshProfiles(profile.Id);
        if (Persist()) Notice.Text = "连接及专属设置已保存。";
    }
    void ForgetSavedPassword(object s, RoutedEventArgs e)
    {
        if (loading || editingProfileId == null) return;
        int i = state.Profiles.FindIndex(p => p.Id == editingProfileId);
        if (i >= 0 && state.Profiles[i].PasswordToken.Length > 0)
        { state.Profiles[i] = state.Profiles[i] with { PasswordToken = "" }; if (Persist()) Notice.Text = "已删除此连接保存的密码。"; }
    }
    void DeleteProfile(object s, RoutedEventArgs e)
    {
        if (editingProfileId == null) return;
        if (MessageBox.Show(this, "删除这条连接及其保存的密码？", "删除连接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        state.Profiles.RemoveAll(p => p.Id == editingProfileId); state.SelectedProfileId = "";
        RefreshProfiles(); NewProfile(s, e); if (Persist()) Notice.Text = "连接已删除。";
    }
    void UpdateProfileSummary() => ProfileSummary.Text = $"{(options.Encoding == FrameEncoding.Zrle ? "ZRLE 压缩" : "Raw 无压缩")} · {(options.ColorDepth == ColorDepth.Rgb565 ? "16 位色" : "全彩")} · 上限 {options.MaxFps} FPS\n" +
        $"{(options.ViewOnly ? "仅查看" : "允许控制")} · {(options.AutoReconnect ? "自动重连已开启" : "自动重连已关闭")}";
    void OpenSettings(object s, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(options) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ReleaseRemoteInputs(); options = dialog.Result; liveOptions = options;
        client?.ApplyLiveOptions(options); state.Defaults = options;
        if (editingProfileId != null)
        {
            int i = state.Profiles.FindIndex(p => p.Id == editingProfileId);
            if (i >= 0) state.Profiles[i] = state.Profiles[i] with { Options = options };
        }
        clipboardEcho = ""; ApplyVisualOptions(); UpdateProfileSummary();
        if (Persist()) Notice.Text = sessionRunning ? "设置已保存。编码、色深、连接超时和共享会话需重新连接后生效。" : "设置已保存。";
    }
    void ApplyVisualOptions()
    {
        if (QuickScale == null) return;
        bool old = loading; loading = true; QuickScale.SelectedIndex = (int)liveOptions.ScaleMode; loading = old;
        RenderOptions.SetBitmapScalingMode(Desktop, liveOptions.SmoothScaling ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
        Performance.Visibility = liveOptions.ShowStatistics ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebar(); UpdateLayoutGeometry();
    }
    void QuickScaleChanged(object s, SelectionChangedEventArgs e)
    {
        if (loading || QuickScale.SelectedIndex < 0) return;
        options = options with { ScaleMode = (DesktopScale)QuickScale.SelectedIndex };
        liveOptions = liveOptions with { ScaleMode = options.ScaleMode, ScalePercent = options.ScalePercent };
        state.Defaults = options; UpdateLayoutGeometry(); Persist();
    }
    void ToggleSidebar(object s, RoutedEventArgs e) { if (Sidebar != null) UpdateSidebar(); }
    void UpdateSidebar()
    {
        bool visible = SidebarToggle.IsChecked == true && !(fullscreen && liveOptions.HideSidebarInFullscreen);
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(visible ? 290 : 0);
    }
    void ToggleFullscreen(object s, RoutedEventArgs e) => SetFullscreen(!fullscreen);
    void SetFullscreen(bool enabled)
    {
        if (fullscreen == enabled) return;
        fullscreen = enabled;
        if (enabled) { windowBeforeFullscreen = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = windowBeforeFullscreen; }
        UpdateSidebar();
    }
    void LocalShortcut(object s, KeyEventArgs e)
    {
        Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key == System.Windows.Input.Key.F11) { if (!e.IsRepeat) SetFullscreen(!fullscreen); e.Handled = true; }
        else if (key == System.Windows.Input.Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        { OpenSettings(s, e); e.Handled = true; }
    }
    async void Connect(object sender, RoutedEventArgs e)
    {
        if (!TryEndpoint(out int port)) return;
        string host = Host.Text.Trim(), user = User.Text, password = Password.Password;
        if (password.Length == 0 && editingProfileId != null)
            password = PasswordProtector.Unprotect(state.Profiles.FirstOrDefault(p => p.Id == editingProfileId)?.PasswordToken ?? "");
        Stop(); long currentEpoch = epoch; var cts = new CancellationTokenSource(); lifetime = cts;
        liveOptions = options; sessionRunning = true; ConnectButton.IsEnabled = false; Notice.Text = "";
        ApplyVisualOptions(); state.Defaults = options; state.SelectedProfileId = editingProfileId ?? ""; Persist();
        try { await RunSession(host, port, user, password, currentEpoch, cts.Token); }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); if (epoch == currentEpoch) Stop(); }
    }
    async Task RunSession(string host, int port, string user, string password, long sessionEpoch, CancellationToken token)
    {
        bool wasConnected = false; int attempts = 0;
        while (!token.IsCancellationRequested && epoch == sessionEpoch)
        {
            var current = new RfbClient(); client = current;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var abort = attempt.Token.Register(current.Dispose);
            var queue = Channel.CreateBounded<Action<RfbClient>>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            input = queue; Exception? failure = null;
            ResetPicture(); Status.Text = $"正在连接 {host}:{port}…";
            current.ClipboardReceived += text => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (client != current || !liveOptions.CanReceiveClipboard) return;
                try { Clipboard.SetText(text); clipboardEcho = text; } catch (COMException) { }
            }));
            current.BellReceived += () => Dispatcher.BeginInvoke(new Action(() => { if (client == current && liveOptions.PlayBell) MessageBeep(0); }));
            try
            {
                await current.ConnectAsync(host, port, user, password, liveOptions, attempt.Token);
                token.ThrowIfCancellationRequested(); if (epoch != sessionEpoch) return;
                wasConnected = true; attempts = 0; Password.Clear(); Surface.Focus();
                EmptyState.Visibility = Visibility.Collapsed; Status.Text = $"已连接 {host}:{port}";
                if (liveOptions.FullscreenOnConnect) SetFullscreen(true);
                var writer = Task.Run(async () => { await foreach (var action in queue.Reader.ReadAllAsync(attempt.Token)) action(current); }, attempt.Token);
                var reader = Task.Run(() => current.Run(attempt.Token), attempt.Token);
                await Task.WhenAny(reader, writer);
                current.Dispose(); attempt.Cancel(); queue.Writer.TryComplete();
                try { await Task.WhenAll(reader, writer); }
                catch (Exception ex) { failure = ex; }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                current.Dispose(); attempt.Cancel(); queue.Writer.TryComplete();
                if (client == current) { client = null; input = null; }
            }
            if (token.IsCancellationRequested || epoch != sessionEpoch) return;
            if (!ViewerOptions.CanRetry(wasConnected, failure is RfbAuthenticationException, false, attempts, liveOptions))
            { Status.Text = "连接已结束：" + (failure?.Message ?? "服务器关闭连接"); return; }
            attempts++;
            for (int left = liveOptions.ReconnectDelaySeconds; left > 0; left--)
            {
                Status.Text = $"连接中断，{left} 秒后重试（{attempts}/{liveOptions.ReconnectAttempts}）· 点击断开可取消";
                await Task.Delay(1000, token);
                if (!liveOptions.AutoReconnect) { Status.Text = "自动重连已关闭。"; return; }
            }
        }
    }
    void ResetPicture()
    {
        bitmap = null; Desktop.Source = null; generation = -1; frames = lastFrames = lastBytes = 0;
        lastStats = clock.Elapsed.TotalSeconds; clipboardEcho = ""; pressed.Clear(); buttons = 0; pendingPointer = null;
        Performance.Text = ""; EmptyState.Visibility = Visibility.Visible;
    }
    void Stop()
    {
        epoch++; lifetime?.Cancel(); lifetime = null;
        input?.Writer.TryComplete(); client?.Dispose(); client = null; input = null; sessionRunning = false;
        ResetPicture(); if (!closing) ConnectButton.IsEnabled = true;
    }
    void Disconnect(object s, RoutedEventArgs e) { Stop(); Status.Text = "已断开连接"; }
    bool Enqueue(Action<RfbClient> action)
    {
        if (client?.Ready != true) return false;
        if (input?.Writer.TryWrite(action) == true) return true;
        Stop(); Status.Text = "输入队列拥堵，连接已停止。"; return false;
    }
    void Render(object? sender, EventArgs e)
    {
        var current = client; if (current?.Ready != true) return;
        double now = clock.Elapsed.TotalSeconds;
        if (pendingPointer is Point point && !liveOptions.ViewOnly)
        { pendingPointer = null; int mask = buttons; Enqueue(c => c.Pointer((int)point.X, (int)point.Y, mask)); }
        if (now - lastPaint >= 1.0 / liveOptions.MaxFps && Monitor.TryEnter(current.Gate))
        {
            try
            {
                lastPaint = now;
                if (generation != current.Generation)
                {
                    if (bitmap == null || bitmap.PixelWidth != current.Width || bitmap.PixelHeight != current.Height)
                    { bitmap = new WriteableBitmap(current.Width, current.Height, 96, 96, PixelFormats.Bgr32, null); Desktop.Source = bitmap; UpdateLayoutGeometry(); }
                    if (current.Dirty is { } dirty)
                    {
                        // Explicit source and destination coordinates in the full framebuffer.
                        bitmap.WritePixels(new Int32Rect(dirty.X, dirty.Y, dirty.W, dirty.H), current.Pixels,
                            current.Width * 4, dirty.X, dirty.Y);
                        current.Dirty = null;
                    }
                    generation = current.Generation; frames++;
                }
            }
            finally { Monitor.Exit(current.Gate); }
        }
        if (now - lastStats >= 1)
        {
            long received = Interlocked.Read(ref current.ReceivedBytes);
            Performance.Text = $"{current.Width} × {current.Height} · {current.ActiveEncoding} · {current.ActiveColorBits} 位色 · 接收 {(received - lastBytes) / (now - lastStats) / 1048576:F1} MB/s · 绘制 {(frames - lastFrames) / (now - lastStats):F0} FPS · 请求上限 {liveOptions.MaxFps} · {(liveOptions.ViewOnly ? "仅查看" : "允许控制")}";
            lastFrames = frames; lastBytes = received; lastStats = now;
        }
        if (now - lastClipboard > .5)
        {
            lastClipboard = now;
            if (liveOptions.CanSendClipboard && (!liveOptions.ClipboardOnlyWhenFocused || Surface.IsKeyboardFocusWithin && IsActive))
            {
                try
                {
                    if (Clipboard.ContainsText()) { string text = Clipboard.GetText(); if (text != clipboardEcho) { clipboardEcho = text; Enqueue(c => c.Clipboard(text)); } }
                }
                catch (COMException) { }
            }
        }
    }
    void UpdateLayoutGeometry()
    {
        if (DesktopScroll == null || Surface == null) return;
        double width = Math.Max(1, DesktopScroll.ViewportWidth), height = Math.Max(1, DesktopScroll.ViewportHeight);
        var c = client;
        if (c == null || c.Width == 0) { Surface.Width = width; Surface.Height = height; return; }
        layout = DesktopLayout.Calculate(width, height, c.Width, c.Height, liveOptions);
        Surface.Width = Math.Max(width, layout.Width); Surface.Height = Math.Max(height, layout.Height);
        Desktop.Width = layout.Width; Desktop.Height = layout.Height;
        Canvas.SetLeft(Desktop, layout.Left); Canvas.SetTop(Desktop, layout.Top);
    }
    void ViewportChanged(object s, ScrollChangedEventArgs e)
    { if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) UpdateLayoutGeometry(); }
    void ViewportSizeChanged(object s, SizeChangedEventArgs e) => UpdateLayoutGeometry();
    Point? RemotePoint(MouseEventArgs e)
    {
        var c = client; if (c?.Ready != true) return null;
        Point p = e.GetPosition(Surface); var mapped = layout.Map(p.X, p.Y, c.Width, c.Height, Surface.IsMouseCaptured);
        if (mapped is not { } point) return null;
        lastPointerX = point.X; lastPointerY = point.Y; return new(point.X, point.Y);
    }
    void PointerMove(object s, MouseEventArgs e) { if (!liveOptions.ViewOnly) pendingPointer = RemotePoint(e); }
    static int Button(MouseButton b) => b == MouseButton.Left ? 1 : b == MouseButton.Middle ? 2 : b == MouseButton.Right ? 4 : 0;
    void PointerDown(object s, MouseButtonEventArgs e)
    {
        Surface.Focus(); if (liveOptions.ViewOnly || RemotePoint(e) == null) return;
        Surface.CaptureMouse(); buttons |= Button(e.ChangedButton); SendPointer(e); e.Handled = true;
    }
    void PointerUp(object s, MouseButtonEventArgs e)
    { buttons &= ~Button(e.ChangedButton); if (!liveOptions.ViewOnly) SendPointer(e); if (buttons == 0) Surface.ReleaseMouseCapture(); e.Handled = true; }
    void SendPointer(MouseEventArgs e)
    { pendingPointer = null; if (RemotePoint(e) is Point p) { int mask = buttons; Enqueue(c => c.Pointer((int)p.X, (int)p.Y, mask)); } }
    void Wheel(object s, MouseWheelEventArgs e)
    {
        if (liveOptions.ViewOnly) { e.Handled = true; return; }
        if (RemotePoint(e) is Point p)
        {
            int mask = buttons, direction = Math.Sign(e.Delta) * (liveOptions.ReverseWheel ? -1 : 1);
            int wheel = direction > 0 ? 8 : 16, count = Math.Min(30, Math.Max(1, Math.Abs(e.Delta) / 120) * liveOptions.WheelSpeed);
            Enqueue(c => { for (int i = 0; i < count; i++) { c.Pointer((int)p.X, (int)p.Y, mask | wheel); c.Pointer((int)p.X, (int)p.Y, mask); } });
        }
        e.Handled = true;
    }
    void KeyDownRemote(object s, KeyEventArgs e)
    {
        if (!liveOptions.ViewOnly)
        {
            Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            int sym = pressed.TryGetValue(key, out int held) ? held : Keysym(key);
            if (sym != 0) { pressed[key] = sym; Enqueue(c => c.Key(sym, true)); }
        }
        e.Handled = true;
    }
    void KeyUpRemote(object s, KeyEventArgs e)
    {
        Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (pressed.Remove(key, out int sym)) Enqueue(c => c.Key(sym, false));
        e.Handled = true;
    }
    void ReleaseKeys(object s, KeyboardFocusChangedEventArgs e) => ReleaseRemoteInputs();
    void ReleaseRemoteInputs()
    {
        int[] keys = pressed.Values.ToArray(); pressed.Clear(); buttons = 0; pendingPointer = null;
        if (Surface?.IsMouseCaptured == true) Surface.ReleaseMouseCapture();
        if (keys.Length > 0) Enqueue(c => { foreach (int sym in keys) c.Key(sym, false); });
        Enqueue(c => c.ReleasePointer(lastPointerX, lastPointerY));
    }
    int Keysym(Key key)
    {
        if (liveOptions.SwapControlAlt) key = key switch
        { System.Windows.Input.Key.LeftCtrl => System.Windows.Input.Key.LeftAlt, System.Windows.Input.Key.RightCtrl => System.Windows.Input.Key.RightAlt, System.Windows.Input.Key.LeftAlt => System.Windows.Input.Key.LeftCtrl, System.Windows.Input.Key.RightAlt => System.Windows.Input.Key.RightCtrl, _ => key };
        int special = key switch
        {
            System.Windows.Input.Key.Back => 0xff08, System.Windows.Input.Key.Tab => 0xff09, System.Windows.Input.Key.Return => 0xff0d,
            System.Windows.Input.Key.Escape => 0xff1b, System.Windows.Input.Key.Delete => 0xffff, System.Windows.Input.Key.Insert => 0xff63,
            System.Windows.Input.Key.Home => 0xff50, System.Windows.Input.Key.End => 0xff57, System.Windows.Input.Key.Left => 0xff51,
            System.Windows.Input.Key.Up => 0xff52, System.Windows.Input.Key.Right => 0xff53, System.Windows.Input.Key.Down => 0xff54,
            System.Windows.Input.Key.PageUp => 0xff55, System.Windows.Input.Key.PageDown => 0xff56,
            System.Windows.Input.Key.LeftShift => 0xffe1, System.Windows.Input.Key.RightShift => 0xffe2,
            System.Windows.Input.Key.LeftCtrl => 0xffe3, System.Windows.Input.Key.RightCtrl => 0xffe4,
            System.Windows.Input.Key.LeftAlt => liveOptions.AltAsCommand ? 0xffeb : 0xffe9,
            System.Windows.Input.Key.RightAlt => liveOptions.AltAsCommand ? 0xffec : 0xffea,
            System.Windows.Input.Key.LWin => 0xffeb, System.Windows.Input.Key.RWin => 0xffec,
            System.Windows.Input.Key.CapsLock => 0xffe5, _ => 0
        };
        if (special != 0) return special;
        if ((int)key >= (int)System.Windows.Input.Key.F1 && (int)key <= (int)System.Windows.Input.Key.F24) return 0xffbe + (int)key - (int)System.Windows.Input.Key.F1;
        byte[] state = new byte[256]; GetKeyboardState(state);
        foreach (int modifier in new[] { 0x11, 0x12, 0xa2, 0xa3, 0xa4, 0xa5 }) state[modifier] = 0;
        var text = new System.Text.StringBuilder(8); int vk = KeyInterop.VirtualKeyFromKey(key);
        int count = ToUnicode((uint)vk, MapVirtualKey((uint)vk, 0), state, text, text.Capacity, 4);
        return count > 0 ? text[0] <= 255 ? text[0] : 0x01000000 | text[0] : 0;
    }
    void RefreshDesktop(object s, RoutedEventArgs e) { client?.Refresh(); Notice.Text = "已请求完整刷新；服务器下一次响应后生效。"; }
    void SendSecureAttention(object s, RoutedEventArgs e)
    {
        if (liveOptions.ViewOnly) { Notice.Text = "当前是仅查看模式。"; return; }
        ReleaseRemoteInputs();
        Enqueue(c => { c.Key(0xffe3, true); c.Key(0xffe9, true); c.Key(0xffff, true); c.Key(0xffff, false); c.Key(0xffe9, false); c.Key(0xffe3, false); });
    }
    void SaveScreenshot(object s, RoutedEventArgs e)
    {
        if (bitmap == null) { Notice.Text = "连接并收到画面后才能截图。"; return; }
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"远程桌面-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(dialog.FileName); encoder.Save(file); Notice.Text = "截图已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice.Text = "截图未保存：" + ex.Message; }
    }
    async void SendText(object s, RoutedEventArgs e)
    {
        if (client?.Ready != true || liveOptions.ViewOnly) { Notice.Text = "请先连接，并关闭仅查看模式。"; return; }
        var dialog = new TextInputWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var queue = input; var cts = lifetime; long sendingEpoch = epoch;
        if (queue == null || cts == null) return;
        try
        {
            foreach (var rune in dialog.Text.Replace("\r\n", "\n").Replace('\r', '\n').EnumerateRunes())
            {
                if (epoch != sendingEpoch || liveOptions.ViewOnly) break;
                int code = rune.Value, sym = code == 10 ? 0xff0d : code == 9 ? 0xff09 : code <= 255 ? code : 0x01000000 | code;
                await queue.Writer.WriteAsync(c => { c.Key(sym, true); c.Key(sym, false); }, cts.Token);
                if (dialog.DelayMilliseconds > 0) await Task.Delay(dialog.DelayMilliseconds, cts.Token);
            }
            if (epoch == sendingEpoch) Notice.Text = "文本已加入发送队列；Unicode 支持取决于服务器。";
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException) { }
    }
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicode(uint vk, uint scan, byte[] state, System.Text.StringBuilder buffer, int size, uint flags);
    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
}
