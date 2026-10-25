using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using OpenVNCViewer;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static byte[] Read(NetworkStream s, int n) { byte[] b = new byte[n]; s.ReadExactly(b); return b; }
static void Handshake(NetworkStream s, ViewerOptions options)
{
    s.Write("RFB 003.008\n"u8); Read(s, 12); s.Write(new byte[] { 1, 1 }); Read(s, 1); s.Write(new byte[4]);
    Check(Read(s, 1)[0] == (options.SharedSession ? 1 : 0), "shared session preference ignored");
    s.Write(new byte[] { 0, 2, 0, 2 }); s.Write(new byte[16]); s.Write(new byte[4]);
    byte[] format = Read(s, 20);
    Check(format[4] == (options.ColorDepth == ColorDepth.Rgb565 ? 16 : 32), "pixel depth negotiation failed");
    byte[] head = Read(s, 4); int count = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
    byte[] enc = Read(s, count * 4);
    int[] list = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32BigEndian(enc.AsSpan(i * 4))).ToArray();
    Check(list[0] == (options.Encoding == FrameEncoding.Raw ? 0 : 16), "encoding preference ignored");
    Check(list.Contains(1) == options.UseCopyRect, "CopyRect toggle ignored");
    Check(Read(s, 10)[1] == 0, "first request must be full");
}
foreach (var color in new[] { ColorDepth.FullColor, ColorDepth.Rgb565 })
foreach (var encoding in new[] { FrameEncoding.Raw, FrameEncoding.Zrle })
{
    var options = new ViewerOptions { ColorDepth = color, Encoding = encoding, UseCopyRect = encoding == FrameEncoding.Zrle, SharedSession = false, MaxFps = 15 };
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var peer = await listener.AcceptTcpClientAsync(); using var s = peer.GetStream(); s.ReadTimeout = 5000;
        Handshake(s, options);
        using var compressed = new MemoryStream(); using var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true);
        long cursor = 0;
        for (int frame = 0; frame < 2; frame++)
        {
            int size = frame == 0 ? 2 : 1;
            byte[] header = new byte[16]; header[0] = 0; header[3] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)frame);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)frame);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)size);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)size);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), encoding == FrameEncoding.Raw ? 0 : 16);
            byte[] pixel = color == ColorDepth.Rgb565 ? frame == 0 ? [0, 248] : [31, 0] : frame == 0 ? [0, 0, 255, 0] : [255, 0, 0, 0];
            var timing = Stopwatch.StartNew(); s.Write(header);
            if (encoding == FrameEncoding.Raw)
            { for (int i = 0; i < size * size; i++) s.Write(pixel); }
            else
            {
                // Both rectangles share a single SYNC_FLUSH stream, like a real server.
                zlib.WriteByte(1); zlib.Write(pixel.AsSpan(0, color == ColorDepth.Rgb565 ? 2 : 3)); zlib.Flush();
                byte[] body = compressed.ToArray()[(int)cursor..]; cursor = compressed.Length;
                byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, body.Length); s.Write(length); s.Write(body);
            }
            byte[] request = Read(s, 10);
            Check(request[1] == (frame == 0 ? 0 : 1), "manual refresh/incremental request broken");
            Check(timing.ElapsedMilliseconds >= 50, "FPS limit not applied to update requests");
        }
        if (options.UseCopyRect)
        {
            s.Write(new byte[] { 0,0,0,1, 0,0,0,0,0,1,0,1, 0,0,0,1, 0,1,0,1 });
            Read(s, 10);
        }
    });
    using var c = new RfbClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var abort = timeout.Token.Register(c.Dispose);
    await c.ConnectAsync("127.0.0.1", port, "", "", options, timeout.Token); c.Refresh();
    try { await Task.Run(() => c.Run(timeout.Token)); } catch (EndOfStreamException) { }
    await server; listener.Stop();
    byte[] expected = [0,0,255,0, 0,0,255,0, 0,0,255,0, 255,0,0,0];
    if (options.UseCopyRect) expected[0] = 255;
    if (options.UseCopyRect) expected[2] = 0;
    Check(c.Pixels.SequenceEqual(expected), $"pixels/dirty offset wrong: {encoding}/{color}");
    Check(c.Dirty == (0, 0, 2, 2), "dirty region union wrong");
    Console.WriteLine($"PASS: {encoding}/{color}, continuous updates, refresh, FPS cap, shared session, CopyRect");
}

// Connection cancellation must interrupt a silent TCP peer rather than wait forever.
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var c = new RfbClient(); using var timeout = new CancellationTokenSource(250);
    var accepted = listener.AcceptTcpClientAsync();
    var attempt = c.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "", "", new ViewerOptions(), timeout.Token);
    using var peer = await accepted;
    try { await attempt; throw new Exception("silent peer did not cancel"); } catch (OperationCanceledException) { }
    listener.Stop(); Console.WriteLine("PASS: cancellation closes pending handshake");
}

var normalized = new ViewerOptions { MaxFps = 1000, WheelSpeed = -1, ColorDepth = (ColorDepth)999, Encoding = (FrameEncoding)999 }.Normalize();
Check(normalized.MaxFps == 120 && normalized.WheelSpeed == 1 && normalized.Encoding == FrameEncoding.Zrle && normalized.ColorDepth == ColorDepth.FullColor, "invalid settings were accepted");
Check(!(new ViewerOptions { ViewOnly = true, Clipboard = ClipboardDirection.Both }).CanSendClipboard, "view-only leaks clipboard");
Check((new ViewerOptions { Clipboard = ClipboardDirection.ReceiveOnly }).CanReceiveClipboard && !(new ViewerOptions { Clipboard = ClipboardDirection.ReceiveOnly }).CanSendClipboard, "clipboard directions wrong");
var retry = new ViewerOptions { AutoReconnect = true, ReconnectAttempts = 3 };
Check(ViewerOptions.CanRetry(true, false, false, 2, retry), "valid reconnect rejected");
Check(!ViewerOptions.CanRetry(true, true, false, 0, retry) && !ViewerOptions.CanRetry(false, false, false, 0, retry) && !ViewerOptions.CanRetry(true, false, true, 0, retry) && !ViewerOptions.CanRetry(true, false, false, 3, retry), "reconnect policy can repeat bad credentials or ignore stop");
var fit = DesktopLayout.Calculate(1000, 1000, 1920, 1080, new());
Check(fit.Map(0, 0, 1920, 1080, false) == null, "letterbox sends pointer events");
Check(fit.Map(500, 500, 1920, 1080, false) == (960, 540), "fit mapping wrong");
var zoom = DesktopLayout.Calculate(800, 600, 1920, 1080, new() { ScaleMode = DesktopScale.Custom, ScalePercent = 200 });
Check(zoom.Width == 3840 && zoom.Map(2000, 1000, 1920, 1080, false) == (1000, 500), "scrolled/custom mapping wrong");
Console.WriteLine("PASS: options validation, clipboard policy, reconnect policy, scale/pointer mapping");

string directory = Path.Combine(Path.GetTempPath(), "OpenVNCViewer-tests-" + Guid.NewGuid());
try
{
    var store = new SettingsStore(Path.Combine(directory, "settings.json"));
    var secret = OperatingSystem.IsWindows() ? PasswordProtector.Protect("private-example-password") : "";
    if (OperatingSystem.IsWindows()) Check(PasswordProtector.Unprotect(secret) == "private-example-password", "DPAPI roundtrip failed");
    var saved = new AppSettings { Defaults = retry, Profiles = [new() { Name = "测试服务器", Host = "192.0.2.1", Port = 5901, PasswordToken = secret, Options = new() { MaxFps = 45, ColorDepth = ColorDepth.Rgb565 } }] };
    store.Save(saved); var loaded = store.Load();
    Check(loaded.Profiles.Count == 1 && loaded.Profiles[0].Options.MaxFps == 45 && loaded.Profiles[0].Port == 5901 && loaded.Defaults.AutoReconnect, "profile settings not persisted");
    Check(!File.ReadAllText(store.Path).Contains("private-example-password"), "plaintext password on disk");
    loaded.Profiles[0] = loaded.Profiles[0] with { PasswordToken = "" }; store.Save(loaded);
    Check(store.Load().Profiles[0].PasswordToken == "", "password revocation not persisted");
    File.WriteAllText(store.Path, "{ broken"); Check(store.Load().Profiles.Count == 0, "corrupt config prevents startup");
    File.WriteAllText(store.Path, "{\"Defaults\":null,\"Profiles\":[null]}"); Check(store.Load().Profiles.Count == 0, "null config prevents startup");
    Console.WriteLine("PASS: profile persistence, DPAPI, password revocation, corrupt config recovery");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
