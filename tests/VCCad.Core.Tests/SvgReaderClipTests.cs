using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A `clip-path` on an element is read, composed, or reported - never ignored.
///
/// `clip-path` is one of the most common ways an SVG crops, and this reader did not read it at all. A crop is a
/// *structural* statement rather than a rendering detail: a file that draws a square and clips it to a circle is
/// not the same picture as a square, and the PDF importer already records the equivalent form `/BBox` as a
/// <see cref="ClipSpec"/>. So an SVG with the same intent imported unclipped, drew the whole square, and said
/// nothing - a plausible wrong answer, which is the failure mode this repository distrusts most.
///
/// **The two frames, which are not the same frame.** A `clipPath` is evaluated in the user space the element's
/// **own** `transform` establishes (SVG 1.1 §14.3), so its outline reaches the model through that transform; a
/// nested viewport's port is stated where the element's `x`/`y`/`width`/`height` are stated, so it does not. Both
/// end up in the item's frame, and both are checked below by composing those transforms explicitly - the same
/// reading <see cref="SvgNestedViewportTests"/> uses for a port.
///
/// **The content stays authored whole.** A clip does not rewrite the geometry it cuts - the same invariant
/// <c>SamplePatternTests</c> pins for PDF text - so the assertions check both halves: the outline is on the model,
/// and the square is still 100 units across underneath it.
/// </summary>
public class SvgReaderClipTests
{
    /// <summary>The fixture from the report: a 100-unit square, clipped to a circle of radius 20 at (50,50).</summary>
    private const string SquareClippedToACircle =
        "<svg width=\"100\" height=\"100\"><defs><clipPath id=\"c\"><circle cx=\"50\" cy=\"50\" r=\"20\"/>" +
        "</clipPath></defs><rect width=\"100\" height=\"100\" clip-path=\"url(#c)\"/></svg>";

    /// <summary>SVG user units are CSS pixels and the model is points, so one user unit is 0.75 pt.</summary>
    private const double UnitsToPoints = 0.75;

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

    /// <summary>The box a clip's outline covers once <paramref name="through"/> has carried it, in points.</summary>
    private static Rect2D Box(ClipSpec clip, AffineTransform through)
    {
        // Built from the corner set rather than by unioning points into Rect2D.Empty, which is (0,0,0,0) and would
        // drag the origin into an outline that does not reach it.
        var points = new List<Point2D>();
        foreach (SubPath sub in clip.SubPaths)
        {
            foreach (PathNode node in sub.Nodes)
            {
                points.Add(through.Transform(node.Anchor));
            }
        }

        return Rect2D.FromPoints(points);
    }

    /// <summary>A clip outline as the artboard sees it, in points.</summary>
    private static Rect2D ClipInPoints(LayerItem item, int index) => Box(item.Clips[index], Ancestors(item));

    /// <summary>A path node as the artboard sees it, in points.</summary>
    private static Point2D NodeInPoints(PathItem path, int node)
        => Ancestors(path).Transform(path.SubPaths[0].Nodes[node].Anchor);

    // ---------------------------------------------------------------- the fixture from the report

    /// <summary>
    /// **The clip outline is on the model, and the square is still authored whole underneath it.**
    ///
    /// The file's page is 100 user units square and the circle is centred at (50,50) with a radius of 20 units, so
    /// the outline the model holds is that circle. A circle is four cubic segments rather than four corners, so the
    /// outline's extent is read as a box - and the extent is what a reader that dropped the clip gets wrong, and
    /// what one that read it in the wrong frame gets wrong too.
    ///
    /// **The geometry is checked in points**, on the artboard, with the file's own unit conversion composed in: the
    /// page's 100 units are 75 pt, so the 40-unit circle is 30 pt across with its centre at (37.5, 37.5) pt and its
    /// extent from 22.5 pt to 52.5 pt. The outline's own numbers are in the user space the element sits in, which
    /// for a root shape is the file's - that is what makes the number 30 rather than 40.
    ///
    /// **Before:** `<c>rect.Clips</c>` was empty - the fixture imported as a plain 100-unit square with no clip
    /// anywhere on the model - so `Assert.Single(rect.Clips)` found none and the whole square was what a viewer
    /// drew.
    /// </summary>
    [Fact]
    public void ASquareClippedToACircleCarriesTheCircleAsAClipInPoints()
    {
        SvgImportResult result = SvgReader.Read(SquareClippedToACircle);

        PathItem rect = Assert.Single(result.Document.AllPaths());
        Assert.Empty(result.Warnings);

        ClipSpec clip = Assert.Single(rect.Clips);
        Assert.Equal(FillRule.NonZero, clip.Rule);

        Rect2D bounds = ClipInPoints(rect, 0);
        Assert.Equal(22.5, bounds.Left, 9);
        Assert.Equal(22.5, bounds.Top, 9);
        Assert.Equal(52.5, bounds.Right, 9);
        Assert.Equal(52.5, bounds.Bottom, 9);

        // And it is a circle, not the square's box: the outline's own numbers are 30..70 in the file's user units,
        // so the file's circle came through rather than a bounding rectangle standing in for it.
        Rect2D raw = Box(clip, AffineTransform.Identity);
        Assert.Equal(30.0, raw.Left, 9);
        Assert.Equal(70.0, raw.Right, 9);
        Assert.Equal(4, clip.SubPaths.Single().Nodes.Count);

        // The content is not rewritten to fit: the square is still the whole 100 units the file drew.
        Assert.Equal(0.0, NodeInPoints(rect, 0).X, 9);
        Assert.Equal(0.0, NodeInPoints(rect, 0).Y, 9);
        Assert.Equal(100.0 * UnitsToPoints, NodeInPoints(rect, 2).X, 9);
        Assert.Equal(100.0 * UnitsToPoints, NodeInPoints(rect, 2).Y, 9);
    }

    /// <summary>
    /// **The clip is the model's own statement, through the accessor the whole application reads.**
    ///
    /// The same <see cref="ClipSpec"/> is what selection, the canvas and the exporter all read, and
    /// <see cref="SelectionEngine.ClipsOn"/> is the accessor they share - so the crop is asserted through it rather
    /// than re-derived, and the outline is checked with the model's own <see cref="ClipSpec.Contains"/>.
    ///
    /// **Before:** `ClipsOn` was empty, because the model held no clip at all.
    /// </summary>
    [Fact]
    public void TheCircleIsTheClipTheRestOfTheApplicationReads()
    {
        SvgImportResult result = SvgReader.Read(SquareClippedToACircle);
        PathItem rect = Assert.Single(result.Document.AllPaths());
        ClipSpec clip = Assert.Single(SelectionEngine.ClipsOn(rect));

        Assert.True(rect.IsClipped);

        // Read in the frame the model records: the file's user units for a root shape.
        Assert.True(clip.Contains(new Point2D(50, 50)), "the circle's centre is inside the clip");
        Assert.False(clip.Contains(new Point2D(8, 8)), "the square's corner is outside the clip");
        Assert.True(clip.Contains(new Point2D(68, 50)), "18 units from the centre is inside");
        Assert.False(clip.Contains(new Point2D(72, 50)), "22 units from the centre is outside");
    }

    /// <summary>
    /// **An element's `clip-path` is read in the frame its own `transform` puts it in.**
    ///
    /// SVG applies the property in the user space the element's own `transform` establishes, so a clip written at
    /// the origin on an element translated by (20,30) is a clip **at (20,30)** once the file's own transform has
    /// carried it. Reading the numbers as the model's frame instead clips in the wrong place, which looks
    /// deliberate and is the reason this is asserted separately rather than folded into the fixture above.
    ///
    /// The shape's own transform is baked into its points, so both halves are checked against the same frame: the
    /// square's corner and the clip's corner are both at (20,30) in the file's space, which is 15 pt and 22.5 pt.
    ///
    /// **Before:** no clip at all, so there was no frame to be right or wrong about.
    /// </summary>
    [Fact]
    public void AClipIsReadInTheFrameTheElementsOwnTransformEstablishes()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><clipPath id=\"c\"><rect width=\"10\" height=\"10\"/></clipPath></defs>" +
            "<rect width=\"100\" height=\"100\" transform=\"translate(20 30)\" clip-path=\"url(#c)\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        Assert.Empty(result.Warnings);

        // The element's own transform is baked into its points, so the square sits at (20,30) in the file's space.
        Assert.Equal(20.0, rect.SubPaths[0].Nodes[0].Anchor.X, 9);
        Assert.Equal(30.0, rect.SubPaths[0].Nodes[0].Anchor.Y, 9);

        // The clip was carried by that same transform: 10 units at (20,30), which is (20,30)-(30,40) in the file's
        // space and (15,22.5)-(22.5,30) in points.
        ClipSpec clip = Assert.Single(rect.Clips);
        Rect2D raw = Box(clip, AffineTransform.Identity);
        Assert.Equal(20.0, raw.Left, 9);
        Assert.Equal(30.0, raw.Top, 9);
        Assert.Equal(30.0, raw.Right, 9);
        Assert.Equal(40.0, raw.Bottom, 9);

        Rect2D points = ClipInPoints(rect, 0);
        Assert.Equal(15.0, points.Left, 9);
        Assert.Equal(22.5, points.Top, 9);
        Assert.Equal(22.5, points.Right, 9);
        Assert.Equal(30.0, points.Bottom, 9);

        // Read in the model's frame instead it would have landed at the origin - a different, plausible crop.
        Assert.NotEqual(0.0, raw.Left, 6);
    }

    // ---------------------------------------------------------------- composed, not replaced

    /// <summary>
    /// **A clip on a nested `svg` composes with its port, and neither replaces the other.**
    ///
    /// A nested `svg` is a viewport, and #152 records its port as a clip on the element's own group. A `clip-path`
    /// on that same element is a second outline, and the model's meaning for two clips on the way down is their
    /// intersection - so both belong on the group, and a reader that kept only one would show content the file
    /// hides while the other half appeared to work.
    ///
    /// **The port is not carried by the element's own transform; the `clip-path` is.** The port is stated where the
    /// element's `x`/`y`/`width`/`height` are stated, so a 50-unit port at (50,50) is a 50-unit rectangle at
    /// (50,50) - from 37.5 pt to 75 pt. The `clipPath` is evaluated in the space the element's own `transform`
    /// establishes, so the circle at (50,50) is carried to (100,100) by it - from 75 pt to 105 pt in points. The two
    /// therefore overlap only where the file says they do, which is what makes each one load-bearing.
    ///
    /// **Before:** the port clip existed (#152) and the `clip-path` did not, so the model held one clip where the
    /// file states two.
    /// </summary>
    [Fact]
    public void AClipOnANestedViewportComposesWithItsPortAndDoesNotReplaceIt()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"200\" height=\"200\">" +
            "<defs><clipPath id=\"c\"><circle cx=\"50\" cy=\"50\" r=\"20\"/></clipPath></defs>" +
            "<svg x=\"50\" y=\"50\" width=\"50\" height=\"50\" clip-path=\"url(#c)\">" +
            "<rect width=\"400\" height=\"400\"/></svg></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        ArtGroup viewport = Assert.IsType<ArtGroup>(rect.Container);

        Assert.Empty(result.Warnings);

        // Both outlines are on the viewport's own group, in the file's space, and both are the file's own numbers.
        Assert.Equal(2, viewport.Clips.Count);
        Assert.Equal(2, SelectionEngine.ClipsOn(rect).Count);

        Rect2D portRaw = Box(viewport.Clips[0], AffineTransform.Identity);
        Assert.Equal(50.0, portRaw.Left, 9);
        Assert.Equal(50.0, portRaw.Top, 9);
        Assert.Equal(100.0, portRaw.Right, 9);
        Assert.Equal(100.0, portRaw.Bottom, 9);

        Rect2D circleRaw = Box(viewport.Clips[1], AffineTransform.Identity);
        Assert.Equal(30.0, circleRaw.Left, 9);
        Assert.Equal(30.0, circleRaw.Top, 9);
        Assert.Equal(70.0, circleRaw.Right, 9);
        Assert.Equal(70.0, circleRaw.Bottom, 9);

        // In points, on the artboard: the port is 37.5..75 and the circle is 22.5..52.5. The file wrote the circle
        // at (50,50), so once the page's own user-unit factor of 0.75 is applied its centre is at 37.5 pt.
        Rect2D port = ClipInPoints(viewport, 0);
        Assert.Equal(37.5, port.Left, 9);
        Assert.Equal(37.5, port.Top, 9);
        Assert.Equal(75.0, port.Right, 9);
        Assert.Equal(75.0, port.Bottom, 9);

        Rect2D circle = ClipInPoints(viewport, 1);
        Assert.Equal(22.5, circle.Left, 9);
        Assert.Equal(22.5, circle.Top, 9);
        Assert.Equal(52.5, circle.Right, 9);
        Assert.Equal(52.5, circle.Bottom, 9);

        // **Both outlines are load-bearing.** The port runs 50..100 and the circle 30..70 in the file's space, so
        // their overlap is 50..70: a point in it survives, a point inside the port but past the circle is cut, and a
        // point inside the circle but above the port is cut too.
        Assert.True(SelectionEngine.ClipsOn(rect).All(c => c.Contains(new Point2D(60, 60))),
            "the overlap of the port and the circle is what survives");
        Assert.False(SelectionEngine.ClipsOn(rect).All(c => c.Contains(new Point2D(90, 90))),
            "a corner of the port is outside the carried circle");
        Assert.False(SelectionEngine.ClipsOn(rect).All(c => c.Contains(new Point2D(40, 40))),
            "above the port is outside it, whatever the circle says");

        // Neither outline contains the other, so a reader that kept only one shows a different picture.
        Assert.True(circle.Left < port.Left && circle.Top < port.Top, "the circle reaches outside the port");
        Assert.True(port.Right > circle.Right && port.Bottom > circle.Bottom, "the port reaches past the circle");
    }

    /// <summary>
    /// **A `clip-path` on a group clips everything the group holds.**
    ///
    /// A group is the other thing a `clip-path` is commonly written on, and the model's answer is the same: the
    /// outline goes on the group, so every child is inside it. A reader that only looked at shapes would draw a
    /// cropped group whole.
    ///
    /// **Before:** no clip anywhere, and the group's child was drawn across the whole page.
    /// </summary>
    [Fact]
    public void AClipOnAGroupAppliesToWhatItHolds()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><clipPath id=\"c\"><rect width=\"10\" height=\"10\"/></clipPath></defs>" +
            "<g clip-path=\"url(#c)\"><rect width=\"100\" height=\"100\"/></g></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        ArtGroup group = Assert.IsType<ArtGroup>(rect.Container);

        Assert.Empty(result.Warnings);
        ClipSpec clip = Assert.Single(group.Clips);
        Assert.Single(SelectionEngine.ClipsOn(rect));

        // The outline is at the group's own placement - the origin, because the file put no transform on it - and
        // it is 10 units of the file's space, which is 7.5 pt.
        Rect2D bounds = ClipInPoints(group, 0);
        Assert.Equal(0.0, bounds.Left, 9);
        Assert.Equal(0.0, bounds.Top, 9);
        Assert.Equal(7.5, bounds.Right, 9);
        Assert.Equal(7.5, bounds.Bottom, 9);

        // The child is inside the group's crop where the crop is, and outside it everywhere else.
        Assert.True(clip.Contains(new Point2D(5, 5)), "the child under the crop is inside it");
        Assert.False(clip.Contains(new Point2D(50, 50)), "the rest of the child is cut away");

        // And the child is still authored whole: the clip cut nothing out of the file's geometry.
        Assert.Equal(100.0, rect.SubPaths[0].Nodes[2].Anchor.X, 9);
    }

    // ---------------------------------------------------------------- what cannot be honoured is reported

    /// <summary>
    /// **`clipPathUnits="objectBoundingBox"` is a different coordinate system, and it is reported rather than
    /// read as user space.**
    ///
    /// The same numbers mean different geometry: the outline's coordinates are fractions of the element's bounding
    /// box here, so a `rect` of `0 0 1 1` is the whole element rather than a one-unit square at its origin. Reading
    /// them as user space would clip to a shape nobody wrote, which is a plausible wrong answer rather than a
    /// visibly missing one - so the import says which clip it could not honour and leaves the content whole,
    /// exactly as an unreadable nested viewport does.
    ///
    /// **Before:** the clip was ignored in silence, and this test's whole assertion set failed on the missing
    /// report.
    /// </summary>
    [Fact]
    public void AnObjectBoundingBoxClipIsReportedRatherThanReadAsUserSpace()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><clipPath id=\"c\" clipPathUnits=\"objectBoundingBox\">" +
            "<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\"/></clipPath></defs>" +
            "<rect width=\"100\" height=\"100\" clip-path=\"url(#c)\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());

        Assert.Contains(result.Warnings, w =>
            w.Contains("clip-path", StringComparison.Ordinal) &&
            w.Contains("objectBoundingBox", StringComparison.Ordinal));

        // Not treated as user space: no clip at all, rather than a one-unit square at the origin.
        Assert.Empty(rect.Clips);
        Assert.False(rect.IsClipped);

        // And the content is drawn whole, because drawing a crop the file did not write is worse than reporting.
        Assert.Equal(75.0, NodeInPoints(rect, 2).X, 9);
    }

    /// <summary>
    /// **A `clip-path` that is not a reference to a `clipPath` is reported rather than ignored.**
    ///
    /// SVG 2 allows a CSS shape (`circle()`, `inset()`, `path()`) and a URL to another document, and this reader
    /// implements neither. The point of the test is the *report*: the defect this closes is a crop that vanishes
    /// in silence, so every value the reader cannot honour has to say so.
    ///
    /// **Before:** all three were ignored with no warning, and the square drew whole and said nothing - which is
    /// the report's own complaint.
    /// </summary>
    [Theory]
    [InlineData("circle(50% at 50% 50%)")]
    [InlineData("path('M 0 0 L 10 0 L 10 10 Z')")]
    [InlineData("url(other.svg#c)")]
    public void AnUnsupportedClipPathValueIsReported(string value)
    {
        SvgImportResult result = SvgReader.Read(
            $"<svg width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" clip-path=\"{value}\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());

        Assert.Contains(result.Warnings, w =>
            w.Contains("clip-path", StringComparison.Ordinal) && w.Contains(value, StringComparison.Ordinal));

        Assert.Empty(rect.Clips);
        Assert.Equal(75.0, NodeInPoints(rect, 2).X, 9);
    }

    /// <summary>
    /// **A reference to an id the document does not define as a `clipPath` is reported.**
    ///
    /// The id may name something else entirely - a gradient, an element, nothing - and none of them is an outline
    /// to clip with. Silence here is the same defect one level in: the file states a crop by reference and the
    /// import produced none.
    ///
    /// **Before:** no warning and no clip.
    /// </summary>
    [Fact]
    public void AClipPathReferenceToSomethingThatIsNotAClipPathIsReported()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#000\"/></linearGradient></defs>" +
            "<rect width=\"100\" height=\"100\" clip-path=\"url(#g)\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        Assert.Empty(rect.Clips);
        Assert.Contains(result.Warnings, w =>
            w.Contains("clip-path", StringComparison.Ordinal) && w.Contains("#g", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A reference to a `clipPath` the document does not have is reported.**
    ///
    /// A dangling id is the file's own statement of a crop that cannot be found, and it is the one case where
    /// saying which id is missing is the whole of what a person needs.
    ///
    /// **Before:** no warning and no clip.
    /// </summary>
    [Fact]
    public void ADanglingClipPathReferenceIsReported()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" clip-path=\"url(#missing)\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        Assert.Empty(rect.Clips);
        Assert.Contains(result.Warnings, w =>
            w.Contains("clip-path", StringComparison.Ordinal) && w.Contains("missing", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A `clipPath` this reader cannot read an outline out of is reported, not silently empty.**
    ///
    /// `use` inside a `clipPath` is a common way to reuse an outline, and it is the one child this reader does not
    /// turn into geometry. Reading nothing from it would leave a crop that is present on the model and cuts
    /// everything away - the worst of both answers - so the element is reported by name and the clip is left off
    /// the item entirely, with the content drawn whole.
    ///
    /// **Before:** nothing was read and nothing was said.
    /// </summary>
    [Fact]
    public void AClipPathWhoseOutlineCannotBeReadIsReported()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><rect id=\"r\" width=\"10\" height=\"10\"/>" +
            "<clipPath id=\"c\"><use href=\"#r\"/></clipPath></defs>" +
            "<rect width=\"100\" height=\"100\" clip-path=\"url(#c)\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());

        Assert.Contains(result.Warnings, w =>
            w.Contains("clip-path", StringComparison.Ordinal) &&
            w.Contains("url(#c)", StringComparison.Ordinal) &&
            w.Contains("use", StringComparison.Ordinal));

        // No clip, rather than an empty one that would hide everything.
        Assert.Empty(rect.Clips);
        Assert.False(rect.IsClipped);
    }

    // ---------------------------------------------------------------- the quiet cases

    /// <summary>
    /// **`clip-path="none"` is not a gap, and a file that writes it is not warned about.**
    ///
    /// `none` is the property's initial value, so it states the absence of a crop rather than one this reader
    /// failed to read. Reporting it would make every file that spells out its default look broken, which is the
    /// noise that makes a real report easy to miss.
    /// </summary>
    [Fact]
    public void AClipPathOfNoneIsNotAClipAndNotAWarning()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\"><rect width=\"100\" height=\"100\" clip-path=\"none\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        Assert.Empty(rect.Clips);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **A document that writes no `clip-path` imports exactly as it did before this was read.**
    ///
    /// The whole change has to be invisible to the files it is not about: no clip, no warning, and the same
    /// geometry. This is the guard against a reader that starts seeing clips in attributes nobody wrote.
    /// </summary>
    [Fact]
    public void ADocumentWithNoClipPathImportsUnchanged()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\"><g transform=\"translate(10 10)\">" +
            "<rect width=\"20\" height=\"20\"/></g></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        ArtGroup group = Assert.IsType<ArtGroup>(rect.Container);

        Assert.Empty(result.Warnings);
        Assert.Empty(group.Clips);
        Assert.Empty(rect.Clips);
        Assert.False(rect.IsClipped);

        Assert.Equal(7.5, NodeInPoints(rect, 0).X, 9);
        Assert.Equal(7.5, NodeInPoints(rect, 0).Y, 9);
        Assert.Equal(22.5, NodeInPoints(rect, 2).X, 9);
        Assert.Equal(22.5, NodeInPoints(rect, 2).Y, 9);
    }

    /// <summary>
    /// **A `clip-rule="evenodd"` on the outline is carried, because it decides which side is inside.**
    ///
    /// A clip path is filled by the same rule as any other path, so a ring outline clips to a ring under `evenodd`
    /// and to a solid disc under the default `nonzero`. Losing the rule is a crop of the wrong shape with identical
    /// geometry, which is invisible in every assertion about coordinates - which is why this checks a point in the
    /// gap rather than the outline's numbers.
    ///
    /// **Before:** there was no clip to carry a rule on.
    /// </summary>
    [Fact]
    public void AClipRuleOnTheOutlineIsCarried()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><clipPath id=\"c\" clip-rule=\"evenodd\"><rect width=\"50\" height=\"50\"/>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\"/></clipPath></defs>" +
            "<rect width=\"100\" height=\"100\" clip-path=\"url(#c)\"/></svg>");

        PathItem rect = Assert.Single(result.Document.AllPaths());
        ClipSpec clip = Assert.Single(rect.Clips);

        Assert.Equal(FillRule.EvenOdd, clip.Rule);
        Assert.Equal(2, clip.SubPaths.Count);

        // The two rectangles are written the same way round, so the inner one is a hole under even-odd and part of
        // a solid region under non-zero - which is exactly why the rule has to survive the read.
        Assert.False(clip.Contains(new Point2D(20, 20)), "the inner rectangle is a hole under even-odd");
        Assert.True(clip.Contains(new Point2D(40, 40)), "the ring between the two rectangles is inside");
        Assert.True(clip.Contains(new Point2D(5, 5)), "the outer rectangle's own edge is inside");

        // And with no rule stated the model keeps the default, so this cannot pass by carrying `evenodd` always.
        SvgImportResult plain = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><clipPath id=\"c\"><rect width=\"50\" height=\"50\"/>" +
            "<rect x=\"10\" y=\"10\" width=\"20\" height=\"20\"/></clipPath></defs>" +
            "<rect width=\"100\" height=\"100\" clip-path=\"url(#c)\"/></svg>");
        ClipSpec plainClip = Assert.Single(Assert.Single(plain.Document.AllPaths()).Clips);
        Assert.Equal(FillRule.NonZero, plainClip.Rule);
        Assert.True(plainClip.Contains(new Point2D(20, 20)), "under non-zero the inner rectangle is filled");
    }
}
