using VCCad.Core.Model;
using VCCad.Core.Serialization;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A hatch survives a save and a reload, exactly.
///
/// A hatch is only worth round-tripping exactly: an angle that comes back as zero is a differently-hatched
/// drawing, and a spacing that comes back rounded is a different pattern. Every member travels, including the
/// ones that are usually default.
/// </summary>
public class HatchSerializationTests
{
    private static PathItem Hatched(FillSpec fill)
    {
        var path = new PathItem { Name = "hatched", Fill = fill };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(80, 0)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(80, 60)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(0, 60)));
        return path;
    }

    private static PathItem RoundTrip(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        return reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
    }

    [Fact]
    public void AHatchSurvivesSaveAndReload()
    {
        var hatch = new HatchSpec(new[]
        {
            new HatchLineSpec(30, 2, 3, 7, 0.75, new DashPattern(new[] { 4.0, 2.0 }, 1.5), StrokeCap.Round),
            new HatchLineSpec(-30, 0, 0, 7),
        });

        PathItem back = RoundTrip(Hatched(FillSpec.WithHatch(hatch)));

        Assert.NotNull(back.Fill.Hatch);
        Assert.Equal(2, back.Fill.Hatch!.Lines.Count);

        HatchLineSpec first = back.Fill.Hatch.Lines[0];
        Assert.Equal(30.0, first.AngleDegrees, 6);
        Assert.Equal(2.0, first.OffsetX, 6);
        Assert.Equal(3.0, first.OffsetY, 6);
        Assert.Equal(7.0, first.Spacing, 6);
        Assert.Equal(0.75, first.Width, 6);
        Assert.Equal(new[] { 4.0, 2.0 }, first.Dash.Segments.ToArray());
        Assert.Equal(1.5, first.Dash.Offset, 6);
        Assert.Equal(StrokeCap.Round, first.Cap);

        Assert.Equal(-30.0, back.Fill.Hatch.Lines[1].AngleDegrees, 6);
    }

    /// <summary>A path with no hatch comes back with none, so the member does not appear where it is not used.</summary>
    [Fact]
    public void APathWithNoHatchComesBackWithNone()
    {
        PathItem back = RoundTrip(Hatched(FillSpec.Solid(ColorRgb.Black)));
        Assert.Null(back.Fill.Hatch);
    }

    /// <summary>A family with no spacing has no repetitions and is dropped rather than drawn as one line.</summary>
    [Fact]
    public void AFamilyWithNoSpacingIsDropped()
    {
        var hatch = new HatchSpec(new[]
        {
            HatchLineSpec.Standard(5),
            new HatchLineSpec(0, 0, 0, 0),
        });

        PathItem back = RoundTrip(Hatched(FillSpec.WithHatch(hatch)));
        Assert.Single(back.Fill.Hatch!.Lines);
    }
}