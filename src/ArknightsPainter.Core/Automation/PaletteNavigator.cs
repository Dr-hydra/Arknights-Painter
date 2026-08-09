using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Automation;

public sealed class PaletteNavigator(
    IAdbClient adb,
    IPaletteVision vision,
    bool skipVisualValidation = false) : IPaletteNavigator
{
    private const int MaxScrolls = 30;

    public async Task ResetToTopAsync(
        string serial,
        CalibrationProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (skipVisualValidation)
        {
            var region = profile.PaletteViewport;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await adb.SwipeAsync(
                    serial,
                    new PixelPoint(region.Center.X, region.Y + (int)(region.Height * 0.25)),
                    new PixelPoint(region.Center.X, region.Bottom - (int)(region.Height * 0.12)),
                    260,
                    cancellationToken);
                await Task.Delay(100, cancellationToken);
            }

            return;
        }

        string? previousSignature = null;
        var stableCount = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
            var signature = Signature(vision.ReadVisibleSwatches(screenshot, profile.PaletteViewport));
            if (signature == previousSignature)
            {
                stableCount++;
                if (stableCount >= 2)
                {
                    return;
                }
            }
            else
            {
                stableCount = 0;
            }

            previousSignature = signature;
            var region = profile.PaletteViewport;
            await adb.SwipeAsync(
                serial,
                new PixelPoint(region.Center.X, region.Y + (int)(region.Height * 0.25)),
                new PixelPoint(region.Center.X, region.Bottom - (int)(region.Height * 0.12)),
                320,
                cancellationToken);
            await Task.Delay(180, cancellationToken);
        }

        throw new InvalidOperationException("无法确认颜料列表已经滚动到顶部。");
    }

    public async Task SelectColorAsync(
        string serial,
        CalibrationProfile profile,
        PaletteColor color,
        CancellationToken cancellationToken = default)
    {
        for (var scroll = 0; scroll <= MaxScrolls; scroll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var screenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
            var swatches = vision.ReadVisibleSwatches(screenshot, profile.PaletteViewport);
            var match = swatches
                .Select(swatch => new
                {
                    Swatch = swatch,
                    Distance = ColorMath.DeltaE2000(swatch.Color, color.Color)
                })
                .OrderBy(candidate => candidate.Distance)
                .FirstOrDefault();
            if (match is not null && match.Distance <= 9)
            {
                await adb.TapAsync(serial, match.Swatch.Center, cancellationToken);
                if (skipVisualValidation)
                {
                    await Task.Delay(100, cancellationToken);
                    return;
                }

                await Task.Delay(140, cancellationToken);
                var confirmation = await adb.CaptureScreenshotAsync(serial, cancellationToken);
                if (vision.VerifySelectionGlow(confirmation, profile.PaletteViewport, match.Swatch.Center))
                {
                    return;
                }

                await adb.TapAsync(serial, match.Swatch.Center, cancellationToken);
                await Task.Delay(180, cancellationToken);
                confirmation = await adb.CaptureScreenshotAsync(serial, cancellationToken);
                if (vision.VerifySelectionGlow(confirmation, profile.PaletteViewport, match.Swatch.Center))
                {
                    return;
                }

                throw new InvalidOperationException($"颜料 {color.Name} 未出现选中发光边框。");
            }

            if (scroll == MaxScrolls)
            {
                break;
            }

            var region = profile.PaletteViewport;
            await adb.SwipeAsync(
                serial,
                new PixelPoint(region.Center.X, region.Bottom - (int)(region.Height * 0.14)),
                new PixelPoint(region.Center.X, region.Y + (int)(region.Height * 0.24)),
                320,
                cancellationToken);
            await Task.Delay(180, cancellationToken);
        }

        throw new InvalidOperationException($"滚动颜料区域后仍未找到 {color.Name} ({color.Color.Hex})。");
    }

    private static string Signature(IReadOnlyList<VisibleSwatch> swatches) =>
        string.Join(',', swatches.Select(swatch => swatch.Color.Hex));
}
