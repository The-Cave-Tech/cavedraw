using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The width and variant a file states reach the person, not only the driver.**
///
/// #161 made the face a file asks for visible: `font-stretch` and `font-variant` are kept on the run, and
/// because this build chooses a face by family, weight and slant and by neither of them, the run draws in the
/// family's own face. The sentence saying so was put in <c>SvgImportResult.Warnings</c> - which only an
/// operation reply carries, so <c>document.importSvg</c> told a driver and File then Import told nobody. That
/// is the parity rule backwards, and the person is the one looking at the page.
///
/// The news belongs on the font usage summary, which is where a person already learns that a font was
/// substituted or missing: a width that was not honoured is the same kind of fact. <see cref="FontUsage.Warning"/>
/// is the string the status bar appends to whatever it is showing, so the sentence reaches the person through
/// a surface that already exists rather than through a channel invented for this.
///
/// The tests read the sentence from the reader itself and demand it on the person-facing surface, so the two
/// cannot drift into two different sentences - the reader's copy is the one #161 tested.
/// </summary>
public class SvgImportWarningSurfaceTests
{
    /// <summary>A file that asks for a width this build does not select a face by.</summary>
    private const string StatesAWidth =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\">" +
        "<text x=\"0\" y=\"20\" font-size=\"10\" font-stretch=\"semi-condensed\">hi</text></svg>";

    /// <summary>A file that asks for a variant this build does not select a face by.</summary>
    private const string StatesAVariant =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\">" +
        "<text x=\"0\" y=\"20\" font-size=\"10\" font-variant=\"small-caps\">hi</text></svg>";

    /// <summary>An ordinary file: nothing about a width and nothing about a variant.</summary>
    private const string StatesNeither =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\">" +
        "<text x=\"0\" y=\"20\" font-size=\"10\">hi</text></svg>";

    /// <summary>The exact sentence the reader produced for a file, taken from the reader rather than retyped.</summary>
    private static string ReaderSentence(string svg, string property)
        => SvgReader.Read(svg).Warnings.Single(w => w.StartsWith(property + "=", StringComparison.Ordinal));

    private static (EditorView View, Window Window) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Settle();
        return (view, window);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// **A width the file states reaches the font usage surface**, which is what the status bar shows.
    ///
    /// The import goes through <see cref="EditorViewModel.ImportSvg"/> - the path a person's import takes - and
    /// not through the raw reader, because the defect was the channel after the reader, not the reader.
    /// </summary>
    [AvaloniaFact]
    public void AWidthTheFileStatesReachesTheFontUsageSurface()
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportSvg(StatesAWidth);

        string sentence = ReaderSentence(StatesAWidth, "font-stretch");
        string? warning = FontUsage.Warning(viewModel.Document);

        Assert.NotNull(warning);
        Assert.Contains(sentence, warning!, StringComparison.Ordinal);
    }

    /// <summary>A variant the file states is the same news, and reaches the same surface.</summary>
    [AvaloniaFact]
    public void AVariantTheFileStatesReachesTheFontUsageSurface()
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportSvg(StatesAVariant);

        string sentence = ReaderSentence(StatesAVariant, "font-variant");
        string? warning = FontUsage.Warning(viewModel.Document);

        Assert.NotNull(warning);
        Assert.Contains(sentence, warning!, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The status bar itself carries the sentence.** The font usage surface is only worth anything if the
    /// view that composes the status line actually renders it, so this reads the text a person would see.
    /// </summary>
    [AvaloniaFact]
    public void TheStatusBarCarriesTheSentence()
    {
        (EditorView view, Window window) = Host();
        try
        {
            string sentence = ReaderSentence(StatesAWidth, "font-stretch");

            view.ViewModel.ImportSvg(StatesAWidth);
            Settle();

            TextBlock status = view.FindControl<TextBlock>("StatusText")!;
            Assert.Contains(sentence, status.Text ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **The import a driver performs leaves the same news on the person's surface.** `document.importSvgFile`
    /// takes the reply's warnings to the driver; a person watching the status bar has to be told too, or the
    /// two halves of the parity rule disagree about the same file.
    /// </summary>
    [AvaloniaFact]
    public void TheFileImportOperationLeavesTheSameNewsOnThePersonSurface()
    {
        (EditorView view, Window window) = Host();
        try
        {
            string sentence = ReaderSentence(StatesAWidth, "font-stretch");
            string path = Path.Combine(Path.GetTempPath(), $"vccad-stretch-{Guid.NewGuid():N}.svg");
            File.WriteAllText(path, StatesAWidth);

            try
            {
                var context = new AutomationContext { ViewModel = view.ViewModel };
                EditorOperations.Invoke(context, "document.importSvgFile",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { path }));
                Settle();

                TextBlock status = view.FindControl<TextBlock>("StatusText")!;
                Assert.Contains(sentence, status.Text ?? string.Empty, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A file that states no width and no variant carries nothing.** A warning that fires on every import is
    /// worse than none: it is how the one file that really does ask for a width gets ignored. The reader's own
    /// silence is asserted first, then the absence of any face-request news on the person's surface.
    /// </summary>
    [AvaloniaFact]
    public void AFileThatStatesNoWidthOrVariantCarriesNothing()
    {
        Assert.Empty(SvgReader.Read(StatesNeither).Warnings);

        var viewModel = new EditorViewModel();
        viewModel.ImportSvg(StatesNeither);

        Assert.Empty(FontUsage.UnselectedFaceRequests(viewModel.Document));

        // Any other warning is a machine fact (a font the machine cannot supply) and is not this test's
        // business; what must not appear is face-request news on an ordinary file.
        string warning = FontUsage.Warning(viewModel.Document) ?? string.Empty;
        Assert.DoesNotContain("font-stretch", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("font-variant", warning, StringComparison.Ordinal);
    }
}
