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
    }

    /// <summary>Applies the current stroke fields to the selection (and current style).</summary>
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
        _vm.ApplyStroke(width, cap, join, miter, align, DashPreset(StrokeDashBox.SelectedIndex));
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.SelectionChanged += (_, _) => Refresh();
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

    private void Refresh()
    {
        if (_vm?.PrimarySelection is not PathItem path)
        {
            StrokeWidthBox.Text = string.Empty;
            MiterBox.Text = string.Empty;
            ShowExportWarning(null);
            return;
        }

        ShowExportWarning(path);

        _syncing = true;
        if (!StrokeWidthBox.IsFocused)
        {
            StrokeWidthBox.Text = path.Stroke.Width.ToString("0.##", CultureInfo.InvariantCulture);
        }

        if (!MiterBox.IsFocused)
        {
            MiterBox.Text = path.Stroke.MiterLimit.ToString("0.##", CultureInfo.InvariantCulture);
        }

        StrokeCapBox.SelectedIndex = path.Stroke.Cap switch
        {
            StrokeCap.Round => 1,
            StrokeCap.Square => 2,
            _ => 0,
        };
        StrokeJoinBox.SelectedIndex = path.Stroke.Join switch
        {
            StrokeJoin.Round => 1,
            StrokeJoin.Bevel => 2,
            _ => 0,
        };
        StrokeAlignBox.SelectedIndex = path.Stroke.Alignment switch
        {
            StrokeAlignment.Inside => 1,
            StrokeAlignment.Outside => 2,
            _ => 0,
        };
        StrokeDashBox.SelectedIndex = DashIndexOf(path.Stroke.Dash);
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

}
