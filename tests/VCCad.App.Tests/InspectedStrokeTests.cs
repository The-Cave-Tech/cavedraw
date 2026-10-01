using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Which stroke in the stack is being inspected - the state the stroke inspector and the appearance panel share.
///
/// With the appearance stack there is no such thing as "the stroke" on a selection, so the two panels have to agree
/// about which one is being edited. Holding it in one place is what stops a person setting a width in one panel and
/// believing it while the other is showing a different stroke; the issue names that as the defect to avoid.
/// </summary>
public class InspectedStrokeTests
{
    private static (AppearancePane Pane, EditorViewModel ViewModel, PathItem Path) Host(int strokes = 3)
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        for (int i = 0; i < strokes; i++)
        {
            path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 2 + i, StrokeCap.Butt, StrokeJoin.Miter, 4));
        }

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        var pane = new AppearancePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 400, Height = 400, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, path);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Choosing a row in the appearance panel moves the shared state, which is what the inspector reads.</summary>
    [AvaloniaFact]
    public void ChoosingARowMovesTheSharedState()
    {
        (AppearancePane pane, EditorViewModel viewModel, _) = Host();

        var list = pane.FindControl<ListBox>("StrokeList")!;
        list.SelectedIndex = 1;
        Settle();

        Assert.Equal(1, viewModel.InspectedStroke);
        Assert.Equal("stroke 2 of 3", viewModel.InspectedStrokeLabel);
    }

    /// <summary>**A selection change cannot leave it pointing at a stroke that is not there.** A clamped index is
    /// what stops the two panels describing different strokes after the stack shrinks.</summary>
    [AvaloniaFact]
    public void TheIndexIsClampedToTheStack()
    {
        (_, EditorViewModel viewModel, _) = Host(strokes: 3);

        viewModel.InspectedStroke = 99;
        Assert.Equal(2, viewModel.InspectedStroke);

        viewModel.InspectedStroke = -5;
        Assert.Equal(-1, viewModel.InspectedStroke);
    }

    /// <summary>And nothing selected means nothing inspected, rather than a stale index into someone else's stack.</summary>
    [AvaloniaFact]
    public void NothingSelectedMeansNothingInspected()
    {
        (_, EditorViewModel viewModel, PathItem path) = Host(strokes: 2);

        viewModel.InspectedStroke = 1;
        Assert.Equal(1, viewModel.InspectedStroke);

        viewModel.ClearSelection();

        Assert.Equal(-1, viewModel.InspectedStroke);
        Assert.Equal("none", viewModel.InspectedStrokeLabel);
        Assert.Equal(2, path.Strokes.Count);
    }

    /// <summary>The driver's half: the same state, settable and readable through the registry.</summary>
    [Fact]
    public void TheOperationSetsAndReportsTheSameState()
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Red, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Blue, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        var context = new AutomationContext { ViewModel = viewModel };

        EditorOperations.Invoke(context, "style.inspectStroke", Params(new { index = 1 }));
        Assert.Equal(1, viewModel.InspectedStroke);

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.inspectStroke", default));

        Assert.Equal(1, reported.GetProperty("index").GetInt32());
        Assert.Equal(2, reported.GetProperty("strokes").GetInt32());
        Assert.Equal("stroke 2 of 2", reported.GetProperty("label").GetString());
    }
}
