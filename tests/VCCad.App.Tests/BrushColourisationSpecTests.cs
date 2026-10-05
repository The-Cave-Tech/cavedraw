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
/// A brush's colourisation is honoured where the art is built (issue #214).
///
/// The member was recorded, serialised, editable from the brush pane and reported by `brush.placements`, and no
/// renderer read it: `Colourisation` appeared in the model, the serializer, the dump and the operations, and in
/// none of the drawing code. A black asset drawn with a red stroke stayed black.
///
/// **This asserts a value, not a picture.** `PlacedArt.Resolve` is the one place a brush stroke becomes artwork -
/// the canvas and the exporter both draw what it returns - so the recolouring belongs there and can be read
/// directly. Pixel counts were tried first and cost four rounds: a counter that did not skip transparent pixels
/// read the empty frame as near-black, and a bitmap smaller than the artwork measured nothing. A value cannot lie
/// in that way, and the rendered behaviour is recorded on the issue as two decoded screenshots.
/// </summary>
public class BrushColourisationTests
{
    /// <summary>The colour the stroke is drawn in, and therefore the tint.</summary>
    private static readonly ColorRgb Ink = new(0.86, 0.12, 0.12);

    private static (EditorViewModel ViewModel, PathItem Stroke) ArtStroke(ArtColourisation how)
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 600, Height = 500, Content = workspace };
        window.Show();
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        Artboard board = viewModel.Document.Artboards[0];

        var asset = new PathItem { Name = "tile", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath tile = asset.AddSubPath(closed: true);
        tile.Nodes.Add(new PathNode(new Point2D(board.X + 10, board.Y + 10)));
        tile.Nodes.Add(new PathNode(new Point2D(board.X + 30, board.Y + 10)));
        tile.Nodes.Add(new PathNode(new Point2D(board.X + 30, board.Y + 30)));
        tile.Nodes.Add(new PathNode(new Point2D(board.X + 10, board.Y + 30)));
        board.Layers[0].AddItem(asset);

        var path = new PathItem { Name = "stroke" };
        SubPath line = path.AddSubPath(closed: false);
        line.Nodes.Add(new PathNode(new Point2D(board.X + 60, board.Y + 200)));
        line.Nodes.Add(new PathNode(new Point2D(board.X + 420, board.Y + 200)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, Ink, 40, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Stroke = path.Stroke with
        {
            Brush = BrushSpec.Art("Tinted", asset.Id, size: 40, ArtStretch.Repeat) with { Colourisation = how },
        };
        board.Layers[0].AddItem(path);

        window.Close();
        return (viewModel, path);
    }

    private static PathItem Placed(EditorViewModel viewModel, PathItem path)
    {
        IReadOnlyList<PlacedArt> art = PlacedArt.Resolve(
            viewModel.Document, path, path.Stroke.Brush!, 1.0, path.Stroke.Pen);

        Assert.NotEmpty(art);
        return Assert.IsType<PathItem>(art[0].Asset);
    }

    /// <summary>With no colourisation the artwork is placed as it is.</summary>
    [AvaloniaFact]
    public void WithoutAColourisationTheArtworkIsPlacedUnchanged()
    {
        (EditorViewModel viewModel, PathItem path) = ArtStroke(ArtColourisation.None);

        ColorRgb paint = Placed(viewModel, path).Fill!.Color;

        Assert.Equal(ColorRgb.Black.R, paint.R, 3);
        Assert.Equal(ColorRgb.Black.G, paint.G, 3);
        Assert.Equal(ColorRgb.Black.B, paint.B, 3);
    }

    /// <summary>A tint replaces the artwork's paint with the stroke's colour.</summary>
    [AvaloniaFact]
    public void ATintPlacesTheArtworkInTheStrokesColour()
    {
        (EditorViewModel viewModel, PathItem path) = ArtStroke(ArtColourisation.Tint);

        ColorRgb paint = Placed(viewModel, path).Fill!.Color;

        Assert.Equal(Ink.R, paint.R, 3);
        Assert.Equal(Ink.G, paint.G, 3);
        Assert.Equal(Ink.B, paint.B, 3);
    }

    /// <summary>
    /// Tint-and-shade places each paint between the brush's shade colour and the tint by its own luminance, so a
    /// black asset stays black - the shade is what the artwork's own darkness becomes.
    /// </summary>
    [AvaloniaFact]
    public void TintAndShadeKeepsABlackAssetBlack()
    {
        (EditorViewModel viewModel, PathItem path) = ArtStroke(ArtColourisation.TintAndShade);

        ColorRgb paint = Placed(viewModel, path).Fill!.Color;

        Assert.Equal(ColorRgb.Black.R, paint.R, 3);
        Assert.Equal(ColorRgb.Black.G, paint.G, 3);
        Assert.Equal(ColorRgb.Black.B, paint.B, 3);
    }

    /// <summary>
    /// The asset in the document is left alone: the recolouring is a copy, so a brush drawn in red does not repaint
    /// the artwork other strokes may be drawing untouched.
    /// </summary>
    [AvaloniaFact]
    public void TheDocumentAssetIsNotRepainted()
    {
        (EditorViewModel viewModel, PathItem path) = ArtStroke(ArtColourisation.Tint);

        ColorRgb placed = Placed(viewModel, path).Fill!.Color;
        ColorRgb original = viewModel.Document.AllItems().OfType<PathItem>()
            .First(item => item.Name == "tile").Fill!.Color;

        Assert.Equal(Ink.R, placed.R, 3);
        Assert.Equal(ColorRgb.Black.R, original.R, 3);
    }
}
