using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
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

    /// <summary>**A raster effect is written now, so the pane stops warning about it.** It is rasterised into an
    /// image XObject from the stroke side, which is the same route the canvas takes.</summary>
    [AvaloniaFact]
    public void ARasterEffectNoLongerShowsTheWarning()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, path => path.Strokes[0] = path.Strokes[0] with
        {
            RasterEffects = new RasterEffectStack(new[] { RasterEffectSpec.Blur(4) }),
        });

        TextBlock warning = Warning(pane);

        Assert.False(warning.IsVisible);
        Assert.DoesNotContain("blur", warning.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **A leaf item's blend is carried now, so the pane stops warning about it** - the PDF composites it with an
    /// `ExtGState /BM` the item switches to. A filter is carried too, as an image XObject.
    /// </summary>
    [AvaloniaFact]
    public void APathBlendModeAndAFilterNoLongerShowTheWarning()
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

        TextBlock warning = Warning(pane);

        Assert.False(warning.IsVisible);
        Assert.DoesNotContain("blend", warning.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filter", warning.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **A group's blend is carried now, so the pane no longer warns about it.** This test used to assert the
    /// opposite, with a doc comment saying the export could not emit a transparency group; the exporter writes one
    /// now (`PdfFormObjects`), the declaration says so, and the warning is drawn from that declaration.
    /// </summary>
    [AvaloniaFact]
    public void AGroupBlendModeShowsNoWarning()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        SelectedGroup(viewModel);

        TextBlock warning = Warning(pane);

        Assert.False(warning.IsVisible);
        Assert.DoesNotContain("blend", warning.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **And there is nothing left to clear it from.** Every feature the declaration knows about is written now, so
    /// `PdfExportSupport.Lossy` is empty and no selection can raise this warning. The assertion is that the state a
    /// person sees is the same before and after clearing - the mechanism is inert rather than broken - and it says
    /// why, so nobody reads it as the clearing behaviour having been dropped.
    /// </summary>
    [AvaloniaFact]
    public void ClearingTheSelectionChangesNothingBecauseNoFeatureIsUnwritten()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        SelectedGroup(viewModel);

        Assert.Empty(PdfExportSupport.Lossy);
        Assert.False(Warning(pane).IsVisible);

        viewModel.ClearSelection();
        Settle();

        Assert.False(Warning(pane).IsVisible);
    }

    /// <summary>A selected group whose blend the PDF will not carry, with a path inside it so the selection is a
    /// real one.</summary>
    private static ArtGroup SelectedGroup(EditorViewModel viewModel)
    {
        PathItem path = Selected(viewModel);

        var group = new ArtGroup { Name = "group", BlendMode = BlendMode.Multiply };
        viewModel.Document.Artboards[0].Layers[0].RemoveItem(path);
        group.AddItem(path);
        viewModel.Document.Artboards[0].Layers[0].AddItem(group);

        viewModel.SelectObject(group);
        Settle();
        return group;
    }
}
