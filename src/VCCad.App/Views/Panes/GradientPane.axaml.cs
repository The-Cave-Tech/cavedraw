using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Color;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;
using ModelStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The Gradient panel: the kind and spread, the ramp with its stops, the selected stop's colour
/// through the same ring-and-triangle picker the Color pane uses, and the geometry that positions
/// the ramp on the object.
///
/// Edits go to the selection through <see cref="SetFillCommand"/>, the same command the Color pane
/// uses, so a gradient change is one undo step like any other style change.
/// </summary>
public partial class GradientPane : UserControl
{
    private static readonly ModelStop[] DefaultStops =
    {
        new(0.0, ColorRgb.White),
        new(1.0, ColorRgb.Black),
    };

    private readonly ColorPickerModel _stopModel = new(new Point2D(0.0, 0.0), 100.0, ColorRgb.Red);

    private EditorViewModel? _vm;
    private bool _syncing;

    /// <summary>The paths this pane has already changed live, with what they looked like before.</summary>
    private List<(PathItem Path, FillSpec Before)>? _fillBefore;

    private int _stopIndex;

    public GradientPane()
    {
        InitializeComponent();

        StopWheel.Model = _stopModel;
        StopWheel.ColorChanged += (_, _) => OnStopColourChanged();
        StopWheel.ColorCommitted += (_, _) => Commit();

        Ramp.SpecChanged += (_, spec) => ApplyLive(spec);
        Ramp.SpecCommitted += (_, _) => Commit();
        Ramp.StopActivated += (_, index) => SelectStop(index);

        ApplyButton.Click += (_, _) => MakeGradient();
        ReverseButton.Click += (_, _) => Edit(spec => spec with
        {
            Stops = spec.Stops
                .Select(s => s with { Position = 1.0 - s.Position })
                .OrderBy(s => s.Position)
                .ToList(),
        });

        KindBox.SelectionChanged += (_, _) => EditKind();
        SpreadBox.SelectionChanged += (_, _) => Edit(spec => spec with { Spread = SelectedSpread() });
        StopOpacityBar.ValueChanged += (_, _) => OnStopOpacityChanged();

        Hook(PositionBox, () => EditStop(stop => stop with { Position = Number(PositionBox, stop.Position, 0, 1) }));
        Hook(StopOpacityBox, () => EditStop(stop => stop with { Opacity = Number(StopOpacityBox, stop.Opacity, 0, 1) }));
        Hook(AngleBox, () => EditLinear());
        Hook(ScaleBox, () => EditLinear());
        Hook(CentreXBox, () => EditRadial());
        Hook(CentreYBox, () => EditRadial());
        Hook(AspectBox, () => EditRadial());
    }

    /// <summary>Wires a numeric field to commit on Enter and on losing focus, like the Color pane.</summary>
    private static void Hook(TextBox box, Action apply)
    {
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                apply();
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => apply();
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    public void Detach()
    {
        _vm = null;
    }

    /// <summary>The gradient in force: the selected path's, or the editor's current fill.</summary>
    private GradientSpec Current =>
        (_vm?.PrimarySelection as PathItem)?.Fill.Gradient
        ?? _vm?.CurrentFill.Gradient
        ?? new GradientSpec { Stops = DefaultStops };

    private void Refresh()
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            bool hasGradient = (_vm.PrimarySelection as PathItem)?.Fill.HasGradient == true
                || _vm.CurrentFill.HasGradient;
            ApplyButton.IsEnabled = !hasGradient;

            GradientSpec spec = Current;
            if (_stopIndex >= spec.Stops.Count)
            {
                _stopIndex = Math.Max(0, spec.Stops.Count - 1);
            }

            Ramp.Spec = spec;
            Ramp.Select(_stopIndex);

            KindBox.ItemsSource = new[] { "Linear", "Radial", "Freeform" };
            KindBox.SelectedIndex = (int)spec.Kind;
            SpreadBox.ItemsSource = new[] { "Pad", "Reflect", "Repeat" };
            SpreadBox.SelectedIndex = (int)spec.Spread;

            LinearRow.IsVisible = spec.Kind == GradientKind.Linear;
            RadialRow.IsVisible = spec.Kind == GradientKind.Radial;

            UpdateStopFields(spec);
            UpdateGeometryFields(spec);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateStopFields(GradientSpec spec)
    {
        if (spec.Stops.Count == 0)
        {
            return;
        }

        _stopIndex = Math.Clamp(_stopIndex, 0, spec.Stops.Count - 1);
        ModelStop stop = spec.Stops[_stopIndex];
        PositionBox.Text = Number(stop.Position);
        StopOpacityBox.Text = Number(stop.Opacity);
        _stopModel.SetColor(stop.Color);
        StopWheel.Refresh();
        StopOpacityBar.Color = stop.Color;
        StopOpacityBar.SetValue(stop.Opacity);
    }

    private void UpdateGeometryFields(GradientSpec spec)
    {
        Vector2D direction = spec.End - spec.Start;
        double angle = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
        if (angle < 0)
        {
            angle += 360.0;
        }

        AngleBox.Text = Number(angle);
        ScaleBox.Text = Number(direction.Length);
        CentreXBox.Text = Number(spec.Center.X);
        CentreYBox.Text = Number(spec.Center.Y);
        AspectBox.Text = Number(spec.RadiusY <= 0 ? 1.0 : spec.RadiusX / spec.RadiusY);
    }

    /// <summary>Points the panel at a stop, which is what double-clicking a marker asks for.</summary>
    public void SelectStop(int index)
    {
        _stopIndex = Math.Max(0, index);
        Refresh();
    }

    /// <summary>
    /// Turns a solid fill into a gradient. The fill's own colour becomes the gradient's fallback
    /// and its midpoint stop, so nothing about the object's colour changes at the moment of the
    /// switch and the model's promise - a fill never has no colour - holds.
    /// </summary>
    private void MakeGradient()
    {
        if (_vm is null)
        {
            return;
        }

        ColorRgb basis = (_vm.PrimarySelection as PathItem)?.Fill.Color ?? _vm.CurrentFill.Color;
        var spec = new GradientSpec
        {
            Kind = GradientKind.Linear,
            Stops = new[]
            {
                new ModelStop(0.0, basis),
                new ModelStop(1.0, basis.WithAlpha(0.0)),
            },
        };

        _stopIndex = 0;
        ApplyLive(spec);
        Commit();
        Refresh();
    }

    private void EditKind()
    {
        if (_syncing)
        {
            return;
        }

        Edit(spec => spec with { Kind = (GradientKind)Math.Clamp(KindBox.SelectedIndex, 0, 3) });
    }

    private void EditLinear()
    {
        if (_syncing)
        {
            return;
        }

        GradientSpec spec = Current;
        double angle = Number(AngleBox, 0, 0, 360) * Math.PI / 180.0;
        double scale = Math.Clamp(Number(ScaleBox, 1, 0.01, 10), 0.01, 10.0);
        var half = new Vector2D(Math.Cos(angle) * scale / 2.0, Math.Sin(angle) * scale / 2.0);
        var centre = new Point2D(0.5, 0.5);
        Edit(_ => spec with { Start = centre - half, End = centre + half });
    }

    private void EditRadial()
    {
        if (_syncing)
        {
            return;
        }

        GradientSpec spec = Current;
        var centre = new Point2D(
            Math.Clamp(Number(CentreXBox, spec.Center.X, -4, 5), -4, 5),
            Math.Clamp(Number(CentreYBox, spec.Center.Y, -4, 5), -4, 5));
        double aspect = Math.Clamp(Number(AspectBox, 1, 0.01, 100), 0.01, 100.0);
        Edit(_ => spec with
        {
            Center = centre,
            RadiusX = spec.RadiusX,
            RadiusY = spec.RadiusX <= 0 ? spec.RadiusY : spec.RadiusX / aspect,
        });
    }

    /// <summary>Changes one stop of the current gradient as a single edit.</summary>
    private void EditStop(Func<ModelStop, ModelStop> change)
    {
        if (_syncing)
        {
            return;
        }

        Edit(spec =>
        {
            if (spec.Stops.Count == 0)
            {
                return spec;
            }

            var stops = spec.Stops.ToList();
            int index = Math.Clamp(_stopIndex, 0, stops.Count - 1);
            stops[index] = change(stops[index]).Clamped();
            return spec with { Stops = stops };
        });
    }

    private void OnStopColourChanged()
    {
        if (_syncing)
        {
            return;
        }

        ColorRgb colour = _stopModel.Color;
        EditStop(stop => stop with { Color = colour });
        StopOpacityBar.Color = colour;
    }

    private void OnStopOpacityChanged()
    {
        if (_syncing)
        {
            return;
        }

        double opacity = StopOpacityBar.Value;
        EditStop(stop => stop with { Opacity = opacity });
    }

    private GradientSpread SelectedSpread()
        => (GradientSpread)Math.Clamp(SpreadBox.SelectedIndex, 0, 2);

    private void Edit(Func<GradientSpec, GradientSpec> change)
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        GradientSpec spec = change(Current);
        ApplyLive(spec);
        Commit();
        Refresh();
    }

    /// <summary>
    /// Shows the change immediately, without a command: the paths carry the new gradient and the
    /// editor's current fill follows, so the canvas repaints as the marker moves.
    /// </summary>
    private void ApplyLive(GradientSpec spec)
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        ColorRgb flattened = spec.Sample(0.5).Color;

        if (_vm.PrimarySelection is PathItem)
        {
            _fillBefore ??= _vm.SelectedPaths().Select(p => (p, p.Fill)).ToList();
            foreach ((PathItem path, _) in _fillBefore)
            {
                path.Fill = path.Fill with
                {
                    IsVisible = true,
                    Gradient = spec,
                    Color = path.Fill.HasGradient ? path.Fill.Color : flattened,
                };
            }
        }

        _vm.CurrentFill = _vm.CurrentFill with
        {
            IsVisible = true,
            Gradient = spec,
            Color = _vm.CurrentFill.HasGradient ? _vm.CurrentFill.Color : flattened,
        };

        _vm.RaiseTransformChanged();
    }

    /// <summary>
    /// Commits the live change as ONE undo step.
    ///
    /// <see cref="SetFillCommand"/> records the value it finds on its first <c>Do</c>, and the live
    /// pass has already written the new gradient onto the path, so committing naively would record
    /// the new value as its own "previous" and undo would do nothing. Each path is therefore put
    /// back to the value it had before the edit first, so the command captures that.
    /// </summary>
    private void Commit()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        if (_fillBefore is { Count: > 0 })
        {
            var edits = new List<IUndoableCommand>();
            foreach ((PathItem path, FillSpec before) in _fillBefore)
            {
                FillSpec after = path.Fill;
                path.Fill = before;
                edits.Add(new SetFillCommand(path, after));
            }

            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Gradient", edits));
        }

        _fillBefore = null;
        Refresh();
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Number(TextBox box, double fallback, double low, double high)
        => double.TryParse((box.Text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
           && double.IsFinite(value)
            ? Math.Clamp(value, low, high)
            : fallback;
}
