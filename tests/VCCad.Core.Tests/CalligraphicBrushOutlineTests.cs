using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The calligraphic nib: an ellipse swept along the path, so the stroke's width is the nib's own
/// projection onto the direction of travel (issue #99).
///
/// These tests measure the **outline**, never the parameters: a nib that is stored and then ignored
/// would pass every test that read the model back. A nib is edge-on to the direction of travel when its
/// long axis lies along it - the thin edge leads - and broadside when its long axis lies across it.
/// Those two cases have closed forms a person recognises - the diameter and the diameter times the
/// roundness - so they are asserted as those numbers rather than as whichever number the code produces.
/// </summary>
public class CalligraphicBrushOutlineTests
{
    private const double Diameter = 20.0;
    private const double Roundness = 0.25;

    /// <summary>The nib as the issue describes it: diameter, angle in degrees, roundness 0..1.</summary>
    private static BrushSpec Nib(double angleDegrees, double roundness = Roundness, double diameter = Diameter)
        => BrushSpec.Calligraphic("Nib", angleDegrees, roundness, diameter);

    private static StrokeSpec Stroked(BrushSpec nib, double width = 1.0)
        => new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4) { Brush = nib };

    private static PathItem Line(params Point2D[] points)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        return path;
    }

    /// <summary>The outline of a stroke, which a brush always produces - a nib cannot be stroked by a renderer.</summary>
    private static IReadOnlyList<Point2D> Outline(PathItem path, StrokeSpec stroke)
    {
        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);
        Assert.True(plan.IsOutline, "a stroke carrying a nib is a filled region, not a stroke a renderer widens");
        return Assert.Single(plan.Outlines);
    }

    /// <summary>
    /// The width of the outline measured **across** a path travelling in <paramref name="travelDegrees"/>:
    /// the extent of the outline along the normal to the path, which is where a nib's width is drawn.
    /// </summary>
    private static double Across(IReadOnlyList<Point2D> outline, double travelDegrees = 0.0)
    {
        double radians = travelDegrees * Math.PI / 180.0;
        var normal = new Vector2D(-Math.Sin(radians), Math.Cos(radians));

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (Point2D point in outline)
        {
            double distance = (point.X * normal.X) + (point.Y * normal.Y);
            min = Math.Min(min, distance);
            max = Math.Max(max, distance);
        }

        return max - min;
    }

    /// <summary>
    /// The full width a nib projects onto a path travelling in <paramref name="travelDegrees"/>.
    ///
    /// The nib is the ellipse with semi-axes <c>diameter/2</c> along <paramref name="nibAngleDegrees"/>
    /// and <c>roundness * diameter/2</c> across it, and the width the pen lays down is twice the distance
    /// from its centre to its edge **measured across the direction of travel**. For an ellipse that is the
    /// support distance <c>sqrt(a^2 sin^2(theta) + b^2 cos^2(theta))</c>, where theta is the angle between
    /// the travel direction and the nib's long axis: at theta = 0 the long axis leads and the thin
    /// semi-axis answers, and at 90 degrees the long axis answers.
    /// </summary>
    private static double Projected(double nibAngleDegrees, double travelDegrees,
        double roundness = Roundness, double diameter = Diameter)
    {
        double major = diameter / 2.0;
        double minor = major * roundness;
        double theta = (travelDegrees - nibAngleDegrees) * Math.PI / 180.0;
        double sin = Math.Sin(theta);
        double cos = Math.Cos(theta);
        return 2.0 * Math.Sqrt((major * major * sin * sin) + (minor * minor * cos * cos));
    }

    // ---------------------------------------------------------------- the two named cases

    /// <summary>
    /// **Edge-on draws thin.** The nib's long axis lies along the path, so the width across the path is
    /// the nib's short axis: diameter times roundness, and nothing else.
    /// </summary>
    [Fact]
    public void AnEdgeOnNibDrawsTheNibsShortAxis()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        double width = Across(Outline(path, Stroked(Nib(angleDegrees: 0))));

        Assert.Equal(Diameter * Roundness, width, 6);
    }

    /// <summary>
    /// **Broadside draws thick.** With the nib turned a quarter turn its long axis lies across the path,
    /// so the width across the path is the diameter itself.
    /// </summary>
    [Fact]
    public void ABroadsideNibDrawsTheNibsLongAxis()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        double width = Across(Outline(path, Stroked(Nib(angleDegrees: 90))));

        Assert.Equal(Diameter, width, 6);
    }

    /// <summary>
    /// **The width is the projection, not a plausible number.** Every angle between the two named cases
    /// is the nib's own support distance across the direction of travel - so an angle that merely
    /// interpolates between thin and thick would fail here.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(135)]
    public void TheWidthIsTheNibsProjectionAtEveryAngle(double nibAngle)
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        double width = Across(Outline(path, Stroked(Nib(nibAngle))));

        Assert.Equal(Projected(nibAngle, travelDegrees: 0), width, 6);
    }

    /// <summary>
    /// The same nib on a path drawn **the other way** draws the same band: the width depends on the line
    /// the path follows, not on which end a person started at.
    /// </summary>
    [Fact]
    public void TheDirectionThePathIsDrawnInDoesNotChangeTheWidth()
    {
        BrushSpec nib = Nib(angleDegrees: 30);

        double forwards = Across(Outline(Line(new Point2D(0, 0), new Point2D(100, 0)), Stroked(nib)));
        double backwards = Across(Outline(Line(new Point2D(100, 0), new Point2D(0, 0)), Stroked(nib)));

        Assert.Equal(forwards, backwards, 6);
    }

    /// <summary>
    /// And a vertical path sees the nib the other way round, which is the whole point of an angle: a nib
    /// that is edge-on to a horizontal line is broadside to a vertical one.
    /// </summary>
    [Fact]
    public void TheSameNibIsBroadsideToAPathItCrossesAtRightAngles()
    {
        BrushSpec nib = Nib(angleDegrees: 0);

        double horizontal = Across(Outline(Line(new Point2D(0, 0), new Point2D(100, 0)), Stroked(nib)));
        double vertical = Across(Outline(Line(new Point2D(0, 0), new Point2D(0, 100)), Stroked(nib)),
            travelDegrees: 90);

        Assert.Equal(Diameter * Roundness, horizontal, 6);
        Assert.Equal(Diameter, vertical, 6);
    }

    /// <summary>
    /// **Rotating the nib a quarter turn swaps the two widths.** The thin one becomes the thick one, and
    /// both are still the nib's own axes - so an implementation that merely scaled the stroke would fail.
    /// </summary>
    [Fact]
    public void RotatingTheNibByNinetyDegreesSwapsTheTwoWidths()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        double edgeOn = Across(Outline(path, Stroked(Nib(angleDegrees: 0))));
        double broadside = Across(Outline(path, Stroked(Nib(angleDegrees: 90))));

        Assert.Equal(Diameter * Roundness, edgeOn, 6);
        Assert.Equal(Diameter, broadside, 6);
        Assert.NotEqual(edgeOn, broadside);
    }

    // ---------------------------------------------------------------- the nib multiplies nothing else

    /// <summary>
    /// The nib is the **whole** width: a stroke's own width, and a width profile, do not scale it. A nib
    /// that inherited the stroke's width would draw a different line for the same brush on two paths that
    /// happen to have been stroked at different widths.
    /// </summary>
    [Fact]
    public void TheStrokeWidthAndProfileDoNotScaleTheNib()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));
        StrokeSpec stroke = Stroked(Nib(angleDegrees: 90), width: 3) with
        {
            WidthProfile = WidthProfileSpec.Taper(40, 40),
        };

        Assert.Equal(Diameter, Across(Outline(path, stroke)), 6);
    }

    /// <summary>A round nib (roundness 1) is an ordinary round pen: the diameter, whichever way the path runs.</summary>
    [Fact]
    public void ARoundNibDrawsTheDiameterWhateverTheDirection()
    {
        BrushSpec round = Nib(angleDegrees: 45, roundness: 1.0);

        Assert.Equal(Diameter, Across(Outline(Line(new Point2D(0, 0), new Point2D(100, 0)), Stroked(round))), 6);
        Assert.Equal(
            Diameter,
            Across(Outline(Line(new Point2D(0, 0), new Point2D(0, 100)), Stroked(round)), travelDegrees: 90),
            6);
    }

    // ---------------------------------------------------------------- a curve

    /// <summary>
    /// **A non-round nib must vary around a circle.** The direction of travel turns through a full turn,
    /// so the nib's long axis is broadside to the path at two places and edge-on at the two between; a
    /// nib applied as one width for the whole path would draw a constant-width ring and fail here.
    ///
    /// The band is measured **around** the ring: the outline is binned by the angle round the centre, and
    /// the thickness in each bin is how far apart its outer and inner edges are. That is the drawn width,
    /// read from the geometry rather than from the nib.
    /// </summary>
    [Fact]
    public void ANonRoundNibVariesAroundACircularPath()
    {
        var centre = new Point2D(0, 0);
        PathItem circle = PathFactory.CreateEllipse("circle", centre, radiusX: 50, radiusY: 50);

        IReadOnlyList<Point2D> outline = Outline(circle, Stroked(Nib(angleDegrees: 0)));
        (double thinnest, double thickest) = BandByAngle(outline, centre, bins: 360);

        Assert.Equal(Diameter * Roundness, thinnest, 1);
        Assert.Equal(Diameter, thickest, 1);
    }

    /// <summary>
    /// The thickness of a closed outline at each angle round a centre: the far edge minus the near one.
    ///
    /// A closed stroked path's outline runs along one edge and back along the other, so a bin at an angle
    /// holds the two sides of the band there. Bins no point landed in are skipped rather than read as
    /// zero, which a coarse binning would otherwise turn into a false "thinnest".
    /// </summary>
    private static (double Thinnest, double Thickest) BandByAngle(
        IReadOnlyList<Point2D> outline, Point2D centre, int bins)
    {
        var near = new double[bins];
        var far = new double[bins];
        Array.Fill(near, double.PositiveInfinity);
        Array.Fill(far, double.NegativeInfinity);

        foreach (Point2D point in outline)
        {
            double dx = point.X - centre.X;
            double dy = point.Y - centre.Y;
            double radius = Math.Sqrt((dx * dx) + (dy * dy));
            double angle = Math.Atan2(dy, dx);
            int bin = (int)Math.Floor((angle + Math.PI) / (2.0 * Math.PI) * bins) % bins;
            near[bin] = Math.Min(near[bin], radius);
            far[bin] = Math.Max(far[bin], radius);
        }

        double thinnest = double.PositiveInfinity;
        double thickest = 0.0;
        for (int i = 0; i < bins; i++)
        {
            if (double.IsPositiveInfinity(near[i]))
            {
                continue;
            }

            thinnest = Math.Min(thinnest, far[i] - near[i]);
            thickest = Math.Max(thickest, far[i] - near[i]);
        }

        return (thinnest, thickest);
    }
}
