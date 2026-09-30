using VCCad.Core.Model;
using VCCad.Core.Picking;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A click picks a path by its geometry - the outline - and not by what its fill encloses.
///
/// Picking by the filled region makes a large panel swallow every click over the lines and labels
/// drawn on top of it: the panel contains the point, so its distance is zero and it wins against
/// whatever the pointer is actually on. The artwork is the paths, so the outline is what a click
/// aims at.
/// </summary>
public class OutlinePickTests
{
    private static PathItem Box(string name, double x, double y, double size)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
    }

    /// <summary>A big filled panel with a label printed across it, drawn on top.</summary>
    private static (CadDocument Document, PathItem Panel, TextItem Label) PanelWithLabel()
    {
        var document = new CadDocument();
        Artboard page = document.AddArtboard(new Size2D(600, 800), "Page 1", new Point2D(0, 0));
        Layer layer = page.AddLayer("Artwork");

        PathItem panel = Box("panel", 0, 0, 400);
        layer.AddItem(panel);

        var label = new TextItem { Name = "label", Origin = new Point2D(180, 180) };
        label.Runs.Add(new TextRun { Text = "PRIYANKA SET", FontSize = 12 });
        layer.AddItem(label);

        return (document, panel, label);
    }

    [Fact]
    public void AClickOnALabelPicksTheLabelNotThePanelFilledUnderIt()
    {
        (CadDocument document, _, TextItem label) = PanelWithLabel();

        // Inside the label and well inside the panel: the case that was reported.
        Point2D onLabel = new(190, 186);
        Assert.True(label.BoundingBox().Contains(onLabel), "the fixture must be aiming at the label");

        SelectionResult result = SelectionEngine.Click(document, onLabel);

        Assert.Same(label, Assert.Single(result.Items));
    }

    [Fact]
    public void AFilledShapeIsStillPickedByItsOutline()
    {
        (CadDocument document, PathItem panel, _) = PanelWithLabel();

        // On the panel's left edge, clear of the label.
        SelectionResult result = SelectionEngine.Click(document, new Point2D(0, 300));

        Assert.Same(panel, Assert.Single(result.Items));
    }

    [Fact]
    public void AClickInsideAFilledShapeAwayFromItsOutlinePicksNothing()
    {
        (CadDocument document, _, _) = PanelWithLabel();

        // The panel's middle: near no edge and near no label.
        SelectionResult result = SelectionEngine.Click(document, new Point2D(320, 320));

        Assert.Empty(result.Items);
    }

    [Fact]
    public void ThePathPickTestIsOutlineOnlyByDefault()
    {
        PathItem rect = Box("rect", 0, 0, 100);
        rect.Fill = FillSpec.Solid(ColorRgb.Black);

        // In the middle: inside the fill, nowhere near the path.
        Assert.Equal(PickKind.None, PathPicking.HitTest(rect, new Point2D(50, 50), 2.0));

        // On the outline.
        Assert.Equal(PickKind.Outline, PathPicking.HitTest(rect, new Point2D(0, 50), 2.0));

        // The filled-region rule is still available for a caller that wants it.
        Assert.Equal(PickKind.Fill,
            PathPicking.HitTest(rect, new Point2D(50, 50), 2.0, pickInsideFill: true));
    }

    /// <summary>
    /// An unstroked fill still has an edge, and that edge is what a click aims at; before, only the
    /// fill area was pickable for a shape with no stroke.
    /// </summary>
    [Fact]
    public void AShapeWithNoStrokeIsPickedByItsOutline()
    {
        PathItem rect = Box("rect", 0, 0, 100);
        rect.Stroke = StrokeSpec.None;

        Assert.Equal(PickKind.Outline, PathPicking.HitTest(rect, new Point2D(50, 0), 2.0));
    }
}
