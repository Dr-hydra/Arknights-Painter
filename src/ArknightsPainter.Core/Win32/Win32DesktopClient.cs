using System.Diagnostics;
using System.Runtime.InteropServices;
using ArknightsPainter.Core.Abstractions;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Win32;

/// <summary>
/// Adapter over MaaFramework's Win32 control unit. Maa owns the window-level
/// capture and input implementation; this class only translates it to the
/// application's existing device abstraction.
/// </summary>
public sealed class Win32DesktopClient : IAdbClient, IDisposable
{
    private const ulong ScreencapFramePool = 1UL << 1;
    private const ulong ScreencapPrintWindow = 1UL << 4;
    // The desktop game reads the real cursor position. Maa's cursor-position
    // message input keeps that behavior while retaining the window-message path.
    private const ulong InputSendMessageWithCursorPos = 1UL << 5;
    private const long InvalidControlId = 0;
    private const int StatusSucceeded = 3000;
    private const int ControllerOptionScreenshotTargetShortSide = 2;
    private const int ScreenshotTargetShortSide = 1080;

    private readonly object _sync = new();
    private readonly int _processId;
    private readonly IntPtr _windowHandle;
    private IntPtr _controller;
    private bool _disposed;

    public Win32DesktopClient(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        _processId = processId;
        _windowHandle = FindWindow(processId);
        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"未找到 PID {processId} 的窗口。");
        }

        MaaNativeLoader.EnsureLoaded();
        _controller = MaaWin32ControllerCreate(
            _windowHandle,
            ScreencapFramePool | ScreencapPrintWindow,
            InputSendMessageWithCursorPos,
            InputSendMessageWithCursorPos);
        if (_controller == IntPtr.Zero)
        {
            throw new InvalidOperationException("MaaFramework 无法创建 Win32 控制器，请确认 Maa DLL 完整且位数为 x64。");
        }

        try
        {
            SetScreenshotSizeOption();
            Execute(
                () => MaaControllerPostConnection(_controller),
                "连接电脑版窗口");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string ExecutablePath => "maa-win32";

    public int ProcessId => _processId;

    public string Serial => $"win32:{_processId}";

    public static IReadOnlyList<AdbDevice> DiscoverDevices()
    {
        var devices = new List<AdbDevice>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || GetWindowTextLength(handle) == 0 ||
                !GetClientRect(handle, out var rect) || rect.Right - rect.Left < 640 || rect.Bottom - rect.Top < 360)
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            // The application name contains "Arknights" as well. Do not
            // mistake this tool (or another instance of it) for the game.
            if (processId == Environment.ProcessId)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                var title = GetWindowTitle(handle);
                var name = process.ProcessName;
                if (name.Contains("ArknightsPainter", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!title.Contains("明日方舟", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("arknights", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                devices.Add(new AdbDevice(
                    $"win32:{processId}",
                    AdbDeviceState.Device,
                    string.IsNullOrWhiteSpace(title) ? name : title,
                    "win32",
                    $"窗口 0x{handle.ToInt64():X} · {rect.Right - rect.Left}×{rect.Bottom - rect.Top}"));
            }
            catch (ArgumentException)
            {
                // Process exited during enumeration.
            }

            return true;
        }, IntPtr.Zero);

        return devices
            .GroupBy(device => device.Serial, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        var window = FindWindow(_processId);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException($"电脑版进程 {_processId} 没有可用窗口，可能已经退出。");
        }

        var title = GetWindowTitle(window);
        IReadOnlyList<AdbDevice> devices =
        [
            new AdbDevice(
                Serial,
                AdbDeviceState.Device,
                string.IsNullOrWhiteSpace(title) ? "电脑版窗口" : title,
                "win32",
                $"窗口 0x{window.ToInt64():X}")
        ];
        return Task.FromResult(devices);
    }

    public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        if (TryParseProcessId(endpoint, out var processId) && processId != _processId)
        {
            throw new InvalidOperationException($"当前客户端绑定 PID {_processId}，不能连接到 PID {processId}。");
        }

        return Task.CompletedTask;
    }

    public Task<byte[]> CaptureScreenshotAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            lock (_sync)
            {
                EnsureNotDisposed();
                var id = MaaControllerPostScreencap(_controller);
                Wait(id, "获取电脑版截图");
                var buffer = MaaImageBufferCreate();
                if (buffer == IntPtr.Zero)
                {
                    throw new InvalidOperationException("MaaFramework 无法创建截图缓冲区。");
                }

                try
                {
                    if (MaaControllerCachedImage(_controller, buffer) == 0)
                    {
                        throw new InvalidOperationException("MaaFramework 未返回电脑版截图。");
                    }

                    var size = MaaImageBufferGetEncodedSize(buffer);
                    var data = MaaImageBufferGetEncoded(buffer);
                    if (data == IntPtr.Zero || size == 0 || size > int.MaxValue)
                    {
                        throw new InvalidOperationException("MaaFramework 返回了空的电脑版截图。");
                    }

                    var bytes = new byte[(int)size];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    return bytes;
                }
                finally
                {
                    MaaImageBufferDestroy(buffer);
                }
            }
        }, cancellationToken);
    }

    public Task<(int Width, int Height)> GetScreenSizeAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotDisposed();
        lock (_sync)
        {
            var width = 0;
            var height = 0;
            if (MaaControllerGetResolution(_controller, ref width, ref height) == 0)
            {
                throw new InvalidOperationException("MaaFramework 无法读取电脑版窗口分辨率。");
            }

            return Task.FromResult((width, height));
        }
    }

    public Task TapAsync(string serial, PixelPoint point, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidatePoint(point);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            lock (_sync)
            {
                Execute(() => MaaControllerPostClick(_controller, point.X, point.Y), "点击电脑版窗口");
            }
        }, cancellationToken);
    }

    public async Task TapBatchAsync(
        string serial,
        IReadOnlyList<PixelPoint> points,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TapAsync(serial, point, cancellationToken);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public Task SwipeAsync(
        string serial,
        PixelPoint from,
        PixelPoint to,
        int durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidatePoint(from);
        ValidatePoint(to);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            lock (_sync)
            {
                Execute(
                    () => MaaControllerPostSwipe(
                        _controller,
                        from.X,
                        from.Y,
                        to.X,
                        to.Y,
                        Math.Max(1, durationMilliseconds)),
                    "拖动电脑版窗口");
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_controller != IntPtr.Zero)
            {
                MaaControllerDestroy(_controller);
                _controller = IntPtr.Zero;
            }
        }

        GC.SuppressFinalize(this);
    }

    ~Win32DesktopClient() => Dispose();

    public static bool TryParseProcessId(string? value, out int processId)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }
        else if (text.StartsWith("win32:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..];
        }

        return int.TryParse(text, out processId) && processId > 0;
    }

    private void SetScreenshotSizeOption()
    {
        var value = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(value, ScreenshotTargetShortSide);
            if (MaaControllerSetOption(
                    _controller,
                    ControllerOptionScreenshotTargetShortSide,
                    value,
                    sizeof(int)) == 0)
            {
                throw new InvalidOperationException("MaaFramework 无法设置截图基准分辨率。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(value);
        }
    }

    private void Execute(Func<long> action, string operation)
    {
        var id = action();
        if (id == InvalidControlId)
        {
            throw new InvalidOperationException($"MaaFramework 未接受{operation}请求。");
        }

        Wait(id, operation);
    }

    private void Wait(long id, string operation)
    {
        var status = MaaControllerWait(_controller, id);
        if (status != StatusSucceeded)
        {
            var hint = operation.Contains("点击", StringComparison.Ordinal) || operation.Contains("拖动", StringComparison.Ordinal)
                ? " 若目标游戏以管理员权限运行，请也以管理员身份启动本程序。"
                : string.Empty;
            throw new InvalidOperationException($"{operation}失败，MaaFramework 状态码 {status}。{hint}");
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed || _controller == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(Win32DesktopClient));
        }
    }

    private void ValidateSerial(string serial)
    {
        if (!string.Equals(serial, Serial, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"设备序列号不属于 PID {_processId}。", nameof(serial));
        }
    }

    private static void ValidatePoint(PixelPoint point)
    {
        if (point.X < 0 || point.Y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(point), "窗口坐标不能为负数。");
        }
    }

    private static IntPtr FindWindow(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            return IntPtr.Zero;
        }

        try
        {
            var process = Process.GetProcessById(processId);
            if (process.MainWindowHandle != IntPtr.Zero && IsWindowVisible(process.MainWindowHandle))
            {
                return process.MainWindowHandle;
            }
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero;
        }

        var result = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner == processId && IsWindowVisible(handle) && GetWindowTextLength(handle) > 0)
            {
                result = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        return GetWindowText(window, buffer, buffer.Length) > 0
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MaaWin32ControllerCreate(IntPtr hwnd, ulong screencapMethod, ulong mouseMethod, ulong keyboardMethod);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MaaControllerDestroy(IntPtr controller);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte MaaControllerSetOption(IntPtr controller, int key, IntPtr value, ulong valueSize);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long MaaControllerPostConnection(IntPtr controller);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long MaaControllerPostClick(IntPtr controller, int x, int y);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long MaaControllerPostSwipe(IntPtr controller, int x1, int y1, int x2, int y2, int duration);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long MaaControllerPostScreencap(IntPtr controller);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MaaControllerWait(IntPtr controller, long id);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte MaaControllerCachedImage(IntPtr controller, IntPtr buffer);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte MaaControllerGetResolution(IntPtr controller, ref int width, ref int height);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MaaImageBufferCreate();

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MaaImageBufferDestroy(IntPtr buffer);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MaaImageBufferGetEncoded(IntPtr buffer);

    [DllImport("MaaFramework.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong MaaImageBufferGetEncodedSize(IntPtr buffer);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, char[] text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class MaaNativeLoader
    {
        private static int _loaded;

        public static void EnsureLoaded()
        {
            if (Interlocked.Exchange(ref _loaded, 1) != 0)
            {
                return;
            }

            var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Maa");
            string[] requiredFiles =
            [
                "MaaFramework.dll",
                "MaaWin32ControlUnit.dll",
                "MaaUtils.dll",
                "opencv_world4_maa.dll",
                "fastdeploy_ppocr_maa.dll",
                "onnxruntime_maa.dll"
            ];
            var missing = requiredFiles
                .Where(file => !File.Exists(Path.Combine(directory, file)))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new FileNotFoundException(
                    $"MaaFramework Win32 运行库不完整，缺少：{string.Join("、", missing)}。请确认发布目录包含完整的 Assets\\Maa。",
                    Path.Combine(directory, missing[0]));
            }

            SetDllDirectory(directory);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string path);
    }
}
