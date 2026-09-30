using System.Globalization;
using System.Text.RegularExpressions;
using VCCad.Core.Input;

namespace VCCad.App.Automation;

/// <summary>
/// Turns the diary's record of a session into a replayable input batch.
///
/// The diary is written for a person to read, so an event's position lives inside its detail text
/// ("at (123,456)") rather than in a field of its own. This reads it back out. That is the cost of
/// keeping one diary format instead of two, and it is worth paying: the batch then carries the very
/// events the recorder saw, so replaying it goes through the same input path a hand does and the
/// two cannot diverge.
///
/// Hover, focus and drag-and-drop are left out. They are worth recording and worth reading, but
/// they are not part of the gesture that produced an edit, and replaying them into a running
/// application would move the pointer around for no result.
/// </summary>
public static partial class DiaryBatchExport
{
    /// <summary>The whole diary, or one session's part of it.</summary>
    public static InputBatch From(InteractionLog diary, string? sessionId = null, int max = 20000)
    {
        IReadOnlyList<InteractionRecord> records = string.IsNullOrWhiteSpace(sessionId)
            ? diary.Tail(max)
            : diary.Session(sessionId, max);

        return ToBatch(records);
    }

    /// <summary>The replayable part of a run of diary records: the person's own input, in order.</summary>
    public static InputBatch ToBatch(IReadOnlyList<InteractionRecord> records)
    {
        var events = new List<InputEvent>();
        DateTimeOffset? previous = null;

        foreach (InteractionRecord record in records)
        {
            // Operations, model calls and session marks are not gestures.
            if (record.Kind != InteractionKind.Ui)
            {
                continue;
            }

            if (Map(record) is not { } input)
            {
                continue;
            }

            // Deltas come from the diary's own timestamps, so a replay keeps the rhythm the person
            // actually worked at rather than one invented at export time.
            double delta = previous is { } last ? (record.TimestampUtc - last).TotalMilliseconds : 0;
            events.Add(input with { DeltaMs = Math.Max(0, delta) });
            previous = record.TimestampUtc;
        }

        return new InputBatch { Name = "diary session", Events = events };
    }

    private static InputEvent? Map(InteractionRecord record) => record.Name switch
    {
        "pointer.press" => TryPoint(record.Details, out double px, out double py)
            ? new InputEvent(InputKinds.Down, px, py,
                Button: ButtonName(record.Details), Modifiers: ModifierNames(record.Details))
            : null,

        "pointer.release" => TryPoint(record.Details, out double rx, out double ry)
            ? new InputEvent(InputKinds.Up, rx, ry, Button: "left")
            : null,

        // A release that ended a drag is recorded as a drop, and its detail text carries two
        // positions - where the drag began and where it ended. The up is the second one; taking
        // the first would let go where the gesture started.
        "pointer.drop" => TryPoint(record.Details, out double dx, out double dy, last: true)
            ? new InputEvent(InputKinds.Up, dx, dy, Button: "left")
            : null,

        // The first sample of a drag, recorded at the point the pointer set off from: it seeds the
        // path so the replay has somewhere to move from.
        "pointer.drag.start" => TryPoint(record.Details, out double sx, out double sy)
            ? new InputEvent(InputKinds.Move, sx, sy, Button: "left")
            : null,

        // The recorder samples a drag at a fixed interval, so this is the same path a person took,
        // sampled - not every pixel, which is why a replayed drag feels identical rather than
        // exactly identical.
        "pointer.drag" => TryPoint(record.Details, out double mx, out double my)
            ? new InputEvent(InputKinds.Move, mx, my, Button: "left")
            : null,

        "pointer.wheel" => TryPoint(record.Details, out double wx, out double wy)
            ? new InputEvent(InputKinds.Wheel, wx, wy,
                WheelDelta: WheelDelta(record.Details),
                Modifiers: record.Details?.Contains("Ctrl", StringComparison.Ordinal) == true
                    ? "Control"
                    : null)
            : null,

        "key.down" => TryKey(record.Details, out string? key, out string? modifiers)
            ? new InputEvent(InputKinds.KeyDown, Key: key, Modifiers: modifiers)
            : null,

        _ => null,
    };

    /// <summary>
    /// The first "(x,y)" in a detail string that really is a pair of numbers - or the last one,
    /// for a drop, whose text names both ends of the drag. Taking the first parenthesis would read
    /// the "(zoom)" in a wheel event as a position.
    /// </summary>
    private static bool TryPoint(string? details, out double x, out double y, bool last = false)
    {
        x = 0;
        y = 0;

        if (details is null)
        {
            return false;
        }

        MatchCollection matches = Parenthesised().Matches(details);
        IEnumerable<Match> ordered = last ? matches.Reverse() : matches;

        foreach (Match match in ordered)
        {
            string[] parts = match.Groups[1].Value.Split(',');

            if (parts.Length == 2 &&
                double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedX) &&
                double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedY))
            {
                x = parsedX;
                y = parsedY;
                return true;
            }
        }

        return false;
    }

    /// <summary>The button a press was made with, from that press's own detail text.</summary>
    private static string? ButtonName(string? details)
    {
        string first = details?.Split(',')[0].Trim() ?? string.Empty;
        return first is "left" or "middle" or "right" or "back" or "forward" ? first : "left";
    }

    /// <summary>
    /// The modifiers a press was made with. The recorder writes them between "modifiers " and the
    /// position, and a flags enum spells more than one of them with commas ("Shift, Control"), so
    /// the two markers are what bound the value rather than the commas inside it.
    /// </summary>
    private static string? ModifierNames(string? details)
    {
        if (details is null)
        {
            return null;
        }

        int start = details.IndexOf("modifiers ", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += "modifiers ".Length;
        int end = details.IndexOf(", at ", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        string value = details[start..end].Trim();
        return value is "-" or "" ? null : value;
    }

    private static double? WheelDelta(string? details)
    {
        if (details is null)
        {
            return null;
        }

        Match match = Regex.Match(details, @"delta\s+(-?[0-9.]+)");
        return match.Success &&
               double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta)
            ? delta
            : null;
    }

    /// <summary>
    /// A key event from its recorded spelling: "Control+S", "F12 ('F12')", "Escape". The recorder
    /// appends the printable symbol in parentheses when no modifier was held, and that is not part
    /// of the key's name.
    /// </summary>
    private static bool TryKey(string? details, out string? key, out string? modifiers)
    {
        key = null;
        modifiers = null;

        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        string text = details;
        int symbol = text.IndexOf(" ('", StringComparison.Ordinal);
        if (symbol > 0)
        {
            text = text[..symbol];
        }

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        key = parts[^1].Trim();
        if (parts.Length > 1)
        {
            modifiers = string.Join(", ", parts[..^1].Select(p => p.Trim()));
        }

        return key.Length > 0;
    }

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex Parenthesised();
}
