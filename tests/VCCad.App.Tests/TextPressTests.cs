using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A press with the Text tool opens the text it lands on, rather than starting a new block (#212).
///
/// The tool used to create an object on every press, so clicking a block you could see put a second, empty
/// text beside it and every keystroke went into the new one - the words stayed put and the typing landed
/// somewhere nobody asked for. Whether a press opens an existing block or starts a new one is the decision
/// these tests assert, through the same method the press handler calls.
///
/// The window is closed after every test: three tests in this suite once left live windows in the shared
/// headless session and eighty-four others failed as a result.
/// </summary>
public class TextPressTests : IDisposable
{
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        _window = null;
        Settle();
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private (EditorView View, EditorViewModel Model, TextItem Text) Host()
    {
        var view = new EditorView();
        _window = new Window { Width = 900, Height = 700, Content = view };
        _window.Show();
        Settle();

        EditorViewModel viewModel = view.ViewModel;
        var text = new TextItem { Origin = new Point2D(40, 60) };
        text.Runs.Add(new TextRun { Text = "Alpha Beta", FontSize = 12, FontFamily = "Nimbus Sans" });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        Settle();

        return (view, viewModel, text);
    }

    [AvaloniaFact]
    public void APressOnAnExistingTextOpensItRatherThanStartingANewBlock()
    {
        (EditorView view, _, TextItem text) = Host();
        Controls.CanvasWorkspace canvas = view.WorkspaceControl;

        // A point inside the block's own bounds - where a person would click to put the caret in the words.
        var inside = new Point2D(text.Origin.X + 4, text.Origin.Y + 6);

        Assert.Same(
            text,
            canvas.TextToOpenAt(inside));
    }

    [AvaloniaFact]
    public void APressOnBlankCanvasOpensNothing()
    {
        (EditorView view, _, _) = Host();
        Controls.CanvasWorkspace canvas = view.WorkspaceControl;

        Assert.Null(canvas.TextToOpenAt(new Point2D(400, 500)));
    }
}