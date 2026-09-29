using VCCad.Core.Color;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Colour-space conversions and the hex field: the panel shows HSL and hex, and
/// both must round trip tightly enough that reading a value and typing it back
/// leaves the colour alone.
/// </summary>
public class ColorMathTests
{
    private static void AssertColor(ColorRgb expected, ColorRgb actual, int precision = 9)
    {
        Assert.Equal(expected.R, actual.R, precision);
        Assert.Equal(expected.G, actual.G, precision);
        Assert.Equal(expected.B, actual.B, precision);
        Assert.Equal(expected.A, actual.A, precision);
    }

    // ---- HSL --------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 1.0, 0.5)]
    [InlineData(60.0, 1.0, 0.5)]
    [InlineData(120.0, 1.0, 0.5)]
    [InlineData(180.0, 1.0, 0.5)]
    [InlineData(240.0, 1.0, 0.5)]
    [InlineData(300.0, 1.0, 0.5)]
    [InlineData(30.0, 0.42, 0.37)]
    [InlineData(210.0, 0.61, 0.28)]
    [InlineData(359.9, 0.83, 0.72)]
    [InlineData(90.0, 0.15, 0.5)]
    [InlineData(270.0, 1.0, 0.25)]
    [InlineData(45.0, 0.9, 0.1)]
    [InlineData(200.0, 0.5, 0.8)]
    public void HslRoundTrip_IsExactForChromaticSamples(double h, double s, double l)
    {
        var hsl = new HslColor(h, s, l);
        ColorRgb rgb = hsl.ToRgb();
        HslColor back = HslColor.FromRgb(rgb);

        Assert.Equal(h, back.H, 9);
        Assert.Equal(s, back.S, 9);
        Assert.Equal(l, back.L, 9);
        AssertColor(rgb, back.ToRgb());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void HslFromRgb_GreysHaveNoSaturationAndRoundTrip(double level)
    {
        ColorRgb grey = new(level, level, level);
        HslColor hsl = HslColor.FromRgb(grey);

        Assert.Equal(0.0, hsl.H);
        Assert.Equal(0.0, hsl.S);
        Assert.Equal(level, hsl.L, 9);
        AssertColor(grey, hsl.ToRgb());
    }

    [Fact]
    public void HslToRgb_BlackAndWhiteAreExactRegardlessOfHue()
    {
        AssertColor(ColorRgb.Black, new HslColor(123.0, 1.0, 0.0).ToRgb());
        AssertColor(ColorRgb.White, new HslColor(123.0, 1.0, 1.0).ToRgb());
        AssertColor(new ColorRgb(0.5, 0.5, 0.5), new HslColor(0.0, 0.0, 0.5).ToRgb());
    }

    [Theory]
    [InlineData(390.0, 30.0)]
    [InlineData(-30.0, 330.0)]
    [InlineData(360.0, 0.0)]
    [InlineData(720.0, 0.0)]
    public void HslHueOutsideZeroTo360_WrapsToTheSameColour(double wrapped, double canonical)
    {
        AssertColor(
            new HslColor(canonical, 0.8, 0.4).ToRgb(),
            new HslColor(wrapped, 0.8, 0.4).ToRgb());
    }

    [Fact]
    public void HslToRgb_CarriesAlphaThrough()
    {
        ColorRgb color = new HslColor(200.0, 0.6, 0.5).ToRgb(0.25);
        Assert.Equal(0.25, color.A, 9);
        Assert.Equal(200.0, HslColor.FromRgb(color).H, 7);
    }

    // ---- HSV --------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(60.0, 1.0, 1.0)]
    [InlineData(137.0, 0.42, 0.83)]
    [InlineData(200.0, 0.5, 0.5)]
    [InlineData(300.0, 0.9, 0.2)]
    [InlineData(359.5, 0.77, 0.61)]
    public void HsvRoundTrip_IsExact(double h, double s, double v)
    {
        ColorRgb rgb = new HsvColor(h, s, v).ToRgb();
        HsvColor back = HsvColor.FromRgb(rgb);

        Assert.Equal(h, back.H, 9);
        Assert.Equal(s, back.S, 9);
        Assert.Equal(v, back.V, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(137.0)]
    [InlineData(180.0)]
    [InlineData(240.0)]
    [InlineData(300.0)]
    [InlineData(359.0)]
    public void HsvHueColor_IsFullySaturatedAndKeepsItsHue(double hue)
    {
        ColorRgb color = HsvColor.HueColor(hue);
        double max = Math.Max(color.R, Math.Max(color.G, color.B));
        double min = Math.Min(color.R, Math.Min(color.G, color.B));

        Assert.Equal(1.0, max, 9);
        Assert.Equal(0.0, min, 9);
        Assert.Equal(hue, HsvColor.FromRgb(color).H, 7);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void HsvFromRgb_GreysHaveNoHueOrSaturationAndRoundTrip(double level)
    {
        ColorRgb grey = new(level, level, level);
        HsvColor hsv = HsvColor.FromRgb(grey);

        Assert.Equal(0.0, hsv.H);
        Assert.Equal(0.0, hsv.S);
        Assert.Equal(level, hsv.V, 9);
        AssertColor(grey, hsv.ToRgb());
    }

    // ---- Hex --------------------------------------------------------------

    [Fact]
    public void HexFormat_IsUppercaseWithLeadingHash()
    {
        Assert.Equal("#FF8000", HexColor.Format(ColorRgb.FromBytes(255, 128, 0)));
        Assert.Equal("#000000", HexColor.Format(ColorRgb.Black));
        Assert.Equal("#FFFFFF", HexColor.Format(ColorRgb.White));
        Assert.Equal("#00000080", HexColor.FormatWithAlpha(ColorRgb.FromBytes(0, 0, 0, 128)));
        Assert.Equal("#FF8000", HexColor.Format(ColorRgb.FromBytes(255, 128, 0), includeAlpha: false));
    }

    [Theory]
    [InlineData("#ff8000", 255, 128, 0, 255)]
    [InlineData("FF8000", 255, 128, 0, 255)]
    [InlineData("  #Ff8000  ", 255, 128, 0, 255)]
    [InlineData("#f80", 255, 136, 0, 255)]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#ABC", 170, 187, 204, 255)]
    [InlineData("#11223344", 17, 34, 51, 68)]
    [InlineData("#1234", 17, 34, 51, 68)]
    public void HexParse_AcceptsEverySpelling(string text, int r, int g, int b, int a)
    {
        Assert.True(HexColor.TryParse(text, out ColorRgb color));
        AssertColor(ColorRgb.FromBytes((byte)r, (byte)g, (byte)b, (byte)a), color, 12);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("1234567")]
    [InlineData("#123456789")]
    [InlineData("#GG0000")]
    [InlineData("#FF80-0")]
    public void HexParse_RejectsMalformedInput(string? text)
    {
        Assert.False(HexColor.TryParse(text, out _));
        Assert.Throws<FormatException>(() => HexColor.Parse(text!));
    }

    [Fact]
    public void HexRoundTrip_IsExactForEveryByteChannel()
    {
        // Step through the cube; a hex field that loses precision on any channel
        // would fail on the first non-multiple-of-17 value.
        for (int r = 0; r < 256; r += 17)
        {
            for (int g = 0; g < 256; g += 17)
            {
                for (int b = 0; b < 256; b += 17)
                {
                    ColorRgb original = ColorRgb.FromBytes((byte)r, (byte)g, (byte)b);
                    string hex = HexColor.Format(original);
                    Assert.True(HexColor.TryParse(hex, out ColorRgb parsed), hex);
                    AssertColor(original, parsed, 12);
                }
            }
        }
    }

    [Fact]
    public void HexRoundTrip_PreservesEveryAlphaLevel()
    {
        for (int a = 0; a < 256; a++)
        {
            ColorRgb original = ColorRgb.FromBytes(10, 20, 30, (byte)a);
            string hex = HexColor.FormatWithAlpha(original);
            Assert.True(HexColor.TryParse(hex, out ColorRgb parsed), hex);
            Assert.Equal(10.0 / 255.0, parsed.R, 12);
            Assert.Equal(a / 255.0, parsed.A, 12);
        }
    }
}
