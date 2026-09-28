using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace VCCad.App.Automation;

/// <summary>
/// A control located in the live visual tree, with enough information for a model
/// (or a person reading a log) to identify and act on it.
/// </summary>
/// <param name="Index">Stable position in the flattened tree; the handle to use.</param>
/// <param name="Type">Control type name, e.g. <c>MenuItem</c>.</param>
/// <param name="Name">x:Name, when set.</param>
/// <param name="Text">Displayed text, when any.</param>
/// <param name="Bounds">Bounds relative to the window.</param>
/// <param name="IsEnabled">Whether the control accepts input.</param>
/// <param name="IsVisible">Whether the control is visible.</param>
/// <param name="Depth">Nesting depth, for readability.</param>
public sealed record UiControlRef(
    int Index,
    string Type,
    string? Name,
    string? Text,
    Rect Bounds,
    bool IsEnabled,
    bool IsVisible,
    int Depth)
{
    /// <summary>One-line form used in operation results.</summary>
    public string Describe()
    {
        string name = Name is null ? string.Empty : "#" + Name;
        string text = Text is null ? string.Empty : " \"" + Text + "\"";
        string flags = (IsVisible ? string.Empty : " hidden") + (IsEnabled ? string.Empty : " disabled");
        return $"[{Index}] {Type}{name}{text} " +
               $"({Bounds.X:F0},{Bounds.Y:F0} {Bounds.Width:F0}x{Bounds.Height:F0}){flags}";
    }
}

/// <summary>
/// Point-and-click automation: find, click, type into and set values on the real
/// controls of the running window.
///
/// The project rule is that nothing may be reachable only through the UI. Menu
/// items, toolbar buttons, dialogs and panes are part of the product surface, so
/// they are automatable too — a driver must be able to open the File menu and
/// choose Import exactly as a person would.
///
/// Actions raise the control's own Click/invoke path (or open a submenu), which is
/// the same code a real pointer press reaches. Synthetic OS-level input is not
/// used: it needs a real cursor, is timing-dependent, and would make automated
/// runs flaky.
/// </summary>
public static class UiAutomation
{
    /// <summary>
    /// Flattens the window's controls in visual order, including menu entries.
    ///
    /// A menu's items are rendered in a popup, which Avalonia hosts in a separate
    /// top-level visual tree — walking the window alone never sees "File → Import".
    /// The entries are still logical children of their parent <see cref="MenuItem"/>,
    /// so they are pulled in explicitly and stay addressable whether or not the menu
    /// has been opened.
    /// </summary>
    public static IReadOnlyList<Visual> Flatten(Visual root)
    {
        var list = new List<Visual>();
        Walk(root, list);

        var seen = new HashSet<Visual>(list);
        foreach (MenuItem menu in list.OfType<MenuItem>().ToArray())
        {
            AddMenuItems(menu, list, seen);
        }

        return list;
    }

    private static void AddMenuItems(MenuItem parent, List<Visual> into, HashSet<Visual> seen)
    {
        foreach (object? entry in parent.Items)
        {
            if (entry is MenuItem child && seen.Add(child))
            {
                into.Add(child);
                AddMenuItems(child, into, seen);
            }
        }
    }

    private static void Walk(Visual visual, List<Visual> into)
    {
        into.Add(visual);
        foreach (Visual child in visual.GetVisualChildren())
        {
            Walk(child, into);
        }
    }

    /// <summary>Describes one visual the way <c>ui.find</c> reports it.</summary>
    public static UiControlRef Describe(Visual root, Visual visual)
    {
        IReadOnlyList<Visual> all = Flatten(root);
        int index = -1;
        for (int i = 0; i < all.Count; i++)
        {
            if (ReferenceEquals(all[i], visual))
            {
                index = i;
                break;
            }
        }

        int depth = 0;
        for (Visual? v = visual.GetVisualParent(); v is not null; v = v.GetVisualParent())
        {
            depth++;
        }

        string? name = (visual as Control)?.Name;
        string? text = VisualTreeDump.TextOf(visual);
        Rect bounds = visual.Bounds;
        try
        {
            Point? origin = visual.TranslatePoint(default, root);
            if (origin is { } p)
            {
                bounds = new Rect(p, visual.Bounds.Size);
            }
        }
        catch (InvalidOperationException)
        {
            // Translation is unavailable (not attached); keep the local bounds.
        }

        return new UiControlRef(
            index,
            visual.GetType().Name,
            string.IsNullOrEmpty(name) ? null : name,
            text,
            bounds,
            visual is Control { IsEnabled: true },
            visual is Control { IsVisible: true },
            depth);
    }

    /// <summary>
    /// Finds controls matching the given criteria. Empty criteria match everything;
    /// callers normally pass a type, a name, or a text fragment.
    /// </summary>
    public static IReadOnlyList<(Visual Visual, UiControlRef Ref)> Find(
        Visual root,
        string? type = null,
        string? name = null,
        string? text = null,
        bool includeHidden = true,
        int max = 100)
    {
        IReadOnlyList<Visual> all = Flatten(root);
        var results = new List<(Visual, UiControlRef)>();

        for (int i = 0; i < all.Count; i++)
        {
            Visual visual = all[i];
            var control = visual as Control;
            if (!includeHidden && control is not { IsVisible: true })
            {
                continue;
            }

            if (type is not null &&
                !visual.GetType().Name.Contains(type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name is not null &&
                !string.Equals(control?.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (text is not null)
            {
                string? content = VisualTreeDump.TextOf(visual);
                if (content is null || !content.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            results.Add((visual, Describe(root, visual)));
            if (results.Count >= max)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Acts on a control the way a click would: a submenu opens, a button or menu
    /// item raises its own Click, a toggle flips. Returns a short description of
    /// what happened.
    /// </summary>
    public static string Click(Visual visual, bool openMenu = true)
    {
        switch (visual)
        {
            case MenuItem menu when openMenu && menu.HasSubMenu:
                menu.Open();
                return $"opened menu \"{VisualTreeDump.TextOf(menu) ?? menu.Name ?? "?"}\"";

            case MenuItem menu:
                if (menu.Command is { } menuCommand && menuCommand.CanExecute(menu.CommandParameter))
                {
                    menuCommand.Execute(menu.CommandParameter);
                    return $"invoked menu item \"{VisualTreeDump.TextOf(menu) ?? menu.Name ?? "?"}\" (command)";
                }

                menu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                return $"clicked menu item \"{VisualTreeDump.TextOf(menu) ?? menu.Name ?? "?"}\"";

            case ToggleButton toggle:
                toggle.IsChecked = toggle.IsChecked != true;
                return $"toggled {(toggle.Name ?? toggle.GetType().Name)} to {toggle.IsChecked}";

            // A person can click a dropdown to see what is in it, so a driver can too.
            // Refusing here made every ComboBox — including the font list — unreachable
            // by clicking, which is exactly how you choose a font.
            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return $"{(combo.IsDropDownOpen ? "opened" : "closed")} dropdown " +
                       $"{combo.Name ?? combo.GetType().Name}";

            case Button button:
                if (button.Command is { } command && command.CanExecute(button.CommandParameter))
                {
                    command.Execute(button.CommandParameter);
                    return $"invoked button \"{VisualTreeDump.TextOf(button) ?? button.Name ?? "?"}\" (command)";
                }

                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return $"clicked button \"{VisualTreeDump.TextOf(button) ?? button.Name ?? "?"}\"";

            case ListBox list:
                if (list.ItemCount > 0)
                {
                    list.SelectedIndex = Math.Min(1, list.ItemCount - 1);
                    return $"selected item {list.SelectedIndex} in list";
                }

                return "list is empty";

            case Control control:
                throw new EditorOperationException(
                    $"{control.GetType().Name} is not directly clickable. Use ui.setValue for inputs, " +
                    "or ui.keys for keyboard shortcuts.");

            default:
                throw new EditorOperationException("That visual is not a control.");
        }
    }

    /// <summary>Sets a value on an input control, as typing/choosing would.</summary>
    public static string SetValue(Visual visual, string value)
    {
        switch (visual)
        {
            case TextBox box:
                box.Text = value;
                box.CaretIndex = box.Text?.Length ?? 0;
                return $"set text of {(box.Name ?? "TextBox")} to \"{value}\"";

            case ToggleButton toggle:
                toggle.IsChecked = ParseBool(value);
                return $"set {(toggle.Name ?? "toggle")} to {toggle.IsChecked}";

            case Slider slider:
                slider.Value = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                return $"set slider to {slider.Value}";

            case NumericUpDown numeric:
                numeric.Value = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                return $"set numeric to {numeric.Value}";

            case ComboBox combo:
                if (int.TryParse(value, out int index) && index >= 0 && index < combo.ItemCount)
                {
                    combo.SelectedIndex = index;
                }
                else
                {
                    combo.SelectedItem = value;
                }

                return $"set combo selection to {combo.SelectedItem ?? "(none)"}";

            default:
                throw new EditorOperationException(
                    $"{visual.GetType().Name} does not accept a value. Use ui.click for buttons and menus.");
        }
    }

    /// <summary>Raises a keyboard shortcut on the focused control (or the window).</summary>
    public static string PressKeys(Visual root, string keys)
    {
        (Key key, KeyModifiers modifiers) = ParseKeys(keys);

        // Send to the focused control, not the window: a routed KeyDown raised on the
        // window bubbles up and never reaches the canvas that is waiting for it, so
        // shortcuts like Escape would silently do nothing.
        var target = Avalonia.Controls.TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement()
            as InputElement ?? root as InputElement;
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        };

        if (target is not null)
        {
            target.RaiseEvent(args);
            return $"pressed {keys} on {target.GetType().Name}";
        }

        throw new EditorOperationException("The window cannot receive key events.");
    }

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("1", StringComparison.Ordinal) ||
           value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("checked", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses <c>Ctrl+Shift+S</c> style shortcuts.</summary>
    private static (Key Key, KeyModifiers Modifiers) ParseKeys(string spec)
    {
        KeyModifiers modifiers = KeyModifiers.None;
        Key key = Key.None;

        foreach (string part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= KeyModifiers.Control; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "alt": modifiers |= KeyModifiers.Alt; break;
                case "win" or "meta": modifiers |= KeyModifiers.Meta; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        throw new EditorOperationException($"Unknown key '{part}' in '{spec}'.");
                    }

                    break;
            }
        }

        if (key == Key.None)
        {
            throw new EditorOperationException($"No key in shortcut '{spec}'.");
        }

        return (key, modifiers);
    }

    /// <summary>Renders a compact report of a control list for a model to read.</summary>
    public static string Report(IEnumerable<UiControlRef> controls)
    {
        var sb = new StringBuilder();
        foreach (UiControlRef control in controls)
        {
            sb.Append(control.Describe()).Append('\n');
        }

        return sb.ToString();
    }
}
