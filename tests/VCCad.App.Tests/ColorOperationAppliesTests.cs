using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The colour operations carry the colour into the document themselves.
///
/// <c>color.set</c> and its siblings write <see cref="EditorColorState.Shared"/>, and the document used to follow
/// only because a live <c>ColorsPane</c> was subscribed and applied the change from its own handler. With no pane
/// on screen - the Color tab hidden, or a headless host - the working colour moved and the selection did not. That
/// is the design defect AGENTS.md §1.1 names: a capability that lives in a control rather than in the operation, so
/// a driver-facing call behaves differently depending on which tab a person happens to have open.
///
/// Every test here drives the registry with **no pane at all** and asserts the document, not the state. The pane
/// half of the behaviour - a person clicking the wheel still recolours the selection - is pinned by
/// <see cref="ColorsPaneLifetimeTests"/> and <see cref="ColorsPaneEyedropperTests"/>.
/// </summary>
public class ColorOperationAppliesTests : IDisposable
{
    private static readonly ColorRgb Choosable = ColorRgb.FromBytes(200, 40, 60);

    private readonly ColorRgb _wasColour = EditorColorState.Shared.Color;

    /// <summary>A document with one selected white square, and no pane anywhere.</summary>
    private static (EditorViewModel Vm, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "square", Fill = FillSpec.Solid(ColorRgb.White) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 60)));
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (vm, path);
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static void AssertColour(ColorRgb expected, ColorRgb actual)
    {
        Assert.Equal(expected.R, actual.R, 3);
        Assert.Equal(expected.G, actual.G, 3);
        Assert.Equal(expected.B, actual.B, 3);
    }

    /// <summary>
    /// The assertion the issue asks for, in the shape it asks for it: drive <c>color.set</c> with no Color pane
    /// attached at all and assert the selection changed. On the leaked-subscription code this fails - the working
    /// colour moves and the selection stays white - because the only thing that ever applied it was the pane.
    /// </summary>
    [Fact]
    public void SettingTheColourRecoloursTheSelectionWithNoPaneAttached()
    {
        (EditorViewModel vm, PathItem path) = Host();
        var context = new AutomationContext { ViewModel = vm };

        EditorOperations.Invoke(context, "color.set", Params(new { hex = "#C8283C" }));

        AssertColour(Choosable, path.Fill.Color);
        Assert.True(path.Fill.IsVisible);

        // The working colour is the same value in both places, so the picker and the document cannot disagree.
        AssertColour(Choosable, EditorColorState.Shared.Color);
    }

    /// <summary>
    /// The colour reaches the document as one undoable edit, so Ctrl+Z puts it back. It used to be applied by the
    /// pane's live-preview path, which writes the document without recording anything: the change was invisible to
    /// Undo and did not mark the document modified.
    /// </summary>
    [Fact]
    public void TheChangeIsOneUndoStep()
    {
        (EditorViewModel vm, PathItem path) = Host();
        var context = new AutomationContext { ViewModel = vm };

        EditorOperations.Invoke(context, "color.set", Params(new { hex = "#C8283C" }));
        AssertColour(Choosable, path.Fill.Color);

        vm.Undo();

        AssertColour(ColorRgb.White, path.Fill.Color);
        Assert.False(vm.ActiveSession.CanUndo);
    }

    /// <summary>
    /// The fill/stroke target a person chooses with the circles beside the picker, reachable from the operation so
    /// that a driver can say what it means. Without an explicit target the colour is a fill.
    /// </summary>
    [Fact]
    public void AnExplicitStrokeTargetRecoloursTheStrokeRatherThanTheFill()
    {
        (EditorViewModel vm, PathItem path) = Host();
        path.Stroke = StrokeSpec.Hairline(ColorRgb.Black);
        var context = new AutomationContext { ViewModel = vm };

        EditorOperations.Invoke(context, "color.set", Params(new { hex = "#C8283C", target = "stroke" }));

        AssertColour(Choosable, path.Stroke.Color);
        Assert.True(path.Stroke.IsVisible);

        // The fill is left as it was: naming the stroke is choosing one, not both.
        AssertColour(ColorRgb.White, path.Fill.Color);
    }

    /// <summary>
    /// Recolouring keeps each path's own winding rule, as the picker does. Forcing one rule turns a holed outline
    /// from EvenOdd into NonZero, which fills the hole in - a recolour that changes the shape.
    /// </summary>
    [Fact]
    public void RecolouringKeepsEachPathsOwnWindingRule()
    {
        (EditorViewModel vm, PathItem path) = Host();
        path.Fill = FillSpec.Solid(ColorRgb.White, FillRule.EvenOdd);
        var context = new AutomationContext { ViewModel = vm };

        EditorOperations.Invoke(context, "color.set", Params(new { hex = "#C8283C" }));

        AssertColour(Choosable, path.Fill.Color);
        Assert.Equal(FillRule.EvenOdd, path.Fill.Rule);
    }

    /// <summary>
    /// A colour the picker selects is applied even when nothing is selected: it is the style the next object is
    /// drawn with, which is what the fill/stroke circles in the pane stand for.
    /// </summary>
    [Fact]
    public void WithNothingSelectedTheWorkingColourBecomesTheFillForTheNextObject()
    {
        (EditorViewModel vm, _) = Host();
        vm.SelectObject(null);
        var context = new AutomationContext { ViewModel = vm };

        EditorOperations.Invoke(context, "color.set", Params(new { hex = "#C8283C" }));

        AssertColour(Choosable, vm.CurrentFill.Color);
    }

    /// <summary>
    /// A screen pick chooses a colour, so it recolours the selection too - the eyedropper is a way of choosing, not
    /// a way of reading. It is <c>color.picked</c> that reports without choosing.
    /// </summary>
    [Fact]
    public async Task AScreenPickRecoloursTheSelectionWithNoPaneAttached()
    {
        (EditorViewModel vm, PathItem path) = Host();
        var context = new AutomationContext
        {
            ViewModel = vm,
            PickFromScreenAsync = () => Task.FromResult<(ColorRgb?, string?)>((Choosable, null)),
        };

        await EditorOperations.InvokeAsync(context, "color.pickScreen", default);

        AssertColour(Choosable, path.Fill.Color);
    }

    public void Dispose()
    {
        // The colour state is process-wide, so a test that writes it puts it back.
        EditorColorState.Shared.SetColor(_wasColour);
        GC.SuppressFinalize(this);
    }
}
