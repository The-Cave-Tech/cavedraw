using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The stroke pane saying what the PDF export will leave out.
///
/// The list comes from `PdfExportSupport`, which `PdfExportSupportTests` derives from the exported bytes - so a
/// person is told here rather than finding out by opening the export and seeing the effect missing. These assert
/// **both directions**: the warning appears for what is not written and stays away for what is, because a warning
/// that fires on everything is one nobody reads.
/// </summary>
public class StrokePaneExportWarningTests
{
    private static (StrokePane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 400, Height = 500, Content = pane };
        window.Show();
        Settle();
        return (pane, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static TextBlock Warning(StrokePane pane)
        => pane.FindControl<TextBlock>("ExportWarning") ?? throw new Xunit.Sdk.XunitException("no warning block");

    private static PathItem Selected(EditorViewModel viewModel, Action<PathItem>? setup = null)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        setup?.Invoke(path);
        viewModel.SelectObject(path);
        Settle();
        return path;
    }

    [AvaloniaFact]
    public void NothingSelectedShowsNoWarning()
    {
        (StrokePane pane, _) = Host();

        Assert.False(Warning(pane).IsVisible);
    }

    /// <summary>An ordinary stroke exports exactly as drawn, so nothing is said.</summary>
    [AvaloniaFact]
    public void AnOrdinaryStrokeShowsNoWarning()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel);

        Assert.False(Warning(pane).IsVisible);
    }

    [AvaloniaFact]
    public void AnOutlineEffectShowsNoWarningBecauseItIsWritten()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, path => path.Strokes[0] = path.Strokes[0] with
        {
            Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(3, seed: 2) }),
        });

        Assert.False(Warning(pane).IsVisible);
    }

    /// <summary>**A raster effect is not written, so the pane says so** - the case the issue calls the honest one.</summary>
    [AvaloniaFact]
    public void ARasterEffectShowsTheWarning()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, path => path.Strokes[0] = path.Strokes[0] with
        {
            RasterEffects = new RasterEffectStack(new[] { RasterEffectSpec.Blur(4) }),
        });

        TextBlock warning = Warning(pane);

        Assert.True(warning.IsVisible);
        Assert.Contains("Not in the PDF export", warning.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("blur", warning.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A filter and a blend mode are per object, and both are declared unwritten.</summary>
    [AvaloniaFact]
    public void AFilterAndABlendModeShowTheWarning()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, path =>
        {
            path.BlendMode = BlendMode.Multiply;
            viewModel.Document.AddFilter(new FilterSpec("soft", new[]
            {
                FilterPrimitive.Blur(8, input: "SourceGraphic"),
            }));
            path.FilterId = "soft";
        });

        string text = Warning(pane).Text ?? string.Empty;

        Assert.True(Warning(pane).IsVisible);
        Assert.Contains("filter", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blend", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And clearing the selection takes the warning away with it.</summary>
    [AvaloniaFact]
    public void ClearingTheSelectionClearsTheWarning()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, path => path.Strokes[0] = path.Strokes[0] with
        {
            RasterEffects = new RasterEffectStack(new[] { RasterEffectSpec.Blur(4) }),
        });

        Assert.True(Warning(pane).IsVisible);

        viewModel.ClearSelection();
        Settle();

        Assert.False(Warning(pane).IsVisible);
    }
}
