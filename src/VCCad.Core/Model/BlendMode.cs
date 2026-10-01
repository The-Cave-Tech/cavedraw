namespace VCCad.Core.Model;

/// <summary>
/// How an object's colour is combined with what is already drawn beneath it.
///
/// The names are CSS's `mix-blend-mode` values, because that is what an SVG file writes and what SVG's `feBlend`
/// uses - so nothing has to be translated between the file, the model and the filter engine. `Normal` is the
/// default and is what an object with no blend mode has; the model stores it rather than a nullable so callers do
/// not have to decide what "no blend mode" means.
/// </summary>
public enum BlendMode
{
    /// <summary>The source replaces the backdrop where it is opaque - the ordinary painting rule.</summary>
    Normal,

    Multiply,
    Screen,
    Darken,
    Lighten,
    Overlay,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

/// <summary>Reading and writing <see cref="BlendMode"/> as the names a file uses.</summary>
public static class BlendModes
{
    private static readonly Dictionary<string, BlendMode> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = BlendMode.Normal,
        ["multiply"] = BlendMode.Multiply,
        ["screen"] = BlendMode.Screen,
        ["darken"] = BlendMode.Darken,
        ["lighten"] = BlendMode.Lighten,
        ["overlay"] = BlendMode.Overlay,
        ["color-dodge"] = BlendMode.ColorDodge,
        ["color-burn"] = BlendMode.ColorBurn,
        ["hard-light"] = BlendMode.HardLight,
        ["soft-light"] = BlendMode.SoftLight,
        ["difference"] = BlendMode.Difference,
        ["exclusion"] = BlendMode.Exclusion,
        ["hue"] = BlendMode.Hue,
        ["saturation"] = BlendMode.Saturation,
        ["color"] = BlendMode.Color,
        ["luminosity"] = BlendMode.Luminosity,
    };

    /// <summary>
    /// The mode a file's name means, or null when it is not one this build knows.
    ///
    /// Null rather than a guess at `Normal`: an unknown blend mode silently painted normally is a picture that is
    /// wrong in a way nothing reports, and the reader's caller can decide to say so.
    /// </summary>
    public static BlendMode? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // CSS's initial value: inherit the blending of the group, which is no blending at all for a leaf here.
            return BlendMode.Normal;
        }

        return ByName.TryGetValue(trimmed, out BlendMode mode) ? mode : null;
    }

    /// <summary>The name a file writes for this mode.</summary>
    public static string ToSvgName(this BlendMode mode) => mode switch
    {
        BlendMode.Normal => "normal",
        BlendMode.Multiply => "multiply",
        BlendMode.Screen => "screen",
        BlendMode.Darken => "darken",
        BlendMode.Lighten => "lighten",
        BlendMode.Overlay => "overlay",
        BlendMode.ColorDodge => "color-dodge",
        BlendMode.ColorBurn => "color-burn",
        BlendMode.HardLight => "hard-light",
        BlendMode.SoftLight => "soft-light",
        BlendMode.Difference => "difference",
        BlendMode.Exclusion => "exclusion",
        BlendMode.Hue => "hue",
        BlendMode.Saturation => "saturation",
        BlendMode.Color => "color",
        _ => "luminosity",
    };
}
