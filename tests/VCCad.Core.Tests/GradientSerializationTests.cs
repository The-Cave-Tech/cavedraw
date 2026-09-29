using System.Text.Json;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Sidecar round-trip and hostile-input coverage for gradient fills.
///
/// The serializer is the stage that makes the gradient model real: nothing is real until it
/// survives a save and a load. Two rules run through every test here. A valid gradient comes
/// back field for field. A malformed one either loads to something the model can evaluate or
/// is refused with a message that names the offending member — never a NullReferenceException,
/// and never a silently wrong answer.
/// </summary>
public class GradientSerializationTests
{
    // ------------------------------------------------------------------
    // The round trip.
    // ------------------------------------------------------------------

    /// <summary>
    /// A gradient carrying every field the model has, in a single fill: kind, spread, three
    /// stops with distinct positions, colours, opacities, midpoints and one name, both
    /// geometry frames, and freeform points and lines. Each is asserted directly, and the
    /// whole document is also compared by canonical dump so a field the assertions forget is
    /// still caught.
    /// </summary>
    [Fact]
    public void AGradientFillSurvivesTheSaveAndLoadUnchanged()
    {
        GradientSpec gradient = FullyPopulated(GradientKind.Freeform);

        CadDocument doc = DocumentWithGradient(gradient);
        string before = ModelDump.Of(doc);
        CadDocument back = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));

        string after = ModelDump.Of(back);
        Assert.Equal(before, after);

        GradientSpec? got = FillOf(back).Gradient;
        Assert.NotNull(got);
        Assert.Equal(gradient.Kind, got!.Kind);
        Assert.Equal(gradient.Spread, got.Spread);
        AssertStopsEqual(gradient.Stops, got.Stops);
        Assert.Equal(gradient.Start, got.Start);
        Assert.Equal(gradient.End, got.End);
        Assert.Equal(gradient.Center, got.Center);
        Assert.Equal(gradient.RadiusX, got.RadiusX);
        Assert.Equal(gradient.RadiusY, got.RadiusY);
        Assert.Equal(gradient.Rotation, got.Rotation);
        Assert.Equal(gradient.Angle, got.Angle);
        AssertPointsEqual(gradient.Points, got.Points);
        Assert.Equal(gradient.FreeformMode, got.FreeformMode);
        AssertLinesEqual(gradient.Lines, got.Lines);
    }

    [Theory]
    [InlineData(GradientKind.Linear)]
    [InlineData(GradientKind.Radial)]
    [InlineData(GradientKind.Freeform)]
    [InlineData(GradientKind.Conical)]
    public void EveryGradientKindSurvivesTheRoundTrip(GradientKind kind)
    {
        GradientSpec gradient = FullyPopulated(kind);

        CadDocument doc = DocumentWithGradient(gradient);
        CadDocument back = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));

        GradientSpec? got = FillOf(back).Gradient;
        Assert.NotNull(got);
        Assert.Equal(kind, got!.Kind);
        Assert.Equal(ModelDump.Of(doc), ModelDump.Of(back));
    }

    // ------------------------------------------------------------------
    // Backward compatibility: the member did not exist before gradients.
    // ------------------------------------------------------------------

    /// <summary>
    /// A sidecar written by the code that predates gradients: a fill with no <c>Gradient</c>
    /// member at all. It must come back as the solid fill it meant, with no gradient attached.
    /// </summary>
    [Fact]
    public void ASidecarWrittenBeforeGradientsStillDeserializesToItsSolidFill()
    {
        const string legacy =
            "{\"Version\":1,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"legacy\"," +
            "\"Artboards\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"a\"," +
            "\"X\":0,\"Y\":0,\"Width\":100,\"Height\":100,\"Layers\":[" +
            "{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"Name\":\"l\",\"IsVisible\":true," +
            "\"IsLocked\":false,\"Opacity\":1,\"Items\":[" +
            "{\"$kind\":\"path\",\"Id\":\"33333333-3333-3333-3333-333333333333\",\"Name\":\"p\"," +
            "\"IsVisible\":true,\"IsLocked\":false," +
            "\"Fill\":{\"Visible\":true,\"Color\":{\"R\":0.25,\"G\":0.5,\"B\":0.75,\"A\":1},\"Rule\":\"EvenOdd\"}," +
            "\"Stroke\":{\"Visible\":false,\"Color\":null,\"Width\":1,\"Cap\":\"Butt\",\"Join\":\"Miter\"," +
            "\"MiterLimit\":4,\"Alignment\":\"Center\",\"Dash\":null,\"DashOffset\":0}," +
            "\"Opacity\":1,\"SubPaths\":[]}]}]}],\"Orphans\":[]}";

        CadDocument doc = VccadDocumentSerializer.Deserialize(legacy);
        FillSpec fill = FillOf(doc);

        Assert.Null(fill.Gradient);
        Assert.True(fill.IsVisible);
        Assert.Equal(FillRule.EvenOdd, fill.Rule);
        Assert.Equal(new ColorRgb(0.25, 0.5, 0.75), fill.Color);
    }

    /// <summary>
    /// The other direction: a solid fill must serialize without a <c>Gradient</c> member, so a
    /// sidecar written now is byte-identical to one written before the feature existed.
    /// </summary>
    [Fact]
    public void ASolidFillIsSerializedWithoutAGradientMember()
    {
        CadDocument doc = CadDocument.CreateDefault("solid");
        PathItem path = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 10, 10));
        path.Fill = FillSpec.Solid(new ColorRgb(0.2, 0.4, 0.6), FillRule.EvenOdd);
        doc.Artboards[0].Layers[0].AddItem(path);

        string json = VccadDocumentSerializer.Serialize(doc);

        Assert.DoesNotContain("Gradient", json);
        Assert.Null(FillOf(VccadDocumentSerializer.Deserialize(json)).Gradient);
    }

    // ------------------------------------------------------------------
    // The dump a driver with no eyes reads.
    // ------------------------------------------------------------------

    [Fact]
    public void AGradientIsLegibleInTheModelDump()
    {
        GradientSpec gradient = FullyPopulated(GradientKind.Freeform);
        string dump = ModelDump.Of(DocumentWithGradient(gradient));

        Assert.Contains("gradient=Freeform/Reflect", dump);
        Assert.Contains("stops=[0:0.1,0.2,0.3,0.4@0.25~0.3'start';0.37:0.9,0.8,0.7,0.6@0.5~0.75'middle';1:0,0,0,1@0~0.5]", dump);
        Assert.Contains("linear=0.125,0.25->0.875,0.75", dump);
        Assert.Contains("radial=0.4,0.6/0.3/0.7/33.5", dump);
        Assert.Contains("angle=145", dump);
        Assert.Contains("points=[10.5,20.25:0.2,0.4,0.6,0.8@0.35;-5,3:0.11,0.22,0.33,0.44@0.9] mode=Lines lines=[0-1]", dump);
    }

    // ------------------------------------------------------------------
    // Determinism.
    // ------------------------------------------------------------------

    [Fact]
    public void AGradientDocumentSerializesToIdenticalBytesEveryTime()
    {
        CadDocument doc = DocumentWithGradient(FullyPopulated(GradientKind.Freeform));

        string once = VccadDocumentSerializer.Serialize(doc);
        string twice = VccadDocumentSerializer.Serialize(doc);
        Assert.Equal(once, twice);

        // And a save/load/save must not drift either.
        CadDocument back = VccadDocumentSerializer.Deserialize(once);
        Assert.Equal(once, VccadDocumentSerializer.Serialize(back));
    }

    // ------------------------------------------------------------------
    // Hostile input that has a sensible interpretation.
    // ------------------------------------------------------------------

    [Fact]
    public void AGradientWithNoStopsLoadsAsTheModelsDefaultRamp()
    {
        var gradient = new GradientSpec { Stops = Array.Empty<GradientStop>() };

        GradientSpec? got = RoundTripGradient(gradient);

        Assert.NotNull(got);
        AssertStopsEqual(GradientSpec.Default.Stops, got!.Stops);
        Assert.Equal(GradientSpec.Default.Stops[0].Color, got.Sample(0.0).Color);
    }

    [Fact]
    public void AGradientWithASingleStopLoadsAndSamplesAsThatStop()
    {
        var only = new GradientStop(0.4, new ColorRgb(0.3, 0.6, 0.9), 0.2, 0.7, "only");
        var gradient = new GradientSpec { Stops = new[] { only } };

        GradientSpec? got = RoundTripGradient(gradient);

        Assert.NotNull(got);
        AssertStopsEqual(gradient.Stops, got!.Stops);
        Assert.Equal(only.Color, got.Sample(0.0).Color);
        Assert.Equal(only.Color, got.Sample(1.0).Color);
        Assert.Equal(only.Opacity, got.Sample(0.5).Opacity);
    }

    /// <summary>
    /// A finite position outside the ramp is kept exactly as written — the sidecar is
    /// lossless — and evaluation clamps it, which is what <c>Normalised()</c> exists for.
    /// </summary>
    [Fact]
    public void StopPositionsOutsideTheRampAreKeptInTheFileAndClampedForEvaluation()
    {
        var low = new GradientStop(-0.5, ColorRgb.Red, 1.0, 0.5, null);
        var high = new GradientStop(1.5, ColorRgb.Blue, 1.0, 0.5, null);
        var gradient = new GradientSpec { Stops = new[] { low, high } };

        GradientSpec? got = RoundTripGradient(gradient);

        Assert.NotNull(got);
        AssertStopsEqual(gradient.Stops, got!.Stops);

        IReadOnlyList<GradientStop> normalised = got.Normalised();
        Assert.Equal(0.0, normalised[0].Position);
        Assert.Equal(1.0, normalised[^1].Position);
        Assert.Equal(ColorRgb.Red, got.Sample(-10.0).Color);
        Assert.Equal(ColorRgb.Blue, got.Sample(10.0).Color);
    }

    /// <summary>
    /// Stops are stored in file order and sorted only for evaluation, so a file that lists
    /// them backwards still loads and still evaluates to the right ramp.
    /// </summary>
    [Fact]
    public void OutOfOrderStopsAreKeptInFileOrderAndSortedForEvaluation()
    {
        var first = new GradientStop(0.9, ColorRgb.Red, 1.0, 0.5, "red");
        var second = new GradientStop(0.1, ColorRgb.Blue, 1.0, 0.5, "blue");
        var gradient = new GradientSpec { Stops = new[] { first, second } };

        GradientSpec? got = RoundTripGradient(gradient);

        Assert.NotNull(got);
        AssertStopsEqual(gradient.Stops, got!.Stops);
        Assert.Equal(ColorRgb.Blue, got.Sample(0.0).Color);
        Assert.Equal(ColorRgb.Red, got.Sample(1.0).Color);
    }

    /// <summary>
    /// Two stops at one position are legal — that is how a hard edge is meant to be built —
    /// and both survive the save. Evaluation collapses the pair to the later stop, which is
    /// the model's documented rule (<c>Normalised()</c>); see the report for the difference
    /// between that rule and a true hard edge.
    /// </summary>
    [Fact]
    public void DuplicateStopPositionsBothSurviveTheSaveAndCollapseForEvaluation()
    {
        var before = new GradientStop(0.5, ColorRgb.Red, 1.0, 0.5, "before");
        var after = new GradientStop(0.5, ColorRgb.Blue, 1.0, 0.5, "after");
        var gradient = new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.White, 1.0, 0.5, null),
                before,
                after,
                new GradientStop(1.0, ColorRgb.Black, 1.0, 0.5, null),
            },
        };

        GradientSpec? got = RoundTripGradient(gradient);

        Assert.NotNull(got);
        AssertStopsEqual(gradient.Stops, got!.Stops);

        IReadOnlyList<GradientStop> normalised = got.Normalised();

        // Both stops at 0.5 survive, deliberately - that pair IS the hard edge. Collapsing them
        // to the later one, which this test previously asserted, turned the edge into a fast ramp
        // and made the colour approaching the edge wrong.
        Assert.Equal(4, normalised.Count);
        List<GradientStop> atHalf = normalised.Where(s => Math.Abs(s.Position - 0.5) < 1e-9).ToList();
        Assert.Equal(2, atHalf.Count);
        Assert.Equal(ColorRgb.Red, atHalf[0].Color);
        Assert.Equal(ColorRgb.Blue, atHalf[1].Color);
    }

    /// <summary>
    /// A freeform gradient's points and lines are carried even when the gradient's current
    /// kind is Radial: the type is a switchable field, and losing the points on a switch
    /// would silently discard work.
    /// </summary>
    [Fact]
    public void AFreeformBodyOnARadialGradientLoadsAndIsKept()
    {
        GradientSpec gradient = FullyPopulated(GradientKind.Radial);

        GradientSpec? got = RoundTripGradient(gradient);

        Assert.NotNull(got);
        Assert.Equal(GradientKind.Radial, got!.Kind);
        AssertPointsEqual(gradient.Points, got.Points);
        AssertLinesEqual(gradient.Lines, got.Lines);
        Assert.Equal(FreeformMode.Lines, got.FreeformMode);
    }

    // ------------------------------------------------------------------
    // Hostile input that must be refused with the member named.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> NonFiniteGradientMembers()
    {
        // Each case mutates exactly one member of a serialized gradient to a non-finite
        // number. Any spelling must be refused with the member named: System.Text.Json
        // refuses most of them itself and quotes the member in its Path, and the
        // serializer's own validation covers whatever reaches it. These tests pin the
        // boundary, not which of the two layers drew it.
        yield return Case("\"Position\":0.25", "\"Position\":\"1e400\"", "Stops[0].Position");
        yield return Case("\"Opacity\":0.35", "\"Opacity\":\"1e400\"", "Stops[0].Opacity");
        yield return Case("\"Midpoint\":0.45", "\"Midpoint\":\"1e400\"", "Stops[0].Midpoint");
        yield return Case("\"R\":0.11", "\"R\":\"1e400\"", "Stops[0].Color");
        yield return Case("\"Start\":{\"X\":0.125", "\"Start\":{\"X\":\"1e400\"", "Start");
        yield return Case("\"End\":{\"X\":0.875", "\"End\":{\"X\":\"1e400\"", "End");
        yield return Case("\"Center\":{\"X\":0.375", "\"Center\":{\"X\":\"1e400\"", "Center");
        yield return Case("\"RadiusX\":0.55", "\"RadiusX\":\"1e400\"", "RadiusX");
        yield return Case("\"RadiusY\":0.65", "\"RadiusY\":\"1e400\"", "RadiusY");
        yield return Case("\"Rotation\":33.5", "\"Rotation\":\"1e400\"", "Rotation");
        yield return Case("\"Angle\":145.25", "\"Angle\":\"1e400\"", "Angle");
        yield return Case("\"Position\":{\"X\":10.5", "\"Position\":{\"X\":\"1e400\"", "Points[0].Position");
        yield return Case("\"Opacity\":0.85", "\"Opacity\":\"1e400\"", "Points[0].Opacity");
        yield return Case("\"R\":0.21", "\"R\":\"1e400\"", "Points[0].Color");
    }

    private static object[] Case(string oldToken, string newToken, string member)
    {
        string json = GradientSubjectJson();
        Assert.Contains(oldToken, json);
        return new object[] { oldToken, json.Replace(oldToken, newToken), member };
    }

    [Theory]
    [MemberData(nameof(NonFiniteGradientMembers))]
    public void ANonFiniteGradientMemberIsRefusedWithTheMemberNamed(string label, string json, string member)
    {
        JsonException thrown = Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));

        Assert.Contains(member, thrown.Message);
        Assert.False(
            string.IsNullOrWhiteSpace(thrown.Message),
            $"{label}: the refusal had no message");
    }

    [Fact]
    public void AnUnknownGradientKindNameIsRefused()
    {
        string json = GradientSubjectJson().Replace("\"Kind\":\"Linear\"", "\"Kind\":\"Spiral\"");

        JsonException thrown = Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));

        Assert.Contains("Kind", thrown.Message);
    }

    [Fact]
    public void AnUnknownGradientKindNumberIsRefused()
    {
        // The enum converter accepts integers, so an out-of-range one would otherwise land
        // in the model as an undefined kind and take the default branch of every switch.
        string json = GradientSubjectJson().Replace("\"Kind\":\"Linear\"", "\"Kind\":99");

        JsonException thrown = Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));

        Assert.Contains("Kind", thrown.Message);
    }

    [Theory]
    [InlineData("\"Spread\":\"Pad\"", "\"Spread\":42", "Spread")]
    [InlineData("\"FreeformMode\":\"Lines\"", "\"FreeformMode\":7", "FreeformMode")]
    public void AnUnknownGradientEnumNumberIsRefused(string oldToken, string newToken, string member)
    {
        string json = GradientSubjectJson().Replace(oldToken, newToken);

        JsonException thrown = Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));

        Assert.Contains(member, thrown.Message);
    }

    [Fact]
    public void AFreeformLinePointingPastTheEndOfThePointListIsRefused()
    {
        string json = GradientSubjectJson().Replace("\"To\":1", "\"To\":9");

        JsonException thrown = Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));

        Assert.Contains("Lines[0]", thrown.Message);
    }

    [Fact]
    public void AFreeformLineWithANegativeIndexIsRefused()
    {
        string json = GradientSubjectJson().Replace("\"From\":0", "\"From\":-1");

        JsonException thrown = Assert.Throws<JsonException>(() => VccadDocumentSerializer.Deserialize(json));

        Assert.Contains("Lines[0]", thrown.Message);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static CadDocument DocumentWithGradient(GradientSpec gradient)
    {
        CadDocument doc = CadDocument.CreateDefault("gradient");
        PathItem path = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        path.Fill = FillSpec.WithGradient(gradient);
        doc.Artboards[0].Layers[0].AddItem(path);
        return doc;
    }

    private static FillSpec FillOf(CadDocument doc)
        => doc.Artboards[0].Layers[0].Children.OfType<PathItem>().First().Fill;

    private static GradientSpec? RoundTripGradient(GradientSpec gradient)
    {
        CadDocument doc = DocumentWithGradient(gradient);
        CadDocument back = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));
        return FillOf(back).Gradient;
    }

    private static GradientSpec FullyPopulated(GradientKind kind) => new()
    {
        Kind = kind,
        Spread = GradientSpread.Reflect,
        Stops = new[]
        {
            new GradientStop(0.0, new ColorRgb(0.1, 0.2, 0.3, 0.4), 0.25, 0.3, "start"),
            new GradientStop(0.37, new ColorRgb(0.9, 0.8, 0.7, 0.6), 0.5, 0.75, "middle"),
            new GradientStop(1.0, new ColorRgb(0.0, 0.0, 0.0, 1.0), 0.0, 0.5, null),
        },
        Start = new Point2D(0.125, 0.25),
        End = new Point2D(0.875, 0.75),
        Center = new Point2D(0.4, 0.6),
        RadiusX = 0.3,
        RadiusY = 0.7,
        Rotation = 33.5,
        Angle = 145.0,
        Points = new[]
        {
            new FreeformPoint(new Point2D(10.5, 20.25), new ColorRgb(0.2, 0.4, 0.6, 0.8), 0.35),
            new FreeformPoint(new Point2D(-5.0, 3.0), new ColorRgb(0.11, 0.22, 0.33, 0.44), 0.9),
        },
        FreeformMode = FreeformMode.Lines,
        Lines = new[] { (0, 1) },
    };

    /// <summary>
    /// The subject the hostile theories mutate: a Linear gradient whose every member has a
    /// value distinctive enough to be replaced by name.
    /// </summary>
    private static string GradientSubjectJson()
        => VccadDocumentSerializer.Serialize(DocumentWithGradient(new GradientSpec
        {
            Kind = GradientKind.Linear,
            Spread = GradientSpread.Pad,
            Stops = new[]
            {
                new GradientStop(0.25, new ColorRgb(0.11, 0.22, 0.33, 0.44), 0.35, 0.45, "first"),
                new GradientStop(0.75, new ColorRgb(0.55, 0.66, 0.77, 0.88), 0.65, 0.85, "second"),
            },
            Start = new Point2D(0.125, 0.375),
            End = new Point2D(0.875, 0.625),
            Center = new Point2D(0.375, 0.625),
            RadiusX = 0.55,
            RadiusY = 0.65,
            Rotation = 33.5,
            Angle = 145.25,
            Points = new[]
            {
                new FreeformPoint(new Point2D(10.5, 20.25), new ColorRgb(0.21, 0.31, 0.41, 0.51), 0.85),
                new FreeformPoint(new Point2D(30.5, 40.25), new ColorRgb(0.61, 0.71, 0.81, 0.91), 0.95),
            },
            FreeformMode = FreeformMode.Lines,
            Lines = new[] { (0, 1) },
        }));

    private static void AssertStopsEqual(
        IReadOnlyList<GradientStop> expected, IReadOnlyList<GradientStop> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    private static void AssertPointsEqual(
        IReadOnlyList<FreeformPoint> expected, IReadOnlyList<FreeformPoint> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    private static void AssertLinesEqual(
        IReadOnlyList<(int From, int To)> expected, IReadOnlyList<(int From, int To)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }
}
