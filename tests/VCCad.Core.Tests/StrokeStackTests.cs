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

    // ---------------------------------------------------------------- per-stroke paint

    /// <summary>
    /// **Per-stroke opacity and blending are members of a stroke, and they survive the sidecar.**
    ///
    /// This is the half of the appearance stack the model did not have: the stack carried widths, colours and
    /// dashes, so a broad translucent highlight under a thin black line was still only expressible as a colour
    /// with a low alpha - which is not the same thing, because the alpha belongs to the paint and the opacity
    /// belongs to the stroke. Two strokes that differ only in these two members are two different documents, and
    /// the check is that they come back as two different strokes rather than as the same one twice.
    /// </summary>
    [Fact]
    public void PerStrokeOpacityAndBlendSurviveSaveAndReload()
    {
        PathItem path = Line();
        path.Strokes.Clear();
        path.Strokes.Add(Stroke(6, 1, 0, 0) with { Opacity = 1.0 });
        path.Strokes.Add(Stroke(2, 0, 0, 0) with { Opacity = 0.35, Blend = BlendMode.Multiply });

        PathItem reloaded = RoundTrip(path);

        Assert.Equal(2, reloaded.Strokes.Count);
        Assert.Equal(1.0, reloaded.Strokes[0].Opacity!.Value, 6);
        Assert.Null(reloaded.Strokes[0].Blend);
        Assert.Equal(0.35, reloaded.Strokes[1].Opacity!.Value, 6);
        Assert.Equal(BlendMode.Multiply, reloaded.Strokes[1].Blend);
    }

    /// <summary>
    /// **Absent at its default, asserted against the bytes rather than assumed.**
    ///
    /// An ordinary stroke states no opacity and no blend mode, so a document that has one serialises to exactly
    /// the bytes it did before either member existed. The assertion is on the absence of the member **names** in
    /// the written JSON, because "the value happens to be 1" is what a writer that always wrote the member would
    /// also produce.
    ///
    /// The reading is of the **stroke object** rather than of the whole document, and that is not a convenience:
    /// a path carries its own `Opacity`, so a whole-document substring test finds that member and passes for a
    /// reason that has nothing to do with the stroke.
    /// </summary>
    [Fact]
    public void AnOrdinaryStrokeWritesNoOpacityOrBlendMember()
    {
        PathItem path = Line();
        path.Stroke = Stroke(2, 0, 0, 0);

        string stroke = StrokeJson(path);

        Assert.DoesNotContain("\"Opacity\"", stroke, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Blend\"", stroke, StringComparison.Ordinal);
    }

    /// <summary>
    /// **Null stores nothing; an explicit value stores a decision - the distinction this issue warns about.**
    ///
    /// A stroke with no opacity member is not the same document as one whose opacity is 1.0: the first says
    /// nothing, the second records that somebody chose full opacity on that stroke of the stack. The sidecar has
    /// to write the decision or a file cannot carry it, and it has to write **nothing** for the absent case or
    /// every document in the world changes on the way out. Both halves are asserted on the member itself rather
    /// than through a value that the two states happen to share.
    /// </summary>
    [Fact]
    public void AnExplicitFullyOpaqueStrokeIsWrittenAndTheAbsentOneIsNot()
    {
        PathItem stated = Line();
        stated.Stroke = Stroke(2, 0, 0, 0) with { Opacity = 1.0, Blend = BlendMode.Normal };

        PathItem silent = Line();
        silent.Stroke = Stroke(2, 0, 0, 0);

        Assert.Contains("\"Opacity\":1", StrokeJson(stated), StringComparison.Ordinal);
        Assert.Contains("\"Blend\":\"normal\"", StrokeJson(stated), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Opacity\"", StrokeJson(silent), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Blend\"", StrokeJson(silent), StringComparison.Ordinal);

        // And the decision comes back as a decision rather than being collapsed to "unstated" on the way in.
        Assert.Equal(1.0, RoundTrip(stated).Stroke.Opacity!.Value, 6);
        Assert.Equal(BlendMode.Normal, RoundTrip(stated).Stroke.Blend);
        Assert.Null(RoundTrip(silent).Stroke.Opacity);
        Assert.Null(RoundTrip(silent).Stroke.Blend);
    }

    /// <summary>
    /// **The order of the stack is what decides the drawing, and the members are per stroke.**
    ///
    /// A stack whose two strokes differ only in opacity and blending must come back differing **at that index**,
    /// not agreeing on one value because the reader took the first stroke's. This is the stack-shaped half of the
    /// issue: a path can be a thin black line under a broad translucent highlight rather than one stroke.
    /// </summary>
    [Fact]
    public void TheStackKeepsEachStrokesOwnOpacityAndBlendAtItsOwnIndex()
    {
        PathItem path = Line();
        path.Strokes.Clear();
        path.Strokes.Add(Stroke(20, 1, 1, 0) with { Opacity = 0.25, Blend = BlendMode.Screen });
        path.Strokes.Add(Stroke(1, 0, 0, 0));

        PathItem reloaded = RoundTrip(path);

        Assert.Equal(new[] { 0.25, 1.0 }, reloaded.Strokes.Select(s => s.Opacity ?? 1.0).ToArray());
        Assert.Equal(BlendMode.Screen, reloaded.Strokes[0].Blend);
        Assert.Null(reloaded.Strokes[1].Blend);
    }

    /// <summary>
    /// **The canonical text form distinguishes two strokes that differ only by opacity or blending.**
    ///
    /// The dump is documentation that two documents with equal dumps are identical in every value the model
    /// holds, and the round-trip tests are built on it. Per-stroke opacity and blending were left out of it, so a
    /// path carrying a translucent highlight and the same path carrying an opaque one **printed the same stroke
    /// text** - every comparison built on the dump then passed for the wrong reason, which is the failure mode the
    /// stack's own `strokes=` member was added to prevent one member earlier.
    ///
    /// What is compared is the **stroke text** rather than the whole dump, because a dump also carries the ids
    /// every document generates for itself: two separately built documents differ by id whatever else they hold,
    /// and a test that compared whole dumps would pass without the paint ever being printed.
    /// </summary>
    [Fact]
    public void TheDumpDistinguishesTwoStrokesThatDifferOnlyByPaint()
    {
        string StrokeText(StrokeSpec stroke)
        {
            CadDocument document = CadDocument.CreateDefault();
            PathItem path = Line();
            path.Stroke = stroke;
            document.Artboards[0].Layers[0].AddItem(path);

            string line = ModelDump.Of(document)
                .Split('\n')
                .First(text => text.Contains("stroke=", StringComparison.Ordinal));
            int at = line.IndexOf("stroke=", StringComparison.Ordinal);
            return line.Substring(at, line.IndexOf(" subpaths=", at, StringComparison.Ordinal) - at);
        }

        StrokeSpec plain = Stroke(4, 0, 0, 0);

        string silent = StrokeText(plain);
        string faint = StrokeText(plain with { Opacity = 0.4 });
        string opaque = StrokeText(plain with { Opacity = 1.0 });
        string multiplied = StrokeText(plain with { Blend = BlendMode.Multiply });

        Assert.NotEqual(silent, faint);

        // The distinction this issue warns about, in the text form: stating 1 is not the same document as
        // stating nothing, and a dump that printed "opacity:1" for both would blur exactly that.
        Assert.NotEqual(silent, opaque);
        Assert.NotEqual(silent, multiplied);

        // And the same stroke text prints the same way twice, so the differences above are the paint.
        Assert.Equal(faint, StrokeText(plain with { Opacity = 0.4 }));
    }

    private static string BytesOf(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return System.Text.Encoding.UTF8.GetString(VccadDocumentSerializer.SerializeToBytes(document));
    }

    /// <summary>
    /// The written stroke object alone, from its key to the end of its braces.
    ///
    /// Reading the stroke rather than the whole document is load-bearing: a **path** carries its own `Opacity`
    /// member, and the fill and the paths write `fillOpacity` attributes, so a whole-document substring search for
    /// "Opacity" finds one of those and passes whether or not the stroke ever stated anything. The stroke is what
    /// these tests are about, so the stroke is what they read.
    /// </summary>
    private static string StrokeJson(PathItem path)
    {
        string json = BytesOf(path);
        int at = json.IndexOf("\"Stroke\":", StringComparison.Ordinal);
        Assert.True(at >= 0, "the document has no stroke member at all");

        int open = json.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < json.Length; i++)
        {
            if (json[i] == '{')
            {
                depth++;
            }
            else if (json[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return json.Substring(open, i - open + 1);
                }
            }
        }

        return json.Substring(open);
    }
}
