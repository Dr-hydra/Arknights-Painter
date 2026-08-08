using SkiaSharp;

namespace ArknightsPainter.Core.Imaging;

public static class SkiaImageLoader
{
    public static SKBitmap LoadOriented(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        using var managed = new SKManagedStream(stream);
        using var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Unsupported or damaged image.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var raw = new SKBitmap(info);
        var result = codec.GetPixels(info, raw.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            raw.Dispose();
            throw new InvalidDataException($"Image decoding failed: {result}.");
        }

        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft)
        {
            return raw;
        }

        var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var oriented = new SKBitmap(
            swap ? raw.Height : raw.Width,
            swap ? raw.Width : raw.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);

        for (var y = 0; y < raw.Height; y++)
        {
            for (var x = 0; x < raw.Width; x++)
            {
                var (dx, dy) = MapOrientation(x, y, raw.Width, raw.Height, codec.EncodedOrigin);
                oriented.SetPixel(dx, dy, raw.GetPixel(x, y));
            }
        }

        raw.Dispose();
        return oriented;
    }

    private static (int X, int Y) MapOrientation(
        int x,
        int y,
        int width,
        int height,
        SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => (width - 1 - x, y),
        SKEncodedOrigin.BottomRight => (width - 1 - x, height - 1 - y),
        SKEncodedOrigin.BottomLeft => (x, height - 1 - y),
        SKEncodedOrigin.LeftTop => (y, x),
        SKEncodedOrigin.RightTop => (height - 1 - y, x),
        SKEncodedOrigin.RightBottom => (height - 1 - y, width - 1 - x),
        SKEncodedOrigin.LeftBottom => (y, width - 1 - x),
        _ => (x, y)
    };
}
