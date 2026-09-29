using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What a transform reaches.
///
/// Moving a group moves what is in it - that is what a group is for. Holding the platform
/// modifier says "this object only", so the container's own placement moves and its contents
/// stay put, which is how something is nudged without disturbing a drawing placed carefully
/// inside it. Command on macOS, Control elsewhere: the platform's convention, not ours.
/// </summary>
public class TransformTargetTests
{
    private static PathItem Box(string name, double x, double y)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + 10, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + 10, y + 10)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + 10)));
        return path;
    }

    [Fact]
    public void APlainTransformReachesTheChildren()
    {
        var group = new ArtGroup { Name = "group" };
        PathItem child = Box("child", 0, 0);
        group.AddItem(child);

        IReadOnlyList<LayerItem> targets = SelectionEngine.TransformTargets(
            new LayerItem[] { group }, ownOnly: false);

        Assert.Equal(2, targets.Count);
        Assert.Contains(group, targets);
        Assert.Contains(child, targets);
    }

    [Fact]
    public void TheModifierLeavesTheChildrenAlone()
    {
        var group = new ArtGroup { Name = "group" };
        group.AddItem(Box("child", 0, 0));

        IReadOnlyList<LayerItem> targets = SelectionEngine.TransformTargets(
            new LayerItem[] { group }, ownOnly: true);

        Assert.Same(group, Assert.Single(targets));
    }

    [Fact]
    public void NestingIsReachedAllTheWayDown()
    {
        // A group in a group in a group: the innermost object still moves with the outermost
        // one, because that is what it means to be inside it.
        var outer = new ArtGroup { Name = "outer" };
        var middle = new ArtGroup { Name = "middle" };
        var inner = new ArtGroup { Name = "inner" };
        PathItem leaf = Box("leaf", 0, 0);

        inner.AddItem(leaf);
        middle.AddItem(inner);
        outer.AddItem(middle);

        IReadOnlyList<LayerItem> targets = SelectionEngine.TransformTargets(
            new LayerItem[] { outer }, ownOnly: false);

        Assert.Equal(4, targets.Count);
        Assert.Contains(leaf, targets);
    }

    [Fact]
    public void TheModifierStopsAtTheObjectEvenWhenNested()
    {
        var outer = new ArtGroup { Name = "outer" };
        var middle = new ArtGroup { Name = "middle" };
        middle.AddItem(Box("leaf", 0, 0));
        outer.AddItem(middle);

        IReadOnlyList<LayerItem> targets = SelectionEngine.TransformTargets(
            new LayerItem[] { outer }, ownOnly: true);

        Assert.Same(outer, Assert.Single(targets));
        Assert.DoesNotContain(middle, targets);
    }

    [Fact]
    public void APlainPathIsItsOwnOnlyTargetEitherWay()
    {
        PathItem path = Box("path", 0, 0);

        Assert.Same(path, Assert.Single(
            SelectionEngine.TransformTargets(new LayerItem[] { path }, ownOnly: false)));

        Assert.Same(path, Assert.Single(
            SelectionEngine.TransformTargets(new LayerItem[] { path }, ownOnly: true)));
    }

    [Fact]
    public void SeveralObjectsAreAllReached()
    {
        var first = new ArtGroup { Name = "first" };
        first.AddItem(Box("a", 0, 0));
        var second = new ArtGroup { Name = "second" };
        second.AddItem(Box("b", 0, 0));

        IReadOnlyList<LayerItem> targets = SelectionEngine.TransformTargets(
            new LayerItem[] { first, second }, ownOnly: false);

        Assert.Equal(4, targets.Count);
    }

    [Fact]
    public void AnObjectReachedTwiceIsListedOnce()
    {
        // Selecting a group and something inside it at the same time is unusual but possible,
        // and applying a transform twice to one object would double it.
        var group = new ArtGroup { Name = "group" };
        PathItem child = Box("child", 0, 0);
        group.AddItem(child);

        IReadOnlyList<LayerItem> targets = SelectionEngine.TransformTargets(
            new LayerItem[] { group, child }, ownOnly: false);

        Assert.Equal(2, targets.Count);
    }

    [Fact]
    public void AnEmptySelectionReachesNothing()
    {
        Assert.Empty(SelectionEngine.TransformTargets(
            Array.Empty<LayerItem>(), ownOnly: false));
        Assert.Empty(SelectionEngine.TransformTargets(
            Array.Empty<LayerItem>(), ownOnly: true));
    }

    [Fact]
    public void TheModifierIsCommandOnMacAndControlElsewhere()
    {
        Assert.Equal(OperatingSystem.IsMacOS(), SelectionEngine.IsOwnTransformModifier);
    }
}
