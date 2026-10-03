using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A `marker` referenced by `marker-start`/`marker-mid`/`marker-end` is **placed as geometry at the vertex**, with
/// `orient="auto"` following the path's own direction there and `markerUnits` deciding the scale.
///
/// **Why geometry and not a report.** A `&lt;pattern&gt;` is a *paint* - it decides what colour a shape's interior
/// is - and <see cref="FillSpec"/> has nowhere to put one, so <see cref="SvgPatternTests"/> reports it by name. A
/// marker is the other kind of thing: it is artwork at a determinate place, oriented by a determinate tangent and
/// scaled by a determinate stroke width, and the model's geometry can hold that faithfully. Placing it is therefore
/// reflecting the file rather than substituting for it.
///
/// **What the model still cannot say**, and is reported: a marker is a *stroke property* in SVG, so an arrowhead
/// belongs to the path and follows it when the path is edited. The model's <see cref="StrokeSpec"/> has no marker
/// member - adding one means editing <c>src/VCCad.Core/Model/**</c>, which is not this change's to make - so the
/// arrowhead is an object of its own beside the path, and the import says so.
///
/// **The geometry is measured in the artboard's frame**, with the enclosing groups' transforms composed in, the way
/// <see cref="SvgReaderClipTests"/> measures a carried clip. An arrowhead that "is there" is not the question: the
/// question is where its tip landed, and which way it points.
/// </summary>
public class SvgMarkerTests
{
    /// <summary>An arrowhead pointing along +x from its own origin, the shape Inkscape's tails are made of.</summary>
    private const string HeadShape = "<path id=\"head\" d=\"M 0 0 L 10 0 L 10 5 Z\"/>";

    /// <summary>SVG user units are CSS pixels and the model is points, so one user unit is 0.75 pt.</summary>
    private const double UnitsToPoints = 0.75;

    /// <summary>A marker with the reference point at its own origin and overflow visible, so only the placement is in play.</summary>
    private static string Marker(string id, string units, string orient = "auto", string attributes = "")
        => $"<marker id=\"{id}\" markerUnits=\"{units}\" orient=\"{orient}\" refX=\"0\" refY=\"0\" " +
           $"style=\"overflow:visible\" {attributes}>{HeadShape}</marker>";

    /// <summary>A two-marker document: the same arrowhead under each `markerUnits`, so the pair is the only difference.</summary>
    private static string Page(string body)
        => "<svg width=\"200\" height=\"200\"><defs>" +
           Marker("stroke", "strokeWidth") +
           Marker("user", "userSpaceOnUse") +
           "</defs>" + body + "</svg>";

    /// <summary>The tail of the arrowhead - the tile's own (10,0) - as the artboard sees it, in points.</summary>
    private static Point2D TipOf(SvgImportResult result, string markerId, int index = 0)
    {
        ArtGroup group = result.Document.AllGroups().Where(g => g.Name == markerId).ElementAt(index);
        return InArtboard(group, new Point2D(10, 0));
    }

    /// <summary>The reference point of the marker - its own origin - as the artboard sees it, in points.</summary>
    private static Point2D RefPointOf(SvgImportResult result, string markerId, int index = 0)
    {
        ArtGroup group = result.Document.AllGroups().Where(g => g.Name == markerId).ElementAt(index);
        return InArtboard(group, new Point2D(0, 0));
    }

    /// <summary>A point in the marker's own coordinates, carried into the artboard by the marker's placement and its ancestors'.</summary>
    private static Point2D InArtboard(ArtGroup marker, Point2D content)
        => Ancestors(marker).Transform(marker.Transform.Transform(content));

    /// <summary>The enclosing groups' transforms, composed outermost last: an item's frame in the artboard.</summary>
    private static AffineTransform Ancestors(LayerItem item)
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

        return transform;
    }

    /// <summary>The unit vector from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static (double X, double Y) Direction(Point2D from, Point2D to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return (dx / length, dy / length);
    }

    private static readonly Point2D UnitX = new(1, 0);
    private static readonly Point2D UnitY = new(0, 1);

    // ---------------------------------------------------------------- orient="auto" follows the path

    /// <summary>
    /// **`orient="auto"` aims the arrowhead along the path's direction of travel at that end.**
    ///
    /// The same arrowhead on the same page is placed on a path that runs left to right and on one that runs right
    /// to left. Its *own* geometry is identical - `M 0 0 L 10 0` points along +x in the marker's coordinates - so
    /// the only thing that can make the two tips point opposite ways is the rotation the reader derived from each
    /// path's tangent. A reader that used a fixed angle, or that used the path's `d` string as written rather than
    /// the direction at the end, lands both arrowheads on the same side.
    ///
    /// **The numbers.** The page is 200 user units wide, so the artboard is 150 pt and one user unit is 0.75 pt.
    /// The forward path ends at (90,10) user units = (67.5, 7.5) pt, and `markerUnits="strokeWidth"` with a stroke
    /// width of 4 scales the tile by 4, so the tip - the tile's own (10,0) - is 40 user units = 30 pt further along
    /// the direction of travel: (97.5, 7.5) pt. The reverse path ends at (10,10) = (7.5, 7.5) pt and its tangent is
    /// (-1,0), so its tip is at (-22.5, 7.5) pt.
    ///
    /// **Before:** no marker was read at all - `AllGroups()` held no group named "stroke" and the document had one
    /// path instead of two, so this test failed on its first lookup.
    /// </summary>
    [Fact]
    public void AnAutoMarkerEndAimsAlongThePathsDirectionOfTravel()
    {
        SvgImportResult forward = SvgReader.Read(Page(
            "<path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-end=\"url(#stroke)\"/>"));

        Point2D forwardEnd = RefPointOf(forward, "stroke");
        Point2D forwardTip = TipOf(forward, "stroke");

        Assert.Equal(67.5, forwardEnd.X, 6);
        Assert.Equal(7.5, forwardEnd.Y, 6);
        Assert.Equal(97.5, forwardTip.X, 6);
        Assert.Equal(7.5, forwardTip.Y, 6);

        (double X, double Y) heading = Direction(forwardEnd, forwardTip);
        Assert.Equal(1.0, heading.X, 9);
        Assert.Equal(0.0, heading.Y, 9);

        // The same marker on a path drawn the other way: the direction of travel is what decides, not the geometry.
        SvgImportResult reverse = SvgReader.Read(Page(
            "<path d=\"M 90 10 L 10 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-end=\"url(#stroke)\"/>"));

        Point2D reverseEnd = RefPointOf(reverse, "stroke");
        Point2D reverseTip = TipOf(reverse, "stroke");

        Assert.Equal(7.5, reverseEnd.X, 6);
        Assert.Equal(7.5, reverseEnd.Y, 6);
        Assert.Equal(-22.5, reverseTip.X, 6);
        Assert.Equal(7.5, reverseTip.Y, 6);

        (double X, double Y) flipped = Direction(reverseEnd, reverseTip);
        Assert.Equal(-1.0, flipped.X, 9);
        Assert.Equal(0.0, flipped.Y, 9);
    }

    /// <summary>
    /// **`marker-start` uses the tangent leaving the vertex, and `marker-mid` the bisector of the two tangents.**
    ///
    /// A start arrowhead sits on the first vertex and a mid arrowhead on every interior one, and the orientation
    /// each gets is the specification's: the outgoing tangent at the start, and the bisector where a segment comes
    /// in and another goes out. The corner is a right angle - in along +x, out along +y - so the bisector is the
    /// 45-degree diagonal, and a reader that averaged the *positions* or used only one of the two tangents lands on
    /// an axis instead.
    ///
    /// **The numbers.** With a stroke width of 4 the tile is scaled by 4, so a tip at the tile's own (10,0) is 40
    /// user units = 30 pt from the vertex along the heading. The start vertex is (10,10) = (7.5, 7.5) pt; the
    /// corner is (50,10) = (37.5, 7.5) pt, and 30 pt along the 45-degree diagonal is about (21.21, 21.21) pt from
    /// it.
    /// </summary>
    [Fact]
    public void AStartAndAMidMarkerUseTheTangentsAtTheirVertices()
    {
        SvgImportResult start = SvgReader.Read(Page(
            "<path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-start=\"url(#stroke)\"/>"));

        Point2D startVertex = RefPointOf(start, "stroke");
        Point2D startTip = TipOf(start, "stroke");
        Assert.Equal(7.5, startVertex.X, 6);
        Assert.Equal(7.5, startVertex.Y, 6);
        Assert.Equal(37.5, startTip.X, 6);
        Assert.Equal(7.5, startTip.Y, 6);

        SvgImportResult mid = SvgReader.Read(Page(
            "<path d=\"M 10 10 L 50 10 L 50 50\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-mid=\"url(#stroke)\"/>"));

        Point2D corner = RefPointOf(mid, "stroke");
        Point2D tip = TipOf(mid, "stroke");
        Assert.Equal(37.5, corner.X, 6);
        Assert.Equal(7.5, corner.Y, 6);

        (double X, double Y) heading = Direction(corner, tip);
        Assert.Equal(Math.Sqrt(0.5), heading.X, 6);
        Assert.Equal(Math.Sqrt(0.5), heading.Y, 6);

        double distance = Math.Sqrt(
            ((tip.X - corner.X) * (tip.X - corner.X)) + ((tip.Y - corner.Y) * (tip.Y - corner.Y)));
        Assert.Equal(30.0, distance, 6);
    }

    // ---------------------------------------------------------------- markerUnits decides the scale

    /// <summary>
    /// **`markerUnits="strokeWidth"` scales the tile by the stroke's width; `userSpaceOnUse` does not.**
    ///
    /// The same tile under both values on the same path, and then the same pair again on a path twice as thick.
    /// Under `strokeWidth` the tip moves exactly twice as far when the stroke doubles; under `userSpaceOnUse` it
    /// does not move at all. One assertion alone would pass for a reader that ignored `markerUnits` and used the
    /// stroke width always, or never - it is the pair that pins it.
    ///
    /// **The numbers.** The forward path ends at (67.5, 7.5) pt. With a stroke width of 4, `strokeWidth` puts the
    /// tile's (10,0) at 40 user units = 30 pt out - (97.5, 7.5) pt - and `userSpaceOnUse` at 10 user units = 7.5 pt
    /// out, (75, 7.5) pt. Doubling the stroke to 8 doubles the first to 60 pt and leaves the second alone.
    /// </summary>
    [Fact]
    public void StrokeWidthScalesTheMarkerAndUserSpaceOnUseDoesNot()
    {
        Point2D Thick(string units, double width)
        {
            SvgImportResult result = SvgReader.Read(Page(
                $"<path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"{width}\" " +
                $"marker-end=\"url(#{units})\"/>"));
            return TipOf(result, units);
        }

        Assert.Equal(30.0, Thick("stroke", 4).X - 67.5, 6);
        Assert.Equal(60.0, Thick("stroke", 8).X - 67.5, 6);

        Assert.Equal(7.5, Thick("user", 4).X - 67.5, 6);
        Assert.Equal(7.5, Thick("user", 8).X - 67.5, 6);

        // And the unscaled case really is the tile's own numbers, not a scale of one stroke width.
        Assert.Equal(75.0, Thick("user", 4).X, 6);
    }

    /// <summary>
    /// **A fixed `orient` angle is absolute, so it does not follow the path.**
    ///
    /// `orient="45"` states an orientation in the referencing element's own user space, not a turn relative to the
    /// path. Two paths running in opposite directions therefore get arrowheads pointing the same way - which is
    /// exactly what tells this apart from `auto`, and what a reader that always added the tangent would get wrong.
    /// </summary>
    [Fact]
    public void AFixedOrientAngleDoesNotFollowThePath()
    {
        string Page45 = "<svg width=\"200\" height=\"200\"><defs>" +
            Marker("fixed", "userSpaceOnUse", orient: "45") +
            "</defs><path d=\"{0}\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" marker-end=\"url(#fixed)\"/></svg>";

        SvgImportResult forward = SvgReader.Read(Page45.Replace("{0}", "M 10 10 L 90 10"));
        SvgImportResult reverse = SvgReader.Read(Page45.Replace("{0}", "M 90 10 L 10 10"));

        foreach (SvgImportResult result in new[] { forward, reverse })
        {
            (double X, double Y) heading = Direction(RefPointOf(result, "fixed"), TipOf(result, "fixed"));
            Assert.Equal(Math.Sqrt(0.5), heading.X, 6);
            Assert.Equal(Math.Sqrt(0.5), heading.Y, 6);
        }
    }

    /// <summary>
    /// **`auto-start-reverse` turns a start arrowhead around, so a tail and a head can share one marker.**
    ///
    /// SVG 2's value exists so that one marker can be both: `auto` anywhere except a start vertex, where it points
    /// back along the outgoing tangent. A reader that treated it as `auto` puts the tail on the wrong side of the
    /// vertex, which is visible and - worse - plausible.
    /// </summary>
    [Fact]
    public void AutoStartReverseTurnsAStartMarkerAround()
    {
        string svg =
            "<svg width=\"200\" height=\"200\"><defs>" +
            Marker("rev", "userSpaceOnUse", orient: "auto-start-reverse") +
            "</defs><path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-start=\"url(#rev)\"/></svg>";

        SvgImportResult result = SvgReader.Read(svg);
        (double X, double Y) heading = Direction(RefPointOf(result, "rev"), TipOf(result, "rev"));
        Assert.Equal(-1.0, heading.X, 6);
        Assert.Equal(0.0, heading.Y, 6);
    }

    /// <summary>
    /// **A marker's `viewBox` scales its content, and `refX`/`refY` are coordinates in that content.**
    ///
    /// The specification's own worked example scales the reference point by the view-box-to-viewport factor before
    /// subtracting it, which is what makes `refX`/`refY` coordinates **in the view box** rather than in the
    /// viewport. Here the view box is a 10-unit square and the viewport is a 2-unit square, so the factor is 0.2:
    /// the tile's `(refX,refY) = (0,5)` must land on the vertex, and its 10-unit-wide head must come out 2 units -
    /// 1.5 pt - wide. A reader that ignored the viewBox would put both at ten times that size, and one that read
    /// `refY` as a viewport coordinate would put the reference point five units off the vertex.
    ///
    /// **The square is deliberate.** With `markerWidth`/`markerHeight` in the same proportion as the view box the
    /// `xMidYMid meet` leftover is zero, so this pins the scale and the reference point without depending on where
    /// the alignment offset lands - a question the specification's own example answers two ways.
    /// </summary>
    [Fact]
    public void AMarkerViewBoxAndReferencePointAreHonoured()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"2000\" height=\"2000\"><defs>" +
            "<marker id=\"tri\" viewBox=\"0 0 10 10\" refX=\"0\" refY=\"5\" markerUnits=\"userSpaceOnUse\" " +
            "markerWidth=\"2\" markerHeight=\"2\" orient=\"0\" style=\"overflow:visible\">" +
            "<path id=\"tri-head\" d=\"M 0 0 L 10 5 L 0 10 Z\"/></marker></defs>" +
            "<path d=\"M 100 100 L 900 100\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-end=\"url(#tri)\"/></svg>");

        ArtGroup group = result.Document.AllGroups().Single(g => g.Name == "tri");

        // The path ends at (900,100) user units, and the page is 2000 units over 1500 pt, so the vertex is
        // 900 * 0.75 = 675 pt across and 75 pt down.
        Point2D vertex = InArtboard(group, new Point2D(0, 5));
        Assert.Equal(675.0, vertex.X, 6);
        Assert.Equal(75.0, vertex.Y, 6);

        // The tile's own (10,5) is the tip of its 10-unit-wide head, so under the 0.2 factor it is two marker units
        // to the right of the reference point - 1.5 pt in the artboard.
        Point2D head = InArtboard(group, new Point2D(10, 5));
        Assert.Equal(676.5, head.X, 6);
        Assert.Equal(75.0, head.Y, 6);
    }

    // ---------------------------------------------------------------- the model gap is said out loud

    /// <summary>
    /// **The import says what it did and what is still missing.**
    ///
    /// This test used to assert that the model had *no marker member* and that the arrowhead was therefore only art.
    /// That stopped being true in #202: the reference is recorded on the path now (`PathItem.MarkerStart`/`Mid`/`End`,
    /// with the definition in the library), so the honest report is the half that remains - the content is **also**
    /// placed as art beside the path, because the canvas and the PDF do not draw a marker from its definition yet.
    /// The assertion is turned over to the new fact rather than deleted, because what it guards is that the
    /// difference is *said* either way.
    /// </summary>
    [Fact]
    public void PlacingAMarkerAsArtIsReported()
    {
        SvgImportResult result = SvgReader.Read(Page(
            "<path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            "marker-end=\"url(#stroke)\"/>"));

        Assert.Contains(result.Warnings, w =>
            w.Contains("url(#stroke)", StringComparison.Ordinal) &&
            w.Contains("reference is recorded on the path", StringComparison.Ordinal) &&
            w.Contains("placed as art at the vertex", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A marker reference this reader cannot honour is reported rather than dropped in silence.**
    ///
    /// Three different failures, one rule: a dangling id, an id that names something other than a `marker`, and a
    /// `markerUnits` or `orient` value that is not one of the ones the specification defines. Each of them ends
    /// with no arrowhead, and an arrowhead that quietly went missing is artwork that went missing.
    /// </summary>
    [Theory]
    [InlineData("marker-end=\"url(#missing)\"", "missing")]
    [InlineData("marker-end=\"url(#grad)\"", "grad")]
    [InlineData("marker-end=\"url(#bad)\"", "banana")]
    public void AMarkerValueThatCannotBeHonouredIsReported(string attribute, string named)
    {
        string svg =
            "<svg width=\"200\" height=\"200\"><defs>" +
            "<linearGradient id=\"grad\"><stop offset=\"0\" stop-color=\"#000\"/></linearGradient>" +
            (named == "banana"
                ? Marker("bad", "banana", orient: "banana")
                : Marker("ok", "userSpaceOnUse")) +
            "</defs><path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" " +
            attribute + "/></svg>";

        SvgImportResult result = SvgReader.Read(svg);

        Assert.Contains(result.Warnings, w => w.Contains("marker", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, w => w.Contains(named, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- the quiet cases

    /// <summary>
    /// **`marker-end="none"` is not a gap, and a file that writes it is not warned about.**
    ///
    /// `none` is the property's initial value, so it states the absence of an arrowhead rather than one this reader
    /// failed to read. Inkscape writes `marker:none` in the shorthand on nearly every object in its own corpus, so
    /// reporting it would bury every real report under thousands of lines of noise.
    /// </summary>
    [Theory]
    [InlineData("marker-end=\"none\"")]
    [InlineData("marker-start=\"none\" marker-mid=\"none\" marker-end=\"none\"")]
    [InlineData("style=\"marker:none\"")]
    [InlineData("")]
    public void NoMarkerIsNotAWarningAndChangesNothing(string attribute)
    {
        SvgImportResult result = SvgReader.Read(Page(
            $"<path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" {attribute}/>"));

        Assert.Empty(result.Warnings);
        Assert.Single(result.Document.AllPaths());
        Assert.Empty(result.Document.AllGroups().Where(g => g.Name is "stroke" or "user"));
    }

    /// <summary>
    /// **The `marker` shorthand sets all three vertices, so a two-point path gets both a head and a tail.**
    ///
    /// `marker` is the shorthand for the three properties, and reading only the three longhands loses every file
    /// that uses it - which is how the shorthand is usually written. A straight two-point path has no interior
    /// vertex, so the two placed markers are the start and the end.
    /// </summary>
    [Fact]
    public void TheMarkerShorthandPlacesBothEnds()
    {
        SvgImportResult result = SvgReader.Read(Page(
            "<path d=\"M 10 10 L 90 10\" fill=\"none\" stroke=\"#000\" stroke-width=\"4\" marker=\"url(#stroke)\"/>"));

        List<ArtGroup> placed = result.Document.AllGroups().Where(g => g.Name == "stroke").ToList();
        Assert.Equal(2, placed.Count);
        Assert.Equal(3, result.Document.AllPaths().Count());

        // Both arrowheads point along the direction of travel - that is what `auto` means at either end - so what
        // tells the two apart is where they sit: one on the start vertex at (7.5,7.5) pt, the other on the end at
        // (67.5,7.5) pt.
        double[] vertices = placed
            .Select(g => InArtboard(g, new Point2D(0, 0)).X)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(7.5, vertices[0], 6);
        Assert.Equal(67.5, vertices[1], 6);

        foreach (ArtGroup group in placed)
        {
            Assert.Equal(1.0, Direction(InArtboard(group, new Point2D(0, 0)), InArtboard(group, new Point2D(10, 0))).X, 6);
        }
    }
}
