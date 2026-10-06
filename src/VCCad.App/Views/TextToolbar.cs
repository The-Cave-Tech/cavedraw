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

    /// <summary>The document the picker's own section was built for, so it is rebuilt only when that changes.</summary>
    private VCCad.Core.Model.CadDocument? _fontListDocument;
    private readonly ToggleButton _italic;
    private readonly ComboBox _align;
    private readonly TextBox _lineSpacing;
    private readonly TextBox _paragraphSpacing;
    private readonly TextBox _rotation;
    private readonly TextBox _color;
    private readonly Border _host;

    private bool _suppress;

    /// <summary>The face in force before a hover preview, so it can be put back.</summary>
    private string? _previewed;

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

        // The chooser's own list: labels carry the face count, and each row is drawn in the
        // face it offers. A family nothing can draw says so instead of quietly rendering in
        // the default, which would look exactly like a row that worked.
        RebuildFontList();

        _font.SelectionChanged += (_, _) => ApplyFace();
        _size.LostFocus += (_, _) => ApplyFace();
        _size.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyFace(); } };
        _bold.IsCheckedChanged += (_, _) => ApplyFace();
        _italic.IsCheckedChanged += (_, _) => ApplyFace();
        _align.SelectionChanged += (_, _) => ApplyParagraph();
        // **Enter commits, as well as leaving the field.** The size field above has always done both; the
        // tracking fields only committed when focus left them, so a value typed and confirmed with Enter sat in
        // the box, the text did not change, and the value landed later - when the person moved to another field
        // for an unrelated reason. That is #216: a confirmed edit that appears to have been ignored and then
        // arrives unasked.
        _lineSpacing.LostFocus += (_, _) => ApplyParagraph();
        _lineSpacing.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyParagraph(); } };
        _paragraphSpacing.LostFocus += (_, _) => ApplyParagraph();
        _paragraphSpacing.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyParagraph(); } };
        _rotation.LostFocus += (_, _) => ApplyParagraph();
        _rotation.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyParagraph(); } };
        _color.LostFocus += (_, _) => ApplyColor();
        _color.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyColor(); } };

        Button width = view.FindControl<Button>("TtWidth")!;
        width.Click += OnSetWidth;
        FollowCaret();
    }

    /// <summary>Shows the toolbar while editing and mirrors the current run into it.</summary>
    /// <summary>
    /// **The caret moving is what these controls follow** (issue #260).
    ///
    /// <see cref="Sync"/> already reads the run at `TextCaretOffset` and writes the face, the size, the weight and the slant
    /// from it - but nothing called it when the caret moved, so the fields described the selected block rather than the text
    /// under the caret. In a block holding regular and bold words that meant the B button showed one answer for both, and the
    /// person had no way to see from the controls what the text at the caret actually was.
    ///
    /// The view model publishes every caret move already, so the subscription is all that was missing.
    /// </summary>
    private void FollowCaret()
    {
        _view.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "TextCaretOffset"
                or "TextSelectionStart"
                or "TextSelectionEnd"
                or "EditingText"
                or "IsEditingText")
            {
                // The document may have changed under us - a second file opened, or the first one loaded after the toolbar
                // was built - and the picker's own section belongs to whatever document is open now (issue #261).
                RebuildFontList();
                Sync();
            }
        };
    }

    /// <summary>
    /// **Fills the picker: the document's faces first, in their own section, then the machine's** (issue #261).
    ///
    /// The list used to be the machine's families alone, built once when the window opened - **before any document
    /// existed** - so a face the open file uses appeared nowhere: `All` never consulted the document, and the `Used`
    /// category looked each name up in the machine's families, which answers nothing for a name like
    /// `NPFRLV+CenturyGothic-Bold`. A person looking for "the font this file uses" was reading a list that did not contain it.
    ///
    /// A document face the machine does not have still gets a row: `FontFamilyEntry` requires at least one face, so it cannot
    /// represent one, but `FontChoice` may carry `Face = null` and the row prints with the `· unavailable` convention the
    /// machine rows already use. Each row says what the text is actually drawn with.
    ///
    /// Rebuilt only when the document changes: the machine's two hundred rows are not worth rebuilding on every keystroke.
    /// </summary>
    private void RebuildFontList()
    {
        VCCad.Core.Model.CadDocument? document = _view.ViewModel.Document;
        if (ReferenceEquals(document, _fontListDocument) && _font.ItemsSource is not null)
        {
            return;
        }

        _fontListDocument = document;

        var rows = new List<FontChoice>();
        IReadOnlyList<FontFamilyEntry> machine = FontCatalog.Families();

        foreach ((FontUsageEntry font, string source) in FontUsage.Detail(document))
        {
            FontFamilyEntry? entry = machine.FirstOrDefault(
                f => string.Equals(f.Name, font.BaseFont, StringComparison.OrdinalIgnoreCase));

            FontFamily? face = entry is null
                ? null
                : FontChooser.RowFace(entry) is { } row ? new FontFamily(row.Family) : null;

            rows.Add(new FontChoice(
                font.BaseFont,
                $"{font.BaseFont} \u00b7 in this file \u2014 {source}",
                face,
                Math.Max(1, font.Runs),
                false));
        }

        rows.AddRange(FontChoices.For(FontChooser.Select(document, FontCategory.All)));
        _font.ItemsSource = rows;
    }

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

        // **The controls follow the caret, not the block.** A block can hold several faces and sizes - a
        // heading and a caption in one frame - so the first run's font is wrong for most carets, and it was
        // what the panel showed while typing went into the run at the caret. The canvas adopts the caret's
        // font as well, and both ask `TextEditing.RunAt`, so the two cannot disagree.
        //
        // `RunAt` takes a **character offset**, and this passed `TextCaretRunIndex` - a **run index** - so for
        // any block whose first run was non-empty the offset 1 meant "the second character of run 1" and the
        // toolbar described the first run for every caret past it (issue #157). The offset is now published
        // alongside the run index, so the toolbar asks in the coordinate `RunAt` takes.
        TextRun? run = TextEditing.RunAt(item, _view.ViewModel.TextCaretOffset)
                       ?? (item.Runs.Count > 0 ? item.Runs[0] : null);

        _suppress = true;
        if (run is not null)
        {
            _font.SelectedItem = ((IEnumerable<FontChoice>?)_font.ItemsSource ?? Array.Empty<FontChoice>())
            .FirstOrDefault(c => string.Equals(c.Name, run.FontFamily, StringComparison.OrdinalIgnoreCase));
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

        // The family's real name, not the row's label: the label carries the face count, and
        // applying "Adobe Arabic (4)" would put a count in the document.
        string family = (_font.SelectedItem as FontChoice)?.Name
            ?? _font.SelectedItem as string
            ?? string.Empty;

        // Nothing selected means there is no block to restyle, so the choice becomes the
        // face new text is created with. Silently doing nothing would be the worst of the
        // three options: the person picked a font and expects to see it used.
        if (_view.ViewModel.SelectedTextItems().ToList() is not { Count: > 0 } items)
        {
            if (!string.IsNullOrEmpty(family))
            {
                _view.ViewModel.DefaultFontFamily = family;
            }

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

        // Styling is the same path a driver takes, and it names only the members this control is for: content and
        // colour are members nobody changed here, and writing them would put this block's words and colour over a
        // selection that does not agree with itself.
        _view.ViewModel.ApplyTextFieldsAt(
            _view.ViewModel.InspectedRun, null, family, size, _bold.IsChecked == true, _italic.IsChecked == true,
            null);
    }

    /// <summary>
    /// Applies a face to the selection while the pointer is merely over it in the list.
    ///
    /// Choosing a font by trying it is how anyone actually picks one, and a dropdown that
    /// only shows the name in its own face still leaves you guessing how it will look on
    /// the words you have. This previews, and <see cref="_previewed"/> lets the list put
    /// the original face back if the pointer leaves without a choice being made.
    /// </summary>
    private void PreviewFace(string? family)
    {
        if (_suppress || string.IsNullOrEmpty(family) ||
            _view.ViewModel.SelectedTextItems().ToList() is not { Count: > 0 } items)
        {
            return;
        }

        TextItem item = items[0];
        if (item.Runs.Count == 0)
        {
            return;
        }

        _previewed ??= item.Runs[0].FontFamily;

        _suppress = true;

        // A preview changes the face of the run the pointer is over and nothing else: content and colour belong to
        // the block, and a preview that wrote them would leave a mixed selection holding one block's words.
        _view.ViewModel.ApplyTextFieldsAt(
            _view.ViewModel.InspectedRun, null, family, null, null, null, null);
        _suppress = false;
    }

    /// <summary>Puts back the face that was in force before an abandoned preview.</summary>
    private void EndPreview()
    {
        if (_previewed is not { } original)
        {
            return;
        }

        _previewed = null;
        if (_view.ViewModel.SelectedTextItems().ToList() is not { Count: > 0 } items ||
            items[0].Runs.Count == 0)
        {
            return;
        }

        TextItem item = items[0];
        _suppress = true;
        _view.ViewModel.ApplyTextFieldsAt(
            _view.ViewModel.InspectedRun, null, original, null, null, null, null);
        _suppress = false;
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

        _view.ViewModel.ApplyTextFieldsAt(
            _view.ViewModel.InspectedRun, null, null, null, null, null, new ColorRgb(r, g, b));
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
