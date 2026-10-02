using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The art brush survives a save and a reload, and a document that has no art brush is written exactly as it was
/// before the kind existed (issue #100).
///
/// The absence half is the one that carries the weight, and it has two edges. A document with no brush at all must
/// still serialise to its old bytes, which is the rule the whole brush member has followed since #99. And a
/// **calligraphic** brush - which is every brush written before this change - must gain no art member on the wire
/// either: the art members belong to a kind, and writing a stretch or an asset onto a nib would be this build
/// inventing data about a file that never said it.
/// </summary>
public class ArtBrushSerializationTests
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

    [Fact]
    public void AnArtBrushSurvivesSaveAndReload()
    {
        var asset = Guid.NewGuid();
        BrushSpec art = BrushSpec.Art(
            "Vine", asset, 24,
            ArtStretch.StretchToFit, flipAcross: true, flipAlong: true,
            ArtColourisation.TintAndShade, new ColorRgb(0.1, 0.2, 0.3));

        (PathItem restored, string json) = RoundTrip(Line(Plain() with { Brush = art }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.Equal("Vine", brush.Name);
        Assert.Equal(BrushKind.Art, brush.Kind);
        Assert.Equal(asset, brush.ArtAsset);
        Assert.Equal(24.0, brush.Diameter, 6);
        Assert.Equal(ArtStretch.StretchToFit, brush.Stretch);
        Assert.True(brush.FlipAcross);
        Assert.True(brush.FlipAlong);
        Assert.Equal(ArtColourisation.TintAndShade, brush.Colourisation);
        Assert.Equal(new ColorRgb(0.1, 0.2, 0.3), brush.ShadeColour);

        Assert.Contains("Art", json, StringComparison.Ordinal);
        Assert.Contains("StretchToFit", json, StringComparison.Ordinal);
        Assert.Contains(asset.ToString(), json, StringComparison.Ordinal);
    }

    /// <summary>
    /// An art brush that names no asset is read back naming no asset. A reader that filled the gap with an id it
    /// made up - or with the empty guid - would point the brush at nothing in a way nothing could tell from a
    /// brush that really named the item it invented.
    /// </summary>
    [Fact]
    public void AnArtBrushWithNoAssetKeepsHavingNone()
    {
        (PathItem restored, string json) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Art("Empty", asset: null, size: 12) }));

        Assert.Null(restored.Stroke.Brush!.ArtAsset);
        Assert.Equal(BrushKind.Art, restored.Stroke.Brush.Kind);
        Assert.DoesNotContain("ArtAsset", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The old-bytes guard for the kind.** A calligraphic brush is written with no art member at all, so every
    /// brush that existed before this change is written as it was.
    /// </summary>
    [Fact]
    public void ACalligraphicBrushGainsNoArtMemberOnTheWire()
    {
        (_, string json) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));

        Assert.Contains("\"Brush\"", json, StringComparison.Ordinal);
        Assert.Contains("Calligraphic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ArtAsset", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Stretch", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Flip", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Colourisation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ShadeColour", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The art brush as a **library asset**, which is where a brush is made once and used many times: the name,
    /// the asset it maps and the members that decide how it is laid along the path all travel.
    /// </summary>
    [Fact]
    public void TheArtBrushLibrarySurvivesSaveAndReload()
    {
        var asset = Guid.NewGuid();
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Calligraphic("Chisel", 35, 0.2, 24));
        document.AddBrush(BrushSpec.Art("Vine", asset, 18, ArtStretch.ScaleProportionally));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.Equal(new[] { "Chisel", "Vine" }, reloaded.Brushes.Select(b => b.Name).ToArray());
        Assert.True(reloaded.FindBrush("Chisel")!.IsNib);
        Assert.True(reloaded.FindBrush("Vine")!.IsArt);
        Assert.Equal(asset, reloaded.FindBrush("Vine")!.ArtAsset);
        Assert.Equal(ArtStretch.ScaleProportionally, reloaded.FindBrush("Vine")!.Stretch);
    }

    /// <summary>
    /// **The old-bytes test.** A sidecar written by a build that had no brush type at all round-trips to the very
    /// same bytes: nothing is invented on load, so nothing new is written on the next save.
    /// </summary>
    [Fact]
    public void ASidecarWrittenBeforeBrushesExistedRoundTripsToTheSameBytes()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(Line(Plain()));
        string before = VccadDocumentSerializer.Serialize(document);

        string after = VccadDocumentSerializer.Serialize(VccadDocumentSerializer.Deserialize(before));

        Assert.Equal(before, after);
        Assert.DoesNotContain("Brush", after, StringComparison.Ordinal);
        Assert.DoesNotContain("ArtAsset", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent brush member is read as **no brush**, not as an art brush at its default: a stroke that came back
    /// with an asset it never named would draw artwork the file does not have.
    ///
    /// Proved by taking the sidecar of a document that **does** have a brush and cutting the member out of it,
    /// which is the state a file written before brushes existed is in.
    /// </summary>
    [Fact]
    public void AnAbsentBrushMemberIsNoBrush()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(
            Line(Plain() with { Brush = BrushSpec.Art("Vine", Guid.NewGuid(), 20) }));

        string json = VccadDocumentSerializer.Serialize(document);
        Assert.Contains("\"Brush\"", json, StringComparison.Ordinal);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(WithoutBrushMember(json));

        PathItem path = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Null(path.Stroke.Brush);
        Assert.False(path.Stroke.HasBrush);
    }

    /// <summary>
    /// The sidecar with the object member <paramref name="name"/> cut out of it, brace-matched so a member that
    /// holds an array of objects is removed whole. It is what a file written before the member existed looks
    /// like, and asserting against real bytes is stronger than asserting against a string that never had it.
    /// </summary>
    private static string WithoutBrushMember(string json)
    {
        const string Name = "\"Brush\":";
        int at = json.IndexOf(Name, StringComparison.Ordinal);

        int open = json.IndexOf('{', at);
        int depth = 0;
        int end = open;
        for (; end < json.Length; end++)
        {
            if (json[end] == '{')
            {
                depth++;
            }
            else if (json[end] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end++;
                    break;
                }
            }
        }

        int start = at > 0 && json[at - 1] == ',' ? at - 1 : at;
        return string.Concat(json.AsSpan(0, start), json.AsSpan(end));
    }
}
