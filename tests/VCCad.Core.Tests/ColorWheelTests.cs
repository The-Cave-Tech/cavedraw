using VCCad.Core.Color;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The spectrum ring, the inscribed triangle, the barycentric colour model inside
/// it and the picker model that ties them together.
///
/// Two things here are easy to get subtly wrong and are pinned deliberately:
/// the *direction* of the corners (clockwise from the selected point: white,
/// then black) and the colour/position inverse (click a point, read the colour,
/// put the marker back — it must land where it started).
/// </summary>
public class ColorWheelTests
{
    // sin(120°) = cos(30°); the fixture uses a circle of radius 100, so the two
    // non-selected corners are at x = -50 and y = ±100·√3/2.
    private const double Root3Over2 = 0.86602540378443864676372317075294;

    private static readonly Point2D Center = new(0.0, 0.0);
    private const double Radius = 100.0;

    private static readonly ColorRgb[] PickableColours =
    {
        ColorRgb.White,
        ColorRgb.Black,
        ColorRgb.Red,
        ColorRgb.Green,
        ColorRgb.Blue,
        ColorRgb.Gray,
        ColorRgb.FromBytes(255, 128, 0),
        ColorRgb.FromBytes(51, 136, 204),
        ColorRgb.FromBytes(200, 30, 90),
        new(0.13, 0.71, 0.42),
        new(0.0, 0.0, 1.0),
        new(1.0, 1.0, 0.0),
    };

    public static IEnumerable<object[]> SelectedAngles()
    {
        yield return new object[] { 0.0 };
        yield return new object[] { 37.5 };
        yield return new object[] { 90.0 };
        yield return new object[] { 123.456 };
        yield return new object[] { 200.0 };
        yield return new object[] { 270.0 };
        yield return new object[] { 359.0 };
    }

    public static IEnumerable<object[]> Weights()
    {
        yield return new object[] { 0.3, 0.4, 0.3 };
        yield return new object[] { 0.5, 0.25, 0.25 };
        yield return new object[] { 0.0, 0.5, 0.5 };
        yield return new object[] { 1.0, 0.0, 0.0 };
        yield return new object[] { 0.0, 1.0, 0.0 };
        yield return new object[] { 0.0, 0.0, 1.0 };
        yield return new object[] { 0.2, 0.3, 0.5 };
    }

    public static IEnumerable<object[]> PickableColourData()
        => PickableColours.Select(c => new object[] { c });

    private static void AssertColor(ColorRgb expected, ColorRgb actual, int precision = 9)
    {
        Assert.Equal(expected.R, actual.R, precision);
        Assert.Equal(expected.G, actual.G, precision);
        Assert.Equal(expected.B, actual.B, precision);
        Assert.Equal(expected.A, actual.A, precision);
    }

    private static void AssertPoint(Point2D expected, Point2D actual, double tolerance = 1e-9)
        => Assert.True(expected.NearlyEquals(actual, tolerance), $"Expected {expected}, got {actual}.");

    private static double SignedArea(Point2D a, Point2D b, Point2D c)
        => 0.5 * ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));

    // ---- Ring -------------------------------------------------------------

    [Theory]
    [InlineData(-720.0, 0.0)]
    [InlineData(-359.0, 1.0)]
    [InlineData(-30.0, 330.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(45.25, 45.25)]
    [InlineData(180.0, 180.0)]
    [InlineData(359.999, 359.999)]
    [InlineData(360.0, 0.0)]
    [InlineData(720.5, 0.5)]
    public void RingHueAndAngle_RoundTripWithNoDrift(double angle, double expected)
    {
        // Expected wrap values are spelled out rather than compared against
        // NormalizeAngle, so a NormalizeAngle that stopped wrapping cannot make
        // this test agree with itself.
        Assert.Equal(expected, SpectrumRing.NormalizeAngle(angle), 12);
        Assert.Equal(expected, SpectrumRing.HueForAngle(angle), 12);
        Assert.Equal(expected, SpectrumRing.AngleForHue(SpectrumRing.HueForAngle(angle)), 12);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.0, 0.0)]
    [InlineData(60.0, 1.0, 1.0, 0.0)]
    [InlineData(120.0, 0.0, 1.0, 0.0)]
    [InlineData(180.0, 0.0, 1.0, 1.0)]
    [InlineData(240.0, 0.0, 0.0, 1.0)]
    [InlineData(300.0, 1.0, 0.0, 1.0)]
    public void RingColorAtAngle_IsTheStandardHueWheel(double angle, double r, double g, double b)
    {
        AssertColor(new ColorRgb(r, g, b), SpectrumRing.ColorAtAngle(angle), 12);
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void RingColorAtAngle_IsPeriodic(double angle)
        => AssertColor(SpectrumRing.ColorAtAngle(angle), SpectrumRing.ColorAtAngle(angle + 360.0), 12);

    [Fact]
    public void RingAngleAtPoint_PinsTheScreenClockwiseConvention()
    {
        // x right, y down: +Y is 90°, because going right → down → left → up is
        // the clockwise direction this model and the renderers share.
        Assert.Equal(0.0, SpectrumRing.AngleAtPoint(Center, new Point2D(1.0, 0.0)), 9);
        Assert.Equal(90.0, SpectrumRing.AngleAtPoint(Center, new Point2D(0.0, 1.0)), 9);
        Assert.Equal(180.0, SpectrumRing.AngleAtPoint(Center, new Point2D(-1.0, 0.0)), 9);
        Assert.Equal(270.0, SpectrumRing.AngleAtPoint(Center, new Point2D(0.0, -1.0)), 9);
        Assert.Equal(45.0, SpectrumRing.AngleAtPoint(Center, new Point2D(1.0, 1.0)), 9);
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void RingPointAndAngle_AreInverse(double angle)
    {
        Point2D point = SpectrumRing.PointAtAngle(Center, Radius, angle);
        Assert.Equal(SpectrumRing.NormalizeAngle(angle), SpectrumRing.AngleAtPoint(Center, point), 9);
    }

    // ---- Triangle corners -------------------------------------------------

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleCorners_FirstSitsOnTheRingAtTheSelectedAngle(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);
        AssertPoint(SpectrumRing.PointAtAngle(Center, Radius, angle), corners.First);
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleCorners_AllThreeTouchTheRing(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);
        Assert.True(corners.AllTouchRing(Center, Radius, 1e-9), $"Corners {corners} do not all touch the ring.");
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleCorners_AreEquilateralWithSideRoot3Radius(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);
        double side = Radius * Math.Sqrt(3.0);
        Assert.Equal(side, corners.SideLength, 9);
        Assert.Equal(side, corners.Second.DistanceTo(corners.Third), 9);
        Assert.Equal(side, corners.Third.DistanceTo(corners.First), 9);
    }

    [Fact]
    public void TriangleCorners_AtZeroDegrees_SecondIsClockwiseDownAndThirdIsClockwiseUp()
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 0.0);

        AssertPoint(new Point2D(100.0, 0.0), corners.First);
        // Clockwise from pointing right (screen y down) is *downwards*, i.e. the
        // lower-left corner, not the upper-left one.
        AssertPoint(new Point2D(-50.0, 100.0 * Root3Over2), corners.Second);
        AssertPoint(new Point2D(-50.0, -100.0 * Root3Over2), corners.Third);
        Assert.True(corners.Second.Y > corners.First.Y, "Second corner must be clockwise (below) the first.");
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleCorners_AreOrderedClockwiseInTheScreenFrame(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);

        // Positive signed area in a y-down frame means the vertex order
        // First → Second → Third turns clockwise on screen.
        Assert.True(
            SignedArea(corners.First, corners.Second, corners.Third) > 0.0,
            $"Corners {corners} are not clockwise for angle {angle}.");
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleCorners_SecondIsPlus120AndThirdIsPlus240(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);
        AssertPoint(SpectrumRing.PointAtAngle(Center, Radius, angle + 120.0), corners.Second);
        AssertPoint(SpectrumRing.PointAtAngle(Center, Radius, angle + 240.0), corners.Third);
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleCornerColors_ClockwiseFromSelectedIsWhiteThenBlack(double angle)
    {
        CornerColors colors = ColorTriangle.CornerColors(angle);

        AssertColor(SpectrumRing.ColorAtAngle(angle), colors.First);
        AssertColor(ColorRgb.White, colors.Second);
        AssertColor(ColorRgb.Black, colors.Third);
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void TriangleSampledAtItsCorners_IsHueThenWhiteThenBlack(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);
        CornerColors colors = ColorTriangle.CornerColors(angle);

        AssertColor(SpectrumRing.ColorAtAngle(angle), ColorTriangle.ColorAt(corners.First, corners, colors));
        AssertColor(ColorRgb.White, ColorTriangle.ColorAt(corners.Second, corners, colors));
        AssertColor(ColorRgb.Black, ColorTriangle.ColorAt(corners.Third, corners, colors));
    }

    // ---- Barycentric ------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Barycentric_AtEachCornerIsTheUnitWeight(int corner)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 37.5);
        Barycentric weights = ColorTriangle.Barycentric(corners[corner], corners);

        Assert.Equal(corner == 0 ? 1.0 : 0.0, weights.First, 9);
        Assert.Equal(corner == 1 ? 1.0 : 0.0, weights.Second, 9);
        Assert.Equal(corner == 2 ? 1.0 : 0.0, weights.Third, 9);
    }

    [Fact]
    public void Barycentric_CentroidIsEqualThirds()
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 200.0);
        Barycentric weights = ColorTriangle.Barycentric(corners.Centroid, corners);

        Assert.Equal(1.0 / 3.0, weights.First, 9);
        Assert.Equal(1.0 / 3.0, weights.Second, 9);
        Assert.Equal(1.0 / 3.0, weights.Third, 9);
        Assert.True(weights.IsInside());
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(12.0, -8.0)]
    [InlineData(-30.0, 40.0)]
    [InlineData(60.0, 20.0)]
    [InlineData(-80.0, -50.0)]
    public void Barycentric_WeightsAlwaysSumToOne_EvenOutsideTheTriangle(double ox, double oy)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 37.5);
        Barycentric weights = ColorTriangle.Barycentric(new Point2D(ox, oy), corners);
        Assert.Equal(1.0, weights.Sum, 12);
    }

    [Fact]
    public void Barycentric_PointBeyondAnEdgeHasANegativeWeight()
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 0.0);
        Barycentric weights = ColorTriangle.Barycentric(new Point2D(-200.0, 0.0), corners);

        Assert.True(weights.First < 0.0, $"Expected a negative first weight, got {weights}.");
        Assert.False(weights.IsInside());
    }

    [Fact]
    public void Barycentric_OnTheSecondThirdEdge_TheOppositeWeightIsZero()
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 200.0);
        Point2D midpoint = corners.Second.LerpTo(corners.Third, 0.5);
        Barycentric weights = ColorTriangle.Barycentric(midpoint, corners);

        Assert.Equal(0.0, weights.First, 9);
        Assert.Equal(0.5, weights.Second, 9);
        Assert.Equal(0.5, weights.Third, 9);
    }

    [Theory]
    [MemberData(nameof(Weights))]
    public void Barycentric_PointFromWeightsInvertsBarycentric(double w0, double w1, double w2)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 123.456);
        Point2D point = ColorTriangle.PointFromWeights(new Barycentric(w0, w1, w2), corners);
        Barycentric back = ColorTriangle.Barycentric(point, corners);

        Assert.Equal(w0, back.First, 9);
        Assert.Equal(w1, back.Second, 9);
        Assert.Equal(w2, back.Third, 9);
    }

    [Fact]
    public void Barycentric_ClampedPullsAnOutsidePointOntoTheEdge()
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 0.0);
        var outside = new Point2D(-200.0, 0.0);
        Barycentric clamped = ColorTriangle.Barycentric(outside, corners).Clamped();

        Assert.Equal(0.0, clamped.First, 9);
        Assert.Equal(0.5, clamped.Second, 9);
        Assert.Equal(0.5, clamped.Third, 9);
        AssertPoint(
            corners.Second.LerpTo(corners.Third, 0.5),
            ColorTriangle.ClampToTriangle(outside, corners));
    }

    // ---- Colour mapping and the marker ------------------------------------

    [Theory]
    [MemberData(nameof(Weights))]
    public void WeightsForColor_IsTheExactInverseOfColorAt(double w0, double w1, double w2)
    {
        CornerColors colors = ColorTriangle.CornerColors(210.0);
        var weights = new Barycentric(w0, w1, w2);

        ColorRgb color = ColorTriangle.ColorAt(weights, colors);
        Barycentric back = ColorTriangle.WeightsForColor(color);

        Assert.Equal(w0, back.First, 9);
        Assert.Equal(w1, back.Second, 9);
        Assert.Equal(w2, back.Third, 9);
    }

    [Theory]
    [MemberData(nameof(Weights))]
    public void MarkerPoint_InvertsColorAtPoint(double w0, double w1, double w2)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, 145.0);
        CornerColors colors = ColorTriangle.CornerColors(145.0);
        var weights = new Barycentric(w0, w1, w2);

        Point2D point = ColorTriangle.PointFromWeights(weights, corners);
        ColorRgb color = ColorTriangle.ColorAt(point, corners, colors);
        Point2D marker = ColorTriangle.PointForColor(color, corners);

        AssertPoint(point, marker);
    }

    [Theory]
    [MemberData(nameof(PickableColourData))]
    public void ColorRoundTrip_ColourToMarkerBackToColour(ColorRgb color)
    {
        var model = new ColorPickerModel(Center, Radius, color);
        ColorRgb back = ColorTriangle.ColorAt(model.MarkerPoint, model.Corners, model.CornerColors);
        AssertColor(color, back);
    }

    [Fact]
    public void WhiteAndBlack_AreExactlyReachableAtTheirCorners()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);
        model.SelectAngle(0.0);

        model.SetColor(ColorRgb.White);
        AssertColor(ColorRgb.White, model.Color);
        AssertPoint(model.Corners.Second, model.MarkerPoint);

        model.SetColor(ColorRgb.Black);
        AssertColor(ColorRgb.Black, model.Color);
        AssertPoint(model.Corners.Third, model.MarkerPoint);

        model.SetColor(SpectrumRing.ColorAtAngle(0.0));
        AssertPoint(model.Corners.First, model.MarkerPoint);
    }

    [Fact]
    public void GreyColour_MarkerSitsOnTheWhiteBlackEdge()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);
        model.SetColor(new ColorRgb(0.5, 0.5, 0.5));

        Barycentric weights = ColorTriangle.WeightsForColor(model.Color);
        Assert.Equal(0.0, weights.First, 9);
        Assert.Equal(0.5, weights.Second, 9);
        Assert.Equal(0.5, weights.Third, 9);
        AssertPoint(model.Corners.Second.LerpTo(model.Corners.Third, 0.5), model.MarkerPoint);
    }

    [Theory]
    [MemberData(nameof(SelectedAngles))]
    public void Gradient_HasThreeStopsAtTheCornersWithTheCornerColours(double angle)
    {
        TriangleCorners corners = ColorTriangle.Corners(Center, Radius, angle);
        CornerColors colors = ColorTriangle.CornerColors(angle);
        TriangleGradient gradient = ColorTriangle.Gradient(corners, colors);

        Assert.Equal(3, gradient.Stops.Count);
        AssertPoint(corners.First, gradient.First.Position);
        AssertPoint(corners.Second, gradient.Second.Position);
        AssertPoint(corners.Third, gradient.Third.Position);
        AssertColor(colors.First, gradient.First.Color);
        AssertColor(colors.Second, gradient.Second.Color);
        AssertColor(colors.Third, gradient.Third.Color);
    }

    // ---- Picker model -----------------------------------------------------

    [Theory]
    [MemberData(nameof(Weights))]
    public void Model_SelectTrianglePoint_MarkerStaysUnderTheClick(double w0, double w1, double w2)
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);
        TriangleCorners corners = model.Corners;
        Point2D click = ColorTriangle.PointFromWeights(new Barycentric(w0, w1, w2), corners);

        model.SelectTrianglePoint(click);

        AssertPoint(click, model.MarkerPoint);
        AssertColor(ColorTriangle.ColorAt(click, corners, model.CornerColors), model.Color);
    }

    [Fact]
    public void Model_SelectTrianglePoint_OutsideIsClampedOntoTheEdge()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);
        var outside = new Point2D(-200.0, 0.0);

        model.SelectTrianglePoint(outside);

        AssertPoint(ColorTriangle.ClampToTriangle(outside, model.Corners), model.MarkerPoint);
        Assert.True(
            ColorTriangle.Barycentric(model.MarkerPoint, model.Corners).IsInside(1e-9),
            "A clamped click must land inside the triangle.");
    }

    [Fact]
    public void Model_SelectRingPoint_ChangesHueAndKeepsSaturationAndValue()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.FromBytes(51, 136, 204));
        HsvColor before = HsvColor.FromRgb(model.Color);

        model.SelectRingPoint(SpectrumRing.PointAtAngle(Center, Radius, 300.0));
        HsvColor after = HsvColor.FromRgb(model.Color);

        Assert.Equal(300.0, model.AngleDegrees, 9);
        Assert.Equal(300.0, after.H, 7);
        Assert.Equal(before.S, after.S, 9);
        Assert.Equal(before.V, after.V, 9);
    }

    [Fact]
    public void Model_SelectRingPoint_AtAHueBoundarySelectsThatHueExactly()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);

        model.SelectRingPoint(SpectrumRing.PointAtAngle(Center, Radius, 120.0));
        AssertColor(ColorRgb.Green, model.Color, 9);
        AssertPoint(model.Corners.First, model.MarkerPoint);
    }

    [Theory]
    [InlineData(37.0)]
    [InlineData(180.0)]
    [InlineData(359.5)]
    [InlineData(-20.0)]
    public void Model_SelectAngleReturningToTheOriginalRestoresTheColour(double offset)
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.FromBytes(51, 136, 204));
        ColorRgb original = model.Color;
        double originalAngle = model.AngleDegrees;

        model.SelectAngle(originalAngle + offset);
        model.SelectAngle(originalAngle);

        AssertColor(original, model.Color);
        Assert.Equal(originalAngle, model.AngleDegrees, 9);
    }

    [Fact]
    public void Model_ReorientingTheRingDoesNotDrift()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.FromBytes(200, 30, 90));
        ColorRgb original = model.Color;

        for (int i = 0; i < 20; i++)
        {
            model.SelectAngle(model.AngleDegrees + 1.0);
        }

        model.SelectAngle(HsvColor.FromRgb(original).H);
        AssertColor(original, model.Color);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(120.0)]
    [InlineData(240.0)]
    [InlineData(37.5)]
    public void Model_SetColor_OrientsTheRingToTheColoursHue(double hue)
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Black);
        ColorRgb color = HsvColor.HueColor(hue);

        model.SetColor(color);

        Assert.Equal(hue, model.AngleDegrees, 9);
        Assert.Equal(hue, model.Hue, 9);
        AssertColor(color, model.HueColor);
    }

    [Fact]
    public void Model_SetColor_ForGreyKeepsTheAngleBecauseHueIsUndefined()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);
        model.SelectAngle(200.0);

        model.SetColor(ColorRgb.Gray);

        Assert.Equal(200.0, model.AngleDegrees, 9);
        Assert.Equal(0.0, HsvColor.FromRgb(model.Color).S, 9);
        Assert.Equal("#808080", model.ToHex());
    }

    [Fact]
    public void Model_SetHexAndHsl_AgreeWithTheSelectedColour()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Black);

        model.SetHex("#3388CC");
        AssertColor(ColorRgb.FromBytes(0x33, 0x88, 0xCC), model.Color, 12);
        Assert.Equal("#3388CC", model.ToHex());
        Assert.InRange(model.Hue, 620.0 / 3.0 - 1e-9, 620.0 / 3.0 + 1e-9);

        var hsl = new HslColor(210.0, 0.5, 0.4);
        model.SetHsl(hsl);
        AssertColor(hsl.ToRgb(), model.Color);
        Assert.Equal(210.0, model.Hue, 7);
    }

    [Fact]
    public void Model_SetAlpha_IsClampedAndCarriedSeparately()
    {
        var model = new ColorPickerModel(Center, Radius, ColorRgb.Red);

        model.SetAlpha(0.4);
        Assert.Equal(0.4, model.Alpha, 12);
        Assert.Equal(0.4, model.ColorWithAlpha.A, 12);

        model.SetAlpha(2.0);
        Assert.Equal(1.0, model.Alpha, 12);
        model.SetAlpha(-1.0);
        Assert.Equal(0.0, model.Alpha, 12);
    }

    [Fact]
    public void Model_Snapshot_ReportsEveryPanelValueConsistently()
    {
        // A pastel, not a fully saturated colour: for a colour on the ring the
        // marker and the first corner coincide, which would hide a snapshot that
        // reported the wrong one.
        var model = new ColorPickerModel(new Point2D(300.0, 200.0), 120.0, ColorRgb.FromBytes(0xCC, 0x99, 0x66));
        model.SetAlpha(0.4);

        Assert.NotEqual(model.RingPoint, model.MarkerPoint);

        ColorPickerSnapshot snapshot = model.Snapshot();

        Assert.Equal(model.AngleDegrees, snapshot.AngleDegrees, 12);
        Assert.Equal(model.Hue, snapshot.Hue, 12);
        Assert.Equal(model.Hsl.S, snapshot.Saturation, 12);
        Assert.Equal(model.Hsl.L, snapshot.Lightness, 12);
        AssertColor(model.Color, snapshot.Color, 12);
        Assert.Equal("#CC9966", snapshot.Hex);
        Assert.Equal(0.4, snapshot.Alpha, 12);
        AssertPoint(model.RingPoint, snapshot.RingPoint);
        AssertPoint(model.MarkerPoint, snapshot.MarkerPoint);
        Assert.Equal(model.Corners, snapshot.Corners);
        Assert.Equal(model.CornerColors, snapshot.CornerColors);
        Assert.Equal(model.Gradient, snapshot.Gradient);
        AssertPoint(snapshot.Corners.First, snapshot.Gradient.First.Position);
    }
}
