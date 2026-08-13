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
    private const int MinimumSwipeRunLength = 3;

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
        var effectivePlan = plan;
        try
        {
            byte[]? canvasScreenshot = null;
            if (options.SkipVisualValidation)
            {
                Report(DrawStage.Validating, "已启用强制绘制，跳过视觉校验。");
            }
            else
            {
                Report(DrawStage.Validating, "正在验证设备画面…");
                canvasScreenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
                if (locator.ScoreCanvas(canvasScreenshot, profile.CanvasBounds) < 0.25)
                {
                    throw new InvalidOperationException("当前画面与已校准的 24×24 画布不匹配，已停止绘制。");
                }
            }

            if (options.UseCanvasValidation)
            {
                Report(DrawStage.Validating, "正在读取当前画布并校验待绘制格子。");
                canvasScreenshot ??= await adb.CaptureScreenshotAsync(serial, cancellationToken);
                using var bitmap = SKBitmap.Decode(canvasScreenshot)
                    ?? throw new InvalidDataException("无法解码画布校验截图。");
                var before = effectivePlan.TotalCells;
                effectivePlan = FilterMatchingCells(effectivePlan, bitmap, profile.CanvasBounds);
                plan = effectivePlan;
                var skipped = before - effectivePlan.TotalCells;
                Report(
                    DrawStage.Validating,
                    $"画布校验完成：跳过 {skipped} 个已匹配格，剩余 {effectivePlan.TotalCells} 个待绘制格。",
                    null,
                    completed);
            }

            plan = effectivePlan;
            await paletteNavigator.ResetToTopAsync(serial, profile, cancellationToken);
            if (!options.SkipVisualValidation)
            {
                var screenshot = await adb.CaptureScreenshotAsync(serial, cancellationToken);
                if (!paletteVision.ValidateVisiblePalette(screenshot, profile.PaletteViewport, palette))
                {
                    throw new InvalidOperationException("当前颜料与内置色板签名不匹配，已停止绘制。");
                }
            }

            foreach (var step in plan.Steps)
            {
                await WaitForResumeAsync(step.Color.Index);
                Report(DrawStage.SelectingColor, $"选择颜料 {step.Color.Name}", step.Color.Index);
                await paletteNavigator.SelectColorAsync(serial, profile, step.Color, cancellationToken);

                if (options.UseSwipeDrawing)
                {
                    var batchSize = Math.Max(1, options.BatchSize);
                    var pendingTaps = new List<GridPoint>(batchSize);
                    foreach (var run in CreateHorizontalRuns(step.Cells))
                    {
                        if (run.Length >= MinimumSwipeRunLength)
                        {
                            while (pendingTaps.Count > 0)
                            {
                                await WaitForResumeAsync(step.Color.Index);
                                var count = Math.Min(batchSize, pendingTaps.Count);
                                await PaintTapBatchAsync(serial, profile, pendingTaps, count, options, cancellationToken);
                                completed += count;
                                Report(DrawStage.Painting, $"正在点击绘制 {step.Color.Name}", step.Color.Index);
                            }

                            await WaitForResumeAsync(step.Color.Index);
                            var from = profile.CanvasBounds.GridCenter(run[0]);
                            var to = profile.CanvasBounds.GridCenter(run[^1]);
                            var duration = Math.Clamp(
                                (run.Length - 1) * Math.Max(1, options.SwipeCellDurationMilliseconds),
                                80,
                                2500);
                            await adb.SwipeAsync(serial, from, to, duration, cancellationToken);
                            if (options.EffectiveTapDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(options.EffectiveTapDelay, cancellationToken);
                            }

                            completed += run.Length;
                            Report(DrawStage.Painting, $"正在滑动绘制 {step.Color.Name}", step.Color.Index);
                            continue;
                        }

                        pendingTaps.AddRange(run);
                        while (pendingTaps.Count >= batchSize)
                        {
                            await WaitForResumeAsync(step.Color.Index);
                            await PaintTapBatchAsync(
                                serial,
                                profile,
                                pendingTaps,
                                batchSize,
                                options,
                                cancellationToken);
                            completed += batchSize;
                            Report(DrawStage.Painting, $"正在点击绘制 {step.Color.Name}", step.Color.Index);
                        }
                    }

                    while (pendingTaps.Count > 0)
                    {
                        await WaitForResumeAsync(step.Color.Index);
                        var count = Math.Min(batchSize, pendingTaps.Count);
                        await PaintTapBatchAsync(serial, profile, pendingTaps, count, options, cancellationToken);
                        completed += count;
                        Report(DrawStage.Painting, $"正在点击绘制 {step.Color.Name}", step.Color.Index);
                    }
                }
                else
                {
                    foreach (var batch in step.Cells.Chunk(Math.Max(1, options.BatchSize)))
                    {
                        await WaitForResumeAsync(step.Color.Index);
                        var points = batch.Select(cell => profile.CanvasBounds.GridCenter(cell)).ToArray();
                        await adb.TapBatchAsync(serial, points, options.EffectiveTapDelay, cancellationToken);
                        completed += batch.Length;
                        Report(DrawStage.Painting, $"正在绘制 {step.Color.Name}", step.Color.Index);
                    }
                }

                if (!options.SkipVisualValidation)
                {
                    Report(DrawStage.Verifying, $"正在校验 {step.Color.Name}", step.Color.Index);
                    var missing = await FindMissingCellsAsync(serial, profile, step, cancellationToken);
                    for (var retry = 0; missing.Count > 0 && retry < options.VerificationRetries; retry++)
                    {
                        foreach (var cell in missing)
                        {
                            await WaitForResumeAsync(step.Color.Index);
                            var point = profile.CanvasBounds.GridCenter(cell);
                            await adb.TapAsync(serial, point, cancellationToken);
                            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
                        }

                        missing = await FindMissingCellsAsync(serial, profile, step, cancellationToken);
                    }

                    if (missing.Count > 0)
                    {
                        throw new DrawingVerificationException(step.Color, missing);
                    }
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

    private async Task PaintTapBatchAsync(
        string serial,
        CalibrationProfile profile,
        List<GridPoint> pending,
        int count,
        DrawExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var points = pending
            .Take(count)
            .Select(cell => profile.CanvasBounds.GridCenter(cell))
            .ToArray();
        pending.RemoveRange(0, count);
        await adb.TapBatchAsync(serial, points, options.EffectiveTapDelay, cancellationToken);
    }

    private static IReadOnlyList<GridPoint[]> CreateHorizontalRuns(IReadOnlyList<GridPoint> cells)
    {
        var ordered = cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var runs = new List<GridPoint[]>();
        var current = new List<GridPoint> { ordered[0] };
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var cell = ordered[index];
            if (cell.Row == previous.Row && cell.Column == previous.Column + 1)
            {
                current.Add(cell);
                continue;
            }

            runs.Add(current.ToArray());
            current = [cell];
        }

        runs.Add(current.ToArray());
        return runs;
    }

    private static DrawPlan FilterMatchingCells(
        DrawPlan plan,
        SKBitmap bitmap,
        PixelRect canvasBounds)
    {
        var cellWidth = canvasBounds.Width / (double)Artwork24.Size;
        var cellHeight = canvasBounds.Height / (double)Artwork24.Size;
        var steps = plan.Steps
            .Select(step => new DrawColorStep(
                step.Color,
                step.Cells
                    .Where(cell => !CellMatchesTarget(
                        bitmap,
                        canvasBounds.GridCenter(cell),
                        cellWidth,
                        cellHeight,
                        step.Color.Color,
                        strictRgbMatch: true,
                        requiredMatchRatio: 0.65))
                    .ToArray()))
            .Where(step => step.Cells.Count > 0)
            .ToArray();

        return new DrawPlan
        {
            Artwork = plan.Artwork,
            Steps = steps
        };
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
        var sampleWidth = profile.CanvasBounds.Width / (double)Artwork24.Size;
        var sampleHeight = profile.CanvasBounds.Height / (double)Artwork24.Size;
        return step.Cells.Where(cell =>
        {
            return !CellMatchesTarget(
                bitmap,
                profile.CanvasBounds.GridCenter(cell),
                sampleWidth,
                sampleHeight,
                step.Color.Color);
        }).ToList();
    }

    private static bool CellMatchesTarget(
        SKBitmap bitmap,
        PixelPoint center,
        double cellWidth,
        double cellHeight,
        RgbColor target,
        bool strictRgbMatch = false,
        double requiredMatchRatio = 0.35)
    {
        const int samplesPerAxis = 7;
        var horizontalRadius = Math.Max(1.0, cellWidth * 0.34);
        var verticalRadius = Math.Max(1.0, cellHeight * 0.34);
        var points = new HashSet<(int X, int Y)>();
        for (var row = 0; row < samplesPerAxis; row++)
        {
            var y = (int)Math.Round(center.Y - verticalRadius +
                                    ((2 * verticalRadius * row) / (samplesPerAxis - 1)));
            for (var column = 0; column < samplesPerAxis; column++)
            {
                var x = (int)Math.Round(center.X - horizontalRadius +
                                        ((2 * horizontalRadius * column) / (samplesPerAxis - 1)));
                if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                {
                    continue;
                }

                points.Add((x, y));
            }
        }

        if (points.Count == 0)
        {
            return false;
        }

        var matches = points.Count(point =>
        {
            var pixel = bitmap.GetPixel(point.X, point.Y);
            if (strictRgbMatch &&
                (Math.Abs(pixel.Red - target.R) > 10 ||
                 Math.Abs(pixel.Green - target.G) > 10 ||
                 Math.Abs(pixel.Blue - target.B) > 10))
            {
                return false;
            }

            return ColorMath.DeltaE2000(new RgbColor(pixel.Red, pixel.Green, pixel.Blue), target) <= 12;
        });
        return matches / (double)points.Count >= requiredMatchRatio;
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
