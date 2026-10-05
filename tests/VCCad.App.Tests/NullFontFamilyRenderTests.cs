using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A run whose font family is null is **drawn**, not fatal (issue #213).
///
/// The reported crash terminated the process from the paint pass: `FontFamily(Uri, string)` threw
/// `ArgumentNullException` for a null name, on the render path, so one bad run in one frame took the application
/// and any unsaved work with it. The guard was added at that site - and the text metrics kept the same unguarded
/// construction one call away, reached while measuring, which is on the way to drawing.
///
/// These tests render the real canvas and measure a real run, because the crash was in the drawing and the
/// measuring rather than in the arithmetic. A model-level test would have passed throughout: the run was always
/// there, and it was the frame that killed the process.
/// </summary>
public class NullFontFamilyRenderTests
{
    private const int Width = 600;
    private const int Height = 400;

    private static (Window Window, EditorViewModel ViewModel, CanvasWorkspace Workspace) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();
        return (window, viewModel, workspace);
    }

    private static void Settle()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static TextItem TextWith(EditorViewModel viewModel, string? family)
    {
        Artboard board = viewModel.Document.Artboards[0];
        var item = new TextItem { Name = "null family" };
        item.Origin = new Point2D(board.X + 30.0, board.Y + 60.0);
        item.Runs.Add(new TextRun { Text = "Drawn anyway", FontFamily = family!, FontSize = 36.0 });
        board.Layers[0].AddItem(item);
        return item;
    }

    /// <summary>Renders the window and counts the pixels that carry ink.</summary>
    private static int Ink(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        target.Render(window);

        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        System.Runtime.InteropServices.GCHandle handle =
            System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        int dark = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] < 100 && pixels[i + 1] < 100 && pixels[i + 2] < 100)
            {
                dark++;
            }
        }

        return dark;
    }

    /// <summary>
    /// The crash itself: a paint pass with a null-family run used to end the process. It now draws the run in the
    /// face the standard-font chain supplies, and the picture proves the pass ran rather than merely not throwing.
    /// </summary>
    [AvaloniaFact]
    public void ARunWithNoFamilyIsPainted()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace _) = Host();

        TextWith(viewModel, null);
        Settle();

        int ink = Ink(window);
        Assert.True(ink > 0, $"the run drew nothing: {ink} dark pixels");

        window.Close();
    }

    /// <summary>A blank name is the same case as a null one, and it reaches the same constructor.</summary>
    [AvaloniaFact]
    public void ARunWithABlankFamilyIsPaintedToo()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace _) = Host();

        TextWith(viewModel, "   ");
        Settle();

        Assert.True(Ink(window) > 0, "the run drew nothing");

        window.Close();
    }

    /// <summary>
    /// The second site, and the one this issue's guard did not cover: the metrics built a FontFamily straight from
    /// the resolver, so measuring a run with no family threw where drawing now does not.
    /// </summary>
    [Fact]
    public void MeasuringARunWithNoFamilyDoesNotThrow()
    {
        var metrics = new AvaloniaTextMetrics();
        var run = new TextRun { Text = "Measure me", FontFamily = null!, FontSize = 24.0 };

        IReadOnlyList<double> advances = metrics.Advances(run);

        Assert.Equal(run.Text.Length, advances.Count);
        Assert.All(advances, advance => Assert.True(advance >= 0.0));
    }

    /// <summary>The resolver answers for every way a name can be missing, and answers with a real family.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheResolverAnswersForAMissingName(string? family)
    {
        var run = new TextRun { Text = "x", FontFamily = family! };

        string name = FontFamilyResolver.NameFor(run);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.NotNull(FontFamilyResolver.For(run));
    }
}
