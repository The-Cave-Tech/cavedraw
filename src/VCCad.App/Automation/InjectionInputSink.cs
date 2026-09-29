using VCCad.Core.Input;

namespace VCCad.App.Automation;

/// <summary>
/// Delivers a replayed batch through the same injection path a person's events take.
///
/// This is the whole point of the batch engine: it decides *when* an event arrives and this
/// decides *how*, and the how is the one already in use. A second implementation of input
/// would be a second set of bugs, and the two would drift the moment either changed.
/// </summary>
public static class InjectionInputSink
{
    /// <summary>A sink that injects into a window's real pointer and keyboard path.</summary>
    public static IInputSink For(Avalonia.Visual root) => new Sink(root);

    private sealed class Sink(Avalonia.Visual root) : IInputSink
    {
        public void Send(InputEvent input)
        {
            bool right = string.Equals(input.Button, "right", StringComparison.OrdinalIgnoreCase);

            switch (input.Kind)
            {
                case InputKinds.Down:
                case InputKinds.PenDown:
                case InputKinds.TouchDown:
                    InputInjection.Press(root, input.X, input.Y, input.Extend, right);
                    break;

                case InputKinds.Move:
                case InputKinds.PenMove:
                case InputKinds.TouchMove:
                    InputInjection.Move(root, input.X, input.Y, leftDown: true);
                    break;

                case InputKinds.Up:
                case InputKinds.PenUp:
                case InputKinds.TouchUp:
                    InputInjection.Release(root, input.X, input.Y, right);
                    break;

                case InputKinds.Hover:
                    // A hover is a move with nothing held, which is exactly what it is.
                    InputInjection.Move(root, input.X, input.Y, leftDown: false);
                    break;

                case InputKinds.Wheel:
                    InputInjection.Wheel(root, input.X, input.Y, input.WheelDelta ?? 0);
                    break;

                case InputKinds.KeyDown:
                case InputKinds.KeyUp:
                case InputKinds.Text:
                    // Text goes through the platform's text-input path and a key through the
                    // key path, because that is the distinction the platform itself makes -
                    // a batch recorded from a person carries them as separate events.
                    if (input.Kind == InputKinds.Text && input.Text is { Length: > 0 } literal)
                    {
                        InputInjection.Type(root, literal);
                    }
                    else if (ParseKey(input.Key) is { } key)
                    {
                        InputInjection.Key(root, key, ParseModifiers(input.Modifiers));
                    }

                    break;

                // Enter/leave and hover-out move nothing on screen, so there is nothing to
                // inject for them yet. Listed rather than defaulted so a future event kind
                // cannot be silently dropped.
                case InputKinds.Enter:
                case InputKinds.Leave:
                case InputKinds.HoverOut:
                    break;
            }
        }
    }

    /// <summary>A key name as the platform's key, or null when it is not one.</summary>
    private static Avalonia.Input.Key? ParseKey(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           Enum.TryParse(name, ignoreCase: true, out Avalonia.Input.Key key)
            ? key
            : null;

    /// <summary>Modifier names joined by '+' as the platform's flags.</summary>
    private static Avalonia.Input.KeyModifiers ParseModifiers(string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return Avalonia.Input.KeyModifiers.None;
        }

        Avalonia.Input.KeyModifiers result = Avalonia.Input.KeyModifiers.None;

        foreach (string part in names.Split('+', StringSplitOptions.RemoveEmptyEntries |
                                                   StringSplitOptions.TrimEntries))
        {
            string name = part.Equals("Cmd", StringComparison.OrdinalIgnoreCase) ||
                          part.Equals("Command", StringComparison.OrdinalIgnoreCase) ||
                          part.Equals("Meta", StringComparison.OrdinalIgnoreCase)
                ? "Meta"
                : part;

            if (Enum.TryParse(name, ignoreCase: true, out Avalonia.Input.KeyModifiers one))
            {
                result |= one;
            }
        }

        return result;
    }
}
