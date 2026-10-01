using VCCad.App.ViewModels;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What travels with an object when it is scaled.
///
/// A stroke width, a corner radius and a font size are each distances or sizes in the document, so a scale
/// that leaves them behind makes the drawing disagree with its own geometry. The defaults are on, because a
/// person who scales a shape and finds a hairline outline where there was a drawn one has been surprised by
/// the wrong default.
/// </summary>
public class ScaleWithObjectTests
{
    private static PathItem Box(string name, double size)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4.0);
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, size)));
        sub.Nodes.Add(new PathNode(new Point2D(0, size)));
        return path;
    }

    private static DocumentSession Session()
    {
        var document = CadDocument.CreateDefault("Page");
        document.Artboards[0].AddLayer("Layer 1");
        var session = new DocumentSession();
        session.Initialize(document);
        return session;
    }

    private static void Scale(DocumentSession session, double scaleX, double scaleY)
        => session.ApplyTransform(new Point2D(0, 0), new Vector2D(0, 0), scaleX, scaleY, 0);

    /// <summary>Line weights scale with the object, which is the default.</summary>
    [Fact]
    public void LineWeightsScaleByTheFactor()
    {
        DocumentSession session = Session();
        PathItem path = Box("box", 100);
        session.Document.Artboards[0].Layers[0].AddItem(path);
        session.SelectObject(path);

        Scale(session, 2, 2);

        Assert.Equal(4.0, path.Stroke.Width, 3);
        Assert.Equal(200.0, path.BoundingBox().Width, 1);
    }

    /// <summary>With the option off the width is untouched, which is what it used to be always.</summary>
    [Fact]
    public void LineWeightsAreLeftAloneWhenTheOptionIsOff()
    {
        DocumentSession session = Session();
        PathItem path = Box("box", 100);
        session.Document.Artboards[0].Layers[0].AddItem(path);
        session.SelectObject(path);
        session.ScaleOptions.LineWeights = false;

        Scale(session, 2, 2);

        Assert.Equal(2.0, path.Stroke.Width, 3);
    }

    /// <summary>
    /// An anisotropic scale has no single factor. The geometric mean is the one that neither doubles a
    /// stroke nor leaves it alone, and a uniform scale returns that factor exactly.
    /// </summary>
    [Fact]
    public void AnAnisotropicScaleUsesTheGeometricMean()
    {
        DocumentSession session = Session();
        PathItem path = Box("box", 100);
        session.Document.Artboards[0].Layers[0].AddItem(path);
        session.SelectObject(path);

        Scale(session, 2, 1);

        Assert.Equal(2.0 * Math.Sqrt(2.0), path.Stroke.Width, 3);
    }

    /// <summary>Scaling twice and undoing once puts both the geometry and the width back.</summary>
    [Fact]
    public void OneUndoRestoresTheGeometryAndTheWidth()
    {
        DocumentSession session = Session();
        PathItem path = Box("box", 100);
        session.Document.Artboards[0].Layers[0].AddItem(path);
        session.SelectObject(path);

        Scale(session, 2, 2);
        session.Undo();

        Assert.Equal(2.0, path.Stroke.Width, 3);
        Assert.Equal(100.0, path.BoundingBox().Width, 1);
    }

    /// <summary>Type inside a frame scales with it, which is the difference between enlarging a label and reflowing one.</summary>
    [Fact]
    public void TextContentsScaleWithTheFrame()
    {
        DocumentSession session = Session();
        var text = new TextItem { Origin = new Point2D(0, 0) };
        text.Runs.Add(new TextRun { Text = "12", FontSize = 12 });
        text.Runs.Add(new TextRun { Text = "8", FontSize = 8 });
        session.Document.Artboards[0].Layers[0].AddItem(text);
        session.SelectObject(text);

        Scale(session, 2, 2);

        // Per run, not one size for the block: a heading and a caption in the same frame are different sizes.
        Assert.Equal(24.0, text.Runs[0].FontSize, 3);
        Assert.Equal(16.0, text.Runs[1].FontSize, 3);
        Assert.Equal(0.0, text.Origin.X, 3);
    }

    /// <summary>With the option off the type is unchanged and the frame moves.</summary>
    [Fact]
    public void TextContentsAreLeftAloneWhenTheOptionIsOff()
    {
        DocumentSession session = Session();
        var text = new TextItem { Origin = new Point2D(100, 0) };
        text.Runs.Add(new TextRun { Text = "12", FontSize = 12 });
        session.Document.Artboards[0].Layers[0].AddItem(text);
        session.SelectObject(text);
        session.ScaleOptions.TextFrameContents = false;

        Scale(session, 2, 2);

        Assert.Equal(12.0, text.Runs[0].FontSize, 3);
        Assert.Equal(200.0, text.Origin.X, 3);
    }

    /// <summary>
    /// **Shape corners are geometry here, and geometry scales.** A rounded corner is baked into the outline
    /// when it is rounded, so the corner travels with the shape without a flag of its own. This measures the
    /// corner's offset from the bounding box and shows it doubling - the behaviour, not the claim.
    /// </summary>
    [Fact]
    public void ShapeCornersScaleBecauseTheyAreGeometry()
    {
        DocumentSession session = Session();
        PathItem path = Box("round", 100);
        session.Document.Artboards[0].Layers[0].AddItem(path);

        Assert.True(CornerRounder.Round(path, 0, 0, 10).Rounded, "the corner must be round for this to measure anything");
        session.SelectObject(path);
        double before = CornerDeviation(path);

        Scale(session, 2, 2);

        Assert.True(before > 0, "the rounded corner has an offset to measure");
        Assert.Equal(before * 2.0, CornerDeviation(path), 1);
    }

    /// <summary>How far the first node sits from the bounding box's corner: the baked corner radius.</summary>
    private static double CornerDeviation(PathItem path)
    {
        Rect2D box = path.BoundingBox();
        PathNode first = path.SubPaths[0].Nodes[0];
        return first.Anchor.DistanceTo(new Point2D(box.X, box.Y));
    }
}
