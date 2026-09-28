using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Pdf.Fonts;

namespace VCCad.App.Views.Panes;

/// <summary>Text tab: content, font family/size/weight, and colour for the
/// selected text object.</summary>
public partial class TextPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncingAlign;

    public TextPane()
    {
        InitializeComponent();
        FamilyBox.ItemsSource = StandardFontResolver.OfferedFamilies();
        AlignBox.SelectionChanged += (_, _) =>
        {
            if (_syncingAlign || _vm is null)
            {
                return;
            }

            _vm.SetTextAlignment(AlignBox.SelectedIndex switch
            {
                1 => TextAlignment.Center,
                2 => TextAlignment.Right,
                _ => TextAlignment.Left,
            });
        };
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Refresh();
        vm.SelectionChanged += (_, _) => Refresh();
        vm.TransformChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (_vm?.PrimarySelection is not TextItem text || text.Runs.Count == 0)
        {
            ContentBox.Text = string.Empty;
            return;
        }

        TextRun run = text.Runs[0];
        if (!ContentBox.IsFocused)
        {
            ContentBox.Text = text.PlainText;
        }

        FamilyBox.SelectedItem = run.FontFamily;
        if (!SizeBox.IsFocused)
        {
            SizeBox.Text = run.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
        }

        BoldBox.IsChecked = run.Bold;
        ItalicBox.IsChecked = run.Italic;

        ColorR.Text = Math.Round(text.Color.R * 255).ToString("0", CultureInfo.InvariantCulture);
        ColorG.Text = Math.Round(text.Color.G * 255).ToString("0", CultureInfo.InvariantCulture);
        ColorB.Text = Math.Round(text.Color.B * 255).ToString("0", CultureInfo.InvariantCulture);
        ColorA.Text = Math.Round(text.Color.A * 255).ToString("0", CultureInfo.InvariantCulture);

        _syncingAlign = true;
        AlignBox.SelectedIndex = text.Alignment switch
        {
            TextAlignment.Center => 1,
            TextAlignment.Right => 2,
            _ => 0,
        };
        _syncingAlign = false;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        string family = FamilyBox.SelectedItem as string ?? TextItem.DefaultFontFamily;
        double size = double.TryParse(SizeBox.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            ? Math.Max(1, s)
            : 12;

        byte Channel(TextBox box)
            => double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? (byte)Math.Clamp(Math.Round(v), 0, 255)
                : (byte)0;

        var color = ColorRgb.FromBytes(Channel(ColorR), Channel(ColorG), Channel(ColorB), Channel(ColorA));
        int? runIndex = _vm.IsEditingText ? _vm.TextCaretRunIndex : null;
        _vm.UpdateSelectedText(ContentBox.Text ?? string.Empty, family, size,
            BoldBox.IsChecked == true, ItalicBox.IsChecked == true, color, runIndex);
        Refresh();
    }
}
