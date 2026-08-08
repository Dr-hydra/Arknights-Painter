using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Vision;

public sealed class ScreenLocator : IScreenLocator
{
    private const double ReferenceWidth = 1920;
    private const double ReferenceHeight = 1080;
    private const double ReferenceCanvasX = 443;
    private const double ReferenceCanvasY = 180;
    private const double ReferenceCanvasWidth = 844;
    private const double ReferenceCanvasHeight = 842;

    public ScreenLocationResult Locate(string deviceSerial, byte[] screenshotPng)
    {
        using var bitmap = SKBitmap.Decode(screenshotPng);
        if (bitmap is null)
        {
            return new ScreenLocationResult(false, null, 0, "无法解码设备截图。");
        }

        var predicted = new PixelRect(
            (int)Math.Round(bitmap.Width * (ReferenceCanvasX / ReferenceWidth)),
            (int)Math.Round(bitmap.Height * (ReferenceCanvasY / ReferenceHeight)),
            (int)Math.Round(bitmap.Width * (ReferenceCanvasWidth / ReferenceWidth)),
            (int)Math.Round(bitmap.Height * (ReferenceCanvasHeight / ReferenceHeight)));
        var predictedCell = (predicted.Width + predicted.Height) / (Artwork24.Size * 2.0);
        var searchRadius = Math.Max(8, (int)Math.Round(predictedCell * 0.75));
        var anchorBounds = predicted;
        var anchorEvidence = DetectAnchors(bitmap, anchorBounds);
        var bestBounds = RefineBounds(bitmap, predicted, searchRadius);
        var bestScore = ScoreCanvas(bitmap, bestBounds);
        var anchors = anchorEvidence;

        if (bestScore < 0.30 || anchors.Hits < 4 || anchors.CrossHits < 2 || !bestBounds.IsValid)
        {
            return new ScreenLocationResult(false, null, bestScore,
                anchors.Hits < 4 || anchors.CrossHits < 2
                    ? "未检测到足够的画板定位点，请确认绘画页面已打开后重试。"
                    : "未可靠识别 24×24 网格，请使用手动校准。");
        }

        var palette = new PixelRect(
            (int)Math.Round(bitmap.Width * (1433 / ReferenceWidth)),
            (int)Math.Round(bitmap.Height * (377 / ReferenceHeight)),
            (int)Math.Round(bitmap.Width * (420 / ReferenceWidth)),
            (int)Math.Round(bitmap.Height * (650 / ReferenceHeight)));
        var profile = new CalibrationProfile(
            deviceSerial,
            bitmap.Width,
            bitmap.Height,
            bestBounds,
            palette,
            bestScore,
            DateTimeOffset.UtcNow);
        return new ScreenLocationResult(true, profile, bestScore, "已通过定位点和网格自动识别画布与颜料区域。");
    }

    public double ScoreCanvas(byte[] screenshotPng, PixelRect bounds)
    {
        using var bitmap = SKBitmap.Decode(screenshotPng);
        return bitmap is null ? 0 : ScoreCanvas(bitmap, bounds);
    }

    private static double ScoreCanvas(SKBitmap bitmap, PixelRect bounds)
    {
        if (!bounds.IsValid || bounds.Right >= bitmap.Width || bounds.Bottom >= bitmap.Height ||
            Math.Abs(bounds.Width - bounds.Height) > Math.Max(bounds.Width, bounds.Height) * 0.04)
        {
            return 0;
        }

        var gridScore = ScoreGrid(bitmap, bounds);
        var frameScore = ScoreFrame(bitmap, bounds);
        var anchorScore = ScoreAnchorTemplate(bitmap, bounds);
        return Math.Clamp((gridScore * 0.62) + (frameScore * 0.13) + (anchorScore * 0.25), 0, 1);
    }

    private static PixelRect RefineBounds(SKBitmap bitmap, PixelRect predicted, int searchRadius)
    {
        var current = predicted;
        for (var iteration = 0; iteration < 2; iteration++)
        {
            current = SearchEdge(bitmap, current, CanvasEdge.Left, searchRadius);
            current = SearchEdge(bitmap, current, CanvasEdge.Right, searchRadius);
            current = SearchEdge(bitmap, current, CanvasEdge.Top, searchRadius);
            current = SearchEdge(bitmap, current, CanvasEdge.Bottom, searchRadius);
            searchRadius = Math.Max(3, searchRadius / 3);
        }

        var best = current;
        var bestScore = ScoreCanvas(bitmap, current);
        for (var leftDelta = -2; leftDelta <= 2; leftDelta++)
        {
            for (var rightDelta = -2; rightDelta <= 2; rightDelta++)
            {
                for (var topDelta = -2; topDelta <= 2; topDelta++)
                {
                    for (var bottomDelta = -2; bottomDelta <= 2; bottomDelta++)
                    {
                        var candidate = FromEdges(
                            current.X + leftDelta,
                            current.Y + topDelta,
                            current.Right + rightDelta,
                            current.Bottom + bottomDelta);
                        var score = ScoreCanvas(bitmap, candidate);
                        if (score > bestScore)
                        {
                            best = candidate;
                            bestScore = score;
                        }
                    }
                }
            }
        }

        return best;
    }

    private static PixelRect SearchEdge(
        SKBitmap bitmap,
        PixelRect bounds,
        CanvasEdge edge,
        int radius)
    {
        var origin = edge switch
        {
            CanvasEdge.Left => bounds.X,
            CanvasEdge.Right => bounds.Right,
            CanvasEdge.Top => bounds.Y,
            CanvasEdge.Bottom => bounds.Bottom,
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
        var best = bounds;
        var bestScore = ScoreCanvas(bitmap, bounds);
        for (var value = origin - radius; value <= origin + radius; value++)
        {
            var candidate = edge switch
            {
                CanvasEdge.Left => FromEdges(value, bounds.Y, bounds.Right, bounds.Bottom),
                CanvasEdge.Right => FromEdges(bounds.X, bounds.Y, value, bounds.Bottom),
                CanvasEdge.Top => FromEdges(bounds.X, value, bounds.Right, bounds.Bottom),
                CanvasEdge.Bottom => FromEdges(bounds.X, bounds.Y, bounds.Right, value),
                _ => bounds
            };
            var score = ScoreCanvas(bitmap, candidate);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    private static double ScoreGrid(SKBitmap bitmap, PixelRect bounds)
    {
        var offset = Math.Max(2, bounds.Width / (Artwork24.Size * 8));
        var samples = 0;
        double verticalContrast = 0;
        double horizontalContrast = 0;
        for (var line = 0; line <= Artwork24.Size; line++)
        {
            var x = bounds.X + (int)Math.Round(line * bounds.Width / (double)Artwork24.Size);
            var y = bounds.Y + (int)Math.Round(line * bounds.Height / (double)Artwork24.Size);
            for (var cell = 0; cell < Artwork24.Size; cell += 2)
            {
                var sampleY = bounds.Y + (int)Math.Round((cell + 0.5) * bounds.Height / Artwork24.Size);
                var sampleX = bounds.X + (int)Math.Round((cell + 0.5) * bounds.Width / Artwork24.Size);
                verticalContrast += LocalLineContrast(bitmap, x, sampleY, offset, horizontal: false);
                horizontalContrast += LocalLineContrast(bitmap, sampleX, y, offset, horizontal: true);
                samples++;
            }
        }

        var verticalAverage = verticalContrast / Math.Max(1, samples);
        var horizontalAverage = horizontalContrast / Math.Max(1, samples);
        var balancedAverage = Math.Sqrt(verticalAverage * horizontalAverage);
        return Math.Clamp(balancedAverage / 16.0, 0, 1);
    }

    private static double ScoreFrame(SKBitmap bitmap, PixelRect bounds)
    {
        var offset = Math.Max(2, bounds.Width / (Artwork24.Size * 8));
        double contrast = 0;
        var samples = 0;
        for (var index = 1; index < Artwork24.Size; index += 2)
        {
            var x = bounds.X + (int)Math.Round((index + 0.5) * bounds.Width / Artwork24.Size);
            var y = bounds.Y + (int)Math.Round((index + 0.5) * bounds.Height / Artwork24.Size);
            contrast += LocalLineContrast(bitmap, bounds.X, y, offset, horizontal: false);
            contrast += LocalLineContrast(bitmap, bounds.Right, y, offset, horizontal: false);
            contrast += LocalLineContrast(bitmap, x, bounds.Y, offset, horizontal: true);
            contrast += LocalLineContrast(bitmap, x, bounds.Bottom, offset, horizontal: true);
            samples += 4;
        }

        return Math.Clamp((contrast / Math.Max(1, samples)) / 48.0, 0, 1);
    }

    private static AnchorDetection DetectAnchors(SKBitmap bitmap, PixelRect bounds)
    {
        var scaleX = bounds.Width / ReferenceCanvasWidth;
        var scaleY = bounds.Height / ReferenceCanvasHeight;
        var scale = Math.Max(0.45, (scaleX + scaleY) / 2);
        var cornerBand = Math.Max(12, (int)Math.Round(72 * scale));
        var cornerGap = Math.Max(4, (int)Math.Round(6 * scale));
        var crossBand = Math.Max(12, (int)Math.Round(82 * scale));
        var crossArm = Math.Max(5, (int)Math.Round(14 * scale));
        var centerY = bounds.Y + (bounds.Height / 2);
        var cornerScores = new[]
        {
            BestHorizontalLine(bitmap, bounds.X - cornerBand, bounds.X - cornerGap, bounds.Y, scale),
            BestVerticalLine(bitmap, bounds.X, bounds.Y - cornerBand, bounds.Y - cornerGap, scale),
            BestHorizontalLine(bitmap, bounds.X - cornerBand, bounds.X - cornerGap, bounds.Bottom, scale),
            BestVerticalLine(bitmap, bounds.X, bounds.Bottom + cornerGap, bounds.Bottom + cornerBand, scale),
            BestHorizontalLine(bitmap, bounds.Right + cornerGap, bounds.Right + cornerBand, bounds.Y, scale),
            BestVerticalLine(bitmap, bounds.Right, bounds.Y - cornerBand, bounds.Y - cornerGap, scale),
            BestHorizontalLine(bitmap, bounds.Right + cornerGap, bounds.Right + cornerBand, bounds.Bottom, scale),
            BestVerticalLine(bitmap, bounds.Right, bounds.Bottom + cornerGap, bounds.Bottom + cornerBand, scale)
        };
        var leftCross = BestCross(bitmap, bounds.X - crossBand, bounds.X - cornerGap, centerY, crossArm, scale);
        var rightCross = BestCross(bitmap, bounds.Right + cornerGap, bounds.Right + crossBand, centerY, crossArm, scale);
        var scores = cornerScores.Append(leftCross).Append(rightCross).ToArray();
        var hits = scores.Count(score => score >= 0.20);
        var crossHits = new[] { leftCross, rightCross }.Count(score => score >= 0.20);
        return new AnchorDetection(
            Math.Clamp(scores.OrderByDescending(score => score).Take(6).Average(), 0, 1),
            hits,
            crossHits);
    }

    private static double ScoreAnchorTemplate(SKBitmap bitmap, PixelRect bounds)
    {
        var scaleX = bounds.Width / ReferenceCanvasWidth;
        var scaleY = bounds.Height / ReferenceCanvasHeight;
        var scale = Math.Max(0.45, (scaleX + scaleY) / 2);
        var cornerOffsetX = Math.Max(4, (int)Math.Round(18 * scaleX));
        var cornerSampleY = Math.Max(4, (int)Math.Round(12 * scaleY));
        var outerSampleX = Math.Max(cornerOffsetX + 2, (int)Math.Round(32 * scaleX));
        var crossGap = Math.Max(cornerOffsetX + 2, (int)Math.Round(28 * scaleX));
        var lineHalf = Math.Max(3, (int)Math.Round(8 * scale));
        var crossArm = Math.Max(3, (int)Math.Round(9 * scale));
        var centerY = bounds.Y + (bounds.Height / 2);
        var scores = new[]
        {
            LineDensity(bitmap, bounds.X - outerSampleX - lineHalf, bounds.X - outerSampleX + lineHalf, bounds.Y, horizontal: true),
            LineDensity(bitmap, bounds.X - cornerOffsetX, bounds.Y - cornerSampleY - lineHalf, bounds.Y - cornerSampleY + lineHalf, horizontal: false),
            LineDensity(bitmap, bounds.X - outerSampleX - lineHalf, bounds.X - outerSampleX + lineHalf, bounds.Bottom, horizontal: true),
            LineDensity(bitmap, bounds.X - cornerOffsetX, bounds.Bottom + cornerSampleY - lineHalf, bounds.Bottom + cornerSampleY + lineHalf, horizontal: false),
            LineDensity(bitmap, bounds.Right + outerSampleX - lineHalf, bounds.Right + outerSampleX + lineHalf, bounds.Y, horizontal: true),
            LineDensity(bitmap, bounds.Right + cornerOffsetX, bounds.Y - cornerSampleY - lineHalf, bounds.Y - cornerSampleY + lineHalf, horizontal: false),
            LineDensity(bitmap, bounds.Right + outerSampleX - lineHalf, bounds.Right + outerSampleX + lineHalf, bounds.Bottom, horizontal: true),
            LineDensity(bitmap, bounds.Right + cornerOffsetX, bounds.Bottom + cornerSampleY - lineHalf, bounds.Bottom + cornerSampleY + lineHalf, horizontal: false),
            LineDensity(bitmap, bounds.X - crossGap - crossArm, bounds.X - crossGap + crossArm, centerY, horizontal: true),
            LineDensity(bitmap, centerY - crossArm, centerY + crossArm, bounds.X - crossGap, horizontal: false),
            LineDensity(bitmap, bounds.Right + crossGap - crossArm, bounds.Right + crossGap + crossArm, centerY, horizontal: true),
            LineDensity(bitmap, centerY - crossArm, centerY + crossArm, bounds.Right + crossGap, horizontal: false)
        };

        return Math.Clamp(scores.OrderByDescending(score => score).Take(8).Average(), 0, 1);
    }

    private static double BestHorizontalLine(SKBitmap bitmap, int startX, int endX, int expectedY, double scale)
    {
        var radius = Math.Max(3, (int)Math.Round(5 * scale));
        var halfLength = Math.Max(3, (int)Math.Round(8 * scale));
        var best = 0.0;
        for (var center = startX; center <= endX; center++)
        {
            for (var y = expectedY - radius; y <= expectedY + radius; y++)
            {
                best = Math.Max(best, LineDensity(bitmap, center - halfLength, center + halfLength, y, horizontal: true));
            }
        }

        return best;
    }

    private static double BestVerticalLine(SKBitmap bitmap, int expectedX, int startY, int endY, double scale)
    {
        var radius = Math.Max(3, (int)Math.Round(5 * scale));
        var halfLength = Math.Max(3, (int)Math.Round(8 * scale));
        var best = 0.0;
        for (var center = startY; center <= endY; center++)
        {
            for (var x = expectedX - radius; x <= expectedX + radius; x++)
            {
                best = Math.Max(best, LineDensity(bitmap, center - halfLength, center + halfLength, x, horizontal: false));
            }
        }

        return best;
    }

    private static double BestCross(SKBitmap bitmap, int startX, int endX, int expectedY, int arm, double scale)
    {
        var radius = Math.Max(4, (int)Math.Round(8 * scale));
        var best = 0.0;
        for (var x = startX; x <= endX; x += 2)
        {
            for (var y = expectedY - radius; y <= expectedY + radius; y += 2)
            {
                var horizontal = LineDensity(bitmap, x - arm, x + arm, y, horizontal: true);
                var vertical = LineDensity(bitmap, y - arm, y + arm, x, horizontal: false);
                best = Math.Max(best, Math.Min(horizontal, vertical));
            }
        }

        return best;
    }

    private static double LineDensity(SKBitmap bitmap, int start, int end, int fixedCoordinate, bool horizontal)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var total = 0;
        var marker = 0;
        for (var coordinate = start; coordinate <= end; coordinate++)
        {
            var x = horizontal ? coordinate : fixedCoordinate;
            var y = horizontal ? fixedCoordinate : coordinate;
            if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
            {
                continue;
            }

            total++;
            var center = bitmap.GetPixel(x, y);
            var centerLuminance = Luminance(center);
            var first = horizontal
                ? LuminanceAt(bitmap, x, y - 4)
                : LuminanceAt(bitmap, x - 4, y);
            var second = horizontal
                ? LuminanceAt(bitmap, x, y + 4)
                : LuminanceAt(bitmap, x + 4, y);
            var surrounding = Math.Max(first, second);
            if (IsMarkerPixel(center) && surrounding - centerLuminance >= 18)
            {
                marker++;
            }
        }

        return total == 0 ? 0 : (double)marker / total;
    }

    private static double LuminanceAt(SKBitmap bitmap, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
        {
            return 0;
        }

        return Luminance(bitmap.GetPixel(x, y));
    }

    private static bool IsMarkerPixel(SKColor color)
    {
        var luminance = Luminance(color);
        var saturation = Math.Max(color.Red, Math.Max(color.Green, color.Blue)) -
                         Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        return luminance < 185 && saturation < 48;
    }

    private static double LocalLineContrast(SKBitmap bitmap, int x, int y, int offset, bool horizontal)
    {
        if (x - offset < 0 || x + offset >= bitmap.Width || y - offset < 0 || y + offset >= bitmap.Height)
        {
            return 0;
        }

        var center = Luminance(bitmap.GetPixel(x, y));
        var first = horizontal ? Luminance(bitmap.GetPixel(x, y - offset)) : Luminance(bitmap.GetPixel(x - offset, y));
        var second = horizontal ? Luminance(bitmap.GetPixel(x, y + offset)) : Luminance(bitmap.GetPixel(x + offset, y));
        return Math.Abs(center - ((first + second) / 2));
    }

    private static double Luminance(SKColor color) =>
        (0.2126 * color.Red) + (0.7152 * color.Green) + (0.0722 * color.Blue);

    private enum CanvasEdge { Left, Right, Top, Bottom }

    private readonly record struct AnchorDetection(double Score, int Hits, int CrossHits);
}
