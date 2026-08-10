using SkiaSharp;

namespace ArknightsPainter.Core.Imaging;

public static class ScreenshotImage
{
    private const int MaximumPreambleLength = 4096;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    public static bool TryNormalize(byte[] bytes, out byte[] normalized)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        normalized = [];
        if (bytes.Length == 0)
        {
            return false;
        }

        var offset = FindImageOffset(bytes);
        if (offset < 0)
        {
            return false;
        }

        var candidate = offset == 0 ? bytes : bytes[offset..];
        using var data = SKData.CreateCopy(candidate);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return false;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static string Describe(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var headerLength = Math.Min(bytes.Length, 16);
        return $"bytes={bytes.Length}, header={Convert.ToHexString(bytes.AsSpan(0, headerLength))}";
    }

    private static int FindImageOffset(ReadOnlySpan<byte> bytes)
    {
        var limit = Math.Min(bytes.Length, MaximumPreambleLength);
        for (var offset = 0; offset < limit; offset++)
        {
            var remaining = bytes[offset..];
            if (remaining.StartsWith(PngSignature) || remaining.StartsWith(JpegSignature) || IsWebP(remaining))
            {
                return offset;
            }
        }

        return -1;
    }

    private static bool IsWebP(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 &&
        bytes[..4].SequenceEqual("RIFF"u8) &&
        bytes.Slice(8, 4).SequenceEqual("WEBP"u8);
}
