using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>Swatches tab: a grid of preset colours applied as fill to the selection.</summary>
public sealed class SwatchesPane : UserControl
{
    private EditorViewModel? _vm;

    private static readonly (string Name, byte R, byte G, byte B)[] Palette =
    {
        ("Black", 0, 0, 0), ("White", 255, 255, 255), ("Gray", 128, 128, 128),
        ("Red", 220, 50, 50), ("Orange", 235, 140, 40), ("Yellow", 240, 210, 60),
        ("Green", 60, 170, 90), ("Teal", 50, 170, 170), ("Blue", 60, 130, 220),
        ("Indigo", 90, 90, 200), ("Purple", 150, 80, 190), ("Pink", 220, 110, 170),
        ("Brown", 140, 95, 60), ("Tan", 210, 175, 130), ("Navy", 30, 50, 100),
    };

    public SwatchesPane()
    {
        var wrap = new WrapPanel { Margin = new Thickness(10, 8) };
        foreach ((string name, byte r, byte g, byte b) in Palette)
        {
            var button = new Button
            {
                Width = 34,
                Height = 26,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
            };
            ColorRgb color = ColorRgb.FromBytes(r, g, b);
            ToolTip.SetTip(button, name);
            button.Click += (_, _) => _vm?.ApplyFill(color, VCCad.Core.Model.FillRule.NonZero);
            wrap.Children.Add(button);
        }

        Content = wrap;
    }

    public void Attach(EditorViewModel vm) => _vm = vm;
}
