using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using Xunit;
using Point2D = VCCad.Geometry.Point2D;

namespace VCCad.App.Tests;

/// <summary>
/// **Editing one member of a stroke keeps every other member** (issue #189).
///
/// The colour and width paths rebuilt a <see cref="StrokeSpec"/> member by member from the handful of values they
/// knew about, so every member they did not mention - the brush, the width profile, the outline and raster effects,
/// the tablet dynamics, the recorded pen, the per-stroke opacity and blend, and whether the colour follows
/// <c>currentColor</c> - was silently reset to its default. Nothing failed and nothing warned: a person picked a
/// brush, set a width profile, added an outline effect, recorded a pen, then changed the colour and got a plain
/// one-colour line back.
///
/// The fix is a clone-and-assign - a <c>with</c> expression - rather than a hand-written list of members, because a
/// list drifts behind the type the moment a member is added and the compiler cannot see it. So the assertion is
/// deliberately **member by member over the whole type**, not a spot-check of two of them: a test that checked only
/// the brush would pass against a rebuild that still dropped the pen.
///
/// **What counts as "the member being edited" is not the same on every route, and the difference is deliberate.**
/// The colour routes - the picker's live application, <see cref="DocumentSession.ApplyWorkingColour"/> and
/// <see cref="DocumentSession.ApplyStrokeColor"/> - edit one member, so every other member must be identical
/// afterwards. `style.setStroke` without an index restrokes the path and its own documentation says an omitted
/// geometry member takes that operation's default (<c>StrokeEditKeepsMembersTests</c> pins the members it cannot
/// name, which is the whole of #189); the indexed form and the stroke inspector's fields name the member they
/// change and leave the rest, which <c>ApplyStrokeFieldsTests</c> already pins.
/// </summary>
public class StrokeEditKeepsMembersTests
{
    /// <summary>
    /// A stroke with **every** member set to something a default would not be, so nothing survives by accident.
    /// </summary>
    private static StrokeSpec EveryMember() => new(
        IsVisible: true,
        Color: ColorRgb.Black,
        Width: 4.0,
        Cap: StrokeCap.Round,
        Join: StrokeJoin.Bevel,
        MiterLimit: 7.0,
        Alignment: StrokeAlignment.Outside,
        Dash: new DashPattern(new[] { 3.0, 2.0 }),
        WidthProfile: new WidthProfileSpec("Profile", new[]
        {
            WidthPoint.Even(0.0, 2.0),
            WidthPoint.Even(1.0, 6.0, WidthInterpolation.Cubic),
        }),
        Effects: new EffectStack(new[] { OutlineEffectSpec.Roughen(2.0, seed: 5) }),
        RasterEffects: new RasterEffectStack(new[] { RasterEffectSpec.Blur(4.0) }),
        Dynamics: new DynamicsSpec(new[]
        {
            new DynamicsTargetSpec(true, new DynamicsCurve(0.2, 0.4, 0.8, 0.9)),
        }),
        Brush: new BrushSpec("Nib", 45.0, 0.3, 12.0),
        Opacity: 0.4,
        Blend: BlendMode.Multiply,
        Pen: new PenProfile(new[] { new PenSample(0.0, 0.2, 10.0), new PenSample(1.0, 1.0, 50.0) }),
        FromCurrentColor: true);

    private static PathItem Line(EditorViewModel viewModel, StrokeSpec stroke)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 20)));
        path.Strokes.Clear();
        path.Strokes.Add(stroke);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        return path;
    }

    private static (EditorViewModel ViewModel, PathItem Path) Host(StrokeSpec stroke)
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, stroke);
        return (viewModel, path);
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// The members **#189 names**, asserted one by one and as the **same instance** wherever the member is a
    /// reference. Same-instance rather than equal-looking, because a rebuild that happens to reproduce an
    /// equal value is still the defect: the stroke is no longer the brush's, the profile's or the pen's.
    /// </summary>
    private static void AssertUnnameableMembersSurvive(StrokeSpec before, StrokeSpec after)
    {
        Assert.Same(before.WidthProfile, after.WidthProfile);
        Assert.Same(before.Effects, after.Effects);
        Assert.Same(before.RasterEffects, after.RasterEffects);
        Assert.Same(before.Dynamics, after.Dynamics);
        Assert.Same(before.Brush, after.Brush);
        Assert.Equal(before.Opacity, after.Opacity);
        Assert.Equal(before.Blend, after.Blend);
        Assert.Same(before.Pen, after.Pen);
        Assert.Equal(before.FromCurrentColor, after.FromCurrentColor);
    }

    /// <summary>Every member except the colour, for the routes whose only edit is the colour.</summary>
    private static void AssertOnlyColourChanged(StrokeSpec before, StrokeSpec after, ColorRgb expected)
    {
        Assert.Equal(expected.R, after.Color.R, 6);
        Assert.Equal(expected.G, after.Color.G, 6);
        Assert.Equal(expected.B, after.Color.B, 6);

        Assert.True(after.IsVisible);
        Assert.Equal(before.Width, after.Width, 6);
        Assert.Equal(before.Cap, after.Cap);
        Assert.Equal(before.Join, after.Join);
        Assert.Equal(before.MiterLimit, after.MiterLimit, 6);
        Assert.Equal(before.Alignment, after.Alignment);
        Assert.Equal(before.Dash, after.Dash);
        AssertUnnameableMembersSurvive(before, after);
    }

    /// <summary>
    /// **`style.setStroke` with a colour and no index keeps the brush, the profile, the effects, the pen, the
    /// opacity and the blend.** Its geometry defaults are the operation's documented restroke and are asserted
    /// here so the two halves are told apart: the fault was never that the restroke sets a width, it was that a
    /// member the operation has no parameter for was dropped without being mentioned.
    /// </summary>
    [Fact]
    public void TheOperationKeepsEveryMemberItCannotNameWhenItSetsTheColour()
    {
        StrokeSpec before = EveryMember();
        (EditorViewModel viewModel, PathItem path) = Host(before);

        EditorOperations.Invoke(
            new AutomationContext { ViewModel = viewModel },
            "style.setStroke",
            Params(new { color = new[] { 10, 120, 40 } }));

        StrokeSpec after = path.Stroke;
        Assert.Equal(ColorRgb.FromBytes(10, 120, 40).R, after.Color.R, 6);
        Assert.Equal(ColorRgb.FromBytes(10, 120, 40).G, after.Color.G, 6);
        Assert.Equal(ColorRgb.FromBytes(10, 120, 40).B, after.Color.B, 6);

        // The documented restroke: an omitted geometry member takes the operation's default.
        Assert.Equal(1.0, after.Width, 6);
        Assert.Equal(StrokeCap.Butt, after.Cap);
        Assert.Equal(StrokeJoin.Miter, after.Join);
        Assert.Equal(4.0, after.MiterLimit, 6);
        Assert.Equal(StrokeAlignment.Center, after.Alignment);

        AssertUnnameableMembersSurvive(before, after);
    }

    /// <summary>The same when the member named is the width rather than the colour.</summary>
    [Fact]
    public void TheOperationKeepsEveryMemberItCannotNameWhenItSetsTheWidth()
    {
        StrokeSpec before = EveryMember();
        (EditorViewModel viewModel, PathItem path) = Host(before);

        EditorOperations.Invoke(
            new AutomationContext { ViewModel = viewModel },
            "style.setStroke",
            Params(new { width = 9.0 }));

        StrokeSpec after = path.Stroke;
        Assert.Equal(9.0, after.Width, 6);
        Assert.Equal(ColorRgb.Black, after.Color);

        AssertUnnameableMembersSurvive(before, after);
    }

    /// <summary>
    /// The stroke inspector's width field reaches the model through <see cref="DocumentSession.ApplyStroke"/>,
    /// which names width, cap, join, miter and alignment - and nothing else. Those land; the rest is the stroke's.
    /// </summary>
    [Fact]
    public void TheStrokePanesGeometryKeepsEveryMemberItCannotName()
    {
        StrokeSpec before = EveryMember();
        (EditorViewModel viewModel, PathItem path) = Host(before);

        viewModel.ApplyStroke(11.0, StrokeCap.Square, StrokeJoin.Round, 9.0, StrokeAlignment.Inside);

        StrokeSpec after = path.Stroke;
        Assert.Equal(11.0, after.Width, 6);
        Assert.Equal(StrokeCap.Square, after.Cap);
        Assert.Equal(StrokeJoin.Round, after.Join);
        Assert.Equal(9.0, after.MiterLimit, 6);
        Assert.Equal(StrokeAlignment.Inside, after.Alignment);
        Assert.Equal(ColorRgb.Black, after.Color);

        AssertUnnameableMembersSurvive(before, after);
    }

    /// <summary>
    /// <see cref="DocumentSession.ApplyWorkingColour"/> is the colour picker's half and `color.set`'s other half:
    /// choosing a stroke colour on a selection edits the colour and **nothing else**.
    /// </summary>
    [Fact]
    public void TheWorkingColourKeepsEveryOtherMember()
    {
        StrokeSpec before = EveryMember();
        (EditorViewModel viewModel, PathItem path) = Host(before);

        viewModel.ActiveSession.ApplyWorkingColour(ColorRgb.FromBytes(200, 30, 90), stroke: true);

        AssertOnlyColourChanged(before, path.Stroke, ColorRgb.FromBytes(200, 30, 90));
    }

    /// <summary>The other colour route the registry reaches, pinned for the same reason.</summary>
    [Fact]
    public void ApplyStrokeColourKeepsEveryOtherMember()
    {
        StrokeSpec before = EveryMember();
        (EditorViewModel viewModel, PathItem path) = Host(before);

        viewModel.ActiveSession.ApplyStrokeColor(ColorRgb.FromBytes(7, 8, 9));

        AssertOnlyColourChanged(before, path.Stroke, ColorRgb.FromBytes(7, 8, 9));
    }

    /// <summary>
    /// **The colour pane's live application is the same defect at the same site.** Dragging in the picker writes
    /// the selection directly - it does not go through <see cref="DocumentSession"/> because a drag is a preview
    /// that becomes one undo step when it ends - so it had its own rebuild, and it dropped the same members.
    ///
    /// Driven as a person drives it: the stroke ring is clicked to make the stroke the target, then the shared
    /// colour state is changed, which is what a drag in the picker does.
    /// </summary>
    [AvaloniaFact]
    public void TheColourPanesStrokeTargetKeepsEveryOtherMember()
    {
        StrokeSpec before = EveryMember();
        ColorRgb wasColour = EditorColorState.Shared.Color;
        ColorRgb? wasPicked = EditorColorState.Shared.LastPicked;

        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, before);

        var pane = new ColorsPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 900, Height = 700, Content = pane };
        window.Show();
        Settle();

        try
        {
            var selector = pane.FindControl<FillStrokeSelector>("TargetSelector")
                ?? throw new Xunit.Sdk.XunitException("no target selector");

            // The stroke ring sits at 36% of the control, which is the layout FillStrokeSelector draws and
            // hit-tests against. Clicking there is the gesture that arms the stroke target.
            var ring = new Point(selector.Bounds.Width * 0.36, selector.Bounds.Height * 0.36);
            Point inWindow = selector.TranslatePoint(ring, window)
                ?? throw new Xunit.Sdk.XunitException("the selector is not in the window");
            window.MouseDown(inWindow, MouseButton.Left);
            window.MouseUp(inWindow, MouseButton.Left);
            Settle();
            Assert.True(selector.StrokeSelected, "the stroke ring click did not arm the stroke target");

            EditorColorState.Shared.SetColor(ColorRgb.FromBytes(3, 4, 5));
            Settle();

            AssertOnlyColourChanged(before, path.Stroke, ColorRgb.FromBytes(3, 4, 5));
        }
        finally
        {
            window.Close();
            pane.Detach();
            Settle();
            EditorColorState.Shared.RestorePicked(wasPicked);
            EditorColorState.Shared.SetColor(wasColour);
        }
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
