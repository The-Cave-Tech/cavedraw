using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A path carries a **stack** of strokes, bottom to top, and the ordinary single-stroke path is a stack of one.
///
/// The model change is deliberately small on the surface: `Stroke` is still there and still means what it
/// meant, so the ninety-odd call sites that read it keep working. What these tests pin is that the stack is
/// real underneath - because the failure mode of a change like this is not a crash, it is a path that had two
/// strokes and now has one, in a place nobody thought to look.
/// </summary>
public class StrokeStackTests
{
    private static PathItem Line()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 20)));
        return path;
    }

    private static StrokeSpec Stroke(double width, params double[] rgb)
        => new(true, new ColorRgb(rgb[0], rgb[1], rgb[2]), width,
            StrokeCap.Butt, StrokeJoin.Miter, 4);

    [Fact]
    public void ANewPathHasExactlyTheDefaultStroke()
    {
        PathItem path = Line();

        // Never empty: a caller reading the stack should not need a null check. The default is the classic
        // hairline - a visible 1pt stroke - and `StrokeSpec.None` is how a path comes to have no visible one.
        StrokeSpec only = Assert.Single(path.Strokes);
        Assert.Equal(StrokeSpec.Hairline(ColorRgb.Black), only);
        Assert.Same(only, path.Stroke);
    }

    [Fact]
    public void SettingTheStrokeReplacesTheWholeStack()
    {
        PathItem path = Line();
        path.Strokes.Add(Stroke(4, 1, 0, 0));
        path.Strokes.Add(Stroke(2, 0, 0, 1));
        path.NotifyStrokesChanged();

        path.Stroke = Stroke(7, 0, 1, 0);

        // "Set the stroke" on a path that has several means the path now has that one. Anything else would
        // leave a caller who set a stroke looking at a different one on screen.
        StrokeSpec only = Assert.Single(path.Strokes);
        Assert.Equal(7.0, only.Width, 3);
    }

    [Fact]
    public void TheCompatibilityPropertyReadsTheBottomStroke()
    {
        PathItem path = Line();
        path.Strokes.Clear();
        path.Strokes.Add(Stroke(5, 1, 0, 0));
        path.Strokes.Add(Stroke(1, 0, 0, 1));

        Assert.Equal(5.0, path.Stroke.Width, 3);
        Assert.Equal(2, path.Strokes.Count);
    }

    /// <summary>
    /// The one call site the compiler caught, and the reason it matters: a clone that copied only `Stroke`
    /// would drop every stroke above the bottom one, so duplicating a two-stroke path would silently lose
    /// artwork.
    /// </summary>
    [Fact]
    public void ACloneKeepsTheWholeStack()
    {
        PathItem path = Line();
        path.Strokes.Clear();
        path.Strokes.Add(Stroke(6, 1, 0, 0));
        path.Strokes.Add(Stroke(3, 0, 1, 0));
        path.Strokes.Add(Stroke(1, 0, 0, 1));

        var copy = (PathItem)path.Clone();

        Assert.Equal(3, copy.Strokes.Count);
        Assert.Equal(new[] { 6.0, 3.0, 1.0 }, copy.Strokes.Select(s => s.Width).ToArray());
        Assert.NotSame(path.Strokes, copy.Strokes);
    }

    [Fact]
    public void HasVisibleStrokeLooksAtEveryStrokeNotJustTheFirst()
    {
        PathItem path = Line();
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);
        path.Strokes.Add(Stroke(2, 0, 0, 0));

        Assert.True(path.HasVisibleStroke);
        Assert.False(path.Stroke.HasVisibleOutline);
    }

    // ---------------------------------------------------------------- the sidecar

    private static PathItem RoundTrip(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        return reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
    }

    [Fact]
    public void AStackSurvivesSaveAndReload()
    {
        PathItem path = Line();
        path.Strokes.Clear();
        path.Strokes.Add(Stroke(6, 1, 0, 0));
        path.Strokes.Add(Stroke(3, 0, 1, 0));

        PathItem reloaded = RoundTrip(path);

        Assert.Equal(2, reloaded.Strokes.Count);
        Assert.Equal(new[] { 6.0, 3.0 }, reloaded.Strokes.Select(s => s.Width).ToArray());
        Assert.Equal(1.0, reloaded.Strokes[0].Color.R, 3);
        Assert.Equal(1.0, reloaded.Strokes[1].Color.G, 3);
    }

    /// <summary>
    /// A document with one stroke is written exactly as it was before strokes became a stack: the member is
    /// omitted, so nothing that has one changes on the way out. This is the compatibility half of the change,
    /// and it is why the stack travels as an extra member rather than replacing the existing one.
    /// </summary>
    [Fact]
    public void ASingleStrokeIsWrittenWithoutTheStackMember()
    {
        PathItem path = Line();
        path.Stroke = Stroke(2, 0, 0, 0);

        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        string json = System.Text.Encoding.UTF8.GetString(VccadDocumentSerializer.SerializeToBytes(document));

        Assert.DoesNotContain("\"Strokes\"", json);
        Assert.Contains("\"Stroke\"", json);
    }

    /// <summary>
    /// And a file written before any of this loads as a stack of one, rather than as a path with no strokes -
    /// which is what "absent means what it always meant" has to mean to be worth saying.
    /// </summary>
    [Fact]
    public void AFileWithoutTheMemberLoadsAsAStackOfOne()
    {
        PathItem reloaded = RoundTrip(Line());

        StrokeSpec only = Assert.Single(reloaded.Strokes);
        Assert.Equal(reloaded.Stroke.Width, only.Width, 6);
    }
}
