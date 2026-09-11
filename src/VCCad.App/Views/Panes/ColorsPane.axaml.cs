using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using ModelFillRule = VCCad.Core.Model.FillRule;

namespace VCCad.App.Views.Panes;

/// <summary>Colors tab: fill (RGB + rule) and stroke colour for the selection.</summary>
public partial class ColorsPane : UserControl
{
    private EditorViewModel? _vm;

    public ColorsPane()
    {
        InitializeComponent();
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (_vm?.PrimarySelection is not PathItem path)
        {
            foreach (TextBox box in new[] { FillR, FillG, FillB, StrokeR, StrokeG, StrokeB })
            {
                SetBox(box, null);
            }

            return;
        }

        SetBox(FillR, Math.Round(path.Fill.Color.R * 255));
        SetBox(FillG, Math.Round(path.Fill.Color.G * 255));
        SetBox(FillB, Math.Round(path.Fill.Color.B * 255));
        FillRuleBox.SelectedIndex = path.Fill.Rule == ModelFillRule.EvenOdd ? 1 : 0;

        SetBox(StrokeR, Math.Round(path.Stroke.Color.R * 255));
        SetBox(StrokeG, Math.Round(path.Stroke.Color.G * 255));
        SetBox(StrokeB, Math.Round(path.Stroke.Color.B * 255));
    }

    private void SetBox(TextBox box, double? value)
    {
        if (!box.IsFocused)
        {
            box.Text = value.HasValue ? value.Value.ToString("0", CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    private static ColorRgb ReadColor(TextBox r, TextBox g, TextBox b)
    {
        byte Channel(TextBox box)
            => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? (byte)Math.Clamp(Math.Round(v), 0, 255)
                : (byte)0;

        return ColorRgb.FromBytes(Channel(r), Channel(g), Channel(b));
    }

    private void OnApplyFill(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        ModelFillRule rule = FillRuleBox.SelectedIndex == 1 ? ModelFillRule.EvenOdd : ModelFillRule.NonZero;
        _vm.ApplyFill(ReadColor(FillR, FillG, FillB), rule);
    }

    private void OnApplyStrokeColor(object? sender, RoutedEventArgs e)
    {
        _vm?.ApplyStrokeColor(ReadColor(StrokeR, StrokeG, StrokeB));
    }
}
