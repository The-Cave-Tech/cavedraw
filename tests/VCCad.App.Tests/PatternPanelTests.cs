using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The pattern editor reads the document's patterns and shows one tiling** (issues #203 and #135).
///
/// The list is `pattern.list`'s own answer - the tile box, the units and the transform as the file wrote them, and
/// how many paths are painted with it - so the panel and a driver read the same thing. The picture beside it is
/// drawn by the **canvas's own painter**, so a preview cannot show something the canvas would not.
/// </summary>
public class PatternPanelTests
{
    private static (SymbolsPane Pane, EditorViewModel ViewModel, CadDocument Document) Host()
    {
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;

        ArtGroup tile = document.AddDefinition("dots");
        tile.ForeignAttributes[SvgWriter.PatternDefinitionTag] = "dots";
        tile.ForeignAttributes["width"] = "12";
        tile.ForeignAttributes["height"] = "8";
        tile.ForeignAttributes["patternUnits"] = "userSpaceOnUse";
        tile.ForeignAttributes["patternTransform"] = "rotate(20)";
        // The tile's artwork: a dot, which is what makes a tiling visible at all. A definition with a fill and no
        // geometry draws nothing, and a preview of nothing looks like a preview that does not work.
        var dot = new PathItem { Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath dotPath = dot.AddSubPath(closed: true);
        dotPath.Nodes.Add(new PathNode(new Point2D(1, 1)));
        dotPath.Nodes.Add(new PathNode(new Point2D(5, 1)));
        dotPath.Nodes.Add(new PathNode(new Point2D(5, 5)));
        dotPath.Nodes.Add(new PathNode(new Point2D(1, 5)));
        tile.AddItem(dot);

        var rect = new PathItem
        {
            Name = "rect",
            Fill = FillSpec.Solid(ColorRgb.Black) with
            {
                Pattern = new PatternSpec("dots", 12, 8, 0, 0, "userSpaceOnUse", null, "rotate(20)"),
            },
        };

        SubPath shape = rect.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(20, 20)));
        shape.Nodes.Add(new PathNode(new Point2D(120, 20)));
        shape.Nodes.Add(new PathNode(new Point2D(120, 90)));
        shape.Nodes.Add(new PathNode(new Point2D(20, 90)));
        document.Artboards[0].Layers[0].AddItem(rect);

        var pane = new SymbolsPane();
        var window = new Window { Width = 320, Height = 700, Content = pane };
        window.Show();
        pane.Attach(viewModel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (pane, viewModel, document);
    }

    [AvaloniaFact]
    public void TheListReportsTheTileBoxTheUnitsTheTransformAndWhoUsesIt()
    {
        (SymbolsPane pane, EditorViewModel viewModel, _) = Host();

        JsonElement listed = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            new AutomationContext { ViewModel = viewModel }, "pattern.list", default));

        JsonElement pattern = Assert.Single(listed.EnumerateArray());
        Assert.Equal("dots", pattern.GetProperty("name").GetString());
        Assert.Equal(12.0, pattern.GetProperty("width").GetDouble(), 6);
        Assert.Equal(8.0, pattern.GetProperty("height").GetDouble(), 6);
        Assert.Equal("userSpaceOnUse", pattern.GetProperty("units").GetString());
        Assert.Equal("rotate(20)", pattern.GetProperty("transform").GetString());
        Assert.Equal(1, pattern.GetProperty("tileItems").GetInt32());
        Assert.Equal(1, pattern.GetProperty("usedBy").GetInt32());

        // And the panel is showing it, with the preview pointed at it: the person's half of the same read.
        pane.Refresh();
        Assert.Contains("dots", pane.PatternNames);
        Assert.Equal("dots", pane.PreviewedPattern);
    }

    /// <summary>
    /// Issue #204: **the library after a save and a reload.** The same panel as the fixture above, but showing a
    /// document that has been through `Serialize` and `Deserialize` - the definition, its tile and the pattern's
    /// place in the list all survive, which is what was missing.
    ///
    /// Runs only when <c>VCCAD_CAPTURE</c> names a directory.
    /// </summary>
    [AvaloniaFact]
    public void CaptureTheLibraryAfterAReload()
    {
        string? directory = Environment.GetEnvironmentVariable("VCCAD_CAPTURE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        (_, EditorViewModel first, CadDocument document) = Host();
        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.Serialize(document));

        var viewModel = new EditorViewModel();
        viewModel.ImportDocument(reloaded);
        var pane = new SymbolsPane();
        var window = new Window { Width = 320, Height = 700, Content = pane };
        window.Show();
        pane.Attach(viewModel);
        pane.Refresh();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.Measure(new Avalonia.Size(320, 700));
        window.Arrange(new Avalonia.Rect(0, 0, 320, 700));

        using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize(320, 700), new Avalonia.Vector(96, 96));
        bitmap.Render(window);

        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, "frame-000.png"));
    }

    /// <summary>
    /// Issues #203 and #135: the panel with a pattern selected, its tile preview beside the list.
    ///
    /// Runs only when <c>VCCAD_CAPTURE</c> names a directory, so the suite carries no dead weight: a capture is
    /// evidence for the issue, not an assertion.
    /// </summary>
    [AvaloniaFact]
    public void CaptureThePanel()
    {
        string? directory = Environment.GetEnvironmentVariable("VCCAD_CAPTURE");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        (SymbolsPane pane, _, _) = Host();
        pane.Refresh();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var window = (Window)pane.Parent!;
        window.Measure(new Avalonia.Size(320, 700));
        window.Arrange(new Avalonia.Rect(0, 0, 320, 700));

        using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize(320, 700), new Avalonia.Vector(96, 96));
        bitmap.Render(window);

        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, "frame-000.png"));
    }
}
