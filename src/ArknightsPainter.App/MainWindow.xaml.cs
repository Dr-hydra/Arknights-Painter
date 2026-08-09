using ArknightsPainter.App.Dialogs;
using ArknightsPainter.App.ViewModels;
using ArknightsPainter.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace ArknightsPainter.App;

public sealed partial class MainWindow : Window
{
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        RootGrid.DataContext = ViewModel;
        ViewModel.PreviewChanged += ViewModel_PreviewChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1320, 860));
    }

    public MainViewModel ViewModel { get; }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await ViewModel.InitializeAsync();
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await RunUiActionAsync(() => ViewModel.LoadImageAsync(file.Path));
        }
    }

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

    private async void CropImage_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var path = ViewModel.CurrentImagePath
                ?? throw new InvalidOperationException("请先导入图片。");
            var dialog = new CropDialog(path, ViewModel.CurrentCrop)
            {
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ApplyCropAsync(dialog.CreateCropRect());
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

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(ViewModel.RefreshDevicesAsync);

    private async void Connect_Click(object sender, RoutedEventArgs e) =>
        await RunUiActionAsync(ViewModel.ConnectAsync);

    private async void ConnectionMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded)
        {
            await RunUiActionAsync(ViewModel.ConnectionModeChangedAsync);
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
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "操作失败",
                Content = ex.Message,
                CloseButtonText = "关闭"
            };
            await dialog.ShowAsync();
        }
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
}
