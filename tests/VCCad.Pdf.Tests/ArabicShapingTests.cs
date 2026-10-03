using VCCad.Core.Text;
using VCCad.Pdf.Fonts;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The presentation-form table against a real Arabic face** (issue #197).
///
/// The table maps each Arabic letter to the four contextual forms Unicode defines for it. Written by hand, so it is
/// checked against a font that has them: every form must have a glyph in `arial.ttf`, and for a letter that joins,
/// the isolated and final forms must be **different glyphs** - which is the whole point of the transformation, and
/// catches a table entry that points at the wrong code point.
///
/// Skips when the machine has no such face, which is this repository's rule for an optional data source.
/// </summary>
public class ArabicShapingTests
{
    private static TrueTypeFont? Arial()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
        return File.Exists(path) ? new TrueTypeFont(File.ReadAllBytes(path)) : null;
    }

    [Fact]
    public void EveryFormInTheTableHasAGlyphInARealArabicFace()
    {
        TrueTypeFont? font = Arial();
        if (font is null)
        {
            return;
        }

        var missing = new List<string>();
        var notDistinct = new List<string>();

        // The letters the table covers. U+063B-U+063F are outside it: this shaper leaves them isolated rather than
        // guessing at forms, and the export declares that - the comment on Forms says so.
        foreach (char letter in ArabicShaping.Letters())
        {
            foreach ((bool joinsPrevious, bool joinsNext, string name) in new[]
                     {
                         (false, false, "isolated"), (false, true, "initial"),
                         (true, true, "medial"), (true, false, "final"),
                     })
            {
                char form = ArabicShaping.FormFor(letter, joinsPrevious, joinsNext);
                if (font.GlyphFor(form) == 0)
                {
                    missing.Add($"{letter:X4} {name} {form:X4}");
                }
            }

            // A letter that joins at all must have a different glyph for its joined form.
            char isolated = ArabicShaping.FormFor(letter, false, false);
            char final = ArabicShaping.FormFor(letter, true, false);
            // Tatweel is a joining connector with one shape, not a letter with contextual forms.
            if (letter != '\u0640' && ArabicShaping.JoinsForward(letter) &&
                font.GlyphFor(isolated) == font.GlyphFor(final))
            {
                notDistinct.Add($"{letter:X4}");
            }
        }

        Assert.Empty(missing);
        Assert.Empty(notDistinct);
    }

    [Fact]
    public void TheIssueWordIsShapedIntoPresentationForms()
    {
        // س ل ا م: meem is final, alef is final, lam and seen join - and the run comes out right to left.
        string shaped = ArabicShaping.Shape("سلام");

        Assert.All(shaped, c => Assert.True(
            c is >= '\uFE70' and <= '\uFEFF' || c is >= '\u0621' and <= '\u064A',
            $"'{c}' (U+{(int)c:X4}) is neither a presentation form nor an Arabic letter"));

        Assert.NotEqual("سلام", shaped);
    }

    [Fact]
    public void TheRunIsWrittenInVisualOrderWithItsContextualForms()
    {
        // `سلام` read right to left is seen, lam-alef, meem - so a pen that advances left to right writes meem
        // **first**, and seen last. Asserting the ends pins both the reversal and the form selection: an unmapped
        // per-character run would put seen at the front, isolated.
        (string shaped, int[] clusters) = ArabicShaping.ShapeWithClusters("سلام");

        Assert.Equal(3, shaped.Length);
        Assert.Equal('\uFEE1', shaped[0]);   // meem, isolated: alef before it does not join forward
        Assert.Equal('\uFEFB', shaped[1]);   // lam-alef, one code point
        Assert.Equal('\uFEB3', shaped[2]);   // seen, initial: it joins the lam that follows it
        Assert.Equal(new[] { 3, 1, 0 }, clusters);
    }

    [Fact]
    public void HebrewReversesWithoutJoining()
    {
        // Hebrew's letters keep their shapes and the run still has to run right to left: שלום comes out םולש.
        (string shaped, int[] clusters) = ArabicShaping.ShapeWithClusters("שלום");

        Assert.Equal("םולש", shaped);
        Assert.Equal(new[] { 3, 2, 1, 0 }, clusters);
    }

    [Fact]
    public void LamAlefIsOneCodePoint()
    {
        // The mandatory ligature: two letters, one glyph.
        string shaped = ArabicShaping.Shape("لا");
        Assert.Single(shaped);
    }
}