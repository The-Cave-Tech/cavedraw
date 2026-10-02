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
/// The stroke inspector against the **thread rule**, not against a stroke.
///
/// <see cref="EditorViewModel.InspectedStroke"/> is shared between two panels, so a change to it can arrive on any
/// thread - and the synchronous <see cref="EditorOperations.Invoke"/> runs its handler on the caller's thread
/// rather than marshalling to the UI thread, which is exactly how <c>style.inspectStroke</c> reaches a live pane
/// from a driver's thread. Everything the pane does in response is UI work, so it must not be written on the thread
/// the change arrived on. This is the same trap `54832f4` fixed in the colour pane (issue #178), and the same test
/// shape: the pane is attached and stays attached, and the change is made from a thread of its own rather than from
/// the thread pool, so the condition is produced deliberately rather than whenever the runner happens to hand it
/// over.
/// </summary>
public class StrokePaneThreadAffinityTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static PathItem Line(EditorViewModel viewModel, params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static StrokeSpec Stroke(double width)
        => new(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// **A change of inspected stroke made off the UI thread must not write a control on that thread.**
    ///
    /// The pane is attached and shown first, so it is a live subscriber - not a pane built after the change, which
    /// nothing would reach. The operation is invoked from a thread of its own: <c>style.inspectStroke</c> sets the
    /// shared index, the pane hears about it there, and without the guard it writes the readouts there too.
    /// </summary>
    [AvaloniaFact]
    public void AStrokeChangeFromAnotherThreadDoesNotWriteAControlOnThatThread()
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, Stroke(4), Stroke(8));
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 0;

        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        Exception? failure = null;

        // A thread of its own, not the thread pool: the change has to be off the UI thread and the assertion has to
        // be about that, not about which worker the pool happened to hand over.
        var changing = new Thread(() =>
        {
            try
            {
                EditorOperations.Invoke(
                    new AutomationContext { ViewModel = viewModel },
                    "style.inspectStroke",
                    Params(new { index = 1 }));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        changing.Start();
        changing.Join();

        // The change itself has to have happened, or "no exception" would also be true of an operation that never
        // reached the pane at all.
        Assert.Equal(1, viewModel.InspectedStroke);
        Assert.True(failure is null, failure?.ToString());

        // And the UI thread catches up when it gets a turn, so posting is not the same as dropping the change: the
        // pane ends up describing the stroke the shared state names.
        Settle();
        Assert.Equal("8", pane.FindControl<TextBox>("StrokeWidthBox")!.Text);
    }
}
