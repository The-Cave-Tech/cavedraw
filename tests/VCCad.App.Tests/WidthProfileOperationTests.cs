using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.Controls;
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

    /// <summary>Two points whose widths a test can read back, both sides the same.</summary>
    private static object[] EvenPoints(double left, double right)
        => new object[]
        {
            new { position = 0.0, left, right },
            new { position = 1.0, left, right },
        };

    /// <summary>
    /// **A negative width is clamped at zero, not refused - the same model the canvas lands on.**
    ///
    /// The canvas clamps a dragged grip with <c>Math.Max(0, ...)</c>, because a negative width is not a
    /// thinner stroke: a point's widths are full widths, the geometry halves them, and a negative half puts
    /// the offset edge on the other side of the centreline. Clamping here is what makes a driver's call and a
    /// person's drag one edit rather than two that disagree about the same value.
    ///
    /// Refusing was the alternative and is rejected: <c>profile.setPoint</c> takes a measurement a driver has
    /// computed, and across a stroke the sign comes from which side of the centreline a sample landed on - so a
    /// near-zero width can arrive as a small negative number. Refusing would throw away an edit whose visible
    /// intent is "pin this side shut", and leave the drag that produces the same result working.
    /// </summary>
    [Fact]
    public void ANegativeWidthIsClampedAtZeroRatherThanRefused()
    {
        (AutomationContext context, _) = Host();
        EditorOperations.Invoke(context, "profile.create",
            Params(new { name = "Brush", points = EvenPoints(20, 20) }));

        EditorOperations.Invoke(context, "profile.setPoint",
            Params(new { name = "Brush", index = 0, left = -6, right = 20 }));

        WidthPoint point = context.ViewModel.Document.WidthProfiles.Single().Points[0];
        Assert.Equal(0.0, point.LeftWidth, 6);

        // Only the member given moved: the other side keeps what it had.
        Assert.Equal(20.0, point.RightWidth, 6);
    }

    /// <summary>
    /// **The visible symptom: the outline a negative width draws stays on the correct side of the
    /// centreline**, and is the very outline the clamped value draws.
    ///
    /// Asserted on the geometry rather than on "the call returned": the defect was never that a number
    /// changed, it was that the stroke mirrored. On a path drawn left to right, left is the upward side, so
    /// the left edge of this stroke belongs at the centreline (y = 0) and never above it in a Y-down space.
    /// A negative left width puts it at +3, across the centreline - the inside-out drawing.
    ///
    /// The comparison against a locally built clamped profile is what proves the two routes are identical
    /// rather than merely both plausible.
    /// </summary>
    [Fact]
    public void ANegativeWidthDrawsTheSameOutlineAsTheClampedWidth()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "profile.create",
            Params(new { name = "Brush", points = EvenPoints(20, 20) }));
        EditorOperations.Invoke(context, "profile.apply", Params(new { name = "Brush" }));

        // Every point's left width is driven negative through the operation, which is the state a driver
        // can reach and a person cannot.
        EditorOperations.Invoke(context, "profile.setPoint", Params(new { name = "Brush", index = 0, left = -6 }));
        EditorOperations.Invoke(context, "profile.setPoint", Params(new { name = "Brush", index = 1, left = -6 }));

        IReadOnlyList<Point2D> drawn = CanvasWorkspace.ProfileLoops(path, path.Stroke)[0];

        var clamped = new WidthProfileSpec("Brush", new[]
        {
            new WidthPoint(0.0, 0.0, 20.0),
            new WidthPoint(1.0, 0.0, 20.0),
        });
        IReadOnlyList<Point2D> expected =
            CanvasWorkspace.ProfileLoops(path, path.Stroke with { WidthProfile = clamped })[0];

        Assert.Equal(expected.Count, drawn.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, drawn[i].X, 6);
            Assert.Equal(expected[i].Y, drawn[i].Y, 6);
        }

        // The centreline is y = 0: the left edge sits on it and the right edge is ten below, which is half
        // the twenty-wide right side. A negative left width would have put the left edge at +3 instead.
        Assert.Equal(0.0, drawn.Min(p => p.Y), 6);
        Assert.Equal(10.0, drawn.Max(p => p.Y), 6);
    }

    /// <summary>
    /// **Every entry point that stores a width clamps, so the clamp is not merely moved one call away.**
    ///
    /// Three routes write a <see cref="WidthPoint"/> from a caller's numbers - <c>style.setWidthProfile</c>,
    /// <c>profile.create</c> and <c>profile.setPoint</c> - and a fourth, <c>profile.apply</c>, only carries a
    /// stored profile across. A clamp in one of them would leave the other two able to write the inside-out
    /// state, so this walks all three and asserts they agree on the same value.
    /// </summary>
    [Fact]
    public void EveryWidthEntryPointClampsANegativeWidthAtZero()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setWidthProfile",
            Params(new { name = "Stroke", points = EvenPoints(-6, 20) }));
        Assert.Equal(0.0, path.Stroke.WidthProfile!.Points[0].LeftWidth, 6);
        Assert.Equal(0.0, path.Stroke.WidthProfile.Points[1].LeftWidth, 6);

        EditorOperations.Invoke(context, "profile.create",
            Params(new { name = "Brush", points = EvenPoints(-6, 20) }));
        Assert.Equal(0.0, context.ViewModel.Document.WidthProfiles.Single().Points[0].LeftWidth, 6);

        EditorOperations.Invoke(context, "profile.setPoint",
            Params(new { name = "Brush", index = 0, left = -6 }));
        Assert.Equal(0.0, context.ViewModel.Document.WidthProfiles.Single().Points[0].LeftWidth, 6);

        // The right width was never negative, so no route touched it - the clamp is not a blanket rewrite.
        Assert.Equal(20.0, context.ViewModel.Document.WidthProfiles.Single().Points[0].RightWidth, 6);
    }
}
