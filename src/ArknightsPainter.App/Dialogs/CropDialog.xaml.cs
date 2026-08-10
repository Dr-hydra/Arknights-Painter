using ArknightsPainter.Core.Imaging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using SkiaSharp;
using Windows.Storage.Streams;
using Windows.UI;

namespace ArknightsPainter.App.Dialogs;

public sealed partial class CropDialog : ContentDialog
{
    private const double SurfaceWidth = 620;
    private const double SurfaceHeight = 470;
    private const double CropRatio = 0.42;
    private const double GridDivisions = 24;
    private const int MaximumOutputSize = 2048;
    private readonly SKBitmap _source;
    private byte[]? _croppedPng;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private Windows.Foundation.Point _dragStart;
    private double _startOffsetX;
    private double _startOffsetY;

    private double CropSize => Math.Min(SurfaceWidth, SurfaceHeight) * CropRatio;

    public CropDialog(string imagePath)
    {
        InitializeComponent();
        _source = SkiaImageLoader.LoadOriented(imagePath);
        BuildGuide();
        ResetTransform();
        PrimaryButtonClick += (_, _) => _croppedPng = CreateCroppedPng();
        Loaded += CropDialog_Loaded;
        Closed += (_, _) => _source.Dispose();
    }

    public byte[] GetCroppedPng() => _croppedPng
        ?? throw new InvalidOperationException("未生成裁切结果。");

    private byte[] CreateCroppedPng()
    {
        var size = CropSize;
        var left = (SurfaceWidth - size) / 2;
        var top = (SurfaceHeight - size) / 2;
        var sourceLeft = (left - _offsetX) / _scale;
        var sourceTop = (top - _offsetY) / _scale;
        var sourceSpan = size / _scale;
        var outputSize = Math.Clamp((int)Math.Round(sourceSpan), 1, MaximumOutputSize);
        using var output = new SKBitmap(outputSize, outputSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Transparent);
        var visibleLeft = Math.Max(sourceLeft, 0);
        var visibleTop = Math.Max(sourceTop, 0);
        var visibleRight = Math.Min(sourceLeft + sourceSpan, _source.Width);
        var visibleBottom = Math.Min(sourceTop + sourceSpan, _source.Height);
        if (visibleRight > visibleLeft && visibleBottom > visibleTop)
        {
            var sourceRect = new SKRect(
                (float)visibleLeft,
                (float)visibleTop,
                (float)visibleRight,
                (float)visibleBottom);
            var targetRect = new SKRect(
                (float)((visibleLeft - sourceLeft) * outputSize / sourceSpan),
                (float)((visibleTop - sourceTop) * outputSize / sourceSpan),
                (float)((visibleRight - sourceLeft) * outputSize / sourceSpan),
                (float)((visibleBottom - sourceTop) * outputSize / sourceSpan));
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(
                _source,
                sourceRect,
                targetRect,
                new SKSamplingOptions(SKCubicResampler.Mitchell),
                paint);
        }

        canvas.Flush();
        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private async void CropDialog_Loaded(object sender, RoutedEventArgs e)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(RenderPreview());
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var source = new BitmapImage();
        await source.SetSourceAsync(stream);
        SourceImage.Source = source;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => ResetTransform();

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetCurrentPoint(CropSurface).Position;
        _startOffsetX = _offsetX;
        _startOffsetY = _offsetY;
        CropSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !e.GetCurrentPoint(CropSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetCurrentPoint(CropSurface).Position;
        _offsetX = _startOffsetX + current.X - _dragStart.X;
        _offsetY = _startOffsetY + current.Y - _dragStart.Y;
        ApplyTransform();
        e.Handled = true;
    }

    private void Interaction_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        CropSurface.ReleasePointerCapture(e.Pointer);
    }

    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CropSurface);
        var factor = Math.Pow(1.1, point.Properties.MouseWheelDelta / 120.0);
        var sourceX = (point.Position.X - _offsetX) / _scale;
        var sourceY = (point.Position.Y - _offsetY) / _scale;
        _scale = Math.Clamp(_scale * factor, 0.02, 64);
        _offsetX = point.Position.X - (sourceX * _scale);
        _offsetY = point.Position.Y - (sourceY * _scale);
        ApplyTransform();
        e.Handled = true;
    }

    private void BuildGuide()
    {
        var size = CropSize;
        var left = (SurfaceWidth - size) / 2;
        var top = (SurfaceHeight - size) / 2;
        SetCanvasRect(ShadeLeft, 0, 0, left, SurfaceHeight);
        SetCanvasRect(ShadeRight, left + size, 0, SurfaceWidth - left - size, SurfaceHeight);
        SetCanvasRect(ShadeTop, left, 0, size, top);
        SetCanvasRect(ShadeBottom, left, top + size, size, SurfaceHeight - top - size);
        SetCanvasRect(CropFrame, left, top, size, size);
        Canvas.SetLeft(GridCanvas, left);
        Canvas.SetTop(GridCanvas, top);
        GridCanvas.Width = size;
        GridCanvas.Height = size;
        var brush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        for (var i = 1; i < GridDivisions; i++)
        {
            var position = i * size / GridDivisions;
            GridCanvas.Children.Add(new Line
            {
                X1 = position,
                Y1 = 0,
                X2 = position,
                Y2 = size,
                Stroke = brush,
                StrokeThickness = 0.8
            });
            GridCanvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = position,
                X2 = size,
                Y2 = position,
                Stroke = brush,
                StrokeThickness = 0.8
            });
        }
    }

    private void ResetTransform()
    {
        _scale = Math.Max(SurfaceWidth / _source.Width, SurfaceHeight / _source.Height);
        _offsetX = (SurfaceWidth - (_source.Width * _scale)) / 2;
        _offsetY = (SurfaceHeight - (_source.Height * _scale)) / 2;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        SourceImage.Width = _source.Width * _scale;
        SourceImage.Height = _source.Height * _scale;
        Canvas.SetLeft(SourceImage, _offsetX);
        Canvas.SetTop(SourceImage, _offsetY);
        var outputPx = Math.Clamp((int)Math.Round(CropSize / _scale), 1, MaximumOutputSize);
        CropSummary.Text = $"输出 {outputPx}×{outputPx} px";
    }

    private byte[] RenderPreview()
    {
        const int maxLongEdge = 2048;
        var scale = Math.Min(1.0, maxLongEdge / (double)Math.Max(_source.Width, _source.Height));
        var width = Math.Max(1, (int)Math.Round(_source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(_source.Height * scale));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(
            _source,
            new SKRect(0, 0, width, height),
            new SKSamplingOptions(SKCubicResampler.Mitchell),
            paint);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void SetCanvasRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
