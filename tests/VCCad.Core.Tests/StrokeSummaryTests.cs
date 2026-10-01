using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What a selection's strokes agree on.
///
/// A panel that edits a selection shows one value per member, and a selection is not obliged to agree with itself.
/// Showing the first path's value as though it were everyone's is the defect #111 names - a person types a number,
/// believes it describes what they selected, and it does not.
/// </summary>
public class StrokeSummaryTests
{
    private static PathItem Path(double width, StrokeCap cap = StrokeCap.Butt, StrokeJoin join = StrokeJoin.Miter,
        double miter = 4, StrokeAlignment alignment = StrokeAlignment.Center)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, width, cap, join, miter, alignment));
        return path;
    }

    /// <summary>A selection that agrees reports the value, and no member is mixed.</summary>
    [Fact]
    public void ASelectionThatAgreesReportsTheValue()
    {
        StrokeSummary summary = StrokeSummary.Of(
            new[] { Path(5), Path(5), Path(5) }, 0);

        Assert.Equal(5.0, summary.Width!.Value, 6);
        Assert.False(summary.WidthMixed);
        Assert.Equal(StrokeCap.Butt, summary.Cap);
        Assert.False(summary.IsMixed);
        Assert.False(summary.IsEmpty);
        Assert.Equal(3, summary.Strokes);
    }

    /// <summary>
    /// **A member that disagrees is mixed, and the others are still reported.** Blanking everything because one
    /// member differs would hide the values the selection does agree on, which is most of what a panel shows.
    /// </summary>
    [Fact]
    public void OneDisagreeingMemberDoesNotHideTheOthers()
    {
        StrokeSummary summary = StrokeSummary.Of(
            new[] { Path(5), Path(9) }, 0);

        Assert.True(summary.WidthMixed);
        Assert.Null(summary.Width);

        // The members they do agree on are still there.
        Assert.False(summary.CapMixed);
        Assert.Equal(StrokeCap.Butt, summary.Cap);
        Assert.Equal(4.0, summary.MiterLimit!.Value, 6);
        Assert.True(summary.IsMixed);
    }

    [Fact]
    public void EachMemberIsMixedOnItsOwn()
    {
        StrokeSummary caps = StrokeSummary.Of(
            new[] { Path(5, StrokeCap.Round), Path(5, StrokeCap.Square) }, 0);

        Assert.True(caps.CapMixed);
        Assert.Null(caps.Cap);
        Assert.False(caps.WidthMixed);
        Assert.Equal(5.0, caps.Width!.Value, 6);

        StrokeSummary alignment = StrokeSummary.Of(
            new[] { Path(5, alignment: StrokeAlignment.Inside), Path(5, alignment: StrokeAlignment.Outside) }, 0);

        Assert.True(alignment.AlignmentMixed);
        Assert.False(alignment.CapMixed);
    }

    /// <summary>
    /// **A path with no stroke at that index is a gap, not a disagreement.** Counting it as "different" would make
    /// every selection with a stroke-less path report mixed for every member, which would make the report useless.
    /// </summary>
    [Fact]
    public void APathWithNoStrokeIsAGapRatherThanADisagreement()
    {
        var bare = new PathItem { Name = "bare", Fill = FillSpec.None };
        bare.Strokes.Clear();
        bare.Strokes.Add(StrokeSpec.None);

        StrokeSummary summary = StrokeSummary.Of(new[] { Path(5), bare }, 0);

        Assert.Equal(1, summary.Strokes);
        Assert.Equal(2, summary.Paths);
        Assert.False(summary.IsMixed);
        Assert.Equal(5.0, summary.Width!.Value, 6);
    }

    /// <summary>A stack shorter than the index has nothing there, which is the same kind of gap.</summary>
    [Fact]
    public void AShorterStackIsSkipped()
    {
        PathItem single = Path(5);
        PathItem two = Path(5);
        two.Strokes.Add(new StrokeSpec(true, ColorRgb.Red, 9, StrokeCap.Butt, StrokeJoin.Miter, 4));

        StrokeSummary summary = StrokeSummary.Of(new[] { single, two }, 1);

        Assert.Equal(1, summary.Strokes);
        Assert.False(summary.IsMixed);
        Assert.Equal(9.0, summary.Width!.Value, 6);
    }

    /// <summary>A selection with nothing to describe says so rather than reporting a default.</summary>
    [Fact]
    public void ASelectionWithNoStrokeIsEmpty()
    {
        StrokeSummary summary = StrokeSummary.Of(Array.Empty<PathItem>(), 0);

        Assert.True(summary.IsEmpty);
        Assert.Equal(0, summary.Strokes);
        Assert.Null(summary.Width);
        Assert.False(summary.WidthMixed);
    }

    /// <summary>Visibility is part of it: a hidden stroke is not something to report a width for.</summary>
    [Fact]
    public void AHiddenStrokeIsNotReported()
    {
        PathItem path = Path(5);
        path.Strokes[0] = path.Strokes[0] with { IsVisible = false };

        Assert.True(StrokeSummary.Of(new[] { path }, 0).IsEmpty);
    }
}
