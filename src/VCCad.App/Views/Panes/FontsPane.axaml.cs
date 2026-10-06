using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The font chooser: the machine's families as a specimen sheet, their faces, the categories,
/// a sample of the one under the pointer, and a heart to keep it.
///
/// It lives in a pane rather than a dropdown because a popup is outside the window's visual
/// tree - nothing can read it, click it or photograph it, and a list nobody can read is a list
/// nobody can check. The toolbar's combo box is still there for choosing a font in one step.
/// </summary>
public partial class FontsPane : UserControl
{
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private FontCategory _category = FontCategory.All;
    private EditorViewModel? _vm;
    private bool _syncing;

    public FontsPane()
    {
        InitializeComponent();
        Rebuild();
    }

    /// <summary>Binds the pane to a document, so "Used" can answer.</summary>
    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        string? search = Search.Text;
        IReadOnlyList<FontRow> rows = FontChooser.Rows(
            _vm?.Document, _category, search, _expanded);

        _syncing = true;
        try
        {
            Rows.ItemsSource = rows;
        }
        finally
        {
            _syncing = false;
        }

        if (rows.Count > 0)
        {
            ShowPreview(rows[0]);
        }
    }

    /// <summary>Sets the sample line in the given family.</summary>
    private void ShowPreview(FontRow row)
    {
        Preview.Text = FontChooser.PreviewText;
        Preview.FontFamily = row.Face ?? FontFamily.Default;
        Preview.FontWeight = row.Weight;
        Preview.FontStyle = row.Slant;
    }

    private void OnCategory(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            _category = FontChooser.Parse(tag);
            Rebuild();
        }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => Rebuild();

    /// <summary>Opens or closes a family's faces.</summary>
    private void OnChevronPressed(object? sender, PointerPressedEventArgs e)
    {
        if (RowOf(sender) is not { } row || !row.Expandable)
        {
            return;
        }

        if (!_expanded.Remove(row.Family))
        {
            _expanded.Add(row.Family);
        }

        e.Handled = true;
        Rebuild();
    }

    /// <summary>
    /// **Shows where a face can be found** (issue #262).
    ///
    /// The person's half of `fonts.findMissing`: the same list from the same helper, printed where the automation surface can
    /// read it - the preview strip under the list. Nothing is downloaded and nothing is installed from here; each site is
    /// listed with the licence note it is known for, because DaFont's "free" and Google's "free" are not the same offer.
    /// </summary>
    private void OnSearchPressed(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        e.Handled = true;

        string places = string.Join("   ", FontSites.Downloads.Select(s => s.Name + " (" + s.Licence + ")"));
        Preview.Text = row.Family + " - where to get it:  " + places + "   " + FontSites.InstallHint;
    }

    /// <summary>Stars or unstars the row's family.</summary>
    private void OnHeartPressed(object? sender, PointerPressedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        FontFavourites.Shared.Toggle(row.Family);
        e.Handled = true;

        // A family that was just unstarred leaves the Favourites list, so the list is redrawn
        // rather than left showing a row that no longer belongs to it.
        Rebuild();
    }

    /// <summary>
    /// Applying is what the list is for, and it goes through the same per-run path the
    /// toolbar uses - only the runs that were selected change.
    /// </summary>
    private void OnRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Rows.SelectedItem is not FontRow row)
        {
            return;
        }

        ShowPreview(row);

        if (_vm is null || _vm.SelectedTextItems().ToList() is not { Count: > 0 })
        {
            return;
        }

        // A family row applies that family; a face row applies its family and its style.
        bool bold = row.Kind == FontRowKind.Face ? row.Weight == FontWeight.Bold : false;
        bool italic = row.Kind == FontRowKind.Face ? row.Slant == FontStyle.Italic : false;

        foreach (Core.Model.TextItem item in _vm.SelectedTextItems().ToList())
        {
            string family = row.Family;

            // The face members alone: the words and the block colour are members this pane does not offer, and
            // writing them would put one block's text and colour over a selection that disagrees with itself.
            _vm.ApplyTextFieldsAt(
                _vm.InspectedRun, null, family, null, bold, italic, null);
        }

        FontFavourites.Shared.Used(row.Family);
    }

    private static FontRow? RowOf(object? sender)
        => (sender as Control)?.DataContext as FontRow;
}
