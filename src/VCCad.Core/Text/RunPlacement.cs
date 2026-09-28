namespace VCCad.Core.Text;

/// <summary>One run, and the width it takes up on its line.</summary>
/// <param name="Text">The run's text, whose newlines are the only line breaks.</param>
/// <param name="Width">
/// How far the pen moves. The PDF's own advance where the importer captured one, so a
/// substitute font does not reflow the layout.
/// </param>
/// <param name="LineHeight">The height of a line of this run, for a break inside it.</param>
public readonly record struct RunMetrics(string Text, double Width, double LineHeight);

/// <summary>Where a run sits, relative to the start of the block.</summary>
public readonly record struct RunPlacement(int Run, double X, double Y, double Width);

/// <summary>
/// Where each run of a text object goes.
///
/// Runs are pieces of a line, not lines. A PDF lays a heading out as separate pieces -
/// "Jalie", "3464", "-", "LILLIE" - each with its own move, and the importer keeps them as
/// runs of one text object. Treating a run as a line stacks them at the same x, one line
/// below the other, which is what put every heading on the page on top of itself.
///
/// The only thing that starts a new line is a newline in the text.
/// </summary>
public static class RunLayout
{
    /// <summary>Places every run relative to the block's own origin.</summary>
    public static IReadOnlyList<RunPlacement> Place(IReadOnlyList<RunMetrics> runs)
    {
        var placed = new List<RunPlacement>(runs.Count);
        double x = 0;
        double y = 0;

        for (int i = 0; i < runs.Count; i++)
        {
            RunMetrics run = runs[i];
            placed.Add(new RunPlacement(i, x, y, run.Width));

            // The run is drawn from (x, y); the pen then moves along the line by its width.
            x += run.Width;

            int breaks = CountBreaks(run.Text);
            if (breaks > 0)
            {
                y += breaks * run.LineHeight;
                x = 0;
            }
        }

        return placed;
    }

    /// <summary>The width of the block: the widest line it contains.</summary>
    public static double BlockWidth(IReadOnlyList<RunMetrics> runs)
    {
        IReadOnlyList<RunPlacement> placed = Place(runs);
        double widest = 0;
        double lineStart = 0;

        for (int i = 0; i < placed.Count; i++)
        {
            bool last = i == placed.Count - 1;
            bool breaks = CountBreaks(runs[i].Text) > 0;

            if (last || breaks)
            {
                double end = placed[i].X + placed[i].Width;
                widest = Math.Max(widest, end - lineStart);
                if (breaks)
                {
                    lineStart = 0;
                }
            }
        }

        return widest;
    }

    private static int CountBreaks(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
