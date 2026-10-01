using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Outline effects through the operation registry, and through a save.
///
/// The effect has to be reachable (a person and a driver use the same operations), it has to survive the sidecar,
/// and the seed has to travel with it - an effect whose randomness was not written down would look one way when it
/// was made and another way when the document was re-opened.
/// </summary>
public class StrokeEffectOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, CadDocument Document, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, vm.Document, path);
    }

    [Fact]
    public void AnEffectCanBeAddedAndReadBack()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect",
            Params(new { kind = "roughen", size = 3.5, seed = 42 }));

        OutlineEffectSpec effect = Assert.Single(path.Stroke.AllEffects);
        Assert.Equal(OutlineEffectKind.Roughen, effect.Kind);
        Assert.Equal(3.5, effect.Size, 6);
        Assert.Equal(42, effect.Seed);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "style.strokes", default));
        Assert.Contains("\"kind\":\"Roughen\"", json, StringComparison.Ordinal);
        Assert.Contains("\"seed\":42", json, StringComparison.Ordinal);
    }

    /// <summary>Effects stack in order, because the order changes the result.</summary>
    [Fact]
    public void EffectsAccumulateInOrder()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "offsetPath", size = 5 }));
        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "zigZag", size = 2 }));

        Assert.Equal(
            new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.ZigZag },
            path.Stroke.AllEffects.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void AnUnknownEffectIsRefused()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "sharpen" })));

        Assert.Contains("not an outline effect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(path.Stroke.HasEffects);
    }

    [Fact]
    public void EffectsCanBeCleared()
    {
        (AutomationContext context, _, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "scribble", detail = 3 }));

        EditorOperations.Invoke(context, "style.clearStrokeEffects", default);

        Assert.False(path.Stroke.HasEffects);
        Assert.Null(path.Stroke.Effects);
    }

    [Fact]
    public void AddingAnEffectIsOneUndoStep()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "roughen", size = 2 }));
        context.ViewModel.ActiveSession.Undo();

        Assert.False(path.Stroke.HasEffects);
    }

    /// <summary>
    /// **The seed travels with the effect**, which is what makes a document look the same when it is re-opened as
    /// it did when it was drawn.
    /// </summary>
    [Fact]
    public void EffectsSurviveSaveAndReloadWithTheirSeed()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStrokeEffect",
            Params(new { kind = "roughen", size = 4.25, seed = 99 }));
        EditorOperations.Invoke(context, "style.addStrokeEffect",
            Params(new { kind = "scribble", size = 1.5, detail = 3, seed = 7 }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));
        StrokeSpec back = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.Equal(2, back.AllEffects.Count);
        Assert.Equal(OutlineEffectKind.Roughen, back.AllEffects[0].Kind);
        Assert.Equal(4.25, back.AllEffects[0].Size, 6);
        Assert.Equal(99, back.AllEffects[0].Seed);
        Assert.Equal(OutlineEffectKind.Scribble, back.AllEffects[1].Kind);
        Assert.Equal(3.0, back.AllEffects[1].Detail, 6);
        Assert.Equal(7, back.AllEffects[1].Seed);

        // Equal as a value, not merely same-members: this is what catches a stack that compares by reference.
        Assert.Equal(path.Stroke, back);
    }

    /// <summary>A stroke with no effects gains no member, so an ordinary document is written as it always was.</summary>
    [Fact]
    public void AStrokeWithoutEffectsWritesNoMember()
    {
        (AutomationContext context, CadDocument document, _) = Host();

        string json = System.Text.Encoding.UTF8.GetString(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.DoesNotContain("Effects", json);
    }
}
