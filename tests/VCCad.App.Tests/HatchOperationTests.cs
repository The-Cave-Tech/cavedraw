using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A hatch is something the assistant can set, not only something a person can draw with.
///
/// Everything goes through the one operation registry - the canvas UI, the HTTP endpoint and the assistant -
/// so a capability that exists only in a control's event handler is a design defect. This drives
/// `style.setHatch` the way a driver would: set it on a selection, read the document back, take it off again.
/// </summary>
public class HatchOperationTests
{
    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "panel", Fill = FillSpec.Solid(ColorRgb.White) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 60)));
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, path);
    }

    [Fact]
    public void OneFamilyIsSetFromAnAngleAndASpacing()
    {
        (AutomationContext context, PathItem path) = Host();

        object? result = EditorOperations.Invoke(
            context, "style.setHatch", JsonSerializer.SerializeToElement(new { angle = 30, spacing = 6, width = 2 }));

        Assert.NotNull(result);
        Assert.NotNull(path.Fill.Hatch);
        HatchLineSpec line = Assert.Single(path.Fill.Hatch!.Lines);
        Assert.Equal(30.0, line.AngleDegrees, 6);
        Assert.Equal(6.0, line.Spacing, 6);
        Assert.Equal(2.0, line.Width, 6);
    }

    /// <summary>The cross option is the same family at right angles, carrying the weight that was asked for.</summary>
    [Fact]
    public void CrossCarriesTheWeight()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(
            context, "style.setHatch", JsonSerializer.SerializeToElement(new { cross = true, spacing = 8, width = 0.5 }));

        Assert.Equal(2, path.Fill.Hatch!.Lines.Count);
        Assert.All(path.Fill.Hatch.Lines, l => Assert.Equal(0.5, l.Width, 6));
        Assert.All(path.Fill.Hatch.Lines, l => Assert.Equal(8.0, l.Spacing, 6));
    }

    /// <summary>Clearing takes the hatch off without disturbing the colour behind it.</summary>
    [Fact]
    public void ClearingLeavesTheColourBehindIt()
    {
        (AutomationContext context, PathItem path) = Host();
        path.Fill = FillSpec.Solid(new ColorRgb(10, 20, 30));

        EditorOperations.Invoke(context, "style.setHatch", JsonSerializer.SerializeToElement(new { spacing = 5 }));
        Assert.NotNull(path.Fill.Hatch);

        EditorOperations.Invoke(context, "style.setHatch", JsonSerializer.SerializeToElement(new { clear = true }));

        Assert.Null(path.Fill.Hatch);
        Assert.True(path.Fill.IsVisible);
        Assert.Equal(10.0, path.Fill.Color.R, 6);
    }

    [Fact]
    public void TheOperationIsInTheCatalogue()
    {
        Assert.True(EditorOperations.TryGet("style.setHatch", out EditorOperation operation));
        Assert.Contains("hatch", operation.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
