using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The type toolbar describes the run the **caret** is in, and says the same thing as the text panel
/// about it (issue #157).
///
/// A block can hold several faces - a heading and a caption in one frame - so "the block's font" is not
/// an answer. The toolbar read the run index and handed it to `TextEditing.RunAt`, which takes a
/// **character offset**: for a block whose first run is non-empty the offset 1 is the second character
/// of the first run, so the toolbar described run 1 for every caret past it. A face change made from a
/// control that shows one run while the caller edits another is the "shows one, edits another" defect.
///
/// Both assertions below read the controls a person sees - the toolbar's own named fields and the
/// panel's <c>RunLabel</c>, which names the run it is describing - after the **gesture**, not after
/// poking the view model. The caret is placed by clicking into the second run, because the offset only
/// becomes real when it is published, and publishing it after the edit was announced is the second
/// half of the same defect.
/// </summary>
public class TextToolbarCaretRunTests
{
    private static (Window Window, EditorView View, EditorViewModel Vm, CanvasWorkspace Workspace,
        TextItem Block, TextPane Pane, string SecondFamily, Window PaneWindow) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        Settle();

        EditorViewModel vm = view.ViewModel;
        CanvasWorkspace workspace = view.WorkspaceControl;

        // The second run is set in a family the chooser really offers, so a toolbar that describes it
        // resolves a row in its own list. The first is deliberately set in a family nothing installs:
        // a toolbar that reads it cannot resolve a row, which makes the two runs impossible to confuse.
        string second = FontCatalog.Families()[0].Name;

        var block = new TextItem { Origin = new Point2D(100, 120) };
        block.Runs.Add(new TextRun
        {
            Text = "TITLE", FontFamily = "No Such Family 157", FontSize = 36, Bold = true,
        });
        block.Runs.Add(new TextRun
        {
            Text = "caption", FontFamily = second, FontSize = 12, Italic = true,
        });

        vm.Document.Artboards[0].Layers[0].AddItem(block);
        vm.SelectObject(block);
        Settle();

        // Attached after the selection so it reads the block, and before the gesture so it sees the
        // edit open - the order the panel's own tests pin.
        var pane = new TextPane();
        pane.Attach(vm);
        var paneWindow = new Window { Width = 440, Height = 780, Content = pane };
        paneWindow.Show();
        Settle();

        return (window, view, vm, workspace, block, pane, second, paneWindow);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Double-clicks into the tail of the block, which is inside the second run.</summary>
    private static void ClickIntoTheSecondRun(CanvasWorkspace workspace, TextItem block)
    {
        Rect2D box = block.BoundingBox();
        workspace.EditAt(new Point2D(box.Right - 4, box.Top + (box.Height / 2)));
        Settle();
    }

    /// <summary>
    /// Moves the caret and makes both views re-read it, the way the application does.
    ///
    /// The caret raises no change of its own - the panel's own `Attach` says so - so the re-read is driven
    /// through the one announcement there is: opening the block is what makes the type controls and the panel
    /// read the caret. Announcing it is therefore the honest stand-in for "the caret moved and something
    /// re-read it", and it exercises the same `Sync` a person's caret movement eventually reaches.
    /// </summary>
    private static void MoveCaretTo(EditorViewModel vm, CanvasWorkspace workspace, int offset)
    {
        workspace.SetTextSelection(offset, offset);
        vm.IsEditingText = false;
        vm.IsEditingText = true;
        Settle();
    }

    private static ComboBox Combo(EditorView view, string name)
        => view.FindControl<ComboBox>(name) ?? throw new Xunit.Sdk.XunitException($"no combo called {name}");

    private static TextBox Box(EditorView view, string name)
        => view.FindControl<TextBox>(name) ?? throw new Xunit.Sdk.XunitException($"no box called {name}");

    private static ToggleButton Toggle(EditorView view, string name)
        => view.FindControl<ToggleButton>(name) ?? throw new Xunit.Sdk.XunitException($"no toggle called {name}");

    /// <summary>
    /// With the caret in the second run the toolbar shows **the second run's** family, size, weight and
    /// slant - not the first run's.
    ///
    /// Against the code this test was written for it showed the first run: size 36, bold on, italic
    /// off, and no family at all, because the family it resolved is one nothing installs.
    /// </summary>
    [AvaloniaFact]
    public void WithTheCaretInTheSecondRunTheToolbarDescribesThatRunsFace()
    {
        (Window window, EditorView view, EditorViewModel vm, CanvasWorkspace workspace, TextItem block,
            _, string second, Window paneWindow) = Host();
        try
        {
            ClickIntoTheSecondRun(workspace, block);

            Assert.True(vm.IsEditingText, "the block should be open - otherwise there is no caret to follow");
            Assert.Equal(1, vm.TextCaretRunIndex);

            // Size first: it is the field that resolves whatever the machine has installed, so its
            // failure is the plain statement "the toolbar showed the first run".
            Assert.Equal("12", Box(view, "TtSize").Text);
            Assert.False(Toggle(view, "TtBold").IsChecked);
            Assert.True(Toggle(view, "TtItalic").IsChecked);

            var choice = Assert.IsType<FontChoice>(Combo(view, "TtFont").SelectedItem);
            Assert.Equal(second, choice.Name);
        }
        finally
        {
            paneWindow.Close();
            window.Close();
        }
    }

    /// <summary>
    /// The toolbar and the text panel must not disagree about the same caret: both describe the second
    /// run.
    ///
    /// `RunLabel` is the panel's own statement of which run its face fields describe, so this compares
    /// the two views rather than comparing either with a value the test worked out. Against the code
    /// this test was written for the panel already said "run 1 of 2" - both views described the first
    /// run, and the caret was in the second.
    /// </summary>
    [AvaloniaFact]
    public void WithTheCaretInTheSecondRunTheToolbarAndThePanelDescribeTheSameRun()
    {
        (Window window, EditorView view, EditorViewModel vm, CanvasWorkspace workspace, TextItem block,
            TextPane pane, string second, Window paneWindow) = Host();
        try
        {
            ClickIntoTheSecondRun(workspace, block);

            Assert.True(vm.IsEditingText, "the block should be open");
            Assert.Equal(1, vm.TextCaretRunIndex);

            // What the panel says it is describing.
            TextBlock label = pane.FindControl<TextBlock>("RunLabel")!;
            Assert.Equal("run 2 of 2, the run the face fields describe", label.Text);
            Assert.Equal("12", pane.FindControl<TextBox>("SizeBox")!.Text);

            // What the toolbar actually describes.
            Assert.Equal("12", Box(view, "TtSize").Text);
            Assert.False(Toggle(view, "TtBold").IsChecked);
            Assert.True(Toggle(view, "TtItalic").IsChecked);

            var choice = Assert.IsType<FontChoice>(Combo(view, "TtFont").SelectedItem);
            Assert.Equal(second, choice.Name);
        }
        finally
        {
            paneWindow.Close();
            window.Close();
        }
    }

    /// <summary>
    /// At the join between the two runs the two views still name the **same** run, which is the run the next
    /// character joins - the one the character before the caret belongs to.
    ///
    /// This is the boundary the two rules used to disagree at: the panel's run index came from "the run the
    /// caret is drawn at" (the second here) while the toolbar asked `RunAt`, whose rule is the run the next
    /// character joins (the first). One caret cannot have two answers, so the plumbing publishes `RunAt`'s -
    /// which is also the run `TextEditing.Insert` puts the next character into.
    /// </summary>
    [AvaloniaFact]
    public void AtTheJoinBetweenTwoRunsTheToolbarAndThePanelNameTheSameRun()
    {
        (Window window, EditorView view, EditorViewModel vm, CanvasWorkspace workspace, TextItem block,
            TextPane pane, _, Window paneWindow) = Host();
        try
        {
            ClickIntoTheSecondRun(workspace, block);

            // Exactly between "TITLE" and "caption".
            MoveCaretTo(vm, workspace, block.Runs[0].Text.Length);

            Assert.Equal(5, vm.TextCaretOffset);
            Assert.Equal(0, vm.TextCaretRunIndex);

            Assert.Equal("run 1 of 2, the run the face fields describe",
                pane.FindControl<TextBlock>("RunLabel")!.Text);
            Assert.Equal("36", Box(view, "TtSize").Text);
            Assert.True(Toggle(view, "TtBold").IsChecked);
            Assert.False(Toggle(view, "TtItalic").IsChecked);
        }
        finally
        {
            paneWindow.Close();
            window.Close();
        }
    }
}
