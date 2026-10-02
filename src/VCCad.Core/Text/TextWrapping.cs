using VCCad.Core.Model;

namespace VCCad.Core.Text;

/// <summary>
/// Where a text block's lines break.
///
/// One implementation, used by everything that needs to know: the model computes its
/// bounds from it, the canvas lays out the caret and the edit box with it, and export
/// breaks the emitted lines with it. When the editor wraps and export does not, the
/// exported page is not the page that was on screen — which is the whole point of the
/// import/export fidelity rule.
///
/// The rule: an explicit newline always breaks; inside a frame, break at the last space
/// that fits, and break a single word wider than the frame rather than let it overflow.
/// A space at the end of a line is collapsed when the line is drawn, so it must not
/// count towards the width — otherwise lines wrap one early and blocks report more
/// height than they have.
/// </summary>
public static class TextWrapping
{
    /// <summary>One display line, as a range over the block's flattened text.</summary>
    public readonly record struct LineRange(int Start, int Length);

    /// <summary>
    /// The block's display lines, over the concatenation of all its runs.
    ///
    /// An empty block still has one (empty) line, so callers can count lines without
    /// special-casing; a block ending in a newline gets the trailing empty line it shows.
    /// </summary>
    public static List<LineRange> Lines(TextItem text)
    {
        (string flat, List<double> widths) = Flatten(text);

        double frame = text.FrameWidth;
        var lines = new List<LineRange>();

        int lineStart = 0;
        int lastSpace = -1;
        double lineWidth = 0;

        for (int i = 0; i < flat.Length; i++)
        {
            char ch = flat[i];

            if (ch == '\n')
            {
                lines.Add(new LineRange(lineStart, i - lineStart));
                lineStart = i + 1;
                lastSpace = -1;
                lineWidth = 0;
                continue;
            }

            if (ch == ' ')
            {
                lastSpace = i;
            }

            lineWidth += widths[i];

            if (frame > 0 && i > lineStart && ch != ' ' && lineWidth > frame)
            {
                // Break at the last space that fits; if the current word alone is wider
                // than the frame, break it rather than let it run out of the box.
                int breakAt = lastSpace > lineStart ? lastSpace : i;

                // **A surrogate pair is one character, and no line may end between its halves.** A break inside
                // one puts half a character at the end of one line and the other half at the start of the next -
                // a string no reader or writer can hold, and one that a UTF-16 substring or an XML writer rejects
                // outright. Moving the break in front of the pair keeps it whole on the next line, and is only
                // done when that leaves the line something to hold; a pair wider than the frame on its own
                // overflows instead, exactly as a single character wider than the frame does.
                if (breakAt > lineStart + 1 &&
                    char.IsLowSurrogate(flat[breakAt]) &&
                    char.IsHighSurrogate(flat[breakAt - 1]))
                {
                    breakAt--;
                }

                lines.Add(new LineRange(lineStart, breakAt - lineStart));
                lineStart = breakAt == lastSpace ? breakAt + 1 : breakAt;
                lastSpace = -1;
                lineWidth = 0;

                // Re-measure the characters carried onto the new line.
                for (int k = lineStart; k <= i; k++)
                {
                    if (flat[k] == ' ')
                    {
                        lastSpace = k;
                    }

                    lineWidth += widths[k];
                }
            }
        }

        lines.Add(new LineRange(lineStart, flat.Length - lineStart));
        return lines;
    }

    /// <summary>The block's text as one string, with each character's advance width.</summary>
    public static (string Text, List<double> Widths) Flatten(TextItem text)
    {
        var builder = new System.Text.StringBuilder();
        var widths = new List<double>();

        foreach (TextRun run in text.Runs)
        {
            builder.Append(run.Text);

            // The run's own advances, tracking included: `TextRun.Advances` is the one place the
            // letter and word spacing a file states is added to the face's widths, so the wrap point,
            // the caret, the bounds and export all move the pen by the same numbers.
            IReadOnlyList<double> advances = run.Advances();

            double natural = 0;
            for (int i = 0; i < advances.Count; i++)
            {
                natural += advances[i];
            }

            // A run the importer captured an advance for keeps that width, however the face we draw
            // it with is proportioned: a substitute font must not reflow the page. Spreading the
            // recorded advance over the run's characters proportionally keeps every position -
            // caret, wrap point, run boundary - on one set of advances.
            double scale = run.AdvanceWidth is > 0 && natural > 0 ? run.AdvanceWidth.Value / natural : 1.0;

            for (int i = 0; i < run.Text.Length; i++)
            {
                double advance = i < advances.Count ? advances[i] : TextMeasurement.AdvanceAtEnd(run);
                widths.Add(advance * scale);
            }
        }

        return (builder.ToString(), widths);
    }
}
