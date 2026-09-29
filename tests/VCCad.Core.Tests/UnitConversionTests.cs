using VCCad.Core.Units;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The unit model: factors stated against the inch, conversions that survive a round trip, and
/// arithmetic that converts before it adds.
///
/// Serialized with the other unit suites because <see cref="UnitSettings.Current"/> is shared
/// state owned by this feature.
/// </summary>
[Collection("unit-settings")]
public class UnitConversionTests
{
    [Fact]
    public void An_inch_is_exactly_twenty_five_point_four_millimetres()
    {
        Assert.Equal(25.4, Length.From(1, LengthUnit.Inches).Millimetres, 12);
    }

    [Fact]
    public void A_point_is_one_seventy_second_of_an_inch()
    {
        Assert.Equal(Length.From(1, LengthUnit.Inches).Millimetres, Length.From(72, LengthUnit.Points).Millimetres, 12);
    }

    [Fact]
    public void A_pica_is_six_to_the_inch()
    {
        Assert.Equal(Length.From(1, LengthUnit.Inches).Millimetres, Length.From(6, LengthUnit.Picas).Millimetres, 12);
    }

    [Fact]
    public void A_centimetre_is_ten_millimetres()
    {
        Assert.Equal(10.0, Length.From(1, LengthUnit.Centimetres).Millimetres, 12);
    }

    [Theory]
    [InlineData(LengthUnit.Millimetres)]
    [InlineData(LengthUnit.Centimetres)]
    [InlineData(LengthUnit.Inches)]
    [InlineData(LengthUnit.Points)]
    [InlineData(LengthUnit.Picas)]
    public void Every_unit_round_trips_back_to_the_number_that_was_typed(LengthUnit unit)
    {
        // 37.5 is exact in every one of these units' own scale.
        Length length = Length.From(37.5, unit);

        Assert.Equal(37.5, length.To(unit), 9);
    }

    [Theory]
    [InlineData(LengthUnit.Millimetres)]
    [InlineData(LengthUnit.Centimetres)]
    [InlineData(LengthUnit.Inches)]
    [InlineData(LengthUnit.Points)]
    [InlineData(LengthUnit.Picas)]
    public void A_length_survives_a_trip_out_to_another_unit_and_back(LengthUnit unit)
    {
        Length original = Length.From(123.456, LengthUnit.Millimetres);

        double there = original.To(unit);
        Length back = Length.From(there, unit);

        Assert.True(
            original.ApproximatelyEquals(back, 1e-9),
            $"round trip through {unit} drifted: {original.Millimetres} mm came back as {back.Millimetres} mm");
    }

    [Fact]
    public void Adding_lengths_written_in_different_units_adds_distances()
    {
        // 2 in is 50.8 mm. Adding the bare numbers would give 27.4; the distance is 76.2 mm.
        Length sum = Length.From(2, LengthUnit.Inches) + Length.From(25.4, LengthUnit.Millimetres);

        Assert.Equal(76.2, sum.Millimetres, 9);
        Assert.Equal(3.0, sum.To(LengthUnit.Inches), 9);
    }

    [Fact]
    public void Subtracting_lengths_written_in_different_units_subtracts_distances()
    {
        Length difference = Length.From(2, LengthUnit.Inches) - Length.From(25.4, LengthUnit.Millimetres);

        Assert.Equal(25.4, difference.Millimetres, 9);
    }

    [Fact]
    public void Scaling_a_length_by_a_number_keeps_it_a_length()
    {
        Length scaled = Length.From(5.5, LengthUnit.Inches) * 5 / 2;

        Assert.Equal(349.25, scaled.Millimetres, 9);
        Assert.Equal(13.75, scaled.To(LengthUnit.Inches), 9);
    }

    [Fact]
    public void A_ratio_of_two_lengths_is_a_plain_number()
    {
        double ratio = Length.From(4, LengthUnit.Inches) / Length.From(2, LengthUnit.Inches);

        Assert.Equal(2.0, ratio, 12);
    }

    [Fact]
    public void Points_and_millimetres_agree_with_the_definition_of_the_inch()
    {
        // The document stores points; a length read in points and read in millimetres must be the
        // same distance, or the display unit would change the drawing rather than its label.
        Length byPoints = Length.FromPoints(72);
        Length byInches = Length.From(1, LengthUnit.Inches);

        Assert.True(byPoints.ApproximatelyEquals(byInches, 1e-12));
    }
}
