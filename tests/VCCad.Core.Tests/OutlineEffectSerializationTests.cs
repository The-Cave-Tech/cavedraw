using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The outline effects' parameters survive the sidecar.
///
/// The effects gained `ridges`, `smooth`, `join`, `density`, `overlap`, `width`, `curviness` and `scatter`, and the
/// sidecar's effect record still carried only kind, size, detail and seed - so a scribble set up on the canvas came
/// back as a plain one after a save, and nothing said so. That is the same defect the gradient focal point had: a
/// value the model holds and the lossless store drops.
///
/// The rule is the repository's: an optional member is **absent** when it holds its default, so a document that
/// named none does not grow members it never had, and one that named them gets them back.
/// </summary>
public class OutlineEffectSerializationTests
{
    private static CadDocument DocumentWith(OutlineEffectSpec effect)
    {
        CadDocument document = CadDocument.CreateDefault();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));

        path.Stroke = new StrokeSpec(
            true, new ColorRgb(0, 0, 0), 8, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Effects = new EffectStack(new[] { effect }),
        };

        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    private static OutlineEffectSpec RoundTrip(OutlineEffectSpec effect)
    {
        string json = VccadDocumentSerializer.Serialize(DocumentWith(effect));
        CadDocument back = VccadDocumentSerializer.Deserialize(json);
        return back.Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke.AllEffects.Single();
    }

    /// <summary>**Every parameter the effects gained comes back.** One assert per member, so a dropped one names itself.</summary>
    [Fact]
    public void AScribbleKeepsEveryParameterItWasGiven()
    {
        var given = new OutlineEffectSpec(OutlineEffectKind.Scribble, 5, 2, 7)
        {
            Density = 3.5,
            Overlap = 0.25,
            Width = 1.75,
            Curviness = 0.5,
            Scatter = 2.25,
        };

        OutlineEffectSpec back = RoundTrip(given);

        Assert.Equal(given.Kind, back.Kind);
        Assert.Equal(given.Size, back.Size, 9);
        Assert.Equal(given.Detail, back.Detail, 9);
        Assert.Equal(given.Seed, back.Seed);
        Assert.Equal(given.Density, back.Density, 9);
        Assert.Equal(given.Overlap, back.Overlap, 9);
        Assert.Equal(given.Width, back.Width, 9);
        Assert.Equal(given.Curviness, back.Curviness, 9);
        Assert.Equal(given.Scatter, back.Scatter, 9);
    }

    /// <summary>Zig-zag's `ridges` and `smooth`, and the offset path's `join`, survive too.</summary>
    [Fact]
    public void AZigZagAndAnOffsetPathKeepTheirOwnParameters()
    {
        OutlineEffectSpec zigZag = RoundTrip(new OutlineEffectSpec(OutlineEffectKind.ZigZag, 4)
        {
            Ridges = 6,
            Smooth = true,
        });

        Assert.Equal(6, zigZag.Ridges);
        Assert.True(zigZag.Smooth);

        OutlineEffectSpec offset = RoundTrip(new OutlineEffectSpec(OutlineEffectKind.OffsetPath, 6)
        {
            Join = OutlineJoin.Round,
        });

        Assert.Equal(OutlineJoin.Round, offset.Join);
    }

    /// <summary>
    /// And a default is **absent** rather than written: an effect that names nothing does not acquire members on the
    /// way out, which is what keeps an old document's bytes stable.
    /// </summary>
    [Fact]
    public void AnEffectsDefaultsAreNotWrittenOut()
    {
        string json = VccadDocumentSerializer.Serialize(DocumentWith(OutlineEffectSpec.Roughen(3, seed: 2)));

        Assert.DoesNotContain("Ridges", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Curviness", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scatter", json, StringComparison.OrdinalIgnoreCase);
    }
}
