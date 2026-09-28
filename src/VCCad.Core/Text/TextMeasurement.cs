using VCCad.Core.Model;

namespace VCCad.Core.Text;

/// <summary>
/// Real font measurement, supplied by whichever host is running.
///
/// The document model has to know how wide text is: bounds, wrapping, selection and
/// hit-testing all depend on it. But <c>VCCad.Core</c> must not reference a rendering
/// toolkit — the dependency rule is Geometry &lt;- Core &lt;- {Pdf, Api, App}, and the model
/// has to work headless.
///
/// So measurement is an interface the host installs at startup. The App installs an
/// Avalonia-backed implementation that asks the text shaper; a headless test can install
/// a fixed one. There is deliberately **one** measurement path in the running program:
/// the canvas and the model ask the same object, so the box the person sees and the box
/// the model reports cannot disagree.
/// </summary>
public interface ITextMetrics
{
    /// <summary>
    /// Advance widths, one per character of the run, in document units.
    ///
    /// These come from the shaper, so kerning and ligatures are included: the width of
    /// "AV" is not the width of "A" plus the width of "V".
    /// </summary>
    IReadOnlyList<double> Advances(TextRun run);

    /// <summary>Ascent above the baseline, in document units, for the run's face.</summary>
    double Ascent(TextRun run);

    /// <summary>Descent below the baseline, in document units, for the run's face.</summary>
    double Descent(TextRun run);
}

/// <summary>
/// The measurement source in force, plus the estimate used when no host has installed
/// one.
///
/// The estimate exists so the model is never unusable — a headless deserialise or a unit
/// test with no renderer still gets an answer. It is a fallback, not a policy: in the
/// running application <see cref="Current"/> is always set, so real metrics are used.
/// </summary>
public static class TextMeasurement
{
    private static ITextMetrics? _current;

    /// <summary>True when a real measurer is installed, rather than the fallback.</summary>
    public static bool IsReal => _current is not null;

    /// <summary>The installed measurer, or null when only the estimate is available.</summary>
    public static ITextMetrics? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>
    /// The last-resort advance for one character, used only when no measurer is
    /// installed (a headless deserialise or a unit test with no renderer).
    ///
    /// Four numbers constitute the whole of the guessing in this codebase — this advance,
    /// the ascent and descent below, and <see cref="TypicalAscentEm"/> — and they live here
    /// rather than scattered, so a search for any of them finds all of it. Nothing outside
    /// this class estimates: callers ask for an advance, an ascent or a descent, and get a
    /// real shaper's answer when a host has installed one.
    ///
    /// Every one of them has an overload taking a size, because half the callers have a
    /// size and no run — a caret, a selection highlight, a face being resolved before its
    /// text exists. Those callers used to write the constant out again, which is how this
    /// class came to claim there were three numbers while six sites held their own copy.
    /// </summary>
    public static double Estimate(TextRun run) => run.FontSize * 0.6;

    /// <summary>The last-resort advance, in em. See <see cref="Estimate"/>.</summary>
    public const double EstimatedAdvanceEm = 0.6;

    /// <summary>The last-resort ascent, in em. See <see cref="Estimate"/>.</summary>
    public const double EstimatedAscentEm = 0.8;

    /// <summary>The last-resort descent, in em. See <see cref="Estimate"/>.</summary>
    public const double EstimatedDescentEm = 0.2;

    /// <summary>
    /// The ascent to assume for a face that declares none.
    ///
    /// A font's own descriptor is the right source and is used whenever it is present; this
    /// is what a Latin face with no <c>/Ascent</c> is assumed to rise to, in em. It is
    /// larger than <see cref="EstimatedAscentEm"/> because the two answer different
    /// questions: this is where a <em>typographic</em> ascent sits, which is what places a
    /// baseline, while that one is the box a line of text needs.
    /// </summary>
    public const double TypicalAscentEm = 0.928;

    /// <summary>The last-resort ascent. See <see cref="Estimate"/>.</summary>
    public static double EstimatedAscent(TextRun run) => run.FontSize * EstimatedAscentEm;

    /// <summary>The last-resort ascent for a size with no run to carry it.</summary>
    public static double EstimatedAscent(double fontSize) => fontSize * EstimatedAscentEm;

    /// <summary>The last-resort descent. See <see cref="Estimate"/>.</summary>
    public static double EstimatedDescent(TextRun run) => run.FontSize * EstimatedDescentEm;

    /// <summary>The last-resort descent for a size with no run to carry it.</summary>
    public static double EstimatedDescent(double fontSize) => fontSize * EstimatedDescentEm;

    /// <summary>Per-character advances, real when available and estimated otherwise.</summary>
    public static IReadOnlyList<double> Advances(TextRun run)
    {
        if (_current is { } metrics && run.Text.Length > 0)
        {
            return metrics.Advances(run);
        }

        int n = run.Text.Length;
        var estimate = new double[n];
        for (int i = 0; i < n; i++)
        {
            estimate[i] = TextMeasurement.Estimate(run);
        }

        return estimate;
    }

    /// <summary>
    /// The advance of a single character, which is what a caret position needs.
    /// </summary>
    public static double AdvanceOf(TextRun run, int index)
    {
        IReadOnlyList<double> advances = Advances(run);
        return index >= 0 && index < advances.Count ? advances[index] : TextMeasurement.Estimate(run);
    }

    /// <summary>
    /// The advance to use for a caret sitting past the last character, which is what the
    /// end of a line needs.
    ///
    /// Callers used to reach for the estimate directly here, which meant a real shaper was
    /// installed and ignored. The run's own last character is a better answer than a guess
    /// about the font size, and it is the width the person already sees.
    /// </summary>
    public static double AdvanceAtEnd(TextRun run)
    {
        if (_current is { } metrics && run.Text.Length > 0)
        {
            IReadOnlyList<double> advances = metrics.Advances(run);
            if (advances.Count > 0)
            {
                return advances[advances.Count - 1];
            }
        }

        return Estimate(run);
    }

    /// <summary>Ascent in document units, real when available.</summary>
    public static double Ascent(TextRun run)
        => _current?.Ascent(run) ?? EstimatedAscent(run);

    /// <summary>Descent in document units, real when available.</summary>
    public static double Descent(TextRun run)
        => _current?.Descent(run) ?? EstimatedDescent(run);
}
