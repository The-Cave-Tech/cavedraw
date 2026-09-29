using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A path's world position. Path geometry is stored relative to its artboard origin, so a
/// path's world bounds are its local box shifted by that origin - and that must hold for a
/// line as much as for a rectangle. A horizontal line's local box has zero height, and
/// `WorldBounds()` used to treat that as "empty" and return the local box as-is, putting the
/// line at the wrong place on any artboard not at the origin.
/// </summary>
public class PathWorldBoundsTests
{
    private static (CadDocument Document, Artboard Board) PageAt(double x, double y)
    {
        var document = new CadDocument();
        var board = new Artboard(new Size2D(612, 792), new Point2D(x, y)) { Name = "Page" };
        board.AddLayer("Artwork");
        document.AddArtboard(board);
        return (document, board);
    }

    [Fact]
    public void AHorizontalLineOnAnArtboardIsReportedAtItsArtboardOrigin()
    {
        (_, Artboard board) = PageAt(300, 200);

        PathItem line = PathFactory.CreateLine("line", new Point2D(10, 10), new Point2D(110, 10));
        board.Layers[0].AddItem(line);

        Rect2D world = line.WorldBounds();

        Assert.Equal(310, world.X, 6);
        Assert.Equal(210, world.Y, 6);
        Assert.Equal(100, world.Width, 6);
    }

    [Fact]
    public void AVerticalLineOnAnArtboardIsReportedAtItsArtboardOrigin()
    {
        (_, Artboard board) = PageAt(300, 200);

        PathItem line = PathFactory.CreateLine("line", new Point2D(10, 10), new Point2D(10, 60));
        board.Layers[0].AddItem(line);

        Rect2D world = line.WorldBounds();

        Assert.Equal(310, world.X, 6);
        Assert.Equal(210, world.Y, 6);
        Assert.Equal(50, world.Height, 6);
    }

    /// <summary>
    /// The other side of the rule: a path with no nodes at all has no world position, and
    /// must not be given one by fabricating a box from the artboard origin.
    /// </summary>
    [Fact]
    public void AnEmptyPathStillHasNoWorldBounds()
    {
        (_, Artboard board) = PageAt(300, 200);

        var empty = new PathItem { Name = "empty" };
        board.Layers[0].AddItem(empty);

        Assert.True(empty.WorldBounds().IsEmpty);
    }
}
