using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DoomLauncher.WinUI;

/// <summary>
/// A native per-pixel-alpha splash window. WinUI's normal swap-chain window
/// always paints an opaque background, so the splash is rendered into a
/// layered Win32 window instead.
/// </summary>
public sealed class SplashWindow : IDisposable
{
    private const int SplashWidth = 720;
    private const int SplashHeight = 600;
    private const uint ExtendedStyles = 0x00080000 | 0x00000080 | 0x00000008;
    private const uint PopupStyle = 0x80000000;
    private const uint UpdateLayeredWindowAlpha = 0x00000002;
    private const byte AlphaSource = 0x01;
    private const int ShowWindowNormal = 5;

    private readonly object _renderLock = new();
    private readonly Bitmap _logo;
    private nint _windowHandle;
    private Timer? _animationTimer;
    private int _animationFrame;
    private bool _disposed;

    public SplashWindow()
    {
        var logoPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "logo_alpha_cropped.png");
        if (!File.Exists(logoPath))
            throw new FileNotFoundException("The splash logo was not found.", logoPath);
        _logo = new Bitmap(logoPath);
    }

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHandle != 0)
            return;

        var x = Math.Max(0, (GetSystemMetrics(0) - SplashWidth) / 2);
        var y = Math.Max(0, (GetSystemMetrics(1) - SplashHeight) / 2);
        _windowHandle = CreateWindowEx(
            ExtendedStyles,
            "STATIC",
            "Doom Launcher 667 - Starting",
            PopupStyle,
            x,
            y,
            SplashWidth,
            SplashHeight,
            0,
            0,
            0,
            0);
        if (_windowHandle == 0)
            throw new InvalidOperationException(
                $"The splash window could not be created ({Marshal.GetLastWin32Error()}).");

        RenderFrame();
        ShowWindow(_windowHandle, ShowWindowNormal);
        _animationTimer = new Timer(
            _ =>
            {
                Interlocked.Increment(ref _animationFrame);
                RenderFrame();
            },
            null,
            0,
            45);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _animationTimer?.Dispose();
            _animationTimer = null;
            if (_windowHandle != 0)
            {
                DestroyWindow(_windowHandle);
                _windowHandle = 0;
            }
            _logo.Dispose();
        }
    }

    private void RenderFrame()
    {
        lock (_renderLock)
        {
            if (_disposed || _windowHandle == 0)
                return;

            using var canvas = new Bitmap(
                SplashWidth,
                SplashHeight,
                PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                const int maximumLogoWidth = SplashWidth - 108;
                const int maximumLogoHeight = SplashHeight - 108;
                var scale = Math.Min(
                    (double)maximumLogoWidth / _logo.Width,
                    (double)maximumLogoHeight / _logo.Height);
                var logoWidth = checked((int)Math.Round(_logo.Width * scale));
                var logoHeight = checked((int)Math.Round(_logo.Height * scale));
                graphics.DrawImage(
                    _logo,
                    (SplashWidth - logoWidth) / 2,
                    28 + (maximumLogoHeight - logoHeight) / 2,
                    logoWidth,
                    logoHeight);

                const int trackWidth = 220;
                const int trackHeight = 4;
                const int segmentWidth = 62;
                var trackX = (SplashWidth - trackWidth) / 2;
                var trackY = SplashHeight - 36;
                using var track = new SolidBrush(Color.FromArgb(54, 40, 161, 160));
                using var segment = new SolidBrush(Color.FromArgb(235, 76, 170, 159));
                graphics.FillRectangle(track, trackX, trackY, trackWidth, trackHeight);
                var cycle = trackWidth + segmentWidth;
                var segmentX = trackX
                    + ((_animationFrame * 8) % cycle)
                    - segmentWidth;
                var visibleLeft = Math.Max(trackX, segmentX);
                var visibleRight = Math.Min(trackX + trackWidth, segmentX + segmentWidth);
                if (visibleRight > visibleLeft)
                {
                    graphics.FillRectangle(
                        segment,
                        visibleLeft,
                        trackY,
                        visibleRight - visibleLeft,
                        trackHeight);
                }
            }

            var screenDc = GetDC(0);
            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmapHandle = canvas.GetHbitmap(Color.FromArgb(0));
            var previousBitmap = SelectObject(memoryDc, bitmapHandle);
            try
            {
                var destination = new NativePoint(
                    Math.Max(0, (GetSystemMetrics(0) - SplashWidth) / 2),
                    Math.Max(0, (GetSystemMetrics(1) - SplashHeight) / 2));
                var source = new NativePoint(0, 0);
                var size = new NativeSize(SplashWidth, SplashHeight);
                var blend = new BlendFunction(0, 0, 255, AlphaSource);
                if (!UpdateLayeredWindow(
                        _windowHandle,
                        screenDc,
                        ref destination,
                        ref size,
                        memoryDc,
                        ref source,
                        0,
                        ref blend,
                        UpdateLayeredWindowAlpha))
                {
                    throw new InvalidOperationException(
                        $"The splash image could not be rendered ({Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                SelectObject(memoryDc, previousBitmap);
                DeleteObject(bitmapHandle);
                DeleteDC(memoryDc);
                ReleaseDC(0, screenDc);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct BlendFunction(
        byte BlendOperation,
        byte BlendFlags,
        byte SourceConstantAlpha,
        byte AlphaFormat);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint windowHandle,
        nint destinationDeviceContext,
        ref NativePoint destinationPoint,
        ref NativeSize size,
        nint sourceDeviceContext,
        ref NativePoint sourcePoint,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint graphicObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint graphicObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint deviceContext);
}
