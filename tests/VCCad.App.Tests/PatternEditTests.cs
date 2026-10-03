using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **Editing a pattern's tile changes every shape painted with it** (issues #203 and #135).
///
/// The gap this closes is the one #135's own test list names, and the shape of defect this repository keeps filing:
/// a paint can be *stored* perfectly and still not be *followed*. A fill names the pattern's definition and the tile
/// is resolved by that name when the picture is drawn, so an edit to the definition must show up on every shape that
/// names it - and that is a claim about the drawing, not about the model.
///
/// **Both shapes, and ink.** Two shapes share the pattern, the tile is edited through `definition.redefine` (the
/// operation a person's control uses, not a reach into the model), and the ink is counted on **each side** of the gap
/// between them - so an edit that reached only the first shape fails rather than averaging out. The two halves are
/// found by the widest blank column between ink, which needs no coordinate arithmetic and cannot drift.
/// </summary>
public class PatternEditTests
{
    private static CadDocument Document()
    {
        CadDocument document = CadDocument.CreateDefault("Patterns");
        document.Artboards[0].Width = 320;
        document.Artboards[0].Height = 200;

        ArtGroup tile = document.AddDefinition("dots");
        tile.ForeignAttributes[SvgWriter.PatternDefinitionTag] = "dots";
        tile.ForeignAttributes["width"] = "10";
        tile.ForeignAttributes["height"] = "10";
        tile.ForeignAttributes["patternUnits"] = "userSpaceOnUse";
        tile.AddItem(Dot(2));

        FillSpec fill = FillSpec.Solid(ColorRgb.Black) with
        {
            Pattern = new PatternSpec("dots", 10, 10, 0, 0, "userSpaceOnUse"),
        };

        Square(document, fill, new Point2D(30, 40), "left");
        Square(document, fill, new Point2D(190, 40), "right");
        return document;
    }

    private static PathItem Dot(double size)
    {
        var dot = new PathItem { Name = "dot", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath path = dot.AddSubPath(closed: true);
        path.Nodes.Add(new PathNode(new Point2D(0, 0)));
        path.Nodes.Add(new PathNode(new Point2D(size, 0)));
        path.Nodes.Add(new PathNode(new Point2D(size, size)));
        path.Nodes.Add(new PathNode(new Point2D(0, size)));
        return dot;
    }

    private static void Square(CadDocument document, FillSpec fill, Point2D at, string name)
    {
        var rect = new PathItem { Name = name, Fill = fill };
        SubPath shape = rect.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(at));
        shape.Nodes.Add(new PathNode(new Point2D(at.X + 80, at.Y)));
        shape.Nodes.Add(new PathNode(new Point2D(at.X + 80, at.Y + 80)));
        shape.Nodes.Add(new PathNode(new Point2D(at.X, at.Y + 80)));
        document.Artboards[0].Layers[0].AddItem(rect);
    }

    private static (EditorViewModel ViewModel, CanvasWorkspace Workspace, Window Window) Host(CadDocument document)
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportDocument(document);
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);
        var window = new Window { Width = 760, Height = 560, Content = workspace };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return (viewModel, workspace, window);
    }

    /// <summary>
    /// The ink on each side of the widest blank column, and the frame itself when a capture is wanted. The split is
    /// found from the picture, so it cannot go stale when the fixture moves.
    /// </summary>
    private static (int Left, int Right, byte[] Png) Ink(CadDocument document, CanvasWorkspace workspace)
    {
        workspace.InvalidateVisual();
        PageRenderer.Workspace = workspace;
        byte[] png = PageRenderer.Render(document, 0, 144)!;

        using SKBitmap bitmap = SKBitmap.Decode(png);
        var columns = new bool[bitmap.Width];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 60 && pixel.Green < 60 && pixel.Blue < 60)
                {
                    columns[x] = true;
                }
            }
        }

        // The widest run of blank columns between the first and last ink: the gap between the two shapes.
        int first = Array.IndexOf(columns, true);
        int last = Array.LastIndexOf(columns, true);
        int gapStart = first, gapEnd = first, best = 0;
        for (int x = first; x <= last; x++)
        {
            if (columns[x])
            {
                continue;
            }

            int runStart = x;
            while (x <= last && !columns[x])
            {
                x++;
            }

            if (x - runStart > best)
            {
                best = x - runStart;
                gapStart = runStart;
                gapEnd = x;
            }
        }

        Assert.True(best > 8, $"the two shapes are not separated in the picture (widest gap {best} px)");

        int split = (gapStart + gapEnd) / 2;
        return (Count(bitmap, 0, split), Count(bitmap, split, bitmap.Width), png);
    }

    private static int Count(SKBitmap bitmap, int fromX, int toX)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = fromX; x < toX; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 60 && pixel.Green < 60 && pixel.Blue < 60)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [AvaloniaFact]
    public void EditingTheTileChangesEveryShapeThatUsesIt()
    {
        CadDocument document = Document();
        (EditorViewModel viewModel, CanvasWorkspace workspace, Window window) = Host(document);

        try
        {
            (int leftBefore, int rightBefore, byte[] before) = Ink(document, workspace);

            // A bigger dot in the tile, through the operation a person's redefine control uses - the form the
            // pattern's geometry is edited in, since a definition has no artboard to draw on.
            PathItem bigger = Dot(6);
            document.Artboards[0].Layers[0].AddItem(bigger);
            viewModel.SelectRange(new[] { bigger }, additive: false);

            EditorOperations.Invoke(
                new AutomationContext { ViewModel = viewModel }, "definition.redefine",
                System.Text.Json.JsonSerializer.SerializeToElement(new { name = "dots" }));

            // The tile holds the new dot, and both fills still name the same definition.
            ArtGroup tile = Assert.IsType<ArtGroup>(document.FindDefinition("dots"));
            Assert.Equal("dot", tile.Children.OfType<PathItem>().Single().Name);
            Assert.All(
                document.AllPaths().Where(path => path.Name is "left" or "right"),
                path => Assert.Equal("dots", path.Fill.Pattern?.Definition));

            (int leftAfter, int rightAfter, byte[] after) = Ink(document, workspace);

            // **Every shape**, not on average: a bigger dot covers more of both squares.
            Assert.True(leftAfter > leftBefore * 2,
                $"the left shape did not follow the tile: {leftBefore} -> {leftAfter} ink");
            Assert.True(rightAfter > rightBefore * 2,
                $"the right shape did not follow the tile: {rightBefore} -> {rightAfter} ink");

            if (Environment.GetEnvironmentVariable("VCCAD_CAPTURE") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "frame-000.png"), before);
                File.WriteAllBytes(Path.Combine(directory, "frame-001.png"), after);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
