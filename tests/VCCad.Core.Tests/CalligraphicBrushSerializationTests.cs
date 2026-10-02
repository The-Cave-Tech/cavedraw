using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A brush survives a save and reload, and a document that has none is written exactly as it was before
/// brushes existed (issue #99).
///
/// Both halves matter, and the second is the one that is easy to get wrong: a brush is the **last** member of
/// the stroke and of the document, and every one of them is optional, so a sidecar written today for a drawing
/// that uses no brush has to be the same bytes it was before the type existed. A reader has to treat an absent
/// member as "no brush" rather than inventing a default nib.
/// </summary>
public class CalligraphicBrushSerializationTests
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
    public void ABrushSurvivesSaveAndReload()
    {
        BrushSpec nib = BrushSpec.Calligraphic("Chisel", angleDegrees: 35, roundness: 0.2, diameter: 24);
        (PathItem restored, _) = RoundTrip(Line(Plain() with { Brush = nib }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.Equal("Chisel", brush.Name);
        Assert.Equal(BrushKind.Calligraphic, brush.Kind);
        Assert.Equal(35.0, brush.AngleDegrees, 6);
        Assert.Equal(0.2, brush.Roundness, 6);
        Assert.Equal(24.0, brush.Diameter, 6);
    }

    /// <summary>The dynamics a brush records travel with it, so a nib that responds to the pen says so after a reload.</summary>
    [Fact]
    public void TheNibsDynamicsSurviveSaveAndReload()
    {
        BrushSpec nib = BrushSpec.Calligraphic(
            "Chisel", angleDegrees: 35, roundness: 0.2, diameter: 24,
            dynamics: DynamicsSpec.PressureToWidth(DynamicsPreset.Soft));
        (PathItem restored, _) = RoundTrip(Line(Plain() with { Brush = nib }));

        BrushSpec brush = restored.Stroke.Brush!;
        Assert.True(brush.HasDynamics);
        DynamicsTargetSpec width = brush.Dynamics!.For(DynamicsTarget.Width);
        Assert.True(width.Enabled);
        Assert.Equal(DynamicsCurve.FromPreset(DynamicsPreset.Soft), width.Curve);
    }

    /// <summary>
    /// A stroke with no brush is written without the member, so nothing that has none changes on the way out -
    /// the same rule the width profile, the effects and the stroke stack follow.
    /// </summary>
    [Fact]
    public void AStrokeWithoutABrushIsWrittenWithoutTheMember()
    {
        (_, string json) = RoundTrip(Line(Plain()));

        Assert.DoesNotContain("Brush", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Roundness", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Diameter", json, StringComparison.Ordinal);
    }

    /// <summary>And one that has a brush says so, so the absence above is a real signal rather than an accident.</summary>
    [Fact]
    public void AStrokeWithABrushWritesTheMember()
    {
        (_, string json) = RoundTrip(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));

        Assert.Contains("\"Brush\"", json, StringComparison.Ordinal);
        Assert.Contains("Chisel", json, StringComparison.Ordinal);
        Assert.Contains("Calligraphic", json, StringComparison.Ordinal);
        Assert.Contains("\"Roundness\":0.2", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The old-bytes test.** A sidecar written by a build that had no brush type round-trips to the very
    /// same bytes: nothing is invented on load, so nothing new is written on the next save. That is what
    /// "absent at its default" has to mean for a document that was written before the member existed.
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
    }

    /// <summary>
    /// An absent brush member is read as **no brush**, not as a default nib: a stroke that came back with a
    /// nib at angle zero would draw a different line from the file that never mentioned one.
    ///
    /// Proved by taking the sidecar of a document that **does** have a brush and cutting the member out of it,
    /// which is the state a file written before brushes existed is in.
    /// </summary>
    [Fact]
    public void AnAbsentBrushMemberIsNoBrush()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(
            Line(Plain() with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) }));

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

    /// <summary>The library is part of the document, so a brush made once survives a round trip as an asset.</summary>
    [Fact]
    public void TheBrushLibrarySurvivesSaveAndReload()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddBrush(BrushSpec.Calligraphic("Chisel", 35, 0.2, 24));
        document.AddBrush(BrushSpec.Calligraphic("Round", 0, 1.0, 6));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.Equal(new[] { "Chisel", "Round" }, reloaded.Brushes.Select(b => b.Name).ToArray());
        Assert.Equal(0.2, reloaded.FindBrush("Chisel")!.Roundness, 6);
    }

    /// <summary>And a document with no brushes is written without the library member at all.</summary>
    [Fact]
    public void ADocumentWithNoBrushesIsWrittenWithoutTheLibrary()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(Line(Plain()));

        string json = VccadDocumentSerializer.Serialize(document);

        Assert.DoesNotContain("Brushes", json, StringComparison.Ordinal);
    }
}
