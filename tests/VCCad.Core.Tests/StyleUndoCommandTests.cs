using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Undo for a repaint of a style.
///
/// The panels write the new colour onto the paths as the person drags, for live feedback, and
/// only then build the command. A command that records "the value it finds on its first Do"
/// therefore records the value the panel just wrote, and Undo puts that same value back - a
/// silent no-op. The fix is for the command to be told what it is replacing, so the caller's
/// ordering cannot decide whether undo works.
/// </summary>
public class StyleUndoCommandTests
{
    private static PathItem Rect()
    {
        var path = new PathItem { Name = "rect" };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        return path;
    }

    [Fact]
    public void SetFillUndoesAValueTheLivePassAlreadyWrote()
    {
        PathItem path = Rect();
        path.Fill = FillSpec.Solid(ColorRgb.Red);

        FillSpec previous = path.Fill;
        FillSpec next = FillSpec.Solid(ColorRgb.Blue);

        // The live pass has already put the new colour on the path.
        path.Fill = next;

        var stack = new CommandStack();
        stack.Execute(new SetFillCommand(path, next, previous));

        Assert.Equal(next.Color, path.Fill.Color);

        stack.Undo();
        Assert.Equal(ColorRgb.Red, path.Fill.Color);
    }

    [Fact]
    public void SetStrokeUndoesAValueTheLivePassAlreadyWrote()
    {
        PathItem path = Rect();
        path.Stroke = StrokeSpec.None;

        StrokeSpec previous = path.Stroke;
        StrokeSpec next = previous with { Color = ColorRgb.Green, Width = 4 };

        path.Stroke = next;

        var stack = new CommandStack();
        stack.Execute(new SetStrokeCommand(path, next, previous));

        Assert.Equal(next.Width, path.Stroke.Width, 6);

        stack.Undo();
        Assert.Equal(previous.Width, path.Stroke.Width, 6);
        Assert.Equal(previous.Color, path.Stroke.Color);
    }
}
