using VCCad.Core.Units;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Filed-entry arithmetic. The unit is part of the value: <c>5.5in * 5 / 2</c> is a length of
/// 349.25 mm, and mixing units adds distances rather than bare numbers. Anything that cannot be
/// evaluated is refused with a reason, never turned into zero.
///
/// Serialized with the other unit suites because <see cref="UnitSettings.Current"/> is shared
/// state owned by this feature.
/// </summary>
[Collection("unit-settings")]
public class UnitExpressionTests
{
    [Fact]
    public void Inches_times_a_number_times_a_number_yields_a_length_not_a_bare_number()
    {
        // The brief's example. Stripping the unit gives the number 13.75; the answer is the
        // length 349.25 mm, which is also 13.75 in.
        Length result = LengthExpression.Evaluate("5.5in * 5 / 2", LengthUnit.Millimetres);

        Assert.Equal(349.25, result.Millimetres, 9);
        Assert.Equal(13.75, result.To(LengthUnit.Inches), 9);
    }

    [Fact]
    public void Multiplying_two_lengths_is_refused_because_the_result_is_an_area()
    {
        Assert.False(LengthExpression.TryEvaluate("2in * 3in", LengthUnit.Millimetres, out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("area", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adding_lengths_written_in_different_units_adds_distances()
    {
        // 2 in is 50.8 mm, so the answer is 76.2 mm. Adding the bare numbers would be 27.4.
        Length result = LengthExpression.Evaluate("2in + 25.4mm", LengthUnit.Millimetres);

        Assert.Equal(76.2, result.Millimetres, 9);
    }

    [Fact]
    public void A_bare_number_means_that_many_of_the_configured_unit()
    {
        Assert.Equal(5.0, LengthExpression.Evaluate("5", LengthUnit.Millimetres).Millimetres, 9);
        Assert.Equal(127.0, LengthExpression.Evaluate("5", LengthUnit.Inches).Millimetres, 9);
        Assert.Equal(50.0, LengthExpression.Evaluate("5", LengthUnit.Centimetres).Millimetres, 9);
        Assert.Equal(5 * 25.4 / 72, LengthExpression.Evaluate("5", LengthUnit.Points).Millimetres, 9);
    }

    [Fact]
    public void Bare_number_arithmetic_is_a_scalar_shaping_the_configured_unit()
    {
        // 5/2 in inches is 63.5 mm — the division happens in the configured unit, not in mm.
        Assert.Equal(63.5, LengthExpression.Evaluate("5/2", LengthUnit.Inches).Millimetres, 9);
        Assert.Equal(2.5, LengthExpression.Evaluate("5/2", LengthUnit.Millimetres).Millimetres, 9);
    }

    [Fact]
    public void Multiplication_and_division_bind_tighter_than_addition()
    {
        // 1 in + 2 x 3 in = 7 in = 177.8 mm.
        Length result = LengthExpression.Evaluate("1in + 2 * 3in", LengthUnit.Millimetres);

        Assert.Equal(177.8, result.Millimetres, 9);
    }

    [Fact]
    public void Parentheses_override_precedence()
    {
        // (2 + 3) x 4 in = 20 in = 508 mm.
        Length result = LengthExpression.Evaluate("(2 + 3) * 4in", LengthUnit.Millimetres);

        Assert.Equal(508.0, result.Millimetres, 9);
    }

    [Fact]
    public void Nested_parentheses_evaluate()
    {
        Length result = LengthExpression.Evaluate("((5.5in * 5) / 2)", LengthUnit.Millimetres);

        Assert.Equal(349.25, result.Millimetres, 9);
    }

    [Fact]
    public void Unary_minus_negates_a_length()
    {
        Assert.Equal(25.4, LengthExpression.Evaluate("-5in + 6in", LengthUnit.Millimetres).Millimetres, 9);
        Assert.Equal(50.8, LengthExpression.Evaluate("-(-2in)", LengthUnit.Millimetres).Millimetres, 9);
        Assert.Equal(-25.4, LengthExpression.Evaluate("-1in", LengthUnit.Millimetres).Millimetres, 9);
    }

    [Fact]
    public void A_ratio_of_two_lengths_is_a_plain_number_read_in_the_configured_unit()
    {
        // The units cancel, so the quotient is a scalar; a scalar field entry means "this many of
        // the configured unit". With inches configured, 4in / 2in = 2in = 50.8 mm — not a bare
        // 2 mm, which is what treating the quotient as a length would give.
        Assert.Equal(50.8, LengthExpression.Evaluate("4in / 2in", LengthUnit.Inches).Millimetres, 9);
    }

    [Fact]
    public void A_length_divided_by_a_number_stays_a_length()
    {
        Assert.Equal(12.7, LengthExpression.Evaluate("1in / 2", LengthUnit.Millimetres).Millimetres, 9);
    }

    [Fact]
    public void Units_are_read_case_insensitively_and_spelled_out_forms_are_accepted()
    {
        Assert.Equal(25.4, LengthExpression.Evaluate("1IN", LengthUnit.Millimetres).Millimetres, 9);
        Assert.Equal(25.4, LengthExpression.Evaluate("1 inches", LengthUnit.Millimetres).Millimetres, 9);
        Assert.Equal(10.0, LengthExpression.Evaluate("10 mm", LengthUnit.Millimetres).Millimetres, 9);
    }

    [Theory]
    [InlineData("5.5in * / 2", "'/'")]
    [InlineData("((3", "unbalanced parenthesis")]
    [InlineData("abc", "'abc'")]
    [InlineData("5.5qq", "unknown unit 'qq'")]
    [InlineData("", "the expression is empty")]
    [InlineData("   ", "the expression is empty")]
    [InlineData("1/0", "division by zero")]
    [InlineData("1in / 0", "division by zero")]
    [InlineData("2in + 3", "unitless")]
    [InlineData("2in - 3", "unitless")]
    [InlineData("5in 5", "unexpected '5'")]
    [InlineData("-", "expected a number")]
    [InlineData("(1 + 2", "unbalanced parenthesis")]
    [InlineData("1 + * 2", "'*'")]
    [InlineData("3 / 4in", "cannot divide")]
    public void A_malformed_expression_is_reported_with_a_reason(string text, string fragment)
    {
        bool ok = LengthExpression.TryEvaluate(text, LengthUnit.Millimetres, out Length value, out string? error);

        Assert.False(ok, $"\"{text}\" was accepted as {value.Millimetres} mm instead of being refused");
        Assert.NotNull(error);
        Assert.Contains(fragment, error, StringComparison.OrdinalIgnoreCase);

        LengthExpressionException thrown = Assert.Throws<LengthExpressionException>(
            () => LengthExpression.Evaluate(text, LengthUnit.Millimetres));
        Assert.Contains(fragment, thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_four_defects_from_the_brief_each_refuse_rather_than_become_zero()
    {
        string[] malformed = { "5.5in * / 2", "((3", "abc", "5.5qq" };

        foreach (string text in malformed)
        {
            bool ok = LengthExpression.TryEvaluate(text, LengthUnit.Millimetres, out Length value, out string? error);

            Assert.False(ok, $"\"{text}\" was accepted as {value.Millimetres} mm");
            Assert.False(string.IsNullOrWhiteSpace(error), $"\"{text}\" failed without a reason");
        }
    }

    [Fact]
    public void The_reason_names_the_position_of_the_offending_text()
    {
        LengthExpressionException thrown = Assert.Throws<LengthExpressionException>(
            () => LengthExpression.Evaluate("5.5qq", LengthUnit.Millimetres));

        Assert.Equal(3, thrown.Position);
        Assert.Equal("unknown unit 'qq'", thrown.Reason);
    }

    [Fact]
    public void A_division_by_zero_is_reported_at_the_operator()
    {
        LengthExpressionException thrown = Assert.Throws<LengthExpressionException>(
            () => LengthExpression.Evaluate("1/0", LengthUnit.Millimetres));

        Assert.Equal(1, thrown.Position);
        Assert.Contains("zero", thrown.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scientific_notation_parses_as_a_number()
    {
        Assert.Equal(2000.0, LengthExpression.Evaluate("2e3", LengthUnit.Millimetres).Millimetres, 9);
        Assert.Equal(0.00001, LengthExpression.Evaluate("1E-05", LengthUnit.Millimetres).Millimetres, 12);
    }
}
