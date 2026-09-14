using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using ModelFillRule = VCCad.Core.Model.FillRule;

namespace VCCad.App.Views.Panes;

/// <summary>Colour tab: a circular spectrum picker (hue ring + saturation +
/// value), hex/RGB entry, and apply-to-fill/stroke actions.</summary>
public partial class ColorsPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;
    private bool _strokeTarget;

    public ColorsPane()
    {
        InitializeComponent();
        Wheel.ColorChanged += (_, _) => OnWheelChanged();
        Wheel.ColorCommitted += (_, _) => CommitWheelColor();
        TargetSelector.TargetChanged += (_, stroke) =>
        {
            _strokeTarget = stroke;
            LoadTargetColor();
        };
        TargetSelector.ClearRequested += (_, stroke) =>
        {
            if (stroke)
            {
                _vm?.ClearStroke();
            }
            else
            {
                _vm?.ClearFill();
            }

            Refresh();
        };
        ValueSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && !_syncing)
            {
                Wheel.Value = ValueSlider.Value / 100.0;
                OnWheelChanged();
            }
        };
        HexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
        };
        HexBox.LostFocus += (_, _) => ApplyHex();
        foreach (TextBox box in new[] { FillR, FillG, FillB })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyRgb();
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => ApplyRgb();
        }
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
            return;
        }

        TargetSelector.SetState(path.Fill.Color, path.Fill.IsVisible,
            path.Stroke.Color, path.Stroke.IsVisible, _strokeTarget);
        LoadTargetColor();
    }

    /// <summary>Points the wheel at the active target's colour.</summary>
    private void LoadTargetColor()
    {
        if (_vm?.PrimarySelection is not PathItem path)
        {
            return;
        }

        TargetSelector.SetState(path.Fill.Color, path.Fill.IsVisible,
            path.Stroke.Color, path.Stroke.IsVisible, _strokeTarget);

        _syncing = true;
        Wheel.SetColor(_strokeTarget ? path.Stroke.Color : path.Fill.Color);
        ValueSlider.Value = Wheel.Value * 100;
        _syncing = false;
        UpdateReadouts();
    }

    /// <summary>Applies the chosen colour to the active target (one undo step).</summary>
    private void CommitWheelColor()
    {
        if (_vm is null || _syncing || _vm.PrimarySelection is not PathItem)
        {
            return;
        }

        if (_strokeTarget)
        {
            _vm.ApplyStrokeColor(Wheel.Color);
        }
        else
        {
            ModelFillRule rule = FillRuleBox.SelectedIndex == 1 ? ModelFillRule.EvenOdd : ModelFillRule.NonZero;
            _vm.ApplyFill(Wheel.Color, rule);
        }
    }

    private void OnWheelChanged()
    {
        if (!_syncing)
        {
            UpdateReadouts();
        }
    }

    private void UpdateReadouts()
    {
        ColorRgb color = Wheel.Color;
        _syncing = true;
        SetBox(FillR, Math.Round(color.R * 255));
        SetBox(FillG, Math.Round(color.G * 255));
        SetBox(FillB, Math.Round(color.B * 255));
        if (!HexBox.IsFocused)
        {
            HexBox.Text = $"#{(byte)Math.Round(color.R * 255):X2}" +
                          $"{(byte)Math.Round(color.G * 255):X2}" +
                          $"{(byte)Math.Round(color.B * 255):X2}";
        }

        _syncing = false;
        Preview.Background = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Round(color.R * 255),
            (byte)Math.Round(color.G * 255),
            (byte)Math.Round(color.B * 255)));
    }

    private void SetBox(TextBox box, double value)
    {
        if (!box.IsFocused)
        {
            box.Text = value.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    private void ApplyHex()
    {
        string text = (HexBox.Text ?? string.Empty).Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            var color = ColorRgb.FromBytes((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            _syncing = true;
            Wheel.SetColor(color);
            ValueSlider.Value = Wheel.Value * 100;
            _syncing = false;
            UpdateReadouts();
        }
    }

    private void ApplyRgb()
    {
        if (_syncing)
        {
            return;
        }

        var color = ColorRgb.FromBytes(
            ParseByte(FillR), ParseByte(FillG), ParseByte(FillB));
        _syncing = true;
        Wheel.SetColor(color);
        ValueSlider.Value = Wheel.Value * 100;
        _syncing = false;
        UpdateReadouts();
    }

    private static byte ParseByte(TextBox box)
        => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? (byte)Math.Clamp(Math.Round(v), 0, 255)
            : (byte)0;

    private void OnApplyFill(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        ModelFillRule rule = FillRuleBox.SelectedIndex == 1 ? ModelFillRule.EvenOdd : ModelFillRule.NonZero;
        _vm.ApplyFill(Wheel.Color, rule);
    }

    private void OnApplyStrokeColor(object? sender, RoutedEventArgs e)
    {
        _vm?.ApplyStrokeColor(Wheel.Color);
    }
}
