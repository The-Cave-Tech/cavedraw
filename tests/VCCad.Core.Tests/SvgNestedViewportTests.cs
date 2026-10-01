using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A nested `svg` is a viewport of its own.
///
/// SVG establishes a new user space by a `viewBox` on any `svg` element, not only the outermost one, and §7.9 makes
/// a new viewport of every nested one: its bounds come from `x`, `y`, `width` and `height`, and percentages inside
/// it are redefined to be percentages of *that* viewport rather than of the page. Reading it as a plain `g` draws
/// its content at the outer scale in the wrong place, and says nothing - which is the same silent-loss family as
/// the file's own numbers being replaced by defaults.
///
/// **The unit conversion is not applied twice.** The factor of 0.75 between the file's user units and the model's
/// points lives in the root transform, which every group below inherits; the nested fit is measured user unit
/// against user unit, so it is a pure ratio. A nested element importing three quarters of its size would be that
/// factor applied a second time.
///
/// Every assertion is on the model geometry **in points** with the enclosing transforms composed in, because that
/// is where a reader that ignored the nested viewport and a reader that honoured it differ.
/// </summary>
public class SvgNestedViewportTests
{
    private const string Header = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"800\" height=\"600\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    /// <summary>A node's coordinate as the artboard sees it, every enclosing group's transform composed in.</summary>
    private static Point2D At(PathItem path, int node)
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

        return transform.Transform(path.SubPaths[0].Nodes[node].Anchor);
    }

    private static PathItem OnlyRect(SvgImportResult result) => Assert.Single(result.Document.AllPaths());

    /// <summary>
    /// A group's clip outline as the artboard sees it.
    ///
    /// Only the **ancestors'** transforms are composed in, because that is the frame the model records a group's
    /// clip in: a clip is the outline an item is drawn inside, so it lives in the space the item is placed in -
    /// the one the enclosing element's own coordinates are written in - and the group's own transform maps its
    /// children into that space rather than moving the outline out of it.
    /// </summary>
    private static IReadOnlyList<Point2D> ClipOutlineInArtboard(LayerItem item)
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

        ClipSpec clip = Assert.Single(item.Clips);
        SubPath outline = Assert.Single(clip.SubPaths);
        return outline.Nodes.Select(node => transform.Transform(node.Anchor)).ToList();
    }

    // ---------------------------------------------------------------- the port clips what it holds

    /// <summary>
    /// **A viewport clips its content to its port.**
    ///
    /// The case from the report, and the reason to nest a viewport at all: a 400-unit rect in a 50-unit port is a
    /// 50-unit window onto it, which is what a browser and Inkscape draw. Reading no clip draws the rect whole -
    /// eight times past the port - and it is a *plausible* wrong answer, because a drawing that overflows looks
    /// deliberate.
    ///
    /// The outline is on the viewport's own group, so it covers everything the element holds and composes with any
    /// viewport around it, and it is written in the containing space - a 50-unit port at (50,50) is a 50-unit
    /// rectangle **at (50,50)**, which is where the enclosing element's numbers put it. The 0.75 of the file's
    /// user units to the model's points is carried by the root group, so 50 units is 37.5 pt.
    ///
    /// Before: no clip existed at all (`Assert.Single(item.Clips)` found none), and the rect was drawn from 37.5 pt
    /// to 337.5 pt instead of being cut at 75 pt.
    /// </summary>
    [Fact]
    public void ANestedViewportClipsItsContentToItsPort()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
            "<svg x=\"50\" y=\"50\" width=\"50\" height=\"50\"><rect width=\"400\" height=\"400\"/></svg></svg>");

        PathItem rect = OnlyRect(result);
        ArtGroup viewport = Assert.IsType<ArtGroup>(rect.Container);

        IReadOnlyList<Point2D> port = ClipOutlineInArtboard(viewport);
        Assert.Equal(4, port.Count);
        Assert.Equal(37.5, port.Min(c => c.X), 9);
        Assert.Equal(37.5, port.Min(c => c.Y), 9);
        Assert.Equal(75.0, port.Max(c => c.X), 9);
        Assert.Equal(75.0, port.Max(c => c.Y), 9);

        // And the rect really does reach past it: 400 units from the port's own corner is 300 pt of rect.
        Assert.Equal(37.5, At(rect, 0).X, 9);
        Assert.Equal(37.5, At(rect, 0).Y, 9);
        Assert.Equal(337.5, At(rect, 2).X, 9);
        Assert.Equal(337.5, At(rect, 2).Y, 9);

        // So the part of the rect that is visible is the port, not the whole rect.
        Assert.True(port.Max(c => c.X) < At(rect, 2).X, "the port cuts the rect rather than containing it");
    }

    /// <summary>
    /// **A port its content already fits inside is not a clip.**
    ///
    /// A clip that removes nothing states nothing the file did not already say, and recording one would put a mask
    /// on every file that happens to nest a viewport - the mistake the PDF importer's form `/BBox` made once
    /// already, twelve masks for twelve pages that clipped nothing. It matters here because most nested viewports
    /// in the wild exist for placement rather than for cropping.
    ///
    /// Before: the model had no clip either way, so this test's positive half is the one that had to be added -
    /// it is the `Assert.False` that stops the clip above from being written unconditionally.
    /// </summary>
    [Fact]
    public void ANestedViewportThatAlreadyContainsItsContentIsNotClipped()
    {
        SvgImportResult result = Read(
            "<svg x=\"10\" y=\"10\" width=\"100\" height=\"100\"><rect width=\"10\" height=\"10\"/></svg>");

        PathItem rect = OnlyRect(result);
        ArtGroup viewport = Assert.IsType<ArtGroup>(rect.Container);

        Assert.False(rect.IsClipped, "a port that removes nothing is not a clip");
        Assert.Empty(viewport.Clips);
        Assert.Empty(result.Warnings);

        // The picture is what it always was: the 10-unit square sits at the port's corner, 7.5 pt in, and is
        // 7.5 pt across.
        Assert.Equal(7.5, At(rect, 0).X, 9);
        Assert.Equal(7.5, At(rect, 0).Y, 9);
        Assert.Equal(15.0, At(rect, 2).X, 9);
        Assert.Equal(15.0, At(rect, 2).Y, 9);
    }

    /// <summary>
    /// **A viewport inside a viewport is inside both ports, not inside the inner one alone.**
    ///
    /// The file nests two viewports, so the content is cut twice: the inner port removes what is outside it, and
    /// the outer port removes what is outside that. A model that let the inner clip replace the outer one would
    /// show content the file hides - and it would look like the inner viewport working, which is the harder
    /// failure to notice.
    ///
    /// Asserted where the two genuinely differ: the ports overlap but neither contains the other, so a point in
    /// the overlap picks the rect, a point inside the inner port but outside the outer picks nothing, and a point
    /// inside the outer port but outside the inner picks nothing either. The middle one is what a replaced outer
    /// clip would get wrong, and the last is what a missing inner clip would. Those three probes are in the file's
    /// own user units, because that is the space selection reads a clip and the geometry it cuts in.
    ///
    /// Before: neither viewport carried a clip, so every one of the three picked the rect.
    /// </summary>
    [Fact]
    public void ANestedViewportClipIsComposedWithTheOneAroundItAndDoesNotReplaceIt()
    {
        SvgImportResult result = Read(
            "<svg width=\"50\" height=\"50\">" +
            "<svg x=\"20\" y=\"20\" width=\"50\" height=\"50\"><rect width=\"500\" height=\"500\"/></svg></svg>");

        PathItem rect = OnlyRect(result);
        ArtGroup inner = Assert.IsType<ArtGroup>(rect.Container);
        ArtGroup outer = Assert.IsType<ArtGroup>(inner.Container);

        // Both ports are on the model, each on the viewport it belongs to.
        Assert.Single(outer.Clips);
        Assert.Single(inner.Clips);
        Assert.Equal(2, SelectionEngine.ClipsOn(rect).Count);

        // In points: the outer port is 50 units of the 800-unit page - 37.5 pt - and the inner one runs from 15 pt
        // to 52.5 pt, so the two overlap from 15 pt to 37.5 pt.
        IReadOnlyList<Point2D> outerPort = ClipOutlineInArtboard(outer);
        IReadOnlyList<Point2D> innerPort = ClipOutlineInArtboard(inner);
        Assert.Equal(0.0, outerPort.Min(c => c.X), 9);
        Assert.Equal(37.5, outerPort.Max(c => c.X), 9);
        Assert.Equal(15.0, innerPort.Min(c => c.X), 9);
        Assert.Equal(52.5, innerPort.Max(c => c.X), 9);

        Artboard artboard = result.Document.Artboards[0];

        // Inside both ports: the rect is what the pointer is on.
        Assert.Same(rect, SelectionEngine.Within(artboard, new Point2D(30, 30)));

        // Inside the inner port and outside the outer one: the outer clip still removes it.
        Assert.Null(SelectionEngine.Within(artboard, new Point2D(60, 60)));

        // Inside the outer port and outside the inner one: so does the inner clip.
        Assert.Null(SelectionEngine.Within(artboard, new Point2D(10, 10)));
    }

    /// <summary>
    /// **A viewport that says its content is not cut keeps all of it.**
    ///
    /// `overflow` is the property that decides this and `visible` is the one value that means "draw past the
    /// port", so a reader that clipped every viewport would be substituting its own answer for the file's - the
    /// same silent loss, in the other direction. All three ways a file can say it are read, because they are one
    /// property through one cascade: the presentation attribute, the inline `style`, and a stylesheet rule.
    ///
    /// Before: nothing read `overflow` at all, so there was nothing to get wrong and nothing to honour either.
    /// </summary>
    [Theory]
    [InlineData("<svg x=\"50\" y=\"50\" width=\"50\" height=\"50\" overflow=\"visible\">" +
                "<rect width=\"400\" height=\"400\"/></svg>")]
    [InlineData("<svg x=\"50\" y=\"50\" width=\"50\" height=\"50\" style=\"overflow:visible\">" +
                "<rect width=\"400\" height=\"400\"/></svg>")]
    [InlineData("<style>svg { overflow: visible; }</style>" +
                "<svg x=\"50\" y=\"50\" width=\"50\" height=\"50\"><rect width=\"400\" height=\"400\"/></svg>")]
    public void ANestedViewportThatSaysOverflowVisibleKeepsItsOverflow(string body)
    {
        SvgImportResult result = Read(body);

        PathItem rect = OnlyRect(result);
        ArtGroup viewport = Assert.IsType<ArtGroup>(rect.Container);

        Assert.Empty(result.Warnings);
        Assert.False(rect.IsClipped, "the file says the content is not cut at the port");
        Assert.Empty(viewport.Clips);

        // The whole 400-unit rect, 300 pt of it, from the same corner as the port.
        Assert.Equal(37.5, At(rect, 0).X, 9);
        Assert.Equal(337.5, At(rect, 2).X, 9);
    }

    /// <summary>
    /// **A nested `svg` whose `x` cannot be read is reported and not drawn.**
    ///
    /// The port is what the content is cut to, so it has to be somewhere - and a stated `x` the reader cannot
    /// resolve used to become the attribute's default of 0. That placement is a number the file never wrote, and
    /// with the port now an outline in the model it would be a clip in the wrong place as well. `width` and
    /// `height` already refuse this way; `x` and `y` are the other two sides of the same rectangle.
    ///
    /// Before: the rect was drawn at the origin (x = 0) and the port went unclipped.
    /// </summary>
    [Fact]
    public void ANestedViewportWhoseOriginCannotBeReadIsReportedAndNotDrawn()
    {
        SvgImportResult result = Read(
            "<svg x=\"10vw\" y=\"0\" width=\"100\" height=\"100\"><rect width=\"10\" height=\"10\"/></svg>");

        Assert.Empty(result.Document.AllPaths());
        Assert.Contains(result.Warnings, w =>
            w.Contains("nested <svg>", StringComparison.Ordinal) &&
            w.Contains("x=\"10vw\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the root viewport is the page

    /// <summary>
    /// **The root viewport is the artboard, and the page is what clips it - so the root needs no clip of its own.**
    ///
    /// `overflow` is a property of any viewport, but the two cases genuinely differ, and this test says how rather
    /// than leaving the difference unstated. The outermost `svg`'s port **is** the page: the artboard is exactly
    /// that rectangle in points, so a reader that also stored a clip would be recording the page a second time.
    /// And the page is already clipped everywhere it matters - `CanvasWorkspace.ClipToArtboard` is the view-level
    /// version of this same outline, and a PDF viewer cuts at the MediaBox - which is a clip the model cannot
    /// express any other way and cannot lose.
    ///
    /// A nested port is the case that needs the model: it is interior to the page, and nothing else in any
    /// renderer cuts at it, which is why `ANestedViewportClipsItsContentToItsPort` is a different test.
    ///
    /// The overflow itself is kept, as it is for every PDF: a tiled page draws its pieces at full size and lets
    /// the page edge do the cutting, and rewriting the geometry to fit is what butchered the labels on the sample
    /// pattern. So the assertion below is that the rect still reaches twice the page's width.
    /// </summary>
    [Fact]
    public void TheRootViewportIsTheArtboardAndThePageIsWhatClipsIt()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
            "<rect width=\"400\" height=\"400\"/></svg>");

        // The port is the page: 200 user units is 150 pt of paper, and the artboard is that rectangle.
        Assert.Equal(150.0, result.Document.Artboards[0].Width, 9);
        Assert.Equal(150.0, result.Document.Artboards[0].Height, 9);

        PathItem rect = OnlyRect(result);

        // The content reaches 300 pt - twice the port - and the model keeps it, because the page is the cut.
        Assert.Equal(0.0, At(rect, 0).X, 9);
        Assert.Equal(300.0, At(rect, 2).X, 9);
        Assert.False(rect.IsClipped, "the root's port is the artboard, not a clip on the model");
        Assert.Empty(result.Warnings);
    }

    // ---------------------------------------------------------------- where the port is

    /// <summary>
    /// The case from the report: a 100x100 port at (10,10) holding a 50x50 view box, so the box is drawn at twice
    /// its size *inside the port* rather than at the page's own scale from the origin.
    ///
    /// Before the viewport was honoured this rect landed on (0,0)-(7.5,7.5) pt: the nested `viewBox` was ignored
    /// and the 10-unit square was the file's own 10 units carried out by the root transform alone.
    /// </summary>
    [Fact]
    public void ANestedViewportWithASizeAndAViewBoxIsDrawnAtTheNestedScale()
    {
        SvgImportResult result = Read(
            "<svg x=\"10\" y=\"10\" width=\"100\" height=\"100\" viewBox=\"0 0 50 50\">" +
            "<rect width=\"10\" height=\"10\"/></svg>");

        PathItem rect = OnlyRect(result);
        Assert.Empty(result.Warnings);

        // 10 user units into the page is 7.5 pt, and the 10-unit square is twice its size, so 15 pt across.
        Assert.Equal(7.5, At(rect, 0).X, 9);
        Assert.Equal(7.5, At(rect, 0).Y, 9);
        Assert.Equal(22.5, At(rect, 2).X, 9);
        Assert.Equal(22.5, At(rect, 2).Y, 9);
    }

    /// <summary>
    /// A view box and no size: SVG 1.1 §5.1.2 gives the missing width and height the default `100%`, which is a
    /// percentage a nested viewport *can* resolve - the containing viewport is the page. The 50x50 box meets the
    /// 800x600 page at 12x, so it is 600 user units of the 800 and is centred.
    ///
    /// Before this it was ignored the same way: (0,0)-(7.5,7.5) pt at the page's origin.
    /// </summary>
    [Fact]
    public void ANestedViewportWithAViewBoxAloneFillsTheViewportAroundIt()
    {
        SvgImportResult result = Read("<svg viewBox=\"0 0 50 50\"><rect width=\"10\" height=\"10\"/></svg>");

        PathItem rect = OnlyRect(result);
        Assert.Empty(result.Warnings);

        Assert.Equal(75.0, At(rect, 0).X, 9);
        Assert.Equal(0.0, At(rect, 0).Y, 9);
        Assert.Equal(165.0, At(rect, 2).X, 9);
        Assert.Equal(90.0, At(rect, 2).Y, 9);
    }

    /// <summary>
    /// A size and no view box: the port is the user space, so the content keeps its size - but it is placed at the
    /// nested `x`/`y`, and the percentages inside it are percentages of the nested 100x100 rather than of the page.
    ///
    /// Before: the `x`/`y` were ignored and `50%` resolved against the page, so the rect landed on
    /// (0,0)-(300,225) pt - six times too wide and at the corner instead of 7.5 pt in.
    /// </summary>
    [Fact]
    public void ANestedViewportWithASizeAloneIsWhereXAndYPutIt()
    {
        SvgImportResult result = Read(
            "<svg x=\"10\" y=\"10\" width=\"100\" height=\"100\">" +
            "<rect width=\"50%\" height=\"50%\"/></svg>");

        PathItem rect = OnlyRect(result);
        Assert.Empty(result.Warnings);

        Assert.Equal(7.5, At(rect, 0).X, 9);
        Assert.Equal(7.5, At(rect, 0).Y, 9);
        Assert.Equal(45.0, At(rect, 2).X, 9);
        Assert.Equal(45.0, At(rect, 2).Y, 9);
    }

    /// <summary>
    /// Neither a size nor a view box: the port is 100% of the page and the user space is unchanged, so only `x`
    /// and `y` move the content - and a percentage inside is still a percentage of the nested viewport, which here
    /// is the page.
    ///
    /// Before: (0,0)-(300,225) pt. The percentages were right by accident and the placement was not.
    /// </summary>
    [Fact]
    public void ANestedViewportWithNoSizeAndNoViewBoxStillTakesTheViewportAroundIt()
    {
        SvgImportResult result = Read("<svg x=\"10\" y=\"10\"><rect width=\"50%\" height=\"50%\"/></svg>");

        PathItem rect = OnlyRect(result);
        Assert.Empty(result.Warnings);

        Assert.Equal(7.5, At(rect, 0).X, 9);
        Assert.Equal(7.5, At(rect, 0).Y, 9);
        Assert.Equal(307.5, At(rect, 2).X, 9);
        Assert.Equal(232.5, At(rect, 2).Y, 9);
    }

    /// <summary>
    /// A percentage inside a nested view box is measured **in the user coordinate system the content is written
    /// in**, which the view box establishes: SVG 1.1 §7.10 defines *actual-width* as the viewport dimension "within
    /// the user coordinate system for the viewport element". So `50%` of a 50-unit box is 25 units - half the
    /// drawing - and not 50 units, which is the box doubled and would fill the port.
    /// </summary>
    [Fact]
    public void APercentageInsideANestedViewBoxIsAFractionOfTheBox()
    {
        SvgImportResult result = Read(
            "<svg width=\"100\" height=\"100\" viewBox=\"0 0 50 50\">" +
            "<rect width=\"50%\" height=\"50%\"/></svg>");

        PathItem rect = OnlyRect(result);
        Assert.Empty(result.Warnings);

        // 25 units of the box, fitted at 2x into the port, is 50 user units of the page, which is 37.5 pt.
        Assert.Equal(0.0, At(rect, 0).X, 9);
        Assert.Equal(0.0, At(rect, 0).Y, 9);
        Assert.Equal(37.5, At(rect, 2).X, 9);
        Assert.Equal(37.5, At(rect, 2).Y, 9);
    }

    /// <summary>
    /// A stated width the reader cannot read is **not** replaced by the 100% default. That replacement is the
    /// defect this family is about: the drawing comes back at a size the file did not write while looking
    /// deliberate. The nested viewport is not established, so nothing inside it is drawn, and both halves of that
    /// are said.
    /// </summary>
    [Fact]
    public void ANestedViewportWhoseWidthCannotBeReadIsReportedAndNotDrawn()
    {
        SvgImportResult result = Read(
            "<svg x=\"0\" y=\"0\" width=\"10vw\" height=\"100\"><rect width=\"10\" height=\"10\"/></svg>");

        Assert.Empty(result.Document.AllPaths());
        Assert.Contains(result.Warnings, w => w.Contains("nested <svg>", StringComparison.Ordinal));
    }

    /// <summary>
    /// A nested `svg` inside a symbol the file never sizes has no containing viewport for its own `100%` default
    /// to be a percentage of - so the viewport cannot be established honestly, and saying so beats measuring the
    /// content against the page and calling that the nested size.
    /// </summary>
    [Fact]
    public void ANestedViewportWithNoContainingViewportForItsDefaultIsReported()
    {
        SvgImportResult result = Read(
            "<symbol id=\"s\"><svg><rect width=\"10\" height=\"10\"/></svg></symbol><use href=\"#s\"/>");

        Assert.Empty(result.Document.AllPaths());
        Assert.Contains(result.Warnings, w => w.Contains("nested <svg>", StringComparison.Ordinal));
    }

    /// <summary>
    /// SVG draws nothing for a viewport of zero width or height. Reported rather than quietly skipped: a drawing
    /// that is simply missing a piece says nothing about why, and this reader's rule is that a gap is something
    /// somebody can act on.
    /// </summary>
    [Fact]
    public void ANestedViewportWithNoAreaIsReported()
    {
        SvgImportResult result = Read(
            "<svg x=\"0\" y=\"0\" width=\"0\" height=\"100\"><rect width=\"10\" height=\"10\"/></svg>");

        Assert.Empty(result.Document.AllPaths());
        Assert.Contains(result.Warnings, w => w.Contains("nested <svg>", StringComparison.Ordinal));
    }
}
