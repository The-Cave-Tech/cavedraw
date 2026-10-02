namespace VCCad.Core.Text;

/// <summary>
/// The Unicode bidirectional algorithm, enough of it to lay a text block out.
///
/// **Why it is here at all.** The visual order of a string is not its logical order. A block whose base direction
/// is right to left runs from the right edge back towards the origin, and a Latin word inside it keeps its own
/// direction, so "abc \u05d0\u05d1\u05d2" is *drawn* "\u05d0\u05d1\u05d2 abc". Drawing the characters in the order the file wrote them
/// produces plausible-looking, wrong text, and every downstream consumer - the painter, the caret, the selection,
/// the SVG writer - has to agree about the order or the document is edited in one order and drawn in another.
///
/// **What it implements** is UAX #9's implicit part: the paragraph level from the first strong character (P2/P3),
/// the weak types (W1-W7), the neutral types (N1-N2) and the implicit levels (I1-I2), over one block of text. The
/// explicit embedding, override and isolate codes (X, and the LRI/RLI/FSI/PDI family) are **treated as neutral**:
/// they are invisible formatting characters that no SVG text element this reader accepts carries, and a file that
/// does carry one is a file whose layout this reader cannot honour. Treating them as a nested level would be a
/// guess dressed as an implementation, so they resolve to the surrounding direction and draw nothing.
///
/// **The order it returns is a permutation, never a rewrite.** Indices into the original string are handed back in
/// the order the pen reaches them, so a run keeps its characters, its raw codes and its glyph ids - which is the
/// rule <see cref="Model.TextItem.MirrorX"/> follows for the same reason.
///
/// The classes are a codepoint range table rather than a call into a framework API, because <c>VCCad.Core</c> has
/// to work headless and the framework's bidi is a rendering concern. Character ranges below ASCII, Hebrew, Arabic
/// and the CJK blocks are left as <see cref="BidiClass.L"/>: a character this table does not know is a character
/// the algorithm would treat as left-to-right, which is what the default class is.
/// </summary>
public static class Bidi
{
    /// <summary>One block of text in visual order, with the levels that produced it.</summary>
    /// <param name="Order">Indices into the original string, in the order the pen reaches them.</param>
    /// <param name="Levels">Each character's resolved embedding level, in **logical** order.</param>
    /// <param name="BaseLevel">0 for a left-to-right block, 1 for a right-to-left one.</param>
    public readonly record struct BidiResult(int[] Order, byte[] Levels, int BaseLevel)
    {
        /// <summary>Whether the block runs right to left.</summary>
        public bool IsRightToLeft => BaseLevel % 2 == 1;
    }

    /// <summary>The Unicode bidirectional character types this algorithm distinguishes.</summary>
    private enum BidiClass
    {
        L,
        R,
        Al,
        En,
        Es,
        Et,
        An,
        Cs,
        Nsm,
        Bn,
        B,
        S,
        Ws,
        On,
    }

    /// <summary>
    /// Orders <paramref name="text"/> into visual order, or the identity when the block is left to right and no
    /// character inside it runs the other way.
    ///
    /// <paramref name="baseDirection"/> is the paragraph level: 0 for left to right, 1 for right to left - what SVG's
    /// <c>direction</c> states and what <see cref="ParagraphDirection"/> answers when a file says nothing.
    /// </summary>
    public static BidiResult Order(string text, int baseDirection)
    {
        int n = text.Length;
        if (n == 0)
        {
            return new BidiResult(Array.Empty<int>(), Array.Empty<byte>(), baseDirection & 1);
        }

        var classes = new BidiClass[n];
        for (int i = 0; i < n; i++)
        {
            classes[i] = ClassOf(text[i]);
        }

        var levels = new byte[n];
        ResolveLevels(text, classes, levels, baseDirection & 1);
        int[] order = Reorder(levels);
        return new BidiResult(order, levels, baseDirection & 1);
    }

    /// <summary>
    /// The paragraph level a block runs at when its file states no <c>direction</c>: the first strong character's
    /// own direction (UAX #9 P2/P3), and left to right when the block holds no strong character at all.
    /// </summary>
    public static int ParagraphDirection(string text)
    {
        foreach (char c in text)
        {
            switch (ClassOf(c))
            {
                case BidiClass.L:
                    return 0;
                case BidiClass.R or BidiClass.Al:
                    return 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether any character in <paramref name="text"/> runs right to left on its own.
    ///
    /// A left-to-right block holding one Hebrew word needs the algorithm just as much as a right-to-left one does -
    /// the word is drawn as a right-to-left island - so callers ask this rather than comparing the base direction.
    /// </summary>
    public static bool HasRightToLeft(string text)
    {
        foreach (char c in text)
        {
            if (ClassOf(c) is BidiClass.R or BidiClass.Al)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a character is one of the bidirectional formatting codes this implementation does not act on.
    ///
    /// Named rather than silently folded, so a reader that meets one in a file can report it: a `RIGHT-TO-LEFT
    /// EMBEDDING` around a phrase is real layout, and treating it as a neutral is a decision the document's author
    /// would not recognise.
    /// </summary>
    public static bool IsEmbeddingCode(char c) => c is
        '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or
        '\u2066' or '\u2067' or '\u2068' or '\u2069';

    /// <summary>
    /// Resolves every character's embedding level in place.
    ///
    /// The array starts at the paragraph level for all of them - there is no explicit embedding here - and the rules
    /// run in the order UAX #9 gives them, because each one reads what the one before it wrote.
    /// </summary>
    private static void ResolveLevels(string text, BidiClass[] classes, byte[] levels, int paragraph)
    {
        int n = classes.Length;

        // X9: a boundary-neutral character takes the direction of what is around it rather than carrying one of
        // its own. There are no explicit embedding codes here, so the only ones that occur are the zero-width
        // controls, and they resolve exactly like the whitespace they sit among.
        for (int i = 0; i < n; i++)
        {
            if (classes[i] == BidiClass.Bn)
            {
                classes[i] = BidiClass.Ws;
            }
        }

        // W1: a non-spacing mark takes the type of the character before it.
        for (int i = 0; i < n; i++)
        {
            if (classes[i] == BidiClass.Nsm)
            {
                classes[i] = i == 0 ? BidiClass.On : classes[i - 1];
            }
        }

        // W2: a European number is a letter when the last strong character is one.
        BidiClass lastStrong = paragraph == 1 ? BidiClass.R : BidiClass.L;
        for (int i = 0; i < n; i++)
        {
            BidiClass c = classes[i];
            if (c is BidiClass.L or BidiClass.R or BidiClass.Al)
            {
                lastStrong = c;
            }
            else if (c == BidiClass.En && lastStrong == BidiClass.Al)
            {
                classes[i] = BidiClass.An;
            }
        }

        // W3: an Arabic letter is right to left for the rest of the algorithm.
        for (int i = 0; i < n; i++)
        {
            if (classes[i] == BidiClass.Al)
            {
                classes[i] = BidiClass.R;
            }
        }

        // W4: a single European or Arabic separator between two numbers of the same kind is that kind.
        for (int i = 1; i < n - 1; i++)
        {
            if (classes[i] == BidiClass.Es && classes[i - 1] == BidiClass.En && classes[i + 1] == BidiClass.En)
            {
                classes[i] = BidiClass.En;
            }
            else if (classes[i] == BidiClass.Cs &&
                     classes[i - 1] == classes[i + 1] &&
                     classes[i - 1] is BidiClass.En or BidiClass.An)
            {
                classes[i] = classes[i - 1];
            }
        }

        // W5: a run of terminators next to a number joins it.
        for (int i = 0; i < n; i++)
        {
            if (classes[i] != BidiClass.Et)
            {
                continue;
            }

            int j = i;
            while (j < n && classes[j] == BidiClass.Et)
            {
                j++;
            }

            bool before = i > 0 && classes[i - 1] == BidiClass.En;
            bool after = j < n && classes[j] == BidiClass.En;
            if (before || after)
            {
                for (int k = i; k < j; k++)
                {
                    classes[k] = BidiClass.En;
                }
            }

            i = j - 1;
        }

        // W6: everything else that is a separator or a terminator is a neutral.
        for (int i = 0; i < n; i++)
        {
            if (classes[i] is BidiClass.Es or BidiClass.Et or BidiClass.Cs)
            {
                classes[i] = BidiClass.On;
            }
        }

        // W7: a European number is a letter when the last strong character before it is one. Unlike W2 the search
        // looks back to the last strong type on its own, because W2 has already changed Arabic numbers.
        for (int i = 0; i < n; i++)
        {
            if (classes[i] != BidiClass.En)
            {
                continue;
            }

            for (int j = i - 1; j >= 0; j--)
            {
                if (classes[j] == BidiClass.L)
                {
                    classes[i] = BidiClass.L;
                    break;
                }

                if (classes[j] == BidiClass.R)
                {
                    break;
                }
            }
        }

        // N1/N2: a run of neutrals between two of the same direction takes it, and otherwise takes the block's.
        for (int i = 0; i < n;)
        {
            if (!IsNeutral(classes[i]))
            {
                i++;
                continue;
            }

            int j = i;
            while (j < n && IsNeutral(classes[j]))
            {
                j++;
            }

            BidiClass before = i > 0 ? Directional(classes[i - 1]) ?? Embedding(paragraph) : Embedding(paragraph);
            BidiClass after = j < n ? Directional(classes[j]) ?? Embedding(paragraph) : Embedding(paragraph);
            BidiClass resolved = before == after ? before : Embedding(paragraph);

            for (int k = i; k < j; k++)
            {
                classes[k] = resolved;
            }

            i = j;
        }

        // I1/I2: the implicit levels, and the two rules do **not** agree about a right-to-left character.
        //
        // - I1, at an even (left-to-right) paragraph level: an R goes up one level and an AN or EN goes up two.
        // - I2, at an odd (right-to-left) paragraph level: an L, EN or AN goes up one level, and an **R stays where
        //   it is** - it is already at its own direction's level.
        //
        // Raising R by two at an odd level is the bug this replaces, and it is not a subtle one: it put the whole
        // right-to-left run one level *past* the paragraph, so L2 reversed it in the wrong sweep and a mixed line
        // came out with the Latin word at the visual left of an `rtl` paragraph - the exact opposite of where a
        // right-to-left line starts. `SvgWritingModeTests.LatinInsideRightToLeftKeepsItsLettersInOrder` had been
        // asserting that wrong order, against a comment beside it that described the right one.
        for (int i = 0; i < n; i++)
        {
            BidiClass c = classes[i];
            if (c == BidiClass.L)
            {
                levels[i] = (byte)(paragraph + (paragraph % 2));
            }
            else if (c is BidiClass.R or BidiClass.En or BidiClass.An)
            {
                byte level = (byte)paragraph;
                levels[i] = level % 2 == 0
                    ? (byte)(c == BidiClass.R ? level + 1 : level + 2)
                    : (byte)(c == BidiClass.R ? level : level + 1);
            }
            else
            {
                levels[i] = (byte)paragraph;
            }
        }

        // L1: a paragraph separator, and any whitespace at the end of the paragraph, goes back to the paragraph
        // level. Without it a trailing space in a right-to-left block is drawn at the wrong end - which moves every
        // character after it to the right place but the block's own extent to the wrong one.
        int last = n;
        while (last > 0 && IsWhitespace(classes[last - 1]))
        {
            last--;
        }

        for (int i = 0; i < n; i++)
        {
            if (classes[i] is BidiClass.B or BidiClass.S)
            {
                levels[i] = (byte)paragraph;
            }
        }

        for (int i = last; i < n; i++)
        {
            levels[i] = (byte)paragraph;
        }
    }

    /// <summary>
    /// UAX #9's rule L2: the visual order, by reversing every stretch at or above each level from the highest down
    /// to the lowest odd one.
    /// </summary>
    private static int[] Reorder(byte[] levels)
    {
        int n = levels.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
        }

        int highest = 0;
        int lowestOdd = int.MaxValue;
        foreach (byte level in levels)
        {
            highest = Math.Max(highest, level);
            if (level % 2 == 1)
            {
                lowestOdd = Math.Min(lowestOdd, level);
            }
        }

        if (lowestOdd == int.MaxValue)
        {
            return order;
        }

        for (int level = highest; level >= lowestOdd; level--)
        {
            for (int i = 0; i < n; i++)
            {
                if (levels[i] < level)
                {
                    continue;
                }

                int j = i;
                while (j < n && levels[j] >= level)
                {
                    j++;
                }

                Array.Reverse(order, i, j - i);
                i = j;
            }
        }

        return order;
    }

    private static bool IsNeutral(BidiClass c) => c is BidiClass.B or BidiClass.S or BidiClass.Ws or BidiClass.On;

    private static bool IsWhitespace(BidiClass c) => c is BidiClass.Ws or BidiClass.B or BidiClass.S;

    /// <summary>The direction a resolved strong type carries, or null when it is not one.</summary>
    private static BidiClass? Directional(BidiClass c)
        => c switch
        {
            BidiClass.L => BidiClass.L,
            BidiClass.R or BidiClass.En or BidiClass.An => BidiClass.R,
            _ => null,
        };

    private static BidiClass Embedding(int paragraph) => paragraph == 1 ? BidiClass.R : BidiClass.L;

    /// <summary>The character's bidirectional type. See the class summary for what is deliberately left out.</summary>
    private static BidiClass ClassOf(char c) => c < '\u0080' ? Ascii(c) : BeyondAscii(c);

    /// <summary>
    /// The classes for ASCII, where the European separators, terminators and neutrals have to be told apart: `+`
    /// and `-` are separators, `$` and `%` terminators, and the rest of the punctuation is neutral. Calling them
    /// all neutral would leave `1+2` split around the sign.
    /// </summary>
    private static BidiClass Ascii(char c) => c switch
    {
        '\t' or '\u000b' or '\u001f' => BidiClass.S,
        '\n' or '\r' or '\u001c' or '\u001d' or '\u001e' => BidiClass.B,
        ' ' or '\u000c' => BidiClass.Ws,
        >= '0' and <= '9' => BidiClass.En,
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' => BidiClass.L,
        '+' or '-' => BidiClass.Es,
        '#' or '$' or '%' => BidiClass.Et,
        _ => BidiClass.On,
    };

    /// <summary>
    /// The classes outside ASCII, in the order the ranges have to be tested: the right-to-left blocks and the
    /// Arabic presentation forms come before the Greek and Cyrillic letters, and the combining marks come before
    /// the Arabic letters they combine with.
    /// </summary>
    private static BidiClass BeyondAscii(char c) => c switch
    {
        // Explicit embedding and override codes, and the isolates. Treated as neutral - see the class summary.
        '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or
        '\u2066' or '\u2067' or '\u2068' or '\u2069' => BidiClass.On,

        '\u0085' or '\u2029' => BidiClass.B,
        '\u00a0' or '\u3000' => BidiClass.Ws,
        '\u200b' => BidiClass.Bn,
        '\u200c' or '\u200d' => BidiClass.Nsm,

        // Combining marks, before the letters they are written with.
        >= '\u0300' and <= '\u036f' => BidiClass.Nsm,
        >= '\u0483' and <= '\u0489' => BidiClass.Nsm,
        >= '\u0591' and <= '\u05bd' => BidiClass.Nsm,
        '\u05bf' => BidiClass.Nsm,
        >= '\u05c1' and <= '\u05c2' => BidiClass.Nsm,
        >= '\u05c4' and <= '\u05c5' => BidiClass.Nsm,
        '\u05c7' => BidiClass.Nsm,
        >= '\u0610' and <= '\u061a' => BidiClass.Nsm,
        >= '\u064b' and <= '\u065f' => BidiClass.Nsm,
        '\u0670' => BidiClass.Nsm,
        >= '\u06d6' and <= '\u06dc' => BidiClass.Nsm,
        >= '\u06df' and <= '\u06e4' => BidiClass.Nsm,
        >= '\u06e7' and <= '\u06e8' => BidiClass.Nsm,
        >= '\u06ea' and <= '\u06ed' => BidiClass.Nsm,
        >= '\ufe00' and <= '\ufe0f' => BidiClass.Nsm,
        >= '\ufe20' and <= '\ufe2f' => BidiClass.Nsm,

        // Arabic numbers and the Arabic letter blocks, which are right to left but give their digits the Arabic
        // number type rather than the European one.
        >= '\u0660' and <= '\u0669' => BidiClass.An,
        >= '\u066b' and <= '\u066c' => BidiClass.An,
        >= '\u06f0' and <= '\u06f9' => BidiClass.An,
        '\u066a' => BidiClass.Et,
        >= '\u0600' and <= '\u06ff' => BidiClass.Al,
        >= '\u0700' and <= '\u074f' => BidiClass.Al,
        >= '\u0750' and <= '\u077f' => BidiClass.Al,
        >= '\u08a0' and <= '\u08ff' => BidiClass.Al,
        >= '\ufb50' and <= '\ufdfd' => BidiClass.Al,
        >= '\ufe70' and <= '\ufefc' => BidiClass.Al,

        // The rest of the right-to-left scripts: Hebrew, Samaritan, the Arabic presentation forms and the
        // historic ones. The supplementary right-to-left planes are left to the default class: they are written
        // outside the Basic Multilingual Plane and this reader does not carry their ranges.
        >= '\u0590' and <= '\u05ff' => BidiClass.R,
        >= '\u07c0' and <= '\u085f' => BidiClass.R,
        >= '\ufb1d' and <= '\ufb4f' => BidiClass.R,
        >= '\ufd50' and <= '\ufdff' => BidiClass.R,

        // Everything else that is a letter is left to right, which is the default class.
        _ => BidiClass.L,
    };
}
