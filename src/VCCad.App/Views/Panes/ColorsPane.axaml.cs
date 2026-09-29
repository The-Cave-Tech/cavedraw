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
        HexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
        };
        HexBox.LostFocus += (_, _) => ApplyHex();

        // The hex entry is live: valid contents are applied as they are typed.
        HexBox.TextChanged += (_, _) => ApplyHexLive();

        OpacityBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyOpacityText();
                e.Handled = true;
            }
        };
        OpacityBox.LostFocus += (_, _) => ApplyOpacityText();

        TargetSelector.TargetChanged += (_, stroke) =>
        {
            _strokeTarget = stroke;
            ResetBefore();
            // Switching the target switches the picker to that target's colour at once,
            // whether or not anything is selected: the ring and circle drive the picker.
            SyncFromCurrent();
        };
        TargetSelector.SwapRequested += (_, _) => SwapFillStroke();
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

            // An external colour set (color.set through the API) is the same act as choosing
            // a colour in the picker: the active current value, the selection and the diagram
            // must all follow it, or a driver and a person would see different colours.
            ApplyLive();
        }
        finally
        {
            _refreshingFromState = false;
        }
    }

    private ColorRgb CurrentColor => Colors.Color.WithAlpha(Colors.Alpha);

    /// <summary>
    /// The fill and stroke the diagram is showing: the selected path's own values when
    /// something is selected, otherwise the editor's current fill/stroke. Reading the
    /// current values when nothing is selected is what stops the diagram and the picker
    /// from disagreeing.
    /// </summary>
    private (FillSpec Fill, StrokeSpec Stroke) TargetState()
        => _vm?.PrimarySelection is PathItem path
            ? (path.Fill, path.Stroke)
            : (_vm?.CurrentFill ?? FillSpec.None, _vm?.CurrentStroke ?? StrokeSpec.Hairline(ColorRgb.Black));

    private void Refresh()
    {
        ResetBefore();
        SyncFromCurrent();
    }

    /// <summary>
    /// Pushes the current fill/stroke into the diagram and loads the active target's colour
    /// into the picker, so the two always agree.
    /// </summary>
    private void SyncFromCurrent()
    {
        if (_vm is null)
        {
            return;
        }

        (FillSpec fill, StrokeSpec stroke) = TargetState();
        TargetSelector.SetState(fill.Color, fill.IsVisible,
            stroke.Color, stroke.IsVisible, _strokeTarget);

        ColorRgb target = _strokeTarget ? stroke.Color : fill.Color;
        bool visible = _strokeTarget ? stroke.IsVisible : fill.IsVisible;

        _syncing = true;
        Colors.Model.SetColor(target);
        // Keep the spectrum fully opaque for a "none" target so it stays legible.
        Colors.Model.SetAlpha(visible ? target.A : 1.0);
        Wheel.Refresh();
        OpacityBar.Color = Wheel.Color;
        OpacityBar.SetValue(Colors.Alpha);
        _syncing = false;
        UpdateReadouts();
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
                Width = 18,
                Height = 18,
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

    /// <summary>
    /// Mutates the selection immediately (no command) for live feedback, and always updates
    /// the editor's current fill/stroke — that is what the ring and circle stand for, so it
    /// has to follow the picker whether or not anything is selected.
    /// </summary>
    private void ApplyLive()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        ColorRgb color = CurrentColor;
        bool hasSelection = _vm.PrimarySelection is PathItem;

        if (_strokeTarget)
        {
            if (hasSelection)
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

            StrokeSpec basis = _vm.PrimarySelection is PathItem sp ? sp.Stroke : _vm.CurrentStroke;
            _vm.CurrentStroke = new StrokeSpec(true, color,
                basis.Width > 0 ? basis.Width : 1.0,
                basis.Cap, basis.Join, basis.MiterLimit, basis.Alignment, basis.Dash);
        }
        else
        {
            if (hasSelection)
            {
                _fillBefore ??= _vm.SelectedPaths()
                    .Select(p => (p, p.Fill)).ToList();
                foreach ((PathItem path, _) in _fillBefore)
                {
                    // Keep each path's own winding rule; the panel no longer offers a
                    // rule chooser, and recolouring must not silently change a donut
                    // from EvenOdd to NonZero.
                    path.Fill = FillSpec.Solid(color, path.Fill.Rule);
                }
            }

            ModelFillRule rule = _vm.PrimarySelection is PathItem fp ? fp.Fill.Rule : _vm.CurrentFill.Rule;
            _vm.CurrentFill = FillSpec.Solid(color, rule);
        }

        UpdateSelectorState();
        _vm.RaiseTransformChanged(); // repaint without a full refresh
    }

    /// <summary>
    /// Flips the fill and stroke colours — what the arc north-east of the circles does. The
    /// two current values swap, and any selected paths swap with them as one undo step; each
    /// spec keeps its own width, rule, caps and so on, so only the colours trade places.
    /// </summary>
    private void SwapFillStroke()
    {
        if (_vm is null)
        {
            return;
        }

        (FillSpec fill, StrokeSpec stroke) = TargetState();

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in _vm.SelectedPaths())
        {
            FillSpec pathFill = path.Fill with { Color = path.Stroke.Color, IsVisible = path.Stroke.IsVisible };
            StrokeSpec pathStroke = path.Stroke with { Color = path.Fill.Color, IsVisible = path.Fill.IsVisible };
            edits.Add(new SetFillCommand(path, pathFill));
            edits.Add(new SetStrokeCommand(path, pathStroke));
        }

        if (edits.Count > 0)
        {
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Swap fill and stroke", edits));
        }

        // With a selection the swapped values now live on the paths; without one they are the
        // new current style.
        if (_vm.PrimarySelection is PathItem primary)
        {
            _vm.CurrentFill = primary.Fill;
            _vm.CurrentStroke = primary.Stroke;
        }
        else
        {
            _vm.CurrentFill = fill with { Color = stroke.Color, IsVisible = stroke.IsVisible };
            _vm.CurrentStroke = stroke with { Color = fill.Color, IsVisible = fill.IsVisible };
        }

        ResetBefore();
        SyncFromCurrent();
        RefreshRecent();
        _vm.RaiseTransformChanged();
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

        // HSL is a readout, not an editor: the reference panel shows the three
        // values stacked beside the ring, so they are captions here.
        HueText.Text = Math.Round(hsl.H).ToString("0", CultureInfo.InvariantCulture);
        SatText.Text = Math.Round(hsl.S * 100.0).ToString("0", CultureInfo.InvariantCulture);
        LightText.Text = Math.Round(hsl.L * 100.0).ToString("0", CultureInfo.InvariantCulture);

        // The hex entry is RGB with the opacity byte: eight characters, no '#' in
        // the box (the caption supplies it).
        if (!HexBox.IsFocused)
        {
            HexBox.Text = HexColor.Format(color, includeAlpha: true).TrimStart('#');
        }

        OpacityBar.Color = Wheel.Color;
        OpacityBar.SetValue(Colors.Alpha);

        // The percentage is editable, so only write it when the person is not in the field.
        if (!OpacityBox.IsFocused)
        {
            OpacityBox.Text = Math.Round(Colors.Alpha * 100.0).ToString("0", CultureInfo.InvariantCulture);
        }

        OpacityPreview.Fill = new SolidColorBrush(ToColor(color));
        _syncing = false;
        UpdateSelectorState();
    }

    /// <summary>The fill/stroke circles show the values the diagram stands for.</summary>
    private void UpdateSelectorState()
    {
        if (_vm is null)
        {
            return;
        }

        (FillSpec fill, StrokeSpec stroke) = TargetState();
        TargetSelector.SetState(fill.Color, fill.IsVisible,
            stroke.Color, stroke.IsVisible, _strokeTarget);
    }

    /// <summary>
    /// Applies the opacity field: a bare number is a percentage. Text that cannot be read as
    /// one is discarded and the field is put back to the opacity that is actually in force,
    /// so a typo can never leave the panel showing a value it is not using.
    /// </summary>
    private void ApplyOpacityText()
    {
        if (_syncing)
        {
            return;
        }

        string text = (OpacityBox.Text ?? string.Empty).Trim().TrimEnd('%').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            && percent >= 0.0 && percent <= 100.0)
        {
            _syncing = true;
            Colors.SetAlpha(percent / 100.0);
            OpacityBar.Color = Wheel.Color;
            OpacityBar.SetValue(Colors.Alpha);
            _syncing = false;
            UpdateReadouts();
            ApplyLive();
            CommitLive();
            return;
        }

        // Invalid: leave the value alone and show what it really is.
        _syncing = true;
        OpacityBox.Text = Math.Round(Colors.Alpha * 100.0).ToString("0", CultureInfo.InvariantCulture);
        _syncing = false;
    }

    /// <summary>
    /// Applies the hex field as it is typed, but only when it reads as a colour. The opacity
    /// byte is honoured for a four- or eight-digit entry; a six-digit one leaves the bar where
    /// the person put it.
    /// </summary>
    private void ApplyHexLive()
    {
        if (_syncing)
        {
            return;
        }

        string text = (HexBox.Text ?? string.Empty).Trim();
        if (!HexColor.TryParse(text, out ColorRgb parsed))
        {
            return;
        }

        ApplyParsedHex(text, parsed);
    }

    private void ApplyHex()
    {
        if (_syncing)
        {
            return;
        }

        string text = (HexBox.Text ?? string.Empty).Trim();
        if (!HexColor.TryParse(text, out ColorRgb parsed))
        {
            // Invalid on leaving the field: put the colour's own hex back.
            _syncing = true;
            HexBox.Text = HexColor.Format(CurrentColor, includeAlpha: true).TrimStart('#');
            _syncing = false;
            return;
        }

        ApplyParsedHex(text, parsed);
        CommitLive();
    }

    private void ApplyParsedHex(string text, ColorRgb parsed)
    {
        int digits = text.TrimStart('#').Length;

        _syncing = true;
        Colors.Model.SetColor(parsed);
        if (digits is 4 or 8)
        {
            Colors.SetAlpha(parsed.A);
        }

        Wheel.Refresh();
        OpacityBar.Color = Wheel.Color;
        OpacityBar.SetValue(Colors.Alpha);
        _syncing = false;
        UpdateReadouts();
        ApplyLive();
    }

    private static Color ToColor(ColorRgb c) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(c.A, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.R, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.G, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.B, 0.0, 1.0) * 255.0));
}
