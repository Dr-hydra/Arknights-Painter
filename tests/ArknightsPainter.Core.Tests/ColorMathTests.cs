using ArknightsPainter.Core.Imaging;
using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Tests;

public sealed class ColorMathTests
{
    [Fact]
    public void DeltaE2000_MatchesPublishedReferencePair()
    {
        var left = new ColorMath.Lab(50, 2.6772, -79.7751);
        var right = new ColorMath.Lab(50, 0, -82.7485);

        var result = ColorMath.DeltaE2000(left, right);

        Assert.Equal(2.0425, result, 4);
    }

    [Fact]
    public void FindNearest_UsesPerceptualDistance()
    {
        var palette = TestPalette.Create(
            new RgbColor(0, 0, 0),
            new RgbColor(255, 255, 255),
            new RgbColor(210, 40, 50));

        var result = ColorMath.FindNearest(new RgbColor(205, 45, 55), palette.Colors);

        Assert.Equal(2, result.Index);
    }

    [Fact]
    public void PaletteSignature_IsStableAndDetectsChanges()
    {
        var palette = TestPalette.Create(new RgbColor(1, 2, 3), new RgbColor(4, 5, 6));
        var first = palette.ComputeSignature();

        var changed = TestPalette.Create(new RgbColor(1, 2, 3), new RgbColor(4, 5, 7));

        Assert.Equal(first, palette.ComputeSignature());
        Assert.NotEqual(first, changed.ComputeSignature());
    }
}
