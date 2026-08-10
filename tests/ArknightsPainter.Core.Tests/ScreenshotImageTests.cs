using ArknightsPainter.Core.Imaging;
using SkiaSharp;

namespace ArknightsPainter.Core.Tests;

public sealed class ScreenshotImageTests
{
    [Fact]
    public void TryNormalize_AcceptsValidPng()
    {
        var png = CreatePng();

        Assert.True(ScreenshotImage.TryNormalize(png, out var normalized));
        Assert.Same(png, normalized);
    }

    [Fact]
    public void TryNormalize_RemovesAdbPreamble()
    {
        var png = CreatePng();
        var preamble = "daemon message\r\n"u8.ToArray();
        var input = preamble.Concat(png).ToArray();

        Assert.True(ScreenshotImage.TryNormalize(input, out var normalized));
        Assert.Equal(png, normalized);
    }

    [Fact]
    public void TryNormalize_RejectsRandomBytes()
    {
        Assert.False(ScreenshotImage.TryNormalize([1, 2, 3, 4, 5], out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public void TryNormalize_RejectsTruncatedPng()
    {
        var png = CreatePng();

        Assert.False(ScreenshotImage.TryNormalize(png[..20], out var normalized));
        Assert.Empty(normalized);
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(16, 12);
        bitmap.Erase(new SKColor(20, 40, 60));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
