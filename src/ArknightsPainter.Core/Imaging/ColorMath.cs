using ArknightsPainter.Core.Models;

namespace ArknightsPainter.Core.Imaging;

public static class ColorMath
{
    public readonly record struct Lab(double L, double A, double B);

    public static Lab ToLab(RgbColor color)
    {
        static double Linearize(double channel)
        {
            channel /= 255.0;
            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        var r = Linearize(color.R);
        var g = Linearize(color.G);
        var b = Linearize(color.B);

        var x = ((r * 0.4124564) + (g * 0.3575761) + (b * 0.1804375)) / 0.95047;
        var y = ((r * 0.2126729) + (g * 0.7151522) + (b * 0.0721750)) / 1.00000;
        var z = ((r * 0.0193339) + (g * 0.1191920) + (b * 0.9503041)) / 1.08883;

        static double Pivot(double value) => value > 0.008856
            ? Math.Cbrt(value)
            : (7.787 * value) + (16.0 / 116.0);

        var fx = Pivot(x);
        var fy = Pivot(y);
        var fz = Pivot(z);
        return new Lab((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public static double DeltaE2000(RgbColor left, RgbColor right) =>
        DeltaE2000(ToLab(left), ToLab(right));

    public static double DeltaE2000(Lab left, Lab right)
    {
        const double degrees = 180.0 / Math.PI;
        const double radians = Math.PI / 180.0;

        var c1 = Math.Sqrt((left.A * left.A) + (left.B * left.B));
        var c2 = Math.Sqrt((right.A * right.A) + (right.B * right.B));
        var cBar = (c1 + c2) / 2;
        var cBar7 = Math.Pow(cBar, 7);
        var g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25, 7))));
        var a1Prime = (1 + g) * left.A;
        var a2Prime = (1 + g) * right.A;
        var c1Prime = Math.Sqrt((a1Prime * a1Prime) + (left.B * left.B));
        var c2Prime = Math.Sqrt((a2Prime * a2Prime) + (right.B * right.B));

        static double Hue(double a, double b)
        {
            var value = Math.Atan2(b, a) * degrees;
            return value < 0 ? value + 360 : value;
        }

        var h1Prime = Hue(a1Prime, left.B);
        var h2Prime = Hue(a2Prime, right.B);
        var deltaLPrime = right.L - left.L;
        var deltaCPrime = c2Prime - c1Prime;
        var deltaHue = h2Prime - h1Prime;
        if (c1Prime * c2Prime == 0)
        {
            deltaHue = 0;
        }
        else if (deltaHue > 180)
        {
            deltaHue -= 360;
        }
        else if (deltaHue < -180)
        {
            deltaHue += 360;
        }

        var deltaHPrime = 2 * Math.Sqrt(c1Prime * c2Prime) * Math.Sin(deltaHue * radians / 2);
        var lBarPrime = (left.L + right.L) / 2;
        var cBarPrime = (c1Prime + c2Prime) / 2;
        double hBarPrime;
        if (c1Prime * c2Prime == 0)
        {
            hBarPrime = h1Prime + h2Prime;
        }
        else if (Math.Abs(h1Prime - h2Prime) <= 180)
        {
            hBarPrime = (h1Prime + h2Prime) / 2;
        }
        else if (h1Prime + h2Prime < 360)
        {
            hBarPrime = (h1Prime + h2Prime + 360) / 2;
        }
        else
        {
            hBarPrime = (h1Prime + h2Prime - 360) / 2;
        }

        var t = 1
            - (0.17 * Math.Cos((hBarPrime - 30) * radians))
            + (0.24 * Math.Cos(2 * hBarPrime * radians))
            + (0.32 * Math.Cos((3 * hBarPrime + 6) * radians))
            - (0.20 * Math.Cos((4 * hBarPrime - 63) * radians));
        var deltaTheta = 30 * Math.Exp(-Math.Pow((hBarPrime - 275) / 25, 2));
        var cPrime7 = Math.Pow(cBarPrime, 7);
        var rc = 2 * Math.Sqrt(cPrime7 / (cPrime7 + Math.Pow(25, 7)));
        var sl = 1 + ((0.015 * Math.Pow(lBarPrime - 50, 2)) /
            Math.Sqrt(20 + Math.Pow(lBarPrime - 50, 2)));
        var sc = 1 + (0.045 * cBarPrime);
        var sh = 1 + (0.015 * cBarPrime * t);
        var rt = -Math.Sin(2 * deltaTheta * radians) * rc;

        var lTerm = deltaLPrime / sl;
        var cTerm = deltaCPrime / sc;
        var hTerm = deltaHPrime / sh;
        return Math.Sqrt((lTerm * lTerm) + (cTerm * cTerm) + (hTerm * hTerm) + (rt * cTerm * hTerm));
    }

    public static PaletteColor FindNearest(RgbColor color, IReadOnlyList<PaletteColor> palette)
    {
        var lab = ToLab(color);
        PaletteColor? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in palette)
        {
            var distance = DeltaE2000(lab, ToLab(candidate.Color));
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best ?? throw new InvalidOperationException("Palette is empty.");
    }
}
