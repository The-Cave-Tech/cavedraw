using VCCad.Core.Model;
using VCCad.Pdf.Tests.Corpus;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Per-feature, corpus-driven tests for the highest-value import gaps listed in
/// AGENTS.md §9: Type3 fonts, non-embedded fonts, images, clipping, transparency
/// groups / soft masks / blend modes, and tiling/shading patterns.
///
/// Each test picks a concrete corpus fixture (falling back to a probe search so a
/// corpus reshuffle degrades to a different file rather than a false failure),
/// asserts what the probe must see, asserts the importer stays robust, and then
/// asserts either the correct behaviour where VCCad already works or the
/// observable current behaviour where there is a known gap. The gap assertions
/// are written to FAIL when the behaviour improves, so they cannot rot into
/// silently-true checks.
///
/// Every test returns early when the corpus or fixture is absent.
/// </summary>
public class PdfFeatureTargetedTests
{
    private readonly ITestOutputHelper _output;

    public PdfFeatureTargetedTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------
    // Type3 fonts
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void Type3Font_ImportsTextButNoGlyphGeometry()
    {
        string? path = Fixture("pdf/d1_with_color_ops.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.FontType3) &&
                           report.Has(PdfFeature.OpText) &&
                           !report.Has(PdfFeature.OpMove) &&
                           !report.Has(PdfFeature.OpRect));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.FontType3), $"{name}: probe should report a Type3 font");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);

        List<LayerItem> items = DocumentSanity.Items(document).ToList();
        List<TextItem> texts = items.OfType<TextItem>().ToList();
        Assert.NotEmpty(texts);

        // GAP (AGENTS.md §9, "Type3 fonts"): Type3 glyphs are content-stream
        // procedures in the font's /CharProcs. The importer never interprets
        // them, and a Type3 font carries no FontDescriptor, so BuildEmbeddedFont
        // returns null. The text therefore arrives as substituted text with no
        // embedded programme and no glyph outlines.
        foreach (TextRun run in texts.SelectMany(text => text.Runs))
        {
            Assert.Null(run.EmbeddedFont);
            Assert.False(string.IsNullOrEmpty(run.FontFamily), $"{name}: run must name a substitute family");
        }

        // No glyph geometry: the fixture's only painting operator is Tj.
        Assert.Empty(items.OfType<PathItem>());
    }

    // ------------------------------------------------------------------
    // non-embedded fonts
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void NonEmbeddedFont_ImportsAsSubstitutedText()
    {
        string? path = Fixture("pdf/singular_ctm_3_tr_mode.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.FontNonEmbedded) &&
                           !report.Has(PdfFeature.FontEmbedded) &&
                           report.Has(PdfFeature.OpText));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.FontNonEmbedded), $"{name}: probe should report a non-embedded font");
        Assert.False(probe.Has(PdfFeature.FontEmbedded), $"{name}: fixture also embeds a font; pick another");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);

        List<TextRun> runs = DocumentSanity.Items(document).OfType<TextItem>()
            .SelectMany(text => text.Runs).ToList();
        Assert.NotEmpty(runs);

        // Correct behaviour by design (AGENTS.md §8): a font with no embedded
        // programme cannot be passed through, so the run is drawn with a bundled
        // substitute family and the importer keeps the source advance width so
        // the substituted face does not reflow the layout.
        foreach (TextRun run in runs)
        {
            Assert.Null(run.EmbeddedFont);
            Assert.False(string.IsNullOrEmpty(run.FontFamily), $"{name}: run must name a substitute family");
            Assert.True(run.FontSize > 0 && double.IsFinite(run.FontSize), $"{name}: non-finite font size");
        }

        // GAP (AGENTS.md §9, "fonts with no embedded programme"): because the
        // original face is unavailable, re-export cannot reproduce it; fidelity
        // rests on the advance-width compensation above.
        Assert.DoesNotContain(runs, run => run.RawCodes is not null);
    }

    // ------------------------------------------------------------------
    // images
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void ImageOnlyPage_ImportsAsAnEmptyArtboard()
    {
        string? path = Fixture("pdf/Jbig2_042_01.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.ImageAny) &&
                           report.Count("count.images") > 0 &&
                           !report.Has(PdfFeature.OpMove) &&
                           !report.Has(PdfFeature.OpRect) &&
                           !report.Has(PdfFeature.OpText) &&
                           !report.Has(PdfFeature.OpCurve));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.ImageAny), $"{name}: probe should report an image XObject");
        Assert.True(probe.Count("count.images") > 0, $"{name}: probe should count the image");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);
        Assert.NotEmpty(document.Artboards);

        // GAP (AGENTS.md §9, "images"): PdfContentImporter.DrawXObject handles
        // only /Subtype /Form, so a page whose entire content is "<name> Do" for
        // an image XObject imports as an empty artboard.
        Assert.Empty(DocumentSanity.Items(document));
        AssertNoModelConcept("image", "Image");
    }

    // ------------------------------------------------------------------
    // clipping
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void ClippingOperators_AreIgnoredSoClippedContentIsPaintedUnclipped()
    {
        string? path = Fixture("Ghent_V3.0/010_CMYK_OP_x3.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Count("op.W") > 0 && report.Has(PdfFeature.OpMove));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.OpClip), $"{name}: probe should report W/W*");
        int clipOps = probe.Count("op.W");
        Assert.True(clipOps > 0, $"{name}: probe should tally the clip operators");
        _output.WriteLine($"{clipOps} clip operators; {probe}");

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);
        Assert.NotEmpty(DocumentSanity.Items(document));

        // GAP (AGENTS.md §9, "clipping (W/Wn)"): PdfContentImporter has no case
        // for W or W*, and VCCad.Core has no representation of a clip region, so
        // a clip path changes nothing about the imported artwork — content that
        // the source PDF clips away is painted in full.
        AssertNoModelConcept("clip", "Clip");
    }

    // ------------------------------------------------------------------
    // even-odd fills
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void EvenOddFills_AreImportedWithTheEvenOddWindingRule()
    {
        string? path = Fixture("pdf/Bug6901014_CImg_flyer.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.OpEvenOdd) && report.Has(PdfFeature.OpFill));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.OpEvenOdd), $"{name}: probe should report f*/B*/W*");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);

        List<FillSpec> fills = DocumentSanity.Items(document).OfType<PathItem>()
            .Select(item => item.Fill).Where(fill => fill.IsVisible).ToList();
        Assert.NotEmpty(fills);

        // The star in f*/B*/b* is the fill rule, not decoration. The file paints some of
        // this document's holes with even-odd, so those fills must arrive carrying it —
        // otherwise every counter drawn that way fills solid.
        Assert.Contains(fills, fill => fill.Rule == FillRule.EvenOdd);
    }

    // ------------------------------------------------------------------
    // transparency: what already works
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void ExtGStateFillAlpha_IsAppliedToImportedFills()
    {
        string? path = Fixture("Ghent_V5.0/GWG1610_Softmasks_Text_part1_X4.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.GsFillAlphaTranslucent) && report.Has(PdfFeature.OpFill));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.GsFillAlpha), $"{name}: probe should report an ExtGState /ca");
        Assert.True(probe.Has(PdfFeature.GsFillAlphaTranslucent),
            $"{name}: fixture must set /ca < 1 or this test proves nothing");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);

        List<double> alphas = DocumentSanity.Items(document).OfType<PathItem>()
            .Where(item => item.Fill.IsVisible)
            .Select(item => item.Fill.Color.A)
            .ToList();
        Assert.NotEmpty(alphas);

        // Works today: the gs handler reads /ca into the graphics state and
        // FlushPath puts it on the fill colour, so translucency survives import.
        Assert.Contains(alphas, alpha => alpha < 1.0);
        Assert.All(alphas, alpha => Assert.InRange(alpha, 0.0, 1.0));
    }

    // ------------------------------------------------------------------
    // transparency: the gap
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void BlendModesSoftMasksAndTransparencyGroups_HaveNoModelRepresentation()
    {
        string? path = Fixture("Ghent_V5.0/GWG160_Transp_Basic_BM_DeviceCMYK_Non-knockout_X4.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.GsBlendModeNonNormal) &&
                           report.Has(PdfFeature.TransparencyGroup));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.GsBlendMode), $"{name}: probe should report ExtGState /BM");
        Assert.True(probe.Has(PdfFeature.TransparencyGroup), $"{name}: probe should report a /Group");
        Assert.True(probe.Has(PdfFeature.GsSoftMask) || probe.Has(PdfFeature.GsBlendModeNonNormal),
            $"{name}: fixture should exercise a soft mask or a non-Normal blend mode");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);
        Assert.NotEmpty(DocumentSanity.Items(document));

        // GAP (AGENTS.md §9, "transparency groups / soft masks / blend modes"):
        // the gs handler reads only /ca and /CA. /BM, /SMask and a
        // transparency-group /Group are dropped, and VCCad.Core has no blend,
        // soft-mask or transparency-group concept to import them into — so a
        // Non-knockout/Multiply composite imports as plain opaque source-over.
        AssertNoModelConcept("blend", "Blend");
        AssertNoModelConcept("soft mask", "SoftMask");
        AssertNoModelConcept("transparency group", "Transparency");
    }

    // ------------------------------------------------------------------
    // patterns and shadings
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void TilingPatterns_ImportAsSolidColourWithNoPatternRepresentation()
    {
        string? path = Fixture("pdf/scn-in-pattern.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Has(PdfFeature.PatternTiling) && report.Has(PdfFeature.OpFill));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.PatternTiling), $"{name}: probe should report /PatternType 1");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);

        List<PathItem> filled = DocumentSanity.Items(document).OfType<PathItem>()
            .Where(item => item.Fill.IsVisible).ToList();
        Assert.NotEmpty(filled);

        // GAP (AGENTS.md §9, "tiling/shading patterns"): painting with a pattern
        // colour space leaves the previous solid colour in place (ResolveColor
        // answers black for /Pattern) and no pattern tile is imported, because
        // the tiling pattern's own content stream is never visited.
        AssertNoModelConcept("pattern", "Pattern");
    }

    [Fact]
    [Trait("Category", "Corpus")]
    public void ShadingOperator_IsDroppedByImport()
    {
        string? path = Fixture("Ghent_V3.0/060_Shading_x1a.pdf")
                       ?? PdfCorpus.FirstFileWhere(report =>
                           report.Count("op.sh") > 0 && report.Has(PdfFeature.OpMove));
        if (path is null)
        {
            return;
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport probe = PdfCorpus.Probe(path);
        Assert.True(probe.Has(PdfFeature.OpShading), $"{name}: probe should report the sh operator");
        Assert.True(probe.Has(PdfFeature.ShadingAny), $"{name}: probe should report a shading dictionary");
        _output.WriteLine(probe.ToString());

        CadDocument document = Import(path);
        DocumentSanity.AssertSane(document, name);
        Assert.NotEmpty(DocumentSanity.Items(document));

        // GAP: the sh operator has no case in the content interpreter, so axial
        // and radial shadings disappear entirely from the imported artwork.
        AssertNoModelConcept("shading", "Shading");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>Absolute path of a named corpus fixture, or null when absent.</summary>
    private static string? Fixture(string relative)
    {
        string? root = PdfCorpus.CorpusRoot();
        if (root is null)
        {
            return null;
        }

        string path = Path.Combine(root, relative);
        return File.Exists(path) ? path : null;
    }

    private static CadDocument Import(string path)
    {
        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));
        Assert.NotNull(document);
        return document;
    }

    /// <summary>
    /// Asserts the model still cannot express <paramref name="concept"/> and
    /// explains what to do when it can.
    /// </summary>
    private static void AssertNoModelConcept(string concept, string memberFragment)
    {
        bool present = ModelCapabilities.HasMemberMatching(
            candidate => candidate.Contains(memberFragment, StringComparison.OrdinalIgnoreCase) &&
                         candidate != "DashPattern");
        Assert.False(present,
            $"VCCad.Core now models a '{concept}' concept; replace this gap pin with a positive " +
            "assertion that the corpus feature is imported into it.");
    }
}
