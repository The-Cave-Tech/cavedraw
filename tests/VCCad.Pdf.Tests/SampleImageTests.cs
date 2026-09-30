using VCCad.Core.Model;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The LILLIE pattern embeds a 274x484 CMYK image with a DeviceGray soft mask. Image
/// XObjects used to be dropped entirely — the importer's XObject handler returned early
/// for anything that was not a Form — so the page lost its tint.
///
/// The image is a pale magenta (samples begin 07 04 04 00), which is worth pinning:
/// reading CMYK samples as RGB turns that tint black, and black-on-a-pattern is a
/// difference you cannot miss but a test can.
/// </summary>
public class SampleImageTests
{
    private const string Lillie = "3464_LILLIE_View_A_Sides_color.pdf";

    private static string? SamplePath(string fileName)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static CadDocument? Import()
    {
        string? path = SamplePath(Lillie);
        return path is null ? null : PdfImporter.Import(File.ReadAllBytes(path));
    }

    private static List<ImageItem> Images(CadDocument document)
        => document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<ImageItem>()
            .ToList();

    [Fact]
    public void TheEmbeddedRasterImageIsImported()
    {
        CadDocument? document = Import();
        if (document is null)
        {
            return; // sample not present in this checkout: skip cleanly
        }

        List<ImageItem> images = Images(document);

        Assert.True(images.Count >= 1, "the embedded image was dropped");
    }

    [Fact]
    public void TheImageKeepsItsPixelDimensionsAndColourSpace()
    {
        CadDocument? document = Import();
        if (document is null)
        {
            return;
        }

        ImageItem image = Images(document)[0];

        Assert.Equal(274, image.PixelWidth);
        Assert.Equal(484, image.PixelHeight);
        Assert.Equal(8, image.BitsPerComponent);

        // DeviceCMYK in the file, and 274 * 484 * 4 samples once decoded.
        Assert.Equal(ImageColorSpace.Cmyk, image.ColorSpace);
        Assert.Equal(274 * 484 * 4, image.Samples.Length);
    }

    [Fact]
    public void TheImagesTintIsPaleRatherThanBlack()
    {
        CadDocument? document = Import();
        if (document is null)
        {
            return;
        }

        ImageItem image = Images(document)[0];
        ColorRgb colour = image.PixelAt(0, 0);

        Assert.True(colour.R > 0.9, $"red was {colour.R}; CMYK read as RGB renders black");
        Assert.True(colour.G > 0.9, $"green was {colour.G}");
        Assert.True(colour.B > 0.9, $"blue was {colour.B}");
    }

    [Fact]
    public void TheSoftMaskComesThrough()
    {
        CadDocument? document = Import();
        if (document is null)
        {
            return;
        }

        ImageItem image = Images(document)[0];

        Assert.True(image.HasMask, "the soft mask was dropped");
        Assert.Equal(274 * 484, image.Mask.Length);

        // The mask begins fully opaque.
        Assert.Equal(1.0, image.CoverageAt(0, 0), 3);
    }

    [Fact]
    public void TheImageIsPlacedOnThePageItIsDrawnOn()
    {
        CadDocument? document = Import();
        if (document is null)
        {
            return;
        }

        ImageItem image = Images(document)[0];

        Assert.False(image.Placement.IsEmpty, "the image has no placement");
        Assert.True(image.Placement.Width > 1, $"placement width was {image.Placement.Width}");
        Assert.True(image.Placement.Height > 1, $"placement height was {image.Placement.Height}");

        // Inside the first A4 page box (595 x 842 points at the origin).
        Assert.InRange(image.Placement.X, -1, 596);
        Assert.InRange(image.Placement.Y, -1, 843);
    }

    [Fact]
    public void AnImageSurvivesSaveAndReload()
    {
        CadDocument? document = Import();
        if (document is null)
        {
            return;
        }

        ImageItem before = Images(document)[0];

        byte[] saved = VCCad.Core.Serialization.VccadDocumentSerializer.SerializeToBytes(document);
        CadDocument reloaded = VCCad.Core.Serialization.VccadDocumentSerializer.Deserialize(saved);
        ImageItem after = Images(reloaded)[0];

        Assert.Equal(before.PixelWidth, after.PixelWidth);
        Assert.Equal(before.PixelHeight, after.PixelHeight);
        Assert.Equal(before.BitsPerComponent, after.BitsPerComponent);
        Assert.Equal(before.ColorSpace, after.ColorSpace);
        Assert.Equal(before.Samples, after.Samples);
        Assert.Equal(before.Mask, after.Mask);
        Assert.Equal(before.Placement, after.Placement);
    }
    /// <summary>
    /// Every item under these, groups included.
    ///
    /// The imported tree is nested - a page's content is a group from the file's own form XObjects, with
    /// optional-content groups inside it - so a walk that looks only at a layer's direct children no longer
    /// finds the artwork. These tests are about fonts and images, not structure, so they walk.
    /// </summary>
    private static IEnumerable<LayerItem> Walk(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;
            if (item is ArtGroup group)
            {
                foreach (LayerItem nested in Walk(group.Children))
                {
                    yield return nested;
                }
            }
        }
    }
}