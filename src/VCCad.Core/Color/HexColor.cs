using System.Globalization;
using VCCad.Core.Model;

namespace VCCad.Core.Color;

/// <summary>
/// Hex RGB parsing and formatting for the picker's hex field.
///
/// Accepts the four CSS-ish spellings the panel must cope with — <c>#RGB</c>,
/// <c>#RGBA</c>, <c>#RRGGBB</c> and <c>#RRGGBBAA</c> — with or without the
/// leading <c>#</c>, in any case, and with surrounding whitespace. Formatting is
/// the inverse for every colour that originated as bytes: typing a hex, reading
/// it back and re-rendering it must yield the identical string.
/// </summary>
public static class HexColor
{
    /// <summary>
    /// Formats as <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when
    /// <paramref name="includeAlpha"/> is set. Uppercase, always leading <c>#</c>.
    /// </summary>
    public static string Format(ColorRgb color, bool includeAlpha = false)
    {
        ColorRgb c = color.Clamped();
        string hex = string.Create(
            includeAlpha ? 9 : 7,
            (c, includeAlpha),
            static (span, state) =>
            {
                span[0] = '#';
                WriteByte(span.Slice(1, 2), state.c.R);
                WriteByte(span.Slice(3, 2), state.c.G);
                WriteByte(span.Slice(5, 2), state.c.B);
                if (state.includeAlpha)
                {
                    WriteByte(span.Slice(7, 2), state.c.A);
                }
            });

        return hex;
    }

    /// <summary>Formats as <c>#RRGGBBAA</c>, including the alpha channel.</summary>
    public static string FormatWithAlpha(ColorRgb color) => Format(color, includeAlpha: true);

    /// <summary>
    /// Parses a hex colour. Returns false for null/blank, a bad length, or any
    /// non-hexadecimal digit; never throws.
    /// </summary>
    public static bool TryParse(string? text, out ColorRgb color)
    {
        color = ColorRgb.Black;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length is not (3 or 4 or 6 or 8))
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[4] { 255, 255, 255, 255 };
        if (span.Length is 3 or 4)
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (!TryParseNibble(span[i], out int nibble))
                {
                    return false;
                }

                // Shorthand duplicates the nibble: #f80 → #ff8800.
                channels[i] = (byte)((nibble << 4) | nibble);
            }
        }
        else
        {
            for (int i = 0; i < span.Length / 2; i++)
            {
                if (!TryParseByte(span.Slice(i * 2, 2), out channels[i]))
                {
                    return false;
                }
            }
        }

        color = ColorRgb.FromBytes(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }

    /// <summary>
    /// Parses a hex colour or throws <see cref="FormatException"/> (the editor
    /// path: a person typed it, and they deserve an error rather than black).
    /// </summary>
    public static ColorRgb Parse(string text)
        => TryParse(text, out ColorRgb color)
            ? color
            : throw new FormatException($"'{text}' is not a hex RGB colour.");

    private static void WriteByte(Span<char> destination, double unit)
        => ColorConversion.ByteFromUnit(unit).TryFormat(destination, out _, "X2", CultureInfo.InvariantCulture);

    private static bool TryParseNibble(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }

    private static bool TryParseByte(ReadOnlySpan<char> pair, out byte value)
    {
        value = 0;
        if (!TryParseNibble(pair[0], out int high) || !TryParseNibble(pair[1], out int low))
        {
            return false;
        }

        value = (byte)((high << 4) | low);
        return true;
    }
}
