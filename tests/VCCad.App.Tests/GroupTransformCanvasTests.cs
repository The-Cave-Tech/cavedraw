using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Svg;
using VCCad.Geometry;
using VCCad.Pdf;
using VCCad.Pdf.Fonts;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A group's `transform` is drawn and picked, not ignored (issue #159).
///
/// The canvas built its world geometry from the artboard offset alone, so every `ArtGroup.Transform` above an
/// object was invisible to it - while `PdfDocumentExporter` composed exactly those transforms and put the
/// artwork where the file says. The two halves of the editor disagreed about where a transformed object *is*:
/// the export was right, the canvas was wrong, and hit-testing agreed with the wrong picture.
///
/// Every SVG import carries one of these transforms already: the root `<svg>`'s unit conversion is an
/// `ArtGroup` with `scale(0.75)`, so a file's artwork was also drawn a third too large. The figures below make
/// the disagreement explicit rather than incidental.
///
/// Each test reads a **second state and compares** - the model's own arithmetic (and, for the export case, the
/// PDF read back) - rather than asserting a pixel is brighter than some number. A threshold test does not bite:
/// the page renders at 224, so "brighter than 200" is true of paper.
/// </summary>
public class GroupTransformCanvasTests
{
    // 200x200 user units at 0.75 pt per unit is a 150x150 pt page, which is the root group's own transform.
    private const string Header = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">";

    /// <summary>`translate(50,50) scale(2)` on a 10-unit square at the origin: 50..70 units, 37.5..52.5 pt.</summary>
    private const string TranslatedAndScaled = Header +
        "<g transform=\"translate(50,50) scale(2)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>An outer `translate(10,10)` and an inner `scale(2)`: 10..30 units, 7.5..22.5 pt. Composing the
    /// two the other way round gives 20..40 units, so this test tells the two orders apart.</summary>
    private const string Nested = Header +
        "<g transform=\"translate(10,10)\"><g transform=\"scale(2)\">" +
        "<rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></g></svg>";

    /// <summary>`translate(40,40) rotate(90)`: the square turns onto its side, 30..40 by 40..50 units, so
    /// 22.5..30 by 30..37.5 pt - a box no translation alone could produce.</summary>
    private const string Rotated = Header +
        "<g transform=\"translate(40,40) rotate(90)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>An axis-flipping scale: `translate(60,10) scale(-1,1)` mirrors the square onto 50..60 units,
    /// 37.5..45 pt, by 7.5..15 pt.</summary>
    private const string Flipped = Header +
        "<g transform=\"translate(60,10) scale(-1,1)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>A ten-unit square scaled eight times under the root's own 0.75: 0..80 user units, 0..60 pt.
    /// Enlarged enough that a raster stretched to fit it is a visibly different picture from a sharp one.</summary>
    private const string LargeGroup = Header +
        "<g transform=\"scale(8)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>
    /// The **text** figure the PDF agreement facts measure: a 10pt line at local (0,10) inside the same
    /// `translate(50,50) scale(2)` group the rect uses.
    ///
    /// Its local baseline is (0,10) user units; the frame above it is that group composed with the root
    /// unit-conversion group, `(1.5,0,0,1.5,37.5,37.5)`, so the baseline lands at (37.5,52.5) pt and the 10pt face
    /// is set at 15pt. Against an exporter that writes the block at its own local origin - which is what #164
    /// fixed - the page carries the baseline at (0,7.5) pt in a 7.5pt face, and neither number is close.
    /// </summary>
    private const string TranslatedAndScaledText = Header +
        "<g transform=\"translate(50,50) scale(2)\"><text x=\"0\" y=\"10\" font-size=\"10\">Hi</text></g></svg>";

    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel, CadDocument Document)
        Host(string svg)
    {
        var viewModel = new EditorViewModel();

        // Imported before the workspace is attached: the canvas takes the document it is shown at attach time.
        viewModel.ImportDocument(SvgReader.Read(svg).Document);

        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);
        viewModel.Tool = EditorTool.Select;

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel, viewModel.Document);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Click through the real pointer path, so the frame the canvas draws in is the frame that is picked.</summary>
    private static void Click(Window window, CanvasWorkspace workspace, Point2D model)
    {
        Point p = workspace.ModelToWindow(model);
        InputInjection.Click(window, p.X, p.Y, clickCount: 1, shift: false);
        Settle();
    }

    /// <summary>
    /// Drag through the real pointer path, between two **world** points.
    ///
    /// The points are converted by the canvas's own mapping, so what the pointer travels is exactly the
    /// model distance between them; the gesture under test is the one a person makes, not a call to an
    /// internal method.
    /// </summary>
    private static void Drag(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point a = workspace.ModelToWindow(from);
        Point b = workspace.ModelToWindow(to);
        InputInjection.Press(window, a.X, a.Y, shift: false);
        InputInjection.Move(window, (a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, leftDown: true);
        InputInjection.Move(window, b.X, b.Y, leftDown: true);
        InputInjection.Release(window, b.X, b.Y);
        Settle();
    }

    /// <summary>
    /// An item's box in document/world coordinates - the frame the pointer and <c>ModelToWindow</c> are in.
    ///
    /// Written out here rather than asked of the code under test, so "the model moved by the pointer's
    /// delta" is measured against arithmetic this test states itself.
    /// </summary>
    private static Rect2D InWorld(LayerItem item)
    {
        Rect2D box = item switch
        {
            PathItem path => path.BoundingBox(),
            TextItem text => text.BoundingBox(),
            _ => Rect2D.Empty,
        };

        if (box.IsEmpty)
        {
            return box;
        }

        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        Rect2D mapped = transform.Transform(box);
        Vector2D origin = item.ArtboardOffset();
        return new Rect2D(mapped.X + origin.X, mapped.Y + origin.Y, mapped.Width, mapped.Height);
    }

    /// <summary>
    /// The bounding box of the ink the canvas actually drew, in points, by rendering page 0 at one pixel per
    /// point - the renderer the editor shows is the renderer under test, not a second one.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) Drawn(CadDocument document, CanvasWorkspace workspace)
    {
        (int left, int top, int right, int bottom, _) = Ink(document, workspace);
        return (left, top, right, bottom);
    }

    /// <summary>
    /// The drawn ink's box **and how many pixels are in it**, in points, at one pixel per point.
    ///
    /// The count is the second half because a filtered object is a **raster** drawn from an offscreen bitmap:
    /// a frame that rasterises at the wrong density leaves the box roughly where it belongs and the pixels
    /// obviously wrong - a hard edge spread over as many pixels as the picture was stretched.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom, int Count) Ink(
        CadDocument document, CanvasWorkspace workspace)
    {
        workspace.InvalidateVisual();
        PageRenderer.Workspace = workspace;
        byte[]? png = PageRenderer.Render(document, 0, 72);
        Assert.NotNull(png);

        using var stream = new MemoryStream(png!);
        using var bitmap = new Bitmap(stream);
        byte[] pixels = Read(bitmap, out int stride);
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;

        int left = width, top = height, right = -1, bottom = -1;
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (IsInk(pixels, stride, x, y))
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                    count++;
                }
            }
        }

        Assert.True(right >= 0, "nothing was drawn at all");
        return (left, top, right, bottom, count);
    }

    private static byte[] Read(Bitmap bitmap, out int stride)
    {
        stride = bitmap.PixelSize.Width * 4;
        var buffer = new byte[stride * bitmap.PixelSize.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        bitmap.CopyPixels(
            new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
            handle.AddrOfPinnedObject(), buffer.Length, stride);
        handle.Free();
        return buffer;
    }

    /// <summary>The black artwork - much darker than the paper the page renders at.</summary>
    private static bool IsInk(byte[] pixels, int stride, int x, int y)
    {
        int at = (y * stride) + (x * 4);
        return pixels[at] < 60 && pixels[at + 1] < 60 && pixels[at + 2] < 60; // BGRA
    }

    private static void AssertDrawnAt(
        (int Left, int Top, int Right, int Bottom) ink, Rect2D expected, string what)
    {
        Assert.True(
            Math.Abs(ink.Left - expected.Left) <= 2 && Math.Abs(ink.Top - expected.Top) <= 2 &&
            Math.Abs(ink.Right - expected.Right) <= 2 && Math.Abs(ink.Bottom - expected.Bottom) <= 2,
            $"{what} should be drawn at {expected.Left},{expected.Top} to {expected.Right},{expected.Bottom} " +
            $"but was drawn at {ink.Left},{ink.Top} to {ink.Right},{ink.Bottom}");
    }

    /// <summary>An item's bounds as the artboard sees them, every enclosing group's transform composed in.</summary>
    private static Rect2D InArtboard(LayerItem item)
    {
        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        Rect2D box = item switch
        {
            PathItem path => path.BoundingBox(),
            _ => Rect2D.Empty,
        };

        return transform.Transform(box);
    }

    /// <summary>
    /// The drawn bounds are the transformed ones.
    ///
    /// `translate(50,50) scale(2)` on a 10-unit square at the origin puts it at 50..70 user units. The file's
    /// root group carries the 0.75 that takes user units to points, so the square is 37.5..52.5 pt. Drawing it
    /// at 0..10 pt - which is what ignoring group transforms does - is a different picture, not a near miss.
    /// </summary>
    [AvaloniaFact]
    public void ATransformedGroupIsDrawnWhereTheFilePutsIt()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            AssertDrawnAt(Drawn(document, workspace), new Rect2D(37.5, 37.5, 15, 15), "the rect");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// What the canvas's own hit test answers at a world point: the deepest object under it, which is
    /// what a double-click drills to and what a single click selects the ancestor of.
    /// </summary>
    private static LayerItem? Picked(CadDocument document, Point2D at)
        => SelectionEngine.Within(document.Artboards[0], at);

    /// <summary>Whether an item is the rect or holds it - "the click landed on this artwork".</summary>
    private static bool Contains(LayerItem item, LayerItem target)
        => ReferenceEquals(item, target) || SelectionEngine.Descendants(item).Contains(target);

    /// <summary>
    /// A click at the transformed position picks the rect, and a click where the canvas used to draw it -
    /// where the model's own untransformed geometry sits - picks nothing.
    ///
    /// Both halves matter. Only the first would pass for a canvas that drew the rect twice; only the second
    /// would pass for one that drew nothing at all. The gesture is driven through the real pointer path as
    /// well, so the frame the canvas draws in is the frame it picks in rather than a frame only the engine
    /// knows about.
    /// </summary>
    [AvaloniaFact]
    public void TheTransformedPositionPicksTheRectAndTheUntransformedOnePicksNothing()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());

            // 60 user units is 45 pt: inside the transformed square.
            Assert.Same(rect, Picked(document, new Point2D(45, 45)));

            // The same point through the pointer: something containing the rect is selected, which is the
            // outermost group the drill starts at (a second click descends - see CanvasDrillTests).
            viewModel.ClearSelection();
            Click(window, workspace, new Point2D(45, 45));
            LayerItem selected = Assert.Single(viewModel.SelectedObjects);
            Assert.True(Contains(selected, rect),
                "the click at the transformed position selected something that does not hold the rect");

            // 5 user units is 3.75 pt: inside the square only as the untransformed geometry has it.
            viewModel.ClearSelection();
            Click(window, workspace, new Point2D(3.75, 3.75));
            Assert.Empty(viewModel.SelectedObjects);
            Assert.Null(Picked(document, new Point2D(3.75, 3.75)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Two nested groups compose outermost last: `translate(10,10)` around `scale(2)` is 10..30 units.</summary>
    [AvaloniaFact]
    public void ANestedGroupIsComposedWithTheOneAroundIt()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(Nested);
        try
        {
            AssertDrawnAt(Drawn(document, workspace), new Rect2D(7.5, 7.5, 15, 15), "the nested rect");

            PathItem rect = Assert.Single(document.AllPaths());

            // 20 user units is 15 pt: inside the composed square. Composed the other way round it would be
            // 20..40 units, and this point would be outside it.
            Assert.Same(rect, Picked(document, new Point2D(15, 15)));
            Assert.Null(Picked(document, new Point2D(3.75, 3.75)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A rotation is composed, not approximated away: the square is drawn - and picked - on its side.
    ///
    /// The square is turned about the group's origin and then moved to (40,40) user units, so it covers
    /// 30..40 by 40..50 units: 22.5..30 by 30..37.5 pt. A translation-only fix would leave it at 30..37.5 pt
    /// square on the diagonal, which the pixel box below separates.
    /// </summary>
    [AvaloniaFact]
    public void ARotatedGroupIsDrawnAndPickedWhereTheRotationPutsIt()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(Rotated);
        try
        {
            AssertDrawnAt(Drawn(document, workspace), new Rect2D(22.5, 30, 7.5, 7.5), "the rotated rect");

            PathItem rect = Assert.Single(document.AllPaths());

            // 35,45 user units is 26.25,33.75 pt: the middle of the rotated square.
            Assert.Same(rect, Picked(document, new Point2D(26.25, 33.75)));
            Assert.Null(Picked(document, new Point2D(3.75, 3.75)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A scale that flips an axis is composed too, rather than being dropped or turned into a mirror of the
    /// bounding box. `scale(-1,1)` under a `translate(60,10)` puts the square at 50..60 by 10..20 units.
    /// </summary>
    [AvaloniaFact]
    public void AnAxisFlippingGroupIsDrawnAndPickedOnTheFlippedSide()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(Flipped);
        try
        {
            AssertDrawnAt(Drawn(document, workspace), new Rect2D(37.5, 7.5, 7.5, 7.5), "the flipped rect");

            PathItem rect = Assert.Single(document.AllPaths());

            // 55,15 user units is 41.25,11.25 pt: inside the flipped square, and outside the untransformed one.
            Assert.Same(rect, Picked(document, new Point2D(41.25, 11.25)));
            Assert.Null(Picked(document, new Point2D(3.75, 3.75)));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The document as the exported PDF's **page** holds it - the artwork, not the model we handed the exporter.
    ///
    /// This goes through <see cref="PdfImporter.TryImportVector"/>, which parses the object model and the content
    /// streams, and deliberately **not** through <see cref="PdfImporter.Import(byte[])"/>. That path cannot answer
    /// any question about what the exporter wrote: our export carries the complete model as a lossless sidecar and
    /// the importer prefers it, so <c>Import</c> hands back the very document the exporter was given - artboards,
    /// group transforms, artboard offsets and all - and reports the same numbers whatever the content stream says.
    /// A canvas-versus-PDF comparison made through it is the canvas against itself and stays green through any
    /// exporter frame bug (issue #170). Only a reader of the page can disagree with the exporter.
    /// </summary>
    private static CadDocument OnThePage(CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);

        Assert.True(
            PdfImporter.TryImportVector(pdf, out CadDocument? page) && page is not null,
            "the exported PDF did not come back through the vector import");

        return page!;
    }

    /// <summary>The one path the page draws, preferring the filled one when the fill and the stroke came back as
    /// separate objects - the importer gives each painting operation its own item.</summary>
    private static PathItem DrawnPath(CadDocument page)
    {
        List<PathItem> paths = page.AllPaths().ToList();
        return paths.Count == 1 ? paths[0] : paths.Single(p => p.Fill.IsVisible);
    }

    /// <summary>
    /// The canvas and the exported PDF put the **rect** in the same place - the invariant this repository's
    /// accuracy work protects.
    ///
    /// The PDF is the reference an external viewer agrees with, so it is read back and its own geometry compared
    /// with the pixels the canvas drew. Both sides are measured, neither is derived from the other - and the
    /// reading is of the **page**, through <see cref="OnThePage"/>, because <see cref="PdfImporter.Import(byte[])"/>
    /// would return the sidecar and agree by construction (issue #170).
    /// </summary>
    [AvaloniaFact]
    public void TheCanvasAndTheExportedPdfFillOnePlan()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            (int left, int top, int right, int bottom) = Drawn(document, workspace);

            PathItem inPdf = DrawnPath(OnThePage(document));
            Rect2D pdfBox = InArtboard(inPdf);

            Assert.True(
                Math.Abs(left - pdfBox.Left) <= 2 && Math.Abs(top - pdfBox.Top) <= 2 &&
                Math.Abs(right - pdfBox.Right) <= 2 && Math.Abs(bottom - pdfBox.Bottom) <= 2,
                $"the canvas drew {left},{top} to {right},{bottom} and the page holds " +
                $"{pdfBox.Left},{pdfBox.Top} to {pdfBox.Right},{pdfBox.Bottom}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Where a block's own text matrix puts it: the baseline, in model points, with the block's rotation folded
    /// in.
    ///
    /// `Origin` is the top-left and a PDF text matrix is set on the baseline, so the baseline is one ascent down
    /// the text's own up axis - the same arithmetic <c>PdfDocumentExporter.WriteText</c> does. It is worked out
    /// from whichever block is handed in, so the canvas's own block and the block recovered from the page are
    /// read by the same rule, and the size the page records is read beside it.
    /// </summary>
    private static (Point2D Baseline, double Size) Baseline(TextItem text)
    {
        TextRun run = Assert.Single(text.Runs);
        double depth = run.PlacedAscentEm * run.FontSize;
        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);
        return (
            new Point2D(text.Origin.X - (sin * depth), text.Origin.Y + (cos * depth)),
            run.FontSize);
    }

    /// <summary>
    /// The canvas and the exported PDF set the **text** in the same place, at the same size.
    ///
    /// This is the half of the agreement the rect fact cannot state, and it is the half that bites: the exporter
    /// wrote every block through a separate loop at the identity frame until #164 gave text the frame its group
    /// draws it in, so a rect-only check passes for an exporter that puts every block at its own local origin.
    ///
    /// Both halves are measured. The canvas's half is the baseline in the frame the canvas composes and draws and
    /// picks with (<see cref="SelectionEngine.ToWorld"/>), read from the block it is actually showing; the page's
    /// half is the baseline a reader recovers from the content stream. Neither is derived from the other, and the
    /// page is read through <see cref="OnThePage"/> rather than through the sidecar - see there for why
    /// <see cref="PdfImporter.Import(byte[])"/> cannot answer this (issue #170).
    ///
    /// Skipped when the machine supplies no standard-font programme to embed: without one the export substitutes a
    /// face and there is no page text to compare. The rendered-ink assertion is inside the same guard for the same
    /// reason.
    /// </summary>
    [AvaloniaFact]
    public void TheCanvasAndTheExportedPdfSetTextInOnePlan()
    {
        if (StandardFontFiles.TryReadProgram(new StandardFace(StandardFontKind.Sans, false, false)) is null)
        {
            return;
        }

        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(TranslatedAndScaledText);
        try
        {
            TextItem source = Assert.Single(document.AllItems().OfType<TextItem>());

            // The canvas's half: the block it is drawing, placed by the composition the canvas draws with. The
            // frame is `translate(50,50) scale(2)` under the root's 0.75, so the local baseline (0,10) is
            // (37.5,52.5) pt. The block's own font size stays the SVG's 10; it is the frame that sets it at 15pt
            // on the page, so the scale is read from the frame rather than from the block.
            (Point2D localBaseline, double localSize) = Baseline(source);
            AffineTransform frame = SelectionEngine.ToWorld(source);
            Point2D canvasBaseline = frame.Transform(localBaseline);
            double canvasScale = Math.Sqrt(Math.Abs((frame.A * frame.D) - (frame.B * frame.C)));

            Assert.Equal(37.5, canvasBaseline.X, 3);
            Assert.Equal(52.5, canvasBaseline.Y, 3);
            Assert.Equal(1.5, canvasScale, 3);

            // And the canvas's rendered picture has to be there, on the frame's baseline: an unmoved block would
            // start at x=0.
            (int left, int top, int right, int bottom) = Drawn(document, workspace);
            Assert.True(
                Math.Abs(left - canvasBaseline.X) <= 3 && Math.Abs(bottom - canvasBaseline.Y) <= 3,
                $"the canvas drew the block at {left},{top} to {right},{bottom}, not on the frame's baseline at " +
                $"{canvasBaseline.X},{canvasBaseline.Y}");

            // The page's half: what a reader recovers from the content stream.
            TextItem printed = Assert.Single(OnThePage(document).AllItems().OfType<TextItem>());
            (Point2D pageBaseline, double pageSize) = Baseline(printed);

            Assert.True(
                Math.Abs(pageBaseline.X - canvasBaseline.X) < 1e-2 &&
                Math.Abs(pageBaseline.Y - canvasBaseline.Y) < 1e-2 &&
                Math.Abs(pageSize - (localSize * canvasScale)) < 1e-3,
                $"the canvas sets the block on the baseline ({canvasBaseline.X}, {canvasBaseline.Y}) pt in a " +
                $"{localSize * canvasScale} pt face; the page re-imports at ({pageBaseline.X}, {pageBaseline.Y}) pt " +
                $"in a {pageSize} pt face (block top-left {printed.Origin.X}, {printed.Origin.Y}).");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The **sidecar** round-trips the model: an exported document re-imported through
    /// <see cref="PdfImporter.Import(byte[])"/> is the document that was exported, group transforms intact.
    ///
    /// This is labelled for what it is and it is **not** an agreement check. It says the lossless model survived
    /// the file, which is exactly what the sidecar is for; it says nothing about the page, and it passes however
    /// badly the exporter writes the content stream - which is why the two facts above read the page instead
    /// (issue #170). It is kept because the round trip is a real promise of our own format, and because deleting a
    /// test that may be the only cover for "the sidecar still loads" would hide a different defect.
    /// </summary>
    [AvaloniaFact]
    public void TheSidecarRoundTripsTheModelRatherThanProvingThePage()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            // The canvas is not the subject here; it is only what makes this document the one the editor holds.
            Drawn(document, workspace);

            CadDocument back = PdfImporter.Import(PdfDocumentExporter.Export(document));

            // The sidecar hands the model back with the group's transform still on the group, rather than baked
            // into the geometry - which is the difference between this path and the page.
            PathItem rect = Assert.Single(back.AllPaths());
            ArtGroup group = Assert.IsType<ArtGroup>(rect.Container);
            Assert.Equal(2.0, group.Transform.A, 6);
            Assert.Equal(2.0, group.Transform.D, 6);
            Assert.Equal(50.0, group.Transform.E, 6);
            Assert.Equal(50.0, group.Transform.F, 6);

            // And the rect is still stored in the group's own local coordinates, not in page ones.
            Rect2D local = rect.BoundingBox();
            Assert.Equal(0.0, local.Left, 6);
            Assert.Equal(10.0, local.Width, 6);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Asserts the box moved by a vector, reporting **both** components when it did not.
    ///
    /// The pointer round trip is exact to well under a device pixel and no better, so the tolerance is
    /// half a point - far smaller than the 15-point error the un-converted frame produces.
    /// </summary>
    private static void AssertMovedBy(Vector2D expected, Rect2D before, Rect2D after)
    {
        var moved = new Vector2D(after.Left - before.Left, after.Top - before.Top);
        Assert.True(
            Math.Abs(moved.X - expected.X) <= 0.5 && Math.Abs(moved.Y - expected.Y) <= 0.5,
            $"the model should have moved {expected.X},{expected.Y} but moved {moved.X},{moved.Y}");
    }

    /// <summary>
    /// A drag inside a **scaled** group moves the stored geometry by the distance the pointer moved
    /// (issue #165).
    ///
    /// `translate(50,50) scale(2)` under the root's own 0.75 puts the 10-unit square at 37.5..52.5 pt, so a
    /// point of pointer travel is two thirds of a unit of the square's own coordinates. Applying the
    /// pointer's world delta straight to the stored geometry - which is what the gesture did - moves the
    /// square **1.5x as far as the pointer**, and the person sees the artwork slide out from under the
    /// cursor. The assertion is on the **model**, because the numbers a drag produces are what the document
    /// stores: a wrong delta is a wrong file, not just a wrong animation.
    /// </summary>
    [AvaloniaFact]
    public void ADraggedObjectInsideAScaledGroupMovesByThePointerDelta()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();

            Rect2D before = InWorld(rect);
            Assert.Equal(37.5, before.Left, 3);
            Assert.Equal(37.5, before.Top, 3);

            // The middle of the drawn square, dragged 30 pt to the right.
            Drag(window, workspace, new Point2D(45, 45), new Point2D(75, 45));

            Rect2D after = InWorld(rect);
            AssertMovedBy(new Vector2D(30, 0), before, after);
            Assert.Equal(15.0, after.Width, 3);
            Assert.Equal(15.0, after.Height, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A drag inside a **rotated** group moves in the direction the pointer moved, as well as the distance.
    ///
    /// Distance alone would pass for a fix that divided by the frame's scale: `translate(40,40) rotate(90)`
    /// has a unit scale, and applying the world delta to the stored geometry would send the square 22.5 pt
    /// **down the page** for a pointer that moved 30 pt to the right. Both components are asserted, so the
    /// frame has to be turned as well as scaled.
    /// </summary>
    [AvaloniaFact]
    public void ADraggedObjectInsideARotatedGroupMovesInTheDirectionThePointerMoved()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(Rotated);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();

            Rect2D before = InWorld(rect);
            Assert.Equal(22.5, before.Left, 3);
            Assert.Equal(30.0, before.Top, 3);

            // 35,45 user units is 26.25,33.75 pt: the middle of the rotated square.
            Drag(window, workspace, new Point2D(26.25, 33.75), new Point2D(56.25, 33.75));

            Rect2D after = InWorld(rect);
            AssertMovedBy(new Vector2D(30, 0), before, after);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A filter that replaces a shape with a solid black copy of its own coverage.
    ///
    /// Chosen because it is a filter that cannot be drawn without one - the pixels come out of the same
    /// offscreen rasterise-and-composite path a blur takes - while leaving edges **hard**, so the drawn
    /// pixels can be compared with the unfiltered picture pixel for pixel.
    /// </summary>
    private static void BlackCopyFilter(CadDocument document, string id)
    {
        document.AddFilter(new FilterSpec(id, new[]
        {
            FilterPrimitive.Solid(ColorRgb.Black, 1.0, "black"),
            FilterPrimitive.Combine("in", "black", "SourceAlpha", "out"),
        })
        {
            Output = "out",
        });
    }

    /// <summary>
    /// A **filtered** path inside a transformed group is drawn where the file puts it - issue #165's second
    /// lead, tested rather than assumed.
    ///
    /// A filtered object cannot be drawn with `DrawGeometry`: it is rasterised offscreen and the bitmap is
    /// drawn in its place. The lead says that bitmap goes through the canvas's zoom/pan rather than the pushed
    /// group frame, which would put the picture somewhere else or at the wrong size. The filter used here
    /// leaves a hard-edged black copy of the shape, so the ink can be measured exactly.
    ///
    /// Measured against the unfiltered picture, this is what the lead actually turns out to be: the **box** is
    /// right - the pushed frame does place the bitmap - but the raster behind it was sampled at the canvas's own
    /// zoom, so an eightfold frame magnifies an eight-times-too-small bitmap and every edge is a ramp that wide.
    /// </summary>
    [AvaloniaFact]
    public void AFilteredPathInsideATransformedGroupIsDrawnWhereTheFilePutsIt()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(LargeGroup);
        try
        {
            (int left, int top, int right, int bottom, int count) = Ink(document, workspace);
            AssertDrawnAt((left, top, right, bottom), new Rect2D(0, 0, 60, 60), "the unfiltered rect");

            BlackCopyFilter(document, "copy");
            Assert.Single(document.AllPaths()).FilterId = "copy";

            (left, top, right, bottom, int filtered) = Ink(document, workspace);

            // The same shape, so the same ink - to within the antialiasing of one edge. A bitmap stretched
            // to cover the group's eightfold enlargement lays a ramp eight pixels wide along every edge, and
            // an ink threshold of 60 (below) throws the outer half of it away, so the drawn shape shrinks.
            Assert.True(filtered > count * 0.9,
                $"the filtered copy should cover the shape it copied: {filtered} ink pixels against {count}, " +
                $"drawn {left},{top} to {right},{bottom}");

            AssertDrawnAt((left, top, right, bottom), new Rect2D(0, 0, 60, 60), "the filtered rect");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A node of a path, in world coordinates, by the test's own arithmetic.</summary>
    private static Point2D AnchorInWorld(PathItem path, int index)
    {
        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = path.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        return transform.Transform(path.SubPaths[0].Nodes[index].Anchor) + path.ArtboardOffset();
    }

    /// <summary>
    /// A **rotation** inside a scaled group turns about the centre the handle was drawn at.
    ///
    /// The square is turned a quarter turn about the middle of the selection, which leaves its box exactly
    /// where it was - so the box cannot tell right from wrong here and the assertion is on a **node's** world
    /// position: the bottom-left corner (37.5,37.5) becomes (52.5,37.5). The old code turned the stored
    /// geometry about the world centre unchanged, which inside a frame of 1.5 lands that node at (172.5,37.5).
    /// </summary>
    [AvaloniaFact]
    public void ARotatedObjectInsideAScaledGroupTurnsAboutTheHandleItWasGiven()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();

            // The rotation knob sits `lift` above the top edge of the chrome box, which is the rect's own
            // world box: 37.5..52.5 either way, so the knob is directly above (45,37.5).
            double lift = Math.Max(22.0 / workspace.Zoom, 4.0);
            double radius = 7.5 + lift;
            Drag(window, workspace, new Point2D(45, 45 - radius), new Point2D(45 + radius, 45));

            Point2D corner = AnchorInWorld(rect, 0);
            Assert.True(
                Math.Abs(corner.X - 52.5) <= 0.5 && Math.Abs(corner.Y - 37.5) <= 0.5,
                $"the quarter turn should carry the corner to 52.5,37.5 but it landed at {corner.X},{corner.Y}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A **resize** inside a scaled group scales about the handle opposite the one being pulled, in world
    /// coordinates.
    ///
    /// The bottom-right corner of the chrome box is dragged from (52.5,52.5) to (67.5,52.5): doubling the box's
    /// width and leaving its height alone, with the top-left corner (37.5,37.5) fixed. So the model is
    /// 37.5..67.5 by 37.5..52.5. The old code scaled the stored geometry about the world pivot directly, which
    /// put the square at -18.75..-3.75 - off the page entirely.
    /// </summary>
    [AvaloniaFact]
    public void AResizedObjectInsideAScaledGroupScalesAboutTheHandleOpposite()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel, CadDocument document) =
            Host(TranslatedAndScaled);
        try
        {
            PathItem rect = Assert.Single(document.AllPaths());
            viewModel.SelectObject(rect);
            Settle();

            Drag(window, workspace, new Point2D(52.5, 52.5), new Point2D(67.5, 52.5));

            Rect2D after = InWorld(rect);
            Assert.True(
                Math.Abs(after.Left - 37.5) <= 0.5 && Math.Abs(after.Top - 37.5) <= 0.5 &&
                Math.Abs(after.Right - 67.5) <= 0.5 && Math.Abs(after.Bottom - 52.5) <= 0.5,
                $"the resize should leave the box at 37.5,37.5 to 67.5,52.5 but left it at " +
                $"{after.Left},{after.Top} to {after.Right},{after.Bottom}");
        }
        finally
        {
            window.Close();
        }
    }
}
