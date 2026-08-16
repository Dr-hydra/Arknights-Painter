using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Imaging;

public sealed class SkiaImageQuantizer : IImageQuantizer
{
    public Task<Artwork24> ConvertAsync(
        string imagePath,
        PaletteDefinition palette,
        ImageConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(palette);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Image file was not found.", imagePath);
        }

        return Task.Run(
            () => new Artwork24(Convert(imagePath, palette, options, Artwork24.Size, cancellationToken)),
            cancellationToken);
    }

    public Task<Artwork96> ConvertMosaicAsync(
        string imagePath,
        PaletteDefinition palette,
        ImageConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(palette);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Image file was not found.", imagePath);
        }

        return Task.Run(
            () => new Artwork96(Convert(imagePath, palette, options, Artwork96.Size, cancellationToken)),
            cancellationToken);
    }

    public byte[] RenderPreview(
        Artwork24 artwork,
        PaletteDefinition palette,
        int outputSize = 576,
        bool showGrid = true)
        => RenderPreview(Artwork24.Size, (column, row) => artwork[column, row], palette, outputSize, showGrid);

    public byte[] RenderPreview(
        Artwork96 artwork,
        PaletteDefinition palette,
        int outputSize = 768,
        bool showGrid = true)
        => RenderPreview(Artwork96.Size, (column, row) => artwork[column, row], palette, outputSize, showGrid);

    private static byte[] RenderPreview(
        int artworkSize,
        Func<int, int, int> getPaletteIndex,
        PaletteDefinition palette,
        int outputSize,
        bool showGrid)
    {
        if (outputSize < artworkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize));
        }

        using var bitmap = new SKBitmap(outputSize, outputSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var cell = (float)outputSize / artworkSize;
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
        for (var row = 0; row < artworkSize; row++)
        {
            for (var column = 0; column < artworkSize; column++)
            {
                var color = palette[getPaletteIndex(column, row)].Color;
                fill.Color = new SKColor(color.R, color.G, color.B);
                canvas.DrawRect(column * cell, row * cell, cell + 0.5f, cell + 0.5f, fill);
            }
        }

        if (showGrid)
        {
            using var grid = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                Color = new SKColor(0, 0, 0, 45),
                IsAntialias = false
            };
            for (var i = 0; i <= artworkSize; i++)
            {
                var position = i * cell;
                canvas.DrawLine(position, 0, position, outputSize, grid);
                canvas.DrawLine(0, position, outputSize, position, grid);
            }

            if (artworkSize == Artwork96.Size)
            {
                using var tileGrid = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3,
                    Color = new SKColor(0, 0, 0, 150),
                    IsAntialias = false
                };
                for (var tile = 0; tile <= Artwork96.TilesPerAxis; tile++)
                {
                    var position = tile * Artwork24.Size * cell;
                    canvas.DrawLine(position, 0, position, outputSize, tileGrid);
                    canvas.DrawLine(0, position, outputSize, position, tileGrid);
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static int[] Convert(
        string imagePath,
        PaletteDefinition palette,
        ImageConversionOptions options,
        int artworkSize,
        CancellationToken cancellationToken)
    {
        using var source = SkiaImageLoader.LoadOriented(imagePath);
        using var sampled = Sample(source, options, artworkSize, cancellationToken);
        ApplyColorAdjustments(sampled, options, cancellationToken);
        var indexes = options.Dither switch
        {
            DitherMode.None => QuantizeDirect(sampled, palette, cancellationToken),
            DitherMode.FloydSteinberg => QuantizeErrorDiffusion(
                sampled, palette, DitherMode.FloydSteinberg, cancellationToken),
            DitherMode.Atkinson => QuantizeErrorDiffusion(
                sampled, palette, DitherMode.Atkinson, cancellationToken),
            DitherMode.Bayer4x4 => QuantizeBayer4x4(sampled, palette, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown dithering mode.")
        };
        return indexes;
    }

    private static SKBitmap Sample(
        SKBitmap source,
        ImageConversionOptions options,
        int artworkSize,
        CancellationToken cancellationToken)
    {
        ValidateCrop(options);
        return options.Algorithm switch
        {
            PixelArtAlgorithm.Perceptual => ComposePerceptual(source, options, artworkSize),
            PixelArtAlgorithm.BeadAverage => ComposeBeadGrid(source, options, artworkSize, dominant: false, cancellationToken),
            PixelArtAlgorithm.BeadDominant => ComposeBeadGrid(source, options, artworkSize, dominant: true, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown pixel-art algorithm.")
        };
    }

    private static SKBitmap ComposePerceptual(SKBitmap source, ImageConversionOptions options, int artworkSize)
    {
        var sourceRect = CreateSourceRect(source, options);
        var output = new SKBitmap(artworkSize, artworkSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(new SKColor(options.Background.R, options.Background.G, options.Background.B));
        var target = CreateTargetRect(sourceRect, options.FitMode, artworkSize);

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, sourceRect, target, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Flush();
        return output;
    }

    private static SKBitmap ComposeBeadGrid(
        SKBitmap source,
        ImageConversionOptions options,
        int artworkSize,
        bool dominant,
        CancellationToken cancellationToken)
    {
        var sourceRect = CreateSourceRect(source, options);
        var target = CreateTargetRect(sourceRect, options.FitMode, artworkSize);
        var output = new SKBitmap(artworkSize, artworkSize, SKColorType.Bgra8888, SKAlphaType.Opaque);

        for (var y = 0; y < artworkSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < artworkSize; x++)
            {
                var color = dominant
                    ? SampleDominant(source, sourceRect, target, x, y, options.Background)
                    : SampleAverage(source, sourceRect, target, x, y, options.Background);
                output.SetPixel(x, y, new SKColor(color.R, color.G, color.B));
            }
        }

        return output;
    }

    private static RgbColor SampleAverage(
        SKBitmap source,
        SKRect sourceRect,
        SKRect target,
        int outputX,
        int outputY,
        RgbColor background)
    {
        var intersection = IntersectCell(target, outputX, outputY);
        if (intersection.Width <= 0 || intersection.Height <= 0)
        {
            return background;
        }

        var coverage = intersection.Width * intersection.Height;
        var sampleRect = MapToSource(intersection, sourceRect, target);
        var accumulator = AccumulateSource(source, sampleRect, background, buckets: null, totalWeight: 1);
        var backgroundWeight = 1 - coverage;
        return new RgbColor(
            ClampByte((accumulator.Red / accumulator.Weight * coverage) + (background.R * backgroundWeight)),
            ClampByte((accumulator.Green / accumulator.Weight * coverage) + (background.G * backgroundWeight)),
            ClampByte((accumulator.Blue / accumulator.Weight * coverage) + (background.B * backgroundWeight)));
    }

    private static RgbColor SampleDominant(
        SKBitmap source,
        SKRect sourceRect,
        SKRect target,
        int outputX,
        int outputY,
        RgbColor background)
    {
        var intersection = IntersectCell(target, outputX, outputY);
        if (intersection.Width <= 0 || intersection.Height <= 0)
        {
            return background;
        }

        var coverage = intersection.Width * intersection.Height;
        var buckets = new Dictionary<int, ColorAccumulator>();
        var sampleRect = MapToSource(intersection, sourceRect, target);
        AccumulateSource(source, sampleRect, background, buckets, coverage);
        if (coverage < 1)
        {
            AddBucket(buckets, background, 1 - coverage);
        }

        var winner = buckets
            .OrderByDescending(pair => pair.Value.Weight)
            .ThenBy(pair => pair.Key)
            .First().Value;
        return new RgbColor(
            ClampByte(winner.Red / winner.Weight),
            ClampByte(winner.Green / winner.Weight),
            ClampByte(winner.Blue / winner.Weight));
    }

    private static ColorAccumulator AccumulateSource(
        SKBitmap source,
        SKRect sampleRect,
        RgbColor background,
        Dictionary<int, ColorAccumulator>? buckets,
        double totalWeight)
    {
        var total = new ColorAccumulator();
        var weightScale = totalWeight / (sampleRect.Width * sampleRect.Height);
        var left = Math.Max(0, (int)Math.Floor(sampleRect.Left));
        var top = Math.Max(0, (int)Math.Floor(sampleRect.Top));
        var right = Math.Min(source.Width, (int)Math.Ceiling(sampleRect.Right));
        var bottom = Math.Min(source.Height, (int)Math.Ceiling(sampleRect.Bottom));

        for (var y = top; y < bottom; y++)
        {
            var overlapY = Math.Max(0, Math.Min(sampleRect.Bottom, y + 1) - Math.Max(sampleRect.Top, y));
            for (var x = left; x < right; x++)
            {
                var overlapX = Math.Max(0, Math.Min(sampleRect.Right, x + 1) - Math.Max(sampleRect.Left, x));
                var weight = overlapX * overlapY * weightScale;
                if (weight <= 0)
                {
                    continue;
                }

                var pixel = source.GetPixel(x, y);
                var alpha = pixel.Alpha / 255.0;
                var color = new RgbColor(
                    ClampByte((pixel.Red * alpha) + (background.R * (1 - alpha))),
                    ClampByte((pixel.Green * alpha) + (background.G * (1 - alpha))),
                    ClampByte((pixel.Blue * alpha) + (background.B * (1 - alpha))));
                total.Add(color, weight);
                if (buckets is not null)
                {
                    AddBucket(buckets, color, weight);
                }
            }
        }

        return total;
    }

    private static void AddBucket(Dictionary<int, ColorAccumulator> buckets, RgbColor color, double weight)
    {
        // Four bits per channel groups nearby photographic colors while preserving flat icon colors.
        var key = ((color.R >> 4) << 8) | ((color.G >> 4) << 4) | (color.B >> 4);
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new ColorAccumulator();
            buckets.Add(key, bucket);
        }

        bucket.Add(color, weight);
    }

    private static SKRect IntersectCell(SKRect target, int x, int y) => new(
        Math.Max(target.Left, x),
        Math.Max(target.Top, y),
        Math.Min(target.Right, x + 1),
        Math.Min(target.Bottom, y + 1));

    private static SKRect MapToSource(SKRect rect, SKRect sourceRect, SKRect target)
    {
        var scaleX = sourceRect.Width / target.Width;
        var scaleY = sourceRect.Height / target.Height;
        return new SKRect(
            sourceRect.Left + ((rect.Left - target.Left) * scaleX),
            sourceRect.Top + ((rect.Top - target.Top) * scaleY),
            sourceRect.Left + ((rect.Right - target.Left) * scaleX),
            sourceRect.Top + ((rect.Bottom - target.Top) * scaleY));
    }

    private static SKRect CreateSourceRect(SKBitmap source, ImageConversionOptions options)
    {
        var crop = options.Crop ?? ImageCropRect.Full;
        return new SKRect(
            (float)(crop.X * source.Width),
            (float)(crop.Y * source.Height),
            (float)(crop.Right * source.Width),
            (float)(crop.Bottom * source.Height));
    }

    private static SKRect CreateTargetRect(SKRect sourceRect, ImageFitMode fitMode, int artworkSize)
    {
        if (fitMode == ImageFitMode.Stretch)
        {
            return new SKRect(0, 0, artworkSize, artworkSize);
        }

        var scaleX = artworkSize / sourceRect.Width;
        var scaleY = artworkSize / sourceRect.Height;
        var scale = fitMode == ImageFitMode.Contain
            ? Math.Min(scaleX, scaleY)
            : Math.Max(scaleX, scaleY);
        var width = sourceRect.Width * scale;
        var height = sourceRect.Height * scale;
        return new SKRect(
            (artworkSize - width) / 2,
            (artworkSize - height) / 2,
            (artworkSize + width) / 2,
            (artworkSize + height) / 2);
    }

    private static void ValidateCrop(ImageConversionOptions options)
    {
        var crop = options.Crop ?? ImageCropRect.Full;
        if (!crop.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image crop must stay inside normalized image bounds.");
        }
    }

    private static void ApplyColorAdjustments(
        SKBitmap bitmap,
        ImageConversionOptions options,
        CancellationToken cancellationToken)
    {
        ValidateAdjustment(options.Brightness, nameof(options.Brightness));
        ValidateAdjustment(options.Contrast, nameof(options.Contrast));
        ValidateAdjustment(options.Saturation, nameof(options.Saturation));
        if (Math.Abs(options.Brightness) < 0.000001 &&
            Math.Abs(options.Contrast) < 0.000001 &&
            Math.Abs(options.Saturation) < 0.000001)
        {
            return;
        }

        var brightnessOffset = options.Brightness * 2.55;
        var contrastFactor = Math.Pow(2, options.Contrast / 50.0);
        var saturationFactor = 1 + (options.Saturation / 100.0);
        for (var y = 0; y < bitmap.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var red = ((pixel.Red - 127.5) * contrastFactor) + 127.5 + brightnessOffset;
                var green = ((pixel.Green - 127.5) * contrastFactor) + 127.5 + brightnessOffset;
                var blue = ((pixel.Blue - 127.5) * contrastFactor) + 127.5 + brightnessOffset;
                var luminance = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
                red = luminance + ((red - luminance) * saturationFactor);
                green = luminance + ((green - luminance) * saturationFactor);
                blue = luminance + ((blue - luminance) * saturationFactor);
                bitmap.SetPixel(x, y, new SKColor(
                    ClampByte(red),
                    ClampByte(green),
                    ClampByte(blue),
                    pixel.Alpha));
            }
        }
    }

    private static void ValidateAdjustment(double value, string name)
    {
        if (!double.IsFinite(value) || value is < -100 or > 100)
        {
            throw new ArgumentOutOfRangeException(name, "Image adjustment must be between -100 and 100.");
        }
    }

    private static int[] QuantizeDirect(
        SKBitmap bitmap,
        PaletteDefinition palette,
        CancellationToken cancellationToken)
    {
        var size = bitmap.Width;
        var result = new int[size * size];
        for (var row = 0; row < size; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < size; column++)
            {
                var pixel = bitmap.GetPixel(column, row);
                result[(row * size) + column] = ColorMath.FindNearest(
                    new RgbColor(pixel.Red, pixel.Green, pixel.Blue), palette.Colors).Index;
            }
        }

        return result;
    }

    private static int[] QuantizeErrorDiffusion(
        SKBitmap bitmap,
        PaletteDefinition palette,
        DitherMode mode,
        CancellationToken cancellationToken)
    {
        var size = bitmap.Width;
        var channels = new double[size, size, 3];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                channels[x, y, 0] = pixel.Red;
                channels[x, y, 1] = pixel.Green;
                channels[x, y, 2] = pixel.Blue;
            }
        }

        var result = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            {
                var current = new RgbColor(
                    ClampByte(channels[x, y, 0]),
                    ClampByte(channels[x, y, 1]),
                    ClampByte(channels[x, y, 2]));
                var nearest = ColorMath.FindNearest(current, palette.Colors);
                result[(y * size) + x] = nearest.Index;
                var error = new[]
                {
                    channels[x, y, 0] - nearest.Color.R,
                    channels[x, y, 1] - nearest.Color.G,
                    channels[x, y, 2] - nearest.Color.B
                };
                if (mode == DitherMode.FloydSteinberg)
                {
                    AddError(channels, x + 1, y, error, 7.0 / 16, size);
                    AddError(channels, x - 1, y + 1, error, 3.0 / 16, size);
                    AddError(channels, x, y + 1, error, 5.0 / 16, size);
                    AddError(channels, x + 1, y + 1, error, 1.0 / 16, size);
                }
                else
                {
                    AddError(channels, x + 1, y, error, 1.0 / 8, size);
                    AddError(channels, x + 2, y, error, 1.0 / 8, size);
                    AddError(channels, x - 1, y + 1, error, 1.0 / 8, size);
                    AddError(channels, x, y + 1, error, 1.0 / 8, size);
                    AddError(channels, x + 1, y + 1, error, 1.0 / 8, size);
                    AddError(channels, x, y + 2, error, 1.0 / 8, size);
                }
            }
        }

        return result;
    }

    private static int[] QuantizeBayer4x4(
        SKBitmap bitmap,
        PaletteDefinition palette,
        CancellationToken cancellationToken)
    {
        int[,] matrix =
        {
            { 0, 8, 2, 10 },
            { 12, 4, 14, 6 },
            { 3, 11, 1, 9 },
            { 15, 7, 13, 5 }
        };
        var size = bitmap.Width;
        var result = new int[size * size];
        for (var y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var offset = (((matrix[y % 4, x % 4] + 0.5) / 16.0) - 0.5) * 48;
                var adjusted = new RgbColor(
                    ClampByte(pixel.Red + offset),
                    ClampByte(pixel.Green + offset),
                    ClampByte(pixel.Blue + offset));
                result[(y * size) + x] = ColorMath.FindNearest(adjusted, palette.Colors).Index;
            }
        }

        return result;
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static void AddError(double[,,] channels, int x, int y, double[] error, double weight, int size)
    {
        if (x < 0 || y < 0 || x >= size || y >= size)
        {
            return;
        }

        for (var channel = 0; channel < 3; channel++)
        {
            channels[x, y, channel] += error[channel] * weight;
        }
    }

    private sealed class ColorAccumulator
    {
        public double Red { get; private set; }

        public double Green { get; private set; }

        public double Blue { get; private set; }

        public double Weight { get; private set; }

        public void Add(RgbColor color, double weight)
        {
            Red += color.R * weight;
            Green += color.G * weight;
            Blue += color.B * weight;
            Weight += weight;
        }
    }
}
