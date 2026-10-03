using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **An SVG-in-OpenType font's glyph definitions are read out of the programme.**
///
/// `text-svg-glyph-custom.svg` declares `@font-face { font-family: "SVGinOTF testfont1"; src: url("./svginotf/…"); }`
/// and writes `abcd` in it. The corpus ships the programme the file names, and it is a purpose-built test font: four
/// glyphs, each with its own gzipped SVG document in the `SVG ` table, so the picture the file means is the one in
/// that table rather than the outlines beside it.
///
/// The assertion is the documents themselves - the family, the glyph ids the cmap gives, and the SVG text each id
/// resolves to - because "the programme parsed" would pass for a parser that read the header and nothing else.
///
/// The corpus is optional, so every test returns without asserting when it is not installed.
/// </summary>
public class SvgFontProgrammeTests
{
    private static string? RenderingTests()
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string root in Directory.GetDirectories(cache, "inkscape*"))
        {
            try
            {
                string? font = Directory
                    .GetFiles(root, "svginotf_testfont1.otf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (font is not null)
                {
                    return Path.GetDirectoryName(font)!;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static byte[]? Programme()
    {
        string? directory = RenderingTests();
        string path = Path.Combine(directory ?? string.Empty, "svginotf_testfont1.otf");
        return directory is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// **The face a document supplies is loaded from its own stylesheet.** `text-svg-glyph-custom.svg` names its
    /// font in an `@font-face` rule with a path relative to itself, which is how a file carries a webfont - so the
    /// reader has to read the at-rule, resolve the path against the document's own directory, and load what is
    /// there. The assertion is the loaded programme's own numbers, and the two ways it must fail quietly: a `src`
    /// naming a file that is not there, and one naming a file that is not a font at all.
    /// </summary>
    [Fact]
    public void TheDocumentsOwnFaceIsLoadedFromItsStylesheet()
    {
        string? directory = RenderingTests();
        if (directory is null)
        {
            return;
        }

        const string css = "@font-face { font-family: \"SVGinOTF testfont1\"; src: url(\"./svginotf_testfont1.otf\"); }";
        SvgFontFaces faces = SvgFontFaces.Load(css, directory);

        Assert.True(faces.Any);
        SvgFontProgramme? face = faces.Find("SVGinOTF testfont1");
        Assert.NotNull(face);
        Assert.Equal(1000, face!.UnitsPerEm);
        Assert.NotEqual(0, face.GlyphFor('a'));

        // The family is matched as the stylesheet writes it, which may be quoted and is not case-sensitive.
        Assert.NotNull(faces.Find("svginotf TESTFONT1"));

        // A `src` that is not there, and one that is there but is not a font, both load nothing - and neither
        // throws, because an unloadable face is a thing to report rather than a reason to fail an import.
        Assert.False(SvgFontFaces.Load("@font-face { font-family: X; src: url(\"missing.otf\"); }", directory).Any);
        Assert.False(SvgFontFaces.Load("@font-face { font-family: X; src: url(\"build.py\"); }", directory).Any);
    }

    [Fact]
    public void TheProgrammeCarriesItsSVGGlyphsAndItsCmap()
    {
        byte[]? bytes = Programme();
        if (bytes is null)
        {
            return;
        }

        SvgFontProgramme? programme = SvgFontProgramme.Parse(bytes);
        Assert.NotNull(programme);

        Assert.Equal("SVGinOTF testfont1", programme!.FamilyName);

        // The font is built with one glyph per character, a, b, c and d, and none of them is the missing glyph.
        int a = programme.GlyphFor('a');
        int b = programme.GlyphFor('b');
        int c = programme.GlyphFor('c');
        int d = programme.GlyphFor('d');
        Assert.NotEqual(0, a);
        Assert.NotEqual(0, b);
        Assert.NotEqual(0, c);
        Assert.NotEqual(0, d);
        Assert.Equal(0, programme.GlyphFor('z'));

        // Each of the four carries a document, and they are four different drawings.
        string[] documents = new[] { a, b, c, d }.Select(id => programme.DocumentFor(id)!).ToArray();
        Assert.All(documents, document => Assert.Contains("<svg", document, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, documents.Distinct(StringComparer.Ordinal).Count());

        // The record is gzipped inside the table, so a parser that skipped the inflation would answer with the
        // compressed bytes rather than with a document - which the document's own markup already rules out.
        Assert.Contains("path", documents[0], StringComparison.OrdinalIgnoreCase);

        // Glyph ids the programme carries.
        Assert.Equal(4, programme.GlyphIds.Count);

        // The em square and the advances, which is what a caller needs to place the glyphs rather than merely find
        // them: `build.py` beside the font sets 1000 units per em and a width of 500 for every glyph except `b`,
        // which is 502 - so the numbers are the font's own recipe, not this parser's assumption.
        Assert.Equal(1000, programme.UnitsPerEm);
        Assert.Equal(500, programme.AdvanceFor(a));
        Assert.Equal(502, programme.AdvanceFor(b));
        Assert.Equal(500, programme.AdvanceFor(c));
        Assert.Equal(500, programme.AdvanceFor(d));
    }

    /// <summary>Something that is not a font at all is not a font: the ordinary case every caller must survive.</summary>
    [Fact]
    public void ANonFontAnswersNull()
    {
        Assert.Null(SvgFontProgramme.Parse(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(SvgFontProgramme.Parse(System.Text.Encoding.ASCII.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>")));
    }
}
