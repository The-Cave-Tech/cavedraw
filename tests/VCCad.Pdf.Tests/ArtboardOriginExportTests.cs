using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Core.Selection;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A rotated group on an artboard that is **not** at the document origin, exported and read back: the canvas's
/// bounds, the hit-test result and the re-imported page geometry have to be the same picture.
///
/// This is the export half of #168. `PdfDocumentExporter.WorldTransform` composed the artboard origin **inside**
/// the item's frame (<c>toDoc.Compose(T(origin))</c>) while the canvas and `SelectionEngine.ToWorld` compose it
/// **outside** (<c>T(origin).Compose(toArtboard)</c>). A translation commutes and hid it; a rotation does not, and
/// then the filter region, the pixels drawn into it and the rectangle the picture is placed at are all computed in
/// a frame the drawing does not use.
///
/// The page is a second witness for the same rule, independently of the canvas: the exporter writes **one PDF page
/// per artboard whose MediaBox is that artboard's own rectangle**, so the content stream never carries the grid
/// position. An item's world frame therefore differs from the page's frame by exactly the artboard origin and by
/// nothing else, which is what these tests subtract - and they read the page through
/// <see cref="PdfImporter.TryImportVector"/> rather than <c>Import</c>, because the latter prefers our own sidecar
/// and would hand back the model that was exported rather than the page that was written (#170).
/// </summary>
public class ArtboardOriginExportTests
{
    private const double Ax = 300;
    private const double Ay = 200;

    /// <summary>
    /// A 400x400 artboard at (300,200) - the second page of a grid - one group turned a quarter turn about the
    /// artboard's own centre, and one 40x40 filled square at artboard-local (150,180). The square reaches
    /// (480,350)..(520,390) in world coordinates and (180,150)..(220,190) on the page.
    /// </summary>
    private static (CadDocument Document, PathItem Path, Artboard Board) Fixture(bool filtered)
    {
        var document = new CadDocument();
        var board = new Artboard(new Size2D(400, 400), new Point2D(Ax, Ay)) { Name = "Page 2" };
        Layer layer = board.AddLayer("Artwork");
        document.AddArtboard(board);

        var group = new ArtGroup
        {
            Name = "turned",
            Transform = AffineTransform.CreateRotationAround(new Point2D(200, 200), Math.PI / 2),
        };

        layer.AddItem(group);

        var path = new PathItem { Name = "square", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(150, 180)));
        sub.Nodes.Add(new PathNode(new Point2D(190, 180)));
        sub.Nodes.Add(new PathNode(new Point2D(190, 220)));
        sub.Nodes.Add(new PathNode(new Point2D(150, 220)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);
        group.AddItem(path);

        if (filtered)
        {
            // A pass-through graph: the shape reaches the page through the raster route with nothing added to it,
            // so the ink on the page is the square itself and can be compared to the canvas's box directly.
            document.AddFilter(new FilterSpec("pass", new[]
            {
                FilterPrimitive.OffsetBy(0, 0, input: "SourceGraphic"),
            }));
            path.FilterId = "pass";
        }

        return (document, path, board);
    }

    /// <summary>Everything under an artboard's layers, groups walked into.</summary>
    private static IEnumerable<LayerItem> Everything(CadDocument document)
    {
        foreach (Artboard board in document.Artboards)
        {
            foreach (Layer layer in board.Layers)
            {
                foreach (LayerItem item in Walk(layer.Children))
                {
                    yield return item;
                }
            }
        }
    }

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

    /// <summary>
    /// The box of the ink an image actually carries, in the page's own model coordinates.
    ///
    /// Read from the coverage rather than from the placement, because "the picture is in the right rectangle" and
    /// "the shape was drawn in the frame that rectangle is in" are different claims: the first is true of a blank
    /// image, and a blank image is exactly what the inside composition produced.
    /// </summary>
    private static Rect2D InkBox(ImageItem image)
    {
        ImageItem picture = image;
        double left = double.MaxValue, top = double.MaxValue;
        double right = double.MinValue, bottom = double.MinValue;
        bool any = false;

        for (int y = 0; y < picture.PixelHeight; y++)
        {
            for (int x = 0; x < picture.PixelWidth; x++)
            {
                if (picture.CoverageAt(x, y) <= 0.001)
                {
                    continue;
                }

                double modelX = picture.Placement.X + ((x + 0.5) * picture.Placement.Width / picture.PixelWidth);
                double modelY = picture.Placement.Y + ((y + 0.5) * picture.Placement.Height / picture.PixelHeight);
                left = Math.Min(left, modelX);
                top = Math.Min(top, modelY);
                right = Math.Max(right, modelX);
                bottom = Math.Max(bottom, modelY);
                any = true;
            }
        }

        Assert.True(any, $"the picture at {picture.Placement} carried no ink at all");
        return new Rect2D(left, top, right - left, bottom - top);
    }

    private static void AssertClose(double expected, double actual, double tolerance, string what)
        => Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            $"{what}: expected {expected} +/- {tolerance}, got {actual}");

    /// <summary>
    /// The unfiltered case, where the geometry itself has to survive: the canvas's world box, the hit test and the
    /// re-imported path are the same square, with the page's own frame - the artboard origin taken off - in
    /// between.
    /// </summary>
    [Fact]
    public void ARotatedGroupOnAnOffOriginArtboardReimportsWhereTheCanvasDrawsIt()
    {
        (CadDocument document, PathItem path, Artboard board) = Fixture(filtered: false);

        Rect2D canvas = SelectionEngine.WorldBounds(new[] { path });
        Assert.Equal(480, canvas.Left, 6);
        Assert.Equal(350, canvas.Top, 6);
        Assert.Equal(40, canvas.Width, 6);
        Assert.Equal(40, canvas.Height, 6);

        Point2D centre = new(canvas.Left + (canvas.Width / 2), canvas.Top + (canvas.Height / 2));
        Assert.Same(path, SelectionEngine.Within(board, centre));

        byte[] pdf = PdfDocumentExporter.Export(document);

        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? page));

        // The file carries no grid position: the page is the artboard's own rectangle. That is why the world frame
        // and the page's frame differ by the origin and by nothing else.
        Artboard imported = Assert.Single(page!.Artboards);
        Assert.Equal(400, imported.Width, 6);
        Assert.Equal(400, imported.Height, 6);
        Assert.Equal(0, imported.X, 6);
        Assert.Equal(0, imported.Y, 6);

        PathItem round = Assert.Single(Everything(page).OfType<PathItem>());
        Rect2D onPage = SelectionEngine.WorldBounds(new[] { (LayerItem)round });

        AssertClose(canvas.Left - Ax, onPage.Left, 1e-6, "the re-imported square's left edge");
        AssertClose(canvas.Top - Ay, onPage.Top, 1e-6, "the re-imported square's top edge");
        AssertClose(canvas.Width, onPage.Width, 1e-6, "the re-imported square's width");
        AssertClose(canvas.Height, onPage.Height, 1e-6, "the re-imported square's height");
    }

    /// <summary>
    /// The filtered case, which is where the two compositions were actually visible: a filtered object reaches the
    /// page as a picture, so the frame it was rasterised in is readable from the page as well as the rectangle it
    /// was placed in.
    ///
    /// Before the fix the picture was placed at a fraction of the region's own origin, the shape was drawn a whole
    /// region away from it, and the page carried a **blank** image - the assertion below failed on "no ink at
    /// all" rather than on a coordinate.
    /// </summary>
    [Fact]
    public void AFilteredShapeInARotatedGroupOnAnOffOriginArtboardIsPaintedWhereTheCanvasDrawsIt()
    {
        (CadDocument document, PathItem path, Artboard board) = Fixture(filtered: true);

        Rect2D canvas = SelectionEngine.WorldBounds(new[] { path });
        Point2D centre = new(canvas.Left + (canvas.Width / 2), canvas.Top + (canvas.Height / 2));
        Assert.Same(path, SelectionEngine.Within(board, centre));

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);

        // It went through the raster route at all, rather than being reported and drawn unfiltered.
        Assert.Contains(notes, note => note.Contains("image", StringComparison.OrdinalIgnoreCase));

        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? page));
        ImageItem picture = Assert.Single(Everything(page!).OfType<ImageItem>());

        // The picture covers the filter's region, which is a fraction of the shape's **world** box - the same box
        // the canvas measures the selection with - carried onto the page by taking the artboard origin off.
        Rect2D region = FilterRasteriser.RegionOf(document.FindFilter("pass")!, canvas);
        AssertClose(region.Left - Ax, picture.Placement.Left, 1e-6, "the picture's left edge on the page");
        AssertClose(region.Top - Ay, picture.Placement.Top, 1e-6, "the picture's top edge on the page");
        AssertClose(region.Width, picture.Placement.Width, 1e-6, "the picture's width");
        AssertClose(region.Height, picture.Placement.Height, 1e-6, "the picture's height");

        // And the shape is actually in it, where the world frame says - within half a pixel of the raster's own
        // grid, which is the finest the answer can be stated at.
        Rect2D ink = InkBox(picture);
        AssertClose(canvas.Left - Ax, ink.Left, 0.6, "the drawn square's left edge on the page");
        AssertClose(canvas.Top - Ay, ink.Top, 0.6, "the drawn square's top edge on the page");
        AssertClose(canvas.Width, ink.Width, 0.6, "the drawn square's width");
        AssertClose(canvas.Height, ink.Height, 0.6, "the drawn square's height");
    }
}
