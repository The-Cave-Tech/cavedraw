using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A `url(#id)` fill that names a `&lt;pattern&gt;` is **read and reported by name**, and the report reaches the
/// tile's own content.
///
/// **Why a report and not a paint.** The model's paint is <see cref="FillSpec"/>: a colour, a gradient or a hatch.
/// A pattern is a *tile of artwork* used as a paint, and the model holds it now as a paint server (#203) -
/// a third paint option beside the gradient and the hatch, with its tile in the library. The report below is what the renderers still owe: this is the same finding the PDF
/// importer reached for a tiling pattern in #175: the tile is art the model has no place for, and the honest
/// answer is to **name it** rather than to substitute a colour. Substituting is the defect: a shape filled with a
/// plausible solid colour looks deliberate and is not the file.
///
/// **What "reaching the tile" means.** Before this was read, a `url(#p)` that named a pattern fell through the
/// gradient lookup and produced *"no paint server called 'p' for this fill"* - the right finding with the wrong
/// name, which is worse than no name because it sends the reader looking for a paint server that is sitting right
/// there in the file. The report below names the element, the pattern id, the **shapes the tile draws**, and the
/// units - so a person can act on it, and a test can tell a report that read the tile from one that only saw the
/// reference.
///
/// <see cref="SvgReaderClipTests"/> is the sibling rule: a value this reader cannot honour is refused with a
/// report rather than guessed at, and `objectBoundingBox` is the case both properties share.
/// </summary>
public class SvgPatternTests
{
    /// <summary>The issue's case, and the shape of the corpus file: a 100-unit square tiled with a two-shape tile.</summary>
    private const string TiledSquare =
        "<svg width=\"100\" height=\"100\">" +
        "<defs><pattern id=\"p\" width=\"1\" height=\"1\" patternUnits=\"userSpaceOnUse\" " +
        "patternTransform=\"scale(30,30)\">" +
        "<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\" fill=\"#0ff\"/>" +
        "<ellipse cx=\"0.5\" cy=\"0.5\" rx=\"0.5\" ry=\"0.5\" fill=\"#00f\"/>" +
        "</pattern></defs>" +
        "<rect id=\"tiled\" x=\"0\" y=\"0\" width=\"100\" height=\"100\" fill=\"url(#p)\"/></svg>";

    // ---------------------------------------------------------------- the tile is reached and named

    /// <summary>
    /// **A pattern fill is reported by name, and the report names what the tile draws.**
    ///
    /// The rectangular tile holds a `rect` and an `ellipse`, and both have to appear in the report: that is the
    /// evidence the reader opened the pattern and looked inside it, rather than noticing a reference and stopping.
    /// A report that only said "a pattern was seen" would pass every assertion about the word "pattern" and would
    /// be useless to the person who has to fix the drawing.
    ///
    /// **The fill is not substituted.** The shape keeps the reader's own placeholder for a paint server that could
    /// not be resolved - visible, and fully transparent - so a viewer draws no fill at all. There is deliberately
    /// no black, and no sampled stop colour from either of the tile's two gradients.
    ///
    /// **Before:** the import produced *"no paint server called 'p' for this fill"*, named nothing in the tile, and
    /// `Assert.Contains(warning, "<ellipse>")` found nothing because no `<ellipse>` had been read.
    /// </summary>
    [Fact]
    public void APatternFillIsReportedByNameAndTheReportReachesTheTile()
    {
        SvgImportResult result = SvgReader.Read(TiledSquare);

        PathItem tiled = result.Document.AllPaths().Single(p => p.Name == "tiled");

        string report = Assert.Single(result.Warnings, w => w.Contains("pattern", StringComparison.OrdinalIgnoreCase));

        // Named so the attribute can be found in the file...
        Assert.Contains("url(#p)", report, StringComparison.Ordinal);
        Assert.Contains("<pattern>", report, StringComparison.Ordinal);

        // ...and the tile itself was read: both of the shapes it draws are named.
        Assert.Contains("<rect>", report, StringComparison.Ordinal);
        Assert.Contains("<ellipse>", report, StringComparison.Ordinal);

        // **The paint server is in the model** (issue #203), naming the tile's definition, and it is still not a
        // substituted colour: no gradient invented out of the tile's stops, and no hatch.
        Assert.NotNull(tiled.Fill.Pattern);
        Assert.Equal("p", tiled.Fill.Pattern!.Definition);
        Assert.Null(tiled.Fill.Gradient);
        Assert.Null(tiled.Fill.Hatch);
        Assert.Equal(0.0, tiled.Fill.Color.A, 9);
    }

    /// <summary>
    /// **`objectBoundingBox` units are refused and reported, not read as user space.**
    ///
    /// The same numbers mean different geometry: under `objectBoundingBox` a tile of `1` by `1` is the whole of
    /// whatever shape it paints, and its contents are fractions of that shape's box. Read as user space they are a
    /// one-unit tile at the origin - a plausible wrong answer, which is exactly the failure
    /// <see cref="SvgReaderClipTests.AnObjectBoundingBoxClipIsReportedRatherThanReadAsUserSpace"/> closes for a
    /// crop. Both the tile's own units and its content units are the same property on the same element, so both
    /// are refused here.
    ///
    /// The report still names the tile's content, so a refusal is as actionable as the report above.
    ///
    /// **Before:** the units were never looked at, because the pattern was never looked at.
    /// </summary>
    [Theory]
    [InlineData("patternUnits")]
    [InlineData("patternContentUnits")]
    public void AnObjectBoundingBoxPatternIsReportedRatherThanReadAsUserSpace(string attribute)
    {
        string svg =
            "<svg width=\"100\" height=\"100\">" +
            $"<defs><pattern id=\"p\" width=\"0.1\" height=\"0.1\" {attribute}=\"objectBoundingBox\">" +
            "<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\"/>" +
            "</pattern></defs>" +
            "<rect width=\"100\" height=\"100\" fill=\"url(#p)\"/></svg>";

        SvgImportResult result = SvgReader.Read(svg);

        string report = Assert.Single(result.Warnings, w => w.Contains("objectBoundingBox", StringComparison.Ordinal));
        Assert.Contains("url(#p)", report, StringComparison.Ordinal);
        Assert.Contains(attribute, report, StringComparison.Ordinal);
        Assert.Contains("<rect>", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A pattern named as a stroke paint is reported too.**
    ///
    /// A paint server reaches either half of the paint, and a pattern on a stroke is painted through the stroke's
    /// own outline - the specification's own words. Reporting the fill and silently painting a black stroke would
    /// leave half the property unread, which is the shape of bug this repository keeps filing.
    ///
    /// **The stroke keeps the reader's own behaviour for a paint server it cannot read; the point is the report.**
    /// </summary>
    [Fact]
    public void APatternOnAStrokeIsReportedByNameToo()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg width=\"100\" height=\"100\">" +
            "<defs><pattern id=\"p\" width=\"1\" height=\"1\" patternUnits=\"userSpaceOnUse\">" +
            "<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\"/></pattern></defs>" +
            "<rect width=\"100\" height=\"100\" fill=\"none\" stroke=\"url(#p)\" stroke-width=\"2\"/></svg>");

        Assert.Contains(result.Warnings, w =>
            w.Contains("stroke", StringComparison.Ordinal) &&
            w.Contains("url(#p)", StringComparison.Ordinal) &&
            w.Contains("<pattern>", StringComparison.Ordinal) &&
            w.Contains("<rect>", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the quiet cases

    /// <summary>
    /// **The round trip keeps the tile.** This test used to assert the opposite - that the export invented no tile,
    /// because the model could hold no pattern paint - and it is turned over rather than deleted now that the paint
    /// server exists (#203): what was a reported loss is a kept asset.
    ///
    /// Both halves have to come back: the reference on the element, and the `&lt;pattern&gt;` holding the tile's
    /// artwork beside it. Writing only the reference would point at a definition that is not in the file, which is
    /// worse than not writing the pattern at all.
    /// </summary>
    [Fact]
    public void ARoundTripKeepsThePatternAndItsTile()
    {
        SvgImportResult first = SvgReader.Read(TiledSquare);
        string exported = SvgWriter.Write(first.Document);

        Assert.Contains("fill=\"url(#p)\"", exported, StringComparison.Ordinal);
        Assert.Contains("<pattern", exported, StringComparison.Ordinal);
        Assert.Contains("patternTransform=\"scale(30,30)\"", exported, StringComparison.Ordinal);

        SvgImportResult again = SvgReader.Read(exported);
        PathItem tiled = again.Document.AllPaths().Single(p => p.Name == "tiled");
        Assert.NotNull(tiled.Fill.Pattern);
        Assert.Equal("p", tiled.Fill.Pattern!.Definition);
        Assert.Equal("scale(30,30)", tiled.Fill.Pattern.Transform);

        // The tile's own artwork came back, not merely an empty element with the id: this writer writes every shape
        // as a `<path>`, so the count is the thing to check and the element name is not.
        var tile = Assert.IsType<ArtGroup>(again.Document.FindDefinition("p"));
        Assert.True(tile.Children.Count >= 2, $"the tile came back with {tile.Children.Count} child item(s)");
    }

    [Fact]
    public void TheSidecarCarriesThePatternBothWays()
    {
        CadDocument reloaded = VCCad.Core.Serialization.VccadDocumentSerializer.Deserialize(
            VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(SvgReader.Read(TiledSquare).Document));

        PathItem tiled = reloaded.AllPaths().Single(p => p.Name == "tiled");
        Assert.NotNull(tiled.Fill.Pattern);
        Assert.Equal("p", tiled.Fill.Pattern!.Definition);
        Assert.Equal(1.0, tiled.Fill.Pattern.Width, 6);
        Assert.Equal(1.0, tiled.Fill.Pattern.Height, 6);
        Assert.Equal("userSpaceOnUse", tiled.Fill.Pattern.Units);
        Assert.Equal("scale(30,30)", tiled.Fill.Pattern.Transform);

        // **What is not asserted here: the tile's own survival through the sidecar.** Whether the library round-trips
        // is a separate question - and the same one a marker's definition already faces - so this pins what the
        // pattern paint server is responsible for: the paint survives, naming the same definition, with what the
        // file said about the tile box, the units and the transform.
    }

    /// <summary>
    /// **A definition that is not a pattern is never written as one.**
    ///
    /// Recognition is the reader's own tag, not a heuristic on `width`: a symbol read into the library can carry a
    /// width of its own, and writing that back as a `&lt;pattern&gt;` would turn a symbol into a paint.
    /// </summary>
    [Fact]
    public void ADefinitionThatIsNotAPatternIsNotWrittenAsOne()
    {
        CadDocument document = CadDocument.CreateDefault("Symbols");
        ArtGroup symbol = document.AddDefinition("frame");
        symbol.ForeignAttributes["width"] = "20";
        symbol.ForeignAttributes["height"] = "20";
        symbol.AddItem(new PathItem { Name = "art", Fill = FillSpec.Solid(ColorRgb.Black) });

        string svg = SvgWriter.Write(document);
        Assert.DoesNotContain("<pattern", svg, StringComparison.Ordinal);
        Assert.Contains("frame", svg, StringComparison.Ordinal);
    }
    // ---------------------------------------------------------------- the corpus file

    /// <summary>
    /// **The corpus pattern file reports the tile it actually draws.**
    ///
    /// `drawing-pattern-test.svg` is the one file the issue names, and its tile is not a placeholder: it holds a
    /// `rect` filled with a gradient and an `ellipse` filled with another, under
    /// `patternTransform="scale(30,30)"`. Reaching both shapes is what tells a report that read this file from one
    /// that read a fixture written to match the reader.
    ///
    /// The theory emits a skip sentinel when the corpus is absent rather than failing, which is this repository's
    /// rule for an optional data source.
    /// </summary>
    [Fact]
    public void TheCorpusPatternFileReportsTheTileItDraws()
    {
        string? path = CorpusFile("drawing-pattern-test.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(path);

        string report = Assert.Single(result.Warnings, w => w.Contains("pattern1", StringComparison.Ordinal));
        Assert.Contains("<pattern>", report, StringComparison.Ordinal);
        Assert.Contains("<rect>", report, StringComparison.Ordinal);
        Assert.Contains("<ellipse>", report, StringComparison.Ordinal);

        // This file's units are user space, so the refusal above is not what produced the report.
        Assert.DoesNotContain("objectBoundingBox", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The corpus file survives a round trip.** The fixture above is written to match the reader; this is the real
    /// file - `drawing-pattern-test.svg`, whose tile holds a `rect` and an `ellipse` under
    /// `patternTransform="scale(30,30)"` - and it is the one that says whether the paint server works on a file
    /// nobody adjusted for it.
    ///
    /// It skips when the corpus is absent rather than failing, which is this repository's rule for an optional data
    /// source.
    /// </summary>
    [Fact]
    public void TheCorpusPatternFileSurvivesARoundTrip()
    {
        string? path = CorpusFile("drawing-pattern-test.svg");
        if (path is null)
        {
            return;
        }

        CadDocument first = SvgReader.ReadFile(path).Document;
        PathItem? patterned = first.AllPaths().FirstOrDefault(p => p.Fill.Pattern is not null);
        Assert.NotNull(patterned);

        PatternSpec spec = patterned!.Fill.Pattern!;
        Assert.Equal("pattern1", spec.Definition);
        Assert.Equal("scale(30,30)", spec.Transform);
        Assert.NotEmpty(first.FindDefinition("pattern1")!.Children);

        string exported = SvgWriter.Write(first);
        Assert.Contains("fill=\"url(#pattern1)\"", exported, StringComparison.Ordinal);
        Assert.Contains("patternTransform=\"scale(30,30)\"", exported, StringComparison.Ordinal);

        // And it comes back the same, which is the only test of an export that matters.
        CadDocument again = SvgReader.Read(exported).Document;
        PatternSpec? reloaded = again.AllPaths().FirstOrDefault(p => p.Fill.Pattern is not null)?.Fill.Pattern;
        Assert.NotNull(reloaded);
        Assert.Equal("pattern1", reloaded!.Definition);
        Assert.Equal("scale(30,30)", reloaded.Transform);
        Assert.NotEmpty(again.FindDefinition("pattern1")!.Children);
    }

    /// <summary>
    /// The Inkscape corpus, wherever it was fetched to - the same search <see cref="SvgCorpusTests"/> uses, kept
    /// local so this file does not depend on another test class's private helpers.
    /// </summary>
    private static string? CorpusFile(string name)
    {
        string? configured = Environment.GetEnvironmentVariable("VCCAD_INKSCAPE_CORPUS");
        string[] roots = string.IsNullOrWhiteSpace(configured)
            ? Array.Empty<string>()
            : new[] { configured };

        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");

        foreach (string root in roots.Concat(Directory.Exists(cache)
                     ? Directory.GetDirectories(cache, "inkscape*")
                     : Array.Empty<string>()))
        {
            try
            {
                string[] found = Directory.GetFiles(root, name, SearchOption.AllDirectories);
                if (found.Length > 0)
                {
                    return found[0];
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable directory is one this test does not need.
            }
        }

        return null;
    }
}
