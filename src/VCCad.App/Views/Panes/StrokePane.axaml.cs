using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>Stroke tab: width, cap, join and miter limit for the selection.</summary>
public partial class StrokePane : UserControl
{
    private EditorViewModel? _vm;

    public StrokePane()
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
            StrokeWidthBox.Text = string.Empty;
            MiterBox.Text = string.Empty;
            return;
        }

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
    }

    private void OnApplyStroke(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        double width = double.TryParse(StrokeWidthBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : 1.0;
        double miter = double.TryParse(MiterBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ? m : 4.0;
        StrokeCap cap = StrokeCapBox.SelectedIndex switch { 1 => StrokeCap.Round, 2 => StrokeCap.Square, _ => StrokeCap.Butt };
        StrokeJoin join = StrokeJoinBox.SelectedIndex switch { 1 => StrokeJoin.Round, 2 => StrokeJoin.Bevel, _ => StrokeJoin.Miter };
        StrokeAlignment align = StrokeAlignBox.SelectedIndex switch { 1 => StrokeAlignment.Inside, 2 => StrokeAlignment.Outside, _ => StrokeAlignment.Center };
        _vm.ApplyStroke(width, cap, join, miter, align);
    }
}
