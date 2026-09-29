using VCCad.Core.Units;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Formatting for display and reading it back: a value becomes text in whichever unit is
/// configured, that text parses back to the same value, and no readout hard-codes millimetres.
///
/// Serialized with the other unit suites because these tests flip
/// <see cref="UnitSettings.Current"/> to prove the readout follows it.
/// </summary>
[Collection("unit-settings")]
public class UnitFormattingTests
{
    [Fact]
    public void Format_writes_the_number_in_the_configured_unit()
    {
        var settings = new UnitSettings { Unit = LengthUnit.Inches };
        Length length = Length.From(5.5, LengthUnit.Inches);

        Assert.Equal("5.5", settings.Format(length));

        settings.Unit = LengthUnit.Millimetres;
        Assert.Equal("139.7", settings.Format(length));
    }

    [Fact]
    public void Format_with_unit_names_the_configured_unit()
    {
        var settings = new UnitSettings { Unit = LengthUnit.Millimetres };
        Length length = LengthExpression.Evaluate("5.5in * 5 / 2", LengthUnit.Millimetres);

        Assert.Equal("349.25 mm", settings.FormatWithUnit(length));

        settings.Unit = LengthUnit.Inches;
        Assert.Equal("13.75 in", settings.FormatWithUnit(length));

        settings.Unit = LengthUnit.Points;
        Assert.Equal("990 pt", settings.FormatWithUnit(length));
    }

    [Theory]
    [InlineData(LengthUnit.Millimetres)]
    [InlineData(LengthUnit.Centimetres)]
    [InlineData(LengthUnit.Inches)]
    [InlineData(LengthUnit.Points)]
    [InlineData(LengthUnit.Picas)]
    public void Text_formatted_in_a_unit_parses_back_to_the_same_value(LengthUnit unit)
    {
        var settings = new UnitSettings { Unit = unit };
        Length original = Length.FromMillimetres(123.456789123456);

        string text = settings.Format(original);
        Length parsed = settings.Parse(text);

        Assert.True(
            original.ApproximatelyEquals(parsed, 1e-6),
            $"{unit}: \"{text}\" parsed back to {parsed.Millimetres} mm, not {original.Millimetres} mm");
    }

    [Theory]
    [InlineData(LengthUnit.Millimetres)]
    [InlineData(LengthUnit.Centimetres)]
    [InlineData(LengthUnit.Inches)]
    [InlineData(LengthUnit.Points)]
    [InlineData(LengthUnit.Picas)]
    public void Exact_text_parses_back_to_the_very_same_length(LengthUnit unit)
    {
        var settings = new UnitSettings { Unit = unit };
        // A third of a millimetre keeps sub-nanometre digits the display is entitled to round
        // away, so this test fails the moment "exact" quietly becomes "as displayed".
        Length original = Length.FromMillimetres(1.0 / 3.0);

        string text = settings.FormatExact(original);
        Length parsed = settings.Parse(text);

        Assert.True(
            original.ApproximatelyEquals(parsed, 1e-12),
            $"{unit}: exact text \"{text}\" parsed back to {parsed.Millimetres} mm, not {original.Millimetres} mm");
    }

    [Theory]
    [InlineData(LengthUnit.Millimetres)]
    [InlineData(LengthUnit.Centimetres)]
    [InlineData(LengthUnit.Inches)]
    [InlineData(LengthUnit.Points)]
    [InlineData(LengthUnit.Picas)]
    public void Text_with_the_unit_suffix_parses_back_to_the_same_value(LengthUnit unit)
    {
        var settings = new UnitSettings { Unit = unit };
        Length original = Length.From(37.5, unit);

        string text = settings.FormatWithUnit(original);
        Length parsed = settings.Parse(text);

        Assert.True(
            original.ApproximatelyEquals(parsed, 1e-6),
            $"{unit}: \"{text}\" parsed back to {parsed.Millimetres} mm, not {original.Millimetres} mm");
    }

    [Fact]
    public void The_readout_is_in_the_configured_unit_not_millimetres()
    {
        // Two identical lengths read out differently purely because the configured unit moved.
        // A readout that hard-coded millimetres would print "139.7" for both.
        Length length = Length.From(5.5, LengthUnit.Inches);

        var settings = new UnitSettings { Unit = LengthUnit.Millimetres };
        Assert.Equal("139.7", settings.Format(length));

        settings.Unit = LengthUnit.Inches;
        Assert.Equal("5.5", settings.Format(length));

        settings.Unit = LengthUnit.Centimetres;
        Assert.Equal("13.97", settings.Format(length));
    }

    [Fact]
    public void The_configured_unit_drives_both_the_readout_and_a_bare_number_entry()
    {
        var settings = new UnitSettings { Unit = LengthUnit.Inches };

        Assert.Equal("2", settings.Format(Length.From(2, LengthUnit.Inches)));
        Assert.Equal(50.8, settings.Parse("2").Millimetres, 9);

        settings.Unit = LengthUnit.Millimetres;

        Assert.Equal("50.8", settings.Format(Length.From(2, LengthUnit.Inches)));
        Assert.Equal(2.0, settings.Parse("2").Millimetres, 9);
    }

    [Fact]
    public void Length_to_string_consults_the_configured_unit()
    {
        UnitSettings.Current.Unit = LengthUnit.Inches;
        try
        {
            Assert.Equal("2 in", Length.From(2, LengthUnit.Inches).ToString());

            UnitSettings.Current.Unit = LengthUnit.Millimetres;
            Assert.Equal("50.8 mm", Length.From(2, LengthUnit.Inches).ToString());
        }
        finally
        {
            UnitSettings.Current.Unit = LengthUnit.Millimetres;
        }
    }

    [Fact]
    public void Very_small_values_still_produce_text_that_parses_back()
    {
        var settings = new UnitSettings { Unit = LengthUnit.Millimetres };
        Length tiny = Length.FromMillimetres(1e-5);

        string text = settings.Format(tiny);

        Assert.True(settings.TryParse(text, out Length parsed, out string? error), $"\"{text}\" did not parse: {error}");
        Assert.True(tiny.ApproximatelyEquals(parsed, 1e-15), $"\"{text}\" parsed back to {parsed.Millimetres}");
    }

    [Fact]
    public void TryParse_reports_malformed_text_instead_of_returning_zero()
    {
        var settings = new UnitSettings { Unit = LengthUnit.Millimetres };

        Assert.False(settings.TryParse("5.5in * / 2", out Length value, out string? error));
        Assert.Equal(default, value);
        Assert.NotNull(error);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void TryParse_accepts_arithmetic_and_spelled_units()
    {
        var settings = new UnitSettings { Unit = LengthUnit.Millimetres };

        Assert.True(settings.TryParse("2in + 25.4mm", out Length value, out string? error), error);
        Assert.Equal(76.2, value.Millimetres, 9);
    }
}
