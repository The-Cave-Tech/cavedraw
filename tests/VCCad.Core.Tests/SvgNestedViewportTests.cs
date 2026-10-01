using VCCad.Core.Model;
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
