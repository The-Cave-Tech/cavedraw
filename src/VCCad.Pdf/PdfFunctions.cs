using System.Globalization;

namespace VCCad.Pdf;

/// <summary>
/// PDF type 3 and type 4 functions, which the content importer previously skipped.
///
/// Both are used for spot colours and shadings. A tint transform that is not evaluated
/// does not produce a slightly wrong colour — the raw tint is handed to a four-component
/// alternate space, so a spot colour comes out as whatever one number means to CMYK. On
/// the Ghent Output Suite's default-colour-space sheet that is black across swatches that
/// should be pink.
///
/// A type 3 function is a stitching of sub-functions over sub-domains; a type 4 function
/// is a PostScript calculator, a small stack machine with no loops and no procedures.
/// </summary>
internal static class PdfFunctions
{
    /// <summary>
    /// A type 0 function: a table of samples, interpolated.
    ///
    /// The samples run with the LAST input varying fastest, and each output is
    /// interpolated between the two surrounding samples along each axis. A table of one
    /// sample per axis is a constant, which is legitimate and common.
    /// </summary>
    public static double[]? Sampled(byte[] table, double[]? size, double[]? domain,
        double[]? encode, double[]? range, int bitsPerSample, double[] inputs)
    {
        if (size is not { Length: >= 1 } || domain is not { Length: >= 2 } ||
            inputs.Length == 0)
        {
            return null;
        }

        int dimensions = size.Length;
        if (domain.Length < dimensions * 2 || inputs.Length < dimensions)
        {
            return null;
        }

        // Range gives two numbers per output; half its length is how many there are.
        int outputs = range is { Length: >= 2 } ? range.Length / 2 : 1;
        if (outputs < 1)
        {
            return null;
        }

        long samples = 1;
        foreach (double n in size)
        {
            long count = (long)Math.Round(n);
            if (count < 1)
            {
                return null;
            }

            samples *= count;
        }

        // One bit packs eight samples to a byte, so the table has to be addressed in bits
        // and read back the way the file packed it.
        long bitCount = samples * outputs * bitsPerSample;
        if (bitCount > (long)table.Length * 8)
        {
            return null;
        }

        var result = new double[outputs];
        var low = new int[dimensions];
        var high = new int[dimensions];
        var fraction = new double[dimensions];

        for (int axis = 0; axis < dimensions; axis++)
        {
            int count = (int)Math.Round(size[axis]);
            double lo = domain[axis * 2];
            double hi = domain[(axis * 2) + 1];

            double value = Math.Clamp(inputs[axis], Math.Min(lo, hi), Math.Max(lo, hi));
            value = hi > lo ? (value - lo) / (hi - lo) : 0.0;

            // Encode maps the input straight onto sample indices; without one the whole
            // table is spanned. Scaling again after encoding would read the wrong sample
            // — with Encode [0 2] over three samples, a half lands on the last one.
            double position = encode is { Length: >= 2 } && (axis * 2) + 1 < encode.Length
                ? encode[axis * 2] + (value * (encode[(axis * 2) + 1] - encode[axis * 2]))
                : value * (count - 1);

            low[axis] = Math.Clamp((int)Math.Floor(position), 0, count - 1);
            high[axis] = Math.Clamp(low[axis] + 1, 0, count - 1);
            fraction[axis] = Math.Max(0.0, position - low[axis]);
        }

        // Interpolate over the 2^dimensions corners of the cell the input falls in.
        int corners = 1 << dimensions;
        for (int corner = 0; corner < corners; corner++)
        {
            double weight = 1.0;
            long index = 0;

            for (int axis = 0; axis < dimensions; axis++)
            {
                bool upper = (corner & (1 << axis)) != 0;
                weight *= upper ? fraction[axis] : 1.0 - fraction[axis];
                index = (index * (long)Math.Round(size[axis])) + (upper ? high[axis] : low[axis]);
            }

            if (weight == 0.0)
            {
                continue;
            }

            for (int output = 0; output < outputs; output++)
            {
                double raw = ReadBits(table, (index * outputs) + output, bitsPerSample);
                double max = (1 << bitsPerSample) - 1;
                result[output] += weight * (max > 0 ? raw / max : 0.0);
            }
        }

        // Decode through Range, which maps the interpolated 0..1 onto the output's range.
        if (range is { Length: >= 2 })
        {
            for (int output = 0; output < outputs; output++)
            {
                int at = Math.Min(output * 2, range.Length - 2);
                result[output] = range[at] + (result[output] * (range[at + 1] - range[at]));
            }
        }

        return result;
    }

    /// <summary>
    /// One sample out of a packed table. Samples are stored most significant bit first,
    /// which matters at one or two bits per sample where eight and four share a byte.
    /// </summary>
    private static double ReadBits(byte[] table, long sample, int bits)
    {
        long bitOffset = sample * bits;
        int value = 0;
        for (int i = 0; i < bits; i++)
        {
            long at = bitOffset + i;
            long index = at >> 3;
            if (index >= table.Length)
            {
                return 0;
            }

            int bit = (table[index] >> (7 - (int)(at & 7))) & 1;
            value = (value << 1) | bit;
        }

        return value;
    }

    /// <summary>
    /// A type 3 function: split the input at the boundaries, scale it into the chosen
    /// sub-domain, and hand it to that sub-function through its own encode range.
    /// </summary>
    public static double[]? Type3(Dictionary<string, object?> dict, double[] inputs,
        Func<object?, IReadOnlyList<double>, double[]?> evaluate, Func<object?, double[]?> numbers)
    {
        double[]? domain = numbers(dict.GetValueOrDefault("Domain"));
        double[]? bounds = numbers(dict.GetValueOrDefault("Bounds"));
        double[]? encode = numbers(dict.GetValueOrDefault("Encode"));
        object? functions = dict.GetValueOrDefault("Functions");

        if (domain is not { Length: >= 2 } || functions is not List<object?> subs || subs.Count == 0)
        {
            return null;
        }

        // Bounds separate the sub-domains, so there is one more part than boundary: n
        // boundaries and n+1 functions.
        int parts = subs.Count;
        if (bounds is null || bounds.Length != parts - 1)
        {
            return null;
        }

        double x = Math.Clamp(inputs.Length > 0 ? inputs[0] : 0.0, domain[0], domain[1]);

        int part = 0;
        while (part < bounds.Length && x >= bounds[part])
        {
            part++;
        }

        double low = part == 0 ? domain[0] : bounds[part - 1];
        double high = part == bounds.Length ? domain[1] : bounds[part];

        // Encode maps the sub-domain onto the sub-function's own domain.
        double encoded = x;
        if (high > low)
        {
            double e0 = encode is { Length: >= 2 } && (part * 2) + 1 < encode.Length
                ? encode[part * 2]
                : 0.0;
            double e1 = encode is { Length: >= 2 } && (part * 2) + 1 < encode.Length
                ? encode[(part * 2) + 1]
                : 1.0;
            encoded = e0 + ((x - low) / (high - low) * (e1 - e0));
        }

        return evaluate(subs[part], new[] { encoded });
    }

    /// <summary>
    /// A type 4 function: a PostScript calculator over a small stack.
    ///
    /// The language has no loops and no procedures, so this is a stack, an if/ifelse, and
    /// arithmetic. Anything it does not recognise stops the evaluation and the caller
    /// keeps the input rather than producing a number that looks plausible and is wrong.
    /// </summary>
    public static double[]? Type4(object? code, double[] inputs, int outputCount)
    {
        string? source = code switch
        {
            string text => text,
            byte[] bytes => System.Text.Encoding.Latin1.GetString(bytes),
            _ => null,
        };

        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var stack = new List<double>(inputs);
        var tokens = Tokenize(source);
        var outputs = new List<double>();

        try
        {
            // The program is itself enclosed in braces, and a brace is otherwise skipped
            // as a procedure that was pushed and not executed — so the wrapper has to come
            // off, or the whole function is skipped and the input falls through unchanged.
            int start = 0;
            int end = tokens.Count;
            if (end >= 2 && tokens[0] == "{" && MatchBrace(tokens, 0, end) == end - 1)
            {
                start = 1;
                end -= 1;
            }

            if (!Run(tokens, start, end, stack, outputs, depth: 0, outputCount))
            {
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        // The number of outputs is fixed by the function's Range, so keep the last that
        // many: a program may leave scratch values underneath its results.
        if (outputs.Count > outputCount)
        {
            outputs.RemoveRange(0, outputs.Count - outputCount);
        }

        return outputs.ToArray();
    }

    /// <summary>Splits the program into names, numbers and brace delimiters.</summary>
    private static List<string> Tokenize(string source)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            // Comments run to the end of the line.
            if (c == '%')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            int start = i;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] is not ('{' or '}'))
            {
                i++;
            }

            tokens.Add(source[start..i]);
        }

        return tokens;
    }

    /// <summary>Runs one stretch of tokens; <paramref name="end"/> is exclusive.</summary>
    private static bool Run(List<string> tokens, int start, int end, List<double> stack,
        List<double> outputs, int depth, int outputCount)
    {
        // Recursion is bounded so a malformed file cannot run away with the stack.
        if (depth > 32)
        {
            return false;
        }

        // A brace group in PostScript pushes a procedure rather than running it, and
        // if/ifelse pop what was pushed. Skipping the group as the loop passes it looks
        // equivalent but is not: by the time ifelse runs, the braces it needs are behind
        // it, so the branch is never taken.
        var procedures = new List<(int Start, int End)>();

        for (int i = start; i < end; i++)
        {
            string token = tokens[i];

            switch (token)
            {
                case "{":
                {
                    int close = MatchBrace(tokens, i, end);
                    if (close < 0)
                    {
                        return false;
                    }

                    procedures.Add((i + 1, close));
                    i = close;
                    continue;
                }

                case "}":
                    return true;
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double number))
            {
                stack.Add(number);
                continue;
            }

            switch (token)
            {
                // Arithmetic.
                case "add": if (!Binary(stack, (a, b) => a + b)) return false; break;
                case "sub": if (!Binary(stack, (a, b) => a - b)) return false; break;
                case "mul": if (!Binary(stack, (a, b) => a * b)) return false; break;
                case "div": if (!Binary(stack, (a, b) => a / b)) return false; break;
                case "idiv": if (!Binary(stack, (a, b) => Math.Truncate(a / b))) return false; break;
                case "mod": if (!Binary(stack, (a, b) => a - (Math.Truncate(a / b) * b))) return false; break;
                case "atan": if (!Binary(stack, (a, b) => Math.Atan2(a, b) * 180.0 / Math.PI)) return false; break;
                case "exp": if (!Binary(stack, Math.Pow)) return false; break;

                case "neg": if (!Unary(stack, a => -a)) return false; break;
                case "abs": if (!Unary(stack, Math.Abs)) return false; break;
                case "ceiling": if (!Unary(stack, Math.Ceiling)) return false; break;
                case "floor": if (!Unary(stack, Math.Floor)) return false; break;
                case "round": if (!Unary(stack, a => Math.Round(a, MidpointRounding.AwayFromZero))) return false; break;
                case "truncate": if (!Unary(stack, Math.Truncate)) return false; break;
                case "sqrt": if (!Unary(stack, Math.Sqrt)) return false; break;
                case "ln": if (!Unary(stack, Math.Log)) return false; break;
                case "log": if (!Unary(stack, Math.Log10)) return false; break;
                case "sin": if (!Unary(stack, a => Math.Sin(a * Math.PI / 180.0))) return false; break;
                case "cos": if (!Unary(stack, a => Math.Cos(a * Math.PI / 180.0))) return false; break;

                // Comparison, producing 1 or 0.
                case "eq": if (!Binary(stack, (a, b) => a == b ? 1 : 0)) return false; break;
                case "ne": if (!Binary(stack, (a, b) => a != b ? 1 : 0)) return false; break;
                case "gt": if (!Binary(stack, (a, b) => a > b ? 1 : 0)) return false; break;
                case "ge": if (!Binary(stack, (a, b) => a >= b ? 1 : 0)) return false; break;
                case "lt": if (!Binary(stack, (a, b) => a < b ? 1 : 0)) return false; break;
                case "le": if (!Binary(stack, (a, b) => a <= b ? 1 : 0)) return false; break;

                // Boolean.
                case "and": if (!Binary(stack, (a, b) => a != 0 && b != 0 ? 1 : 0)) return false; break;
                case "or": if (!Binary(stack, (a, b) => a != 0 || b != 0 ? 1 : 0)) return false; break;
                case "not": if (!Unary(stack, a => a == 0 ? 1 : 0)) return false; break;
                case "xor": if (!Binary(stack, (a, b) => (a != 0) ^ (b != 0) ? 1 : 0)) return false; break;

                // Stack.
                case "dup":
                {
                    if (stack.Count < 1) return false;
                    stack.Add(stack[^1]);
                    break;
                }

                case "exch":
                {
                    if (stack.Count < 2) return false;
                    (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                    break;
                }

                case "pop":
                    if (stack.Count < 1) return false;
                    stack.RemoveAt(stack.Count - 1);
                    break;

                case "copy":
                {
                    if (stack.Count < 1) return false;
                    int count = (int)stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (count < 0 || stack.Count < count) return false;
                    for (int k = 0; k < count; k++)
                    {
                        stack.Add(stack[stack.Count - count]);
                    }

                    break;
                }

                case "index":
                {
                    if (stack.Count < 1) return false;
                    int n = (int)stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (n < 0 || stack.Count < n + 1) return false;
                    stack.Add(stack[stack.Count - 1 - n]);
                    break;
                }

                case "roll":
                {
                    if (stack.Count < 2) return false;
                    int j = (int)stack[^1];
                    int n = (int)stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    if (n < 0 || j < 0 || stack.Count < n) return false;
                    List<double> slice = stack.GetRange(stack.Count - n, n);
                    stack.RemoveRange(stack.Count - n, n);
                    int shift = ((j % n) + n) % n;
                    for (int k = 0; k < n; k++)
                    {
                        stack.Add(slice[(k - shift + n) % n]);
                    }

                    break;
                }

                // Control.
                case "if":
                {
                    if (procedures.Count < 1 || stack.Count < 1) return false;
                    (int bodyStart, int bodyEnd) = procedures[^1];
                    procedures.RemoveAt(procedures.Count - 1);
                    bool run = stack[^1] != 0;
                    stack.RemoveAt(stack.Count - 1);
                    if (run && !Run(tokens, bodyStart, bodyEnd, stack, outputs, depth + 1, outputCount))
                    {
                        return false;
                    }

                    break;
                }

                case "ifelse":
                {
                    if (procedures.Count < 2 || stack.Count < 1) return false;
                    (int elseStart, int elseEnd) = procedures[^1];
                    (int thenStart, int thenEnd) = procedures[^2];
                    procedures.RemoveRange(procedures.Count - 2, 2);
                    bool runThen = stack[^1] != 0;
                    stack.RemoveAt(stack.Count - 1);
                    bool ok = runThen
                        ? Run(tokens, thenStart, thenEnd, stack, outputs, depth + 1, outputCount)
                        : Run(tokens, elseStart, elseEnd, stack, outputs, depth + 1, outputCount);
                    if (!ok)
                    {
                        return false;
                    }

                    break;
                }

                default:
                    // An operator this does not implement is not guessed at: the caller
                    // keeps the input rather than a number that looks plausible.
                    return false;
            }
        }

        // Whatever is left on the stack is the result, in order.
        if (outputs.Count == 0)
        {
            outputs.AddRange(stack);
        }

        return true;
    }

    private static int MatchBrace(List<string> tokens, int open, int end)
    {
        int depth = 0;
        for (int i = open; i < end; i++)
        {
            if (tokens[i] == "{")
            {
                depth++;
            }
            else if (tokens[i] == "}")
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool Binary(List<double> stack, Func<double, double, double> op)
    {
        if (stack.Count < 2)
        {
            return false;
        }

        double b = stack[^1];
        double a = stack[^2];
        stack.RemoveRange(stack.Count - 2, 2);
        stack.Add(op(a, b));
        return true;
    }

    private static bool Unary(List<double> stack, Func<double, double> op)
    {
        if (stack.Count < 1)
        {
            return false;
        }

        stack[^1] = op(stack[^1]);
        return true;
    }
}
