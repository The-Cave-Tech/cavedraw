using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The stroke subsystem round-trips through the sidecar **field by field**, and adding it did not change what an
/// ordinary document serialises to.
///
/// Both halves of that matter. A stroke that came back with its alignment defaulted or its dash offset lost is a
/// drawing that looks different after a save, which is the defect this project treats as the worst kind. And a
/// document written before any of this existed has to be byte-identical to one written now, or every sidecar in
/// the world changes the moment a feature is added.
/// </summary>
public class StrokeModelRoundTripTests
{
    private static PathItem Path(string name = "line", params Point2D[] points)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        return path;
    }

    private static byte[] Serialize(CadDocument document) => VccadDocumentSerializer.SerializeToBytes(document);

    private static CadDocument RoundTrip(CadDocument document)
        => VccadDocumentSerializer.Deserialize(Serialize(document));

    /// <summary>
    /// **Every member of a stroke, checked one at a time.** Set to values that are all different from their
    /// defaults, so a member that is silently dropped is visibly wrong rather than coincidentally right.
    /// </summary>
    [Fact]
    public void EveryStrokeMemberSurvives()
    {
        var stroke = new StrokeSpec(
            IsVisible: true,
            Color: new ColorRgb(0.2, 0.4, 0.6, 0.8),
            Width: 7.25,
            Cap: StrokeCap.Square,
            Join: StrokeJoin.Round,
            MiterLimit: 9.5,
            Alignment: StrokeAlignment.Outside,
            Dash: new DashPattern(new[] { 3.0, 1.5, 0.5 }, 2.25),
            WidthProfile: new WidthProfileSpec("Brush 4", new[]
            {
                new WidthPoint(0.0, 14.5, 6.25),
                new WidthPoint(0.35, 9.0, 3.5, WidthInterpolation.Cubic),
                new WidthPoint(1.0, 0.0, 0.0),
            }));

        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("stroked", new Point2D(0, 0), new Point2D(50, 25));
        path.Stroke = stroke;
        document.Artboards[0].Layers[0].AddItem(path);

        StrokeSpec back = RoundTrip(document).Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.True(back.IsVisible);
        Assert.Equal(0.2, back.Color.R, 6);
        Assert.Equal(0.4, back.Color.G, 6);
        Assert.Equal(0.6, back.Color.B, 6);
        Assert.Equal(0.8, back.Color.A, 6);
        Assert.Equal(7.25, back.Width, 6);
        Assert.Equal(StrokeCap.Square, back.Cap);
        Assert.Equal(StrokeJoin.Round, back.Join);
        Assert.Equal(9.5, back.MiterLimit, 6);
        Assert.Equal(StrokeAlignment.Outside, back.Alignment);

        Assert.Equal(new[] { 3.0, 1.5, 0.5 }, back.Dash.Segments.ToArray());
        Assert.Equal(2.25, back.Dash.Offset, 6);

        WidthProfileSpec profile = back.WidthProfile!;
        Assert.Equal("Brush 4", profile.Name);
        Assert.Equal(3, profile.Points.Count);
        Assert.Equal(0.0, profile.Points[0].Position, 6);
        Assert.Equal(14.5, profile.Points[0].LeftWidth, 6);
        Assert.Equal(6.25, profile.Points[0].RightWidth, 6);
        Assert.Equal(0.35, profile.Points[1].Position, 6);
        Assert.Equal(WidthInterpolation.Cubic, profile.Points[1].Interpolation);
        Assert.Equal(WidthInterpolation.Linear, profile.Points[2].Interpolation);

        // And the whole thing is equal as a value, which catches a member added later that nobody round-tripped.
        Assert.Equal(stroke, back);
    }

    /// <summary>
    /// **An invisible stroke keeps its settings.**
    ///
    /// This is the case that needs the members rebuilt rather than simply read: an invisible stroke with no colour
    /// of its own is written the way "no stroke" has always been written - no colour - and comes back through the
    /// branch that has to put the width, caps and joins back itself. Collapsing it to a plain `None` here is
    /// invisible in the file and turns a 6pt square-capped stroke into a hairline the moment someone switches it
    /// back on.
    /// </summary>
    [Fact]
    public void AnInvisibleStrokeKeepsItsSettings()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("hidden", new Point2D(0, 0), new Point2D(50, 0));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(
            IsVisible: false,
            Color: ColorRgb.Black,
            Width: 6,
            Cap: StrokeCap.Square,
            Join: StrokeJoin.Round,
            MiterLimit: 8,
            Alignment: StrokeAlignment.Inside,
            Dash: new DashPattern(new[] { 4.0, 2.0 }, 1.0)));
        document.Artboards[0].Layers[0].AddItem(path);

        StrokeSpec back = RoundTrip(document)
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Strokes[0];

        Assert.False(back.IsVisible);
        Assert.Equal(6.0, back.Width, 6);
        Assert.Equal(StrokeCap.Square, back.Cap);
        Assert.Equal(StrokeJoin.Round, back.Join);
        Assert.Equal(8.0, back.MiterLimit, 6);
        Assert.Equal(StrokeAlignment.Inside, back.Alignment);
        Assert.Equal(new[] { 4.0, 2.0 }, back.Dash.Segments.ToArray());
        Assert.Equal(1.0, back.Dash.Offset, 6);
    }

    /// <summary>The appearance stack survives with its order, which is what decides what is drawn on top.</summary>
    [Fact]
    public void TheStrokeStackSurvivesInOrder()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("stacked", new Point2D(0, 0), new Point2D(50, 0));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, new ColorRgb(1, 0, 0), 9, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Strokes.Add(new StrokeSpec(true, new ColorRgb(0, 1, 0), 5, StrokeCap.Round, StrokeJoin.Bevel, 6));
        path.Strokes.Add(new StrokeSpec(false, new ColorRgb(0, 0, 1), 1, StrokeCap.Square, StrokeJoin.Round, 2));
        document.Artboards[0].Layers[0].AddItem(path);

        PathItem back = RoundTrip(document).Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.Equal(3, back.Strokes.Count);
        Assert.Equal(new[] { 9.0, 5.0, 1.0 }, back.Strokes.Select(s => s.Width).ToArray());
        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, new[] { back.Strokes[0].Color.R, back.Strokes[0].Color.G, back.Strokes[0].Color.B });
        Assert.False(back.Strokes[2].IsVisible);
        Assert.Equal(path.Strokes, back.Strokes);
    }

    /// <summary>The library survives, asset by asset, with its order.</summary>
    [Fact]
    public void TheProfileLibrarySurvives()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddWidthProfile(WidthProfileSpec.Taper(20, 2));
        document.AddWidthProfile(
            new WidthProfileSpec("Brush 4", new[] { new WidthPoint(0.25, 8, 4, WidthInterpolation.Cubic) }));
        document.AddWidthProfile(WidthProfileSpec.Constant(6));

        CadDocument back = RoundTrip(document);

        Assert.Equal(new[] { "Taper", "Brush 4", "Uniform" }, back.WidthProfiles.Select(p => p.Name).ToArray());
        Assert.Equal(2, back.WidthProfiles[0].Points.Count);
        Assert.Single(back.WidthProfiles[1].Points);
        Assert.Equal(0.25, back.WidthProfiles[1].Points[0].Position, 6);
        Assert.Equal(8.0, back.WidthProfiles[1].Points[0].LeftWidth, 6);
        Assert.Equal(WidthInterpolation.Cubic, back.WidthProfiles[1].Points[0].Interpolation);
    }

    /// <summary>
    /// **Determinism.** The same document serialises to the same bytes, twice running, with assets in the
    /// document. Identical-bytes is a repo invariant, and an asset collection is exactly the kind of thing that
    /// quietly stops being ordered.
    /// </summary>
    [Fact]
    public void TheSameDocumentSerialisesToTheSameBytes()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.AddWidthProfile(WidthProfileSpec.Taper(20, 2));
        document.AddWidthProfile(new WidthProfileSpec("Zebra", new[] { WidthPoint.Even(0, 4) }));
        document.AddWidthProfile(new WidthProfileSpec("Alpha", new[] { WidthPoint.Even(0, 5) }));

        for (int i = 0; i < 3; i++)
        {
            var path = Path("p" + i, new Point2D(0, 0), new Point2D(10 * i, 5));
            path.Stroke = new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4,
                StrokeAlignment.Center, default, WidthProfileSpec.Taper(8, 1));
            document.Artboards[0].Layers[0].AddItem(path);
        }

        byte[] first = Serialize(document);
        byte[] second = Serialize(document);

        Assert.Equal(first, second);

        // And again after a round trip, which is the harder half: deserializing and re-serializing must not
        // reorder anything or drop an absent-means-default member.
        Assert.Equal(first, Serialize(RoundTrip(document)));
    }

    /// <summary>
    /// **The compatibility case.** A document with one plain stroke and no assets is written without any of the
    /// new members, so it is byte-identical to what this build wrote before the subsystem existed.
    /// </summary>
    [Fact]
    public void AnOrdinaryDocumentGainsNoNewMembers()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("plain", new Point2D(0, 0), new Point2D(10, 0));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 1, StrokeCap.Butt, StrokeJoin.Miter, 4);
        document.Artboards[0].Layers[0].AddItem(path);

        string json = Encoding.UTF8.GetString(Serialize(document));

        Assert.DoesNotContain("WidthProfile", json);
        Assert.DoesNotContain("Strokes", json);
        Assert.Contains("\"Stroke\"", json);
        Assert.Contains("\"width\"", json, StringComparison.OrdinalIgnoreCase);

        // The old sidecar's shape: absent members simply are not there, so a file without them loads unchanged.
        CadDocument back = RoundTrip(document);
        Assert.Single(back.Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Strokes);
        Assert.Empty(back.WidthProfiles);
    }

    /// <summary>
    /// An older sidecar - one written before profiles existed - loads with a single stroke and no profile, and
    /// re-serialises to exactly the bytes it came from. Removing the members is what makes that true, so a test
    /// that only checks it loads would miss a spurious member being added back on the way out.
    /// </summary>
    [Fact]
    public void AnOlderSidecarLoadsAndReserialisesUnchanged()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("old", new Point2D(0, 0), new Point2D(10, 0));
        path.Stroke = new StrokeSpec(true, new ColorRgb(0.1, 0.2, 0.3), 4, StrokeCap.Round, StrokeJoin.Bevel, 5,
            StrokeAlignment.Inside, new DashPattern(new[] { 2.0, 2.0 }, 0));
        document.Artboards[0].Layers[0].AddItem(path);

        byte[] original = Serialize(document);
        byte[] again = Serialize(VccadDocumentSerializer.Deserialize(original));

        Assert.Equal(original, again);
    }

    /// <summary>
    /// **An invisible stroke keeps its width profile.**
    ///
    /// The stroke that comes back without a colour is rebuilt member by member, because an absent colour is how the
    /// format says "no stroke" and the file therefore carries no colour to read. Every other member was rebuilt
    /// there; the profile was not. The file **had** written it - a stroke switched off with its taper still on it
    /// is an ordinary thing to save - so this was the reader dropping a value the document carries, and a taper
    /// came back as an invisible hairline with no taper at all.
    ///
    /// The bytes do not move for this case, which is why the assertion is on the model rather than on the JSON:
    /// the write side was already correct, and only the read was lossy.
    /// </summary>
    [Fact]
    public void AnInvisibleStrokeKeepsItsWidthProfile()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("hidden", new Point2D(0, 0), new Point2D(50, 0));
        path.Stroke = new StrokeSpec(false, ColorRgb.Black, 6, StrokeCap.Square, StrokeJoin.Round, 8)
        {
            WidthProfile = WidthProfileSpec.Taper(20, 2),
        };
        document.Artboards[0].Layers[0].AddItem(path);

        // The member really is in the file, so the loss below is the reader's and not the writer's.
        string json = Encoding.UTF8.GetString(Serialize(document));
        Assert.Contains("\"WidthProfile\"", json, StringComparison.Ordinal);

        StrokeSpec back = RoundTrip(document)
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.False(back.IsVisible);
        Assert.True(back.HasWidthProfile);
        WidthProfileSpec profile = back.WidthProfile!;
        Assert.Equal("Taper", profile.Name);
        Assert.Equal(2, profile.Points.Count);
        Assert.Equal(0.0, profile.Points[0].Position, 6);
        Assert.Equal(20.0, profile.Points[0].LeftWidth, 6);
        Assert.Equal(1.0, profile.Points[1].Position, 6);
        Assert.Equal(2.0, profile.Points[1].LeftWidth, 6);
        Assert.Equal(WidthInterpolation.Cubic, profile.Points[1].Interpolation);
    }

    /// <summary>
    /// **An all-off dynamics spec is a value, and it is written.**
    ///
    /// The model states the distinction in as many words: a null spec stores nothing, an all-off spec stores that
    /// every response was switched off. The sidecar collapsed the second to the first, keying the member off
    /// <see cref="StrokeSpec.HasDynamics"/> - a predicate that answers "does this stroke respond to the pen",
    /// which an all-off spec answers no to while still being a decision somebody recorded. A person can reach the
    /// state through the stroke pane's own checkbox (`SetDynamicsAt(..., enabled: false, ...)`), so it is not a
    /// shape only a test can build.
    ///
    /// The bytes change for exactly this document and no other: one that carries no dynamics at all still writes
    /// no member, which <see cref="AnOrdinaryDocumentGainsNoNewMembers"/> and
    /// <see cref="ASidecarWrittenBeforeTheStrokeSubsystemRoundTripsToTheSameBytes"/> both pin.
    /// </summary>
    [Fact]
    public void AnAllOffDynamicsSpecIsWrittenAndComesBack()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("switched-off", new Point2D(0, 0), new Point2D(50, 0));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Dynamics = DynamicsSpec.None,
        };
        document.Artboards[0].Layers[0].AddItem(path);

        string json = Encoding.UTF8.GetString(Serialize(document));
        Assert.Contains("\"Dynamics\"", json, StringComparison.Ordinal);

        StrokeSpec back = RoundTrip(document)
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.NotNull(back.Dynamics);
        Assert.True(back.Dynamics!.IsEmpty);
        Assert.Equal(DynamicsTargetSpec.Off, back.Dynamics.For(DynamicsTarget.Width));
        Assert.Equal(path.Stroke, back);
    }

    /// <summary>
    /// The same all-off spec on a **brush**, which travels through its own record and had the same collapsed
    /// predicate. A nib whose every response was switched off came back as one that had never recorded any.
    /// </summary>
    [Fact]
    public void ABrushsAllOffDynamicsSpecIsWrittenAndComesBack()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Path("brush", new Point2D(0, 0), new Point2D(50, 0));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24, dynamics: DynamicsSpec.None),
        };
        document.Artboards[0].Layers[0].AddItem(path);

        string json = Encoding.UTF8.GetString(Serialize(document));
        Assert.Contains("\"Dynamics\"", json, StringComparison.Ordinal);

        BrushSpec brush = RoundTrip(document)
            .Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke.Brush!;

        Assert.NotNull(brush.Dynamics);
        Assert.True(brush.Dynamics!.IsEmpty);
        Assert.Equal(path.Stroke.Brush, brush);
    }

    /// <summary>
    /// **The old-bytes assertion, as a literal.**
    ///
    /// This is the sidecar a build without the stroke subsystem wrote for a path whose stroke is switched off:
    /// the exact JSON, fixed ids and all, with no profile, no effect, no raster effect, no dynamics and no brush
    /// member. Deserializing it and serializing it again must produce the identical string - not merely a document
    /// that loads. A reader that invented a default nib, an empty profile or an all-off spec would add a member
    /// here and fail on the bytes, which is the failure this test exists for.
    ///
    /// The literal is byte-for-byte what the pre-fix build wrote for this document; it was captured by serializing
    /// the same model before the fix and is committed here so the guarantee survives without an old binary to
    /// compare against.
    /// </summary>
    [Fact]
    public void ASidecarWrittenBeforeTheStrokeSubsystemRoundTripsToTheSameBytes()
    {
        const string legacy =
            "{\"Version\":1,\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\"legacy\"," +
            "\"Artboards\":[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"Artboard 1\"," +
            "\"X\":0,\"Y\":0,\"Width\":841.8897637795276,\"Height\":595.2755905511812,\"Layers\":[" +
            "{\"Id\":\"22222222-2222-2222-2222-222222222222\",\"Name\":\"Layer 1\",\"IsVisible\":true," +
            "\"IsLocked\":false,\"Opacity\":1,\"Items\":[" +
            "{\"$kind\":\"path\",\"Id\":\"33333333-3333-3333-3333-333333333333\",\"Name\":\"line\"," +
            "\"IsVisible\":true,\"IsLocked\":false," +
            "\"Fill\":{\"Visible\":false,\"Color\":null,\"Rule\":\"NonZero\"}," +
            "\"Stroke\":{\"Visible\":false,\"Color\":null,\"Width\":6,\"Cap\":\"Square\",\"Join\":\"Round\"," +
            "\"MiterLimit\":8,\"Alignment\":\"Center\",\"Dash\":null,\"DashOffset\":0}," +
            "\"Opacity\":1,\"SubPaths\":[{\"Closed\":false,\"Nodes\":[" +
            "{\"Anchor\":{\"X\":0,\"Y\":0},\"InHandle\":{\"X\":0,\"Y\":0},\"OutHandle\":{\"X\":0,\"Y\":0}}," +
            "{\"Anchor\":{\"X\":80,\"Y\":0},\"InHandle\":{\"X\":80,\"Y\":0},\"OutHandle\":{\"X\":80,\"Y\":0}}]}]," +
            "\"SourceFillCmyk\":null,\"SourceStrokeCmyk\":null,\"ShapeKind\":null,\"ShapeParameters\":null," +
            "\"Clips\":null}]}]}],\"Orphans\":[]}";

        // None of the members this issue is about is in the literal, so the byte comparison below is a comparison
        // against a document that carries none of the features.
        foreach (string member in new[]
                 { "WidthProfile", "Effects", "RasterEffects", "Dynamics", "Brush", "Brushes", "WidthProfiles" })
        {
            Assert.DoesNotContain(member, legacy, StringComparison.Ordinal);
        }

        CadDocument document = VccadDocumentSerializer.Deserialize(legacy);
        Assert.Equal(legacy, VccadDocumentSerializer.Serialize(document));

        StrokeSpec stroke = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;
        Assert.False(stroke.HasWidthProfile);
        Assert.False(stroke.HasEffects);
        Assert.False(stroke.HasRasterEffects);
        Assert.False(stroke.HasDynamics);
        Assert.False(stroke.HasBrush);
        Assert.Null(stroke.Dynamics);
        Assert.Null(stroke.Brush);
        Assert.Null(stroke.WidthProfile);
    }
}
