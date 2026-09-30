using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A CMYK-indexed palette through the real writer and the real reader.
///
/// The Core tests pin the arithmetic; this pins that the *base space* survives the trip, which is
/// where the bug lived: `[/Indexed /DeviceCMYK 251 ...]` has four bytes per entry, and a file that
/// re-emits it as `[/Indexed /DeviceRGB ...]` changes the picture's colours on every save.
/// </summary>
public class IndexedPaletteExportTests
{
    private static ImageItem CmykIndexed()
    {
        var image = new ImageItem
        {
            Name = "tint",
            PixelWidth = 2,
            PixelHeight = 1,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Indexed,
            PaletteBase = ImageColorSpace.Cmyk,
            Palette = new byte[]
            {
                0, 0, 0, 128, // entry 0: 50% black
                255, 0, 0, 0, // entry 1: cyan
            },
            Samples = new byte[] { 0, 1 },
            Placement = new Geometry.Rect2D(10, 10, 2, 1),
        };

        return image;
    }

    [Fact]
    public void ThePaletteBaseReachesTheExportedColourSpace()
    {
        CadDocument document = CadDocument.CreateDefault("tint");
        document.Artboards[0].Layers[0].AddItem(CmykIndexed());

        string pdf = System.Text.Encoding.Latin1.GetString(PdfDocumentExporter.Export(document));

        // The base is the thing that decides the entry size, so it has to be written out as itself.
        Assert.Contains("/Indexed /DeviceCMYK", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/Indexed /DeviceRGB", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePaletteAndItsBaseSurviveTheRoundTrip()
    {
        CadDocument document = CadDocument.CreateDefault("tint");
        document.Artboards[0].Layers[0].AddItem(CmykIndexed());

        CadDocument again = PdfImporter.Import(PdfDocumentExporter.Export(document));

        ImageItem reloaded = again.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<ImageItem>()
            .Single();

        Assert.Equal(ImageColorSpace.Indexed, reloaded.ColorSpace);
        Assert.Equal(ImageColorSpace.Cmyk, reloaded.PaletteBase);
        Assert.Equal(4, reloaded.PaletteEntryBytes);

        ColorRgb half = reloaded.PixelAt(0, 0);
        ColorRgb cyan = reloaded.PixelAt(1, 0);

        Assert.Equal(1.0 - (128.0 / 255.0), half.R, 2);
        Assert.Equal(half.R, half.B, 2);
        Assert.Equal(1.0, cyan.G, 2);
        Assert.Equal(0.0, cyan.R, 2);
    }
}
