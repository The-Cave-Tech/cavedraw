using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace VCCad.App.Automation;

/// <summary>
/// Injects pointer and keyboard input at window coordinates, so an automation client can
/// operate the canvas the way a person does.
///
/// Cloning a control's effect is not the same as driving it. Everything else in this
/// automation surface calls an operation directly; text editing cannot be exercised that
/// way, because the behaviour under test *is* the gesture — placing a caret by clicking,
/// double-clicking a word, typing into a selection. Those are only covered if the events
/// actually arrive at the control.
/// </summary>
public static class InputInjection
{
    /// <summary>Next synthetic pointer id, so successive gestures are distinct devices.</summary>
    private static int _pointerId = 900;

    /// <summary>Press, optionally twice, and release at a window point.</summary>
    public static string Click(Visual root, double x, double y, int clickCount, bool shift)
    {
        Visual? target = HitTest(root, x, y);
        if (target is not Control control)
        {
            throw new EditorOperationException($"Nothing is at ({x},{y}).");
        }

        var pointer = new Pointer(++_pointerId, PointerType.Mouse, true);
        Point position = root.TranslatePoint(new Point(x, y), control) ?? new Point(x, y);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton,
            PointerUpdateKind.LeftButtonPressed);
        KeyModifiers modifiers = shift ? KeyModifiers.Shift : KeyModifiers.None;

        for (int i = 0; i < Math.Max(1, clickCount); i++)
        {
            Press(control, pointer, position, properties, modifiers, i + 1);
            Release(control, pointer, position, modifiers);
        }

        return $"{(clickCount >= 2 ? "double-" : string.Empty)}clicked {control.GetType().Name} " +
               $"at ({x:F0},{y:F0})";
    }

    /// <summary>
    /// Sends typed text to the focused control, as the keyboard would.
    ///
    /// The event must go to the *focused element*, not the window: a routed TextInput
    /// raised on the window bubbles up from the window and never reaches the canvas that
    /// is waiting for it, so typing would silently do nothing.
    /// </summary>
    /// <summary>Presses the left button at a window point without releasing.</summary>
    public static string Press(Visual root, double x, double y, bool shift)
    {
        Visual target = HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        var pointer = new Pointer(++_pointerId, PointerType.Mouse, true);
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        (target as InputElement)?.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, target, position, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                PointerUpdateKind.LeftButtonPressed),
            shift ? KeyModifiers.Shift : KeyModifiers.None, 1));

        return $"pressed at ({x:F0},{y:F0})";
    }

    /// <summary>
    /// Moves the pointer at a window point. A selection is extended by *moving* with the
    /// button held, not by releasing: a drag that only presses and releases selects
    /// nothing, which is exactly how a click-drag selection gets reported as broken.
    /// </summary>
    public static string Move(Visual root, double x, double y, bool leftDown)
    {
        Visual target = HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        var pointer = new Pointer(_pointerId, PointerType.Mouse, true);
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        RawInputModifiers modifiers = leftDown
            ? RawInputModifiers.LeftMouseButton
            : RawInputModifiers.None;

        (target as InputElement)?.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, target, pointer, target, position, 0,
            new PointerPointProperties(modifiers, PointerUpdateKind.Other),
            KeyModifiers.None));

        return $"moved to ({x:F0},{y:F0})";
    }

    /// <summary>Releases the left button at a window point - the end of a drag.</summary>
    public static string Release(Visual root, double x, double y)
    {
        Visual target = HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        var pointer = new Pointer(_pointerId, PointerType.Mouse, true);
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        (target as InputElement)?.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, target, position, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));

        return $"released at ({x:F0},{y:F0})";
    }

    public static string Type(Visual root, string text)
    {
        InputElement target = Focused(root);
        target.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = text,
        });

        return $"typed {text.Length} character(s) into {target.GetType().Name}";
    }

    /// <summary>
    /// Types at a human pace, one character at a time, so the text can be watched as it
    /// arrives. Five characters per word is the usual average, so 50 wpm is one character
    /// every 240 ms. The run is paced by a timer on the UI thread and returns at once;
    /// call it, then watch.
    /// </summary>
    public static string TypePaced(Visual root, string text, double wpm)
    {
        double perChar = 60000.0 / Math.Max(1.0, wpm * 5.0);
        int index = 0;

        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(10, perChar)),
        };

        timer.Tick += (_, _) =>
        {
            if (index >= text.Length)
            {
                timer.Stop();
                return;
            }

            InputElement target = Focused(root);
            target.RaiseEvent(new TextInputEventArgs
            {
                RoutedEvent = InputElement.TextInputEvent,
                Text = text[index].ToString(),
            });

            index++;
        };

        timer.Start();
        return $"typing {text.Length} characters at {wpm:0} wpm " +
               $"({perChar:0} ms/char, about {text.Length * perChar / 1000.0:0} s)";
    }

    /// <summary>Sends a key, with modifiers, to the focused control.</summary>
    public static string Key(Visual root, Key key, KeyModifiers modifiers)
    {
        InputElement target = Focused(root);
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });

        return $"pressed {key} into {target.GetType().Name}";
    }

    /// <summary>The control that currently has keyboard focus, else the window itself.</summary>
    private static InputElement Focused(Visual root)
    {
        IInputElement? focused = TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement();
        return focused as InputElement
            ?? root as InputElement
            ?? throw new EditorOperationException("The window cannot receive input.");
    }

    /// <summary>Wheel-scrolls at a window point.</summary>
    public static string Wheel(Visual root, double x, double y, double delta)
    {
        Visual? target = HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        (target as InputElement)?.RaiseEvent(new PointerWheelEventArgs(
            target, null, target, position, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None, new Vector(0, delta)));

        return $"wheel {delta:+0;-0} at ({x:F0},{y:F0})";
    }

    private static void Press(Control control, Pointer pointer, Point position,
        PointerPointProperties properties, KeyModifiers modifiers, int clickCount)
        => control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, control, position, 0, properties, modifiers, clickCount));

    private static void Release(Control control, Pointer pointer, Point position,
        KeyModifiers modifiers)
        => control.RaiseEvent(new PointerReleasedEventArgs(
            control, pointer, control, position, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            modifiers, MouseButton.Left));

    /// <summary>The deepest visible control under a window point.</summary>
    private static Visual? HitTest(Visual root, double x, double y)
    {
        Visual? best = null;
        foreach (Visual visual in root.GetVisualDescendants().OfType<Visual>())
        {
            if (visual is not Control { IsVisible: true, IsHitTestVisible: true } control ||
                control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            {
                continue;
            }

            Point? origin = root.TranslatePoint(new Point(x, y), control);
            if (origin is { } local && new Rect(control.Bounds.Size).Contains(local))
            {
                best = control;
            }
        }

        return best;
    }
}
