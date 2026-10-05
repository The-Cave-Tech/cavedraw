using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Typing into an imported block extends its box; typing into it does not collapse it (issue #246).
///
/// The person's report on the Lillie page header was that typing one character made the edit box "miniscule" with
/// the text "compressed to fit in the box", and that the behaviour should match the other tool they compared with:
/// the rectangle extends to the right to make room and shortens when text is deleted, keeping the metrics the text
/// has been using.
///
/// **One character's worth, measured in the same face.** The absolute numbers here are the face this machine
/// resolves, which is not necessarily the one the PDF measured, so the expectation is built from a block of that
/// one character laid out naturally rather than from the file's own advance figures.
/// </summary>
public class TypingGrowsTheEditBoxTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 1000, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The Lillie header as the importer builds it: seven pieces, each with the advance the file stated.</summary>
    private static TextItem ImportedHeader(EditorViewModel viewModel)
    {
        var text = new TextItem { Name = "header", Origin = new Point2D(60, 80) };
        Add(text, "Jalie ", 16.8574);
        Add(text, "3464 ", 19.406);
        Add(text, "- ", 3.599);
        Add(text, "LILLIE ", 22.052);
        Add(text, "- ", 3.599);
        Add(text, "Page ", 19.318);
        Add(text, "1/12", 18.061);
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        Settle();
        return text;

        static void Add(TextItem text, string piece, double advance)
            => text.Runs.Add(new TextRun
            {
                Text = piece,
                FontFamily = "Nimbus Sans",
                FontSize = 9,
                AdvanceWidth = advance,
            });
    }

    /// <summary>What one character is worth in this face, laid out on its own.</summary>
    private static double OneCharacterWidth(CanvasWorkspace workspace, EditorViewModel viewModel, char c)
    {
        var fresh = new TextItem { Name = "probe", Origin = new Point2D(700, 80) };
        fresh.Runs.Add(new TextRun { Text = c.ToString(), FontFamily = "Nimbus Sans", FontSize = 9 });
        viewModel.Document.Artboards[0].Layers[0].AddItem(fresh);
        Settle();
        return BoxWidth(workspace, fresh);
    }

    /// <summary>The width of the drawn edit box, from the same handles the canvas paints.</summary>
    private static double BoxWidth(CanvasWorkspace workspace, TextItem text)
    {
        IReadOnlyList<Point2D> handles = workspace.EditBoxHandlesWorld(text);
        Assert.Equal(5, handles.Count);

        // The first three handles are the right edge, so their x is the box's width in the block's own frame.
        return handles[0].X - text.Origin.X;
    }

    [AvaloniaFact]
    public void TypingExtendsTheBoxByTheCharacter()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        TextItem text = ImportedHeader(viewModel);
        double before = BoxWidth(workspace, text);
        Assert.True(before > 50, $"the imported block's box is only {before:0.#} wide to begin with");

        // What a keystroke does to the model, through the same editing operation the canvas uses.
        TextEditing.Insert(text, 8, "5");
        Settle();
        double after = BoxWidth(workspace, text);

        double character = OneCharacterWidth(workspace, viewModel, '5');

        Assert.True(after > before, $"the box did not grow: {before:0.#} -> {after:0.#}");
        Assert.True(
            Math.Abs((after - before) - character) < character * 0.25,
            $"the box grew by {after - before:0.#} where one character is {character:0.#} " +
            $"({before:0.#} -> {after:0.#})");

        // And the collapse this issue is about: a sixth of the width, not a hair either side of one character.
        Assert.True(after > before * 0.8, $"the box collapsed: {before:0.#} -> {after:0.#}");

        window.Close();
    }

    [AvaloniaFact]
    public void DeletingShrinksTheBoxByTheCharacter()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        TextItem text = ImportedHeader(viewModel);
        double before = BoxWidth(workspace, text);

        // Index 8 is the '6' of "3464".
        TextEditing.DeleteRange(text, 8, 9);
        Settle();
        double after = BoxWidth(workspace, text);

        Assert.True(after < before, $"the box did not shrink: {before:0.#} -> {after:0.#}");

        // **A deleted character takes its share of what the file stated, not the fallback face's width.** The
        // stored advance for "3464 " is 19.4 units over five characters - 3.9 each - while this machine's face
        // lays a character out at 5.4. Comparing against 5.4 would call the correct 3.9 short. The yardstick is
        // the block's own average character: what the edit removed was one of them.
        double average = before / 31;
        double shrank = before - after;
        Assert.True(
            shrank > average * 0.5 && shrank < average * 1.5,
            $"the box shrank by {shrank:0.#} where the block's own character averages {average:0.#} " +
            $"({before:0.#} -> {after:0.#})");

        window.Close();
    }
}
