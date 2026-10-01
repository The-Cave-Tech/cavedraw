using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `DocumentSession.ApplyStrokeFieldsAt` - the member-wise apply the stroke inspector edits through.
///
/// `ApplyStrokeAt` has to be given **every** member, so a panel showing a selection whose widths disagree could only
/// hand it one path's number for the member the person did not touch. Naming only the members to change is what
/// closes that, and "null leaves it alone" is the whole of it - so it is pinned here, directly on the session,
/// rather than only through the pane that happens to use it today.
/// </summary>
public class ApplyStrokeFieldsTests
{
    private static (DocumentSession Session, PathItem First, PathItem Second) Host(
        double[] firstWidths, double[] secondWidths)
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, firstWidths);
        PathItem second = Line(viewModel, secondWidths);
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        return (viewModel.ActiveSession, first, second);
    }

    private static PathItem Line(EditorViewModel viewModel, params double[] widths)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.AddRange(widths.Select(width =>
            new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4.0)));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>
    /// **A member that is not given is left exactly as the stroke has it**, which is what lets an edit to an
    /// agreeing member happen while the selection disagrees about another.
    /// </summary>
    [Fact]
    public void AMemberThatIsNotGivenIsLeftAsTheStrokeHasIt()
    {
        (DocumentSession session, PathItem first, PathItem second) = Host(new[] { 4.0, 8.0 }, new[] { 4.0, 4.0 });

        int changed = session.ApplyStrokeFieldsAt(
            1, width: null, cap: StrokeCap.Round, join: null, miterLimit: null, alignment: null);

        Assert.Equal(2, changed);
        Assert.Equal(StrokeCap.Round, first.Strokes[1].Cap);
        Assert.Equal(StrokeCap.Round, second.Strokes[1].Cap);

        // The widths disagreed (8 and 4) and were not named, so each keeps its own rather than one being written
        // over the other.
        Assert.Equal(8.0, first.Strokes[1].Width, 6);
        Assert.Equal(4.0, second.Strokes[1].Width, 6);

        // And a named member reaches every selected path at that index, leaving the rest of each stack alone.
        Assert.Equal(4.0, first.Strokes[0].Width, 6);
        Assert.Equal(4.0, second.Strokes[0].Width, 6);
    }

    /// <summary>A request that changes nothing reports nothing changed - it puts no undo step on the stack, because
    /// an undo step that undoes to exactly where it started reads as "undo did nothing".</summary>
    [Fact]
    public void ARequestThatChangesNothingReportsNothing()
    {
        (DocumentSession session, PathItem first, PathItem second) = Host(new[] { 4.0, 8.0 }, new[] { 4.0, 8.0 });

        int changed = session.ApplyStrokeFieldsAt(1, null, null, null, 4.0, null);

        Assert.Equal(0, changed);
        Assert.Equal(8.0, first.Strokes[1].Width, 6);
        Assert.Equal(8.0, second.Strokes[1].Width, 6);
    }

    /// <summary>Nothing inspected is a request to change nothing, not a request to change the top of the stack.</summary>
    [Fact]
    public void AnIndexBelowZeroChangesNothing()
    {
        (DocumentSession session, PathItem first, _) = Host(new[] { 4.0, 8.0 }, new[] { 4.0, 8.0 });

        Assert.Equal(0, session.ApplyStrokeFieldsAt(-1, 99.0, null, null, null, null));
        Assert.Equal(8.0, first.Strokes[1].Width, 6);
    }

    /// <summary>A path whose stack is shorter than the index is a gap and is skipped, and one edit is one undo step
    /// across the selection.</summary>
    [Fact]
    public void APathWithNoStrokeThereIsSkippedAndOneGestureIsOneUndoStep()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, 4.0, 8.0);
        PathItem shortStack = Line(viewModel, 5.0);
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(shortStack);

        Assert.Equal(1, viewModel.ActiveSession.ApplyStrokeFieldsAt(1, 7.0, null, null, null, null));
        Assert.Equal(7.0, first.Strokes[1].Width, 6);
        Assert.Equal(5.0, shortStack.Strokes[0].Width, 6);

        viewModel.Undo();

        Assert.Equal(8.0, first.Strokes[1].Width, 6);
    }
}
