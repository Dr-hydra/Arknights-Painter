using System.Runtime.InteropServices;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.App.Services;

public static class ScreenCapture
{
    public static (SKBitmap Bitmap, int Left, int Top, int Width, int Height) CaptureMonitorAtCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            throw new InvalidOperationException("无法获取鼠标位置。");
        }

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法定位显示器。");
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("无法获取显示器信息。");
        }

        // 只截取鼠标所在显示器；覆盖窗口铺满该显示器时，
        // XamlRoot.RasterizationScale 即该显示器的 DPI 缩放，坐标转换一致。
        var left = monitorInfo.Monitor.Left;
        var top = monitorInfo.Monitor.Top;
        var width = monitorInfo.Monitor.Right - monitorInfo.Monitor.Left;
        var height = monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("无法获取屏幕尺寸。");
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法获取屏幕设备上下文。");
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("无法创建截图设备上下文。");
        }

        var hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("无法创建截图位图。");
        }

        var previous = SelectObject(memoryDc, hBitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SRCCOPY))
            {
                throw new InvalidOperationException("屏幕画面复制失败。");
            }

            // GetDIBits 要求位图未被选入设备上下文，先恢复原对象。
            SelectObject(memoryDc, previous);

            var pixels = new byte[width * height * 4];
            var header = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (GetDIBits(memoryDc, hBitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref header, 0) != height)
                {
                    throw new InvalidOperationException("屏幕像素读取失败。");
                }
            }
            finally
            {
                handle.Free();
            }

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var result = new SKBitmap(info);
            Marshal.Copy(pixels, 0, result.GetPixels(), pixels.Length);
            return (result, left, top, width, height);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static byte[] CropToPng(SKBitmap source, PixelRect rect)
    {
        var x = Math.Clamp(rect.X, 0, source.Width);
        var y = Math.Clamp(rect.Y, 0, source.Height);
        // 贴屏幕边缘时选区可能退化为 0 宽/高，保证至少 1px，避免 Clamp(min>max) 抛异常。
        var width = Math.Clamp(rect.Width, 1, Math.Max(1, source.Width - x));
        var height = Math.Clamp(rect.Height, 1, Math.Max(1, source.Height - y));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var sourcePixels = source.GetPixels();
        var targetPixels = bitmap.GetPixels();
        var rowBuffer = new byte[width * 4];
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((y + row) * source.Width + x) * 4;
            var targetOffset = row * width * 4;
            Marshal.Copy(sourcePixels + sourceOffset, rowBuffer, 0, rowBuffer.Length);
            Marshal.Copy(rowBuffer, 0, targetPixels + targetOffset, rowBuffer.Length);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr dc,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr dc,
        IntPtr bitmap,
        uint start,
        uint lines,
        IntPtr bits,
        ref BitmapInfoHeader info,
        uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor;
        public MonitorRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
