using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The strokes on the selected path, with add, remove and reorder - the panel half of the appearance stack.
///
/// **Every button calls the same session method a driver's operation calls.** That is not politeness: a capability
/// that exists only inside a click handler is a defect in this repository, and the way to avoid one is for the
/// panel to be a view of the model rather than a second place the state lives.
///
/// **The list cannot show an order the document does not have.** It is rebuilt from the model after every edit,
/// including a reorder, so a drag that changed only the rows and not the document is not a thing that can happen
/// here - which is the failure a panel like this is most likely to have.
/// </summary>
public partial class AppearancePane : UserControl
{
    private EditorViewModel? _viewModel;
    private DocumentSession? _session;
    private bool _updating;

    public AppearancePane()
    {
        InitializeComponent();

        AddStrokeButton.Click += OnAdd;
        RemoveStrokeButton.Click += OnRemove;
        MoveUpButton.Click += OnMoveUp;
        MoveDownButton.Click += OnMoveDown;
        StrokeList.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>One row: what a stroke looks like, in a list that is a view of the model.</summary>
    private sealed record Row(IBrush Swatch, string Label, string Badge, int Index, bool IsVisible)
    {
        /// <summary>What the row's button says: the action, not the state, so it is unambiguous.</summary>
        public string Toggle => IsVisible ? "Hide" : "Show";
    }

    /// <summary>Binds the panel to the editor and shows the current selection's strokes.</summary>
    public void Attach(EditorViewModel viewModel)
    {
        _viewModel = viewModel;
        Refresh();
    }

    /// <summary>The row showing, counted from the bottom, or -1 when the panel is empty.</summary>
    internal int SelectedRow => StrokeList.SelectedIndex;

    /// <summary>How many rows the list shows.</summary>
    internal int RowCount => StrokeList.ItemCount;

    /// <summary>
    /// Rebuilds the list from the model.
    ///
    /// Rebuilt rather than patched: the list has to agree with the document after an edit, and re-reading is the
    /// only way to be sure it does. The selection is restored by index, so a row keeps its place across a refresh.
    /// </summary>
    internal void Refresh()
    {
        _updating = true;
        try
        {
            _session = _viewModel?.ActiveSession;
            PathItem? path = _session?.SelectedPaths().FirstOrDefault();

            int keep = StrokeList.SelectedIndex;
            var rows = new List<Row>();

            if (path is not null)
            {
                foreach (StrokeSpec stroke in path.Strokes)
                {
                    ColorRgb colour = stroke.Color;
                    var brush = new SolidColorBrush(
                        Color.FromArgb(
                            (byte)Math.Round(Math.Clamp(stroke.IsVisible ? colour.A : 0.25, 0, 1) * 255),
                            (byte)Math.Round(Math.Clamp(colour.R, 0, 1) * 255),
                            (byte)Math.Round(Math.Clamp(colour.G, 0, 1) * 255),
                            (byte)Math.Round(Math.Clamp(colour.B, 0, 1) * 255)));

                    var badges = new List<string>();
                    if (stroke.HasWidthProfile)
                    {
                        badges.Add("profile");
                    }

                    if (stroke.HasEffects)
                    {
                        badges.Add("effects");
                    }

                    if (!stroke.IsVisible)
                    {
                        badges.Add("hidden");
                    }

                    rows.Add(new Row(
                        brush,
                        $"{stroke.Width:0.##}pt  #{colour.R * 255:0}{colour.G * 255:0}{colour.B * 255:0}",
                        string.Join("  ", badges),
                        rows.Count,
                        stroke.IsVisible));
                }
            }

            StrokeList.ItemsSource = rows;
            StrokeList.SelectedIndex = rows.Count == 0 ? -1 : Math.Clamp(keep, 0, rows.Count - 1);
            RemoveStrokeButton.IsEnabled = rows.Count > 0;
            MoveUpButton.IsEnabled = rows.Count > 1;
            MoveDownButton.IsEnabled = rows.Count > 1;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// Shows or hides the stroke the row is for, through the session - the same call a driver's
    /// style.setStrokeVisible makes - and then re-reads the list, because the toggle changes what the row says.
    /// </summary>
    private void OnToggleVisible(object? sender, RoutedEventArgs e)
    {
        if (_session is null || sender is not Control { DataContext: Row row } || row.Index < 0)
        {
            return;
        }

        if (_session.SetStrokeVisible(row.Index, !row.IsVisible) == 0)
        {
            return;
        }

        Refresh();
        StrokeList.SelectedIndex = Math.Clamp(row.Index, 0, Math.Max(0, StrokeList.ItemCount - 1));
        Changed();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _ = _updating;
    }

    /// <summary>
    /// Adds a stroke and selects it, so the next thing typed lands on the stroke just made rather than on the one
    /// that happened to be selected before.
    /// </summary>
    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        int added = _session.AddStroke();
        if (added == 0)
        {
            return;
        }

        Refresh();
        StrokeList.SelectedIndex = StrokeList.ItemCount - 1;
        Changed();
    }

    /// <summary>
    /// Removes the showing stroke and selects its **neighbour** rather than clearing the selection: somebody
    /// removing a stroke usually wants to keep working on what is left, and an empty selection means the next
    /// click goes nowhere.
    /// </summary>
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        int index = StrokeList.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        if (_session.RemoveStroke(index) == 0)
        {
            return;
        }

        Refresh();
        StrokeList.SelectedIndex = StrokeList.ItemCount == 0 ? -1 : Math.Min(index, StrokeList.ItemCount - 1);
        Changed();
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Move(+1);

    private void OnMoveDown(object? sender, RoutedEventArgs e) => Move(-1);

    /// <summary>
    /// Reorders through the session - the same call a driver's `style.reorderStroke` makes - and then re-reads the
    /// list, so what is shown is the document's order rather than the list's own idea of it.
    /// </summary>
    private void Move(int direction)
    {
        if (_session is null)
        {
            return;
        }

        int from = StrokeList.SelectedIndex;
        int to = from + direction;
        if (from < 0 || to < 0 || to >= StrokeList.ItemCount)
        {
            return;
        }

        if (_session.MoveStroke(from, to) == 0)
        {
            return;
        }

        Refresh();
        StrokeList.SelectedIndex = to;
        Changed();
    }

    private void Changed() => _viewModel?.NotifyDocumentChanged();
}
