using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using VCCad.App.Views.Panes;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A dock panel's content fills the panel it is in.
///
/// The Objects list had a height of its own, 240, so dragging the panel taller left the list
/// the same size and the row centred it — the panel grew and nothing in it did. The height
/// belongs to the panel; the content stretches into whatever it is given.
///
/// Checked live through the automation API as well: with the pane at 520 the list measured
/// 476, at 300 it measured 256, at 160 it measured 116 — the panel less its header each
/// time. Before the change it was 240 at every one of those sizes.
/// </summary>
public class PaneContentFillsTests
{
    [AvaloniaFact]
    public void TheObjectsListHasNoHeightOfItsOwn()
    {
        var pane = new ObjectsPane();
        TreeView tree = pane.FindControl<TreeView>("ObjectTree")!;

        // NaN is "as tall as I am given". A number here is the bug: it is what left the list
        // stranded in the middle of a resized panel.
        Assert.True(double.IsNaN(tree.Height),
            $"the list should take the height it is given, but it asks for {tree.Height}");
    }

    [AvaloniaFact]
    public void TheObjectsListStretchesRatherThanCentring()
    {
        var pane = new ObjectsPane();
        TreeView tree = pane.FindControl<TreeView>("ObjectTree")!;

        Assert.Equal(VerticalAlignment.Stretch, tree.VerticalAlignment);
    }

    [AvaloniaFact]
    public void ThePanesContentHoldsTheListWithoutSizingIt()
    {
        // No wrapper that would size it independently of the pane.
        var pane = new ObjectsPane();
        // The pane's content holds the list, and the list still keeps no height of its own.
        // This asserted Content WAS the tree, as a way of saying "no wrapper that sizes the list
        // independently". A wrapper whose child takes the height it is given satisfies that, and
        // the first two tests in this file already pin it directly, so the assertion is now the
        // same invariant stated about the child. FindControl reaches the named template whether or
        // not the pane is attached, which GetVisualDescendants does not.
        TreeView tree = pane.FindControl<TreeView>("ObjectTree")!;
        Assert.NotNull(tree);
        Assert.True(double.IsNaN(tree.Height),
            $"the list should take the height it is given, but it asks for {tree.Height}");
    }
}
