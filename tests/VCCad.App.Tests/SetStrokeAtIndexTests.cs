using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `style.setStroke` can edit **any** stroke of the stack, not only the top one.
///
/// The stroke inspector lets a person pick a stroke and edit it; without this the same index reached only the panel,
/// and a driver could not edit the stroke a person was looking at - a capability existing only in the UI, which this
/// repository calls a defect. `index` counts from the bottom and its absence is the old behaviour, so nothing that
/// already worked changes.
/// </summary>
public class SetStrokeAtIndexTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Red, 2, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    /// <summary>**The indexed edit lands on the stroke that was named**, and leaves its neighbour alone.</summary>
    [Fact]
    public void AnIndexEditsThatStrokeOfTheStack()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setStroke", Params(new
        {
            color = new[] { 0.0, 255.0, 0.0 },
            width = 5.0,
            index = 0,
        }));

        Assert.Equal(5.0, path.Strokes[0].Width, 6);
        Assert.Equal(1.0, path.Strokes[0].Color.G, 6);

        // The second stroke is untouched, in width and in colour.
        Assert.Equal(8.0, path.Strokes[1].Width, 6);
        Assert.Equal(1.0, path.Strokes[1].Color.B, 6);
    }

    /// <summary>And omitting the index is exactly the old behaviour: the operation as it was.</summary>
    [Fact]
    public void NoIndexEditsTheTopStrokeAsBefore()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setStroke", Params(new { width = 3.0 }));

        // The un-indexed operation is the one that has always existed, and it is not narrowed here: whatever it\n        // did before, it still does. The indexed form above is the change.\n        Assert.Equal(3.0, path.Strokes[1].Width, 6);
    }

    /// <summary>One gesture is one undo step, whichever stroke it lands on.</summary>
    [Fact]
    public void TheIndexedEditIsOneUndoStep()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setStroke", Params(new { width = 6.0, index = 0 }));
        Assert.Equal(6.0, path.Strokes[0].Width, 6);

        context.Session.Undo();

        Assert.Equal(2.0, path.Strokes[0].Width, 6);
    }

    /// <summary>An index past the end of a stack changes nothing rather than throwing.</summary>
    [Fact]
    public void AnIndexPastTheEndChangesNothing()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setStroke", Params(new { width = 9.0, index = 7 }));

        Assert.Equal(2.0, path.Strokes[0].Width, 6);
        Assert.Equal(8.0, path.Strokes[1].Width, 6);
    }
}
