namespace VCCad.Core.Model;

/// <summary>
/// Measurement support for the document model.
///
/// VCCad stores every coordinate internally in PDF points (1/72 inch). Points are
/// the native unit of PDF, so "export without rounding" is guaranteed by
/// construction (ADR-10). This file only converts at the *boundary* — when the
/// user types millimetres into the UI, or the model fabricates an A4 artboard.
/// </summary>
public static class Measurement
{
    /// <summary>Points per inch — the PostScript/PDF definition.</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>Millimetres per inch (exact definition of the inch).</summary>
    public const double MillimetresPerInch = 25.4;

    /// <summary>Scaling from millimetres to points.</summary>
    public static double MmToPoints(double mm) => mm * PointsPerInch / MillimetresPerInch;

    /// <summary>Scaling from points to millimetres.</summary>
    public static double PointsToMm(double pt) => pt * MillimetresPerInch / PointsPerInch;
}

/// <summary>
/// Well-known page formats expressed in points, ready to size artboards.
/// A4 landscape is the VCCad default per the product brief.
/// </summary>
public static class PageSizes
{
    /// <summary>ISO A4 portrait, 210 × 297 mm → 595.28 × 841.89 pt.</summary>
    public static readonly Geometry.Size2D A4Portrait =
        new(Measurement.MmToPoints(210.0), Measurement.MmToPoints(297.0));

    /// <summary>ISO A4 landscape, 297 × 210 mm → 841.89 × 595.28 pt.</summary>
    public static readonly Geometry.Size2D A4Landscape =
        new(Measurement.MmToPoints(297.0), Measurement.MmToPoints(210.0));

    /// <summary>US Letter portrait, 8.5 × 11 in → 612 × 792 pt.</summary>
    public static readonly Geometry.Size2D LetterPortrait =
        new(8.5 * Measurement.PointsPerInch, 11.0 * Measurement.PointsPerInch);
}
