using ArknightsPainter.Core.Models;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;
using Windows.System;
using Windows.Foundation;

namespace ArknightsPainter.App;

public sealed partial class CaptureOverlayWindow : Window
{
    private readonly SKBitmap _screen;
    private readonly TaskCompletionSource<PixelRect?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _selecting;
    private bool _finished;
    private double _scale = 1;
    private Windows.Foundation.Point _start;
    private Rect _selection;

    public CaptureOverlayWindow(SKBitmap screen)
    {
        InitializeComponent();
        _screen = screen;
        OverlayRoot.Loaded += OverlayRoot_Loaded;
        Closed += (_, _) => Cancel();
    }

    public Task<PixelRect?> WaitForResultAsync() => _completion.Task;

    private async void OverlayRoot_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _scale = OverlayRoot.XamlRoot.RasterizationScale;
            ScreenImage.Source = await ToBitmapImageAsync(_screen);
            OverlayRoot.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            // 加载失败也必须结束任务，否则主窗口会一直隐藏。
            Debug.WriteLine($"截图预览加载失败: {ex.Message}");
            Cancel();
        }
    }

    private void OverlayRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_finished)
        {
            return;
        }

        _selecting = true;
        _start = e.GetCurrentPoint(OverlayRoot).Position;
        OverlayRoot.CapturePointer(e.Pointer);
        Toolbar.Visibility = Visibility.Collapsed;
        UpdateSelection(_start, _start);
        e.Handled = true;
    }

    private void OverlayRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        UpdateSelection(_start, e.GetCurrentPoint(OverlayRoot).Position);
        e.Handled = true;
    }

    private void OverlayRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        OverlayRoot.ReleasePointerCapture(e.Pointer);
        if (_selection.Width >= 8 && _selection.Height >= 8)
        {
            ShowToolbar();
        }
        else
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            HideShades();
        }

        e.Handled = true;
    }

    private void OverlayRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && _selection.Width >= 8 && _selection.Height >= 8)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Confirm()
    {
        if (_finished || _selection.Width < 8 || _selection.Height < 8)
        {
            return;
        }

        _finished = true;
        var x = Math.Clamp((int)Math.Round(_selection.X * _scale), 0, _screen.Width);
        var y = Math.Clamp((int)Math.Round(_selection.Y * _scale), 0, _screen.Height);
        var right = Math.Clamp((int)Math.Round((_selection.X + _selection.Width) * _scale), 0, _screen.Width);
        var bottom = Math.Clamp((int)Math.Round((_selection.Y + _selection.Height) * _scale), 0, _screen.Height);
        _completion.TrySetResult(new PixelRect(x, y, right - x, bottom - y));
    }

    private void Cancel()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _completion.TrySetResult(null);
    }

    private void UpdateSelection(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        _selection = new Rect(x, y, width, height);
        SelectionRect.Visibility = Visibility.Visible;
        SetRect(SelectionRect, x, y, width, height);
        SetCanvasRect(ShadeLeft, 0, 0, x, OverlayRoot.ActualHeight);
        SetCanvasRect(ShadeTop, x, 0, width, y);
        SetCanvasRect(ShadeRight, x + width, 0, OverlayRoot.ActualWidth - x - width, OverlayRoot.ActualHeight);
        SetCanvasRect(ShadeBottom, x, y + height, width, OverlayRoot.ActualHeight - y - height);
    }

    private void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = Toolbar.DesiredSize.Width;
        var toolbarHeight = Toolbar.DesiredSize.Height;
        var left = Math.Clamp(
            _selection.X + (_selection.Width - toolbarWidth) / 2,
            0,
            Math.Max(0, OverlayRoot.ActualWidth - toolbarWidth));
        var top = Math.Clamp(
            _selection.Y + _selection.Height + 8,
            0,
            Math.Max(0, OverlayRoot.ActualHeight - toolbarHeight));
        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
        SizeText.Text = $"{Math.Max(1, (int)Math.Round(_selection.Width * _scale))} × " +
                       $"{Math.Max(1, (int)Math.Round(_selection.Height * _scale))} px";
    }

    private void HideShades()
    {
        SetCanvasRect(ShadeLeft, 0, 0, 0, 0);
        SetCanvasRect(ShadeTop, 0, 0, 0, 0);
        SetCanvasRect(ShadeRight, 0, 0, 0, 0);
        SetCanvasRect(ShadeBottom, 0, 0, 0, 0);
    }

    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static void SetCanvasRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static async Task<BitmapImage> ToBitmapImageAsync(SKBitmap bitmap)
    {
        // PNG 编码是纯 CPU 计算，放到后台线程避免阻塞 UI。
        var pngData = await Task.Run(() =>
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        });

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(pngData);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        return source;
    }
}
