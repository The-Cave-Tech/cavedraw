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
    /// The bounding box of the ink the canvas actually drew, in points, by rendering page 0 at one pixel per
    /// point - the renderer the editor shows is the renderer under test, not a second one.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom) Drawn(CadDocument document, CanvasWorkspace workspace)
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
                }
            }
        }

        Assert.True(right >= 0, "nothing was drawn at all");
        return (left, top, right, bottom);
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
    /// The canvas and the exported PDF put the rect in the same place - the invariant this repository's accuracy
    /// work protects.
    ///
    /// The PDF is the reference an external viewer agrees with, so it is read back and its own geometry compared
    /// with the pixels the canvas drew. Both sides are measured, neither is derived from the other.
    /// </summary>
    [AvaloniaFact]
    public void TheCanvasAndTheExportedPdfFillOnePlan()
    {
        (Window window, CanvasWorkspace workspace, _, CadDocument document) = Host(TranslatedAndScaled);
        try
        {
            (int left, int top, int right, int bottom) = Drawn(document, workspace);

            CadDocument back = PdfImporter.Import(PdfDocumentExporter.Export(document));
            PathItem inPdf = Assert.Single(back.AllPaths());
            Rect2D pdfBox = InArtboard(inPdf);

            Assert.True(
                Math.Abs(left - pdfBox.Left) <= 2 && Math.Abs(top - pdfBox.Top) <= 2 &&
                Math.Abs(right - pdfBox.Right) <= 2 && Math.Abs(bottom - pdfBox.Bottom) <= 2,
                $"the canvas drew {left},{top} to {right},{bottom} and the PDF holds " +
                $"{pdfBox.Left},{pdfBox.Top} to {pdfBox.Right},{pdfBox.Bottom}");
        }
        finally
        {
            window.Close();
        }
    }
}
