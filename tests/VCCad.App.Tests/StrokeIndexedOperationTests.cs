using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The registry can name **one** stroke of the appearance stack.
///
/// `style.setWidthProfile`, `profile.apply`, `style.setDynamics` and `style.clearDynamics` wrote every stroke of
/// every selected path and quietly ignored a `strokeIndex`, so a person could set a profile or a tablet response on
/// one member of the stack while a driver could not - a capability that exists only in the UI, which is a defect in
/// this repository. The stroke index is presence-checked rather than defaulted: naming none means every stroke, which
/// is what these operations did before the stack was addressable, and naming one must mean exactly that member.
/// </summary>
public class StrokeIndexedOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Two selected paths, each carrying a stack of two strokes.</summary>
    private static (AutomationContext Context, PathItem First, PathItem Second) Host()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, "first");
        PathItem second = Line(viewModel, "second");
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        return (new AutomationContext { ViewModel = viewModel }, first, second);
    }

    private static PathItem Line(EditorViewModel viewModel, string name)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static object Points() => new[]
    {
        new { position = 0.0, left = 2.0, right = 2.0 },
        new { position = 1.0, left = 12.0, right = 12.0 },
    };

    /// <summary>
    /// **A profile named by stroke index lands on that stroke and nowhere else.** The "silently ignored" case: before
    /// the index was honoured the profile reached stroke 0 too, which is what a person editing one member did not ask
    /// for and a driver could not avoid.
    /// </summary>
    [Fact]
    public void AWidthProfileNamedByStrokeIndexLandsOnThatStrokeOnly()
    {
        (AutomationContext context, PathItem first, PathItem second) = Host();

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new { points = Points(), strokeIndex = 1 }));

        Assert.NotNull(first.Strokes[1].WidthProfile);
        Assert.Null(first.Strokes[0].WidthProfile);
        Assert.NotNull(second.Strokes[1].WidthProfile);
        Assert.Null(second.Strokes[0].WidthProfile);

        // The profile is the one asked for, not merely present.
        Assert.Equal(12.0, first.Strokes[1].WidthProfile!.Points[1].LeftWidth, 6);
    }

    /// <summary>The same for a profile from the document's library.</summary>
    [Fact]
    public void AStoredProfileAppliedAtIndexLandsOnThatStrokeOnly()
    {
        (AutomationContext context, PathItem first, PathItem second) = Host();

        EditorOperations.Invoke(context, "profile.create", Params(new { name = "Taper", points = Points() }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Taper", strokeIndex = 1 }));

        Assert.Equal("Taper", first.Strokes[1].WidthProfile?.Name);
        Assert.Null(first.Strokes[0].WidthProfile);
        Assert.Equal("Taper", second.Strokes[1].WidthProfile?.Name);
        Assert.Null(second.Strokes[0].WidthProfile);
    }

    /// <summary>A tablet response named by stroke index lands on that stroke and nowhere else.</summary>
    [Fact]
    public void DynamicsNamedByStrokeIndexLandsOnThatStrokeOnly()
    {
        (AutomationContext context, PathItem first, PathItem second) = Host();

        EditorOperations.Invoke(
            context, "style.setDynamics", Params(new { target = "width", preset = "linear", strokeIndex = 1 }));

        Assert.True(first.Strokes[1].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.Null(first.Strokes[0].Dynamics);
        Assert.True(second.Strokes[1].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.Null(second.Strokes[0].Dynamics);
    }

    /// <summary>
    /// Clearing by stroke index clears that stroke and leaves the other's response in place - the operation that made
    /// the gap visible, because "clear every stroke" is the opposite of naming one.
    /// </summary>
    [Fact]
    public void ClearingDynamicsAtIndexLeavesTheOtherStrokesResponse()
    {
        (AutomationContext context, PathItem first, PathItem second) = Host();
        foreach (PathItem path in new[] { first, second })
        {
            for (int i = 0; i < path.Strokes.Count; i++)
            {
                path.Strokes[i] = path.Strokes[i] with { Dynamics = DynamicsSpec.PressureToWidth() };
            }
        }

        EditorOperations.Invoke(context, "style.clearDynamics", Params(new { strokeIndex = 1 }));

        Assert.Null(first.Strokes[1].Dynamics);
        Assert.True(first.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.Null(second.Strokes[1].Dynamics);
        Assert.True(second.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);
    }

    /// <summary>
    /// **Naming none still means every stroke**, which is what these operations did before the stack was addressable
    /// and must keep doing: a caller that passes no index sees no change at all.
    /// </summary>
    [Fact]
    public void WithoutAStrokeIndexEveryStrokeIsStillWritten()
    {
        (AutomationContext context, PathItem first, PathItem second) = Host();

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new { points = Points() }));

        Assert.NotNull(first.Strokes[0].WidthProfile);
        Assert.NotNull(first.Strokes[1].WidthProfile);
        Assert.NotNull(second.Strokes[0].WidthProfile);
        Assert.NotNull(second.Strokes[1].WidthProfile);
    }
}
