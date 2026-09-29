using VCCad.Core.Selection;

namespace VCCad.Core.Input;

/// <summary>Whether replay waits with a clock or runs flat out.</summary>
public enum InputTiming
{
    /// <summary>Wait each event's delta against the clock, as a person's session did.</summary>
    RealTime,

    /// <summary>Deliver everything at once; the deltas stay on the events but are not waited.</summary>
    AsFastAsPossible,
}

/// <summary>Which device produced a pointer event.</summary>
public enum InputPointerDevice
{
    Mouse,
    Pen,
    Touch,
}

/// <summary>A mouse/pen button, by the name it carries in the shared file format.</summary>
public enum InputButton
{
    None,
    Left,
    Middle,
    Right,
    Back,
    Forward,
}

/// <summary>Modifier keys held when an event happened, as the platform reports them.</summary>
[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Meta = 8,
    CapsLock = 16,
    NumLock = 32,
}

/// <summary>
/// The event kinds the shared gesture format knows, named as they appear in the file.
///
/// The pointer kinds stay <c>down</c>, <c>move</c> and <c>up</c> so that a batch written here
/// is still readable as the selection fixture's list of <c>RecordedEvent</c>s: there is one
/// file format for a gesture, whatever produced it. The rest of the kinds are additions to
/// that same record, not a second format.
/// </summary>
public static class InputKinds
{
    public const string Down = "down";
    public const string Move = "move";
    public const string Up = "up";
    public const string Wheel = "wheel";
    public const string Hover = "hover";
    public const string HoverOut = "hover-out";
    public const string Enter = "enter";
    public const string Leave = "leave";
    public const string KeyDown = "keydown";
    public const string KeyUp = "keyup";
    public const string Text = "text";
    public const string PenDown = "pen-down";
    public const string PenMove = "pen-move";
    public const string PenUp = "pen-up";
    public const string TouchDown = "touch-down";
    public const string TouchMove = "touch-move";
    public const string TouchUp = "touch-up";

    /// <summary>Every kind the format understands.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Down, Move, Up, Wheel, Hover, HoverOut, Enter, Leave,
        KeyDown, KeyUp, Text, PenDown, PenMove, PenUp, TouchDown, TouchMove, TouchUp,
    };

    /// <summary>Pointer events that carry a position and take part in a drag.</summary>
    public static bool IsPointer(string? kind) => kind is
        Down or Move or Up or PenDown or PenMove or PenUp or TouchDown or TouchMove or TouchUp;

    /// <summary>Kinds that carry a position but no button.</summary>
    public static bool HasPosition(string? kind) => IsPointer(kind) || kind is
        Wheel or Hover or HoverOut or Enter or Leave;

    public static bool IsPen(string? kind) => kind is PenDown or PenMove or PenUp;

    public static bool IsTouch(string? kind) => kind is TouchDown or TouchMove or TouchUp;

    public static bool IsKey(string? kind) => kind is KeyDown or KeyUp;
}

/// <summary>
/// One recorded input event, exactly as it travels in a batch file.
///
/// This is the selection fixture's <see cref="RecordedEvent"/> widened, not replaced: the first
/// five members keep its names and meaning, so an existing fixture loads unchanged, and the rest
/// carry what every other kind of input needs — a delta, button and modifier state, pressure and
/// tilt for a pen, a pointer id for touch. No window, no control, no Avalonia anywhere.
/// </summary>
/// <param name="Kind">One of <see cref="InputKinds"/> — "down", "move", "up", and the rest.</param>
/// <param name="X">Document x.</param>
/// <param name="Y">Document y.</param>
/// <param name="DeltaMs">Milliseconds since the event before it, or since the replay began.</param>
/// <param name="Extend">Whether the additive modifier was held.</param>
/// <param name="Modifier">Whether the platform transform modifier was held.</param>
/// <param name="Button">Button state: "left", "middle", "right", "back", "forward".</param>
/// <param name="Modifiers">Modifier keys held: "Shift, Control".</param>
/// <param name="Device">Which device: "mouse", "pen" or "touch".</param>
/// <param name="Pressure">Pen pressure in 0..1, when the platform reports it.</param>
/// <param name="TiltX">Pen tilt in degrees, when the platform reports it.</param>
/// <param name="TiltY">Pen tilt in degrees, when the platform reports it.</param>
/// <param name="Key">Key name for a keyboard event, as the platform names it.</param>
/// <param name="Text">Text for a text input event.</param>
/// <param name="PointerId">The pointer's id, so a multi-touch sequence stays distinguishable.</param>
/// <param name="WheelDelta">Wheel movement, in notches; positive is up.</param>
public sealed record InputEvent(
    string Kind,
    double X = 0,
    double Y = 0,
    double DeltaMs = 0,
    bool Extend = false,
    bool Modifier = false,
    string? Button = null,
    string? Modifiers = null,
    string? Device = null,
    double? Pressure = null,
    double? TiltX = null,
    double? TiltY = null,
    string? Key = null,
    string? Text = null,
    int? PointerId = null,
    double? WheelDelta = null)
{
    /// <summary>This event as the selection engine's own kind, or null when it is not a pointer.</summary>
    public SelectEvent? ToSelectEvent() => Kind switch
    {
        InputKinds.Down or InputKinds.PenDown or InputKinds.TouchDown =>
            new PointerDown(new VCCad.Geometry.Point2D(X, Y), Extend),
        InputKinds.Move or InputKinds.PenMove or InputKinds.TouchMove =>
            new PointerMove(new VCCad.Geometry.Point2D(X, Y)),
        InputKinds.Up or InputKinds.PenUp or InputKinds.TouchUp =>
            new PointerUp(new VCCad.Geometry.Point2D(X, Y), Extend),
        _ => null,
    };

    /// <summary>Widens a selection fixture event into the shared format.</summary>
    public static InputEvent FromRecorded(RecordedEvent recorded) => new(
        recorded.Kind,
        recorded.X,
        recorded.Y,
        DeltaMs: 0,
        Extend: recorded.Extend,
        Modifier: recorded.Modifier,
        Device: nameof(InputPointerDevice.Mouse));

    /// <summary>The button as a value, or null when the event names none.</summary>
    public InputButton? ButtonState() =>
        InputEventNames.TryParseButton(Button, out InputButton button) ? button : null;

    /// <summary>The modifiers as a value, or null when the event names unparseable ones.</summary>
    public InputModifiers? ModifierState() =>
        InputEventNames.TryParseModifiers(Modifiers, out InputModifiers modifiers) ? modifiers : null;

    /// <summary>The device as a value, or null when the event names none/unparseable ones.</summary>
    public InputPointerDevice? DeviceKind() =>
        InputEventNames.TryParseDevice(Device, out InputPointerDevice device) ? device : null;
}

/// <summary>Parsing and formatting for the names the shared format carries as text.</summary>
public static class InputEventNames
{
    /// <summary>Reads a button name.</summary>
    public static bool TryParseButton(string? text, out InputButton button)
    {
        button = InputButton.None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out button);
    }

    /// <summary>Reads a comma/plus separated list of modifier names.</summary>
    public static bool TryParseModifiers(string? text, out InputModifiers modifiers)
    {
        modifiers = InputModifiers.None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string token in text.Split(
            new[] { ',', '+', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(token, ignoreCase: true, out InputModifiers one))
            {
                return false;
            }

            modifiers |= one;
        }

        return true;
    }

    /// <summary>Reads a device name.</summary>
    public static bool TryParseDevice(string? text, out InputPointerDevice device)
    {
        device = InputPointerDevice.Mouse;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out device);
    }

    /// <summary>Writes modifiers the way the format carries them.</summary>
    public static string FormatModifiers(InputModifiers modifiers)
    {
        if (modifiers == InputModifiers.None)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (InputModifiers flag in Enum.GetValues<InputModifiers>())
        {
            if (flag != InputModifiers.None && modifiers.HasFlag(flag))
            {
                parts.Add(flag.ToString());
            }
        }

        return string.Join(", ", parts);
    }
}
