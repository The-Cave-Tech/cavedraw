using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Inkscape's live path effects as they arrive in an SVG file (issue #130), which is the half the stroke subsystem's
/// variable-width work cannot supply on its own: the translation and the outline already exist, and what was missing
/// was the **element** the path's <c>inkscape:path-effect="#id"</c> refers to.
///
/// Inkscape does not stroke a powerstroke. It keeps the path the effect was applied to in <c>d</c>, writes the
/// effect's own parameters into a <c>defs</c> element, and lets whoever opens the file run the effect. A reader
/// that walks past <c>defs</c> therefore has a reference that leads nowhere: the path draws as an ordinary stroke
/// of the width the file happened to declare, which is a plausible picture of something the file never drew, and an
/// export hands back a document with no effect on it at all.
///
/// Four things are asserted here rather than "the effect was read". The **widths in points**, because the model
/// measures paper and the file measures user units and a translation can get one of those wrong and still produce
/// a profile. The **description, attribute for attribute**, because a translation that threw the element away
/// would draw the right picture once. And the two **silences** that produced issues #140, #143, #144, #150 and
/// #151: an effect this build does not implement, and a reference that points at nothing.
///
/// Issue #155 is the third silence and the one this class did not cover: an effect **nothing refers to**. It says
/// nothing about how anything is drawn, so it was a stated boundary rather than a bug - but a file that keeps a
/// library of named effects lost the unused half of it on the way through, silently, and silence was the option
/// the issue says should not survive. The model now has a document-level home for it
/// (<see cref="CadDocument.ForeignPathEffects"/>), the reader puts it there and the writer puts it back in
/// <c>defs</c> beside the referenced ones.
/// </summary>
public class SvgPathEffectTests
{
    /// <summary>An eight-segment open path, so a powerstroke's segment-indexed knots are known exactly.</summary>
    private const string Line = "M 0,0 L 10,0 L 20,0 L 30,0 L 40,0 L 50,0 L 60,0 L 70,0 L 80,0";

    /// <summary>
    /// One of Inkscape's own powerstroke elements, written the way Inkscape writes one: the effect's name, its id
    /// and the <c>lpeversion</c> first, then the parameters in the tool's own order.
    /// </summary>
    private const string PowerStroke =
        "<inkscape:path-effect effect=\"powerstroke\" id=\"path-effect1\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" offset_points=\"0,2 | 3,5 | 8,1\" not_jump=\"false\" sort_points=\"true\" " +
        "interpolator_type=\"Linear\" start_linecap_type=\"zerowidth\" linejoin_type=\"extrp_arc\" " +
        "miter_limit=\"4\" scale_width=\"1\" end_linecap_type=\"zerowidth\" />";

    /// <summary>
    /// The other half of the corpus: an effect this build has no translation for. <c>bend_path</c> reshapes the
    /// path itself, so drawing the path without it is not a smaller version of the picture - it is a different one.
    /// </summary>
    private const string BendPath =
        "<inkscape:path-effect effect=\"bend_path\" id=\"path-effect1\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" bendpath=\"m 10,50 c 20,0 40,20 70,0\" />";

    /// <summary>
    /// A file shaped like Inkscape's: the effect in <c>defs</c>, the path it was applied to carrying the reference
    /// and the path the effect was run on in <c>inkscape:original-d</c>.
    /// </summary>
    private static string File(string effectElement, string reference = "#path-effect1")
        => "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
           "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
           "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
           "<defs>" + effectElement + "</defs>" +
           "<path id=\"p1\" style=\"fill:none;stroke:#000000;stroke-width:1\" d=\"" + Line + "\" " +
           "inkscape:original-d=\"" + Line + "\" inkscape:path-effect=\"" + reference + "\" />" +
           "</svg>";

    /// <summary>
    /// An effect nothing points at. It is the same kind of element as <see cref="PowerStroke"/> and a different one
    /// of Inkscape's own to keep: a library entry a person has not applied yet.
    /// </summary>
    private const string UnreferencedEffect =
        "<inkscape:path-effect effect=\"powerstroke\" id=\"path-effect2\" lpeversion=\"1.4\" " +
        "is_visible=\"true\" offset_points=\"0,1 | 4,6 | 8,2\" not_jump=\"false\" sort_points=\"true\" " +
        "interpolator_type=\"Linear\" start_linecap_type=\"zerowidth\" linejoin_type=\"extrp_arc\" " +
        "miter_limit=\"4\" scale_width=\"1\" end_linecap_type=\"zerowidth\" />";

    /// <summary>
    /// A file with a library of two effects: <c>#path-effect1</c>, which the path refers to, and
    /// <c>#path-effect2</c>, which nothing refers to.
    ///
    /// The second one is issue #155. It says nothing about how anything is drawn, which is exactly why it had no
    /// home - the reader hangs a referenced effect on the path that points at it, and there is no path here to hang
    /// this one on. But it is still content the file carried, and a file that keeps a library of named effects loses
    /// the unused half of it on the way through this editor.
    /// </summary>
    private const string TwoEffects =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
        "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
        "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
        "<defs>" + PowerStroke + UnreferencedEffect + "</defs>" +
        "<path id=\"p1\" style=\"fill:none;stroke:#000000;stroke-width:1\" d=\"" + Line + "\" " +
        "inkscape:original-d=\"" + Line + "\" inkscape:path-effect=\"#path-effect1\" />" +
        "</svg>";

    private static PathItem OnlyPath(SvgImportResult result) => Assert.Single(result.Document.AllPaths());

    /// <summary>The effect with this id wherever it sits in a file, or null when the file has no such element.</summary>
    private static XElement? Effect(string svg, string id)
        => XDocument.Parse(svg).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "path-effect" && e.Attribute("id")?.Value == id);

    /// <summary>The effect element in a file, wherever it sits.</summary>
    private static XElement Effect(string svg)
        => XDocument.Parse(svg).Descendants().Single(e => e.Name.LocalName == "path-effect");

    /// <summary>
    /// An element's attributes as the file wrote them - each one's name and value, in order, with the namespace
    /// declarations left out. That is what "the attributes Inkscape wrote" means, and comparing the joined text
    /// catches a rename, a reorder and a re-quoting alike.
    /// </summary>
    private static string[] Attributes(XElement element)
        => element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .Select(a => $"{a.Name.LocalName}={a.Value}")
            .ToArray();

    // ---------------------------------------------------------------- a library with an unused entry

    /// <summary>
    /// **The half of a two-effect library that is used still survives, and the half that is not is kept too.**
    ///
    /// This is the case issue #155 names: a file whose <c>defs</c> holds two effects, one referenced by a path and
    /// one not. The referenced one has always travelled on the path that points at it, so this test is partly a
    /// guard against the new document-level home swallowing it - a kept list that also claimed the referenced
    /// element would write the same definition into <c>defs</c> twice.
    ///
    /// The unreferenced one is asserted on **both sides**: on the imported model, because that is where a caller
    /// looks before exporting, and on the exported bytes, because a model that held it and an exporter that did not
    /// write it would still hand the person back a file with their library missing.
    /// </summary>
    [Fact]
    public void AReferencedEffectSurvivesAndAnUnreferencedOneIsKept()
    {
        SvgImportResult result = SvgReader.Read(TwoEffects);

        // The referenced half: translated into a stroke, and its element kept on the path as it always was.
        PathItem path = OnlyPath(result);
        Assert.True(path.Stroke.HasWidthProfile, string.Join(" | ", result.Warnings));
        Assert.Equal("path-effect1", path.Stroke.WidthProfile!.Name);
        Assert.Contains(path.ForeignElements, xml => xml.Contains("path-effect1", StringComparison.Ordinal));

        // The unreferenced half: nothing was invented onto the path to hold it, and it has a home of its own.
        Assert.DoesNotContain(path.ForeignElements, xml => xml.Contains("path-effect2", StringComparison.Ordinal));

        string kept = Assert.Single(result.Document.ForeignPathEffects);
        Assert.Equal(Attributes(Effect(TwoEffects, "path-effect2")), Attributes(XElement.Parse(kept)));

        // And it is written back into `defs` beside the referenced one, verbatim.
        string exported = SvgWriter.Write(result.Document);

        Assert.NotNull(Effect(exported, "path-effect1"));
        Assert.NotNull(Effect(exported, "path-effect2"));
        Assert.Equal("defs", Effect(exported, "path-effect2")!.Parent!.Name.LocalName);
        Assert.Equal(Attributes(Effect(TwoEffects, "path-effect2")), Attributes(Effect(exported, "path-effect2")!));

        // A second import finds the kept effect again, so the element is read rather than merely echoed.
        SvgImportResult again = SvgReader.Read(exported);
        Assert.Single(again.Document.ForeignPathEffects);
        Assert.Contains("path-effect2", again.Document.ForeignPathEffects[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// **The kept effect survives this repository's own format too.**
    ///
    /// A document saved as a sidecar and reopened is the ordinary path through this editor, so "kept on the
    /// document" would only be true until the first save if the serializer did not carry it - which is the rule the
    /// Inkscape metadata already follows. And a document that has none stays byte-identical, so the member is
    /// **absent** rather than an empty array: the same reason <c>OutlineEffectDto</c> writes a default as nothing.
    /// </summary>
    [Fact]
    public void TheKeptEffectSurvivesTheSidecarAndANoneIsAbsent()
    {
        SvgImportResult result = SvgReader.Read(TwoEffects);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(result.Document));

        Assert.Single(reloaded.ForeignPathEffects);
        Assert.Equal(
            Attributes(XElement.Parse(result.Document.ForeignPathEffects[0])),
            Attributes(XElement.Parse(reloaded.ForeignPathEffects[0])));

        // And a document that keeps nothing gains no member: the bytes are what they were before this existed.
        string plain = System.Text.Encoding.UTF8.GetString(
            VccadDocumentSerializer.SerializeToBytes(CadDocument.CreateDefault()));

        Assert.DoesNotContain("ForeignPathEffects", plain, StringComparison.Ordinal);

        // An effect reference is not a use of the *definition* the path was written from, so a file whose only
        // effect is referenced still keeps nothing at the document level.
        CadDocument referenced = SvgReader.Read(File(PowerStroke)).Document;
        Assert.Empty(referenced.ForeignPathEffects);
    }

    // ---------------------------------------------------------------- the translation

    /// <summary>
    /// **A powerstroke imports as the width profile it describes, measured in points.**
    ///
    /// The model stores paper - points - and the file stores CSS user units, three quarters of one each. The
    /// reader keeps every length in the file's own units and carries them into points with the view box transform,
    /// so the assertion has to go through that transform: a knot stored as an offset of five user units either side
    /// of the centreline is a band 7.5pt wide, and a translation that forgot the factor would draw it 10pt wide.
    ///
    /// The positions are the other half. Inkscape stores a knot as <c>curve_index + t</c> over the whole path, so
    /// the knots at 0, 3 and 8 of this eight-segment path sit at 0, 0.375 and 1 - reading them as fractions of the
    /// path directly would pass on this file and be wrong on every other.
    /// </summary>
    [Fact]
    public void APowerStrokeImportsAsTheWidthProfileItDescribesInPoints()
    {
        SvgImportResult result = SvgReader.Read(File(PowerStroke));
        PathItem path = OnlyPath(result);

        Assert.True(path.Stroke.HasWidthProfile, string.Join(" | ", result.Warnings));

        WidthProfileSpec profile = path.Stroke.WidthProfile!;
        Assert.Equal("path-effect1", profile.Name);
        Assert.Equal(3, profile.Points.Count);
        Assert.Equal(0.0, profile.Points[0].Position, 9);
        Assert.Equal(0.375, profile.Points[1].Position, 9);
        Assert.Equal(1.0, profile.Points[2].Position, 9);

        // The stored width is the whole band, which is twice the knot's offset from the centreline.
        Assert.Equal(4.0, profile.Points[0].LeftWidth, 9);
        Assert.Equal(10.0, profile.Points[1].LeftWidth, 9);
        Assert.Equal(2.0, profile.Points[2].LeftWidth, 9);

        // The join the corpus's powerstroke case asks for: an extrapolated arc is a mitre without the fillet.
        Assert.Equal(StrokeJoin.Miter, path.Stroke.Join);

        // The page is the file's own 100 user units, which is 75pt of paper, and the view box is what carries one
        // into the other.
        Assert.Equal(75.0, result.Document.Artboards[0].Width, 9);
        ArtGroup viewBox = Assert.Single(result.Document.AllGroups());
        Assert.Equal(0.75, viewBox.Transform.A, 9);

        // The geometry the model actually draws, in points. The centreline runs along y=0 from x=0 to x=80 in the
        // file's units, so the outline's two edges at a knot are the half-width either side of it.
        IReadOnlyList<Point2D> outline = Assert.Single(StrokeOutlineBuilder.Plan(path, path.Stroke).Outlines);
        double[] Edge(double x) => outline
            .Where(p => Math.Abs(p.X - x) < 1e-9)
            .Select(p => viewBox.Transform.Transform(p).Y)
            .ToArray();

        // At the start: a knot of 2 user units, so edges 1.5pt either side of the centreline.
        Assert.Contains(Edge(0.0), y => Math.Abs(y + 1.5) < 1e-9);
        Assert.Contains(Edge(0.0), y => Math.Abs(y - 1.5) < 1e-9);

        // At the widest knot, a third of the way along: 5 user units, so 3.75pt either side.
        Assert.Contains(Edge(30.0), y => Math.Abs(y + 3.75) < 1e-9);
        Assert.Contains(Edge(30.0), y => Math.Abs(y - 3.75) < 1e-9);

        // Nothing was substituted, so there is nothing to report.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("path-effect1", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the description

    /// <summary>
    /// **The effect description survives import, export and import again, attribute for attribute.**
    ///
    /// This is the difference between translating an effect and reading one. A reader that turned the powerstroke
    /// into a width profile and dropped the element would draw the right picture once and write a file with no
    /// effect on it - so the picture would be right and the document would be gone. The element goes back into
    /// <c>defs</c>, spelled and ordered exactly as Inkscape wrote it, because this build models a handful of an
    /// effect's parameters and Inkscape decides what all of them mean from its own <c>lpeversion</c>.
    /// </summary>
    [Fact]
    public void TheEffectDescriptionSurvivesTheRoundTripByteForByte()
    {
        string[] written = Attributes(Effect(File(PowerStroke)));

        string exported = SvgWriter.Write(SvgReader.Read(File(PowerStroke)).Document);
        XElement effect = Effect(exported);

        Assert.Equal(written, Attributes(effect));

        // Inkscape's own three first, in Inkscape's own order - which is what a rebuilt element would not have.
        Assert.Equal(
            new[] { "effect=powerstroke", "id=path-effect1", "lpeversion=1.4" },
            Attributes(effect).Take(3));

        // And it is in `defs` again, which is where the file had it and where the next reader looks for it.
        Assert.Equal("defs", effect.Parent!.Name.LocalName);

        // A second import finds it too, so the description is read rather than merely echoed into the file.
        string preserved = Assert.Single(
            OnlyPath(SvgReader.Read(exported)).ForeignElements,
            xml => xml.Contains("path-effect", StringComparison.Ordinal));

        Assert.Equal(written, Attributes(XElement.Parse(preserved)));
    }

    // ---------------------------------------------------------------- the two silences

    /// <summary>
    /// **An effect this build does not implement is reported by name, and the geometry is the file's own.**
    ///
    /// The path already holds the effect's output, because that is what <c>d</c> is, so leaving it alone draws the
    /// right picture. What must not happen is the silence: a document that came in with a live path effect on it
    /// and says nothing looks exactly like a drawing that never had one, and the person comparing it with Inkscape
    /// has nothing to go on.
    /// </summary>
    [Fact]
    public void AnUnknownEffectIsReportedAndTheGeometryIsUnchanged()
    {
        SvgImportResult result = SvgReader.Read(File(BendPath));

        // Named by the id the file used, by the effect's own name, and by what this build does implement.
        Assert.Contains(result.Warnings, w =>
            w.Contains("path-effect1", StringComparison.Ordinal) &&
            w.Contains("bend_path", StringComparison.Ordinal) &&
            w.Contains("powerstroke", StringComparison.Ordinal));

        PathItem path = OnlyPath(result);

        Assert.False(path.Stroke.HasWidthProfile);
        Assert.Equal(1.0, path.Stroke.Width, 9);
        Assert.Equal(StrokeJoin.Miter, path.Stroke.Join);

        // The geometry is the file's own path, node for node - not the effect's output, and not a redrawn stroke.
        IReadOnlyList<PathNode> nodes = Assert.Single(path.SubPaths).Nodes;
        Assert.Equal(9, nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            Assert.Equal(new Point2D(i * 10, 0), nodes[i].Anchor);
        }

        // And the description is kept even though it could not be translated, so the export still carries it.
        Assert.Contains(path.ForeignElements, xml => xml.Contains("bend_path", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A reference that points at nothing is reported, in the list a missing `use` or image target goes in.**
    ///
    /// A dangling reference is the same failure as an unimplemented effect - the path draws without its effect -
    /// and it is worse to diagnose, because there is no name to search the file for. Reported beside the other
    /// references that led nowhere, so a driver comparing the file with the drawing has one place to look.
    /// </summary>
    [Fact]
    public void ADanglingPathEffectReferenceIsReported()
    {
        SvgImportResult result = SvgReader.Read(File(string.Empty));

        Assert.Contains(result.Missing, m =>
            m.Contains("path-effect1", StringComparison.Ordinal) &&
            m.Contains("path-effect", StringComparison.Ordinal));

        PathItem path = OnlyPath(result);

        // There is no element to keep, so nothing is invented to hang on the path.
        Assert.Empty(path.ForeignElements);

        Assert.False(path.Stroke.HasWidthProfile);
        IReadOnlyList<PathNode> nodes = Assert.Single(path.SubPaths).Nodes;
        Assert.Equal(new Point2D(0, 0), nodes[0].Anchor);
        Assert.Equal(new Point2D(80, 0), nodes[8].Anchor);
    }
}
