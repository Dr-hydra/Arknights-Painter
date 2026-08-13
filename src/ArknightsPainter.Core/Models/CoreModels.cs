using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace ArknightsPainter.Core.Models;

public enum ImageFitMode { Contain, Cover, Stretch }

public enum PixelArtAlgorithm
{
    Perceptual,
    BeadAverage,
    BeadDominant
}

public enum DitherMode
{
    None,
    FloydSteinberg,
    Atkinson,
    Bayer4x4
}

public enum DrawStage
{
    Idle,
    Validating,
    SelectingColor,
    Painting,
    Verifying,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum AdbDeviceState { Device, Offline, Unauthorized, Unknown }

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public int Packed => (R << 16) | (G << 8) | B;

    public static RgbColor FromHex(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var hex = value.TrimStart('#');
        if (hex.Length != 6)
        {
            throw new FormatException("Color values must use #RRGGBB format.");
        }

        return new RgbColor(
            byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

public sealed record PaletteColor(int Index, string Name, RgbColor Color);

public sealed class PaletteDefinition
{
    public string Version { get; init; } = "1.0";

    public int Columns { get; init; } = 4;

    public bool Complete { get; init; }

    public string Signature { get; init; } = string.Empty;

    public List<PaletteColor> Colors { get; init; } = [];

    public PaletteColor this[int index] => Colors.First(color => color.Index == index);

    public static PaletteDefinition Load(Stream stream)
    {
        var palette = JsonSerializer.Deserialize<PaletteDefinition>(stream, JsonOptions)
            ?? throw new InvalidDataException("Palette file is empty.");
        palette.Validate();
        return palette;
    }

    public static PaletteDefinition Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public string ComputeSignature()
    {
        var packed = string.Join('|', Colors.OrderBy(color => color.Index)
            .Select(color => $"{color.Index}:{color.Color.Hex}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(packed)))[..16];
    }

    public void Validate()
    {
        if (Columns <= 0 || Colors.Count == 0)
        {
            throw new InvalidDataException("Palette must contain at least one color and one column.");
        }

        if (Colors.Select(color => color.Index).Distinct().Count() != Colors.Count)
        {
            throw new InvalidDataException("Palette color indexes must be unique.");
        }

        if (!string.IsNullOrWhiteSpace(Signature) &&
            !string.Equals(Signature, ComputeSignature(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Palette signature does not match its colors.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public readonly record struct GridPoint(int Column, int Row)
{
    public int FlatIndex => (Row * Artwork24.Size) + Column;
}

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0;

    public PixelPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public PixelPoint GridCenter(GridPoint point, int columns = Artwork24.Size, int rows = Artwork24.Size) =>
        new(
            X + (int)Math.Round((point.Column + 0.5) * Width / columns),
            Y + (int)Math.Round((point.Row + 0.5) * Height / rows));
}

public readonly record struct ImageCropRect(double X, double Y, double Width, double Height)
{
    public static ImageCropRect Full { get; } = new(0, 0, 1, 1);

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
                           Right <= 1.000001 && Bottom <= 1.000001;
}

public sealed class Artwork24
{
    public const int Size = 24;
    public const int PixelCount = Size * Size;

    private readonly int[] _paletteIndexes;

    public Artwork24(IEnumerable<int> paletteIndexes)
    {
        _paletteIndexes = paletteIndexes.ToArray();
        if (_paletteIndexes.Length != PixelCount)
        {
            throw new ArgumentException($"Artwork must contain exactly {PixelCount} pixels.", nameof(paletteIndexes));
        }
    }

    public ReadOnlyMemory<int> PaletteIndexes => _paletteIndexes;

    public int this[int column, int row] => _paletteIndexes[(row * Size) + column];

    public int this[GridPoint point] => this[point.Column, point.Row];

    public IReadOnlyDictionary<int, int> ColorUsage => new ReadOnlyDictionary<int, int>(
        _paletteIndexes.GroupBy(index => index).ToDictionary(group => group.Key, group => group.Count()));
}

public sealed record ImageConversionOptions(
    ImageFitMode FitMode,
    RgbColor Background,
    PixelArtAlgorithm Algorithm,
    DitherMode Dither,
    ImageCropRect? Crop = null,
    double Brightness = 0,
    double Contrast = 0,
    double Saturation = 0)
{
    public static ImageConversionOptions Default { get; } =
        new(
            ImageFitMode.Contain,
            new RgbColor(255, 255, 255),
            PixelArtAlgorithm.BeadAverage,
            DitherMode.None);

    public ImageConversionOptions(
        ImageFitMode fitMode,
        RgbColor background,
        bool dither,
        ImageCropRect? crop = null)
        : this(
            fitMode,
            background,
            PixelArtAlgorithm.Perceptual,
            dither ? DitherMode.FloydSteinberg : DitherMode.None,
            crop)
    {
    }
}

public sealed record CalibrationProfile(
    string DeviceSerial,
    int ScreenWidth,
    int ScreenHeight,
    PixelRect CanvasBounds,
    PixelRect PaletteViewport,
    double Confidence,
    DateTimeOffset UpdatedAt)
{
    public bool Matches(string serial, int width, int height) =>
        string.Equals(DeviceSerial, serial, StringComparison.OrdinalIgnoreCase) &&
        ScreenWidth == width && ScreenHeight == height;
}

public sealed record AdbDevice(string Serial, AdbDeviceState State, string Model, string Product, string Description);

public sealed record VisibleSwatch(
    int Column,
    int VisibleRow,
    PixelPoint Center,
    RgbColor Color,
    bool HasSelectionGlow);

public sealed record ScreenLocationResult(
    bool Success,
    CalibrationProfile? Profile,
    double Confidence,
    string Message);

public sealed record DrawColorStep(PaletteColor Color, IReadOnlyList<GridPoint> Cells);

public sealed class DrawPlan
{
    public required Artwork24 Artwork { get; init; }

    public required IReadOnlyList<DrawColorStep> Steps { get; init; }

    public int TotalCells => Steps.Sum(step => step.Cells.Count);

    public static DrawPlan Create(Artwork24 artwork, PaletteDefinition palette)
    {
        var steps = Enumerable.Range(0, Artwork24.PixelCount)
            .Select(flat => new GridPoint(flat % Artwork24.Size, flat / Artwork24.Size))
            .GroupBy(point => artwork[point])
            .Select(group => new DrawColorStep(palette[group.Key], group.ToArray()))
            .OrderBy(step => step.Color.Index)
            .ToArray();

        return new DrawPlan { Artwork = artwork, Steps = steps };
    }
}

public sealed record DrawProgress(
    DrawStage Stage,
    int CompletedCells,
    int TotalCells,
    string Message,
    int? CurrentPaletteIndex = null)
{
    public double Fraction => TotalCells == 0 ? 1 : (double)CompletedCells / TotalCells;
}

public sealed record DrawExecutionOptions(
    int BatchSize = 20,
    TimeSpan? TapDelay = null,
    int VerificationRetries = 1,
    bool SkipVisualValidation = false,
    bool UseSwipeDrawing = false,
    int SwipeCellDurationMilliseconds = 50,
    bool UseCanvasValidation = false)
{
    public TimeSpan EffectiveTapDelay => TapDelay ?? TimeSpan.FromMilliseconds(50);
}
