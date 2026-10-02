using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **The three files #128 names, asserted as geometry.**
///
/// `SvgCorpusTests` proves the corpus imports (an artboard, an object, no throw) and deliberately says nothing about
/// where anything ends up. These are the files the issue is about, and what it asks for is not that they import but
/// that they are *drawn where the file says*:
///
/// - `text-glyphs-combining.svg` and `test-glyph-y-pos.svg` are a base letter followed by combining marks in three
///   writing modes - the mark must be drawn at its base and take no advance of its own, in every one of them;
/// - `test-baseline-shift.svg` is `H` + a `baseline-shift:sub`/`super` `2` + `O`, so the shifted run's glyphs must sit
///   below or above the line's baseline by its own shift.
///
/// The corpus is an optional data source, so every test here returns without asserting when it is not found - the
/// sentinel the corpus rules require - and `TheCorpusIsFound` in `SvgCorpusTests` is what fails loudly if the corpus
/// moved rather than was never fetched.
/// </summary>
public class SvgTextCorpusTests
{
    private static string? RenderingTests()
    {
        string? configured = Environment.GetEnvironmentVariable("VCCAD_INKSCAPE_CORPUS");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            try
            {
                string? found = Directory
                    .GetDirectories(directory, "*", SearchOption.AllDirectories)
                    .Prepend(directory)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "text-glyphs-combining.svg")));
                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The blocks of a corpus file, or null when the corpus is not on this machine.</summary>
    private static TextItem[]? Blocks(string file)
    {
        string? root = RenderingTests();
        if (root is null)
        {
            return null;
        }

        string path = Path.Combine(root, file);
        return File.Exists(path)
            ? SvgReader.ReadFile(path).Document.AllItems().OfType<TextItem>().ToArray()
            : null;
    }

    /// <summary>
    /// Every combining mark is drawn at its base and takes no advance, in each writing mode the file states.
    ///
    /// This is the whole of "a base plus combining mark producing zero added advance and the mark drawn over the
    /// base": the mark's own inline place is its base's, and the pen does not move after it - so the letters are as
    /// far apart as the file's own base characters and no further.
    /// </summary>
    [Theory]
    [InlineData("text-glyphs-combining.svg")]
    [InlineData("test-glyph-y-pos.svg")]
    public void EveryCombiningMarkSitsOnItsBase(string file)
    {
        TextItem[]? blocks = Blocks(file);
        if (blocks is null || blocks.Length == 0)
        {
            return;
        }

        int marksSeen = 0;
        foreach (TextItem block in blocks)
        {
            TextLayout layout = TextLayoutEngine.Compute(block);

            // The flattened text of the block, so a glyph's own character can be asked about directly. A glyph's
            // `Index` is its place in that text, which is the one thing the visual order does not change.
            string flat = string.Concat(block.Runs.Select(r => r.Text));
            Assert.Equal(flat.Length, layout.Glyphs.Count);

            var byIndex = layout.Glyphs.ToDictionary(g => g.Index);
            for (int i = 1; i < flat.Length; i++)
            {
                if (!TextMeasurement.IsCombiningMark(flat[i]))
                {
                    continue;
                }

                marksSeen++;
                GlyphBox mark = byIndex[i];
                GlyphBox baseGlyph = byIndex[i - 1];

                Assert.Equal(baseGlyph.Inline, mark.Inline, 9);
                Assert.Equal(0.0, mark.Advance, 9);
            }
        }

        Assert.True(marksSeen > 0, $"{file} is about combining marks and none were seen");
    }

    /// <summary>
    /// **The shifted run sits off the line's baseline by its own shift.** `H` + `sub` `2` + `O` puts the `2` below the
    /// baseline and `super` puts it above, and the runs before and after it are unmoved - a `baseline-shift` moves a
    /// run, not the line.
    /// </summary>
    [Fact]
    public void TheBaselineShiftFileMovesTheShiftedRunOffTheLine()
    {
        TextItem[]? blocks = Blocks("test-baseline-shift.svg");
        if (blocks is null)
        {
            return;
        }

        // The file states the same pair twice horizontally and twice as a vertical column. This test reads the
        // horizontal ones, where the shift moves a glyph up or down the page; a column's shift moves it across the
        // column instead, which is the same rule on the other axis and not what these assertions are measuring.
        TextItem[] shiftedBlocks = blocks
            .Where(b => b.WritingMode == TextWritingMode.HorizontalTb)
            .Where(b => b.Runs.Any(r => Math.Abs(r.BaselineShift) > 1e-9))
            .ToArray();
        Assert.NotEmpty(shiftedBlocks);

        foreach (TextItem shifted in shiftedBlocks)
        {
            int index = shifted.Runs.FindIndex(r => Math.Abs(r.BaselineShift) > 1e-9);
            Assert.True(index > 0, "the shifted run follows an unshifted one");

            TextLayout layout = TextLayoutEngine.Compute(shifted);
            double baseline = layout.Lines[0].Baseline;
            double shift = TextLayoutEngine.BaselineShiftFor(shifted.Runs[index]);
            Assert.NotEqual(0.0, shift);

            double before = layout.Glyphs.First(g => g.Run == index - 1).Y;
            double here = layout.Glyphs.First(g => g.Run == index).Y;

            // `ShiftUp` moves a horizontal glyph up the page for a positive shift, which is -y in this frame. The run
            // before it is unmoved - a `baseline-shift` moves a run, not the line - and the file's `super` case is the
            // last run of its block, so a following run is checked only where there is one.
            Assert.Equal(baseline, before, 9);
            Assert.Equal(baseline - shift, here, 9);
            if (index + 1 < shifted.Runs.Count)
            {
                Assert.Equal(baseline, layout.Glyphs.First(g => g.Run == index + 1).Y, 9);
            }
        }
    }
}
