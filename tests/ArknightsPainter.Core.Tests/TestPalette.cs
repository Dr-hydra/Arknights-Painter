using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Tests;

internal static class TestPalette
{
    public static PaletteDefinition Create(params RgbColor[] colors)
    {
        var entries = colors.Select((color, index) => new PaletteColor(index, $"Color {index}", color)).ToList();
        return new PaletteDefinition
        {
            Version = "test",
            Columns = Math.Min(4, colors.Length),
            Complete = true,
            Colors = entries
        };
    }
}
