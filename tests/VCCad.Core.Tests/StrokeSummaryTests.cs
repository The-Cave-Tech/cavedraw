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
        double miter = 4, StrokeAlignment alignment = StrokeAlignment.Center,
        DashPattern? dash = null, WidthProfileSpec? profile = null, DynamicsSpec? dynamics = null)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, width, cap, join, miter, alignment,
            dash ?? DashPattern.None, profile, Dynamics: dynamics));
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

    /// <summary>
    /// **Dash, width profile and tablet dynamics are agreement too**, and each is mixed on its own. The stroke pane
    /// worked the dash out for itself because there was no member for it here; a second opinion in a panel is a
    /// reading that can drift from the one `style.commonStroke` reports, which is why it belongs in the summary.
    /// </summary>
    [Fact]
    public void ADisagreeingDashProfileAndDynamicsAreEachMixedOnTheirOwn()
    {
        StrokeSummary dashes = StrokeSummary.Of(
            new[] { Path(5, dash: new DashPattern(new[] { 4.0, 2.0 })), Path(5, dash: new DashPattern(new[] { 1.0, 1.0 })) }, 0);

        Assert.True(dashes.DashMixed);
        Assert.Null(dashes.Dash);
        Assert.True(dashes.IsMixed);

        StrokeSummary profiles = StrokeSummary.Of(
            new[] { Path(5, profile: WidthProfileSpec.Taper(1, 5)), Path(5, profile: WidthProfileSpec.Constant(3)) }, 0);

        Assert.True(profiles.WidthProfileMixed);
        Assert.Null(profiles.WidthProfile);

        StrokeSummary dynamics = StrokeSummary.Of(
            new[]
            {
                Path(5, dynamics: DynamicsSpec.PressureToWidth(DynamicsPreset.Linear)),
                Path(5, dynamics: DynamicsSpec.PressureToWidth(DynamicsPreset.Soft)),
            }, 0);

        Assert.True(dynamics.DynamicsMixed);
        Assert.Null(dynamics.Dynamics);

        // And the members the selection does agree on are still reported.
        Assert.False(dynamics.WidthMixed);
        Assert.Equal(5.0, dynamics.Width!.Value, 6);
    }

    /// <summary>A selection that agrees on all three reports the values rather than a mix.</summary>
    [Fact]
    public void AnAgreeingDashProfileAndDynamicsAreReported()
    {
        var dash = new DashPattern(new[] { 4.0, 2.0 });
        WidthProfileSpec profile = WidthProfileSpec.Taper(1, 5);
        DynamicsSpec dynamics = DynamicsSpec.PressureToWidth();

        StrokeSummary summary = StrokeSummary.Of(
            new[]
            {
                Path(5, dash: dash, profile: profile, dynamics: dynamics),
                Path(5, dash: new DashPattern(new[] { 4.0, 2.0 }), profile: WidthProfileSpec.Taper(1, 5),
                    dynamics: DynamicsSpec.PressureToWidth()),
            }, 0);

        Assert.False(summary.DashMixed);
        Assert.False(summary.WidthProfileMixed);
        Assert.False(summary.DynamicsMixed);
        Assert.False(summary.IsMixed);

        Assert.Equal(new[] { 4.0, 2.0 }, summary.Dash!.Value.Segments.ToArray());
        Assert.Equal("Taper", summary.WidthProfile!.Name);
        Assert.True(summary.Dynamics!.For(DynamicsTarget.Width).Enabled);
    }

    /// <summary>
    /// **Storing no dynamics and storing every target switched off are the same picture**, so they agree rather than
    /// making the selection read mixed over a difference nothing on canvas can show.
    /// </summary>
    [Fact]
    public void NoDynamicsAndAnAllOffSpecAgree()
    {
        StrokeSummary summary = StrokeSummary.Of(
            new[] { Path(5), Path(5, dynamics: DynamicsSpec.None) }, 0);

        Assert.False(summary.DynamicsMixed);
        Assert.False(summary.IsMixed);
    }
}
