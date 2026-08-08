using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Storage.Streams;

namespace ArknightsPainter.App.Dialogs;

public sealed partial class CropDialog : ContentDialog
{
    private const double MinimumCropSize = 36;
    private readonly byte[] _preview;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private FrameworkElement? _captureElement;
    private ResizeEdges _resizeEdges;
    private Windows.Foundation.Point _startPointer;
    private double _startLeft;
    private double _startTop;
    private double _startWidth;
    private double _startHeight;

    public CropDialog(string imagePath, ImageCropRect? initialCrop)
    {
        InitializeComponent();
        using var source = SkiaImageLoader.LoadOriented(imagePath);
        _sourceWidth = source.Width;
        _sourceHeight = source.Height;

        var scale = Math.Min(900.0 / source.Width, 560.0 / source.Height);
        var displayWidth = Math.Max(120, (int)Math.Round(source.Width * scale));
        var displayHeight = Math.Max(120, (int)Math.Round(source.Height * scale));
        CropSurface.Width = displayWidth;
        CropSurface.Height = displayHeight;
        CropOverlay.Width = displayWidth;
        CropOverlay.Height = displayHeight;
        _preview = RenderPreview(source, displayWidth, displayHeight);

        var crop = initialCrop is { IsValid: true } value ? value : ImageCropRect.Full;
        SetCrop(crop);
        Loaded += CropDialog_Loaded;
    }

    public ImageCropRect CreateCropRect() => new(
        Canvas.GetLeft(CropRegion) / CropOverlay.Width,
        Canvas.GetTop(CropRegion) / CropOverlay.Height,
        CropRegion.Width / CropOverlay.Width,
        CropRegion.Height / CropOverlay.Height);

    private async void CropDialog_Loaded(object sender, RoutedEventArgs e)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(_preview);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        SourceImage.Source = source;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => SetCrop(ImageCropRect.Full);

    private void CenterSquare_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceWidth >= _sourceHeight)
        {
            var width = _sourceHeight / (double)_sourceWidth;
            SetCrop(new ImageCropRect((1 - width) / 2, 0, width, 1));
        }
        else
        {
            var height = _sourceWidth / (double)_sourceHeight;
            SetCrop(new ImageCropRect(0, (1 - height) / 2, 1, height));
        }
    }

    private void CropRegion_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        BeginInteraction((FrameworkElement)sender, ResizeEdges.None, e);

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (FrameworkElement)sender;
        var edges = ParseEdges(handle.Tag as string);
        BeginInteraction(handle, edges, e);
        e.Handled = true;
    }

    private void BeginInteraction(FrameworkElement captureElement, ResizeEdges edges, PointerRoutedEventArgs e)
    {
        _captureElement = captureElement;
        _resizeEdges = edges;
        _startPointer = e.GetCurrentPoint(CropOverlay).Position;
        _startLeft = Canvas.GetLeft(CropRegion);
        _startTop = Canvas.GetTop(CropRegion);
        _startWidth = CropRegion.Width;
        _startHeight = CropRegion.Height;
        captureElement.CapturePointer(e.Pointer);
    }

    private void CropRegion_PointerMoved(object sender, PointerRoutedEventArgs e) => UpdateInteraction(e);

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        UpdateInteraction(e);
        e.Handled = true;
    }

    private void UpdateInteraction(PointerRoutedEventArgs e)
    {
        if (_captureElement is null || !e.GetCurrentPoint(CropOverlay).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetCurrentPoint(CropOverlay).Position;
        var deltaX = current.X - _startPointer.X;
        var deltaY = current.Y - _startPointer.Y;
        if (_resizeEdges == ResizeEdges.None)
        {
            SetCropBounds(
                Math.Clamp(_startLeft + deltaX, 0, CropOverlay.Width - _startWidth),
                Math.Clamp(_startTop + deltaY, 0, CropOverlay.Height - _startHeight),
                _startWidth,
                _startHeight);
            return;
        }

        var left = _startLeft;
        var top = _startTop;
        var right = _startLeft + _startWidth;
        var bottom = _startTop + _startHeight;
        if (_resizeEdges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Clamp(_startLeft + deltaX, 0, right - MinimumCropSize);
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Right))
        {
            right = Math.Clamp(right + deltaX, left + MinimumCropSize, CropOverlay.Width);
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Clamp(_startTop + deltaY, 0, bottom - MinimumCropSize);
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            bottom = Math.Clamp(bottom + deltaY, top + MinimumCropSize, CropOverlay.Height);
        }

        SetCropBounds(left, top, right - left, bottom - top);
    }

    private void Interaction_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var capture = _captureElement;
        _captureElement = null;
        capture?.ReleasePointerCapture(e.Pointer);
    }

    private void SetCrop(ImageCropRect crop) => SetCropBounds(
        crop.X * CropOverlay.Width,
        crop.Y * CropOverlay.Height,
        crop.Width * CropOverlay.Width,
        crop.Height * CropOverlay.Height);

    private void SetCropBounds(double left, double top, double width, double height)
    {
        Canvas.SetLeft(CropRegion, left);
        Canvas.SetTop(CropRegion, top);
        CropRegion.Width = width;
        CropRegion.Height = height;
        UpdateShades(left, top, width, height);
        CropSummary.Text = $"{Math.Max(1, (int)Math.Round(width / CropOverlay.Width * _sourceWidth))} × " +
                           $"{Math.Max(1, (int)Math.Round(height / CropOverlay.Height * _sourceHeight))} px";
    }

    private void UpdateShades(double left, double top, double width, double height)
    {
        SetCanvasRect(ShadeLeft, 0, 0, left, CropOverlay.Height);
        SetCanvasRect(ShadeRight, left + width, 0, CropOverlay.Width - left - width, CropOverlay.Height);
        SetCanvasRect(ShadeTop, left, 0, width, top);
        SetCanvasRect(ShadeBottom, left, top + height, width, CropOverlay.Height - top - height);
    }

    private static void SetCanvasRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static ResizeEdges ParseEdges(string? value)
    {
        var result = ResizeEdges.None;
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            result |= Enum.Parse<ResizeEdges>(part, ignoreCase: true);
        }
        return result;
    }

    private static byte[] RenderPreview(SKBitmap source, int width, int height)
    {
        using var preview = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(preview);
        using var checker = new SKPaint { IsAntialias = false };
        const int tile = 12;
        for (var y = 0; y < height; y += tile)
        {
            for (var x = 0; x < width; x += tile)
            {
                checker.Color = ((x / tile) + (y / tile)) % 2 == 0
                    ? new SKColor(225, 225, 225)
                    : new SKColor(190, 190, 190);
                canvas.DrawRect(x, y, tile, tile, checker);
            }
        }

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(
            source,
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKCubicResampler.Mitchell),
            paint);
        using var image = SKImage.FromBitmap(preview);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8
    }
}
