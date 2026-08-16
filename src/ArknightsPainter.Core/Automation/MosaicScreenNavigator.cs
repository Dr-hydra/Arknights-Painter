using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Automation;

public sealed class MosaicScreenNavigator(IAdbClient device, IScreenLocator locator) : IMosaicScreenNavigator
{
    private const int TransitionAttempts = 30;
    private static readonly TimeSpan TransitionDelay = TimeSpan.FromMilliseconds(250);

    public async Task EnsureEditorAsync(
        string serial,
        CalibrationProfile profile,
        CancellationToken cancellationToken = default)
    {
        var screenshot = await device.CaptureScreenshotAsync(serial, cancellationToken);
        if (IsEditor(screenshot, profile))
        {
            return;
        }

        if (!IsGallery(screenshot, profile))
        {
            throw new InvalidOperationException("当前既不是绘画编辑页，也不是画像册页面，无法开始 96×96 分片任务。");
        }

        await device.TapAsync(serial, ScalePoint(screenshot, 0.903, 0.911), cancellationToken);
        await WaitForAsync(serial, profile, expectEditor: true, cancellationToken);
    }

    public async Task SaveAsync(
        string serial,
        CalibrationProfile profile,
        CancellationToken cancellationToken = default)
    {
        var screenshot = await device.CaptureScreenshotAsync(serial, cancellationToken);
        if (IsGallery(screenshot, profile))
        {
            return;
        }

        if (!IsEditor(screenshot, profile))
        {
            throw new InvalidOperationException("保存分片前未检测到绘画编辑页，已停止自动操作。");
        }

        await device.TapAsync(serial, ScalePoint(screenshot, 0.744, 0.060), cancellationToken);
        await WaitForAsync(serial, profile, expectEditor: false, cancellationToken);
    }

    internal bool IsGallery(byte[] screenshotPng, CalibrationProfile profile)
    {
        if (IsEditor(screenshotPng, profile))
        {
            return false;
        }

        using var bitmap = SKBitmap.Decode(screenshotPng);
        if (bitmap is null)
        {
            return false;
        }

        var left = (int)(bitmap.Width * 0.81);
        var right = (int)(bitmap.Width * 0.985);
        var top = (int)(bitmap.Height * 0.84);
        var bottom = (int)(bitmap.Height * 0.98);
        var cyan = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var pixel = bitmap.GetPixel(x, y);
                sampled++;
                if (pixel.Green >= 140 && pixel.Blue >= 140 &&
                    pixel.Green - pixel.Red >= 20 && pixel.Blue - pixel.Red >= 20)
                {
                    cyan++;
                }
            }
        }

        if (sampled > 0 && cyan / (double)sampled >= 0.08)
        {
            return true;
        }

        // The add button may disappear when the gallery reaches its capacity.
        // The cyan divider below the gallery header remains visible in that state.
        var headerCyan = 0;
        var headerSamples = 0;
        var headerLeft = (int)(bitmap.Width * 0.53);
        var headerRight = (int)(bitmap.Width * 0.98);
        var headerTop = (int)(bitmap.Height * 0.10);
        var headerBottom = (int)(bitmap.Height * 0.125);
        for (var y = headerTop; y < headerBottom; y += 2)
        {
            for (var x = headerLeft; x < headerRight; x += 4)
            {
                var pixel = bitmap.GetPixel(x, y);
                headerSamples++;
                if (pixel.Green >= 175 && pixel.Blue >= 175 &&
                    pixel.Green - pixel.Red >= 12 && pixel.Blue - pixel.Red >= 12)
                {
                    headerCyan++;
                }
            }
        }

        return headerSamples > 0 && headerCyan / (double)headerSamples >= 0.16;
    }

    private bool IsEditor(byte[] screenshotPng, CalibrationProfile profile) =>
        locator.ScoreCanvas(screenshotPng, profile.CanvasBounds) >= 0.25;

    private async Task<byte[]> WaitForAsync(
        string serial,
        CalibrationProfile profile,
        bool expectEditor,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < TransitionAttempts; attempt++)
        {
            await Task.Delay(TransitionDelay, cancellationToken);
            var screenshot = await device.CaptureScreenshotAsync(serial, cancellationToken);
            if (expectEditor ? IsEditor(screenshot, profile) : IsGallery(screenshot, profile))
            {
                return screenshot;
            }
        }

        throw new InvalidOperationException(expectEditor
            ? "点击新增后未检测到绘画编辑页，已停止自动操作。"
            : "点击保存后未检测到画像册页面，已停止自动操作。");
    }

    private static PixelPoint ScalePoint(byte[] screenshotPng, double normalizedX, double normalizedY)
    {
        using var bitmap = SKBitmap.Decode(screenshotPng)
            ?? throw new InvalidDataException("无法解码页面导航截图。");
        return new PixelPoint(
            (int)Math.Round(bitmap.Width * normalizedX),
            (int)Math.Round(bitmap.Height * normalizedY));
    }
}
