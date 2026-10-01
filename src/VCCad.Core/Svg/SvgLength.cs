using System.Globalization;

namespace VCCad.Core.Svg;

/// <summary>
/// The viewport a percentage resolves against, in the units the file's own coordinates are written in.
///
/// **These are SVG user units, not model points.** The reader keeps the file's coordinate system (one user unit
/// is one CSS pixel) and maps it into the model with the viewport transform - so a percentage here is a percentage
/// of the viewport as the file measures it, and the transform that follows carries the file into points.
/// </summary>
internal readonly record struct SvgViewport(double Width, double Height)
{
    /// <summary>
    /// The reference a percentage with no obvious axis resolves against.
    ///
    /// SVG says the "normalized diagonal" for <c>r</c> and the stroke properties, which is what makes a circle with
    /// <c>r="50%"</c> fit inside a viewport of any shape rather than growing with one axis.
    /// </summary>
    public double Diagonal => Math.Sqrt(((Width * Width) + (Height * Height)) / 2.0);
}

/// <summary>
/// Which measurement of the viewport a percentage of this attribute is a percentage of.
///
/// SVG does not have one rule, it has three: a horizontal coordinate is a fraction of the width, a vertical one of
/// the height, and a length with no direction - a radius, a stroke width - of the diagonal, so that a circle keeps
/// its shape in a viewport that is not square.
/// </summary>
internal enum SvgAxis
{
    X,
    Y,
    Diagonal,
}

/// <summary>
/// A length as SVG writes it, resolved into the units the document's own coordinates are in.
///
/// **The unit is not decoration.** The reader used to take the number and drop the suffix, which reads `1in` as one
/// unit and `72pt` as seventy-two - so a file measured in millimetres imported a ninety-sixth of its size, and two
/// lengths SVG calls equal (`96px` and `72pt`) came out differently. Every absolute unit is converted here at the
/// CSS ratio: **1in = 96px = 72pt = 25.4mm = 2.54cm**, so all five resolve to one number.
///
/// The space they resolve INTO is the document's own: SVG's user unit, which is a CSS pixel, and which is the same
/// unit the page's `width` and `height` are measured in. That is what makes the answer a single number for a length
/// wherever it appears. Carrying the whole page from that space into the model's points is the reader's business and
/// not this table's: it is one factor (<see cref="UserUnitsToPoints"/>) applied in one place, the viewport - so the
/// unit a length was written in decides its number, and where it sits decides nothing.
///
/// A bare number is already a user unit and is returned unchanged - SVG's rule is that a number with no unit is a
/// length in the current user space, which is the page's own space until a view box says otherwise. A percentage is
/// **reported as such** rather than silently resolving to its number: what it means depends on the axis and the
/// viewport, and only the caller knows either - so a caller that has no viewport to offer reports it instead of
/// substituting a value the file did not write.
/// </summary>
internal static class SvgLength
{
    /// <summary>CSS pixels per inch, which is the unit SVG's user coordinate system is defined in.</summary>
    internal const double PixelsPerInch = 96.0;

    /// <summary>PostScript points per inch, which is the unit the model is stored in.</summary>
    internal const double PointsPerInch = 72.0;

    /// <summary>
    /// Model points per SVG user unit, which is the one factor between the file's space and the model's.
    ///
    /// A user unit is a CSS pixel and the model stores PDF points (`AGENTS.md` §8), so a length the file writes is
    /// three quarters of the length it describes. The factor is applied **once**, where the document's viewport is
    /// read, so a value cannot resolve differently depending on which element reads it - which is what a per-attribute
    /// conversion would eventually do.
    /// </summary>
    internal const double UserUnitsToPoints = PointsPerInch / PixelsPerInch;

    /// <summary>Millimetres per inch, the exact definition of the inch.</summary>
    internal const double MillimetresPerInch = 25.4;

    /// <summary>
    /// The font size `em` and `ex` fall back to.
    ///
    /// This is CSS's initial font size - the size of an element that declares none - so it is the value the
    /// specification already gives, not one invented here. It is still an assumption about text this reader does
    /// not yet read, which is why using it is reported.
    /// </summary>
    internal const double DefaultFontSize = 16.0;

    /// <summary>A length in the file's own user units, or null when it cannot be read.</summary>
    internal static double? Parse(string? text, Action<string>? warn = null)
        => ParseWithUnit(text, warn) is { IsPercent: false } parsed ? parsed.Value : null;

    /// <summary>
    /// A length, saying whether it was written as a percentage.
    ///
    /// The split is the whole point: a percentage is a length the reader has to hand back to its caller, and one
    /// that has been read as a plain number is a position the file did not name.
    /// </summary>
    internal static (double Value, bool IsPercent)? ParseWithUnit(string? text, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        int end = NumberEnd(trimmed);
        if (end == 0 ||
            !double.TryParse(trimmed[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            warn?.Invoke($"'{trimmed}' is not a length");
            return null;
        }

        string unit = trimmed[end..].Trim();
        switch (unit.ToLowerInvariant())
        {
            case "":
            case "px":
                return (value, false);

            // 1pt is 1/72in and 1in is 96px, so a point is four thirds of a user unit. This is the ratio that makes
            // 72pt and 96px the same length, and a reader that assumed they were equal imports every point-measured
            // file at three quarters of its size.
            case "pt":
                return (value * PixelsPerInch / PointsPerInch, false);

            // A pica is a sixth of an inch, i.e. twelve points.
            case "pc":
                return (value * PixelsPerInch / 6.0, false);

            case "in":
                return (value * PixelsPerInch, false);

            case "mm":
                return (value * PixelsPerInch / MillimetresPerInch, false);

            case "cm":
                return (value * PixelsPerInch / (MillimetresPerInch / 10.0), false);

            // The quarter-millimetre, which is CSS's `Q` and is what a Japanese paper size is usually written in.
            case "q":
                return (value * PixelsPerInch / (MillimetresPerInch * 4.0), false);

            case "em":
                warn?.Invoke(
                    $"'{trimmed}' is relative to a font size this reader has no text context for, " +
                    $"and was resolved against CSS's initial size of {DefaultFontSize}px");
                return (value * DefaultFontSize, false);

            case "ex":
                warn?.Invoke(
                    $"'{trimmed}' is relative to a font size this reader has no text context for, " +
                    $"and was resolved against half of CSS's initial size of {DefaultFontSize}px");
                return (value * DefaultFontSize / 2.0, false);

            case "%":
                return (value, true);

            default:
                // Viewport units and font-relative units this reader cannot resolve. Reported rather than
                // dropped, because the caller's fallback is a value the file did not ask for.
                warn?.Invoke($"'{trimmed}' is a length in a unit the reader does not know");
                return null;
        }
    }

    /// <summary>
    /// Where a number ends, which is where the unit begins.
    ///
    /// The same grammar the rest of the reader uses: an optional sign, digits, one dot and one exponent. It stops
    /// rather than failing so that `10mm` is a number and a unit, and it reads `10-5` as two numbers rather than
    /// one malformed token - which is what `points` and `viewBox` rely on.
    /// </summary>
    private static int NumberEnd(string text)
    {
        int end = 0;
        bool seenDot = false;
        bool seenExponent = false;

        while (end < text.Length)
        {
            char c = text[end];
            if (char.IsDigit(c))
            {
                end++;
                continue;
            }

            if (c == '.' && !seenDot && !seenExponent)
            {
                seenDot = true;
                end++;
                continue;
            }

            if (c is '+' or '-' && (end == 0 || text[end - 1] is 'e' or 'E'))
            {
                end++;
                continue;
            }

            // An exponent only where a number follows it, because `1em` is one unit and not a malformed ten: a
            // scanner that takes the `e` and then fails leaves the whole length unread, which is how a font-relative
            // unit silently became the attribute's default.
            if (c is 'e' or 'E' && !seenExponent && ExponentFollows(text, end))
            {
                seenExponent = true;
                end++;
                continue;
            }

            break;
        }

        return end;
    }

    /// <summary>Whether the `e` at this position starts an exponent rather than a unit name.</summary>
    private static bool ExponentFollows(string text, int at)
    {
        int next = at + 1;
        if (next < text.Length && text[next] is '+' or '-')
        {
            next++;
        }

        return next < text.Length && char.IsDigit(text[next]);
    }
}
