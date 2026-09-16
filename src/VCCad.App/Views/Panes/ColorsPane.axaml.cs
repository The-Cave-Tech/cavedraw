using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Commands;
using ModelFillRule = VCCad.Core.Model.FillRule;

namespace VCCad.App.Views.Panes;

/// <summary>
/// Colour tab: a circular spectrum picker (hue ring + saturation + value) and a
/// fill/stroke target diagram. There are no Apply buttons — the active target is
/// chosen on the diagram, and colour changes apply immediately. Live dragging
/// mutates the model directly for instant feedback and commits a single undo step
/// when the interaction finishes.
/// </summary>
public partial class ColorsPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;
    private bool _strokeTarget;

    private List<(PathItem Path, FillSpec Before)>? _fillBefore;
    private List<(PathItem Path, StrokeSpec Before)>? _strokeBefore;

    public ColorsPane()
    {
        InitializeComponent();
        Wheel.ColorChanged += (_, _) => OnWheelChanged();
        Wheel.ColorCommitted += (_, _) => CommitLive();
        ValueSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && !_syncing)
            {
                Wheel.Value = ValueSlider.Value / 100.0;
            }
        };
        FillRuleBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
            {
                ApplyLive();
                CommitLive();
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
        ValueSlider.PointerReleased += (_, _) => CommitLive();
        ValueSlider.LostFocus += (_, _) => CommitLive();
        foreach (TextBox box in new[] { FillR, FillG, FillB, AlphaBox })
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

        TargetSelector.TargetChanged += (_, stroke) =>
        {
            _strokeTarget = stroke;
            ResetBefore();
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
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => { Refresh(); RefreshSwatches(); };
        vm.TransformChanged += (_, _) => RefreshSwatches();
        vm.SelectionChanged += (_, _) => Refresh();
        Refresh();
        RefreshSwatches();
    }

    private void Refresh()
    {
        ResetBefore();
        if (_vm?.PrimarySelection is not PathItem path)
        {
            return;
        }

        TargetSelector.SetState(path.Fill.Color, path.Fill.IsVisible,
            path.Stroke.Color, path.Stroke.IsVisible, _strokeTarget);
        LoadTargetColor();
    }

    /// <summary>Rebuilds the swatch strip from the document's used colours.</summary>
    private void RefreshSwatches()
    {
        if (_vm is null)
        {
            return;
        }

        SwatchStrip.Children.Clear();
        foreach (ColorRgb color in _vm.UsedColors())
        {
            var swatch = new Button
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)),
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(color.A * 255),
                    (byte)Math.Round(color.R * 255),
                    (byte)Math.Round(color.G * 255),
                    (byte)Math.Round(color.B * 255))),
            };
            ToolTip.SetTip(swatch, $"#{color.R * 255:0}{color.G * 255:0}{color.B * 255:0}");
            ColorRgb picked = color;
            swatch.Click += (_, _) => PickSwatch(picked);
            SwatchStrip.Children.Add(swatch);
        }
    }

    private void PickSwatch(ColorRgb color)
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        Wheel.SetColor(color);
        ValueSlider.Value = Wheel.Value * 100;
        AlphaBox.Text = Math.Round(color.A * 255).ToString("0", CultureInfo.InvariantCulture);
        _syncing = false;
        UpdateReadouts();
        ApplyLive();
        CommitLive();
    }

    private void ResetBefore()
    {
        _fillBefore = null;
        _strokeBefore = null;
    }

    private void LoadTargetColor()
    {
        if (_vm?.PrimarySelection is not PathItem path)
        {
            return;
        }

        TargetSelector.SetState(path.Fill.Color, path.Fill.IsVisible,
            path.Stroke.Color, path.Stroke.IsVisible, _strokeTarget);

        ColorRgb targetColor = _strokeTarget ? path.Stroke.Color : path.Fill.Color;
        bool visible = _strokeTarget ? path.Stroke.IsVisible : path.Fill.IsVisible;

        _syncing = true;
        Wheel.SetColor(targetColor);
        if (!visible)
        {
            Wheel.Value = 1.0; // keep the spectrum visible for a "none" target
        }

        ValueSlider.Value = Wheel.Value * 100;
        _syncing = false;
        UpdateReadouts();
    }

    private void OnWheelChanged()
    {
        if (_syncing)
        {
            return;
        }

        UpdateReadouts();
        ApplyLive();
    }

    /// <summary>Mutates the selection immediately (no command) for live feedback.</summary>
    private void ApplyLive()
    {
        if (_vm is null || _syncing || _vm.PrimarySelection is not PathItem)
        {
            return;
        }

        ModelFillRule rule = FillRuleBox.SelectedIndex == 1 ? ModelFillRule.EvenOdd : ModelFillRule.NonZero;
        ColorRgb color = Wheel.Color.WithAlpha(ParseByte(AlphaBox) / 255.0);

        if (_strokeTarget)
        {
            _strokeBefore ??= _vm.SelectedPaths()
                .Select(p => (p, p.Stroke)).ToList();
            foreach ((PathItem path, _) in _strokeBefore)
            {
                double width = path.Stroke.Width > 0 ? path.Stroke.Width : 1.0;
                path.Stroke = new StrokeSpec(true, color, width, path.Stroke.Cap, path.Stroke.Join,
                    path.Stroke.MiterLimit, path.Stroke.Alignment);
            }
        }
        else
        {
            _fillBefore ??= _vm.SelectedPaths()
                .Select(p => (p, p.Fill)).ToList();
            foreach ((PathItem path, _) in _fillBefore)
            {
                path.Fill = FillSpec.Solid(color, rule);
            }
        }

        // Keep the "current style" in sync so new objects inherit these colours.
        if (_strokeTarget)
        {
            _vm.CurrentStroke = _vm.PrimarySelection is PathItem sp ? sp.Stroke : _vm.CurrentStroke;
        }
        else
        {
            _vm.CurrentFill = _vm.PrimarySelection is PathItem fp ? fp.Fill : _vm.CurrentFill;
        }

        UpdateSelectorState();
        _vm.RaiseTransformChanged(); // repaint without a full refresh
    }

    /// <summary>Commits the live change as one undo step.</summary>
    private void CommitLive()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        if (_strokeTarget && _strokeBefore is { Count: > 0 })
        {
            var edits = _strokeBefore
                .Select(t => (IUndoableCommand)new SetStrokeCommand(t.Path, t.Path.Stroke))
                .ToList();
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Stroke colour", edits));
        }
        else if (!_strokeTarget && _fillBefore is { Count: > 0 })
        {
            var edits = _fillBefore
                .Select(t => (IUndoableCommand)new SetFillCommand(t.Path, t.Path.Fill))
                .ToList();
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Fill", edits));
        }

        ResetBefore();
        Refresh();
    }

    private void UpdateReadouts()
    {
        ColorRgb color = Wheel.Color.WithAlpha(ParseByte(AlphaBox) / 255.0);
        _syncing = true;
        SetBox(FillR, Math.Round(color.R * 255));
        SetBox(FillG, Math.Round(color.G * 255));
        SetBox(FillB, Math.Round(color.B * 255));
        SetBox(AlphaBox, Math.Round(color.A * 255));
        if (!HexBox.IsFocused)
        {
            HexBox.Text = $"#{(byte)Math.Round(color.R * 255):X2}" +
                          $"{(byte)Math.Round(color.G * 255):X2}" +
                          $"{(byte)Math.Round(color.B * 255):X2}";
        }

        _syncing = false;
        UpdateSelectorState();
    }

    /// <summary>The fill/stroke circles show the currently selected colours.</summary>
    private void UpdateSelectorState()
    {
        if (_vm?.PrimarySelection is not PathItem path)
        {
            return;
        }

        TargetSelector.SetState(path.Fill.Color, path.Fill.IsVisible,
            path.Stroke.Color, path.Stroke.IsVisible, _strokeTarget);
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
            var color = ColorRgb.FromBytes((byte)(value >> 16), (byte)(value >> 8), (byte)value, ParseByte(AlphaBox));
            _syncing = true;
            Wheel.SetColor(color);
            ValueSlider.Value = Wheel.Value * 100;
            _syncing = false;
            UpdateReadouts();
            ApplyLive();
            CommitLive();
        }
    }

    private void ApplyRgb()
    {
        if (_syncing)
        {
            return;
        }

        var color = ColorRgb.FromBytes(ParseByte(FillR), ParseByte(FillG), ParseByte(FillB), ParseByte(AlphaBox));
        _syncing = true;
        Wheel.SetColor(color);
        ValueSlider.Value = Wheel.Value * 100;
        _syncing = false;
        UpdateReadouts();
        ApplyLive();
        CommitLive();
    }

    private static byte ParseByte(TextBox box)
        => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? (byte)Math.Clamp(Math.Round(v), 0, 255)
            : (byte)0;
}
