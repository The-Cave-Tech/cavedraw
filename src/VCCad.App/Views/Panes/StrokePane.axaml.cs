using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Pdf;

namespace VCCad.App.Views.Panes;

/// <summary>Stroke tab: width, cap, join and miter limit for the selection.</summary>
public partial class StrokePane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;

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

    /// <summary>
    /// Builds one control per parameter the selected effect's kind declares.
    ///
    /// The controls come from the **registry**, which is the requirement this issue names: the panel asks what the
    /// effect takes rather than knowing, so an effect that declares a new parameter gets an editor for it without
    /// anyone touching this file. Only numbers and whole numbers are built, because those are what
    /// `SetEffectParameter` accepts - a colour is not a number and is not claimed here.
    /// </summary>
    private void BuildEffectEditors()
    {
        EffectParameters.Children.Clear();

        if (_vm is null || EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count)
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
                Text = (_vm.ActiveSession.EffectParameterValue(row.Raster, row.Index, parameter.Name) ?? parameter.Default)
                    .ToString("0.####", CultureInfo.InvariantCulture),
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
            EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count)
        {
            return;
        }

        if (!double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];
        if (_vm.ActiveSession.SetEffectParameter(row.Raster, row.Index, name, value) > 0)
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
    /// an index into one names a different effect in the other.
    /// </summary>
    private void OnRemoveEffect(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count)
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];
        int changed = row.Raster
            ? _vm.ActiveSession.RemoveStrokeRasterEffect(row.Index)
            : _vm.ActiveSession.RemoveStrokeEffect(row.Index);

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
    /// </summary>
    private void OnAddEffect(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || EffectKindBox.SelectedItem is not string kind)
        {
            return;
        }

        EffectDefinition? definition = EffectRegistry.Find(kind);
        if (definition is null)
        {
            return;
        }

        int changed = definition.Raster
            ? _vm.ActiveSession.AddRasterEffect(new RasterEffectSpec(definition.RasterKind!.Value))
            : _vm.ActiveSession.AddOutlineEffect(new OutlineEffectSpec(definition.OutlineKind!.Value));

        if (changed > 0)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Applies the current fields to **the stroke the pane is describing** - the inspected one - by calling the
    /// session, the way the operations do, rather than building the edit here.
    ///
    /// The old version of this wrote the selection's **top** stroke (`PathItem.Stroke`, the compatibility property),
    /// so typing a width while the appearance panel showed stroke 2 changed stroke 3: one panel describing a stroke
    /// the other was not editing. Naming the index is what closes that, and the panel does not build the edit itself
    /// because a capability living only in a click handler is a defect here.
    ///
    /// With nothing selected there is no stroke to edit, and the fields keep doing what they did before - becoming
    /// the style objects drawn next get, which is why setting a width before drawing works.
    /// </summary>
    private void ApplyNow()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        double width = double.TryParse(StrokeWidthBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : 1.0;
        double miter = double.TryParse(MiterBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : 4.0;
        StrokeCap cap = StrokeCapBox.SelectedIndex switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt };
        StrokeJoin join = StrokeJoinBox.SelectedIndex switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter };
        StrokeAlignment align = StrokeAlignBox.SelectedIndex switch { 1 => StrokeAlignment.Inside, 2 => StrokeAlignment.Outside, _ => StrokeAlignment.Center };
        DashPattern dash = DashPreset(StrokeDashBox.SelectedIndex);

        if (_vm.ActiveSession.SelectedPaths().FirstOrDefault() is not { } path)
        {
            _vm.ApplyStroke(width, cap, join, miter, align, dash);
            return;
        }

        int index = _vm.InspectedStroke;
        if (index < 0 || index >= path.Strokes.Count)
        {
            // Nothing is being inspected, so there is no stroke this could honestly be applied to. Editing the top
            // one instead is exactly the disagreement the shared index exists to prevent.
            return;
        }

        _vm.ActiveSession.ApplyStrokeAt(index, width, cap, join, miter, align, dash);
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.SelectionChanged += (_, _) => Refresh();

        // The appearance panel is what chooses the inspected stroke, and it does so by writing the shared state
        // rather than by telling this pane. Listening for that is what keeps the fields describing the stroke the
        // other panel is showing instead of the one that was showing when this panel last refreshed.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.InspectedStroke) or nameof(EditorViewModel.InspectedStrokeLabel))
            {
                Refresh();
            }
        };

        Refresh();
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

    private void Refresh()
    {
        PathItem? path = _vm?.ActiveSession.SelectedPaths().FirstOrDefault();

        // The same words the appearance panel uses, so the two panels cannot be describing different strokes without
        // saying so.
        StrokeTargetLabel.Text = _vm?.InspectedStrokeLabel ?? "none";

        // The export warning is about the object (its filter, its blend mode, whether any of its strokes carries a
        // raster effect), so it reads the path even when no stroke is inspected. The effects list deliberately still
        // reads the top of the stack: the effect session methods work across the stack rather than on one member, so
        // pointing the list at the inspected stroke would show one stroke's effects while the buttons edited
        // another's. Widening those is separate work; leaving the list as it was is at least honest about what it is.
        ShowExportWarning(path);
        RefreshEffects(path);

        StrokeSpec? stroke = InspectedStrokeSpec();
        if (stroke is null)
        {
            _syncing = true;
            StrokeWidthBox.Text = string.Empty;
            MiterBox.Text = string.Empty;
            _syncing = false;
            return;
        }

        _syncing = true;
        if (!StrokeWidthBox.IsFocused)
        {
            StrokeWidthBox.Text = stroke.Width.ToString("0.##", CultureInfo.InvariantCulture);
        }

        if (!MiterBox.IsFocused)
        {
            MiterBox.Text = stroke.MiterLimit.ToString("0.##", CultureInfo.InvariantCulture);
        }

        StrokeCapBox.SelectedIndex = stroke.Cap switch
        {
            StrokeCap.Round => 1,
            StrokeCap.Square => 2,
            _ => 0,
        };
        StrokeJoinBox.SelectedIndex = stroke.Join switch
        {
            StrokeJoin.Round => 1,
            StrokeJoin.Bevel => 2,
            _ => 0,
        };
        StrokeAlignBox.SelectedIndex = stroke.Alignment switch
        {
            StrokeAlignment.Inside => 1,
            StrokeAlignment.Outside => 2,
            _ => 0,
        };
        StrokeDashBox.SelectedIndex = DashIndexOf(stroke.Dash);
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
    private void ShowExportWarning(PathItem? path)
    {
        var present = new List<string>();

        if (path is not null)
        {
            // Raster effects are per stroke; a filter and a blend mode are per object. All three are declared as
            // not written, and the declaration is asked rather than a list being repeated here.
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
    /// The stroke's effects, in the order they apply.
    ///
    /// Outline effects first and then the raster ones, because those are two lists the model keeps separately and
    /// pretending otherwise would let a move put an outline effect into the raster list. The row remembers which
    /// list it came from, which is what makes the move land in the right one.
    /// </summary>
    private void RefreshEffects(PathItem? path)
    {
        var rows = new List<EffectRow>();

        if (path is not null)
        {
            StrokeSpec stroke = path.Stroke;
            for (int i = 0; i < stroke.AllEffects.Count; i++)
            {
                OutlineEffectSpec effect = stroke.AllEffects.ElementAt(i);
                rows.Add(new EffectRow(effect.Kind.ToString(), string.Empty, i, Raster: false));
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
        if (_vm is null || EffectList.SelectedIndex < 0 || EffectList.SelectedIndex >= _effectRows.Count)
        {
            return;
        }

        EffectRow row = _effectRows[EffectList.SelectedIndex];

        int to = row.Index + direction;
        int changed = row.Raster
            ? _vm.ActiveSession.MoveStrokeRasterEffect(row.Index, to)
            : _vm.ActiveSession.MoveStrokeEffect(row.Index, to);

        if (changed == 0)
        {
            return;
        }

        Refresh();
    }

}
