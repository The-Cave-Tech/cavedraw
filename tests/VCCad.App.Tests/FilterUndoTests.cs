using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **A filter edit is an edit.** The graph is document state like any other, so adding a primitive, wiring one,
/// retuning a value, deleting a filter and applying one to a selection each have to be one undo step - the same
/// stack, the same "one edit is one step" the rest of the editor keeps.
///
/// This is asserted on the document **model**, not on the operation's own answer: a `filter.list` that echoes the
/// request would carry the test by itself, and the question here is what Undo puts back.
/// </summary>
public class FilterUndoTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static void Create(AutomationContext context, string name = "drop")
    {
        EditorOperations.Invoke(context, "filter.create", Params(new
        {
            name,
            primitives = new object[]
            {
                new { kind = "gaussianBlur", @in = "SourceAlpha", radius = 2.0, result = "soft" },
            },
        }));
    }

    /// <summary>
    /// The whole document as text, for a before/after comparison.
    ///
    /// The **serializer** rather than <c>ModelDump</c>: the dump describes objects and their appearance, and a
    /// filter is document state, so a dump is the same with and without the filter library - which would let these
    /// tests pass with the model never restored at all.
    /// </summary>
    private static string Dump(AutomationContext context)
        => VccadDocumentSerializer.Serialize(context.Document);

    // ---------------------------------------------------------------- one edit, one step

    [Fact]
    public void AddingAPrimitiveUndoesAndRedoesAsOneStep()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string before = Dump(context);
        int depth = context.Session.UndoDepth;

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "flood",
            result = "tint",
            floodColor = new[] { 255, 0, 0 },
        }));

        Assert.NotEqual(before, Dump(context));
        Assert.Equal(depth + 1, context.Session.UndoDepth);

        context.Session.Undo();

        Assert.Equal(before, Dump(context));
        Assert.Equal(depth, context.Session.UndoDepth);

        context.Session.Redo();

        Assert.Equal(depth + 1, context.Session.UndoDepth);
        Assert.Equal(2, context.Document.FindFilter("drop")!.Primitives.Count);
        Assert.Equal(FilterPrimitiveKind.Flood, context.Document.FindFilter("drop")!.Primitives[1].Kind);
    }

    [Fact]
    public void SettingAPrimitiveParameterUndoesToThePreviousValue()
    {
        (AutomationContext context, _) = Host();
        Create(context);

        string before = Dump(context);
        EditorOperations.Invoke(context, "filter.setPrimitiveParameter", Params(new
        {
            name = "drop",
            index = 0,
            parameter = "radius",
            value = 9.0,
        }));

        Assert.Equal(9.0, context.Document.FindFilter("drop")!.Primitives[0].Radius);

        context.Session.Undo();
        Assert.Equal(before, Dump(context));

        context.Session.Redo();
        Assert.Equal(9.0, context.Document.FindFilter("drop")!.Primitives[0].Radius);
    }

    [Fact]
    public void DeletingAFilterUndoesWithTheItemsThatDrewThroughIt()
    {
        (AutomationContext context, PathItem path) = Host();
        Create(context);
        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "drop" }));

        string before = Dump(context);
        Assert.Equal("drop", path.FilterId);

        EditorOperations.Invoke(context, "filter.delete", Params(new { name = "drop" }));

        Assert.Null(context.Document.FindFilter("drop"));
        Assert.Null(path.FilterId);

        context.Session.Undo();

        Assert.Equal(before, Dump(context));
        Assert.NotNull(context.Document.FindFilter("drop"));
        Assert.Equal("drop", path.FilterId);

        context.Session.Redo();
        Assert.Null(context.Document.FindFilter("drop"));
        Assert.Null(path.FilterId);
    }

    /// <summary>
    /// Applying a filter to a selection is a document edit too - it is the operation a person reaches through the
    /// Filter panel - so it is on the same stack and one step, like every other appearance edit.
    /// </summary>
    [Fact]
    public void ApplyingAFilterToTheSelectionUndoesAsOneStep()
    {
        (AutomationContext context, PathItem path) = Host();
        Create(context);

        int depth = context.Session.UndoDepth;
        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "drop" }));

        Assert.Equal("drop", path.FilterId);
        Assert.Equal(depth + 1, context.Session.UndoDepth);

        context.Session.Undo();
        Assert.Null(path.FilterId);
        Assert.Equal(depth, context.Session.UndoDepth);
    }

    /// <summary>
    /// The other half of "one undo step per operation": a driver that makes the same edit twice and undoes twice
    /// arrives at the state between them and then at the state before the first, rather than losing the first edit
    /// with the second undo.
    ///
    /// The <c>filter.create</c> is an edit too - the library is document state like a width profile, and
    /// <c>profile.create</c> has always been one step - so the stack is empty only after that step's own undo. That
    /// is asserted rather than assumed: a create that quietly bypassed the session would leave the last line
    /// failing, which is the difference between "the filter edits are on the stack" and "some of them are".
    /// </summary>
    [Fact]
    public void TwoEditsTakeTwoUndosToRevert()
    {
        (AutomationContext context, _) = Host();

        string empty = Dump(context);
        Create(context);

        string before = Dump(context);

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "offset",
            @in = "soft",
            dx = 3.0,
            dy = 0.0,
            result = "moved",
        }));

        string halfway = Dump(context);

        EditorOperations.Invoke(context, "filter.addPrimitive", Params(new
        {
            name = "drop",
            kind = "flood",
            result = "tint",
            floodColor = new[] { 0, 0, 255 },
        }));

        context.Session.Undo();
        Assert.Equal(halfway, Dump(context));

        context.Session.Undo();
        Assert.Equal(before, Dump(context));

        Assert.True(context.Session.CanUndo);

        context.Session.Undo();
        Assert.Equal(empty, Dump(context));

        Assert.False(context.Session.CanUndo);
    }
}
