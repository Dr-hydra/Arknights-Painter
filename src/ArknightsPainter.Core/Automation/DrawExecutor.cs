using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using SkiaSharp;

namespace ArknightsPainter.Core.Automation;

public sealed class DrawExecutor(
    IAdbClient adb,
    IScreenLocator locator,
    IPaletteVision paletteVision,
    IPaletteNavigator paletteNavigator) : IDrawExecutor
{
    public async Task ExecuteAsync(
        string serial,
        CalibrationProfile profile,
        PaletteDefinition palette,
        DrawPlan plan,
        DrawExecutionOptions options,
        PauseController pauseController,
        IProgress<DrawProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var completed = 0;
        try
        {
            Report(DrawStage.Validating, "正在验证设备画面…");
            var screenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
            if (locator.ScoreCanvas(screenshot, profile.CanvasBounds) < 0.25)
            {
                throw new InvalidOperationException("当前画面与已校准的 24×24 画布不匹配，已停止绘制。");
            }

            await paletteNavigator.ResetToTopAsync(serial, profile, cancellationToken);
            screenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
            if (!paletteVision.ValidateVisiblePalette(screenshot, profile.PaletteViewport, palette))
            {
                throw new InvalidOperationException("当前颜料与内置色板签名不匹配，已停止绘制。");
            }

            foreach (var step in plan.Steps)
            {
                await WaitForResumeAsync(step.Color.Index);
                Report(DrawStage.SelectingColor, $"选择颜料 {step.Color.Name}", step.Color.Index);
                await paletteNavigator.SelectColorAsync(serial, profile, step.Color, cancellationToken);

                foreach (var batch in step.Cells.Chunk(Math.Max(1, options.BatchSize)))
                {
                    await WaitForResumeAsync(step.Color.Index);
                    var points = batch.Select(cell => profile.CanvasBounds.GridCenter(cell)).ToArray();
                    await adb.TapBatchAsync(serial, points, options.EffectiveTapDelay, cancellationToken);
                    completed += batch.Length;
                    Report(DrawStage.Painting, $"正在绘制 {step.Color.Name}", step.Color.Index);
                }

                Report(DrawStage.Verifying, $"正在校验 {step.Color.Name}", step.Color.Index);
                var missing = await FindMissingCellsAsync(serial, profile, step, cancellationToken);
                for (var retry = 0; missing.Count > 0 && retry < options.VerificationRetries; retry++)
                {
                    foreach (var cell in missing)
                    {
                        await WaitForResumeAsync(step.Color.Index);
                        var point = profile.CanvasBounds.GridCenter(cell);
                        await adb.SwipeAsync(serial, point, point, 80, cancellationToken);
                        await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
                    }

                    missing = await FindMissingCellsAsync(serial, profile, step, cancellationToken);
                }

                if (missing.Count > 0)
                {
                    throw new DrawingVerificationException(step.Color, missing);
                }
            }

            Report(DrawStage.Completed, "24×24 绘制完成。", null, plan.TotalCells);
        }
        catch (OperationCanceledException)
        {
            Report(DrawStage.Cancelled, "绘制已取消。", null, completed);
            throw;
        }
        catch (Exception ex)
        {
            Report(DrawStage.Failed, ex.Message, null, completed);
            throw;
        }

        async Task WaitForResumeAsync(int paletteIndex)
        {
            if (pauseController.IsPaused)
            {
                Report(DrawStage.Paused, "绘制已暂停。", paletteIndex);
            }

            await pauseController.WaitIfPausedAsync(cancellationToken);
        }

        void Report(DrawStage stage, string message, int? paletteIndex = null, int? count = null) =>
            progress?.Report(new DrawProgress(stage, count ?? completed, plan.TotalCells, message, paletteIndex));
    }

    private async Task<List<GridPoint>> FindMissingCellsAsync(
        string serial,
        CalibrationProfile profile,
        DrawColorStep step,
        CancellationToken cancellationToken)
    {
        await Task.Delay(160, cancellationToken);
        var screenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
        using var bitmap = SKBitmap.Decode(screenshot)
            ?? throw new InvalidDataException("无法解码绘制校验截图。");
        var radius = Math.Max(1, profile.CanvasBounds.Width / (Artwork24.Size * 7));
        return step.Cells.Where(cell =>
        {
            var sampled = SampleCell(bitmap, profile.CanvasBounds.GridCenter(cell), radius);
            return ColorMath.DeltaE2000(sampled, step.Color.Color) > 12;
        }).ToList();
    }

    private static RgbColor SampleCell(SKBitmap bitmap, PixelPoint center, int radius)
    {
        var reds = new List<byte>();
        var greens = new List<byte>();
        var blues = new List<byte>();
        for (var y = center.Y - radius; y <= center.Y + radius; y += Math.Max(1, radius))
        {
            for (var x = center.X - radius; x <= center.X + radius; x += Math.Max(1, radius))
            {
                if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                {
                    continue;
                }

                var pixel = bitmap.GetPixel(x, y);
                reds.Add(pixel.Red);
                greens.Add(pixel.Green);
                blues.Add(pixel.Blue);
            }
        }

        reds.Sort();
        greens.Sort();
        blues.Sort();
        var middle = reds.Count / 2;
        return reds.Count == 0 ? default : new RgbColor(reds[middle], greens[middle], blues[middle]);
    }
}

public sealed class DrawingVerificationException(PaletteColor color, IReadOnlyList<GridPoint> missingCells)
    : Exception(CreateMessage(color, missingCells))
{
    public PaletteColor Color { get; } = color;

    public IReadOnlyList<GridPoint> MissingCells { get; } = missingCells;

    private static string CreateMessage(PaletteColor color, IReadOnlyList<GridPoint> missingCells)
    {
        var coordinates = string.Join(", ", missingCells.Take(12).Select(cell => $"({cell.Column + 1},{cell.Row + 1})"));
        var suffix = missingCells.Count > 12 ? "…" : string.Empty;
        return $"颜料 {color.Name} 有 {missingCells.Count} 个格子校验失败，绘制已暂停。未通过位置：{coordinates}{suffix}";
    }
}
