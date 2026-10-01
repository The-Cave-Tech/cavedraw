using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Setting a width profile through the operation registry, which is the only way anything gets done here - a
/// person and a driver use the same one.
/// </summary>
public class WidthProfileOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, path);
    }

    [Fact]
    public void AProfileIsSetAndReadBack()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new
        {
            name = "Brush 4",
            points = new object[]
            {
                new { position = 0.0, left = 16, right = 4 },
                new { position = 1.0, left = 0, right = 0, interpolation = "cubic" },
            },
        }));

        WidthProfileSpec profile = path.Stroke.WidthProfile!;
        Assert.Equal("Brush 4", profile.Name);
        Assert.Equal(2, profile.Points.Count);
        Assert.Equal(WidthInterpolation.Cubic, profile.Points[1].Interpolation);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "style.strokes", default));
        Assert.Contains("\"profile\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Brush 4\"", json, StringComparison.Ordinal);
        Assert.Contains("\"left\":16", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty list **clears** the profile rather than storing a profile that says nothing. One state for "no
    /// profile" is what keeps every renderer from having to treat two of them the same way.
    /// </summary>
    [Fact]
    public void AnEmptyListClearsTheProfile()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new
        {
            points = new object[] { new { position = 0.0, left = 10, right = 10 } },
        }));
        Assert.True(path.Stroke.HasWidthProfile);

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new { points = Array.Empty<object>() }));

        Assert.Null(path.Stroke.WidthProfile);
        Assert.False(path.Stroke.HasWidthProfile);
    }

    /// <summary>A point given without a width is skipped, not read as a width of zero.</summary>
    [Fact]
    public void AMalformedPointIsSkippedRatherThanPinchingTheStroke()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new
        {
            points = new object[]
            {
                new { position = 0.0, left = 10, right = 10 },
                new { position = 0.5 },
                new { position = 1.0, left = 2, right = 2 },
            },
        }));

        Assert.Equal(2, path.Stroke.WidthProfile!.Points.Count);
    }

    [Fact]
    public void SettingAProfileIsOneUndoStep()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new
        {
            points = new object[] { new { position = 0.0, left = 10, right = 10 } },
        }));

        context.ViewModel.ActiveSession.Undo();

        Assert.Null(path.Stroke.WidthProfile);
    }

    /// <summary>The profile is independent of the stroke's own width, so clearing one leaves the other.</summary>
    [Fact]
    public void ClearingAProfileLeavesTheStrokeWidthAlone()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new
        {
            points = new object[] { new { position = 0.0, left = 4, right = 4 } },
        }));

        EditorOperations.Invoke(context, "style.setWidthProfile", Params(new { points = Array.Empty<object>() }));

        Assert.Equal(8.0, path.Stroke.Width, 3);
    }
}
