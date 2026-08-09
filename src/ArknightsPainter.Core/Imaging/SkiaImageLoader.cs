using SkiaSharp;
using Svg.Skia;
using System.Xml;

namespace ArknightsPainter.Core.Imaging;

public static class SkiaImageLoader
{
    private const long MaximumSvgFileBytes = 8 * 1024 * 1024;
    private const int SvgRasterLongEdge = 2048;

    public static SKBitmap LoadOriented(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return LoadSvg(path);
        }

        return LoadRasterOriented(path);
    }

    private static SKBitmap LoadRasterOriented(string path)
    {
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

    private static SKBitmap LoadSvg(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("SVG 文件不存在。", path);
        }

        if (file.Length is <= 0 or > MaximumSvgFileBytes)
        {
            throw new InvalidDataException($"SVG 文件必须小于 {MaximumSvgFileBytes / 1024 / 1024} MB。");
        }

        var data = File.ReadAllBytes(path);
        ValidateSvg(data);

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var svg = new SKSvg();
            var picture = svg.Load(stream)
                ?? throw new InvalidDataException("SVG 中没有可渲染的图形。");
            var bounds = picture.CullRect;
            if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidDataException("SVG 缺少有效的宽高或 viewBox。");
            }

            var scale = SvgRasterLongEdge / Math.Max(bounds.Width, bounds.Height);
            var width = Math.Clamp((int)Math.Round(bounds.Width * scale), 1, SvgRasterLongEdge);
            var height = Math.Clamp((int)Math.Round(bounds.Height * scale), 1, SvgRasterLongEdge);
            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / bounds.Width, height / bounds.Height);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
            return bitmap;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("SVG 解码失败，文件可能已损坏或包含不支持的内容。", exception);
        }
    }

    private static void ValidateSvg(byte[] data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersInDocument = MaximumSvgFileBytes * 2
        };

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            var foundRoot = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (!foundRoot && reader.Depth == 0)
                    {
                        foundRoot = true;
                        if (!string.Equals(reader.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("文件根元素不是 SVG。");
                        }
                    }

                    if (string.Equals(reader.LocalName, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("SVG 不允许包含脚本。");
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("SVG 不允许包含事件脚本。");
                            }

                            if (string.Equals(reader.LocalName, "href", StringComparison.OrdinalIgnoreCase) &&
                                !IsSafeEmbeddedReference(reader.Value))
                            {
                                throw new InvalidDataException("SVG 不允许加载外部图片或文件。");
                            }

                            if (ContainsUnsafeCssReference(reader.Value))
                            {
                                throw new InvalidDataException("SVG 不允许加载外部样式资源。");
                            }
                        }

                        reader.MoveToElement();
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA &&
                         ContainsUnsafeCssReference(reader.Value))
                {
                    throw new InvalidDataException("SVG 不允许加载外部样式资源。");
                }
            }

            if (!foundRoot)
            {
                throw new InvalidDataException("SVG 文件为空。");
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("SVG XML 格式无效。", exception);
        }
    }

    private static bool IsSafeEmbeddedReference(string value)
    {
        var reference = value.Trim().Trim('\'', '"');
        return reference.Length == 0 ||
               reference.StartsWith('#') ||
               reference.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase) ||
               reference.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) ||
               reference.StartsWith("data:image/webp", StringComparison.OrdinalIgnoreCase) ||
               reference.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase) ||
               reference.StartsWith("data:image/bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsUnsafeCssReference(string value)
    {
        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var searchFrom = 0;
        while (searchFrom < value.Length)
        {
            var urlIndex = value.IndexOf("url", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (urlIndex < 0)
            {
                return false;
            }

            var openingParenthesis = urlIndex + 3;
            while (openingParenthesis < value.Length && char.IsWhiteSpace(value[openingParenthesis]))
            {
                openingParenthesis++;
            }

            if (openingParenthesis >= value.Length || value[openingParenthesis] != '(')
            {
                searchFrom = openingParenthesis;
                continue;
            }

            var closingParenthesis = value.IndexOf(')', openingParenthesis + 1);
            if (closingParenthesis < 0)
            {
                return true;
            }

            var reference = value[(openingParenthesis + 1)..closingParenthesis];
            if (!IsSafeEmbeddedReference(reference))
            {
                return true;
            }

            searchFrom = closingParenthesis + 1;
        }

        return false;
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
