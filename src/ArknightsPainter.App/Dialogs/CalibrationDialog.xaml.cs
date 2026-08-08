using ArknightsPainter.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;

namespace ArknightsPainter.App.Dialogs;

public sealed partial class CalibrationDialog : ContentDialog
{
    private readonly byte[] _screenshot;
    private readonly string _serial;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private FrameworkElement? _activeRegion;
    private bool _resizing;
    private Windows.Foundation.Point _startPointer;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;

    public CalibrationDialog(byte[] screenshot, string serial, CalibrationProfile initial)
    {
        InitializeComponent();
        _screenshot = screenshot;
        _serial = serial;
        using var bitmap = SKBitmap.Decode(screenshot) ?? throw new InvalidDataException("无法解码校准截图。");
        _sourceWidth = bitmap.Width;
        _sourceHeight = bitmap.Height;

        var scale = Math.Min(940.0 / _sourceWidth, 550.0 / _sourceHeight);
        CalibrationSurface.Width = Math.Round(_sourceWidth * scale);
        CalibrationSurface.Height = Math.Round(_sourceHeight * scale);
        OverlayCanvas.Width = CalibrationSurface.Width;
        OverlayCanvas.Height = CalibrationSurface.Height;
        SetRegion(CanvasRegion, initial.CanvasBounds, scale);
        SetRegion(PaletteRegion, initial.PaletteViewport, scale);
        Loaded += CalibrationDialog_Loaded;
    }

    public CalibrationProfile CreateProfile()
    {
        var scaleX = _sourceWidth / OverlayCanvas.Width;
        var scaleY = _sourceHeight / OverlayCanvas.Height;
        return new CalibrationProfile(
            _serial,
            _sourceWidth,
            _sourceHeight,
            ReadRegion(CanvasRegion, scaleX, scaleY),
            ReadRegion(PaletteRegion, scaleX, scaleY),
            1.0,
            DateTimeOffset.UtcNow);
    }

    private async void CalibrationDialog_Loaded(object sender, RoutedEventArgs e)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(_screenshot);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        ScreenshotImage.Source = source;
    }

    private void Region_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginInteraction((FrameworkElement)sender, e, resizing: false);
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (FrameworkElement)sender;
        BeginInteraction(handle == CanvasResizeHandle ? CanvasRegion : PaletteRegion, e, resizing: true);
        e.Handled = true;
    }

    private void BeginInteraction(FrameworkElement region, PointerRoutedEventArgs e, bool resizing)
    {
        _activeRegion = region;
        _resizing = resizing;
        _startPointer = e.GetCurrentPoint(OverlayCanvas).Position;
        _startLeft = Canvas.GetLeft(region);
        _startTop = Canvas.GetTop(region);
        _startWidth = region.Width;
        _startHeight = region.Height;
        region.CapturePointer(e.Pointer);
    }

    private void Region_PointerMoved(object sender, PointerRoutedEventArgs e) => UpdateInteraction(e);

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateInteraction(e);
        e.Handled = true;
    }

    private void UpdateInteraction(PointerRoutedEventArgs e)
    {
        if (_activeRegion is null || !e.GetCurrentPoint(OverlayCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetCurrentPoint(OverlayCanvas).Position;
        var deltaX = current.X - _startPointer.X;
        var deltaY = current.Y - _startPointer.Y;
        if (_resizing)
        {
            _activeRegion.Width = Math.Clamp(_startWidth + deltaX, 80, OverlayCanvas.Width - _startLeft);
            _activeRegion.Height = Math.Clamp(_startHeight + deltaY, 80, OverlayCanvas.Height - _startTop);
        }
        else
        {
            Canvas.SetLeft(_activeRegion, Math.Clamp(_startLeft + deltaX, 0, OverlayCanvas.Width - _activeRegion.Width));
            Canvas.SetTop(_activeRegion, Math.Clamp(_startTop + deltaY, 0, OverlayCanvas.Height - _activeRegion.Height));
        }
    }

    private void Region_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _activeRegion?.ReleasePointerCapture(e.Pointer);
        _activeRegion = null;
    }

    private static void SetRegion(FrameworkElement region, PixelRect bounds, double scale)
    {
        Canvas.SetLeft(region, bounds.X * scale);
        Canvas.SetTop(region, bounds.Y * scale);
        region.Width = Math.Max(80, bounds.Width * scale);
        region.Height = Math.Max(80, bounds.Height * scale);
    }

    private static PixelRect ReadRegion(FrameworkElement region, double scaleX, double scaleY) => new(
        (int)Math.Round(Canvas.GetLeft(region) * scaleX),
        (int)Math.Round(Canvas.GetTop(region) * scaleY),
        (int)Math.Round(region.Width * scaleX),
        (int)Math.Round(region.Height * scaleY));
}
