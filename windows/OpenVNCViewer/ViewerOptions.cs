// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;
namespace OpenVNCViewer;

public enum FrameEncoding { Zrle, Raw }
public enum ColorDepth { FullColor, Rgb565 }
public enum DesktopScale { Fit, ActualSize, Custom }
public enum ClipboardDirection { Disabled, SendOnly, ReceiveOnly, Both }

// Immutable snapshots can safely be shared with the decoder thread.
public sealed record ViewerOptions
{
    public int MaxFps { get; init; } = 30;
    public FrameEncoding Encoding { get; init; } = FrameEncoding.Zrle;
    public ColorDepth ColorDepth { get; init; } = ColorDepth.FullColor;
    public bool UseCopyRect { get; init; } = true;
    public DesktopScale ScaleMode { get; init; } = DesktopScale.Fit;
    public int ScalePercent { get; init; } = 100;
    public bool SmoothScaling { get; init; }
    public bool ViewOnly { get; init; }
    public bool AltAsCommand { get; init; } = true;
    public bool SwapControlAlt { get; init; }
    public int WheelSpeed { get; init; } = 1;
    public bool ReverseWheel { get; init; }
    public ClipboardDirection Clipboard { get; init; } = ClipboardDirection.Disabled;
    public bool ClipboardOnlyWhenFocused { get; init; } = true;
    public bool PlayBell { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public bool SharedSession { get; init; } = true;
    public bool KeepAlive { get; init; } = true;
    public bool AutoReconnect { get; init; }
    public int ReconnectDelaySeconds { get; init; } = 3;
    public int ReconnectAttempts { get; init; } = 5;
    public bool FullscreenOnConnect { get; init; }
    public bool HideSidebarInFullscreen { get; init; } = true;
    public bool ShowStatistics { get; init; } = true;
    public bool RememberWindow { get; init; } = true;

    [JsonIgnore] public bool CanSendClipboard => !ViewOnly && Clipboard is ClipboardDirection.SendOnly or ClipboardDirection.Both;
    [JsonIgnore] public bool CanReceiveClipboard => Clipboard is ClipboardDirection.ReceiveOnly or ClipboardDirection.Both;

    public ViewerOptions Normalize() => this with
    {
        MaxFps = Math.Clamp(MaxFps, 5, 120),
        Encoding = Enum.IsDefined(Encoding) ? Encoding : FrameEncoding.Zrle,
        ColorDepth = Enum.IsDefined(ColorDepth) ? ColorDepth : ColorDepth.FullColor,
        ScaleMode = Enum.IsDefined(ScaleMode) ? ScaleMode : DesktopScale.Fit,
        ScalePercent = Math.Clamp(ScalePercent, 25, 200),
        WheelSpeed = Math.Clamp(WheelSpeed, 1, 10),
        Clipboard = Enum.IsDefined(Clipboard) ? Clipboard : ClipboardDirection.Disabled,
        ConnectTimeoutSeconds = Math.Clamp(ConnectTimeoutSeconds, 3, 60),
        ReconnectDelaySeconds = Math.Clamp(ReconnectDelaySeconds, 1, 30),
        ReconnectAttempts = Math.Clamp(ReconnectAttempts, 1, 20)
    };

    public static bool CanRetry(bool wasConnected, bool authenticationFailed, bool userStopped,
        int attempts, ViewerOptions options) => options.AutoReconnect && wasConnected &&
        !authenticationFailed && !userStopped && attempts < options.ReconnectAttempts;
}

// Geometry is shared by rendering and pointer mapping, including letterboxing/scrolling.
public readonly record struct DesktopLayout(double Width, double Height, double Left, double Top)
{
    public static DesktopLayout Calculate(double availableWidth, double availableHeight,
        int remoteWidth, int remoteHeight, ViewerOptions options)
    {
        if (remoteWidth <= 0 || remoteHeight <= 0) return default;
        double scale = options.ScaleMode switch
        {
            DesktopScale.ActualSize => 1,
            DesktopScale.Custom => options.ScalePercent / 100.0,
            _ => Math.Min(availableWidth / remoteWidth, availableHeight / remoteHeight)
        };
        scale = Math.Max(.001, scale);
        double width = remoteWidth * scale, height = remoteHeight * scale;
        return new(width, height, Math.Max(0, (availableWidth - width) / 2),
            Math.Max(0, (availableHeight - height) / 2));
    }
    public (int X, int Y)? Map(double x, double y, int remoteWidth, int remoteHeight, bool clamp)
    {
        if (Width <= 0 || Height <= 0 || remoteWidth <= 0 || remoteHeight <= 0) return null;
        x -= Left; y -= Top;
        if (!clamp && (x < 0 || y < 0 || x >= Width || y >= Height)) return null;
        return ((int)Math.Clamp(Math.Round(x / Width * remoteWidth), 0, remoteWidth - 1),
            (int)Math.Clamp(Math.Round(y / Height * remoteHeight), 0, remoteHeight - 1));
    }
}
