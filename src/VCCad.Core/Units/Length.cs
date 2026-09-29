namespace VCCad.Core.Units;

/// <summary>
/// A length: a number <em>with</em> a unit, stored canonically in millimetres.
///
/// The unit is part of the value, not decoration on its way to the text box. Adding two
/// lengths written in different units converts them first, so <c>2in + 25.4mm</c> is one
/// length of 76.2 mm rather than the number 27.4. Whether it is displayed in millimetres or
/// inches is decided at the edge, by <see cref="LengthFormatter"/>, never while computing.
/// </summary>
public readonly struct Length : IEquatable<Length>, IComparable<Length>
{
    private readonly double _millimetres;

    private Length(double millimetres) => _millimetres = millimetres;

    /// <summary>A zero length.</summary>
    public static Length Zero => default;

    /// <summary>The value in the canonical unit, millimetres.</summary>
    public double Millimetres => _millimetres;

    /// <summary>The value in inches.</summary>
    public double Inches => To(LengthUnit.Inches);

    /// <summary>The value in points (1/72 inch).</summary>
    public double Points => To(LengthUnit.Points);

    /// <summary>Builds a length from a number and the unit that number is written in.</summary>
    public static Length From(double value, LengthUnit unit)
        => new(value * LengthUnits.MillimetresPer(unit));

    /// <summary>Builds a length from a value already expressed in millimetres.</summary>
    public static Length FromMillimetres(double millimetres) => new(millimetres);

    /// <summary>Builds a length from a value already expressed in points.</summary>
    public static Length FromPoints(double points) => From(points, LengthUnit.Points);

    /// <summary>The number of <paramref name="unit"/> this length measures.</summary>
    public double To(LengthUnit unit) => _millimetres / LengthUnits.MillimetresPer(unit);

    /// <summary>Adds two lengths, whatever units they were written in.</summary>
    public static Length operator +(Length a, Length b) => new(a._millimetres + b._millimetres);

    /// <summary>Subtracts one length from another, whatever units they were written in.</summary>
    public static Length operator -(Length a, Length b) => new(a._millimetres - b._millimetres);

    /// <summary>Reverses a length.</summary>
    public static Length operator -(Length value) => new(-value._millimetres);

    /// <summary>Scales a length by a plain number.</summary>
    public static Length operator *(Length length, double factor) => new(length._millimetres * factor);

    /// <summary>Scales a length by a plain number.</summary>
    public static Length operator *(double factor, Length length) => new(length._millimetres * factor);

    /// <summary>Divides a length by a plain number.</summary>
    public static Length operator /(Length length, double divisor) => new(length._millimetres / divisor);

    /// <summary>The ratio of two lengths — a plain number, since the units cancel.</summary>
    public static double operator /(Length a, Length b) => a._millimetres / b._millimetres;

    /// <summary>Two lengths are equal when they measure the same distance.</summary>
    public bool Equals(Length other) => _millimetres.Equals(other._millimetres);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Length other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _millimetres.GetHashCode();

    /// <summary>Orders lengths by the distance they measure.</summary>
    public int CompareTo(Length other) => _millimetres.CompareTo(other._millimetres);

    /// <summary>Equality in the sense a person means it: the same distance.</summary>
    public static bool operator ==(Length a, Length b) => a.Equals(b);

    /// <summary>Inequality in the sense a person means it: a different distance.</summary>
    public static bool operator !=(Length a, Length b) => !a.Equals(b);

    /// <summary>
    /// Closeness inside a tolerance, in millimetres. Conversion divides and multiplies by a
    /// floating-point factor, so a round trip through another unit lands within a rounding
    /// step of where it started rather than exactly on it.
    /// </summary>
    public bool ApproximatelyEquals(Length other, double toleranceMillimetres = 1e-9)
        => Math.Abs(_millimetres - other._millimetres) <= toleranceMillimetres;

    /// <summary>
    /// The length written in whatever unit is currently configured — consulting
    /// <see cref="UnitSettings.Current"/> rather than assuming millimetres.
    /// </summary>
    public override string ToString()
        => LengthFormatter.FormatWithUnit(this, UnitSettings.Current.Unit);
}
