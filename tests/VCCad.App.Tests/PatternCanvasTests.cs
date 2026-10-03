using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The canvas paints a pattern by repeating its tile** (issue #203).
///
/// Ink, and the *amount* of it, is what tells the three possible pictures apart: a solid fill would be the whole
/// shape (1600 px for this fixture), a single copy of the tile would be a handful of pixels, and a tiled fill is a
/// small fraction spread across it. The fixture's tile is a 2x2 dot in a 10x10 box, so about 4% of the shape.
/// </summary>
public class PatternCanvasTests
{
    private static CadDocument Document(bool withPattern)
    {
        CadDocument document = CadDocument.CreateDefault("Patterns");
        document.Artboards[0].Width = 300;
        document.Artboards[0].Height = 300;

        ArtGroup tile = document.AddDefinition("dots");
        tile.ForeignAttributes[SvgWriter.PatternDefinitionTag] = "dots";
        tile.ForeignAttributes["width"] = "10";
        tile.ForeignAttributes["height"] = "10";
        tile.ForeignAttributes["patternUnits"] = "userSpaceOnUse";

        var dot = new PathItem { Name = "dot", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath dotPath = dot.AddSubPath(closed: true);
        dotPath.Nodes.Add(new PathNode(new Point2D(0, 0)));
        dotPath.Nodes.Add(new PathNode(new Point2D(2, 0)));
        dotPath.Nodes.Add(new PathNode(new Point2D(2, 2)));
        dotPath.Nodes.Add(new PathNode(new Point2D(0, 2)));
        tile.AddItem(dot);

        FillSpec fill = withPattern
            ? FillSpec.Solid(ColorRgb.Black) with
            {
                Pattern = new PatternSpec("dots", 10, 10, 0, 0, "userSpaceOnUse"),
            }
            : FillSpec.Solid(ColorRgb.Black);

        var rect = new PathItem { Name = "rect", Fill = fill };
        SubPath shape = rect.AddSubPath(closed: true);
        shape.Nodes.Add(new PathNode(new Point2D(20, 20)));
        shape.Nodes.Add(new PathNode(new Point2D(60, 20)));
        shape.Nodes.Add(new PathNode(new Point2D(60, 60)));
        shape.Nodes.Add(new PathNode(new Point2D(20, 60)));
        document.Artboards[0].Layers[0].AddItem(rect);

        return document;
    }

    private static int Ink(CadDocument document)
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportDocument(document);
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);
        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        workspace.InvalidateVisual();
        PageRenderer.Workspace = workspace;
        using SKBitmap bitmap = SKBitmap.Decode(PageRenderer.Render(document, 0, 72)!);

        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 60 && pixel.Green < 60 && pixel.Blue < 60)
                {
                    count++;
                }
            }
        }

        window.Close();
        return count;
    }

    [AvaloniaFact]
    public void APatternFillsTheShapeWithRepeatedTiles()
    {
        int patterned = Ink(Document(withPattern: true));
        int solid = Ink(Document(withPattern: false));

        // The shape really is 40 by 40: without a pattern it paints that whole square.
        Assert.InRange(solid, 1500, 1700);

        // **Repeated, and not one copy.** A single tile is about 4 px and a solid fill about 1600; a grid of 2x2 dots
        // in 10x10 tiles over a 40x40 shape is 16 of them. Each dot loses its edge pixels to anti-aliasing at this
        // ink threshold - the measured figure is 66 - so the floor is set where one or two tiles cannot reach it.
        Assert.InRange(patterned, 30, 700);
    }
}
