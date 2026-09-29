using System.Globalization;

namespace VCCad.Core.Units;

/// <summary>
/// A field entry that is not a length, and the reason it is not — with the position in the
/// text where the trouble was found.
///
/// Entry is refused rather than coerced. A field that quietly becomes 0 because someone typed
/// <c>5.5in * / 2</c> loses the drawing; a field that says what was wrong does not.
/// </summary>
public sealed class LengthExpressionException : Exception
{
    /// <summary>Creates the failure from a reason and a zero-based position in the text.</summary>
    public LengthExpressionException(string reason, int position)
        : base(position >= 0 ? $"position {position}: {reason}" : reason)
    {
        Reason = reason;
        Position = position;
    }

    /// <summary>What was wrong, without the position prefix.</summary>
    public string Reason { get; }

    /// <summary>Zero-based position in the typed text, or -1 when it does not apply.</summary>
    public int Position { get; }
}

/// <summary>
/// Evaluates a length typed into a numeric field: numbers, <c>+ - * /</c>, parentheses, unary
/// signs, and units written inside the expression.
///
/// The unit is part of the value. <c>5.5in * 5 / 2</c> is 13.75 inches — 349.25 mm — not the bare
/// number 13.75. A number with a unit becomes a length; a bare number is a plain scalar, which is
/// what makes <c>* 5 / 2</c> a scaling rather than an attempt to multiply two lengths. A scalar
/// result is read as "that many of the configured unit", so typing <c>5</c> into a field whose
/// unit is inches means five inches.
///
/// Multiplication and addition check dimensions instead of assuming: <c>2in + 25.4mm</c> adds two
/// lengths, <c>2in * 3in</c> is refused as an area, and adding a bare number to a length is
/// refused because guessing which unit the number meant is how wrong drawings happen.
/// </summary>
public static class LengthExpression
{
    /// <summary>Evaluates <paramref name="text"/> as a length in <paramref name="unit"/>.</summary>
    /// <exception cref="LengthExpressionException">The text is not a length.</exception>
    public static Length Evaluate(string? text, LengthUnit unit)
    {
        List<Token> tokens = Tokenize(text ?? string.Empty);
        if (tokens.Count == 1)
            throw new LengthExpressionException("the expression is empty", 0);

        var parser = new Parser(tokens);
        Quantity quantity = parser.ParseExpression();
        parser.ExpectEnd();
        return quantity.AsLength(unit);
    }

    /// <summary>
    /// Evaluates <paramref name="text"/>, reporting the reason instead of throwing.
    /// </summary>
    /// <returns><c>true</c> when a length was produced.</returns>
    public static bool TryEvaluate(string? text, LengthUnit unit, out Length value, out string? error)
    {
        try
        {
            value = Evaluate(text, unit);
            error = null;
            return true;
        }
        catch (LengthExpressionException ex)
        {
            value = default;
            error = ex.Message;
            return false;
        }
    }

    private enum TokenKind
    {
        Number,
        Identifier,
        Plus,
        Minus,
        Star,
        Slash,
        LeftParen,
        RightParen,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, double Number, int Position);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'))
                    i++;

                if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                {
                    int after = i + 1;
                    if (after < text.Length && (text[after] == '+' || text[after] == '-'))
                        after++;
                    if (after < text.Length && char.IsAsciiDigit(text[after]))
                    {
                        i = after;
                        while (i < text.Length && char.IsAsciiDigit(text[i]))
                            i++;
                    }
                }

                string raw = text[start..i];
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    throw new LengthExpressionException($"'{raw}' is not a number", start);
                tokens.Add(new Token(TokenKind.Number, raw, number, start));
                continue;
            }

            if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
                tokens.Add(new Token(TokenKind.Identifier, text[start..i], 0, start));
                continue;
            }

            TokenKind kind = c switch
            {
                '+' => TokenKind.Plus,
                '-' => TokenKind.Minus,
                '*' => TokenKind.Star,
                '/' => TokenKind.Slash,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                _ => throw new LengthExpressionException($"unexpected character '{c}'", i),
            };
            tokens.Add(new Token(kind, c.ToString(), 0, i));
            i++;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, 0, text.Length));
        return tokens;
    }

    /// <summary>A value being computed: either a length (stored in mm) or a plain scalar.</summary>
    private readonly struct Quantity
    {
        private Quantity(double value, bool isLength)
        {
            Value = value;
            IsLength = isLength;
        }

        public double Value { get; }

        public bool IsLength { get; }

        public static Quantity Scalar(double value) => new(value, false);

        public static Quantity AsLength(double millimetres) => new(millimetres, true);

        /// <summary>A scalar means "this many of the configured unit".</summary>
        public Length AsLength(LengthUnit unit)
            => IsLength ? Length.FromMillimetres(Value) : Length.From(Value, unit);
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _index;

        public Parser(List<Token> tokens) => _tokens = tokens;

        private Token Current => _tokens[_index];

        public Quantity ParseExpression()
        {
            Quantity left = ParseTerm();
            while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                Token op = Current;
                _index++;
                Quantity right = ParseTerm();
                left = Add(left, right, op);
            }

            return left;
        }

        public void ExpectEnd()
        {
            if (Current.Kind != TokenKind.End)
                throw new LengthExpressionException($"unexpected '{Describe(Current)}'", Current.Position);
        }

        private Quantity ParseTerm()
        {
            Quantity left = ParseUnary();
            while (Current.Kind is TokenKind.Star or TokenKind.Slash)
            {
                Token op = Current;
                _index++;
                Quantity right = ParseUnary();
                left = op.Kind == TokenKind.Star
                    ? Multiply(left, right, op.Position)
                    : Divide(left, right, op.Position);
            }

            return left;
        }

        private Quantity ParseUnary()
        {
            if (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                Token op = Current;
                _index++;
                Quantity operand = ParseUnary();
                if (op.Kind == TokenKind.Plus)
                    return operand;
                return operand.IsLength
                    ? Quantity.AsLength(-operand.Value)
                    : Quantity.Scalar(-operand.Value);
            }

            return ParsePrimary();
        }

        private Quantity ParsePrimary()
        {
            Token token = Current;

            if (token.Kind == TokenKind.Number)
            {
                _index++;
                if (Current.Kind == TokenKind.Identifier)
                {
                    Token unitToken = Current;
                    if (!LengthUnits.TryParse(unitToken.Text, out LengthUnit unit))
                        throw new LengthExpressionException($"unknown unit '{unitToken.Text}'", unitToken.Position);
                    _index++;
                    return Quantity.AsLength(Length.From(token.Number, unit).Millimetres);
                }

                return Quantity.Scalar(token.Number);
            }

            if (token.Kind == TokenKind.LeftParen)
            {
                int openPosition = token.Position;
                _index++;
                Quantity inner = ParseExpression();
                if (Current.Kind != TokenKind.RightParen)
                {
                    throw new LengthExpressionException(
                        $"unbalanced parenthesis: expected ')' to close the '(' at position {openPosition}",
                        Current.Position);
                }

                _index++;
                return inner;
            }

            throw new LengthExpressionException(
                $"expected a number or '(' but found '{Describe(token)}'", token.Position);
        }

        private static Quantity Add(Quantity left, Quantity right, Token op)
        {
            if (left.IsLength != right.IsLength)
            {
                string verb = op.Kind == TokenKind.Plus ? "add" : "subtract";
                throw new LengthExpressionException(
                    $"cannot {verb} a length and a unitless number; give the number a unit", op.Position);
            }

            double sum = op.Kind == TokenKind.Plus ? left.Value + right.Value : left.Value - right.Value;
            return left.IsLength ? Quantity.AsLength(sum) : Quantity.Scalar(sum);
        }

        private static Quantity Multiply(Quantity left, Quantity right, int position)
        {
            if (left.IsLength && right.IsLength)
                throw new LengthExpressionException("cannot multiply two lengths; that would be an area", position);

            double product = left.Value * right.Value;
            return left.IsLength || right.IsLength ? Quantity.AsLength(product) : Quantity.Scalar(product);
        }

        private static Quantity Divide(Quantity left, Quantity right, int position)
        {
            if (right.Value == 0)
                throw new LengthExpressionException("division by zero", position);

            if (left.IsLength && right.IsLength)
                return Quantity.Scalar(left.Value / right.Value);
            if (left.IsLength)
                return Quantity.AsLength(left.Value / right.Value);
            if (right.IsLength)
                throw new LengthExpressionException("cannot divide a number by a length", position);

            return Quantity.Scalar(left.Value / right.Value);
        }

        private static string Describe(Token token) => token.Kind switch
        {
            TokenKind.End => "end of expression",
            TokenKind.Number or TokenKind.Identifier => token.Text,
            _ => token.Text,
        };
    }
}
