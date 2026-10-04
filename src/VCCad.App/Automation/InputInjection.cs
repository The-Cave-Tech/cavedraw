using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using VCCad.App.Controls;

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

    /// <summary>
    /// The pointer currently down, reused across press/move/release.
    ///
    /// A fresh <see cref="Pointer"/> per event loses the capture a drag depends on: a
    /// control that captures on press only receives the moves that follow if they carry
    /// the same pointer, so a synthetic drag would move the pointer across the window
    /// while the captured control never heard about it. Real drags - a dock separator, a
    /// slider, a scrollbar - silently did nothing.
    /// </summary>
    private static Pointer? _activePointer;

    /// <summary>Press, optionally twice, and release at a window point.</summary>
    public static string Click(Visual root, double x, double y, int clickCount, bool shift,
        bool right = false)
    {
        Visual? target = HitTest(root, x, y);
        if (target is not Control control)
        {
            throw new EditorOperationException($"Nothing is at ({x},{y}).");
        }

        FocusFor(control);
        var pointer = new Pointer(++_pointerId, PointerType.Mouse, true);
        _activePointer = pointer;
        Point position = root.TranslatePoint(new Point(x, y), control) ?? new Point(x, y);
        var properties = new PointerPointProperties(
            right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
            right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed);
        KeyModifiers modifiers = shift ? KeyModifiers.Shift : KeyModifiers.None;

        for (int i = 0; i < Math.Max(1, clickCount); i++)
        {
            Press(control, pointer, position, properties, modifiers, i + 1);
            Release(control, pointer, position, modifiers, right);
        }

        if (right)
        {
            // Avalonia opens a context menu from ContextRequested, which its own input manager
            // raises. Synthetic pointer events do not pass through that manager, so a right
            // click was delivered - the operation reported the control it hit - and no menu
            // ever appeared. Raising the request here is what makes the gesture complete.
            control.RaiseEvent(new ContextRequestedEventArgs());
        }

        return $"{(clickCount >= 2 ? "double-" : string.Empty)}clicked {control.GetType().Name} " +
               $"at ({x:F0},{y:F0})" + Describe(control);
    }

    /// <summary>
    /// Where the control sits in the visual tree, so a click that lands somewhere unexpected says where.
    ///
    /// A gesture that reaches the wrong control is the worst kind of automation failure: the operation
    /// succeeds, the client sees no error, and the only evidence is a selection that did not change. The
    /// caller is told which control took the click *and* what it is inside, which is the difference between
    /// "clicked Grid" and knowing the canvas never saw it.
    /// </summary>
    private static string Describe(Control control)
    {
        var names = new List<string> { Label(control) };
        bool insideCanvas = false;
        CanvasWorkspace? canvas = null;

        for (Visual? v = control.GetVisualParent(); v is not null && names.Count < 8; v = v.GetVisualParent())
        {
            names.Add(Label(v));
            if (v is CanvasWorkspace found)
            {
                insideCanvas = true;
                canvas = found;
            }
        }

        string where = $" in {string.Join(" < ", names)}";

        // A gesture that lands outside the canvas selects nothing and reports no error, which is the
        // hardest kind of miss to see. Say so, and say where the canvas actually is, so the next click can
        // be aimed rather than guessed at.
        if (!insideCanvas)
        {
            CanvasWorkspace? nearest = canvas ?? control.GetVisualDescendants().OfType<CanvasWorkspace>().FirstOrDefault();
            if (nearest is null && TopLevel.GetTopLevel(control) is { } top)
            {
                nearest = top.GetVisualDescendants().OfType<CanvasWorkspace>().FirstOrDefault();
            }

            if (nearest is not null && TopLevel.GetTopLevel(nearest) is { } window)
            {
                Point at = nearest.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
                where += $" - the canvas is not under this point; it starts at window " +
                         $"({at.X:F0},{at.Y:F0}) and is {nearest.Bounds.Width:F0}x{nearest.Bounds.Height:F0}";
            }
        }

        return where;
    }

    /// <summary>A control's type and name - "Grid#LeftPanelHost" answers a question that "Grid" cannot.</summary>
    private static string Label(Visual visual)
        => visual is Control { Name: { Length: > 0 } name }
            ? $"{visual.GetType().Name}#{name}"
            : visual.GetType().Name;

    /// <summary>
    /// Sends typed text to the focused control, as the keyboard would.
    ///
    /// The event must go to the *focused element*, not the window: a routed TextInput
    /// raised on the window bubbles up from the window and never reaches the canvas that
    /// is waiting for it, so typing would silently do nothing.
    /// </summary>
    /// <summary>
    /// Presses the left button at a window point without releasing.
    ///
    /// `pointerType` exists because the pen path is gated on it: `CanvasWorkspace.PenSample` reads pressure and tilt
    /// only from a pointer whose `Type` is `PointerType.Pen`, so a driver that can only raise a mouse can never
    /// reach the pen's dynamics - a person with a tablet can, which makes it a parity defect rather than a gap.
    /// `Move` and `Release` reuse the active pointer, so a stroke pressed as a pen stays a pen throughout.
    /// </summary>
    public static string Press(Visual root, double x, double y, bool shift, bool right = false,
        PointerType pointerType = PointerType.Mouse,
        float? pressure = null, float? xTilt = null, float? yTilt = null, int clickCount = 1)
    {
        Visual target = HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        FocusFor(target);
        var pointer = new Pointer(++_pointerId, pointerType, true);
        _activePointer = pointer;
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        (target as InputElement)?.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, target, position, 0,
            Props(
                right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
                right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed,
                pressure, xTilt, yTilt),
            shift ? KeyModifiers.Shift : KeyModifiers.None, clickCount));

        return $"pressed at ({x:F0},{y:F0})";
    }

    /// <summary>
    /// The pointer's properties, carrying a pen's own reading when one is stated.
    ///
    /// `PointerPointProperties` has a constructor that takes them - `(modifiers, kind, twist, pressure, xTilt,
    /// yTilt)` - which is what makes the pressure dynamics reachable by a driver at all: `CanvasWorkspace.PenSample`
    /// reads `properties.Pressure` and `XTilt`/`YTilt` for a pointer whose type is `Pen`, so a synthetic pen can now
    /// carry a pressure that **varies along the stroke** rather than one constant default.
    ///
    /// With nothing stated the two-argument constructor is used, so everything that does not ask for a pen keeps
    /// exactly the properties it had.
    /// </summary>
    private static PointerPointProperties Props(RawInputModifiers modifiers, PointerUpdateKind kind,
        float? pressure, float? xTilt, float? yTilt)
        => pressure is null && xTilt is null && yTilt is null
            ? new PointerPointProperties(modifiers, kind)
            : new PointerPointProperties(modifiers, kind, 0f, pressure ?? 0.5f, xTilt ?? 0f, yTilt ?? 0f);

    /// <summary>
    /// Moves the pointer at a window point. A selection is extended by *moving* with the
    /// button held, not by releasing: a drag that only presses and releases selects
    /// nothing, which is exactly how a click-drag selection gets reported as broken.
    /// </summary>
    public static string Move(Visual root, double x, double y, bool leftDown,
        float? pressure = null, float? xTilt = null, float? yTilt = null)
    {
        // While a button is down the captured element gets the moves, exactly as a real
        // pointer behaves - otherwise the capture made on press is meaningless.
        Pointer pointer = _activePointer ?? new Pointer(++_pointerId, PointerType.Mouse, true);
        Visual target = pointer.Captured as Visual
            ?? HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        RawInputModifiers modifiers = leftDown
            ? RawInputModifiers.LeftMouseButton
            : RawInputModifiers.None;

        (target as InputElement)?.RaiseEvent(new PointerEventArgs(
            InputElement.PointerMovedEvent, target, pointer, target, position, 0,
            Props(modifiers, PointerUpdateKind.Other, pressure, xTilt, yTilt),
            KeyModifiers.None));

        return $"moved to ({x:F0},{y:F0})";
    }

    /// <summary>Releases the left button at a window point - the end of a drag.</summary>
    public static string Release(Visual root, double x, double y, bool right = false)
    {
        // While a button is down the captured element gets the moves, exactly as a real
        // pointer behaves - otherwise the capture made on press is meaningless.
        Pointer pointer = _activePointer ?? new Pointer(++_pointerId, PointerType.Mouse, true);
        Visual target = pointer.Captured as Visual
            ?? HitTest(root, x, y)
            ?? throw new EditorOperationException($"Nothing is at ({x},{y}).");
        Point position = root.TranslatePoint(new Point(x, y), target) ?? new Point(x, y);

        (target as InputElement)?.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, target, position, 0,
            new PointerPointProperties(RawInputModifiers.None,
                right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, right ? MouseButton.Right : MouseButton.Left));

        // The drag is over, so the next press starts a fresh pointer.
        _activePointer = null;

        return $"released at ({x:F0},{y:F0})";
    }

    public static string Type(Visual root, string text) => Type(Focused(root), text);

    /// <summary>
    /// Types text into a named element, as the keyboard would.
    ///
    /// A batch aims at the canvas, and the focus manager may not have moved there yet inside
    /// the batch's own synchronous pass, so a batch hands its keys to the canvas directly
    /// rather than to whatever <see cref="Focused"/> happens to say.
    /// </summary>
    public static string Type(InputElement target, string text)
    {
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
        => Key(Focused(root), key, modifiers);

    /// <summary>
    /// Sends a key, with modifiers, to a named element.
    ///
    /// A batch uses this to put its shortcut on the canvas without waiting for the focus
    /// manager: raising the event on the window instead is what made a batch's "L" silently
    /// do nothing and its drag select rather than draw.
    /// </summary>
    public static string Key(InputElement target, Key key, KeyModifiers modifiers)
    {
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

    /// <summary>
    /// Gives the target the keyboard focus a real press gives it.
    ///
    /// A real pointer press focuses the first focusable element on the way, which is what makes the
    /// next keystroke land where the person clicked: typing into a field, nudging a shape, Escape out
    /// of an edit. Raising the press by hand skips that step, so keys went to whatever held focus
    /// before - a hex typed into the Fill box went nowhere, and an arrow key did not move the shape -
    /// and neither reported an error.
    ///
    /// The hit test often lands on an *inner* visual - a `TextPresenter` inside a TextBox, an icon
    /// inside a button - so walking up to the first focusable element is the part that matters. A
    /// control that is not focusable is skipped rather than forced, because that is what the input
    /// manager does and a control that refuses focus has a reason.
    /// </summary>
    private static void FocusFor(Visual target)
    {
        for (Visual? v = target; v is not null; v = v.GetVisualParent())
        {
            if (v is InputElement { Focusable: true, IsEffectivelyEnabled: true } element)
            {
                element.Focus(NavigationMethod.Pointer);
                return;
            }
        }
    }

    private static void Press(Control control, Pointer pointer, Point position,
        PointerPointProperties properties, KeyModifiers modifiers, int clickCount)
        => control.RaiseEvent(new PointerPressedEventArgs(
            control, pointer, control, position, 0, properties, modifiers, clickCount));

    private static void Release(Control control, Pointer pointer, Point position,
        KeyModifiers modifiers, bool right = false)
        => control.RaiseEvent(new PointerReleasedEventArgs(
            control, pointer, control, position, 0,
            new PointerPointProperties(RawInputModifiers.None,
                right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased),
            modifiers, right ? MouseButton.Right : MouseButton.Left));

    /// <summary>
    /// The control a real click at a window point would reach.
    ///
    /// This asks Avalonia rather than scanning bounds by hand. The scan took the LAST control
    /// in visual-descendant order whose rectangle contained the point, which is list order and
    /// not z-order - so an internal overlay that covers the window and appears late in the list
    /// always won. That is exactly what happened: with a text field focused, a click aimed at
    /// the colour wheel was delivered to Avalonia's own TextSelectorLayer instead, and the
    /// operation reported success while nothing the person could see was touched.
    ///
    /// A hand-rolled scan cannot know about adorner layers, overlays, or anything else that
    /// sits above the content, and every future one would be another chance to get it wrong.
    /// </summary>
    private static Visual? HitTest(Visual root, double x, double y)
    {
        Point point = new(x, y);

        // Topmost first, so the first hit-testable control is the one on top.
        foreach (Visual visual in root.GetVisualsAt(point))
        {
            if (visual is Control { IsVisible: true, IsHitTestVisible: true } control &&
                control.Bounds.Width > 0 && control.Bounds.Height > 0)
            {
                return Deepest(control, root, point);
            }
        }

        return null;
    }

    /// <summary>
    /// The innermost hit-testable visual under the point, because a routed event travels **up** from
    /// its source and never down.
    ///
    /// Taking the first visual Avalonia reports is not enough. When that is an outer control - and for
    /// a button built from a template it often is - raising the press on it delivers the event to that
    /// control and its ancestors, and every handler on the parts *inside* it is skipped. That is how a
    /// long press on the shape tool produced no flyout while the same press by hand did: the control
    /// that opens the flyout is an inner button, and it never heard about the press.
    ///
    /// The walk stops at the last visual that is both hit-testable and actually contains the point, so
    /// a transparent parent cannot swallow a gesture aimed at a child.
    /// </summary>
    private static Visual Deepest(Visual candidate, Visual root, Point point)
    {
        Visual current = candidate;

        while (true)
        {
            Visual? next = null;
            foreach (Visual child in current.GetVisualChildren())
            {
                if (child is not Control { IsVisible: true, IsHitTestVisible: true } control ||
                    control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                {
                    continue;
                }

                if (root.TranslatePoint(point, control) is { } local &&
                    new Rect(control.Bounds.Size).Contains(local))
                {
                    next = control;
                    break;
                }
            }

            if (next is null)
            {
                return current;
            }

            current = next;
        }
    }
}
