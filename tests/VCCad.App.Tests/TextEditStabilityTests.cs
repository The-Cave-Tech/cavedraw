using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Opening a text block for editing must not change it.
///
/// The Transform pane's fields display measurements and commit on focus loss - and opening a text block moves
/// focus out of them, so clicking into a block **is** a commit. Reading a field back is a round trip through a
/// unit and a formatted string, and that round trip is not exact: an untouched field asked for a scale of about
/// 0.999. On a path that is invisible. On type it is not, because a text item's size **is** its font size, so
/// the type shrank a little every time a block was opened and again on the next click.
/// </summary>
public class TextEditStabilityTests
{
    private static (Window Window, TransformPane Pane, EditorViewModel Vm, TextItem Text) Host()
    {
        var vm = new EditorViewModel();

        var text = new TextItem { Origin = new Point2D(40, 60) };
        text.Runs.Add(new TextRun { Text = "Bag End", FontSize = 24 });
        vm.Document.Artboards[0].Layers[0].AddItem(text);
        vm.SelectObject(text);

        var pane = new TransformPane();
        pane.Attach(vm);
        var window = new Window { Width = 700, Height = 560, Content = pane };
        window.Show();
        Settle();
        return (window, pane, vm, text);
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

    /// <summary>Committing a field nobody typed into is not an edit.</summary>
    [AvaloniaFact]
    public void CommittingAnUntouchedFieldLeavesTheTypeAlone()
    {
        (Window window, TransformPane pane, _, TextItem text) = Host();
        try
        {
            double size = text.Runs[0].FontSize;
            double before = text.Origin.X;

            // What happens when focus leaves the pane, which is what clicking into the canvas does.
            pane.CommitFromField(pane.WBox);
            pane.CommitFromField(pane.HBox);
            Settle();

            Assert.Equal(size, text.Runs[0].FontSize, 3);
            Assert.Equal(before, text.Origin.X, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>And it stays true after several of them, which is what repeated clicks look like.</summary>
    [AvaloniaFact]
    public void RepeatedCommitsDoNotShrinkItFurther()
    {
        (Window window, TransformPane pane, _, TextItem text) = Host();
        try
        {
            double size = text.Runs[0].FontSize;

            for (int i = 0; i < 5; i++)
            {
                pane.CommitFromField(pane.WBox);
                pane.CommitFromField(pane.HBox);
                Settle();
            }

            Assert.Equal(size, text.Runs[0].FontSize, 3);
        }
        finally
        {
            window.Close();
        }
    }

    //    /// <summary>
    /// A real edit still transforms - the guard is about the identity, not about ignoring the fields. Proved
    /// on a path, which is sized by W and H: a text item's size is its type, and the pane's width field is not
    /// the control for it.
    /// </summary>
    [AvaloniaFact]
    public void ATypedMeasurementStillTransforms()
    {
        var vm = new EditorViewModel();
        var rect = new PathItem { Name = "rect", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = rect.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 100)));
        vm.Document.Artboards[0].Layers[0].AddItem(rect);
        vm.SelectObject(rect);

        var pane = new TransformPane();
        pane.Attach(vm);
        var window = new Window { Width = 700, Height = 560, Content = pane };
        window.Show();
        Settle();

        try
        {
            double before = rect.BoundingBox().Width;
            pane.WBox.Text = "200mm";
            pane.CommitFromField(pane.WBox);
            Settle();

            Assert.True(rect.BoundingBox().Width > before,
                $"the width should have grown, {before} -> {rect.BoundingBox().Width}");
        }
        finally
        {
            window.Close();
        }
    }
}