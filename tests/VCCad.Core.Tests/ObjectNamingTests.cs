using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What an object is called before anybody names it.
///
/// A panel full of "Path" tells the person nothing. The shape tells them everything, and it
/// is derivable: two corners joined by something straight is a line, four corners at right
/// angles is a rectangle, four curves bent by the quarter-circle handle is an ellipse.
///
/// The rule is geometry, so these are built as geometry rather than by naming a fixture —
/// a test that sets Name = "Rectangle" would pass without the rule existing.
/// </summary>
public class ObjectNamingTests
{
    private const double Kappa = 0.5522847498307936;

    private static PathItem Open(IEnumerable<Point2D> points)
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D p in points)
        {
            sub.Nodes.Add(new PathNode(p));
        }

        return path;
    }

    private static PathItem Closed(IEnumerable<Point2D> points)
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        foreach (Point2D p in points)
        {
            sub.Nodes.Add(new PathNode(p));
        }

        return path;
    }

    /// <summary>A closed four-corner ellipse from a bounding box, handles and all.</summary>
    private static PathItem Ellipse(double x, double y, double w, double h)
    {
        double rx = w / 2, ry = h / 2;
        double cx = x + rx, cy = y + ry;
        double kx = rx * Kappa, ky = ry * Kappa;

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);

        // Right, bottom, left, top, each handle tangent and kappa long.
        sub.Nodes.Add(new PathNode(
            new Point2D(cx + rx, cy),
            new Point2D(cx + rx, cy + ky),
            new Point2D(cx + rx, cy - ky)));
        sub.Nodes.Add(new PathNode(
            new Point2D(cx, cy + ry),
            new Point2D(cx + kx, cy + ry),
            new Point2D(cx - kx, cy + ry)));
        sub.Nodes.Add(new PathNode(
            new Point2D(cx - rx, cy),
            new Point2D(cx - rx, cy - ky),
            new Point2D(cx - rx, cy + ky)));
        sub.Nodes.Add(new PathNode(
            new Point2D(cx, cy - ry),
            new Point2D(cx - kx, cy - ry),
            new Point2D(cx + kx, cy - ry)));

        return path;
    }

    /// <summary>A text item holding the given content, built the way the editor builds one.</summary>
    private static TextItem Text(string content)
    {
        var item = new TextItem();
        if (content.Length > 0)
        {
            TextEditing.Insert(item, 0, content);
        }

        return item;
    }

    [Fact]
    public void TwoCornersJoinedStraightIsALine()
        => Assert.Equal("Line", ObjectNaming.LabelFor(
            Open(new[] { new Point2D(0, 0), new Point2D(100, 0) })));

    [Fact]
    public void ADiagonalLineIsStillALine()
        => Assert.Equal("Line", ObjectNaming.LabelFor(
            Open(new[] { new Point2D(10, 20), new Point2D(90, 40) })));

    [Fact]
    public void AnOpenPathOfStraightSegmentsIsAPath()
        => Assert.Equal("Path", ObjectNaming.LabelFor(
            Open(new[]
            {
                new Point2D(0, 0), new Point2D(50, 0), new Point2D(50, 50),
            })));

    [Fact]
    public void AnOpenPathWithACurveIsACurve()
    {
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0), new Point2D(0, 0), new Point2D(20, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0), new Point2D(80, 0), new Point2D(100, 0)));

        Assert.Equal("Curve", ObjectNaming.LabelFor(path));
    }

    [Fact]
    public void FourCornersAtRightAnglesIsARectangle()
        => Assert.Equal("Rectangle", ObjectNaming.LabelFor(
            Closed(new[]
            {
                new Point2D(0, 0), new Point2D(200, 0),
                new Point2D(200, 100), new Point2D(0, 100),
            })));

    [Fact]
    public void ARotatedRectangleIsStillARectangle()
    {
        // A person calls this a rectangle. The corners meet at right angles; only the page
        // is at an angle to them.
        Point2D[] corners =
        {
            Rotate(new Point2D(0, 0), 30), Rotate(new Point2D(200, 0), 30),
            Rotate(new Point2D(200, 100), 30), Rotate(new Point2D(0, 100), 30),
        };

        Assert.Equal("Rectangle", ObjectNaming.LabelFor(Closed(corners)));
    }

    private static Point2D Rotate(Point2D p, double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        return new Point2D(
            (p.X * Math.Cos(r)) - (p.Y * Math.Sin(r)),
            (p.X * Math.Sin(r)) + (p.Y * Math.Cos(r)));
    }

    [Fact]
    public void FourStraightCornersThatAreNotSquareAreAPath()
    {
        // A quadrilateral, not a rectangle: the corners do not meet at right angles.
        Assert.Equal("Path", ObjectNaming.LabelFor(
            Closed(new[]
            {
                new Point2D(0, 0), new Point2D(200, 0),
                new Point2D(160, 100), new Point2D(0, 100),
            })));
    }

    [Fact]
    public void AFourCornerEllipseIsAnEllipse()
        => Assert.Equal("Ellipse", ObjectNaming.LabelFor(Ellipse(10, 10, 200, 120)));

    [Fact]
    public void ACircleIsAnEllipse()
        => Assert.Equal("Ellipse", ObjectNaming.LabelFor(Ellipse(0, 0, 100, 100)));

    [Fact]
    public void FourCurvesThatAreNotAnEllipseAreACurve()
    {
        // Same four corners, straight: a rectangle. Bend one side and it is a curve, not an
        // ellipse, because an ellipse's handles are a specific length.
        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(
            new Point2D(200, 0), new Point2D(150, 60), new Point2D(200, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 100)));

        Assert.Equal("Curve", ObjectNaming.LabelFor(path));
    }

    [Fact]
    public void AClosedPathOfStraightSegmentsWithMoreCornersIsAPath()
        => Assert.Equal("Path", ObjectNaming.LabelFor(
            Closed(new[]
            {
                new Point2D(0, 0), new Point2D(60, 0), new Point2D(80, 40),
                new Point2D(40, 70), new Point2D(0, 40),
            })));

    [Fact]
    public void TextIsLabelledWithItsContents()
    {
        var text = Text("Jalie 3464 - LILLIE - Page 1");
        Assert.Equal("Jalie 3464 - LILLIE - Page 1", ObjectNaming.LabelFor(text));
    }

    [Fact]
    public void LongTextIsTrimmedForTheRow()
    {
        var text = Text(new string('x', 200));
        string label = ObjectNaming.LabelFor(text);

        Assert.True(label.Length <= 40, $"label is {label.Length} characters");
        Assert.EndsWith("…", label);
    }

    [Fact]
    public void ATextRunWhoseTextIsAllWhitespaceStillReadsAsText()
        => Assert.Equal("Text", ObjectNaming.LabelFor(Text("\n   \n")));

    [Fact]
    public void ATextRunWithLineBreaksIsOneLineInTheLabel()
        => Assert.Equal("Hello world", ObjectNaming.LabelFor(
            Text("Hello\nworld")));

    [Fact]
    public void AnEmptyPathIsStillAPath()
        => Assert.Equal("Path", ObjectNaming.LabelFor(new PathItem()));

    [Fact]
    public void AnImageIsAnImage()
        => Assert.Equal("Image", ObjectNaming.LabelFor(new ImageItem()));

    [Fact]
    public void AGroupIsAGroupAndAClippingGroupSaysSo()
    {
        Assert.Equal("Group", ObjectNaming.LabelFor(new ArtGroup()));
        Assert.Equal("Clipping Mask", ObjectNaming.LabelFor(new ArtGroup
        {
            Clips = { new ClipSpec() },
        }));
    }

    [Fact]
    public void ATypedNameIsKeptAndADefaultNameIsNot()
    {
        PathItem line = Open(new[] { new Point2D(0, 0), new Point2D(10, 0) });

        // Unnamed: the panel says what it is.
        Assert.Equal("Line", ObjectNaming.DisplayName(line));

        // Named: the panel says what the person called it, even after the geometry changes.
        line.Name = "Left sleeve seam";
        line.NameIsUserSet = true;
        Assert.Equal("Left sleeve seam", ObjectNaming.DisplayName(line));

        // And an object whose name came from somewhere other than a person keeps following
        // its geometry.
        PathItem other = Open(new[] { new Point2D(0, 0), new Point2D(10, 0) });
        other.Name = "Path 7";
        Assert.Equal("Line", ObjectNaming.DisplayName(other));
    }

    [Fact]
    public void ShapesMadeOfSeveralPartsAreNotCalledAfterOneOfThem()
    {
        // Two separate straight runs: not a line, because a line is one run.
        var path = new PathItem();
        SubPath a = path.AddSubPath(closed: false);
        a.Nodes.Add(new PathNode(new Point2D(0, 0)));
        a.Nodes.Add(new PathNode(new Point2D(10, 0)));
        SubPath b = path.AddSubPath(closed: false);
        b.Nodes.Add(new PathNode(new Point2D(0, 50)));
        b.Nodes.Add(new PathNode(new Point2D(10, 50)));

        Assert.Equal("Path", ObjectNaming.LabelFor(path));
    }
}
