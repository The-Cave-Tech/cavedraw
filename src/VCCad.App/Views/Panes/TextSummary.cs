using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>
/// What a selection of text blocks agrees on, and what it does not.
///
/// The same judgement `StrokeSummary` makes about a stroke's members, applied to text: a panel editing a selection
/// has to show **one** value per member, and a selection is not obliged to agree with itself. Showing the first
/// block's size as though it were everyone's is the defect this exists to remove - a person reads a number, believes
/// it describes what they selected, and it does not. So each member is either the common value or explicitly
/// **mixed**, and the caller decides whether that is a blank field, a placeholder or a warning.
///
/// Two readings are deliberate, and they are the ones `StrokeSummary` already takes:
///
/// <list type="bullet">
/// <item><description>**One disagreeing member does not hide the others.** Two blocks whose colour differs still
/// report the family, size, weight and slant they agree on; blanking everything because one member differs throws
/// away most of what a panel shows.</description></item>
/// <item><description>**A block with no run at the inspected index is a gap, not a disagreement.** Blocks carry
/// different numbers of runs - that is what a run list is - and counting a shorter block as "different" would make
/// every selection of unequal blocks report every face member as mixed, which makes the report useless.</description></item>
/// </list>
///
/// Face members (family, size, weight, slant) are read **at the inspected run**, because the model holds them per
/// run, and so are the four the model gained for tracking and face selection - letter spacing, word spacing, font
/// stretch and font variant (#147) - the glyph orientation (#127) and the baseline shift (#128), which are likewise
/// the run's own, and the run's own colour (#190) alongside them. Content,
/// colour, alignment and paragraph style are read from the block, because the model holds one of each per block -
/// and so are the writing mode and the base direction (#127), which are how
/// the block's axes are set rather than what it says. A run can carry its own colour (TextRun.Color, #161), so a
/// colour change inside one block is expressible and the canvas and the exporter both read it; this summary reports
/// the colour the blocks agree on **and**, at the inspected run, the colour that run states for itself - absent
/// meaning it is drawn in the block's.
/// </summary>
public sealed record TextSummary(
    int Blocks,
    int Runs,
    string? Content,
    bool ContentMixed,
    string? Family,
    bool FamilyMixed,
    double? FontSize,
    bool FontSizeMixed,
    bool? Bold,
    bool BoldMixed,
    bool? Italic,
    bool ItalicMixed,
    ColorRgb? Color,
    bool ColorMixed,
    ColorRgb? RunColor,
    bool RunColorMixed,
    TextAlignment? Alignment,
    bool AlignmentMixed,
    double? LineSpacing,
    bool LineSpacingMixed,
    double? ParagraphSpacing,
    bool ParagraphSpacingMixed,
    double? RotationDegrees,
    bool RotationMixed,
    double? FrameWidth,
    bool FrameWidthMixed,
    double? LetterSpacing,
    bool LetterSpacingMixed,
    double? WordSpacing,
    bool WordSpacingMixed,
    string? FontStretch,
    bool FontStretchMixed,
    string? FontVariant,
    bool FontVariantMixed,
    GlyphOrientation? Orientation,
    bool OrientationMixed,
    double? BaselineShift,
    bool BaselineShiftMixed,
    TextWritingMode? WritingMode,
    bool WritingModeMixed,
    TextDirection? Direction,
    bool DirectionMixed)
{
    /// <summary>Whether the selection has nothing to describe - no selected text block.</summary>
    public bool IsEmpty => Blocks == 0;

    /// <summary>Whether any member disagrees across the selection.</summary>
    public bool IsMixed => ContentMixed || FamilyMixed || FontSizeMixed || BoldMixed || ItalicMixed
        || ColorMixed || RunColorMixed || AlignmentMixed || LineSpacingMixed || ParagraphSpacingMixed
        || RotationMixed || FrameWidthMixed || LetterSpacingMixed || WordSpacingMixed
        || FontStretchMixed || FontVariantMixed || OrientationMixed || BaselineShiftMixed
        || WritingModeMixed || DirectionMixed;

    /// <summary>
    /// Summarises the blocks at <paramref name="runIndex"/>, which names the run the face members are read from.
    ///
    /// A block whose run list is shorter than the index is skipped for those members rather than treated as
    /// disagreeing: it has no run there, which is a different thing from having one that differs.
    /// </summary>
    public static TextSummary Of(IEnumerable<TextItem> items, int runIndex)
    {
        var blocks = items.ToList();

        var runs = new List<TextRun>();
        if (runIndex >= 0)
        {
            foreach (TextItem block in blocks)
            {
                if (runIndex < block.Runs.Count)
                {
                    runs.Add(block.Runs[runIndex]);
                }
            }
        }

        if (blocks.Count == 0)
        {
            return new TextSummary(0, 0, null, false, null, false, null, false, null, false, null, false,
                null, false, null, false, null, false, null, false, null, false, null, false,
                null, false, null, false, null, false, null, false, null, false, null, false,
                null, false, null, false, null, false);
        }

        TextItem first = blocks[0];

        bool contentMixed = !blocks.All(b => string.Equals(b.PlainText, first.PlainText, StringComparison.Ordinal));
        bool colorMixed = !blocks.All(b => SameColor(b.Color, first.Color));
        bool alignmentMixed = !blocks.All(b => b.Alignment == first.Alignment);
        bool lineSpacingMixed = !blocks.All(b => Math.Abs(b.LineSpacing - first.LineSpacing) < 1e-9);
        bool paragraphMixed = !blocks.All(b => Math.Abs(b.ParagraphSpacing - first.ParagraphSpacing) < 1e-9);
        bool rotationMixed = !blocks.All(b => Math.Abs(b.RotationRadians - first.RotationRadians) < 1e-9);
        bool frameWidthMixed = !blocks.All(b => Math.Abs(b.FrameWidth - first.FrameWidth) < 1e-9);

        // The block's own axes and its base direction are block members like the leading and the alignment: the
        // model holds one of each per block, so a selection of a horizontal and a vertical block is genuinely mixed
        // about the mode.
        bool writingModeMixed = !blocks.All(b => b.WritingMode == first.WritingMode);
        bool directionMixed = !blocks.All(b => b.Direction == first.Direction);

        bool familyMixed = false;
        bool sizeMixed = false;
        bool boldMixed = false;
        bool italicMixed = false;
        bool letterSpacingMixed = false;
        bool wordSpacingMixed = false;
        bool stretchMixed = false;
        bool variantMixed = false;
        bool orientationMixed = false;
        bool baselineShiftMixed = false;
        bool runColorMixed = false;

        if (runs.Count > 0)
        {
            TextRun firstRun = runs[0];

            // Face is per run, and so are tracking, word spacing, stretch, variant and the glyph orientation: #147
            // and #127 put them on the run, so they are read at the inspected run exactly as family, size, weight
            // and slant are.
            familyMixed = !runs.All(r => string.Equals(r.FontFamily, firstRun.FontFamily, StringComparison.Ordinal));
            sizeMixed = !runs.All(r => Math.Abs(r.FontSize - firstRun.FontSize) < 1e-9);
            boldMixed = !runs.All(r => r.Bold == firstRun.Bold);
            italicMixed = !runs.All(r => r.Italic == firstRun.Italic);
            letterSpacingMixed = !runs.All(r => Math.Abs(r.LetterSpacing - firstRun.LetterSpacing) < 1e-9);
            wordSpacingMixed = !runs.All(r => Math.Abs(r.WordSpacing - firstRun.WordSpacing) < 1e-9);
            stretchMixed = !runs.All(r => string.Equals(r.FontStretch, firstRun.FontStretch, StringComparison.Ordinal));
            variantMixed = !runs.All(r => string.Equals(r.FontVariant, firstRun.FontVariant, StringComparison.Ordinal));
            orientationMixed = !runs.All(r => r.FontOrientation == firstRun.FontOrientation);
            baselineShiftMixed = !runs.All(r => Math.Abs(r.BaselineShift - firstRun.BaselineShift) < 1e-9);

            // A run's **own** colour, one level below the block's: absent is the model's way of saying "drawn in the
            // block's", so an absent run colour and a stated one are a disagreement rather than two spellings of the
            // same paint. Two stated colours that render the same are not, for the reason `SameColor` gives.
            runColorMixed = !runs.All(r => SameColorOrNone(r.Color, firstRun.Color));
        }

        return new TextSummary(
            blocks.Count,
            runs.Count,
            contentMixed ? null : first.PlainText,
            contentMixed,
            familyMixed ? null : runs.Count > 0 ? runs[0].FontFamily : null,
            familyMixed,
            sizeMixed ? null : runs.Count > 0 ? runs[0].FontSize : null,
            sizeMixed,
            boldMixed ? null : runs.Count > 0 ? runs[0].Bold : null,
            boldMixed,
            italicMixed ? null : runs.Count > 0 ? runs[0].Italic : null,
            italicMixed,
            colorMixed ? null : first.Color,
            colorMixed,
            runColorMixed ? null : runs.Count > 0 ? runs[0].Color : null,
            runColorMixed,
            alignmentMixed ? null : first.Alignment,
            alignmentMixed,
            lineSpacingMixed ? null : first.LineSpacing,
            lineSpacingMixed,
            paragraphMixed ? null : first.ParagraphSpacing,
            paragraphMixed,
            rotationMixed ? null : first.RotationRadians * 180.0 / Math.PI,
            rotationMixed,
            frameWidthMixed ? null : first.FrameWidth,
            frameWidthMixed,
            letterSpacingMixed ? null : runs.Count > 0 ? runs[0].LetterSpacing : null,
            letterSpacingMixed,
            wordSpacingMixed ? null : runs.Count > 0 ? runs[0].WordSpacing : null,
            wordSpacingMixed,
            stretchMixed ? null : runs.Count > 0 ? runs[0].FontStretch : null,
            stretchMixed,
            variantMixed ? null : runs.Count > 0 ? runs[0].FontVariant : null,
            variantMixed,
            orientationMixed ? null : runs.Count > 0 ? runs[0].FontOrientation : null,
            orientationMixed,
            baselineShiftMixed ? null : runs.Count > 0 ? runs[0].BaselineShift : null,
            baselineShiftMixed,
            writingModeMixed ? null : first.WritingMode,
            writingModeMixed,
            directionMixed ? null : first.Direction,
            directionMixed);
    }

    /// <summary>
    /// Whether two block colours are the same colour, compared where the person sees them.
    ///
    /// The model carries channels as 0..1 fractions while every field and operation speaks 0..255, so two colours
    /// that differ by less than a byte render, print and edit identically. Comparing the full doubles would call a
    /// selection mixed over a difference nothing can show.
    /// </summary>
    private static bool SameColor(ColorRgb a, ColorRgb b)
        => Channel(a.R) == Channel(b.R) && Channel(a.G) == Channel(b.G)
           && Channel(a.B) == Channel(b.B) && Channel(a.A) == Channel(b.A);

    private static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>
    /// Whether two run colours are the same statement, where an absent one is a statement of its own: "this run is
    /// drawn in the block's". So absent matches absent and nothing else, or a block whose second run states the
    /// colour the block already paints would report a colour mix nobody can see.
    /// </summary>
    private static bool SameColorOrNone(ColorRgb? a, ColorRgb? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return SameColor(a.Value, b.Value);
    }
}
