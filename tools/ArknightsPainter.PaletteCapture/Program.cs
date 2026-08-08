using ArknightsPainter.Core.Adb;
using ArknightsPainter.Core.Automation;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using ArknightsPainter.Core.Vision;

return await PaletteCaptureProgram.RunAsync(args);

internal static class PaletteCaptureProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = ParseArguments(args);
            var adbPath = AdbPathResolver.Find(Get(options, "adb"))
                ?? throw new FileNotFoundException("找不到 adb.exe，请通过 --adb 指定路径。");
            var adb = new AdbClient(adbPath);
            var devices = await adb.GetDevicesAsync();
            var serial = Get(options, "serial")
                ?? devices.SingleOrDefault(device => device.State == AdbDeviceState.Device)?.Serial
                ?? throw new InvalidOperationException("请保持唯一在线设备，或通过 --serial 指定设备。");
            var selected = devices.FirstOrDefault(device => device.Serial == serial);
            if (selected?.State != AdbDeviceState.Device)
            {
                throw new InvalidOperationException($"设备 {serial} 当前不是 device 状态。");
            }

            var vision = new PaletteVision();
            var locator = new ScreenLocator();
            var screenshot = await adb.CaptureScreenshotAsync(serial);
            var location = locator.Locate(serial, screenshot);
            var profile = location.Profile;
            if (profile is null)
            {
                throw new InvalidOperationException(
                    "自动识别失败。先在桌面应用中完成手动校准，或确保当前为目标绘画页面。" );
            }

            if (TryParseRect(Get(options, "palette"), out var paletteRect))
            {
                profile = profile with { PaletteViewport = paletteRect, Confidence = 1 };
            }

            Console.WriteLine($"设备：{serial}");
            Console.WriteLine($"颜料区域：{profile.PaletteViewport}");
            var navigator = new PaletteNavigator(adb, vision);
            Console.WriteLine("正在将颜料列表滚动到顶部…");
            await navigator.ResetToTopAsync(serial, profile);

            var colors = new List<RgbColor>();
            string? previousPage = null;
            var stablePages = 0;
            for (var page = 0; page < 40 && stablePages < 2; page++)
            {
                screenshot = await adb.CaptureScreenshotAsync(serial);
                var swatches = vision.ReadVisibleSwatches(screenshot, profile.PaletteViewport)
                    .OrderBy(swatch => swatch.VisibleRow)
                    .ThenBy(swatch => swatch.Column)
                    .Select(swatch => swatch.Color)
                    .ToArray();
                var pageSignature = string.Join(',', swatches.Select(color => color.Hex));
                AppendWithOverlap(colors, swatches);
                Console.WriteLine($"第 {page + 1} 页：累计 {colors.Count} 色");
                stablePages = pageSignature == previousPage ? stablePages + 1 : 0;
                previousPage = pageSignature;
                if (stablePages >= 2)
                {
                    break;
                }

                var region = profile.PaletteViewport;
                await adb.SwipeAsync(
                    serial,
                    new PixelPoint(region.Center.X, region.Bottom - (int)(region.Height * 0.14)),
                    new PixelPoint(region.Center.X, region.Y + (int)(region.Height * 0.24)),
                    320);
                await Task.Delay(220);
            }

            if (colors.Count < 24 || stablePages < 2)
            {
                throw new InvalidOperationException("未可靠到达颜料列表底部，拒绝生成不完整色板。");
            }

            var entries = colors.Select((color, index) =>
                new PaletteColor(index, $"颜料 {index + 1:D2}", color)).ToList();
            var unsigned = new PaletteDefinition
            {
                Version = DateTimeOffset.Now.ToString("yyyy.MM.dd"),
                Columns = 4,
                Complete = true,
                Colors = entries
            };
            var palette = new PaletteDefinition
            {
                Version = unsigned.Version,
                Columns = unsigned.Columns,
                Complete = true,
                Colors = entries,
                Signature = unsigned.ComputeSignature()
            };
            var output = Path.GetFullPath(Get(options, "output") ?? "palette.v1.json");
            palette.Save(output);
            Console.WriteLine($"完成：{entries.Count} 色，签名 {palette.Signature}");
            Console.WriteLine($"已写入 {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"采集失败：{ex.Message}");
            return 1;
        }
    }

    private static void AppendWithOverlap(List<RgbColor> collected, IReadOnlyList<RgbColor> page)
    {
        var maximum = Math.Min(collected.Count, page.Count);
        var overlap = 0;
        for (var length = maximum; length > 0; length--)
        {
            var matches = true;
            for (var index = 0; index < length; index++)
            {
                if (ColorMath.DeltaE2000(collected[collected.Count - length + index], page[index]) > 2.5)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                overlap = length;
                break;
            }
        }

        foreach (var color in page.Skip(overlap))
        {
            if (collected.Count == 0 || ColorMath.DeltaE2000(collected[^1], color) > 0.5)
            {
                collected.Add(color);
            }
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[index][2..];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"参数 --{key} 缺少值。");
            }

            result[key] = args[++index];
        }

        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : null;

    private static bool TryParseRect(string? value, out PixelRect rect)
    {
        rect = default;
        var parts = value?.Split(',');
        if (parts is not { Length: 4 } || parts.Any(part => !int.TryParse(part, out _)))
        {
            return false;
        }

        rect = new PixelRect(
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            int.Parse(parts[2]),
            int.Parse(parts[3]));
        return rect.IsValid;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ArknightsPainter.PaletteCapture

            用法：
              dotnet run --project tools/ArknightsPainter.PaletteCapture -- \
                --serial 127.0.0.1:16384 \
                --output src/ArknightsPainter.App/Assets/palette.v1.json

            可选参数：
              --adb <路径>             指定 adb.exe
              --serial <设备序列号>    指定在线设备
              --palette x,y,w,h        覆盖自动识别的颜料区域
              --output <路径>          输出 JSON，默认 palette.v1.json
            """);
    }
}
