using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Type 0 (sampled) functions.
///
/// A sampled function is a table, and the table is the whole function. The importer used to
/// read the first sample and scale the output range — which is not a rough version of a
/// sampled function, it ignores every sample but one, so a tint transform built from a
/// table came out as a straight ramp from nothing. Type 0 appears in about one corpus file
/// in eight, and is what shadings and spot-colour tints are usually built from.
/// </summary>
public class SampledFunctionTests
{
    /// <summary>Packs samples the way PDF does: most significant bit first.</summary>
    private static byte[] Pack(int[] values, int bits)
    {
        var bytes = new byte[(values.Length * bits + 7) / 8];
        for (int i = 0; i < values.Length; i++)
        {
            for (int b = 0; b < bits; b++)
            {
                int value = (values[i] >> (bits - 1 - b)) & 1;
                if (value == 0)
                {
                    continue;
                }

                int at = (i * bits) + b;
                bytes[at >> 3] |= (byte)(1 << (7 - (at & 7)));
            }
        }

        return bytes;
    }

    [Fact]
    public void ASingleOutputTableIsInterpolated()
    {
        // Three samples: 0, 128, 255. Halfway between the first two must be about the
        // midpoint, not the first sample scaled.
        byte[] table = Pack(new[] { 0, 128, 255 }, 8);

        double[]? low = PdfFunctions.Sampled(table, new[] { 3.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 2.0 }, new[] { 0.0, 1.0 }, 8, new[] { 0.0 });
        // Three samples encode onto indices 0, 1 and 2 across the input domain, so a
        // quarter of the way in is halfway between the 0 and the 128 sample.
        double[]? mid = PdfFunctions.Sampled(table, new[] { 3.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 2.0 }, new[] { 0.0, 1.0 }, 8, new[] { 0.25 });
        double[]? high = PdfFunctions.Sampled(table, new[] { 3.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 2.0 }, new[] { 0.0, 1.0 }, 8, new[] { 1.0 });

        Assert.Equal(0.0, low![0], 3);
        Assert.Equal(64.0 / 255.0, mid![0], 3);
        Assert.Equal(1.0, high![0], 3);
    }

    [Fact]
    public void TheCurveIsFollowedRatherThanRamped()
    {
        // A table that dips in the middle. A straight ramp from the first sample would
        // rise monotonically and never dip, which is the bug this replaced.
        byte[] table = Pack(new[] { 255, 0, 255 }, 8);
        double[]? middle = PdfFunctions.Sampled(table, new[] { 3.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 2.0 }, new[] { 0.0, 1.0 }, 8, new[] { 0.5 });

        Assert.Equal(0.0, middle![0], 3);
    }

    [Fact]
    public void SeveralOutputsAreInterpolatedTogether()
    {
        // Two samples, two outputs each: (0, 255) then (255, 0).
        byte[] table = Pack(new[] { 0, 255, 255, 0 }, 8);

        double[]? half = PdfFunctions.Sampled(table, new[] { 2.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 1.0 }, new[] { 0.0, 1.0, 0.0, 1.0 }, 8, new[] { 0.5 });

        // Halfway between the two samples on both outputs.
        Assert.Equal(0.5, half![0], 3);
        Assert.Equal(0.5, half[1], 3);
    }

    [Fact]
    public void TheOutputRangeIsApplied()
    {
        // The same full-scale sample, decoded into 0..4 instead of 0..1.
        byte[] table = Pack(new[] { 255 }, 8);

        double[]? scaled = PdfFunctions.Sampled(table, new[] { 1.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 0.0 }, new[] { 0.0, 4.0 }, 8, new[] { 0.5 });

        Assert.Equal(4.0, scaled![0], 3);
    }

    [Fact]
    public void OneBitSamplesAreRead()
    {
        // Four one-bit samples: 1, 0, 1, 0. At one bit the table packs four to a byte.
        byte[] table = Pack(new[] { 1, 0, 1, 0 }, 1);

        double[]? first = PdfFunctions.Sampled(table, new[] { 4.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 3.0 }, new[] { 0.0, 1.0 }, 1, new[] { 0.0 });
        double[]? second = PdfFunctions.Sampled(table, new[] { 4.0 }, new[] { 0.0, 1.0 },
            new[] { 0.0, 3.0 }, new[] { 0.0, 1.0 }, 1, new[] { 1.0 / 3.0 });

        Assert.Equal(1.0, first![0], 3);
        Assert.Equal(0.0, second![0], 3);
    }

    [Fact]
    public void ATruncatedTableIsRefusedRatherThanRead()
    {
        // The table must hold every sample the size promises. Reading past the end would
        // produce a number rather than an error, and that number would look like a colour.
        Assert.Null(PdfFunctions.Sampled(new byte[] { 1, 2 }, new[] { 100.0 },
            new[] { 0.0, 1.0 }, new[] { 0.0, 99.0 }, new[] { 0.0, 1.0 }, 8, new[] { 0.5 }));
    }

    [Fact]
    public void ASampledTintTransformReachesTheSwatch()
    {
        // End to end: a Separation whose tint transform is a sampled table, read through
        // the importer rather than called directly. The objects are numbered so the page
        // can name the function: 4 is the content, 5 the function, 6 the colour space.
        byte[] table = Pack(new[] { 0, 128, 255 }, 8);

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /ColorSpace << /CS1 6 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 44 >>\nstream\nq /CS1 cs 1.0 scn 60 600 120 120 re f Q\nendstream",
            "<< /FunctionType 0 /Domain [0 1] /Size [3] /BitsPerSample 8 /Range [0 1] "
                + $"/Encode [0 2] /Length {table.Length} >>\n"
                + $"stream\n{System.Text.Encoding.Latin1.GetString(table)}\nendstream",
            "[/Separation /Spot /DeviceGray 5 0 R]",
        };

        CadDocument document = PdfImporter.Import(
            System.Text.Encoding.Latin1.GetBytes(Build(bodies)));
        PathItem path = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        // Full tint reads the last sample, which is white.
        Assert.Equal(1.0, path.Fill.Color.R, 2);
    }

    /// <summary>Assembles numbered objects into a PDF with a correct cross-reference.</summary>
    private static string Build(List<string> bodies)
    {
        var builder = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return builder.ToString();
    }
}
