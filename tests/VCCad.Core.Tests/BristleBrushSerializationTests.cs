using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The bristle brush survives a save and a reload, and a document that has no bristle brush is written exactly as it
/// was before the kind existed (issue #103).
///
/// The absence half carries the weight. A document with no brush at all, and every brush of the four kinds that
/// existed before this one - calligraphic, art, pattern and scatter - must gain no bristle member on the wire,
/// because the members belong to a kind. And the presence half is not a round-trip alone: a round trip is correct
/// even when nothing honours the value, which is the defect this whole family has produced seven times, so the
/// bristle's own geometry test measures what the bundle draws.
/// </summary>
public class BristleBrushSerializationTests
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

    /// <summary>A bundle with every control stated at something other than the model's default, so nothing can be lost quietly.</summary>
    private static BrushSpec Full()
        => BrushSpec.Bristle(
            "Scrub",
            size: 36,
            new BristleBrushSpec(
                Count: 23,
                Length: 55,
                Stiffness: 0.8,
                Thickness: 1.75,
                Spread: 1.4,
                Randomness: 0.6,
                PressureSpread: 0.9,
                TiltTurn: 2.5,
                ColourJitter: 0.45));

    [Fact]
    public void ABristleBrushSurvivesSaveAndReload()
    {
        BrushSpec bristle = Full();
        (PathItem restored, string json) = RoundTrip(Line(Plain() with { Brush = bristle }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.Equal("Scrub", brush.Name);
        Assert.Equal(BrushKind.Bristle, brush.Kind);
        Assert.True(brush.IsBristle);
        Assert.False(brush.IsNib);
        Assert.False(brush.IsArt);
        Assert.False(brush.IsPattern);
        Assert.False(brush.IsScatter);
        Assert.Equal(36.0, brush.Diameter, 6);

        // The whole bundle comes back as one value, not member by member: a control left behind would still make
        // the five assertions around it pass.
        Assert.Equal(bristle.BristleSpec, brush.BristleSpec);

        Assert.Contains("Bristle", json, StringComparison.Ordinal);
        Assert.Contains("\"Count\":23", json, StringComparison.Ordinal);
        Assert.Contains("\"ColourJitter\":0.45", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A bundle equal to the model's own defaults still round-trips as a bundle.** A brush that states the
    /// defaults is not the same document as one that states nothing: the first is a bristle brush, the second is a
    /// nib - and the store has to keep that apart, which is what writing the spec for the kind does.
    /// </summary>
    [Fact]
    public void ABundleHoldingEveryDefaultStillComesBackAsABundle()
    {
        (PathItem restored, string json) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Bristle("Plain", size: 20) }));

        Assert.True(restored.Stroke.Brush!.IsBristle);
        Assert.Equal(BristleBrushSpec.Default, restored.Stroke.Brush.BristleSpec);
        Assert.Contains("Bristle", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The old-bytes guard for the kind.** A nib, an art brush, a pattern brush and a scatter brush are written
    /// with no bristle member at all, so every brush that existed before this change is written as it was.
    /// </summary>
    [Fact]
    public void EveryOtherKindGainsNoBristleMemberOnTheWire()
    {
        (_, string nib) = RoundTrip(Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));
        Assert.DoesNotContain("Bristle", nib, StringComparison.Ordinal);

        (_, string art) = RoundTrip(Line(Plain() with { Brush = BrushSpec.Art("Vine", Guid.NewGuid(), 20) }));
        Assert.DoesNotContain("Bristle", art, StringComparison.Ordinal);

        (_, string pattern) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Pattern("Rail", 12, side: new PatternTileSpec(Guid.NewGuid())) }));
        Assert.DoesNotContain("Bristle", pattern, StringComparison.Ordinal);

        (_, string scatter) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Scatter("Spray", null, 20, spacing: new ScatterParameter(30)) }));
        Assert.DoesNotContain("Bristle", scatter, StringComparison.Ordinal);
    }

    /// <summary>The bristle brush as a **library asset**, which is where a brush is made once and used many times.</summary>
    [Fact]
    public void TheBristleBrushLibrarySurvivesSaveAndReload()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Calligraphic("Chisel", 35, 0.2, 24));
        document.AddBrush(BrushSpec.Art("Vine", Guid.NewGuid(), 18, ArtStretch.ScaleProportionally));
        document.AddBrush(BrushSpec.Pattern("Rail", 12, side: new PatternTileSpec(Guid.NewGuid())));
        document.AddBrush(BrushSpec.Scatter("Spray", Guid.NewGuid(), 20, spacing: new ScatterParameter(30)));
        document.AddBrush(Full());

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.Equal(
            new[] { "Chisel", "Vine", "Rail", "Spray", "Scrub" },
            reloaded.Brushes.Select(b => b.Name).ToArray());

        BrushSpec back = reloaded.FindBrush("Scrub")!;
        Assert.True(back.IsBristle);
        Assert.Equal(23, back.BristleSpec!.Count);
        Assert.Equal(2.5, back.BristleSpec.TiltTurn, 9);
    }

    /// <summary>
    /// **The old-bytes test.** A sidecar written by a build that had no bristle brush type round-trips to the very
    /// same bytes: nothing is invented on load, so nothing new is written on the next save.
    /// </summary>
    [Fact]
    public void ASidecarWrittenBeforeBristleBrushesExistedRoundTripsToTheSameBytes()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Scatter("Spray", null, 20, spacing: new ScatterParameter(30)));
        document.Artboards[0].Layers[0].AddItem(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));

        string before = VccadDocumentSerializer.Serialize(document);
        string after = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(before));

        Assert.Equal(before, after);
        Assert.DoesNotContain("Bristle", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sidecar is **deterministic** for a bristle brush too: the same document serialises to the same bytes,
    /// which the whole store depends on and which a member holding a hash-ordered thing would break.
    /// </summary>
    [Fact]
    public void ABristleBrushSerialisesToTheSameBytesTwice()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(Full());
        document.Artboards[0].Layers[0].AddItem(Line(Plain() with { Brush = Full() }));

        string first = VccadDocumentSerializer.Serialize(document);
        string second = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(first));

        Assert.Equal(first, second);
        Assert.Contains("Bristle", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A brush written by an older build is not read as a bristle brush.** The kind is read as the file wrote it,
    /// so a calligraphic brush stays calligraphic and gains no bundle - which is what makes the absent member mean
    /// "not this kind" rather than "this build guessed".
    /// </summary>
    [Fact]
    public void ADocumentWrittenWithoutTheMemberReadsBackAsTheKindTheFileStates()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Calligraphic("Chisel", 35, 0.2, 24));

        string json = VccadDocumentSerializer.Serialize(document);
        CadDocument reloaded = VccadDocumentSerializer.Deserialize(json);

        BrushSpec brush = reloaded.FindBrush("Chisel")!;
        Assert.Equal(BrushKind.Calligraphic, brush.Kind);
        Assert.Null(brush.BristleSpec);
        Assert.False(brush.IsBristle);
    }
}
