using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.Fonts;
using VCCad.Core.Model;
using TextAlignment = VCCad.Core.Model.TextAlignment;

namespace VCCad.App.Views;

/// <summary>
/// The contextual text toolbar: the type controls, shown while a text block is being
/// edited and hidden the rest of the time.
///
/// It is deliberately built from ordinary controls with names, because everything here
/// also has to be reachable through <c>ui.find</c> and <c>ui.setValue</c>. A formatting
/// control that only the person can reach is as much a defect as one only the assistant
/// can reach.
/// </summary>
public sealed class TextToolbar
{
    private readonly EditorView _view;
    private readonly ComboBox _font;
    private readonly TextBox _size;
    private readonly ToggleButton _bold;
    private readonly ToggleButton _italic;
    private readonly ComboBox _align;
    private readonly TextBox _lineSpacing;
    private readonly TextBox _paragraphSpacing;
    private readonly TextBox _rotation;
    private readonly TextBox _color;
    private readonly Border _host;

    private bool _suppress;

    public TextToolbar(EditorView view)
    {
        _view = view;
        _host = view.FindControl<Border>("TextToolbar")!;
        _font = view.FindControl<ComboBox>("TtFont")!;
        _size = view.FindControl<TextBox>("TtSize")!;
        _bold = view.FindControl<ToggleButton>("TtBold")!;
        _italic = view.FindControl<ToggleButton>("TtItalic")!;
        _align = view.FindControl<ComboBox>("TtAlign")!;
        _lineSpacing = view.FindControl<TextBox>("TtLineSpacing")!;
        _paragraphSpacing = view.FindControl<TextBox>("TtParagraphSpacing")!;
        _rotation = view.FindControl<TextBox>("TtRotation")!;
        _color = view.FindControl<TextBox>("TtColor")!;

        _font.ItemsSource = StandardFontResolver.OfferedFamilies();

        _font.SelectionChanged += (_, _) => ApplyFace();
        _size.LostFocus += (_, _) => ApplyFace();
        _size.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyFace(); } };
        _bold.IsCheckedChanged += (_, _) => ApplyFace();
        _italic.IsCheckedChanged += (_, _) => ApplyFace();
        _align.SelectionChanged += (_, _) => ApplyParagraph();
        _lineSpacing.LostFocus += (_, _) => ApplyParagraph();
        _paragraphSpacing.LostFocus += (_, _) => ApplyParagraph();
        _rotation.LostFocus += (_, _) => ApplyParagraph();
        _color.LostFocus += (_, _) => ApplyColor();

        Button width = view.FindControl<Button>("TtWidth")!;
        width.Click += OnSetWidth;
    }

    /// <summary>Shows the toolbar while editing and mirrors the current run into it.</summary>
    public void Sync()
    {
        bool editing = _view.ViewModel.IsEditingText;
        _host.IsVisible = editing;
        if (!editing)
        {
            return;
        }

        List<TextItem> items = _view.ViewModel.SelectedTextItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        TextItem item = items[0];
        TextRun? run = item.Runs.Count > 0 ? item.Runs[0] : null;

        _suppress = true;
        if (run is not null)
        {
            _font.SelectedItem = run.FontFamily;
            _size.Text = run.FontSize.ToString("0.##", CultureInfo.InvariantCulture);
            _bold.IsChecked = run.Bold;
            _italic.IsChecked = run.Italic;
        }

        _align.SelectedIndex = item.Alignment switch
        {
            TextAlignment.Center => 1,
            TextAlignment.Right => 2,
            _ => 0,
        };
        _lineSpacing.Text = item.LineSpacing.ToString("0.##", CultureInfo.InvariantCulture);
        _paragraphSpacing.Text = item.ParagraphSpacing.ToString("0.##", CultureInfo.InvariantCulture);
        _rotation.Text = (item.RotationRadians * 180.0 / Math.PI).ToString("0.#", CultureInfo.InvariantCulture);
        _color.Text = $"{item.Color.R},{item.Color.G},{item.Color.B}";
        _suppress = false;
    }

    private void ApplyFace()
    {
        if (_suppress)
        {
            return;
        }

        List<TextItem> items = _view.ViewModel.SelectedTextItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        TextItem item = items[0];
        TextRun? run = item.Runs.Count > 0 ? item.Runs[0] : null;
        if (run is null)
        {
            return;
        }

        double size = double.TryParse(_size.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            ? s
            : run.FontSize;
        string family = _font.SelectedItem as string ?? run.FontFamily;

        // Styling is the same path a driver takes: it reuses the object's own content.
        _view.ViewModel.UpdateSelectedText(
            item.PlainText, family, size, _bold.IsChecked == true, _italic.IsChecked == true, item.Color);
    }

    private void ApplyParagraph()
    {
        if (_suppress)
        {
            return;
        }

        double? leading = Parse(_lineSpacing.Text);
        double? space = Parse(_paragraphSpacing.Text);
        double? rotation = Parse(_rotation.Text);

        TextAlignment? alignment = _align.SelectedIndex switch
        {
            1 => TextAlignment.Center,
            2 => TextAlignment.Right,
            0 => TextAlignment.Left,
            _ => null,
        };

        _view.ViewModel.ApplyTextStyle(leading, space, rotation, null, alignment);
    }

    private void ApplyColor()
    {
        if (_suppress)
        {
            return;
        }

        string[] parts = _color.Text?.Split(',') ?? Array.Empty<string>();
        if (parts.Length < 3 ||
            !byte.TryParse(parts[0].Trim(), out byte r) ||
            !byte.TryParse(parts[1].Trim(), out byte g) ||
            !byte.TryParse(parts[2].Trim(), out byte b))
        {
            return;
        }

        List<TextItem> items = _view.ViewModel.SelectedTextItems().ToList();
        if (items.Count == 0)
        {
            return;
        }

        TextItem item = items[0];
        TextRun? run = item.Runs.Count > 0 ? item.Runs[0] : null;
        if (run is null)
        {
            return;
        }

        _view.ViewModel.UpdateSelectedText(
            item.PlainText, run.FontFamily, run.FontSize, run.Bold, run.Italic,
            new ColorRgb(r, g, b));
    }

    private void OnSetWidth(object? sender, RoutedEventArgs e)
    {
        double? width = Parse(_view.FindControl<TextBox>("TtFrameWidth")?.Text ?? string.Empty);
        if (width is { } value)
        {
            _view.ViewModel.ApplyTextStyle(frameWidth: value);
            return;
        }

        // No explicit width typed: take the frame from the block's current width.
        List<TextItem> items = _view.ViewModel.SelectedTextItems().ToList();
        if (items.Count > 0 && items[0].FrameWidth <= 0)
        {
            _view.ViewModel.ApplyTextStyle(frameWidth: Math.Max(80, items[0].BoundingBox().Width));
        }
    }

    private static double? Parse(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
}
