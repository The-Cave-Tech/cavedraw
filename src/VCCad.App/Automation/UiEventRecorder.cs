using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace VCCad.App.Automation;

/// <summary>
/// Records what a person does in the window: pointer presses, drags, drops, wheel
/// gestures and keystrokes — including hover, so the diary shows what they were
/// looking at as well as what they clicked.
///
/// The recorder taps the tunneling phase on the window, so it sees every event
/// before any control can handle it, and never changes behaviour: nothing is
/// marked handled and no state is touched.
///
/// Volume is controlled rather than the content: a hover is only recorded when the
/// control under the pointer changes (or after a quiet period), drag moves are
/// sampled, and sub-pixel jitter is ignored. A person moving the mouse for a minute
/// therefore produces a few hundred entries, not tens of thousands.
/// </summary>
public sealed class UiEventRecorder : IDisposable
{
    /// <summary>Minimum gap between sampled drag positions.</summary>
    private const int DragSampleMs = 60;

    /// <summary>Minimum gap between hover records on the same control.</summary>
    private const int HoverResampleMs = 1500;

    /// <summary>Movement below this (device-independent px) is jitter, not intent.</summary>
    private const double JitterPixels = 2.0;

    /// <summary>Travel that turns a press into a drag.</summary>
    private const double DragThresholdPixels = 4.0;

    private readonly InteractionLog _log;
    private readonly Visual _root;
    private readonly DateTime _started = DateTime.UtcNow;

    private string? _hoverTarget;
    private DateTime _hoverAt = DateTime.MinValue;

    private Point? _pressPoint;
    private IPointer? _pressedPointer;
    private string? _pressTarget;
    private bool _dragging;
    private DateTime _dragSampleAt = DateTime.MinValue;

    private string? _lastKey;
    private DateTime _lastKeyAt = DateTime.MinValue;

    private UiEventRecorder(InteractionLog log, Visual root)
    {
        _log = log;
        _root = root;
    }

    /// <summary>Starts recording events for <paramref name="window"/>.</summary>
    public static UiEventRecorder Attach(Window window, InteractionLog log)
    {
        var recorder = new UiEventRecorder(log, window);

        window.AddHandler(InputElement.PointerPressedEvent, recorder.OnPointerPressed, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerReleasedEvent, recorder.OnPointerReleased, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerMovedEvent, recorder.OnPointerMoved, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, recorder.OnWheel, RoutingStrategies.Tunnel, true);
        window.AddHandler(InputElement.KeyDownEvent, recorder.OnKeyDown, RoutingStrategies.Tunnel, true);
        window.AddHandler(DragDrop.DragEnterEvent, recorder.OnDragEnter, RoutingStrategies.Tunnel, true);
        window.AddHandler(DragDrop.DropEvent, recorder.OnDrop, RoutingStrategies.Tunnel, true);

        // Pointer capture (dragging off a control) and focus moves are context that
        // makes the rest of a session readable.
        window.AddHandler(InputElement.GotFocusEvent, recorder.OnFocus, RoutingStrategies.Tunnel, true);

        log.Record(InteractionKind.System, InteractionCategory.Note, "ui.recorder.attached",
            target: window.GetType().Name,
            details: $"recording pointer, keyboard and drag events for {window.Title}",
            tags: new[] { "recorder" });

        return recorder;
    }

    /// <summary>Stops recording.</summary>
    public void Dispose()
    {
        if (_root is InputElement element)
        {
            element.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            element.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            element.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            element.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            element.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            element.RemoveHandler(DragDrop.DragEnterEvent, OnDragEnter);
            element.RemoveHandler(DragDrop.DropEvent, OnDrop);
            element.RemoveHandler(InputElement.GotFocusEvent, OnFocus);
        }
    }

    // ------------------------------------------------------------------
    // Pointer
    // ------------------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(_root);
        _pressedPointer = e.Pointer;
        _pressTarget = Describe(e.Source as Visual);
        _dragging = false;

        _log.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.press",
            target: _pressTarget,
            details: string.Join(", ",
                FormatButton(e), $"modifiers {FormatModifiers(e.KeyModifiers)}", $"at {FormatPoint(_pressPoint.Value)}"),
            tags: new[] { "click" });
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Point position = e.GetPosition(_root);
        string target = Describe(e.Source as Visual);

        if (_dragging && _pressPoint is { } start)
        {
            _log.Record(InteractionKind.Ui, InteractionCategory.DragDrop, "pointer.drop",
                target: target,
                details: $"drag from {FormatPoint(start)} ({_pressTarget ?? "?"}) to {FormatPoint(position)}, " +
                         $"distance {Math.Round(Distance(start, position), 1)}",
                tags: new[] { "drag", "drop" });
        }
        else
        {
            double travelled = _pressPoint is { } p ? Distance(p, position) : 0;
            _log.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.release",
                target: target,
                details: $"at {FormatPoint(position)}, moved {Math.Round(travelled, 1)}",
                tags: new[] { "click" });
        }

        _pressPoint = null;
        _pressedPointer = null;
        _pressTarget = null;
        _dragging = false;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point position = e.GetPosition(_root);
        Visual? source = e.Source as Visual;

        if (_pressedPointer is not null && _pressPoint is { } start)
        {
            double travelled = Distance(start, position);
            if (travelled >= DragThresholdPixels)
            {
                if (!_dragging)
                {
                    _dragging = true;
                    _log.Record(InteractionKind.Ui, InteractionCategory.DragDrop, "pointer.drag.start",
                        target: _pressTarget,
                        details: $"from {FormatPoint(start)}",
                        tags: new[] { "drag" });
                }

                if ((DateTime.UtcNow - _dragSampleAt).TotalMilliseconds >= DragSampleMs)
                {
                    _dragSampleAt = DateTime.UtcNow;
                    _log.Record(InteractionKind.Ui, InteractionCategory.DragDrop, "pointer.drag",
                        target: Describe(source),
                        details: $"to {FormatPoint(position)}, travelled {Math.Round(travelled, 1)}",
                        tags: new[] { "drag" });
                }

                return;
            }
        }

        // Hover: only when the control under the pointer changes, or occasionally to
        // show that attention stayed put. Recording every move would swamp the diary.
        string hovered = Describe(source);
        bool changed = !string.Equals(hovered, _hoverTarget, StringComparison.Ordinal);
        if (changed || (DateTime.UtcNow - _hoverAt).TotalMilliseconds >= HoverResampleMs)
        {
            if (changed && _hoverTarget is not null)
            {
                _log.Record(InteractionKind.Ui, InteractionCategory.Hover, "pointer.hover.out",
                    target: _hoverTarget, details: "pointer left", tags: new[] { "hover" });
            }

            _hoverTarget = hovered;
            _hoverAt = DateTime.UtcNow;
            _log.Record(InteractionKind.Ui, InteractionCategory.Hover, "pointer.hover",
                target: hovered,
                details: $"at {FormatPoint(position)}",
                tags: new[] { "hover" });
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        _log.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.wheel",
            target: Describe(e.Source as Visual),
            details: $"delta {Math.Round(e.Delta.Y, 2)} at {FormatPoint(e.GetPosition(_root))}" +
                     (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? " with Ctrl (zoom)" : string.Empty),
            tags: new[] { "wheel" });
    }

    // ------------------------------------------------------------------
    // Keyboard
    // ------------------------------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Auto-repeat and held modifiers would otherwise fill the diary.
        string key = FormatKey(e);
        if (string.Equals(key, _lastKey, StringComparison.Ordinal) &&
            (DateTime.UtcNow - _lastKeyAt).TotalMilliseconds < 400)
        {
            return;
        }

        _lastKey = key;
        _lastKeyAt = DateTime.UtcNow;

        _log.Record(InteractionKind.Ui, InteractionCategory.Key, "key.down",
            target: Describe(e.Source as Visual),
            details: key,
            tags: e.KeyModifiers == KeyModifiers.None ? new[] { "key" } : new[] { "key", "shortcut" });
    }

    private void OnFocus(object? sender, GotFocusEventArgs e)
    {
        _log.Record(InteractionKind.Ui, InteractionCategory.Pointer, "focus",
            target: Describe(e.Source as Visual),
            details: e.NavigationMethod.ToString(),
            tags: new[] { "focus" });
    }

    // ------------------------------------------------------------------
    // Drag and drop (files from the desktop, objects inside the app)
    // ------------------------------------------------------------------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        _log.Record(InteractionKind.Ui, InteractionCategory.DragDrop, "drag.enter",
            target: Describe(e.Source as Visual),
            details: DescribeDragData(e),
            tags: new[] { "drag" });
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _log.Record(InteractionKind.Ui, InteractionCategory.DragDrop, "drag.drop",
            target: Describe(e.Source as Visual),
            details: $"{DescribeDragData(e)} at {FormatPoint(e.GetPosition(_root))}",
            tags: new[] { "drag", "drop" });
    }

    private static string DescribeDragData(DragEventArgs e)
    {
        try
        {
            string[] formats = e.Data.GetDataFormats().ToArray();
            string? files = e.Data.Contains(DataFormats.Files)
                ? string.Join(", ", (e.Data.GetFiles() ?? Enumerable.Empty<IStorageItem>()).Select(f => f.Name))
                : null;
            string? text = e.Data.Contains(DataFormats.Text) ? e.Data.GetText() : null;
            return $"formats [{string.Join(", ", formats)}]" +
                   (string.IsNullOrEmpty(files) ? string.Empty : $" files [{files}]") +
                   (string.IsNullOrEmpty(text) ? string.Empty : $" text [{text}]");
        }
        catch (Exception ex)
        {
            return $"unreadable drag data: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------
    // Formatting
    // ------------------------------------------------------------------

    /// <summary>
    /// Names the control an event landed on, preferring text a person would
    /// recognise ("Button \"Zoom in\"") over a type name.
    /// </summary>
    private static string Describe(Visual? source)
    {
        if (source is null)
        {
            return "window";
        }

        string type = source.GetType().Name;
        string? name = (source as Control)?.Name;
        string? text = VisualTreeDump.TextOf(source);

        if (!string.IsNullOrWhiteSpace(text))
        {
            return name is null ? $"{type} \"{Trim(text)}\"" : $"{type}#{name} \"{Trim(text)}\"";
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"{type}#{name}";
        }

        // Walk up to the nearest named ancestor so bare shapes/presenters are still
        // identifiable.
        for (Visual? parent = source.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            if (parent is Control { Name: { Length: > 0 } ancestorName } ancestor)
            {
                return $"{type} in {ancestor.GetType().Name}#{ancestorName}";
            }
        }

        return type;
    }

    private static string Trim(string text)
        => text.Length <= 40 ? text : text[..40] + "…";

    private string FormatButton(PointerPressedEventArgs e)
    {
        var parts = new List<string>();
        PointerPointProperties properties = e.GetCurrentPoint(_root).Properties;
        if (properties.IsLeftButtonPressed) parts.Add("left");
        if (properties.IsRightButtonPressed) parts.Add("right");
        if (properties.IsMiddleButtonPressed) parts.Add("middle");
        return parts.Count == 0 ? "button" : string.Join("+", parts);
    }

    private static string FormatModifiers(KeyModifiers modifiers)
        => modifiers == KeyModifiers.None ? "-" : modifiers.ToString();

    private static string FormatKey(KeyEventArgs e)
    {
        string key = e.Key.ToString();
        string text = e.KeySymbol is { Length: > 0 } symbol && e.KeyModifiers == KeyModifiers.None
            ? $" ('{symbol}')"
            : string.Empty;
        string modifiers = e.KeyModifiers == KeyModifiers.None ? string.Empty : e.KeyModifiers + "+";
        return $"{modifiers}{key}{text}";
    }

    private static string FormatPoint(Point point) => $"({Math.Round(point.X)},{Math.Round(point.Y)})";

    private static double Distance(Point a, Point b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How long this recorder has been attached (diagnostics).</summary>
    public TimeSpan Uptime => DateTime.UtcNow - _started;
}
