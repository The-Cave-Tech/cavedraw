namespace VCCad.Core.Units;

/// <summary>
/// A length unit a person can configure, and type inside a field expression.
/// </summary>
public enum LengthUnit
{
    /// <summary>Millimetres. The initial configured unit, not a privileged one.</summary>
    Millimetres,

    /// <summary>Centimetres.</summary>
    Centimetres,

    /// <summary>Inches.</summary>
    Inches,

    /// <summary>PostScript / PDF points (1/72 inch) — the document's native unit.</summary>
    Points,

    /// <summary>Picas (1/6 inch).</summary>
    Picas,
}

/// <summary>
/// The exact factors behind <see cref="LengthUnit"/>, and the abbreviations a person types.
///
/// Every factor is stated against the inch: 25.4 mm by the definition of the inch, 72 pt by
/// PostScript, 6 picas typographically. Nothing is derived from a rounded millimetre figure,
/// so a conversion chain cannot accumulate drift.
/// </summary>
public static class LengthUnits
{
    /// <summary>Millimetres per inch — the exact definition of the inch.</summary>
    public const double MillimetresPerInch = 25.4;

    /// <summary>Points per inch — the PostScript/PDF definition.</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>Picas per inch — the typographic definition.</summary>
    public const double PicasPerInch = 6.0;

    /// <summary>Every unit, in the order a unit picker should offer them.</summary>
    public static readonly IReadOnlyList<LengthUnit> All = new[]
    {
        LengthUnit.Millimetres,
        LengthUnit.Centimetres,
        LengthUnit.Inches,
        LengthUnit.Points,
        LengthUnit.Picas,
    };

    /// <summary>The number of millimetres in one <paramref name="unit"/>.</summary>
    public static double MillimetresPer(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimetres => 1.0,
        LengthUnit.Centimetres => 10.0,
        LengthUnit.Inches => MillimetresPerInch,
        LengthUnit.Points => MillimetresPerInch / PointsPerInch,
        LengthUnit.Picas => MillimetresPerInch / PicasPerInch,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <summary>The short form a person types and reads, e.g. "mm" or "in".</summary>
    public static string Abbreviation(this LengthUnit unit) => unit switch
    {
        LengthUnit.Millimetres => "mm",
        LengthUnit.Centimetres => "cm",
        LengthUnit.Inches => "in",
        LengthUnit.Points => "pt",
        LengthUnit.Picas => "pc",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown length unit."),
    };

    /// <summary>
    /// Reads a unit written the way a person writes it: the abbreviation, case-insensitively,
    /// or the spelled-out name in singular or plural.
    /// </summary>
    public static bool TryParse(string? text, out LengthUnit unit)
    {
        unit = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "mm":
            case "millimetre":
            case "millimetres":
            case "millimeter":
            case "millimeters":
                unit = LengthUnit.Millimetres;
                return true;
            case "cm":
            case "centimetre":
            case "centimetres":
            case "centimeter":
            case "centimeters":
                unit = LengthUnit.Centimetres;
                return true;
            case "in":
            case "inch":
            case "inches":
                unit = LengthUnit.Inches;
                return true;
            case "pt":
            case "point":
            case "points":
                unit = LengthUnit.Points;
                return true;
            case "pc":
            case "pica":
            case "picas":
                unit = LengthUnit.Picas;
                return true;
            default:
                return false;
        }
    }
}
