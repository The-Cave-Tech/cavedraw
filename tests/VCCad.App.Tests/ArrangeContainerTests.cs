using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Arranging a container arranges what is inside it.
///
/// A group has no geometry of its own - it is where its contents are - so moving a group can only mean moving
/// everything in it. It used to fall to the arm that counts a container as unmovable: a selected group was
/// skipped, its contents stayed where they were, and the status line said so. The report was a container whose
/// contents are left behind.
///
/// Note what this is *not*: aligning a selection that contains only a group is a no-op whatever the code does,
/// because the group's own bounds already are the target. The case that matters is a group lined up **against
/// something else**, which is when it receives a delta that has to reach its children.
/// </summary>
public class ArrangeContainerTests
{
    private static PathItem Box(string name, double x, double y, double size)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    private static DocumentSession Session(
        out ArtGroup group, out PathItem first, out PathItem second, out PathItem other)
    {
        var document = CadDocument.CreateDefault("Page");
        document.Artboards[0].AddLayer("Layer 1");

        first = Box("first", 0, 0, 100);
        second = Box("second", 300, 0, 100);
        group = new ArtGroup { Name = "Group" };
        group.AddItem(first);
        group.AddItem(second);

        // Far to the left of the group, so aligning to the start lines everything up against this one.
        other = Box("other", -500, 0, 100);

        document.Artboards[0].Layers[0].AddItem(group);
        document.Artboards[0].Layers[0].AddItem(other);

        var session = new DocumentSession();
        session.Initialize(document);
        return session;
    }

    /// <summary>A group lined up against another object takes its contents with it.</summary>
    [Fact]
    public void AligningAGroupAgainstSomethingElseMovesItsContents()
    {
        DocumentSession session = Session(out ArtGroup group, out PathItem first, out PathItem second, out PathItem other);

        session.SelectRange(new LayerItem[] { group, other }, additive: false);
        session.AlignSelection(ArrangeAxis.Horizontal, ArrangeEdge.Start);

        // The far-left object is the target and stays put. The group is 500 to its right and both of its
        // children travel with it, keeping their positions relative to each other.
        Assert.Equal(-500, other.BoundingBox().X, 3);
        Assert.Equal(-500, first.BoundingBox().X, 3);
        Assert.Equal(-200, second.BoundingBox().X, 3);
    }

    /// <summary>
    /// Selecting a group and one of its own children moves that child once - not twice, which is what
    /// flattening without remembering what has already moved would do.
    /// </summary>
    [Fact]
    public void AChildReachedTwiceMovesOnce()
    {
        DocumentSession session = Session(out ArtGroup group, out PathItem first, out PathItem second, out PathItem other);

        session.SelectRange(new LayerItem[] { group, first, other }, additive: false);
        session.AlignSelection(ArrangeAxis.Horizontal, ArrangeEdge.Start);

        Assert.Equal(-500, first.BoundingBox().X, 3);
        Assert.Equal(-200, second.BoundingBox().X, 3);
    }
}
