using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Adb;

public sealed class AdbClient : IAdbClient
{
    public AdbClient(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("adb.exe was not found.", executablePath);
        }

        ExecutablePath = Path.GetFullPath(executablePath);
    }

    public string ExecutablePath { get; }

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunTextAsync(["devices", "-l"], cancellationToken, TimeSpan.FromSeconds(10));
        var devices = new List<AdbDevice>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var fields = parts.Skip(2)
                .Select(part => part.Split(':', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            var state = parts[1].ToLowerInvariant() switch
            {
                "device" => AdbDeviceState.Device,
                "offline" => AdbDeviceState.Offline,
                "unauthorized" => AdbDeviceState.Unauthorized,
                _ => AdbDeviceState.Unknown
            };
            fields.TryGetValue("model", out var model);
            fields.TryGetValue("product", out var product);
            devices.Add(new AdbDevice(parts[0], state, model ?? string.Empty, product ?? string.Empty, line));
        }

        return devices;
    }

    public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default) =>
        RunTextAsync(["connect", endpoint], cancellationToken, TimeSpan.FromSeconds(30));

    public async Task<byte[]> CaptureScreenshotAsync(string serial, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CaptureScreenshotCoreAsync(serial, cancellationToken);
        }
        catch (AdbCommandException) when (serial.Contains(':'))
        {
            await ConnectAsync(serial, cancellationToken);
            return await CaptureScreenshotCoreAsync(serial, cancellationToken);
        }
    }

    private async Task<byte[]> CaptureScreenshotCoreAsync(string serial, CancellationToken cancellationToken)
    {
        var direct = await RunBinaryAsync(
            ["-s", serial, "exec-out", "screencap", "-p"],
            cancellationToken,
            TimeSpan.FromSeconds(12));
        if (ScreenshotImage.TryNormalize(direct, out var normalized))
        {
            return normalized;
        }

        var remotePath = $"/data/local/tmp/arknights-painter-{Guid.NewGuid():N}.png";
        try
        {
            await RunTextAsync(
                ["-s", serial, "shell", "screencap", "-p", remotePath],
                cancellationToken,
                TimeSpan.FromSeconds(12));
            var fallback = await RunBinaryAsync(
                ["-s", serial, "exec-out", "cat", remotePath],
                cancellationToken,
                TimeSpan.FromSeconds(12));
            if (ScreenshotImage.TryNormalize(fallback, out normalized))
            {
                return normalized;
            }

            throw new AdbCommandException(
                "ADB 返回的截图无法解码。",
                $"direct: {ScreenshotImage.Describe(direct)}; fallback: {ScreenshotImage.Describe(fallback)}");
        }
        finally
        {
            try
            {
                await RunTextAsync(
                    ["-s", serial, "shell", "rm", "-f", remotePath],
                    CancellationToken.None,
                    TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }
        }
    }

    public async Task<(int Width, int Height)> GetScreenSizeAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        var output = await RunTextAsync(["-s", serial, "shell", "wm", "size"], cancellationToken);
        var sizeText = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.Contains('x'))
            ?.Split(':').Last().Trim();
        var dimensions = sizeText?.Split('x');
        return dimensions is { Length: 2 } &&
               int.TryParse(dimensions[0], out var width) &&
               int.TryParse(dimensions[1], out var height)
            ? (width, height)
            : throw new AdbCommandException("Unable to parse device screen size.", output);
    }

    public Task TapAsync(string serial, PixelPoint point, CancellationToken cancellationToken = default) =>
        point.X < 0 || point.Y < 0
            ? Task.FromException(new ArgumentOutOfRangeException(nameof(point), "Touch coordinates cannot be negative."))
            : RunTextAsync(
                ["-s", serial, "shell", "input", "tap", point.X.ToString(), point.Y.ToString()],
                cancellationToken);

    public Task TapBatchAsync(
        string serial,
        IReadOnlyList<PixelPoint> points,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (points.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (points.Any(point => point.X < 0 || point.Y < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(points), "Touch coordinates cannot be negative.");
        }

        var seconds = Math.Max(0, delay.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var commands = points.Select(point => $"input tap {point.X} {point.Y}; sleep {seconds}");
        return RunTextAsync(["-s", serial, "shell", "sh", "-c", string.Join("; ", commands)], cancellationToken);
    }

    public Task SwipeAsync(
        string serial,
        PixelPoint from,
        PixelPoint to,
        int durationMilliseconds,
        CancellationToken cancellationToken = default) =>
        from.X < 0 || from.Y < 0 || to.X < 0 || to.Y < 0
            ? Task.FromException(new ArgumentOutOfRangeException(nameof(from), "Touch coordinates cannot be negative."))
            : RunTextAsync(
            [
                "-s", serial, "shell", "input", "swipe",
                from.X.ToString(), from.Y.ToString(), to.X.ToString(), to.Y.ToString(),
                durationMilliseconds.ToString()
            ],
            cancellationToken);

    private async Task<string> RunTextAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var process = Start(arguments);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new AdbCommandException("ADB command timed out.", string.Join(' ', arguments));
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new AdbCommandException($"ADB exited with code {process.ExitCode}.", stderr);
        }

        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private async Task<byte[]> RunBinaryAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var process = Start(arguments);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var buffer = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(timeoutSource.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new AdbCommandException("ADB screenshot timed out.", string.Join(' ', arguments));
        }
        var stderr = await stderrTask;
        if (process.ExitCode != 0 || buffer.Length == 0)
        {
            throw new AdbCommandException($"ADB screenshot failed with code {process.ExitCode}.", stderr);
        }

        return buffer.ToArray();
    }

    private Process Start(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new AdbCommandException("Unable to start adb.exe.", string.Empty);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class AdbCommandException(string message, string details) : Exception(message)
{
    public string Details { get; } = details;
}
