using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The compound shape button and its flyout.
///
/// What is covered here is the **structure and the choices**: that one button holds all nine shapes, that
/// it carries the corner bevel, that choosing a shape arms it and closes the flyout, that closing without
/// choosing changes nothing, and that every entry is named so a driver can find it.
///
/// **Not covered yet, and deliberately not faked**: the gesture rules - long press opens, drag-to-choose,
/// release-on-the-button leaves it open, dragging off cancels. Those need synthetic pointer input to reach
/// a control that owns a popup, and my first attempt never reached the handlers at all: the press landed
/// in the wrong place, and having fixed that the events still did not arrive. Writing them as calls to the
/// control's own methods would have tested my own code path rather than the gesture, which is the one
/// thing a test of a gesture must not do. So they are left unwritten and recorded on the issue instead of
/// passing for the wrong reason.
/// </summary>
public class ShapeFlyoutTests
{
    private static (Window Window, ShapeFlyoutButton Button, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var button = new ShapeFlyoutButton { PressMilliseconds = 10 };
        button.Attach(viewModel);

        var window = new Window { Width = 600, Height = 400, Content = button };
        window.Show();
        Settle();
        return (window, button, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Every shape is behind the one button, so the toolbar has one slot for nine tools.</summary>
    [AvaloniaFact]
    public void TheButtonHoldsAllNineShapes()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            Assert.Equal(9, button.Shapes.Count);
            Assert.Equal(ShapeLibrary.All.Count, button.Shapes.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The bevel is the affordance: without it nothing says there is more than one tool here.</summary>
    [AvaloniaFact]
    public void TheButtonCarriesTheCornerBevel()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            Assert.True(button.HasBevel);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Choosing a shape arms that shape and puts the flyout away.</summary>
    [AvaloniaFact]
    public void ChoosingAShapeArmsItAndClosesTheFlyout()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            button.OpenFlyout();
            Assert.True(button.IsFlyoutOpen);

            button.Choose(ShapeKind.Star);

            Assert.Equal(ShapeKind.Star, viewModel.CurrentShape);
            Assert.Equal(EditorTool.Shape, viewModel.Tool);
            Assert.False(button.IsFlyoutOpen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Closing without choosing changes nothing: the flyout never lingers, and cancelling is not a choice.
    /// </summary>
    [AvaloniaFact]
    public void ClosingWithoutChoosingChangesNothing()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            button.OpenFlyout();
            Assert.True(button.IsFlyoutOpen);

            button.CloseFlyout();

            Assert.False(button.IsFlyoutOpen);
            Assert.Equal(ShapeKind.Rectangle, viewModel.CurrentShape);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Every entry is named, so `ui.find` can reach it and a driver can click it. The entries live in a
    /// popup with its own visual tree, which is why they are asked for directly rather than hunted for
    /// among the control's descendants.
    /// </summary>
    [AvaloniaFact]
    public void EveryEntryIsNamed()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            List<string> names = button.Entries.Select(b => b.Name ?? string.Empty).ToList();

            Assert.Equal(ShapeLibrary.All.Count, names.Count);

            foreach (ShapeKind kind in ShapeLibrary.All)
            {
                string name = ShapeLibrary.Name(kind);
                string expected = $"ShapeFlyout{char.ToUpperInvariant(name[0])}{name[1..]}";
                Assert.Contains(expected, names);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
