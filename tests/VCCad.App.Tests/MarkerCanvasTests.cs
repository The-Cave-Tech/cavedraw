using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.Views;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The canvas draws a marker from its definition** (issue #202).
///
/// The fixture has **no materialised artwork at all**: a path that names a marker, and the marker in the document's
/// library. Ink can therefore only appear where the canvas placed the definition itself - a document whose reference
/// is drawn reads here, and one whose reference is ignored draws nothing.
///
/// The second half is the double-draw: the reader materialises an arrowhead beside its path and tags it, and a
/// canvas that drew both the tag and the definition would show two arrowheads. The tagged group is deliberately
/// larger than the definition, so the ink's box says which of them was drawn.
/// </summary>
public class MarkerCanvasTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host(CadDocument document)
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportDocument(document);

        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>A library marker: a filled box `size` units across, referenced at its own top-left.</summary>
    private static ArtGroup Marker(CadDocument document, string name, double size)
    {
        ArtGroup definition = document.AddDefinition(name);
        definition.ForeignAttributes["markerWidth"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        definition.ForeignAttributes["markerHeight"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        definition.ForeignAttributes["markerUnits"] = "userSpaceOnUse";

        var path = new PathItem { Name = "marker", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, size)));
        sub.Nodes.Add(new PathNode(new Point2D(0, size)));
        definition.AddItem(path);
        return definition;
    }

    private static PathItem Line(CadDocument document, string? markerEnd)
    {
        var path = new PathItem
        {
            Name = "line",
            Stroke = StrokeSpec.Hairline(ColorRgb.Black),
            MarkerEnd = markerEnd,
        };

        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(20, 50)));
        sub.Nodes.Add(new PathNode(new Point2D(120, 50)));
        document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static (Rect2D Box, int Count) Ink(CadDocument document, CanvasWorkspace workspace)
    {
        workspace.InvalidateVisual();
        PageRenderer.Workspace = workspace;
        byte[]? png = PageRenderer.Render(document, 0, 72);
        Assert.NotNull(png);

        using SKBitmap bitmap = SKBitmap.Decode(png!);
        double left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        int count = 0;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);

                // **Black artwork only, at the threshold the other canvas harness uses.** The canvas draws its own
                // background and the artboard's edge, and both are far lighter than this - a looser test caught the
                // whole frame, which is what the first attempt at this measurement did.
                if (pixel.Red >= 60 || pixel.Green >= 60 || pixel.Blue >= 60)
                {
                    continue;
                }

                count++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return (new Rect2D(left, top, right - left, bottom - top), count);
    }

    [AvaloniaFact]
    public void TheCanvasDrawsAMarkerFromItsDefinition()
    {
        var document = CadDocument.CreateDefault("Markers");
        Marker(document, "arrow", 20);
        Line(document, "arrow");

        (Window window, CanvasWorkspace workspace, _) = Host(document);
        try
        {
            (Rect2D box, int count) = Ink(document, workspace);

            // Ink exists at all: there is no materialised artwork in this document, so the arrowhead can only be
            // the definition the canvas placed. And it is **at the end vertex**, 20 units across.
            Assert.True(count > 0, "the canvas drew nothing for a marker reference");
            Assert.True(box.Right >= 120, $"the marker should sit at the end vertex, and the ink ends at {box.Right}");
            // One pixel per point at 72 dpi, so the answer can only be a pixel out: the far edge of the box is\n            // anti-aliased and a strict ink threshold loses it.\n            Assert.Equal(20.0, box.Width, 1.0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheMaterialisedArtworkIsNotDrawnAsWell()
    {
        var document = CadDocument.CreateDefault("Markers");
        Marker(document, "arrow", 20);
        PathItem line = Line(document, "arrow");

        // The shadow the reader leaves: tagged as this slot and marker, and deliberately **much larger**, so the
        // ink's box says whether it was drawn.
        var shadow = new ArtGroup { Name = "arrow" };
        var big = new PathItem { Name = "big", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = big.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(80, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(80, 80)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 80)));
        shadow.AddItem(big);
        shadow.ForeignAttributes[SvgWriterTag] = "End arrow";
        document.Artboards[0].Layers[0].AddItem(shadow);

        (Window window, CanvasWorkspace workspace, _) = Host(document);
        try
        {
            (Rect2D box, _) = Ink(document, workspace);

            // The definition's box, not the shadow's: one arrowhead, and the one the reference names.
            // One pixel per point at 72 dpi, so the answer can only be a pixel out: the far edge of the box is\n            // anti-aliased and a strict ink threshold loses it.\n            Assert.Equal(20.0, box.Width, 1.0);
            Assert.True(box.Height <= 21.0, $"the 80-unit shadow was drawn as well: ink is {box.Height} tall");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ChangingTheReferenceChangesWhatIsDrawn()
    {
        var document = CadDocument.CreateDefault("Markers");
        Marker(document, "small", 10);
        Marker(document, "large", 40);
        PathItem line = Line(document, "small");

        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host(document);
        try
        {
            (Rect2D before, _) = Ink(document, workspace);
            Assert.Equal(10.0, before.Width, 1.0);

            // Through the operation a person's control uses, not by reaching into the model.
            EditorOperations.Invoke(
                new AutomationContext { ViewModel = viewModel }, "marker.set",
                JsonSerializer.SerializeToElement(new { slot = "end", name = "large", itemIds = new[] { line.Id } }));

            (Rect2D after, _) = Ink(document, workspace);
            Assert.Equal(40.0, after.Width, 1.0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EachSlotIsDrawnAtItsOwnVertices()
    {
        // A path with a start vertex, one interior vertex and an end vertex, so all three slots have somewhere to
        // go. #135 asks for a marker to be assignable to **each of the three slots** and the canvas to show it at
        // the corresponding vertices; this is that, slot by slot, through the operation a person's control uses.
        var document = CadDocument.CreateDefault("Markers");
        Marker(document, "box", 10);

        var path = new PathItem { Name = "bend", Stroke = StrokeSpec.Hairline(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(20, 50)));
        sub.Nodes.Add(new PathNode(new Point2D(70, 50)));
        sub.Nodes.Add(new PathNode(new Point2D(70, 100)));
        document.Artboards[0].Layers[0].AddItem(path);

        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host(document);
        try
        {
            var context = new AutomationContext { ViewModel = viewModel };
            (_, int none) = Ink(document, workspace);

            // The end slot: at (70, 100), the last vertex.
            EditorOperations.Invoke(context, "marker.set",
                JsonSerializer.SerializeToElement(new { slot = "end", name = "box", itemIds = new[] { path.Id } }));
            (Rect2D end, int withEnd) = Ink(document, workspace);
            Assert.True(withEnd > none, "the end marker was not drawn");
            Assert.True(end.Bottom > 100, $"the end marker should be at the last vertex, and the ink ends at {end.Bottom}");

            // The start slot: at (20, 50), which is what pulls the ink's left edge back.
            EditorOperations.Invoke(context, "marker.set",
                JsonSerializer.SerializeToElement(new { slot = "start", name = "box", itemIds = new[] { path.Id } }));
            (Rect2D both, int withStart) = Ink(document, workspace);
            Assert.True(withStart > withEnd, "the start marker was not drawn as well");
            Assert.True(both.Left < 22, $"the start marker should be at the first vertex, and the ink starts at {both.Left}");

            // The mid slot: at (70, 50), the interior vertex - and only there, because two segments have one.
            EditorOperations.Invoke(context, "marker.set",
                JsonSerializer.SerializeToElement(new { slot = "mid", name = "box", itemIds = new[] { path.Id } }));
            (_, int withMid) = Ink(document, workspace);
            Assert.True(withMid > withStart, "the mid marker was not drawn");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The tag the reader leaves on materialised artwork, from <see cref="VCCad.Core.Svg.SvgWriter"/>.</summary>
    private const string SvgWriterTag = VCCad.Core.Svg.SvgWriter.MarkerArtTag;
}
