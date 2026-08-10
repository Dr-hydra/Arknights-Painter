using ArknightsPainter.App.Dialogs;
using ArknightsPainter.App.Services;
using ArknightsPainter.App.ViewModels;
using ArknightsPainter.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace ArknightsPainter.App;

public sealed partial class MainWindow : Window
{
    private bool _loaded;
    private CancellationTokenSource? _adjustmentDebounceCts;
    private AppWindow _appWindow;
    private string? _lastTempImage;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        RootGrid.DataContext = ViewModel;
        ViewModel.PreviewChanged += ViewModel_PreviewChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1320, 860));
    }

    public MainViewModel ViewModel { get; }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (ViewModel.IsDesktopMode && await RequestDesktopElevationAsync())
        {
            return;
        }

        await ViewModel.InitializeAsync();
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(async () =>
        {
            var path = NativeFilePicker.PickImagePath(WindowNative.GetWindowHandle(this));
            if (path is not null)
            {
                await ViewModel.LoadImageAsync(path);
            }
        });

    private async void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var launched = await Launcher.LaunchUriAsync(
                new Uri("https://github.com/Dr-hydra/Arknights-Painter"));
            if (!launched)
            {
                throw new InvalidOperationException("系统未能打开默认浏览器。");
            }
        });
    }

    private async void OpenBilibili_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var launched = await Launcher.LaunchUriAsync(
                new Uri("https://space.bilibili.com/441133155"));
            if (!launched)
            {
                throw new InvalidOperationException("系统未能打开默认浏览器。");
            }
        });
    }

    private async void CropImage_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var path = ViewModel.CurrentImagePath
                ?? throw new InvalidOperationException("请先导入图片。");
            var dialog = new CropDialog(path)
            {
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var png = dialog.GetCroppedPng();
                await ViewModel.LoadImageAsync(SaveTempImage(png, "crop"));
            }
        });
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            _appWindow.Hide();
            try
            {
                await Task.Delay(250);
                var capture = ScreenCapture.CaptureVirtualScreen();
                try
                {
                    var overlay = new CaptureOverlayWindow(capture.Bitmap);
                    try
                    {
                        var overlayWindow = overlay.AppWindow;
                        overlayWindow.MoveAndResize(
                            new RectInt32(capture.Left, capture.Top, capture.Width, capture.Height));
                        if (overlayWindow.Presenter is OverlappedPresenter presenter)
                        {
                            presenter.IsAlwaysOnTop = true;
                            presenter.SetBorderAndTitleBar(false, false);
                            presenter.IsMaximizable = false;
                            presenter.IsMinimizable = false;
                            presenter.IsResizable = false;
                        }

                        overlay.Activate();
                        var selection = await overlay.WaitForResultAsync();
                        if (selection is { } rect)
                        {
                            var png = ScreenCapture.CropToPng(capture.Bitmap, rect);
                            await ViewModel.LoadImageAsync(SaveTempImage(png, "screen"));
                        }
                    }
                    finally
                    {
                        overlay.Close();
                    }
                }
                finally
                {
                    capture.Bitmap.Dispose();
                }
            }
            finally
            {
                _appWindow.Show();
            }
        });
    }

    private void ImportArea_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "导入图片";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void ImportArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.FirstOrDefault() is StorageFile file)
        {
            await RunUiActionAsync(() => ViewModel.LoadImageAsync(file.Path));
        }
    }

    private async void ConversionOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            await RunUiActionAsync(ViewModel.ReconvertAsync);
        }
    }

    private async void ImageAdjustment_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _adjustmentDebounceCts?.Cancel();
        _adjustmentDebounceCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _adjustmentDebounceCts = cancellation;
        try
        {
            await Task.Delay(160, cancellation.Token);
            await RunUiActionAsync(ViewModel.ReconvertAsync);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_adjustmentDebounceCts, cancellation))
            {
                _adjustmentDebounceCts = null;
            }

            cancellation.Dispose();
        }
    }

    private async void ResetImageAdjustments_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetImageAdjustments();
        _adjustmentDebounceCts?.Cancel();
        await RunUiActionAsync(ViewModel.ReconvertAsync);
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(ViewModel.RefreshDevicesAsync);

    private async void Connect_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(ViewModel.ConnectAsync);

    private async void AdbMode_Click(object sender, RoutedEventArgs e)
    {
        ((ToggleButton)sender).IsChecked = true;
        ViewModel.SelectConnectionMode("adb");
        if (_loaded)
        {
            await RunUiActionAsync(ViewModel.RefreshDevicesAsync);
        }
    }

    private async void DesktopMode_Click(object sender, RoutedEventArgs e)
    {
        ((ToggleButton)sender).IsChecked = true;
        ViewModel.SelectConnectionMode("win32");
        if (await RequestDesktopElevationAsync())
        {
            return;
        }

        if (_loaded)
        {
            await RunUiActionAsync(ViewModel.RefreshDevicesAsync);
        }
    }

    private async void AutoCalibrate_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var capture = await ViewModel.CaptureCalibrationAsync();
            if (capture.Result.Success && capture.Result.Profile is not null)
            {
                ViewModel.SaveCalibration(capture.Result.Profile);
                return;
            }

            await ShowManualCalibrationAsync(capture);
        });
    }

    private async void ManualCalibrate_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var capture = await ViewModel.CaptureCalibrationAsync();
            await ShowManualCalibrationAsync(capture);
        });
    }

    private async Task ShowManualCalibrationAsync(CalibrationCapture capture)
    {
        using var bitmap = SKBitmap.Decode(capture.Screenshot)
            ?? throw new InvalidDataException("无法解码设备截图。");
        var serial = ViewModel.SelectedDevice?.Device.Serial
            ?? throw new InvalidOperationException("未选择设备。");
        var initial = ViewModel.CurrentCalibration ?? capture.Result.Profile ?? new CalibrationProfile(
            serial,
            bitmap.Width,
            bitmap.Height,
            ScaleReference(443, 180, 844, 842, bitmap.Width, bitmap.Height),
            ScaleReference(1433, 377, 420, 650, bitmap.Width, bitmap.Height),
            0,
            DateTimeOffset.UtcNow);
        var dialog = new CalibrationDialog(capture.Screenshot, serial, initial)
        {
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SaveCalibration(dialog.CreateProfile());
        }
    }

    private async void StartDrawing_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(ViewModel.StartDrawingAsync, showCancellation: false);

    private void PauseDrawing_Click(object sender, RoutedEventArgs e) => ViewModel.TogglePause();

    private void PauseDrawingAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.TogglePause();
        args.Handled = true;
    }

    private void CancelDrawing_Click(object sender, RoutedEventArgs e) => ViewModel.CancelDrawing();

    private void ViewModel_PreviewChanged(object? sender, byte[] png)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var source = new BitmapImage();
            await source.SetSourceAsync(stream);
            ArtworkPreview.Source = source;
        });
    }

    private async Task RunUiActionAsync(Func<Task> action, bool showCancellation = true)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (!showCancellation)
        {
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task<bool> RequestDesktopElevationAsync()
    {
        if (ElevationService.IsAdministrator)
        {
            return false;
        }

        try
        {
            if (ElevationService.TryRestartAsAdministrator())
            {
                Close();
                return true;
            }
        }
        catch (Exception ex)
        {
            ViewModel.SelectConnectionMode("adb");
            await ShowErrorAsync(ex.Message);
            return false;
        }

        ViewModel.SelectConnectionMode("adb");
        await ShowErrorAsync("电脑版控制需要管理员权限。权限申请已取消，已切回模拟器模式。");
        return false;
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "操作失败",
            Content = message,
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
    }

    private static PixelRect ScaleReference(
        int x,
        int y,
        int width,
        int height,
        int screenWidth,
        int screenHeight) => new(
            (int)Math.Round(x * screenWidth / 1920.0),
            (int)Math.Round(y * screenHeight / 1080.0),
            (int)Math.Round(width * screenWidth / 1920.0),
            (int)Math.Round(height * screenHeight / 1080.0));

    private string SaveTempImage(byte[] png, string prefix)
    {
        if (_lastTempImage is not null)
        {
            try
            {
                File.Delete(_lastTempImage);
            }
            catch
            {
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"ArknightsPainter-{prefix}-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        _lastTempImage = path;
        return path;
    }
}
