namespace VCCad.Core.Units;

/// <summary>
/// The configured unit every readout consults.
///
/// This is the setting a Transform panel flips when the person chooses inches; a field's readout
/// and its entry parse both go through these members, so the displayed unit and the assumed unit
/// for a bare number can never disagree. <see cref="Current"/> is the application-wide instance;
/// hands that want their own can construct one.
/// </summary>
public sealed class UnitSettings
{
    private static UnitSettings _current = new();

    /// <summary>
    /// The application-wide configured unit. Assigning a fresh instance is how a unit switch is
    /// published to every readout.
    /// </summary>
    public static UnitSettings Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The unit readouts are shown in and bare numbers are read as.</summary>
    public LengthUnit Unit { get; set; } = LengthUnit.Millimetres;

    /// <summary>This length as text in the configured unit.</summary>
    public string Format(Length value) => LengthFormatter.Format(value, Unit);

    /// <summary>This length as text with its abbreviation in the configured unit.</summary>
    public string FormatWithUnit(Length value) => LengthFormatter.FormatWithUnit(value, Unit);

    /// <summary>This length as text keeping every bit of precision, for saving rather than reading.</summary>
    public string FormatExact(Length value) => LengthFormatter.FormatExact(value, Unit);

    /// <summary>Reads field entry as a length, with the configured unit as the assumed one.</summary>
    public Length Parse(string? text) => LengthFormatter.Parse(text, Unit);

    /// <summary>Reads field entry, reporting the reason instead of throwing.</summary>
    public bool TryParse(string? text, out Length value, out string? error)
        => LengthFormatter.TryParse(text, Unit, out value, out error);
}
