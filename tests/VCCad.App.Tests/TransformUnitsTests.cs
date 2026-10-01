using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.Core.Units;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The Transform pane's fields are in the document's own units.
///
/// VCCad stores every coordinate in PDF points, and the pane is a boundary: it shows a length in whatever unit
/// the person has configured and reads one back the same way. It used to pass the model's number straight to a
/// formatter that called its argument millimetres - so an A4 page, which is 841.89 points across, read
/// "841.89mm" - and to read a number back as millimetres when comparing it against an object's width in
/// points, which put a scale of 0.35 into any edit instead of the one that was typed.
/// </summary>
public class TransformUnitsTests
{
    private static (Window Window, TransformPane Pane, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var rect = new PathItem { Name = "rect", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = rect.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 60)));
        vm.Document.Artboards[0].Layers[0].AddItem(rect);
        vm.SelectObject(rect);

        var pane = new TransformPane();
        pane.Attach(vm);
        var window = new Window { Width = 700, Height = 560, Content = pane };
        window.Show();

        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return (window, pane, rect);
    }

    /// <summary>
    /// What the field shows is the model's width **in the configured unit**, and not the model's number with a
    /// unit written after it - which is what it was: an A4 page read "841.89mm" across.
    /// </summary>
    [AvaloniaFact]
    public void TheFieldShowsTheModelLengthInTheConfiguredUnit()
    {
        (Window window, TransformPane pane, _) = Host();
        try
        {
            string expected = UnitSettings.Current.FormatWithUnit(Length.FromPoints(100.0));
            Assert.Equal(expected, pane.WBox.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// And what it reads back is the model's own number, so a value typed as it is shown leaves the object the
    /// size it was - rather than scaling it by the millimetre-to-point factor on every commit.
    /// </summary>
    [AvaloniaFact]
    public void ReadingTheFieldBackGivesTheModelLength()
    {
        (Window window, TransformPane pane, _) = Host();
        try
        {
            Assert.True(TransformPane.TryRead(pane.WBox, out double width));
            Assert.Equal(100.0, width, 2);

            Assert.True(TransformPane.TryRead(pane.HBox, out double height));
            Assert.Equal(60.0, height, 2);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Typing the displayed value straight back is the identity: an entry that does not change what the field
    /// says must not change the object. This is the one that would have caught the factor being applied twice.
    /// </summary>
    [AvaloniaFact]
    public void RetypingWhatIsShownChangesNothing()
    {
        (Window window, TransformPane pane, PathItem rect) = Host();
        try
        {
            Rect2D before = rect.BoundingBox();

            pane.CommitFromField(pane.WBox);
            pane.CommitFromField(pane.HBox);

            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(before.Width, rect.BoundingBox().Width, 2);
            Assert.Equal(before.Height, rect.BoundingBox().Height, 2);
        }
        finally
        {
            window.Close();
        }
    }
}
