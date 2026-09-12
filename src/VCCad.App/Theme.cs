using Avalonia;
using Avalonia.Controls;

namespace VCCad.App;

/// <summary>
/// Shared UI settings. The font size is a single variable (the "VcFontSize"
/// application resource) so the whole program can be rescaled from one place —
/// XAML uses <c>{DynamicResource VcFontSize}</c> and code reads <see cref="FontSize"/>.
/// </summary>
public static class EditorTheme
{
    /// <summary>Global font size used by every label/control in the editor.</summary>
    public static double FontSize
    {
        get
        {
            if (Application.Current?.TryFindResource("VcFontSize", out object? value) == true && value is double size)
            {
                return size;
            }

            return 10.4;
        }
    }
}
