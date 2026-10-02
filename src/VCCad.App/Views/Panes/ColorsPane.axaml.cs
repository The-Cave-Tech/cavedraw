using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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

        // The hatch buttons go through the operation registry rather than setting a fill here, so a hatch set
        // from this pane and one set by a driver are the same code doing the same thing - the capability-parity
        // rule, and the reason there is no second implementation to drift.
        Hatch45.Click += (_, _) => ApplyHatch("""{"angle":45,"spacing":4,"width":0.5}""");
        HatchCross.Click += (_, _) => ApplyHatch("""{"cross":true,"spacing":4,"width":0.5}""");
        HatchNone.Click += (_, _) => ApplyHatch("""{"clear":true}""");

        Eyedropper.Click += async (_, _) => await PickFromScreenAsync();

        // The circle does exactly what a recent swatch does, through the same call, so the two cannot drift.
        PickedSwatch.Click += (_, _) =>
        {
            if (Colors.LastPicked is { } picked)
            {
                PickSwatch(picked);
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

        // The fill rule deliberately is **not** here. It is a property of an outline's geometry, not of a
        // colour, and a picker that also decides how a region is filled answers two unrelated questions at
        // once. It was removed for this reason once before and came back, so a test now asserts the picker
        // holds no rule control at all - see ColorsPaneContentsTests. The capability is an operation
        // (`style.setFillRule`), so nothing was lost by taking the control out.
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

        SubscribeToColorState();

        vm.DocumentChanged += (_, _) => { Refresh(); RefreshRecent(); };
        vm.TransformChanged += (_, _) => RefreshRecent();
        vm.SelectionChanged += (_, _) => Refresh();
        Refresh();
        RefreshRecent();
    }

    /// <summary>
    /// Stops listening to the shared colour state, and forgets the view model.
    ///
    /// This is explicit teardown, for a host that owns the pane's whole life. It is not the path that
    /// keeps the process clean: a pane put away in the dock is released by leaving the visual tree, so
    /// the pane does not depend on somebody remembering to call this.
    /// </summary>
    public void Detach()
    {
        UnsubscribeFromColorState();
        _vm = null;
    }

    /// <summary>
    /// Takes the subscription to the shared colour state.
    ///
    /// Unsubscribing first makes this idempotent, so the pane is refreshed exactly once per change
    /// however many times it is attached.
    /// </summary>
    private void SubscribeToColorState()
    {
        Colors.Changed -= OnColorStateChanged;
        Colors.Changed += OnColorStateChanged;
    }

    /// <summary>
    /// Gives it up, so the process-wide state does not hold this pane for the life of the process.
    /// </summary>
    private void UnsubscribeFromColorState() => Colors.Changed -= OnColorStateChanged;

    /// <summary>
    /// The pane is on screen, so it follows the colour state again.
    ///
    /// <see cref="OnColorStateChanged"/> reads and writes controls, so the subscription is only worth
    /// having while they are showing - and it has to be taken again here, because the dock puts a tab's
    /// view away and brings the same instance back when the tab is selected again. The view model is
    /// deliberately kept across that, so the pane is live the moment it is showing rather than needing
    /// a second <see cref="Attach"/> that the dock's content factory does not make.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeToColorState();
    }

    /// <summary>
    /// The pane is no longer on screen: give up the subscription.
    ///
    /// This is what releases the pane in the product. <see cref="EditorColorState.Shared"/> outlives
    /// every pane built over the life of the process, and a handler left on it keeps the pane alive and
    /// keeps it working on a document it is no longer showing. The dock removes the previous tab's view
    /// when another tab is selected - see <c>DockTabPanelView.Rebuild</c> - so this is the product path,
    /// not a method somebody has to remember to call.
    ///
    /// <see cref="Detach"/> is deliberately not called here: it forgets the view model as well, and the
    /// same pane instance is put back on screen when its tab is selected again.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeFromColorState();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The world changed the working colour — an API call, or another panel: repaint the
    /// picker and the readouts without it having to be touched first.
    ///
    /// The pane writes the shared state from its own interactions, so the guard stops a
    /// change caused by that write from re-entering this handler. Nothing here writes the
    /// state back, so the guard is belt-and-braces rather than load-bearing.
    ///
    /// The state is process-wide and shared, so it is written from whichever thread made the
    /// change - not only from this pane's own clicks. The screen eyedropper is the live case:
    /// <c>color.pickScreen</c> completes on a background continuation and calls
    /// <see cref="EditorColorState.SetPicked"/> there. Everything below is UI work - readouts onto
    /// controls, and the selection onto the document - so a change that arrives on another thread
    /// is posted to the UI thread rather than written where it landed. Writing it where it landed
    /// is what turned an API call into "Call from invalid thread".
    /// </summary>
    private void OnColorStateChanged(object? sender, EventArgs e)
    {
        // Read here rather than inside the UI work: a change from another thread is posted to the UI thread, and by
        // the time it runs the caller may have closed its scope. Whether the caller applied the colour is a fact
        // about the change, not about the moment it is painted.
        bool appliedByCaller = Colors.IsAppliedByCaller;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => FollowStateChange(appliedByCaller));
            return;
        }

        FollowStateChange(appliedByCaller);
    }

    /// <summary>Repaints the picker from the shared state, and applies it when nobody else has.</summary>
    private void FollowStateChange(bool appliedByCaller)
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
            //
            // The operation applies the colour to the document itself, through DocumentSession, and says so -
            // because a driver's call must not depend on this pane being on screen (#184). It then only repaints
            // here. A change nobody else applied, which is a colour written straight to the shared state, is still
            // carried into the document from this handler, which is what ColorsPaneLifetimeTests pins.
            if (!appliedByCaller)
            {
                ApplyLive();
            }
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

    /// <summary>Rebuilds the pane from the shared colour state. Internal so a test can drive it.</summary>
    internal void Refresh()
    {
        ResetBefore();
        SyncFromCurrent();

        // The circle beside the eyedropper shows what the picker last chose, and is dimmed when there is
        // nothing to show: an empty circle that looks enabled would be a button that does nothing.
        if (Colors.LastPicked is { } picked)
        {
            PickedSwatch.Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(
                (byte)Math.Clamp(picked.A * 255, 0, 255),
                (byte)Math.Clamp(picked.R * 255, 0, 255),
                (byte)Math.Clamp(picked.G * 255, 0, 255),
                (byte)Math.Clamp(picked.B * 255, 0, 255)));
            PickedSwatch.Opacity = 1.0;
        }
        else
        {
            PickedSwatch.Background = Brushes.Transparent;
            PickedSwatch.Opacity = 0.4;
        }
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
    /// <summary>
    /// Applies a hatch to the selection through the operation registry.
    ///
    /// The registry, not a fill assignment here: a hatch set by a click and a hatch set by a driver are then the
    /// same code doing the same thing, which is the capability-parity rule and the reason there is no second
    /// implementation to drift from the first.
    /// </summary>
    private void ApplyHatch(string parameters)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            EditorOperations.Invoke(
                new AutomationContext { ViewModel = _vm },
                "style.setHatch",
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(parameters));
        }
        catch (EditorOperationException)
        {
            // Nothing selected, or nothing hatchable. A button that does nothing is better than an exception
            // thrown out of a click handler, which surfaces as a crash rather than as a refusal.
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Runs the screen eyedropper through the operation registry, the same way a driver would.
    ///
    /// The registry owns the overlay, the sampling and the recording, so a colour picked by this button and one
    /// picked by <c>color.pickScreen</c> are the same code doing the same thing - and the refusal, when the
    /// platform cannot do it, comes back with its reason rather than as a colour from nowhere.
    /// </summary>
    private async Task PickFromScreenAsync()
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            // The target is named, because the operation applies the picked colour to the document itself and
            // the fill/stroke circles are a choice this pane holds: without it, picking with the stroke circle
            // armed would recolour the fill a driver would have described.
            await EditorOperations.InvokeAsync(
                new AutomationContext
                {
                    ViewModel = _vm,
                    PickFromScreenAsync = () => Picking.ScreenPickOverlay.PickFor(this),
                },
                "color.pickScreen",
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new { target = _strokeTarget ? "stroke" : "fill" }));
        }
        catch (EditorOperationException)
        {
            return;
        }

        Refresh();
    }

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
            edits.Add(new SetFillCommand(path, pathFill, path.Fill));
            edits.Add(new SetStrokeCommand(path, pathStroke, path.Stroke));
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
            // `t.Path.Stroke` is the live value the drag already wrote, and `t.Before` is what
            // it replaced - Undo needs the latter.
            var edits = _strokeBefore
                .Select(t => (IUndoableCommand)new SetStrokeCommand(t.Path, t.Path.Stroke, t.Before))
                .ToList();
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Stroke colour", edits));
        }
        else if (!_strokeTarget && _fillBefore is { Count: > 0 })
        {
            var edits = _fillBefore
                .Select(t => (IUndoableCommand)new SetFillCommand(t.Path, t.Path.Fill, t.Before))
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
