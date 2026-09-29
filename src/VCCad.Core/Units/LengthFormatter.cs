using System.Globalization;

namespace VCCad.Core.Units;

/// <summary>
/// The one place a length becomes text and text becomes a length.
///
/// Every readout goes through <see cref="Format(Length, LengthUnit)"/> and every field entry
/// through <see cref="TryParse"/>, both taking the unit explicitly. No display path may assume
/// millimetres: whoever draws the number passes the configured unit in.
/// </summary>
public static class LengthFormatter
{
    /// <summary>
    /// How much of a unit a readout keeps. Nine decimal places of a millimetre is a nanometre:
    /// finer than any drawing, and enough that "typed, read out, typed back" does not drift. The
    /// number is not cosmetic — <see cref="Format"/> rounds to it, so a readout is honest about
    /// how much of the value it is showing.
    /// </summary>
    public const int DisplayDecimalPlaces = 9;

    private static readonly string DisplayPattern = "0." + new string('#', DisplayDecimalPlaces);

    /// <summary>
    /// The number of <paramref name="unit"/> this length measures, as text for a field: rounded to
    /// <see cref="DisplayDecimalPlaces"/> so 349.25000000000006 mm reads as <c>349.25</c>, not as
    /// the floating-point noise of the arithmetic that produced it. Culture-independent — a
    /// decimal point, never a decimal comma.
    /// </summary>
    public static string Format(Length value, LengthUnit unit)
        => value.To(unit).ToString(DisplayPattern, CultureInfo.InvariantCulture);

    /// <summary>
    /// The value with every bit of precision kept, so it parses back to exactly the same length.
    /// For saving and for tests, not for a field a person reads.
    /// </summary>
    public static string FormatExact(Length value, LengthUnit unit)
        => value.To(unit).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The value with its abbreviation, e.g. <c>349.25 mm</c>. Parses back exactly.</summary>
    public static string FormatWithUnit(Length value, LengthUnit unit)
        => Format(value, unit) + " " + unit.Abbreviation();

    /// <summary>Reads a length written in <paramref name="unit"/> — a number, or arithmetic.</summary>
    /// <exception cref="LengthExpressionException">The text is not a length.</exception>
    public static Length Parse(string? text, LengthUnit unit)
        => LengthExpression.Evaluate(text, unit);

    /// <summary>Reads a length, reporting the reason instead of throwing.</summary>
    public static bool TryParse(string? text, LengthUnit unit, out Length value, out string? error)
        => LengthExpression.TryEvaluate(text, unit, out value, out error);
}
