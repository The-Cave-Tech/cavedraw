using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Pdf;

namespace VCCad.App.Views.Panes;

/// <summary>Stroke tab: what the selection's strokes are, and what they disagree about.</summary>
public partial class StrokePane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;

    /// <summary>What the last refresh put in each control.</summary>
    /// <remarks>
    /// Tracked because a field the person did not touch is **not** an edit. A mixed selection's width box reads
    /// "mixed", and an apply that could not tell that from a typed value would have to invent a number for it -
    /// which is the defect the mixed readout exists to remove, one level down.
    /// </remarks>
    private string _shownWidth = string.Empty;
    private string _shownMiter = string.Empty;
    private int _shownCap = -1;
    private int _shownJoin = -1;
    private int _shownAlign = -1;
    private int _shownDash = -1;

    /// <summary>The profile the inspected stroke carries, as the last refresh read it.</summary>
    private WidthProfileSpec? _shownProfile;

    /// <summary>The curve shown for each dynamics target, so a change to one control can keep the other's value.</summary>
    private readonly Dictionary<DynamicsTarget, DynamicsCurve> _shownCurves = new();

    private readonly Dictionary<DynamicsTarget, (CheckBox Enabled, ComboBox Curve)> _dynamicsRows = new();

    /// <summary>The word every mixed field reads, so the pane says the same thing in every place.</summary>
    private const string MixedWord = "mixed";

    private const string PlainStrokeLabel = "Plain";

    /// <summary>What the brush selector shows for a stroke swept with no brush - a state, not an empty control.</summary>
    private const string NoBrushLabel = "(none)";

    private static readonly DynamicsPreset[] Presets =
    {
        DynamicsPreset.Linear,
        DynamicsPreset.Soft,
        DynamicsPreset.Hard,
        DynamicsPreset.Exponential,
    };

    public StrokePane()
    {
        InitializeComponent();

        // Changes take effect immediately; text fields also commit on Enter.
        StrokeCapBox.SelectionChanged += (_, _) => ApplyNow();
        StrokeJoinBox.SelectionChanged += (_, _) => ApplyNow();
        StrokeAlignBox.SelectionChanged += (_, _) => ApplyNow();
        StrokeDashBox.SelectionChanged += (_, _) => ApplyNow();
        foreach (TextBox box in new[] { StrokeWidthBox, MiterBox })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ApplyNow();
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => ApplyNow();
        }

        // Choosing the kind of stroke is a model edit like any other, and it goes through the session for the same
        // reason the width does - see ApplyStrokeType.
        StrokeTypeBox.SelectionChanged += (_, _) => ApplyStrokeType();

        // The brush goes through the operation registry rather than editing the stroke here: a brush chosen by this
        // control and one chosen by `brush.apply` are then one act on one model state rather than two that agree
        // until somebody changes one of them. See ApplyBrush.
        StrokeBrushBox.SelectionChanged += (_, _) => ApplyBrush();

        _dynamicsRows[DynamicsTarget.Width] = (DynamicsWidthEnabled, DynamicsWidthCurve);
        _dynamicsRows[DynamicsTarget.Opacity] = (DynamicsOpacityEnabled, DynamicsOpacityCurve);
        _dynamicsRows[DynamicsTarget.ScatterScale] = (DynamicsScatterEnabled, DynamicsScatterCurve);
        _dynamicsRows[DynamicsTarget.CalligraphicAngle] = (DynamicsAngleEnabled, DynamicsAngleCurve);
        _dynamicsRows[DynamicsTarget.Smoothing] = (DynamicsSmoothingEnabled, DynamicsSmoothingCurve);

        foreach (KeyValuePair<DynamicsTarget, (CheckBox Enabled, ComboBox Curve)> row in _dynamicsRows)
        {
            // The target travels with the closure rather than being looked up from the sender, which is what keeps
            // this one handler rather than five near-identical ones.
            DynamicsTarget target = row.Key;
            row.Value.Enabled.IsCheckedChanged += (_, _) => ApplyDynamics(target);
            row.Value.Curve.SelectionChanged += (_, _) => ApplyDynamics(target);
        }

        // The effects list, ordered because the order is the picture. The two moves go through the session methods
        // the operations call, so a person and a driver do the same thing - and the list is rebuilt from the model
        // after each, so it cannot show an order the document does not have.
        MoveEffectUpButton.Click += (_, _) => MoveEffect(+1);
        MoveEffectDownButton.Click += (_, _) => MoveEffect(-1);

        // The kinds come from the registry, so a new effect appears in this list by existing rather than by being
        // added to a switch here - the requirement the issue names.
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            EffectKindBox.Items.Add(definition.Kind);
        }

        EffectList.SelectionChanged += (_, _) => BuildEffectEditors();
        EffectKindBox.SelectedIndex = 0;
        AddEffectButton.Click += OnAddEffect;
        RemoveEffectButton.Click += OnRemoveEffect;
    }

    // ------------------------------------------------------------------
    // Stroke type: plain, or a width profile
    // ------------------------------------------------------------------

    /// <summary>
    /// Gives the inspected stroke a width profile - the stroke's own, or one from the document's library - or
    /// clears it, through the session method rather than by building the edit here.
    ///
    /// The session method names the stroke, because the operation that does the same thing
    /// (`style.setWidthProfile`) writes **every** stroke of the selection: a panel describing stroke 2 of 3 must
    /// not give the profile to strokes 1 and 3. That gap is reported rather than worked around by writing the
    /// model from a click handler, which is a defect in this repository.
    /// </summary>
    private void ApplyStrokeType()
    {
        if (_vm is null || _syncing || StrokeTypeBox.SelectedItem is not string name ||
            InspectedStrokeIndex() is not { } index)
        {
            return;
        }

        WidthProfileSpec? profile;
        if (name == PlainStrokeLabel)
        {
            profile = null;
        }
        else if (_shownProfile is { } own && own.Name == name)
        {
            // The stroke's own profile, which need not be in the document's library at all -
            // `style.setWidthProfile` writes one without registering it. Choosing it keeps it as it is rather than
            // replacing it with whatever the library happens to hold under the same name.
            profile = own;
        }
        else
        {
            profile = _vm.ActiveSession.Document.FindProfile(name);
            if (profile is null)
            {
                return;
            }
        }

        if (_vm.ActiveSession.SetWidthProfileAt(index, profile) > 0)
        {
            Refresh();
        }
    }

    /// <summary>
    /// The type box's items: the inspected stroke's own profile first, then the document's library.
    ///
    /// The stroke's own goes first because the point of the box is to show **this stroke's** kind, and a profile
    /// the document does not carry would otherwise be unrepresentable - which is exactly the state
    /// `profile.missing` reports.
    /// </summary>
    private void RefreshStrokeType(int index, StrokeSpec? stroke)
    {
        WidthProfileSpec? own = stroke is { HasWidthProfile: true } ? stroke.WidthProfile : null;
        _shownProfile = own;

        var names = new List<string> { PlainStrokeLabel };
        if (own is not null)
        {
            names.Add(own.Name);
        }

        if (_vm is not null)
        {
            foreach (WidthProfileSpec library in _vm.ActiveSession.Document.WidthProfiles)
            {
                if (!names.Contains(library.Name))
                {
                    names.Add(library.Name);
                }
            }
        }

        bool mixed = ProfileMixedAt(index);

        StrokeTypeBox.ItemsSource = names;
        StrokeTypeBox.PlaceholderText = mixed ? MixedWord : string.Empty;
        StrokeTypeBox.SelectedIndex = mixed ? -1 : own is null ? 0 : names.IndexOf(own.Name);

        // A readout of a profile that is not there describes nothing, and neither does one over a selection that
        // disagrees about which profile it has.
        ProfileSection.IsVisible = !mixed && own is not null;
        ProfileSummary.Text = own is null ? string.Empty : ProfileName(own);
        ProfilePoints.Text = own is null ? string.Empty : ProfilePointsText(own);
    }

    /// <summary>The profile's name, saying so when the document does not carry it.</summary>
    private string ProfileName(WidthProfileSpec profile)
    {
        bool known = _vm?.ActiveSession.Document.FindProfile(profile.Name) is not null;
        string points = profile.Points.Count == 1 ? "1 width point" : $"{profile.Points.Count} width points";
        return known ? $"{profile.Name} — {points}" : $"{profile.Name} — {points} (not in the document)";
    }

    /// <summary>Every width point, position and both sides, because a profile is not a symmetric bulge.</summary>
    private static string ProfilePointsText(WidthProfileSpec profile)
    {
        return string.Join(" · ", profile.Points.Select(point =>
            $"{point.Position:0.##}: {point.LeftWidth:0.##}/{point.RightWidth:0.##}pt"
            + (point.Interpolation == WidthInterpolation.Cubic ? " cubic" : string.Empty)));
    }

    /// <summary>Whether the selection disagrees about which width profile the stroke at this index carries.</summary>
    private bool ProfileMixedAt(int index)
    {
        IReadOnlyList<StrokeSpec> strokes = AgreeingStrokesAt(index);
        if (strokes.Count < 2)
        {
            return false;
        }

        return strokes.Skip(1).Any(stroke => !SameProfile(strokes[0], stroke));
    }

    private static bool SameProfile(StrokeSpec a, StrokeSpec b)
    {
        WidthProfileSpec? left = a.HasWidthProfile ? a.WidthProfile : null;
        WidthProfileSpec? right = b.HasWidthProfile ? b.WidthProfile : null;
        return left is null ? right is null : left.Equals(right);
    }

    // ------------------------------------------------------------------
    // Brush: the reusable asset this stroke is swept with
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies the chosen brush to the inspected stroke, through the **operation registry** rather than by editing
    /// the model here.
    ///
    /// This is the capability-parity rule in its plain form: a brush chosen in this combo and one applied by
    /// <c>brush.apply</c> are the same code doing the same thing, so there is no second implementation to drift.
    /// The inspected index travels as `strokeIndex`, which is what makes the choice land on the stroke the
    /// appearance panel is showing rather than on every stroke of the stack, and "(none)" is <c>brush.clear</c>.
    /// </summary>
    private void ApplyBrush()
    {
        if (_vm is null || _syncing || StrokeBrushBox.SelectedItem is not string name ||
            InspectedStrokeIndex() is not { } index)
        {
            return;
        }

        try
        {
            if (name == NoBrushLabel)
            {
                Invoke("brush.clear", new { strokeIndex = index });
            }
            else
            {
                Invoke("brush.apply", new { name, strokeIndex = index });
            }
        }
        catch (EditorOperationException)
        {
            // A brush that vanished from the library between the read and the click, or nothing selected. A control
            // that does nothing is better than an exception thrown out of a selection handler, which surfaces as a
            // crash rather than as a refusal.
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Runs an operation the way a driver would, on this pane's view model.
    ///
    /// The registry owns the edit, the undo step, the journal and the diagnostics record, which is the whole point:
    /// a control that reached into the model itself would be a capability that exists only in the UI.
    /// </summary>
    private void Invoke(string operation, object parameters)
        => EditorOperations.Invoke(
            new AutomationContext { ViewModel = _vm },
            operation,
            JsonSerializer.SerializeToElement(parameters));

    /// <summary>
    /// The brush box's items: "(none)", then the document's library, then - first of all after "(none)" - the
    /// inspected stroke's **own** brush when the document does not carry it.
    ///
    /// The stroke's own goes in for the same reason the type box carries the stroke's own profile: the point of the
    /// box is to show **this stroke's** brush, and a name the document has lost would otherwise be unrepresentable -
    /// which is exactly the state `brush.missing` reports.
    /// </summary>
    private void RefreshBrush(int index, StrokeSpec? stroke)
    {
        BrushSpec? own = stroke?.Brush;
        bool mixed = BrushMixedAt(index);

        var names = new List<string> { NoBrushLabel };
        if (own is not null)
        {
            names.Add(own.Name);
        }

        if (_vm is not null)
        {
            foreach (BrushSpec library in _vm.ActiveSession.Document.Brushes)
            {
                if (!names.Contains(library.Name))
                {
                    names.Add(library.Name);
                }
            }
        }

        StrokeBrushBox.ItemsSource = names;
        StrokeBrushBox.PlaceholderText = mixed ? MixedWord : string.Empty;
        StrokeBrushBox.SelectedIndex = mixed ? -1 : own is null ? 0 : names.IndexOf(own.Name);

        // A readout of a brush that is not there describes nothing, and neither does one over a selection that
        // disagrees about which brush it carries.
        BrushSummary.IsVisible = !mixed && own is not null;
        BrushSummary.Text = own is null ? string.Empty : BrushName(own);
    }

    /// <summary>The brush's name and the nib it draws, saying so when the document does not carry the asset.</summary>
    private string BrushName(BrushSpec brush)
    {
        bool known = _vm?.ActiveSession.Document.FindBrush(brush.Name) is not null;
        string nib = $"{brush.Diameter:0.##}pt nib at {brush.AngleDegrees:0.##}°"
            + (brush.Roundness < 1.0 ? $", roundness {brush.Roundness:0.##}" : string.Empty);
        return known ? $"{brush.Name} — {nib}" : $"{brush.Name} — {nib} (not in the document)";
    }

    /// <summary>Whether the selection disagrees about which brush the stroke at this index carries.</summary>
    private bool BrushMixedAt(int index)
    {
        IReadOnlyList<StrokeSpec> strokes = AgreeingStrokesAt(index);
        if (strokes.Count < 2)
        {
            return false;
        }

        return strokes.Skip(1).Any(stroke => !SameBrush(strokes[0].Brush, stroke.Brush));
    }

    private static bool SameBrush(BrushSpec? a, BrushSpec? b) => a is null ? b is null : a.Equals(b);

    // ------------------------------------------------------------------
    // Dynamics: what the stroke records about the pen
    // ------------------------------------------------------------------

    /// <summary>
    /// Sets one target of the tablet response on the inspected stroke, through the session method rather than by
    /// building the edit here.
    ///
    /// A target the selection **disagrees** about is shown indeterminate, and that is not something a person chose:
    /// writing it would turn "mixed" into "off" on every path without anybody asking, so it is left alone. Clicking
    /// the box does choose a value, and that resolves the disagreement on every path at this index.
    /// </summary>
    private void ApplyDynamics(DynamicsTarget target)
    {
        if (_vm is null || _syncing || !_dynamicsRows.TryGetValue(target, out (CheckBox Enabled, ComboBox Curve) row) ||
            row.Enabled.IsChecked is not { } enabled || InspectedStrokeIndex() is not { } index)
        {
            return;
        }

        // The preset the box shows, or - the box showing "custom" or "mixed" - the curve this stroke already has.
        int preset = row.Curve.SelectedIndex;
        DynamicsCurve curve = DynamicsCurve.Linear;
        if (preset >= 0 && preset < Presets.Length)
        {
            curve = DynamicsCurve.FromPreset(Presets[preset]);
        }
        else if (_shownCurves.TryGetValue(target, out DynamicsCurve shown))
        {
            curve = shown;
        }

        if (_vm.ActiveSession.SetDynamicsAt(index, target, enabled, curve) > 0)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Shows each target's recorded response, or says **mixed** where the selection disagrees about it.
    ///
    /// Enabled and the curve are marked separately, because they differ independently: two paths can both respond
    /// to pressure while following different curves, and one path can respond while another does not. A target
    /// nobody responds to agrees - it agrees on "off" - which is why the mixed flag is about disagreement rather
    /// than about whether anything is switched on.
    /// </summary>
    private List<string> RefreshDynamics(int index, StrokeSpec? stroke)
    {
        var mixedNames = new List<string>();

        IReadOnlyList<StrokeSpec> strokes = AgreeingStrokesAt(index);
        if (strokes.Count == 0 && stroke is not null)
        {
            // Nothing at this index is drawn, so the inspected stroke is the only witness there is - which is the
            // same fallback the geometry fields take for a hidden stroke.
            strokes = new[] { stroke };
        }

        DynamicsRows.IsVisible = stroke is not null;

        foreach (KeyValuePair<DynamicsTarget, (CheckBox Enabled, ComboBox Curve)> row in _dynamicsRows)
        {
            DynamicsTarget target = row.Key;
            CheckBox enabled = row.Value.Enabled;
            ComboBox curve = row.Value.Curve;

            if (strokes.Count == 0)
            {
                enabled.IsChecked = false;
                curve.SelectedIndex = 0;
                curve.PlaceholderText = string.Empty;
                _shownCurves[target] = DynamicsCurve.Linear;
                continue;
            }

            DynamicsTargetSpec first = Dynamics(strokes[0], target);
            bool enabledAgrees = strokes.All(s => Dynamics(s, target).Enabled == first.Enabled);
            bool curveAgrees = strokes.All(s => Dynamics(s, target).Curve == first.Curve);

            enabled.IsChecked = enabledAgrees ? first.Enabled : null;

            int preset = PresetIndex(first.Curve);
            curve.SelectedIndex = curveAgrees && preset >= 0 ? preset : -1;
            curve.PlaceholderText = !curveAgrees ? MixedWord : preset >= 0 ? string.Empty : "custom";
            _shownCurves[target] = first.Curve;

            if (!enabledAgrees || !curveAgrees)
            {
                mixedNames.Add($"{TargetName(target)} dynamics");
            }
        }

        return mixedNames;
    }

    private static DynamicsTargetSpec Dynamics(StrokeSpec stroke, DynamicsTarget target)
        => stroke.Dynamics?.For(target) ?? DynamicsTargetSpec.Off;

    private static int PresetIndex(DynamicsCurve curve)
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            if (DynamicsCurve.FromPreset(Presets[i]) == curve)
            {
                return i;
            }
        }

        return -1;
    }

    private static string TargetName(DynamicsTarget target) => target switch
    {
        DynamicsTarget.Opacity => "opacity",
        DynamicsTarget.ScatterScale => "scatter scale",
        DynamicsTarget.CalligraphicAngle => "nib angle",
        DynamicsTarget.Smoothing => "smoothing",
        _ => "width",
    };

    // ------------------------------------------------------------------
    // The selection, and what it agrees about
    // ------------------------------------------------------------------

    /// <summary>
    /// The strokes at <paramref name="index"/> that count towards agreement - which is exactly what
    /// `StrokeSummary.Of` counts, and for the same reason: a path whose stack is shorter has no stroke there, which
    /// is a gap in the selection rather than a disagreement about a value, and a stroke nobody draws is not
    /// something a person is comparing on canvas.
    /// </summary>
    private IReadOnlyList<StrokeSpec> AgreeingStrokesAt(int index)
    {
        var strokes = new List<StrokeSpec>();
        if (_vm is null || index < 0)
        {
            return strokes;
        }

        foreach (PathItem path in _vm.ActiveSession.SelectedPaths())
        {
            if (index < path.Strokes.Count && path.Strokes[index].HasVisibleOutline)
            {
                strokes.Add(path.Strokes[index]);
            }
        }

        return strokes;
    }

    /// <summary>
    /// Builds one control per parameter the selected effect's kind declares.
    ///
    /// The controls come from the **registry**, which is the requirement this issue names: the panel asks what the
    /// effect takes rather than knowing, so an effect that declares a new parameter gets an editor for it without
    /// anyone touching this file. Only numbers and whole numbers are built, because those are what
    /// `SetEffectParameter` accepts - a colour is not a number and is not claimed here.
    ///
    /// The value shown is read from the **inspected stroke**, so the box describes the same stroke the row came
    /// from; reading the stack's first match would show one stroke's number and write it to another.
    /// </summary>
    private void BuildEffectEditors()
    {
        EffectParameters.Children.Clear();

        if (_vm is null || EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count ||
            InspectedStrokeIndex() is not { } strokeIndex)
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];
        EffectDefinition? definition = EffectRegistry.Find(row.Label);
        if (definition is null)
        {
            return;
        }

        foreach (EffectParameter parameter in definition.Parameters)
        {
            if (parameter.Kind == EffectParameterKind.Color)
            {
                continue;
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

            var label = new TextBlock
            {
                Text = parameter.Name,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
            };

            var box = new TextBox
            {
                Text = (_vm.ActiveSession.EffectParameterValue(row.Raster, row.Index, parameter.Name, strokeIndex)
                        ?? parameter.Default).ToString("0.####", CultureInfo.InvariantCulture),
                Tag = parameter.Name,
            };

            // One handler for every box, because the name travels on the control rather than in a closure per
            // parameter - which is what keeps this loop something that can be read.
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitEffectParameter(box);
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => CommitEffectParameter(box);

            Grid.SetColumn(label, 0);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            EffectParameters.Children.Add(grid);
        }
    }

    /// <summary>Writes one edited parameter back, through the session method the operation calls.</summary>
    private void CommitEffectParameter(TextBox box)
    {
        if (_vm is null || _syncing || box.Tag is not string name ||
            EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count ||
            InspectedStrokeIndex() is not { } strokeIndex)
        {
            return;
        }

        if (!double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];
        if (_vm.ActiveSession.SetEffectParameter(row.Raster, row.Index, name, value, strokeIndex) > 0)
        {
            // Re-reads the effect, so a value that was clamped - an opacity over one, a detail under one - shows
            // as what it became rather than as what was typed.
            _syncing = true;
            BuildEffectEditors();
            _syncing = false;
        }
    }

    /// <summary>
    /// Removes the selected effect from the list it came from, through the session method the operation calls.
    ///
    /// The row remembers whether it is an outline or a raster effect, because those are two lists in the model and
    /// an index into one names a different effect in the other. The stroke is the inspected one: removing row 0 of
    /// a list that shows a **middle** stroke's effects must not take row 0 off the top of the stack.
    /// </summary>
    private void OnRemoveEffect(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count ||
            InspectedStrokeIndex() is not { } strokeIndex)
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];
        int changed = row.Raster
            ? _vm.ActiveSession.RemoveStrokeRasterEffect(row.Index, strokeIndex)
            : _vm.ActiveSession.RemoveStrokeEffect(row.Index, strokeIndex);

        if (changed > 0)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Adds the chosen effect with the registry's own defaults.
    ///
    /// The kind comes from the box, which is filled from the registry, and the defaults come from the model's
    /// records - so this never invents a parameter value and never needs to know what an effect takes. A panel that
    /// grows parameter editors will read the same declaration to build them.
    ///
    /// The stroke is the inspected one, so the effect arrives on the stroke whose list the panel is showing rather
    /// than on every stroke of the selection. With nothing inspected there is no stroke it could honestly land on,
    /// so nothing happens.
    /// </summary>
    private void OnAddEffect(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || EffectKindBox.SelectedItem is not string kind ||
            InspectedStrokeIndex() is not { } strokeIndex)
        {
            return;
        }

        EffectDefinition? definition = EffectRegistry.Find(kind);
        if (definition is null)
        {
            return;
        }

        int changed = definition.Raster
            ? _vm.ActiveSession.AddRasterEffect(new RasterEffectSpec(definition.RasterKind!.Value), strokeIndex)
            : _vm.ActiveSession.AddOutlineEffect(new OutlineEffectSpec(definition.OutlineKind!.Value), strokeIndex);

        if (changed > 0)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Applies the fields the person **actually changed** to the stroke the pane is describing - the inspected one -
    /// by calling the session, the way the operations do, rather than building the edit here.
    ///
    /// Only the changed ones, because a selection is not obliged to agree with itself: with widths of 4 and 8 the
    /// width box reads "mixed", and a method that had to be given a number for it would write one path's width over
    /// the other's from a field the person never touched. That was the old defect here - it wrote the selection's
    /// top stroke, so typing while the appearance panel showed stroke 2 changed stroke 3.
    ///
    /// With nothing selected there is no stroke to edit, and the fields keep doing what they did before - becoming
    /// the style objects drawn next get, which is why setting a width before drawing works. That path still reads
    /// every field, because there is no model there to disagree with.
    /// </summary>
    private void ApplyNow()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        if (_vm.ActiveSession.SelectedPaths().FirstOrDefault() is not { } path)
        {
            double typedWidth = Parse(StrokeWidthBox.Text) ?? 1.0;
            double typedMiter = Parse(MiterBox.Text) ?? 4.0;
            _vm.ApplyStroke(
                typedWidth,
                StrokeCapBox.SelectedIndex switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt },
                StrokeJoinBox.SelectedIndex switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter },
                typedMiter,
                StrokeAlignBox.SelectedIndex switch
                {
                    1 => StrokeAlignment.Inside,
                    2 => StrokeAlignment.Outside,
                    _ => StrokeAlignment.Center,
                },
                DashPreset(StrokeDashBox.SelectedIndex));
            return;
        }

        double? width = ChangedDouble(StrokeWidthBox, _shownWidth);
        double? miter = ChangedDouble(MiterBox, _shownMiter);
        StrokeCap? cap = Changed(StrokeCapBox, _shownCap) is { } capIndex ? MapCap(capIndex) : null;
        StrokeJoin? join = Changed(StrokeJoinBox, _shownJoin) is { } joinIndex ? MapJoin(joinIndex) : null;
        StrokeAlignment? align = Changed(StrokeAlignBox, _shownAlign) is { } alignIndex ? MapAlign(alignIndex) : null;
        DashPattern? dash = Changed(StrokeDashBox, _shownDash) is { } dashIndex ? DashPreset(dashIndex) : null;

        if (width is null && miter is null && cap is null && join is null && align is null && dash is null)
        {
            // A field committed without being edited is not an edit. Writing it anyway would put an undo step on the
            // stack that undoes to exactly where it started, which reads as "undo did nothing".
            return;
        }

        int index = _vm.InspectedStroke;
        if (index < 0 || index >= path.Strokes.Count)
        {
            // Nothing is being inspected, so there is no stroke this could honestly be applied to. Editing the top
            // one instead is exactly the disagreement the shared index exists to prevent.
            return;
        }

        _vm.ActiveSession.ApplyStrokeFieldsAt(index, width, cap, join, miter, align, dash);
    }

    /// <summary>A text field's value, or null when the person did not change it.</summary>
    private static double? ChangedDouble(TextBox box, string shown)
    {
        string text = box.Text?.Trim() ?? string.Empty;
        return text == shown ? null : Parse(text);
    }

    /// <summary>A combo's index, or null when the person did not change it - and when it is showing no value at all,
    /// which is how a mixed member is drawn.</summary>
    private static int? Changed(ComboBox box, int shown)
        => box.SelectedIndex == shown || box.SelectedIndex < 0 ? null : box.SelectedIndex;

    private static double? Parse(string? text)
        => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;

    private static StrokeCap MapCap(int index) => index switch
    {
        1 => StrokeCap.Round,
        2 => StrokeCap.Square,
        _ => StrokeCap.Butt,
    };

    private static StrokeJoin MapJoin(int index) => index switch
    {
        1 => StrokeJoin.Round,
        2 => StrokeJoin.Bevel,
        _ => StrokeJoin.Miter,
    };

    private static StrokeAlignment MapAlign(int index) => index switch
    {
        1 => StrokeAlignment.Inside,
        2 => StrokeAlignment.Outside,
        _ => StrokeAlignment.Center,
    };

    private static int CapIndex(StrokeCap cap) => cap switch
    {
        StrokeCap.Round => 1,
        StrokeCap.Square => 2,
        _ => 0,
    };

    private static int JoinIndex(StrokeJoin join) => join switch
    {
        StrokeJoin.Round => 1,
        StrokeJoin.Bevel => 2,
        _ => 0,
    };

    private static int AlignIndex(StrokeAlignment alignment) => alignment switch
    {
        StrokeAlignment.Inside => 1,
        StrokeAlignment.Outside => 2,
        _ => 0,
    };

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => OnUiThread(Refresh);
        vm.SelectionChanged += (_, _) => OnUiThread(Refresh);

        // The appearance panel is what chooses the inspected stroke, and it does so by writing the shared state
        // rather than by telling this pane. Listening for that is what keeps the fields describing the stroke the
        // other panel is showing instead of the one that was showing when this panel last refreshed.
        //
        // That state is **shared**, so a change can arrive on any thread: `style.inspectStroke` is an operation like
        // any other, and the synchronous registry runs its handler on the caller's thread rather than marshalling.
        // Everything below is UI work - the readouts onto controls and the selection onto the document - so it is
        // posted to the UI thread when it did not arrive there, the same discipline `54832f4` gave the colour pane.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.InspectedStroke) or nameof(EditorViewModel.InspectedStrokeLabel))
            {
                OnUiThread(Refresh);
            }
        };

        Refresh();
    }

    /// <summary>
    /// Runs UI work on the UI thread, wherever the change that asked for it happened.
    ///
    /// A pane observing shared state cannot assume it is told on the thread that owns its controls: the state is
    /// written by whoever made the change. Posting rather than writing where it landed is what keeps an operation
    /// invoked from a driver's thread from becoming "Call from invalid thread".
    /// </summary>
    private static void OnUiThread(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            work();
        }
        else
        {
            Dispatcher.UIThread.Post(work);
        }
    }

    /// <summary>Built-in dash presets, indexed by combo order. Lengths are in points.</summary>
    private static DashPattern DashPreset(int index) => index switch
    {
        1 => new DashPattern(new double[] { 4, 3 }),
        2 => new DashPattern(new double[] { 1, 2 }),
        3 => new DashPattern(new double[] { 4, 2, 1, 2 }),
        4 => new DashPattern(new double[] { 4, 2, 1, 2, 1, 2 }),
        _ => DashPattern.None,
    };

    private static int DashIndexOf(DashPattern dash)
    {
        for (int i = 0; i < 5; i++)
        {
            if (DashPreset(i).Equals(dash))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The stroke this pane describes: the first selected path's stroke at the shared inspected index, or null when
    /// there is nothing to describe - no selected path, no inspected stroke, or a stack shorter than the index.
    ///
    /// One place, read by both <see cref="Refresh"/> and <see cref="ApplyNow"/>, because a pane that shows one stroke
    /// and edits another is the defect this is here to remove.
    /// </summary>
    private StrokeSpec? InspectedStrokeSpec()
    {
        if (_vm?.ActiveSession.SelectedPaths().FirstOrDefault() is not { } path)
        {
            return null;
        }

        int index = _vm.InspectedStroke;
        return index >= 0 && index < path.Strokes.Count ? path.Strokes[index] : null;
    }

    /// <summary>
    /// The inspected stroke's index, or null when nothing is being inspected - the form the session methods take,
    /// where naming a stroke and naming none are different requests.
    ///
    /// Derived from <see cref="InspectedStrokeSpec"/> rather than from `_vm.InspectedStroke` alone, so the effects
    /// list and its buttons agree with the geometry fields about whether there is a stroke here at all.
    /// </summary>
    private int? InspectedStrokeIndex() => InspectedStrokeSpec() is null ? null : _vm!.InspectedStroke;

    /// <summary>
    /// Shows what the selection says about the inspected stroke: each member's **common** value, or the word
    /// "mixed" where the selection disagrees.
    ///
    /// `StrokeSummary` is the authority on agreement, so the pane asks it rather than forming a second opinion that
    /// could drift from the one `style.commonStroke` reports. The value shown for an agreeing member is the
    /// summary's common value, never one path's, which is the difference between describing a selection and
    /// describing whichever path happened to be first.
    /// </summary>
    private void Refresh()
    {
        // The same words the appearance panel uses, so the two panels cannot be describing different strokes without
        // saying so.
        StrokeTargetLabel.Text = _vm?.InspectedStrokeLabel ?? "none";

        // The export warning is about the selected objects (a filter, a blend mode, whether any stroke carries a
        // raster effect), so it reads the whole selection even when no stroke is inspected.
        ShowExportWarning();

        // One read for the geometry, the sections and the effects list, so none of them can describe a different
        // stroke from the others - a list showing the top of the stack while the buttons edited it was the same
        // disagreement, one level down.
        StrokeSpec? stroke = InspectedStrokeSpec();
        int index = _vm?.InspectedStroke ?? -1;

        RefreshEffects(stroke);

        StrokeSummary summary = StrokeSummary.Of(
            _vm?.ActiveSession.SelectedPaths() ?? Enumerable.Empty<PathItem>(), index);

        var mixed = new List<string>();
        _syncing = true;

        if (stroke is null)
        {
            // Nothing inspected is a state, and it is not the same as mixed: there is no value to compare, so the
            // fields are empty rather than saying they disagree.
            StrokeWidthBox.Text = string.Empty;
            MiterBox.Text = string.Empty;
            _shownWidth = string.Empty;
            _shownMiter = string.Empty;
            MixedLabel.Text = string.Empty;
            MixedLabel.IsVisible = false;
            RefreshStrokeType(index, null);
            RefreshBrush(index, null);
            RefreshDynamics(index, null);
            _syncing = false;
            return;
        }

        // The summary is empty when nothing at this index is drawn, and then there is no selection-wide statement
        // to make: the inspected stroke's own value is what the pane has, and what it will edit.
        double? shownWidth = summary.Width ?? stroke.Width;
        double? shownMiter = summary.MiterLimit ?? stroke.MiterLimit;
        bool widthMixed = summary.WidthMixed;
        bool miterMixed = summary.MiterMixed;
        bool capMixed = summary.CapMixed;
        bool joinMixed = summary.JoinMixed;
        bool alignMixed = summary.AlignmentMixed;
        StrokeCap shownCap = summary.Cap ?? stroke.Cap;
        StrokeJoin shownJoin = summary.Join ?? stroke.Join;
        StrokeAlignment shownAlign = summary.Alignment ?? stroke.Alignment;

        // Dash is a `StrokeSummary` member like the rest, so it is read from the same authority rather than worked
        // out a second time here - a second reading is one that can drift from the one `style.commonStroke`
        // reports. The inspected stroke's own dash is the fallback for a selection with nothing drawn at this index,
        // which is the same fallback the geometry fields take.
        DashPattern shownDash = summary.Dash ?? stroke.Dash;
        bool dashMixed = summary.DashMixed;

        if (!StrokeWidthBox.IsFocused)
        {
            StrokeWidthBox.Text = widthMixed ? MixedWord : shownWidth!.Value.ToString("0.##", CultureInfo.InvariantCulture);
            _shownWidth = StrokeWidthBox.Text;
        }

        if (!MiterBox.IsFocused)
        {
            MiterBox.Text = miterMixed ? MixedWord : shownMiter!.Value.ToString("0.##", CultureInfo.InvariantCulture);
            _shownMiter = MiterBox.Text;
        }

        StrokeCapBox.PlaceholderText = capMixed ? MixedWord : string.Empty;
        StrokeCapBox.SelectedIndex = capMixed ? -1 : CapIndex(shownCap);
        _shownCap = StrokeCapBox.SelectedIndex;

        StrokeJoinBox.PlaceholderText = joinMixed ? MixedWord : string.Empty;
        StrokeJoinBox.SelectedIndex = joinMixed ? -1 : JoinIndex(shownJoin);
        _shownJoin = StrokeJoinBox.SelectedIndex;

        StrokeAlignBox.PlaceholderText = alignMixed ? MixedWord : string.Empty;
        StrokeAlignBox.SelectedIndex = alignMixed ? -1 : AlignIndex(shownAlign);
        _shownAlign = StrokeAlignBox.SelectedIndex;

        StrokeDashBox.PlaceholderText = dashMixed ? MixedWord : string.Empty;
        StrokeDashBox.SelectedIndex = dashMixed ? -1 : DashIndexOf(shownDash);
        _shownDash = StrokeDashBox.SelectedIndex;

        if (widthMixed)
        {
            mixed.Add("width");
        }

        if (capMixed)
        {
            mixed.Add("cap");
        }

        if (joinMixed)
        {
            mixed.Add("join");
        }

        if (miterMixed)
        {
            mixed.Add("miter");
        }

        if (alignMixed)
        {
            mixed.Add("align");
        }

        if (dashMixed)
        {
            mixed.Add("dash");
        }

        RefreshStrokeType(index, stroke);
        if (ProfileMixedAt(index))
        {
            mixed.Add("profile");
        }

        RefreshBrush(index, stroke);
        if (BrushMixedAt(index))
        {
            mixed.Add("brush");
        }

        mixed.AddRange(RefreshDynamics(index, stroke));

        MixedLabel.Text = mixed.Count == 0 ? string.Empty : MixedWord + ": " + string.Join(", ", mixed);
        MixedLabel.IsVisible = mixed.Count > 0;

        _syncing = false;
    }

    /// <summary>
    /// Says what the PDF export will leave out of what is selected.
    ///
    /// The list comes from <see cref="PdfExportSupport"/> - the same declaration `PdfExportSupportTests` derives
    /// from the exported bytes - so this cannot warn about a gap the exporter no longer has, or stay silent about
    /// one it has grown. Saying it here is the point: a person should not have to open the exported file to find
    /// out that a blur was not in it.
    /// </summary>
    private void ShowExportWarning()
    {
        var present = new List<string>();

        foreach (LayerItem item in _vm?.SelectedObjects ?? (IReadOnlyList<LayerItem>)Array.Empty<LayerItem>())
        {
            switch (item)
            {
                // Raster effects are per stroke; a filter and a blend mode are per object. The leaf cases are all
                // written now, so they are listed and then filtered by the declaration rather than being assumed -
                // a warning that fires on something the exporter carries is one nobody reads.
                case PathItem path:
                    if (path.Strokes.Any(stroke => stroke.HasRasterEffects))
                    {
                        present.Add("rasterEffect");
                    }

                    if (path.FilterId is { Length: > 0 })
                    {
                        present.Add("filter");
                    }

                    if (path.BlendMode != BlendMode.Normal)
                    {
                        present.Add("blendMode");
                    }

                    break;

                // **A group's blend is the one that is still not carried.** CSS composites a group as a unit
                // against the backdrop, which PDF needs an isolated transparency group for - and that is a form
                // XObject this exporter does not emit. The declaration is asked rather than the fact repeated here.
                case ArtGroup group when group.BlendMode != BlendMode.Normal:
                    present.Add("blendModeGroup");
                    break;
            }
        }

        string[] missing = PdfExportSupport.Lossy
            .Where(feature => present.Contains(feature.Name))
            .Select(feature => $"{feature.Name}: {feature.Note}")
            .ToArray();

        ExportWarning.Text = missing.Length == 0 ? string.Empty : "Not in the PDF export: " + string.Join(" ", missing);
        ExportWarning.IsVisible = missing.Length > 0;
    }

    /// <summary>One row of the effects list: the effect, and whether the export will carry it.</summary>
    private sealed record EffectRow(string Label, string Badge, int Index, bool Raster);

    /// <summary>The rows last shown, so a button can ask which one is selected without guessing at the control's items.</summary>
    private List<EffectRow> _effectRows = new();

    /// <summary>
    /// The inspected stroke's effects, in the order they apply.
    ///
    /// Outline effects first and then the raster ones, because those are two lists the model keeps separately and
    /// pretending otherwise would let a move put an outline effect into the raster list. The row remembers which
    /// list it came from, which is what makes the move land in the right one. Null means nothing is inspected, and
    /// then there is nothing to list.
    /// </summary>
    private void RefreshEffects(StrokeSpec? stroke)
    {
        var rows = new List<EffectRow>();

        if (stroke is not null)
        {
            var effects = stroke.AllEffects.ToList();
            for (int i = 0; i < effects.Count; i++)
            {
                rows.Add(new EffectRow(effects[i].Kind.ToString(), string.Empty, i, Raster: false));
            }

            if (stroke.AllRasterEffects is { } raster)
            {
                for (int i = 0; i < raster.Count; i++)
                {
                    // Raster effects are the honest case: the export does not carry them, so the row says so
                    // rather than leaving it to the block below to explain after the fact.
                    rows.Add(new EffectRow(raster[i].Kind.ToString(), "not exported", i, Raster: true));
                }
            }
        }

        int keep = EffectList.SelectedIndex;
        _effectRows = rows;
        EffectList.ItemsSource = rows;
        EffectList.SelectedIndex = rows.Count == 0 ? -1 : Math.Clamp(keep, 0, rows.Count - 1);
        MoveEffectUpButton.IsEnabled = rows.Count > 1;
        MoveEffectDownButton.IsEnabled = rows.Count > 1;
    }

    /// <summary>
    /// Moves the selected effect within its own list, through the same session method the operation calls, and then
    /// re-reads the list so what is shown is the model's order rather than the list's own idea of it.
    ///
    /// "+1" is towards the top of the panel, which is later in the list: effects apply from the start, so the bottom
    /// row is applied first and moving a row up moves it later.
    /// </summary>
    private void MoveEffect(int direction)
    {
        if (_vm is null || EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count ||
            InspectedStrokeIndex() is not { } strokeIndex)
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];

        int to = row.Index + direction;
        int changed = row.Raster
            ? _vm.ActiveSession.MoveStrokeRasterEffect(row.Index, to, strokeIndex)
            : _vm.ActiveSession.MoveStrokeEffect(row.Index, to, strokeIndex);

        if (changed == 0)
        {
            return;
        }

        Refresh();
    }

}
