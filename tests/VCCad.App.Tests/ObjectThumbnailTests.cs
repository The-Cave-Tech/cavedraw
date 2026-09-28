using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using MediaGeometry = Avalonia.Media.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The little sample of an object at the left of its row.
///
/// It has to be the object's own shape: two rectangles must not produce the same picture, and
/// a diagonal line must not come out as a horizontal one. The traps are that the fit must be
/// uniform — stretching a thin line to fill the square makes it look like a different object —
/// and that everything has to land inside the box whatever its proportions.
/// </summary>
public class ObjectThumbnailTests
{
    private const double Size = 20;

    /// <summary>A rectangle with a long side twice its short one.</summary>
    private static PathItem Rect(double w, double h)
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        foreach (Point2D p in new[]
                 {
                     new Point2D(0, 0), new Point2D(w, 0),
                     new Point2D(w, h), new Point2D(0, h),
                 })
        {
            sub.Nodes.Add(new PathNode(p));
        }

        return path;
    }

    private static PathItem Line(Point2D a, Point2D b)
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(a));
        sub.Nodes.Add(new PathNode(b));
        return path;
    }

    /// <summary>Checks the geometry is present and drawn inside the square.</summary>
    private static Rect BoundsOf(MediaGeometry geometry)
    {
        Rect b = geometry.Bounds;
        Assert.True(b.Width >= 0 && b.Height >= 0, "geometry has no extent");
        return b;
    }

    [AvaloniaFact]
    public void APathGetsAGeometry()
    {
        MediaGeometry? geometry = ObjectThumbnail.For(Rect(100, 50), Size);
        Assert.NotNull(geometry);

        Rect b = BoundsOf(geometry!);
        Assert.True(b.Right <= Size + 0.001, $"right edge {b.Right} escapes the box");
        Assert.True(b.Bottom <= Size + 0.001, $"bottom edge {b.Bottom} escapes the box");
        Assert.True(b.Left >= -0.001 && b.Top >= -0.001, "geometry starts outside the box");
    }

    [AvaloniaFact]
    public void TheLongSideFillsTheBoxAndTheFitIsUniform()
    {
        // 100x50 in a 20 box: the long side fills it and the short side is half, not also 20.
        // A non-uniform fit would make this look like a square, which it is not.
        MediaGeometry geometry = ObjectThumbnail.For(Rect(100, 50), Size)!;
        Rect b = BoundsOf(geometry);

        Assert.Equal(Size, b.Width, 1);
        Assert.Equal(Size / 2, b.Height, 1);
    }

    [AvaloniaFact]
    public void TwoDifferentShapesDoNotLookTheSame()
    {
        MediaGeometry wide = ObjectThumbnail.For(Rect(200, 20), Size)!;
        MediaGeometry tall = ObjectThumbnail.For(Rect(20, 200), Size)!;

        Assert.True(
            Math.Abs(wide.Bounds.Width - tall.Bounds.Width) > 1,
            "a wide rectangle and a tall one produced the same picture");
    }

    [AvaloniaFact]
    public void ADiagonalLineKeepsItsDirection()
    {
        // A line from bottom-left to top-right must not be flattened to a horizontal one.
        MediaGeometry diagonal = ObjectThumbnail.For(
            Line(new Point2D(0, 0), new Point2D(100, 100)), Size)!;

        Rect b = BoundsOf(diagonal);
        Assert.Equal(Size, b.Width, 1);
        Assert.Equal(Size, b.Height, 1);
    }

    [AvaloniaFact]
    public void AHorizontalLineIsWideAndFlat()
    {
        MediaGeometry geometry = ObjectThumbnail.For(
            Line(new Point2D(0, 0), new Point2D(100, 0)), Size)!;

        Rect b = BoundsOf(geometry);
        Assert.Equal(Size, b.Width, 1);
        Assert.Equal(0, b.Height, 1);
    }

    [AvaloniaFact]
    public void TheShapeIsCentredInTheBox()
    {
        // A 20-wide, 10-tall shape in a 20 box: it sits in the middle vertically, not at the
        // top, so a column of thumbnails lines up on its centre.
        MediaGeometry geometry = ObjectThumbnail.For(Rect(100, 50), Size)!;
        Rect b = BoundsOf(geometry);

        Assert.Equal((Size - b.Height) / 2, b.Top, 1);
        Assert.Equal((Size - b.Width) / 2, b.Left, 1);
    }

    [AvaloniaFact]
    public void AnEmptyPathHasNoThumbnailRatherThanAnEmptyOne()
    {
        Assert.Null(ObjectThumbnail.For(new PathItem(), Size));
    }

    [AvaloniaFact]
    public void ATextRunGetsItsOwnOutlines()
    {
        var text = new TextItem();
        TextEditing.Insert(text, 0, "Jalie");

        MediaGeometry? geometry = ObjectThumbnail.For(text, Size);
        Assert.NotNull(geometry);

        Rect b = BoundsOf(geometry!);
        Assert.True(b.Width > 0, "the text thumbnail has no width");
        Assert.True(b.Right <= Size + 0.001 && b.Bottom <= Size + 0.001,
            "the text thumbnail escapes its box");
    }

    [AvaloniaFact]
    public void DifferentTextLooksDifferent()
    {
        var one = new TextItem();
        TextEditing.Insert(one, 0, "l");
        var two = new TextItem();
        TextEditing.Insert(two, 0, "mm");

        MediaGeometry a = ObjectThumbnail.For(one, Size)!;
        MediaGeometry b = ObjectThumbnail.For(two, Size)!;

        Assert.True(Math.Abs(a.Bounds.Width - b.Bounds.Width) > 0.5,
            "two different words produced the same thumbnail");
    }

    [AvaloniaFact]
    public void AnImageGetsItsFrame()
    {
        var image = new ImageItem
        {
            PixelWidth = 4,
            PixelHeight = 2,
            Placement = new Rect2D(0, 0, 100, 50),
        };

        MediaGeometry? geometry = ObjectThumbnail.For(image, Size);
        Assert.NotNull(geometry);

        Rect b = BoundsOf(geometry!);
        Assert.Equal(Size, b.Width, 1);
        Assert.Equal(Size / 2, b.Height, 1);
    }

    [AvaloniaFact]
    public void AShapeFarFromTheOriginIsStillDrawnInTheBox()
    {
        // The fit is relative to the object's own bounds, so where it sits on the page does
        // not matter - a thumbnail of a page's worth of offsets would be empty.
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        foreach (Point2D p in new[]
                 {
                     new Point2D(5000, 4000), new Point2D(5100, 4000),
                     new Point2D(5100, 4050), new Point2D(5000, 4050),
                 })
        {
            sub.Nodes.Add(new PathNode(p));
        }

        Rect b = BoundsOf(ObjectThumbnail.For(path, Size)!);
        Assert.True(b.Right <= Size + 0.001 && b.Left >= -0.001,
            $"a distant shape drew at {b.Left}..{b.Right}");
    }

    [AvaloniaFact]
    public void ASquareObjectIsStretchedToTheBoxAndStaysSquare()
    {
        MediaGeometry geometry = ObjectThumbnail.For(Rect(50, 50), Size)!;
        Rect b = BoundsOf(geometry);

        Assert.Equal(Size, b.Width, 1);
        Assert.Equal(Size, b.Height, 1);
    }

    [AvaloniaFact]
    public void AGroupShowsWhatItsPathsShow()
    {
        var group = new ArtGroup();
        group.AddItem(Rect(100, 50));

        MediaGeometry? fromGroup = ObjectThumbnail.For(group, Size);
        MediaGeometry fromPath = ObjectThumbnail.For(Rect(100, 50), Size)!;

        Assert.NotNull(fromGroup);
        Assert.Equal(fromPath.Bounds.Width, fromGroup!.Bounds.Width, 1);
        Assert.Equal(fromPath.Bounds.Height, fromGroup.Bounds.Height, 1);
    }
}
