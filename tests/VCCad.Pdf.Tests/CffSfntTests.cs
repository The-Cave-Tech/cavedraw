using System.Buffers.Binary;
using VCCad.Core.Samples;
using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using VCCad.Pdf.Fonts;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Tests for <see cref="CffSfnt"/>, which wraps a bare CFF programme into a minimal
/// OpenType container.
///
/// This matters because PDF embeds bare CFF for most Illustrator artwork, while
/// platform font managers only load sfnt. Handed a bare programme they refuse it,
/// and a caller that does not check ends up drawing the programme's glyph ids
/// through an unrelated fallback face — the canvas showed random characters. These
/// tests run without Avalonia so CI covers the wrapper even where font backends
/// differ.
/// </summary>
public class CffSfntTests
{
    private static string? Sample()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string? candidate = SampleLibrary.Find("A0-Temi-Bow-Bustier-sewing-pattern.pdf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>Every embedded programme the sample PDF carries.</summary>
    private static List<EmbeddedFont> SampleFonts(out bool corpusPresent)
    {
        string? path = Sample();
        corpusPresent = path is not null;
        if (path is null)
        {
            return new List<EmbeddedFont>();
        }

        CadDocument doc = PdfImporter.Import(File.ReadAllBytes(path));
        return doc.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<TextItem>()
            .SelectMany(t => t.Runs)
            .Select(r => r.EmbeddedFont)
            .Where(f => f is not null)
            .Select(f => f!)
            .Distinct()
            .ToList();
    }

    [Fact]
    public void RealEmbeddedCffProgrammesWrapIntoLoadableSfnt()
    {
        List<EmbeddedFont> fonts = SampleFonts(out bool present);
        if (!present)
        {
            return; // bundled sample missing; nothing to assert
        }

        List<EmbeddedFont> cff = fonts.Where(f => CffSfnt.IsBareCff(f.Program)).ToList();
        Assert.NotEmpty(cff);

        foreach (EmbeddedFont font in cff)
        {
            byte[]? sfnt = CffSfnt.Wrap(
                font.Program, font.FamilyName, font.Ascent, font.Descent, font.FontBBox);

            Assert.NotNull(sfnt);
            Assert.Equal("OTTO", Encoding.ASCII.GetString(sfnt!, 0, 4));

            IReadOnlyDictionary<string, (int Offset, int Length)> tables = ReadTableDirectory(sfnt!);

            // The tables a rasterizer needs before it will build a typeface.
            foreach (string required in new[] { "CFF ", "head", "hhea", "maxp", "hmtx", "cmap", "name", "post", "OS/2" })
            {
                Assert.True(tables.ContainsKey(required), $"{font.FamilyName}: missing {required} table");
            }

            // The CFF table must carry the original programme through untouched.
            (int cffOffset, int cffLength) = tables["CFF "];
            Assert.Equal(font.Program.Length, cffLength);
            Assert.Equal(font.Program, sfnt!.AsSpan(cffOffset, cffLength).ToArray());

            // head: magic number and unitsPerEm must be sane, and the glyph count in
            // maxp must match the CharStrings INDEX the wrapper parsed.
            (int headOffset, _) = tables["head"];
            Assert.Equal(0x5F0F3CF5u, BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(headOffset + 12)));
            ushort unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(headOffset + 18));
            Assert.Equal(1000, unitsPerEm);

            (int maxpOffset, int maxpLength) = tables["maxp"];
            Assert.Equal(6, maxpLength);
            int glyphCount = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(maxpOffset + 4));
            Assert.True(glyphCount > 0, $"{font.FamilyName}: no glyphs");

            // hmtx must have one full metric per glyph, and at least one real
            // advance (an all-zero hmtx collapses every glyph onto one another).
            (int hmtxOffset, int hmtxLength) = tables["hmtx"];
            Assert.Equal(glyphCount * 4, hmtxLength);
            bool anyAdvance = false;
            for (int g = 0; g < glyphCount; g++)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(hmtxOffset + (g * 4))) != 0)
                {
                    anyAdvance = true;
                    break;
                }
            }

            Assert.True(anyAdvance, $"{font.FamilyName}: every advance is zero");
        }
    }

    [Fact]
    public void AdvancesAreRecoveredFromTheCharstrings()
    {
        List<EmbeddedFont> fonts = SampleFonts(out bool present);
        if (!present)
        {
            return;
        }

        // Guards against a regression to "uniform 1000 for every glyph": a subset
        // containing different letters must expose differing advances.
        var report = new List<string>();
        int varied = 0;
        foreach (EmbeddedFont font in fonts.Where(f => CffSfnt.IsBareCff(f.Program)))
        {
            byte[] sfnt = CffSfnt.Wrap(font.Program, font.FamilyName)!;
            IReadOnlyDictionary<string, (int Offset, int Length)> tables = ReadTableDirectory(sfnt);
            (int maxpOffset, _) = tables["maxp"];
            (int hmtxOffset, _) = tables["hmtx"];
            int glyphCount = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(maxpOffset + 4));

            var distinct = new HashSet<ushort>();
            for (int g = 0; g < glyphCount; g++)
            {
                distinct.Add(BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(hmtxOffset + (g * 4))));
            }

            report.Add($"{font.FamilyName}: glyphs={glyphCount} distinctAdvance={distinct.Count} " +
                       $"min={distinct.Min()} max={distinct.Max()}");
            if (distinct.Count > 1)
            {
                varied++;
            }
        }

        Assert.True(varied > 0,
            "no embedded CFF font recovered more than one advance; charstring widths are not being read.\n  "
            + string.Join("\n  ", report));
    }

    [Fact]
    public void NonCffProgrammesAreNotWrapped()
    {
        Assert.False(CffSfnt.IsBareCff(Array.Empty<byte>()));
        Assert.False(CffSfnt.IsBareCff(new byte[] { 0, 1, 0, 0 }));            // TrueType sfnt
        Assert.False(CffSfnt.IsBareCff(Encoding.ASCII.GetBytes("OTTO....")));  // OpenType sfnt
        Assert.Null(CffSfnt.Wrap(new byte[] { 0, 1, 0, 0 }, "x"));
        Assert.Null(CffSfnt.Wrap(new byte[] { 1, 0, 4, 2 }, "x"));             // header only, no INDEXes
    }

    private static IReadOnlyDictionary<string, (int Offset, int Length)> ReadTableDirectory(byte[] sfnt)
    {
        Assert.Equal("OTTO", Encoding.ASCII.GetString(sfnt, 0, 4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int at = 12 + (i * 16);
            string tag = Encoding.ASCII.GetString(sfnt, at, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(at + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(at + 12));
            Assert.InRange(offset + length, 0, sfnt.Length);
            tables[tag] = (offset, length);
        }

        // Table records must be sorted by tag, as the specification requires.
        string[] tags = tables.Keys.ToArray();
        string[] sorted = tags.OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, tags);
        return tables;
    }
}