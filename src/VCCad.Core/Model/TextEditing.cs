using System.Text;

namespace VCCad.Core.Model;

/// <summary>
/// Rich-text editing operations over a <see cref="TextItem"/>'s run list. Runs are
/// split at edit boundaries so a style can be applied to an arbitrary character
/// range, then adjacent runs with identical styles are merged back together.
/// Indices are global character offsets into the concatenated text.
/// </summary>
public static class TextEditing
{
    /// <summary>Total number of characters across all runs.</summary>
    public static int Length(TextItem text) => text.Runs.Sum(r => r.Text.Length);

    /// <summary>Concatenated text.</summary>
    public static string GetText(TextItem text) => string.Concat(text.Runs.Select(r => r.Text));

    /// <summary>Substring of the global character range [start, end).</summary>
    public static string GetRange(TextItem text, int start, int end)
    {
        start = Math.Clamp(start, 0, Length(text));
        end = Math.Clamp(end, start, Length(text));
        return GetText(text).Substring(start, end - start);
    }

    /// <summary>Locates the run/char position for a global index.</summary>
    public static (int Run, int Char) Locate(TextItem text, int index)
    {
        int remaining = Math.Clamp(index, 0, Length(text));
        for (int r = 0; r < text.Runs.Count; r++)
        {
            int len = text.Runs[r].Text.Length;
            if (remaining <= len)
            {
                return (r, remaining);
            }

            remaining -= len;
        }

        return (Math.Max(0, text.Runs.Count - 1),
                text.Runs.Count > 0 ? text.Runs[^1].Text.Length : 0);
    }

    /// <summary>Splits the run containing <paramref name="index"/> so it is a run
    /// boundary (no-op when already a boundary or at the ends).</summary>
    public static void SplitAt(TextItem text, int index)
    {
        if (text.Runs.Count == 0)
        {
            return;
        }

        (int run, int ch) = Locate(text, index);
        if (ch == 0 || ch == text.Runs[run].Text.Length)
        {
            return;
        }

        TextRun original = text.Runs[run];
        string left = original.Text[..ch];
        string right = original.Text[ch..];
        original.Text = left;
        var tail = original.Clone();
        tail.Text = right;
        text.Runs.Insert(run + 1, tail);
    }

    /// <summary>Inserts text at a global index, using the style of the run there.</summary>
    public static void Insert(TextItem text, int index, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (text.Runs.Count == 0)
        {
            text.Runs.Add(new TextRun { Text = value });
            return;
        }

        SplitAt(text, index);
        (int run, int ch) = Locate(text, index);

        // Insert into the run ending at the index when possible, else the next one.
        int target = ch == 0 && run > 0 ? run - 1 : run;
        target = Math.Clamp(target, 0, text.Runs.Count - 1);
        int at = ch == 0 && run > 0 ? text.Runs[target].Text.Length : ch;
        text.Runs[target].Text = text.Runs[target].Text.Insert(at, value);
        Merge(text);
    }

    /// <summary>Deletes the global range [start, end).</summary>
    public static void DeleteRange(TextItem text, int start, int end)
    {
        start = Math.Clamp(start, 0, Length(text));
        end = Math.Clamp(end, start, Length(text));
        if (start == end)
        {
            return;
        }

        SplitAt(text, start);
        SplitAt(text, end);

        int removed = 0;
        int pos = 0;
        for (int r = 0; r < text.Runs.Count && removed < end - start;)
        {
            int len = text.Runs[r].Text.Length;
            if (pos + len <= start)
            {
                pos += len;
                r++;
                continue;
            }

            int take = Math.Min(len, (end - start) - removed);
            int localStart = Math.Max(0, start - pos);
            text.Runs[r].Text = text.Runs[r].Text.Remove(localStart, Math.Min(take, len - localStart));
            removed += take;
            pos += len - take;

            if (text.Runs[r].Text.Length == 0)
            {
                text.Runs.RemoveAt(r);
            }
            else
            {
                r++;
            }
        }

        if (text.Runs.Count == 0)
        {
            text.Runs.Add(new TextRun());
        }

        Merge(text);
    }

    /// <summary>Applies a style to the global range [start, end).</summary>
    public static void ApplyStyle(TextItem text, int start, int end, Action<TextRun> apply)
    {
        start = Math.Clamp(start, 0, Length(text));
        end = Math.Clamp(end, start, Length(text));

        // An empty range styles the run containing the caret.
        if (start == end)
        {
            if (text.Runs.Count == 0)
            {
                text.Runs.Add(new TextRun());
            }

            (int run, _) = Locate(text, start);
            apply(text.Runs[run]);
            Merge(text);
            return;
        }

        SplitAt(text, start);
        SplitAt(text, end);

        int pos = 0;
        foreach (TextRun run in text.Runs)
        {
            int len = run.Text.Length;
            if (pos >= start && pos + len <= end)
            {
                apply(run);
            }

            pos += len;
        }

        Merge(text);
    }

    /// <summary>Merges adjacent runs whose style is identical.</summary>
    public static void Merge(TextItem text)
    {
        for (int r = 0; r + 1 < text.Runs.Count;)
        {
            TextRun a = text.Runs[r];
            TextRun b = text.Runs[r + 1];
            if (a.FontFamily == b.FontFamily && Math.Abs(a.FontSize - b.FontSize) < 1e-9 &&
                a.Bold == b.Bold && a.Italic == b.Italic)
            {
                a.Text += b.Text;
                text.Runs.RemoveAt(r + 1);
            }
            else
            {
                r++;
            }
        }
    }
}
