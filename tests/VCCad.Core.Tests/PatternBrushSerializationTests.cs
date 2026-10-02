using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The pattern brush survives a save and a reload, and a document that has no pattern brush is written exactly as
/// it was before the kind existed (issue #101).
///
/// The absence half carries the weight, and it has three edges rather than the art brush's two. A document with no
/// brush at all must still serialise to its old bytes; a **calligraphic** brush and an **art** brush - which is
/// every brush written before this change - must gain no pattern member on the wire either, because the members
/// belong to a kind; and a pattern brush's own empty slots must stay absent, so "the file filled no corner slot"
/// is a different document from "the build filled it with something".
/// </summary>
public class PatternBrushSerializationTests
{
    private static PathItem Line(StrokeSpec stroke)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(80, 0)));
        path.Stroke = stroke;
        return path;
    }

    private static StrokeSpec Plain()
        => new(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);

    private static (PathItem Restored, string Json) RoundTrip(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        byte[] bytes = VccadDocumentSerializer.SerializeToBytes(document);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(bytes);
        return (reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single(),
            Encoding.UTF8.GetString(bytes));
    }

    private static BrushSpec Full()
    {
        var side = new PatternTileSpec(Guid.NewGuid());
        var start = new PatternTileSpec(Guid.NewGuid(), FlipAcross: true);
        var end = new PatternTileSpec(Guid.NewGuid(), FlipAlong: true);
        var inner = new PatternTileSpec(Guid.NewGuid(), RotationDegrees: 45.0);
        var outer = new PatternTileSpec(Guid.NewGuid(), Scale: 2.5);

        return BrushSpec.Pattern("Rail", 18, side, start, end, inner, outer, spacing: 3.5,
            cornerThresholdDegrees: 12.0);
    }

    [Fact]
    public void APatternBrushSurvivesSaveAndReload()
    {
        BrushSpec pattern = Full();
        (PathItem restored, string json) = RoundTrip(Line(Plain() with { Brush = pattern }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.Equal("Rail", brush.Name);
        Assert.Equal(BrushKind.Pattern, brush.Kind);
        Assert.True(brush.IsPattern);
        Assert.False(brush.IsNib);
        Assert.False(brush.IsArt);
        Assert.Equal(18.0, brush.Diameter, 6);
        Assert.Equal(3.5, brush.PatternSpacing, 6);
        Assert.Equal(12.0, brush.PatternCornerThresholdDegrees, 6);

        Assert.Equal(pattern.PatternSideTile, brush.PatternSideTile);
        Assert.Equal(pattern.PatternStartTile, brush.PatternStartTile);
        Assert.Equal(pattern.PatternEndTile, brush.PatternEndTile);
        Assert.Equal(pattern.PatternInnerTile, brush.PatternInnerTile);
        Assert.Equal(pattern.PatternOuterTile, brush.PatternOuterTile);

        Assert.Contains("PatternSideTile", json, StringComparison.Ordinal);
        Assert.Contains("PatternOuterTile", json, StringComparison.Ordinal);
        Assert.Contains("\"Rotation\":45", json, StringComparison.Ordinal);
        Assert.Contains("\"Scale\":2.5", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A **pattern brush with no tile set** is not a pattern brush with tiles: the slots that hold nothing are
    /// absent, so a file that filled only a side tile comes back having filled only a side tile rather than one
    /// this build invented.
    /// </summary>
    [Fact]
    public void APatternBrushFillsOnlyTheSlotsItHolds()
    {
        var side = Guid.NewGuid();
        (PathItem restored, string json) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Pattern("Rail", 12, side: new PatternTileSpec(side)) }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.Equal(BrushKind.Pattern, brush.Kind);
        Assert.Equal(side, brush.PatternSideTile!.Asset);
        Assert.Null(brush.PatternStartTile);
        Assert.Null(brush.PatternEndTile);
        Assert.Null(brush.PatternInnerTile);
        Assert.Null(brush.PatternOuterTile);

        Assert.Contains("PatternSideTile", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PatternStartTile", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PatternEndTile", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PatternInnerTile", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PatternOuterTile", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The old-bytes guard for the kind.** A calligraphic brush and an art brush are written with no pattern
    /// member at all, so every brush that existed before this change is written as it was.
    /// </summary>
    [Fact]
    public void ANibOrAnArtBrushGainsNoPatternMemberOnTheWire()
    {
        (_, string nib) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));
        Assert.Contains("Calligraphic", nib, StringComparison.Ordinal);
        Assert.DoesNotContain("Pattern", nib, StringComparison.Ordinal);

        (_, string art) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Art("Vine", Guid.NewGuid(), 20) }));
        Assert.Contains("Art", art, StringComparison.Ordinal);
        Assert.DoesNotContain("Pattern", art, StringComparison.Ordinal);
    }

    /// <summary>The pattern brush as a **library asset**, which is where a brush is made once and used many times.</summary>
    [Fact]
    public void ThePatternBrushLibrarySurvivesSaveAndReload()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Calligraphic("Chisel", 35, 0.2, 24));
        document.AddBrush(BrushSpec.Art("Vine", Guid.NewGuid(), 18, ArtStretch.ScaleProportionally));
        document.AddBrush(Full());

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.Equal(new[] { "Chisel", "Vine", "Rail" }, reloaded.Brushes.Select(b => b.Name).ToArray());
        Assert.True(reloaded.FindBrush("Chisel")!.IsNib);
        Assert.True(reloaded.FindBrush("Vine")!.IsArt);
        Assert.True(reloaded.FindBrush("Rail")!.IsPattern);
        Assert.Equal(3.5, reloaded.FindBrush("Rail")!.PatternSpacing, 6);
        Assert.Equal(12.0, reloaded.FindBrush("Rail")!.PatternCornerThresholdDegrees, 6);
    }

    /// <summary>
    /// **The old-bytes test.** A sidecar written by a build that had no pattern brush type round-trips to the very
    /// same bytes: nothing is invented on load, so nothing new is written on the next save.
    /// </summary>
    [Fact]
    public void ASidecarWrittenBeforePatternBrushesExistedRoundTripsToTheSameBytes()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Art("Vine", Guid.NewGuid(), 18));
        document.Artboards[0].Layers[0].AddItem(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));
        string before = VccadDocumentSerializer.Serialize(document);

        string after = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(before));

        Assert.Equal(before, after);
        Assert.DoesNotContain("Pattern", after, StringComparison.Ordinal);
        Assert.DoesNotContain("PatternTile", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sidecar is **deterministic** for a pattern brush too: the same document serialises to the same bytes,
    /// which the whole store depends on and which a member holding a hash-ordered thing would break.
    /// </summary>
    [Fact]
    public void APatternBrushSerialisesToTheSameBytesTwice()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(Full());
        document.Artboards[0].Layers[0].AddItem(Line(Plain() with { Brush = Full() }));

        string first = VccadDocumentSerializer.Serialize(document);
        string second = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(first));

        Assert.Equal(first, second);
        Assert.Contains("Pattern", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pattern brush's artwork is named rather than copied, so a document whose tile item is gone reports it -
    /// which is the difference between a known gap and a brush that quietly draws nothing.
    /// </summary>
    [Fact]
    public void ADeletedTileItemIsReportedRatherThanSilentlyDrawn()
    {
        var tile = new PathItem { Name = "tile", Fill = FillSpec.None };
        var gone = Guid.NewGuid();

        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(Line(Plain()));
        document.AddBrush(BrushSpec.Pattern(
            "Rail", 12,
            side: new PatternTileSpec(tile.Id),
            outerCorner: new PatternTileSpec(gone)));

        // Neither id is in the tree, so both are reported; then the side tile's item arrives, and only the corner
        // tile's is still missing - in slot order, which is the side first and the outer corner second.
        Assert.Equal(
            new[] { tile.Id, gone },
            document.MissingBrushAssets().Select(m => m.Asset).ToArray());

        document.Orphans.AddItem(tile);

        (BrushSpec Brush, Guid Asset) missing = Assert.Single(document.MissingBrushAssets());
        Assert.Equal("Rail", missing.Brush.Name);
        Assert.Equal(gone, missing.Asset);
    }
}
