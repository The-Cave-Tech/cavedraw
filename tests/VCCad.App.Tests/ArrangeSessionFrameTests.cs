using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Aligning and distributing through <see cref="DocumentSession"/> measure every object in one frame
/// (issue #174).
///
/// <see cref="Arrange"/> now produces its deltas in the **artboard** frame, and the session has to carry
/// each one into the item's own frame before applying it - the conversion
/// <see cref="SelectionEngine.DeltaInItem"/> states once. The session is the caller under test here
/// because the Core tests drive `Arrange` themselves and would not notice a session that applied the
/// arranged delta raw, which is exactly the half of the defect a person meets when they press Align.
///
/// Measured on **model geometry**, composed by this test rather than asked of the code under test.
/// </summary>
public class ArrangeSessionFrameTests
{
    private static PathItem Box(string name, Rect2D box)
    {
        PathItem path = PathFactory.CreateRectangle(name, box);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    /// <summary>An item's box in document coordinates, composed from the document's own transforms.</summary>
    private static Rect2D InWorld(PathItem path)
    {
        Rect2D nodes = Rect2D.Empty;
        AffineTransform world = SelectionEngine.ToWorld(path);
        foreach (SubPath sub in path.SubPaths)
        {
            foreach (PathNode node in sub.Nodes)
            {
                Point2D point = world.Transform(node.Anchor);
                nodes = nodes.Union(Rect2D.FromPoints(point, point));
            }
        }

        return nodes;
    }

    private static void AssertSame(double expected, double actual, string what)
        => Assert.True(Math.Abs(expected - actual) <= 1e-6,
            $"{what} should be {expected} but is {actual}");

    /// <summary>
    /// Aligning a group against an object outside it puts both on the same line **on the page**.
    ///
    /// The group is `translate(200,300) scale(2) rotate(30°)`, so its square is drawn at 187.32,327.32 to
    /// 241.96,381.96 while the loose object is at 0..40 by 0..40. Aligning to the start moves the group to
    /// the loose object's left edge - 0 - and the group doubles everything, so the square's own numbers must
    /// move by half the distance the selection travelled, not by all of it.
    ///
    /// Against the old code the delta was applied raw to the stored numbers, so the square travelled twice
    /// as far as the line it was aligned to and the two were never on the same edge.
    /// </summary>
    [Fact]
    public void AligningAGroupAgainstAnObjectOutsideItPutsBothOnTheSamePageEdge()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];

        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateTranslation(200, 300)
                .Compose(AffineTransform.CreateScale(2, 2))
                .Compose(AffineTransform.CreateRotation(Math.PI / 6)),
        };

        PathItem inside = Box("inside", new Rect2D(10, 10, 20, 20));
        PathItem outside = Box("outside", new Rect2D(0, 0, 40, 40));
        group.AddItem(inside);
        layer.AddItem(group);
        layer.AddItem(outside);

        AssertSame(187.3205080756888, InWorld(inside).Left, "the grouped square before aligning");
        AssertSame(0, InWorld(outside).Left, "the loose object before aligning");

        viewModel.SelectRange(new LayerItem[] { group, outside }, additive: false);
        int moved = viewModel.ActiveSession.AlignSelection(ArrangeAxis.Horizontal, ArrangeEdge.Start);

        Assert.Equal(1, moved);
        AssertSame(0, InWorld(outside).Left, "the loose object after aligning");
        AssertSame(0, InWorld(inside).Left, "the grouped square after aligning");

        // The geometry really was rewritten rather than left alone: the page moved it 187.32 units left, so
        // inside a frame that doubles and turns, a stored x that never changed could not be on the line.
        Assert.NotEqual(10.0, inside.BoundingBox().Left, 6);
    }
}
