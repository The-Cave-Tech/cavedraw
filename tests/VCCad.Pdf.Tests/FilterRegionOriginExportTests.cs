using System.Globalization;
using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Issue #181: a filtered object on an artboard that is **not at the document origin** exported a blank picture,
/// placed half a region away from the shape it belongs to.
///
/// <para>
/// The region is a rectangle in model units, and the sampling density is how many pixels that rectangle is drawn
/// at. `Rasterise` asked for the region at **one pixel per unit** and then divided the resulting pixel origin by
/// the density, so the origin it drew the shape around was half the region's model origin - the shape landed
/// outside its own bitmap and the placement was built from the halved origin too. On an artboard at (0, 0) the
/// region origin is close enough to zero that the error is a pixel; anywhere else it is fatal, which is why
/// <see cref="Document"/> moves the artboard rather than relying on the default one.
/// </para>
///
/// <para>
/// Asserted on the **exported file read back**: the picture's colour samples and its soft mask are counted, not
/// merely checked to exist, and the `cm` that places the picture is compared with the region the filter names.
/// A test that only looked at the bytes would pass on the blank picture this pins, which is the failure mode the
/// issue names.
/// </para>
///
/// <para>
/// One coordinate space matters and is easy to get wrong: the region is measured in **world** units (a path's box
/// plus its artboard's origin), while the content stream draws in the **page's** own units (the path's anchors as
/// stored). The placement is compared with the region over the object's own box, which is the page-space box, so
/// the assertion is about where a reader will see the picture rather than about the rasteriser's internal frame.
/// </para>
/// </summary>
public class FilterRegionOriginExportTests
{
    /// <summary>Where the artboard sits, chosen so the region origin is nowhere near zero.</summary>
    private const double ArtboardX = 300;
    private const double ArtboardY = 200;

    private static CadDocument Document()
    {
        CadDocument document = CadDocument.CreateDefault("Filter region origin");

        // The artboard, not the object, is what moves: the path's own coordinates below stay small and readable,
        // so the region origin is the artboard origin and a fix that only worked at the origin would be invisible.
        document.Artboards[0].X = ArtboardX;
        document.Artboards[0].Y = ArtboardY;

        // **Red, not black.** A black shape paints a picture whose colour samples are all zero by construction -
        // the picture is black over transparent - so counting them would measure nothing and the blank export this
        // test pins would pass. The mask is zero either way; the colour is what makes the samples mean something.
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(new ColorRgb(1, 0, 0)) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(60, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 100)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        // A blur, because it is a raster operation: there is no operator in PDF for one, so the only way it can be
        // in the file is as the picture this test reads back.
        document.AddFilter(new FilterSpec("soft", new[]
        {
            FilterPrimitive.Blur(2, input: "SourceGraphic"),
        }));

        path.FilterId = "soft";
        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    [Fact]
    public void AFilteredObjectOnAMovedArtboardExportsItsPictureAndPlacesIt()
    {
        CadDocument document = Document();
        PathItem path = document.AllPaths().Single();
        FilterSpec filter = document.FindFilter("soft")!;

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);

        // The filter is written by this build (`PdfFilterExportTests` pins that declaration); if that ever flips,
        // the picture is absent for a reason this test does not own - and saying so beats a confusing count.
        Assert.True(PdfExportSupport.Find("filter")!.Written, "the exporter does not declare filters written");
        Assert.DoesNotContain(notes, note => note.Contains("is not written", StringComparison.Ordinal));

        List<PdfDrawing.ImageObject> images = PdfDrawing.Images(pdf);
        PdfDrawing.ImageObject picture = Assert.Single(images, image => image.Is("DeviceRGB"));
        PdfDrawing.ImageObject mask = Assert.Single(images, image => image.Is("DeviceGray"));

        // (a) The picture is a picture. Counted rather than assumed: the defect this pins produced a well-formed
        // image XObject of the right size whose every sample was zero, and `Assert.NotEmpty` would have passed it.
        int litSamples = picture.Samples.Count(value => value != 0);
        int litMask = mask.Samples.Count(value => value != 0);

        Assert.True(
            litSamples > 0,
            $"the exported picture is blank: 0 of {picture.Samples.Length} colour samples are non-zero " +
            $"(mask {litMask} of {mask.Samples.Length}, {PlacementText(pdf)})");
        Assert.True(
            litMask > 0,
            $"the exported mask is blank: 0 of {mask.Samples.Length} mask samples are non-zero " +
            $"({PlacementText(pdf)})");

        // (a cont.) And not merely non-zero once: the shape covers the region's middle, so a picture that drew a
        // single stray pixel would still be the blank export with noise. The mask has to be substantially covered.
        int opaque = mask.Samples.Count(value => value > 200);
        Assert.True(
            opaque > 200,
            $"the exported mask is not the shape: only {opaque} of {mask.Samples.Length} samples are opaque " +
            $"({PlacementText(pdf)})");

        // (b) The placement lands the picture where the object is. The `cm` is written in the **content stream's own
        // coordinates**, which is the page's: a path's anchors are stored relative to its artboard, so that is the
        // box the object is drawn at and the box the picture has to cover. The region is the filter's own rectangle
        // over that box, and its origin is what the `cm`'s translation must be - against the **density in use**,
        // which is read from the image rather than assumed, because the tolerance is one drawn pixel and a
        // restated density could be wrong about which one that is.
        Rect2D box = path.BoundingBox();
        Rect2D region = FilterRasteriser.RegionOf(filter, box);
        double density = picture.NumberValue("Width") / region.Width;

        (double a, double b, double c, double d, double e, double f) = PlacementOf(pdf);
        var placed = new Rect2D(Math.Min(e, e + a), Math.Min(f, f + d), Math.Abs(a), Math.Abs(d));

        double pixel = 1.0 / density;
        Assert.True(
            Math.Abs(e - region.X) <= pixel + 1e-6 && Math.Abs(f - region.Y) <= pixel + 1e-6,
            $"the picture is placed at ({Number(e)}, {Number(f)}) but its region starts at " +
            $"({Number(region.X)}, {Number(region.Y)}) on the page - the box the object is drawn in - and is " +
            $"drawn at {Number(density)} px per unit, so the origin may only be one pixel out: " +
            PlacementText(pdf));

        // And the stronger reading of the same claim: the picture is big enough and near enough to hold the shape
        // the filter was evaluated over. A placement at the halved origin fails this by the whole region origin.
        Assert.True(
            placed.Contains(box),
            $"the placed picture {placed} does not contain the object's own box {box}: " + PlacementText(pdf));

        Assert.Equal(0.0, b, 6);
        Assert.Equal(0.0, c, 6);
    }

    /// <summary>
    /// The `cm` immediately before the picture's `Do`, as (a, b, c, d, e, f) - the placement the page is drawn with.
    ///
    /// Taken from the content stream rather than from <see cref="FilterRasteriser.Placement"/> so the test measures
    /// the **file**; asking the function under test what it would write is how a placement bug survives a suite.
    /// </summary>
    private static (double A, double B, double C, double D, double E, double F) PlacementOf(byte[] pdf)
    {
        Match match = Regex.Match(
            PdfDrawing.Of(pdf),
            @"(?m)^(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm\s*\r?\n/\S+ Do$");

        Assert.True(match.Success, $"the page does not paint an image XObject:\n{PdfDrawing.Of(pdf)}");

        return (
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>The same placement, as a phrase for a failure message.</summary>
    private static string PlacementText(byte[] pdf)
    {
        (double a, double b, double c, double d, double e, double f) = PlacementOf(pdf);
        return $"placement {Number(a)} {Number(b)} {Number(c)} {Number(d)} {Number(e)} {Number(f)} cm";
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
