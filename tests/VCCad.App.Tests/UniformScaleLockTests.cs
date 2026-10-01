using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The uniform-scaling lock in the Transform pane.
///
/// Engaged, editing **W** moves **H** with it and the other way about, so a piece can be sized to a target
/// width without working out the other dimension and typing it. Disengaged, the fields are independent, which
/// is what they were before the lock existed.
///
/// The ratio asserted here is the object's **current** one at the moment of the edit, not a stored number:
/// the lock holds whatever proportion the object has, which is what makes it usable on a selection of pieces
/// that are all different shapes.
/// </summary>
public class UniformScaleLockTests
{
    private static (Window Window, TransformPane Pane, EditorViewModel Vm, PathItem Path) Host()
    {
        var vm = new EditorViewModel();

        // 200 x 100, so the ratio being held is 2:1 and a proportional scale is easy to see.
        var path = new PathItem { Name = "box", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 100)));
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        var pane = new TransformPane();
        pane.Attach(vm);
        var window = new Window { Width = 600, Height = 500, Content = pane };
        window.Show();
        Settle();

        return (window, pane, vm, path);
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

    /// <summary>Locked, a width edit takes the height with it.</summary>
    [AvaloniaFact]
    public void LockedWidthEditsKeepTheRatio()
    {
        (Window window, TransformPane pane, _, PathItem path) = Host();
        try
        {
            pane.UniformLock.IsChecked = true;

            pane.WBox.Text = "400";
            pane.CommitFromField(pane.WBox);
            Settle();

            Rect2D box = path.BoundingBox();
            Assert.Equal(2.0, box.Width / box.Height, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>And the other way about: a height edit takes the width with it.</summary>
    [AvaloniaFact]
    public void LockedHeightEditsKeepTheRatio()
    {
        (Window window, TransformPane pane, _, PathItem path) = Host();
        try
        {
            pane.UniformLock.IsChecked = true;

            pane.HBox.Text = "50";
            pane.CommitFromField(pane.HBox);
            Settle();

            Rect2D box = path.BoundingBox();
            Assert.Equal(2.0, box.Width / box.Height, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Unlocked, a width edit leaves the height exactly where it was.</summary>
    [AvaloniaFact]
    public void UnlockedWidthEditsLeaveTheHeightAlone()
    {
        (Window window, TransformPane pane, _, PathItem path) = Host();
        try
        {
            Assert.NotEqual(true, pane.UniformLock.IsChecked);

            double before = path.BoundingBox().Height;
            pane.WBox.Text = "300";
            pane.CommitFromField(pane.WBox);
            Settle();

            Rect2D box = path.BoundingBox();
            Assert.Equal(before, box.Height, 1);
            Assert.NotEqual(2.0, box.Width / box.Height, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The lock's appearance says which state it is in - not only its tooltip.</summary>
    [AvaloniaFact]
    public void TheLockLooksDifferentWhenEngaged()
    {
        (Window window, TransformPane pane, _, _) = Host();
        try
        {
            pane.UniformLock.IsChecked = false;
            Settle();
            string idle = pane.UniformLock.Background?.ToString() ?? "none";

            pane.UniformLock.IsChecked = true;
            Settle();
            string engaged = pane.UniformLock.Background?.ToString() ?? "none";

            // A pseudo-class is not in `Classes`, so the appearance has to be read from what the style
            // actually resolved to - which is also the thing a person sees.
            Assert.NotEqual(idle, engaged);
        }
        finally
        {
            window.Close();
        }
    }
}
