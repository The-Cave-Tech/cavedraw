using VCCad.Core.Model;
using VCCad.Core.Text;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The caret sits where the glyphs are (issue #248).
///
/// The canvas draws a text block and its caret from the same layout engine: the glyphs from `Glyphs`, the caret
/// from `CaretX[index]` where `index` is the document-wide character offset the person clicked at. So the caret is
/// correct by construction **only if those two agree** - and they are computed on different paths, because an
/// imported run whose advance the file stated has its glyphs scaled to that advance while the caret positions come
/// from the character advances.
///
/// The Lillie page header is the case in point: seven runs, each with an advance the file stated, summing to 102.9
/// units while the block draws 124.1. The person's report is that clicking between the `4` of `34` and the `6` of
/// `64` puts the caret too far to the right - inside a glyph rather than in the gap.
///
/// This asserts the invariant rather than a number, so it holds for any face this machine resolves.
/// </summary>
public class TextCaretAgreesWithGlyphsTests
{
    /// <summary>The Lillie header as the importer builds it: seven pieces, each with its own advance.</summary>
    private static TextItem ImportedHeader()
    {
        var text = new TextItem { Name = "header" };
        Add(text, "Jalie ", 16.8574);
        Add(text, "3464 ", 19.406);
        Add(text, "- ", 3.599);
        Add(text, "LILLIE ", 22.052);
        Add(text, "- ", 3.599);
        Add(text, "Page ", 19.318);
        Add(text, "1/12", 18.061);
        return text;

        static void Add(TextItem text, string piece, double advance)
            => text.Runs.Add(new TextRun
            {
                Text = piece,
                FontFamily = "Nimbus Sans",
                FontSize = 9,
                AdvanceWidth = advance,
            });
    }

    /// <summary>No imported advances: the plain case, which the caret is certainly right for.</summary>
    private static TextItem PlainHeader()
    {
        var text = new TextItem { Name = "plain" };
        text.Runs.Add(new TextRun
        {
            Text = "Jalie 3464 - LILLIE - Page 1/12",
            FontFamily = "Nimbus Sans",
            FontSize = 9,
        });
        return text;
    }

    [Fact]
    public void WithNoImportedAdvanceTheCaretIsAtThePenOfTheCharacterItPrecedes()
    {
        TextLayout layout = TextLayoutEngine.Compute(PlainHeader());

        // One caret per glyph, plus the slot past the last character - which is where the caret sits when it is at
        // the end of the block.
        Assert.Equal(layout.Glyphs.Count + 1, layout.CaretX.Count);
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            Assert.Equal(layout.Glyphs[i].X, layout.CaretX[i], 3);
        }
    }

    /// <summary>
    /// And with the file's own advances, where the glyphs are scaled to them - the case the person was looking at.
    /// </summary>
    [Fact]
    public void WithImportedAdvancesTheCaretIsStillAtThePenOfItsCharacter()
    {
        TextLayout layout = TextLayoutEngine.Compute(ImportedHeader());

        Assert.Equal(layout.Glyphs.Count + 1, layout.CaretX.Count);
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            Assert.True(
                Math.Abs(layout.Glyphs[i].X - layout.CaretX[i]) < 0.01,
                $"the caret before character {i} ('{CharacterAt(layout, i)}') is at {layout.CaretX[i]:0.###} " +
                $"but that character is drawn at {layout.Glyphs[i].X:0.###}");
        }
    }

    /// <summary>
    /// The person's exact case stated as a gap: the caret between the `34` and the `64` must fall between the
    /// character before it and the character after it, not inside either.
    /// </summary>
    [Fact]
    public void TheCaretBetweenThe34AndThe64FallsInTheGap()
    {
        TextLayout layout = TextLayoutEngine.Compute(ImportedHeader());

        // Document offset 8: after "Jalie 34".
        const int Offset = 8;
        double caret = layout.CaretX[Offset];

        // The character before the caret ('4') starts at its own x and runs to where the caret is; the character
        // after it ('6') starts where the caret is. So the caret is the boundary between them.
        Assert.True(
            caret > layout.Glyphs[Offset - 1].X,
            $"the caret at {caret:0.###} is not past the '4' at {layout.Glyphs[Offset - 1].X:0.###}");
        Assert.True(
            caret <= layout.Glyphs[Offset].X + 0.01,
            $"the caret at {caret:0.###} is inside the '6' at {layout.Glyphs[Offset].X:0.###}");
    }

    private static string CharacterAt(TextLayout layout, int index)
    {
        string flat = string.Concat(ImportedHeader().Runs.Select(run => run.Text));
        return index < flat.Length ? flat[index].ToString() : "end";
    }
}
