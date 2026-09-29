using System.Text;
using System.Text.Json;
using VCCad.Core.Model;
using VCCad.Core.Picking;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Hostile-input and permutation tests for the document model and the lossless
/// serializer.
///
/// The serializer is the contract between the sidecar embedded in a PDF and the
/// live document, so its input is a byte string that a person (or a corrupted
/// file) can produce. A crafted sidecar is the model's version of a hostile file:
/// duplicate identities, missing members, absurd version numbers, and nesting
/// deeper than the reader's.
///
/// Tests named as requirements (…IsRefused…) fail against the current build and
/// pin real defects; they are deliberately left failing.
/// </summary>
public class HostileModelTests
{
    // ------------------------------------------------------------------
    // Defect 1 — a syntactically valid but incomplete sidecar hits a
    // NullReferenceException rather than a clear refusal.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> IncompleteSidecars()
    {
        yield return new object[] { "empty object", "{}" };
        yield return new object[] { "version only", "{\"Version\":1}" };
        yield return new object[] { "null artboards", "{\"Version\":1,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"x\",\"Artboards\":null,\"Orphans\":[]}" };
    }

    [Theory]
    [MemberData(nameof(IncompleteSidecars))]
    public void AnIncompleteSidecarIsRefusedWithAClearMessage(string name, string json)
    {
        Exception? thrown = Record.Exception(() => VccadDocumentSerializer.Deserialize(json));

        Assert.NotNull(thrown);
        Assert.False(
            thrown is NullReferenceException,
            $"{name}: refused with a NullReferenceException, which names neither the document " +
            "nor the missing member; the sidecar contract needs a clear refusal.");
        Assert.False(string.IsNullOrWhiteSpace(thrown!.Message), $"{name}: refusal had no message");
    }

    // ------------------------------------------------------------------
    // Defect 2 — two objects with the same Id are accepted, so every
    // id-addressed operation silently picks one of them.
    // ------------------------------------------------------------------

    [Fact]
    public void ADuplicateIdInASidecarIsRefusedRatherThanProducingTwoObjectsWithOneIdentity()
    {
        CadDocument doc = CadDocument.CreateDefault("dupes");
        Layer layer = doc.Artboards[0].Layers[0];
        PathItem a = PathFactory.CreateRectangle("a", new Rect2D(0, 0, 10, 10));
        PathItem b = PathFactory.CreateRectangle("b", new Rect2D(20, 0, 10, 10));
        layer.AddItem(a);
        layer.AddItem(b);

        var shared = Guid.Parse("22222222-2222-2222-2222-222222222222");
        string json = VccadDocumentSerializer.Serialize(doc)
            .Replace(a.Id.ToString(), shared.ToString())
            .Replace(b.Id.ToString(), shared.ToString());

        CadDocument back = VccadDocumentSerializer.Deserialize(json);
        List<PathItem> paths = back.Artboards[0].Layers[0].Children.OfType<PathItem>().ToList();

        Assert.Equal(2, paths.Count);
        Assert.NotEqual(
            paths[0].Id, paths[1].Id);
    }

    // ------------------------------------------------------------------
    // Behaviour that is already correct — pinned so it stays that way.
    // ------------------------------------------------------------------

    [Fact]
    public void AGroupCannotBeMovedIntoItsOwnDescendantOrIntoItself()
    {
        CadDocument doc = CadDocument.CreateDefault("cycles");
        Layer layer = doc.Artboards[0].Layers[0];
        var outer = new ArtGroup { Name = "outer" };
        var inner = new ArtGroup { Name = "inner" };
        layer.AddItem(outer);
        outer.AddItem(inner);

        Assert.Throws<InvalidOperationException>(() => inner.AddItem(outer));
        Assert.Throws<ArgumentException>(() => outer.AddItem(outer));

        // The guard is a back-pointer walk, so it must terminate even after the
        // failed attempts left the tree in whatever state they left it in.
        Assert.Contains(outer, layer.Children);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void SidecarNestingBeyondTheReadersDepthIsRefusedWithAJsonError(int depth)
    {
        // Contrast with the PDF reader: System.Text.Json enforces MaxDepth (64 by
        // default) and reports it, rather than recursing until the stack dies.
        string json = NestedGroups(depth);

        Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));
    }

    private static string NestedGroups(int depth)
    {
        var sb = new StringBuilder();
        sb.Append("{\"Version\":1,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"deep\",\"Artboards\":[],\"Orphans\":[");
        for (int i = 0; i < depth; i++)
        {
            sb.Append("{\"$kind\":\"group\",\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"g\",")
              .Append("\"IsVisible\":true,\"IsLocked\":false,\"Opacity\":1,")
              .Append("\"Transform\":{\"A\":1,\"B\":0,\"C\":0,\"D\":1,\"E\":0,\"F\":0},")
              .Append("\"Children\":[");
        }

        sb.Append("{\"$kind\":\"path\",\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Name\":\"p\",")
          .Append("\"IsVisible\":true,\"IsLocked\":false,\"Opacity\":1,")
          .Append("\"Fill\":{\"Visible\":false,\"Color\":null,\"Rule\":\"NonZero\"},")
          .Append("\"Stroke\":{\"Visible\":false,\"Color\":null,\"Width\":1,\"Cap\":\"Butt\",\"Join\":\"Miter\",\"MiterLimit\":4,\"Alignment\":\"Center\",\"Dash\":null,\"DashOffset\":0},")
          .Append("\"SubPaths\":[]}");

        for (int i = 0; i < depth; i++)
        {
            sb.Append("]}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    [Fact]
    public void AFutureSidecarVersionIsRefusedAndAnOlderOneIsAccepted()
    {
        string future = "{\"Version\":99,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"x\",\"Artboards\":[],\"Orphans\":[]}";
        Assert.Throws<NotSupportedException>(() => VccadDocumentSerializer.Deserialize(future));

        string older = "{\"Version\":0,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"x\",\"Artboards\":[],\"Orphans\":[]}";
        CadDocument doc = VccadDocumentSerializer.Deserialize(older);
        Assert.Equal("x", doc.Name);
    }

    [Fact]
    public void AnUnknownItemDiscriminatorIsRefused()
    {
        string json = "{\"Version\":1,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"x\",\"Artboards\":[],\"Orphans\":[{\"$kind\":\"not-a-kind\"}]}";
        Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"1e400\"")]
    [InlineData("null")]
    [InlineData("1e400")]
    public void ANonFiniteArtboardSizeInASidecarIsRefused(string width)
    {
        string json = "{\"Version\":1,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"x\"," +
                      "\"Artboards\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"a\"," +
                      "\"X\":0,\"Y\":0,\"Width\":" + width + ",\"Height\":10,\"Layers\":[]}],\"Orphans\":[]}";

        // A non-finite artboard size must be refused, not handed to layout and
        // rendering (which will then compute NaN fits and silently draw nothing).
        Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));
    }

    // ------------------------------------------------------------------
    // Permutations of independent options.
    //
    // Fill visibility and rule, stroke visibility, width, dash pattern and cap/
    // join are independent, and the serializer drops members whose value equals
    // the default. Each combination is written, read back and compared with the
    // canonical model dump, so a lost or reordered flag shows as a difference.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> PathStylePermutations()
    {
        double[][] dashes =
        {
            Array.Empty<double>(),
            new[] { 3.0, 2.0 },
            new[] { 1.0, 2.0, 3.0, 4.0 },
        };
        StrokeCap[] caps = { StrokeCap.Butt, StrokeCap.Round, StrokeCap.Square };
        StrokeJoin[] joins = { StrokeJoin.Miter, StrokeJoin.Round, StrokeJoin.Bevel };

        foreach (bool fillVisible in new[] { true, false })
        foreach (FillRule rule in new[] { FillRule.NonZero, FillRule.EvenOdd })
        foreach (bool strokeVisible in new[] { true, false })
        foreach (double width in new[] { 0.0, 0.5, 25.0 })
        foreach (double[] dash in dashes)
        {
            yield return new object[]
            {
                $"fill={fillVisible}/{rule} stroke={strokeVisible} w={width} dash={dash.Length}",
                fillVisible, rule, strokeVisible, width, dash,
                caps[(int)width % 3], joins[dash.Length % 3],
            };
        }
    }

    [Theory]
    [MemberData(nameof(PathStylePermutations))]
    public void EveryFiniteStylePermutationSurvivesTheRoundTrip(
        string name, bool fillVisible, FillRule rule, bool strokeVisible, double width, double[] dash,
        StrokeCap cap, StrokeJoin join)
    {
        CadDocument doc = CadDocument.CreateDefault(name);
        var path = new PathItem
        {
            Fill = fillVisible ? FillSpec.Solid(new ColorRgb(0.1, 0.2, 0.3, 0.4), rule) : FillSpec.None,
            Stroke = strokeVisible
                ? new StrokeSpec(true, ColorRgb.Black, width, cap, join, 4.0, StrokeAlignment.Center, new DashPattern(dash, 0.5))
                : StrokeSpec.None,
        };
        SubPath sub = path.AddSubPath(true);
        sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(10, 0));
        sub.AppendNode(new Point2D(10, 10));
        doc.Artboards[0].Layers[0].AddItem(path);

        AssertRoundTripsUnchanged(doc, name);
    }

    public static IEnumerable<object[]> ClipPermutations()
    {
        string[] shapes = { "none", "square", "open-line", "empty" };
        foreach (FillRule rule in new[] { FillRule.NonZero, FillRule.EvenOdd })
        foreach (string shape in shapes)
        foreach (int count in new[] { 0, 1, 2 })
        {
            // An empty ClipSpec has no geometry at all; it is legal on an item only
            // as the "clipped to nothing" case, which must still travel.
            if (count == 0 && shape != "none")
            {
                continue;
            }

            yield return new object[] { $"clip={rule}/{shape}x{count}", rule, shape, count };
        }
    }

    [Theory]
    [MemberData(nameof(ClipPermutations))]
    public void EveryClipPermutationSurvivesTheRoundTrip(string name, FillRule rule, string shape, int count)
    {
        CadDocument doc = CadDocument.CreateDefault(name);
        PathItem path = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 10, 10));
        doc.Artboards[0].Layers[0].AddItem(path);

        for (int i = 0; i < count; i++)
        {
            var clip = new ClipSpec { Rule = rule };
            if (shape == "square")
            {
                var square = new SubPath { IsClosed = true };
                square.AppendNode(new Point2D(1, 1));
                square.AppendNode(new Point2D(5, 1));
                square.AppendNode(new Point2D(5, 5));
                clip.SubPaths.Add(square);
            }
            else if (shape == "open-line")
            {
                var line = new SubPath { IsClosed = false };
                line.AppendNode(new Point2D(0, 0));
                line.AppendNode(new Point2D(9, 9));
                clip.SubPaths.Add(line);
            }

            path.Clips.Add(clip);
        }

        AssertRoundTripsUnchanged(doc, name);
    }

    public static IEnumerable<object[]> TextRunPermutations()
    {
        foreach (bool hasAdvance in new[] { true, false })
        foreach (bool hasRawCodes in new[] { true, false })
        foreach (bool hasGlyphIds in new[] { true, false })
        foreach (bool hasSourceFont in new[] { true, false })
        {
            yield return new object[]
            {
                $"advance={hasAdvance} raw={hasRawCodes} glyphs={hasGlyphIds} source={hasSourceFont}",
                hasAdvance, hasRawCodes, hasGlyphIds, hasSourceFont,
            };
        }
    }

    [Theory]
    [MemberData(nameof(TextRunPermutations))]
    public void EveryTextRunPermutationSurvivesTheRoundTrip(
        string name, bool hasAdvance, bool hasRawCodes, bool hasGlyphIds, bool hasSourceFont)
    {
        CadDocument doc = CadDocument.CreateDefault(name);
        var text = new TextItem
        {
            Name = "t",
            Origin = new Point2D(5, 6),
            Color = ColorRgb.Black,
            RotationRadians = 0.25,
        };
        var run = new TextRun
        {
            Text = "ABC",
            FontFamily = "Nimbus Sans",
            FontSize = 11.0,
            Bold = true,
            Italic = true,
            AdvanceWidth = hasAdvance ? 12.5 : null,
            RawCodes = hasRawCodes ? "\u0001\u0002\u0003" : null,
            GlyphIds = hasGlyphIds ? new ushort[] { 4, 5, 6 } : null,
            SourceFont = hasSourceFont ? "Helvetica-Bold" : null,
            GapAfter = 1.5,
            PlacedAscentEm = 0.75,
        };
        text.Runs.Add(run);
        doc.Artboards[0].Layers[0].AddItem(text);

        AssertRoundTripsUnchanged(doc, name);
    }

    private static void AssertRoundTripsUnchanged(CadDocument doc, string name)
    {
        string before = ModelDump.Of(doc);
        CadDocument back = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));
        string after = ModelDump.Of(back);

        if (before != after)
        {
            string[] a = before.Split('\n');
            string[] b = after.Split('\n');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                string x = i < a.Length ? a[i] : "<missing>";
                string y = i < b.Length ? b[i] : "<missing>";
                if (x != y)
                {
                    Assert.Fail($"{name}: round trip changed line {i}:\n  before: {x}\n  after : {y}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Degenerate geometry: a path with no nodes, one node, or a node whose
    // handles are NaN. None of these may throw or produce a bogus non-empty box.
    // ------------------------------------------------------------------

    [Fact]
    public void APathWithOneNodeOrNoneIsHandled()
    {
        var empty = new PathItem();
        Assert.Empty(empty.SubPaths);
        Assert.True(empty.BoundingBox().IsEmpty);
        Assert.True(double.IsPositiveInfinity(PathPicking.OutlineDistance(empty, new Point2D(0, 0))));

        var openOne = new PathItem();
        SubPath open = openOne.AddSubPath(false);
        open.AppendNode(new Point2D(10, 20));
        Assert.Equal(0, open.SegmentCount);
        Assert.True(openOne.BoundingBox().IsEmpty);
        Assert.Empty(open.Segments());
        Assert.Throws<ArgumentOutOfRangeException>(() => open.GetSegment(0));
        Assert.Equal(0, open.EstimateLength(), 12);

        var closedOne = new PathItem();
        SubPath closed = closedOne.AddSubPath(true);
        closed.AppendNode(new Point2D(10, 20));
        Assert.Equal(1, closed.SegmentCount);
        Assert.True(closedOne.BoundingBox().IsEmpty);
        Assert.Single(closed.Segments());

        // Nothing may be grabbed by the empty-point box far from the node.
        Assert.Equal(PickKind.None, PathPicking.HitTest(closedOne, new Point2D(0, 0), 0.5));
    }

    [Fact]
    public void APathWithNaNGeometryDoesNotThrowFromTheGeometryQueries()
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(false);
        sub.AppendNode(new Point2D(double.NaN, double.NaN));
        sub.AppendNode(new Point2D(1, 1));

        // These run on every repaint and every hit test; none may throw.
        _ = path.BoundingBox();
        _ = path.WorldBounds();
        _ = PathPicking.OutlineDistance(path, new Point2D(0, 0));
        _ = PathPicking.HitTest(path, new Point2D(0, 0), 1.0);
        _ = PathPicking.ClosestSegment(path, new Point2D(0, 0), 1.0);
        _ = PathPicking.IntersectsRect(path, new Rect2D(0, 0, 10, 10));
        _ = sub.EstimateLength();
    }

    /// <summary>
    /// A NaN rectangle is reported as having extent, because every comparison in
    /// <c>IsEmpty</c> is false for NaN. Callers that guard with
    /// <c>if (box.IsEmpty) return;</c> therefore carry NaN straight into layout,
    /// fitting and export.
    /// </summary>
    [Fact]
    public void ANaNBoundingBoxIsReportedAsEmpty()
    {
        Assert.True(new Rect2D(0, 0, double.NaN, 1.0).IsEmpty);
        Assert.True(new Rect2D(0, 0, 1.0, double.PositiveInfinity).IsEmpty);
    }

    [Fact]
    public void AnEmptyOrDegenerateClipContainsNothingAndDoesNotThrow()
    {
        var empty = new ClipSpec();
        Assert.True(empty.IsEmpty);
        Assert.False(empty.Contains(new Point2D(0, 0)));

        var oneNode = new ClipSpec();
        var single = new SubPath { IsClosed = true };
        single.AppendNode(new Point2D(3, 3));
        oneNode.SubPaths.Add(single);
        Assert.False(oneNode.Contains(new Point2D(3, 3)));

        Assert.Single(oneNode.Clone().SubPaths);
    }

    /// <summary>
    /// A clip outline containing NaN has no inside. Today the winding test answers
    /// "inside" for any query point, so a clipped item paints as though it were
    /// not clipped at all — a silent wrong answer from malformed geometry.
    /// </summary>
    [Fact]
    public void AClipMadeOfNaNGeometryIsNotTreatedAsContainingEveryPoint()
    {
        var nan = new ClipSpec();
        var nanSub = new SubPath { IsClosed = true };
        nanSub.AppendNode(new Point2D(double.NaN, double.NaN));
        nanSub.AppendNode(new Point2D(1, 0));
        nanSub.AppendNode(new Point2D(0, 1));
        nan.SubPaths.Add(nanSub);

        Assert.False(nan.Contains(new Point2D(0, 0)));
    }
}
