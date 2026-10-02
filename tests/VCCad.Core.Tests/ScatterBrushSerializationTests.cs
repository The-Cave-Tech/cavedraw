using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The scatter brush survives a save and a reload, and a document that has no scatter brush is written exactly as it
/// was before the kind existed (issue #102).
///
/// The absence half carries the weight, and it has four edges rather than the pattern brush's three. A document
/// with no brush at all must still serialise to its old bytes; a **calligraphic**, an **art** and a **pattern**
/// brush - which is every brush written before this change - must gain no scatter member on the wire either,
/// because the members belong to a kind; and a scatter brush's own parameters must come back as they were stated,
/// range included, so a scatter that was pinned down does not come back scattered.
/// </summary>
public class ScatterBrushSerializationTests
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
        => BrushSpec.Scatter(
            "Spray",
            Guid.NewGuid(),
            size: 22,
            spacing: new ScatterParameter(40, 8),
            rotation: new ScatterParameter(35, 20),
            scale: new ScatterParameter(1.5, 0.5),
            offset: new ScatterParameter(4, 3),
            opacity: new ScatterParameter(0.7, 0.2));

    [Fact]
    public void AScatterBrushSurvivesSaveAndReload()
    {
        BrushSpec scatter = Full();
        (PathItem restored, string json) = RoundTrip(Line(Plain() with { Brush = scatter }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.Equal("Spray", brush.Name);
        Assert.Equal(BrushKind.Scatter, brush.Kind);
        Assert.True(brush.IsScatter);
        Assert.False(brush.IsNib);
        Assert.False(brush.IsArt);
        Assert.False(brush.IsPattern);
        Assert.Equal(22.0, brush.Diameter, 6);

        Assert.Equal(scatter.ScatterSpec, brush.ScatterSpec);

        // The value and the range both travel, and they are told apart: a brush whose range came back as zero
        // would draw a machined row where the file states a scatter.
        Assert.Equal(40.0, brush.ScatterSpec!.Spacing.Value, 6);
        Assert.Equal(8.0, brush.ScatterSpec.Spacing.Randomness, 6);
        Assert.Equal(35.0, brush.ScatterSpec.Rotation.Value, 6);
        Assert.Equal(20.0, brush.ScatterSpec.Rotation.Randomness, 6);
        Assert.Equal(1.5, brush.ScatterSpec.Scale.Value, 6);
        Assert.Equal(0.5, brush.ScatterSpec.Scale.Randomness, 6);
        Assert.Equal(4.0, brush.ScatterSpec.Offset.Value, 6);
        Assert.Equal(3.0, brush.ScatterSpec.Offset.Randomness, 6);
        Assert.Equal(0.7, brush.ScatterSpec.Opacity.Value, 6);
        Assert.Equal(0.2, brush.ScatterSpec.Opacity.Randomness, 6);

        Assert.Contains("Scatter", json, StringComparison.Ordinal);
        Assert.Contains("\"Randomness\":8", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The old-bytes guard for the kind.** A nib, an art brush and a pattern brush are written with no scatter
    /// member at all, so every brush that existed before this change is written as it was.
    /// </summary>
    [Fact]
    public void ANibAnArtOrAPatternBrushGainsNoScatterMemberOnTheWire()
    {
        (_, string nib) = RoundTrip(Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));
        Assert.Contains("Calligraphic", nib, StringComparison.Ordinal);
        Assert.DoesNotContain("Scatter", nib, StringComparison.Ordinal);

        (_, string art) = RoundTrip(Line(Plain() with { Brush = BrushSpec.Art("Vine", Guid.NewGuid(), 20) }));
        Assert.Contains("Art", art, StringComparison.Ordinal);
        Assert.DoesNotContain("Scatter", art, StringComparison.Ordinal);

        (_, string pattern) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Pattern("Rail", 12, side: new PatternTileSpec(Guid.NewGuid())) }));
        Assert.Contains("Pattern", pattern, StringComparison.Ordinal);
        Assert.DoesNotContain("Scatter", pattern, StringComparison.Ordinal);
    }

    /// <summary>The scatter brush as a **library asset**, which is where a brush is made once and used many times.</summary>
    [Fact]
    public void TheScatterBrushLibrarySurvivesSaveAndReload()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Calligraphic("Chisel", 35, 0.2, 24));
        document.AddBrush(BrushSpec.Art("Vine", Guid.NewGuid(), 18, ArtStretch.ScaleProportionally));
        document.AddBrush(BrushSpec.Pattern("Rail", 12, side: new PatternTileSpec(Guid.NewGuid())));
        document.AddBrush(Full());

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.Equal(new[] { "Chisel", "Vine", "Rail", "Spray" }, reloaded.Brushes.Select(b => b.Name).ToArray());
        Assert.True(reloaded.FindBrush("Spray")!.IsScatter);
        Assert.Equal(40.0, reloaded.FindBrush("Spray")!.ScatterSpec!.Spacing.Value, 6);
    }

    /// <summary>
    /// **The old-bytes test.** A sidecar written by a build that had no scatter brush type round-trips to the very
    /// same bytes: nothing is invented on load, so nothing new is written on the next save.
    /// </summary>
    [Fact]
    public void ASidecarWrittenBeforeScatterBrushesExistedRoundTripsToTheSameBytes()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Art("Vine", Guid.NewGuid(), 18));
        document.Artboards[0].Layers[0].AddItem(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));

        string before = VccadDocumentSerializer.Serialize(document);
        string after = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(before));

        Assert.Equal(before, after);
        Assert.DoesNotContain("Scatter", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sidecar is **deterministic** for a scatter brush too: the same document serialises to the same bytes,
    /// which the whole store depends on and which a member holding a hash-ordered thing would break.
    /// </summary>
    [Fact]
    public void AScatterBrushSerialisesToTheSameBytesTwice()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(Full());
        document.Artboards[0].Layers[0].AddItem(Line(Plain() with { Brush = Full() }));

        string first = VccadDocumentSerializer.Serialize(document);
        string second = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(first));

        Assert.Equal(first, second);
        Assert.Contains("Scatter", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scatter brush's asset is named rather than copied, so a document whose asset is gone reports it - which is
    /// the difference between a known gap and a brush that quietly draws nothing.
    /// </summary>
    [Fact]
    public void ADeletedScatterAssetIsReportedRatherThanSilentlyDrawn()
    {
        var gone = Guid.NewGuid();

        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(Line(Plain()));
        document.AddBrush(BrushSpec.Scatter("Spray", gone, size: 20, spacing: new ScatterParameter(40)));

        (BrushSpec Brush, Guid Asset) missing = Assert.Single(document.MissingBrushAssets());
        Assert.Equal("Spray", missing.Brush.Name);
        Assert.Equal(gone, missing.Asset);
    }
}
