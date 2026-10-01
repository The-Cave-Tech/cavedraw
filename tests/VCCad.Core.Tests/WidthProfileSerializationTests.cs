using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A width profile survives a save and reload, and an ordinary stroke is written exactly as it was before
/// profiles existed.
///
/// Both halves matter. A profile is a piece of a drawing's style; one that came back nameless or with its
/// interpolation flattened would be a stroke that looks different after a round trip, which is the defect this
/// project treats as the worst kind.
/// </summary>
public class WidthProfileSerializationTests
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

    private static (PathItem Restored, string Json) RoundTrip(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        byte[] bytes = VccadDocumentSerializer.SerializeToBytes(document);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(bytes);
        return (reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single(),
            System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void AProfileSurvivesSaveAndReload()
    {
        var stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, default,
            new WidthProfileSpec("Brush 4", new[]
            {
                new WidthPoint(0.0, 16, 4),
                new WidthPoint(0.6, 10, 2, WidthInterpolation.Cubic),
                new WidthPoint(1.0, 0, 0),
            }));

        (PathItem restored, _) = RoundTrip(Line(stroke));

        WidthProfileSpec profile = restored.Stroke.WidthProfile!;
        Assert.Equal("Brush 4", profile.Name);
        Assert.Equal(3, profile.Points.Count);
        Assert.Equal(0.6, profile.Points[1].Position, 6);
        Assert.Equal(10.0, profile.Points[1].LeftWidth, 3);
        Assert.Equal(2.0, profile.Points[1].RightWidth, 3);
        Assert.Equal(WidthInterpolation.Cubic, profile.Points[1].Interpolation);
        Assert.Equal(WidthInterpolation.Linear, profile.Points[2].Interpolation);
    }

    /// <summary>
    /// A stroke with no profile is written without the member, so nothing that has none changes on the way out -
    /// the same rule the stack and the gradients follow.
    /// </summary>
    [Fact]
    public void AStrokeWithoutAProfileIsWrittenWithoutTheMember()
    {
        (_, string json) = RoundTrip(Line(new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4)));

        Assert.DoesNotContain("WidthProfile", json);
    }

    /// <summary>And one that has a profile says so, so the absence above is a real signal rather than an accident.</summary>
    [Fact]
    public void AStrokeWithAProfileWritesTheMember()
    {
        (_, string json) = RoundTrip(Line(new StrokeSpec(
            true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4, StrokeAlignment.Center, default,
            WidthProfileSpec.Taper(12, 2))));

        Assert.Contains("WidthProfile", json);
        Assert.Contains("Taper", json);
    }

    /// <summary>A profile with no points is not one, so it is not written and does not send anything down the outline route.</summary>
    [Fact]
    public void AnEmptyProfileIsTreatedAsNoProfile()
    {
        StrokeSpec stroke = new StrokeSpec(
            true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4, StrokeAlignment.Center, default,
            new WidthProfileSpec("Empty", Array.Empty<WidthPoint>()));

        Assert.False(stroke.HasWidthProfile);

        (_, string json) = RoundTrip(Line(stroke));
        Assert.DoesNotContain("WidthProfile", json);
    }
}
