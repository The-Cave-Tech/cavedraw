using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Commands;
using ModelFillRule = VCCad.Core.Model.FillRule;
using HslColor = VCCad.Core.Color.HslColor;
using HexColor = VCCad.Core.Color.HexColor;

namespace VCCad.App.Views.Panes;

/// <summary>
/// Colour tab: the ring-and-triangle spectrum picker with HSL and hex readouts,
/// an opacity bar and a two-column recent-colour pad.
///
/// All colour maths lives in <c>VCCad.Core.Color</c> via <see cref="Controls.ColorWheel"/>.
/// The picker drives <see cref="EditorColorState.Shared"/>, the same model the
/// <c>color.*</c> operations read and write, so a colour clicked here and a colour
/// set through the API are one value rather than two that look alike. This pane only
/// moves that value between the picker and the document selection. There are no
/// Apply buttons — the active fill/stroke target is chosen on the diagram, changes
/// apply immediately, and the interaction commits a single undo step when it ends.
/// </summary>
public partial class ColorsPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;
    private bool _strokeTarget;
    private bool _refreshingFromState;

    private List<(PathItem Path, FillSpec Before)>? _fillBefore;
    private List<(PathItem Path, StrokeSpec Before)>? _strokeBefore;

    private static EditorColorState Colors => EditorColorState.Shared;

    public ColorsPane()
    {
        InitializeComponent();
        Wheel.ColorChanged += (_, _) => OnWheelChanged();
        Wheel.ColorCommitted += (_, _) => CommitLive();
        OpacityBar.ValueChanged += (_, _) => OnOpacityChanged();
        OpacityBar.Commit += (_, _) => CommitLive();
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
        foreach (TextBox box in new[] { HueBox, SatBox, LightBox })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyHsl();
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => ApplyHsl();
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

        // One model for the panel and the registry: the picker draws the editor's
        // working colour, and color.get/color.set are the same value.
        Wheel.Model = Colors.Model;

        // A re-attach must not leave the old subscription behind. Unsubscribing first
        // makes this idempotent, so the pane is refreshed exactly once per change.
        Colors.Changed -= OnColorStateChanged;
        Colors.Changed += OnColorStateChanged;

        vm.DocumentChanged += (_, _) => { Refresh(); RefreshRecent(); };
        vm.TransformChanged += (_, _) => RefreshRecent();
        vm.SelectionChanged += (_, _) => Refresh();
        Refresh();
        RefreshRecent();
    }

    /// <summary>
    /// Stops listening to the shared colour state. Attaching again re-subscribes, so a
    /// pane that is put away and brought back does not accumulate handlers.
    /// </summary>
    public void Detach()
    {
        Colors.Changed -= OnColorStateChanged;
        _vm = null;
    }

    /// <summary>
    /// The world changed the working colour — an API call, or another panel: repaint the
    /// picker and the readouts without it having to be touched first.
    ///
    /// The pane writes the shared state from its own interactions, so the guard stops a
    /// change caused by that write from re-entering this handler. Nothing here writes the
    /// state back, so the guard is belt-and-braces rather than load-bearing.
    /// </summary>
    private void OnColorStateChanged(object? sender, EventArgs e)
    {
        if (_syncing || _refreshingFromState)
        {
            return;
        }

        _refreshingFromState = true;
        try
        {
            Wheel.Refresh();
            UpdateReadouts();
            RefreshRecent();
        }
        finally
        {
            _refreshingFromState = false;
        }
    }

    private ColorRgb CurrentColor => Colors.Color.WithAlpha(Colors.Alpha);

    private void Refresh()
    {
        ResetBefore();
        if (_vm?.PrimarySelection is not PathItem path)
        {
            // Nothing selected: still show what the picker itself is pointing at.
            UpdateReadouts();
            return;
        }

        TargetSelector.SetState(path.Fill.Color, path.Fill.IsVisible,
            path.Stroke.Color, path.Stroke.IsVisible, _strokeTarget);
        LoadTargetColor();
    }

    /// <summary>Rebuilds the two-column recent-colour pad.</summary>
    private void RefreshRecent()
    {
        if (_vm is null)
        {
            return;
        }

        IReadOnlyList<ColorRgb> colours = Colors.Recent.Count > 0 ? Colors.Recent : _vm.UsedColors();
        SwatchPad.Children.Clear();
        foreach (ColorRgb color in colours.Take(12))
        {
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)),
                Background = new SolidColorBrush(ToColor(color.WithAlpha(1.0))),
            };
            ToolTip.SetTip(swatch, HexColor.Format(color));
            ColorRgb picked = color;
            swatch.Click += (_, _) => PickSwatch(picked);
            SwatchPad.Children.Add(swatch);
        }
    }

    private void PickSwatch(ColorRgb color)
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        Colors.SetColor(color);
        Colors.SetAlpha(color.A);
        Wheel.Refresh();
        OpacityBar.Color = Wheel.Color;
        OpacityBar.SetValue(Colors.Alpha);
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
        Colors.Model.SetColor(targetColor);
        // Keep the spectrum fully opaque for a "none" target so it stays legible.
        Colors.Model.SetAlpha(visible ? targetColor.A : 1.0);
        Wheel.Refresh();
        OpacityBar.Color = Wheel.Color;
        OpacityBar.SetValue(Colors.Alpha);
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

    private void OnOpacityChanged()
    {
        if (_syncing)
        {
            return;
        }

        Colors.SetAlpha(OpacityBar.Value);
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
        ColorRgb color = CurrentColor;

        if (_strokeTarget)
        {
            _strokeBefore ??= _vm.SelectedPaths()
                .Select(p => (p, p.Stroke)).ToList();
            foreach ((PathItem path, _) in _strokeBefore)
            {
                double width = path.Stroke.Width > 0 ? path.Stroke.Width : 1.0;
                path.Stroke = new StrokeSpec(true, color, width, path.Stroke.Cap, path.Stroke.Join,
                    path.Stroke.MiterLimit, path.Stroke.Alignment, path.Stroke.Dash);
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

        Colors.Remember(Colors.Color);
        ResetBefore();
        Refresh();
        RefreshRecent();
    }

    private void UpdateReadouts()
    {
        ColorRgb color = CurrentColor;
        HslColor hsl = HslColor.FromRgb(color);

        _syncing = true;
        SetBox(HueBox, Math.Round(hsl.H));
        SetBox(SatBox, Math.Round(hsl.S * 100.0));
        SetBox(LightBox, Math.Round(hsl.L * 100.0));
        if (!HexBox.IsFocused)
        {
            HexBox.Text = HexColor.Format(color);
        }

        OpacityBar.Color = Wheel.Color;
        OpacityBar.SetValue(Colors.Alpha);
        OpacityText.Text = Math.Round(Colors.Alpha * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        OpacityPreview.Fill = new SolidColorBrush(ToColor(color));
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

    private static void SetBox(TextBox box, double value)
    {
        if (!box.IsFocused)
        {
            box.Text = value.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    private void ApplyHex()
    {
        if (_syncing || !HexColor.TryParse(HexBox.Text, out ColorRgb parsed))
        {
            return;
        }

        _syncing = true;
        Colors.Model.SetColor(parsed);
        Wheel.Refresh();
        _syncing = false;
        UpdateReadouts();
        ApplyLive();
        CommitLive();
    }

    private void ApplyHsl()
    {
        if (_syncing
            || !TryParse(HueBox, out double h)
            || !TryParse(SatBox, out double s)
            || !TryParse(LightBox, out double l))
        {
            return;
        }

        var hsl = new HslColor(h, s / 100.0, l / 100.0).Normalized();
        _syncing = true;
        Colors.Model.SetColor(hsl.ToRgb(Colors.Alpha));
        Wheel.Refresh();
        _syncing = false;
        UpdateReadouts();
        ApplyLive();
        CommitLive();
    }

    private static bool TryParse(TextBox box, out double value)
        => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static Color ToColor(ColorRgb c) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(c.A, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.R, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.G, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.B, 0.0, 1.0) * 255.0));
}
