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
    private static byte[]? Programme()
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
                    return File.ReadAllBytes(font);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
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
