using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Color;
using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>
/// Filter tab: the document's filters, the graph each one holds, and one editor per parameter the declaration gives
/// a primitive.
///
/// **The panel is drawn from the declaration.** <see cref="FilterPrimitiveRegistry"/> says which primitives exist,
/// what each takes, of what kind, and with what bounds; the editors here are built from that and never from a list
/// written in this file, so a primitive that declares a new parameter grows an editor by existing. That is the same
/// declaration <c>filter.kinds</c> reports and the other filter operations validate against, which is what keeps a
/// person and a driver reaching the same capabilities.
///
/// **Nothing here writes the model.** Every action is one of the filter operations, invoked through
/// <see cref="EditorOperations"/> exactly as the HTTP endpoint and the assistant invoke it - so a click and a driver
/// call are the same code doing the same thing, the edit is journaled, and a refusal comes back with its reason
/// rather than being swallowed.
/// </summary>
public partial class FilterPane : UserControl
{
    private EditorViewModel? _vm;

    /// <summary>The filter names last shown, so a button can ask which filter its row meant without guessing.</summary>
    private List<string> _filterNames = new();

    /// <summary>The region fields as last shown, so leaving one without changing anything is not an edit.</summary>
    private string _regionShown = string.Empty;

    /// <summary>
    /// True while the panel is running an operation or rebuilding itself.
    ///
    /// An operation raises <c>DocumentChanged</c> from inside the call, and rebuilding a list under a control that
    /// is handling an event of its own re-enters these handlers - so the guard is what stops one edit being applied
    /// twice, and what stops a box that lost focus to a rebuild from committing the value it already committed.
    /// </summary>
    private bool _syncing;

    public FilterPane()
    {
        InitializeComponent();

        FilterList.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
            {
                RefreshPrimitives();
                RefreshSelectedPrimitive();
            }
        };

        PrimitiveList.SelectionChanged += (_, _) => { if (!_syncing) RefreshSelectedPrimitive(); };

        // The kinds come from the registry - the declaration `filter.kinds` reports - so a new primitive appears in
        // this list by existing rather than by being added to a switch here.
        foreach (FilterPrimitiveDefinition definition in FilterPrimitiveRegistry.All)
        {
            AddKindBox.Items.Add(definition.Kind);
        }

        AddKindBox.SelectedIndex = 0;

        AddPrimitiveButton.Click += OnAddPrimitive;
        RemovePrimitiveButton.Click += OnRemovePrimitive;
        CreateFilterButton.Click += OnCreateFilter;
        DeleteFilterButton.Click += OnDeleteFilter;
        ApplyFilterButton.Click += OnApplyFilter;
        ClearFilterButton.Click += OnClearFilter;

        foreach (TextBox box in new[]
                 {
                     NewFilterNameBox, AddIndexBox,
                     RegionXBox, RegionYBox, RegionWidthBox, RegionHeightBox, OutputBox,
                 })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Commit(box);
                    e.Handled = true;
                }
            };
            box.LostFocus += (_, _) => Commit(box);
        }

        // Click rather than a checked/unchecked pair, so assigning IsChecked while refreshing the pane does not look
        // like a person flipping it.
        RegionUnitsBox.Click += (_, _) => CommitRegion();
    }

    /// <summary>
    /// Commits the field that was left, whichever of the pane's text fields it was.
    ///
    /// The two fields that feed a button rather than an operation - the name a filter is created with and the index
    /// a primitive is inserted at - are deliberately not committed on their own: neither is an edit until its
    /// button is pressed, and creating a filter because someone typed its name and then clicked elsewhere would be
    /// a surprising thing for a text box to do.
    /// </summary>
    private void Commit(TextBox box)
    {
        if (ReferenceEquals(box, NewFilterNameBox) || ReferenceEquals(box, AddIndexBox))
        {
            return;
        }

        if (ReferenceEquals(box, RegionXBox) || ReferenceEquals(box, RegionYBox) ||
            ReferenceEquals(box, RegionWidthBox) || ReferenceEquals(box, RegionHeightBox) ||
            ReferenceEquals(box, OutputBox))
        {
            CommitRegion();
            return;
        }

        CommitParameter(box);
    }

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => { if (!_syncing) Refresh(); };
        Refresh();
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// The one way this pane changes anything: an operation from the registry, through the same entry point the HTTP
    /// endpoint and the assistant use.
    ///
    /// A refusal is shown rather than swallowed - <see cref="EditorOperationException"/> is the operation saying why
    /// in words a person can act on, and a filter edit that disappeared silently would change the picture without
    /// anything pointing at the edit that changed it.
    /// </summary>
    private bool Run(string operation, object parameters)
    {
        if (_vm is null)
        {
            return false;
        }

        bool ok;
        _syncing = true;
        try
        {
            EditorOperations.Invoke(
                new AutomationContext { ViewModel = _vm },
                operation,
                JsonSerializer.SerializeToElement(parameters));
            ok = true;
        }
        catch (EditorOperationException ex)
        {
            ShowMessage(ex.Message);
            ok = false;
        }
        finally
        {
            _syncing = false;
        }

        if (ok)
        {
            ClearMessage();

            // The operation raised DocumentChanged while the guard was up, so the pane refreshes itself here
            // instead - once, from the model the operation left behind.
            Refresh();
        }

        return ok;
    }

    private void OnCreateFilter(object? sender, RoutedEventArgs e)
    {
        string name = NewFilterNameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ShowMessage("a filter needs a name");
            return;
        }

        if (SelectedDefinition() is not { } definition)
        {
            return;
        }

        Run("filter.create", new { name, primitives = new[] { PrimitiveJson(definition) } });
    }

    private void OnDeleteFilter(object? sender, RoutedEventArgs e)
    {
        if (SelectedFilter() is not { } filter)
        {
            return;
        }

        Run("filter.delete", new { name = filter.Name });
    }

    private void OnApplyFilter(object? sender, RoutedEventArgs e)
    {
        if (SelectedFilter() is not { } filter)
        {
            return;
        }

        Run("filter.apply", new { name = filter.Name });
    }

    /// <summary>
    /// Takes whatever filter the selection is drawn through off again.
    ///
    /// An empty name is how <c>filter.apply</c> says "no filter", and it is a separate button rather than a second
    /// meaning for Apply because the two are different requests: one names a filter, the other names none.
    /// </summary>
    private void OnClearFilter(object? sender, RoutedEventArgs e) => Run("filter.apply", new { name = string.Empty });

    /// <summary>
    /// Adds the chosen primitive, with the values the declaration requires.
    ///
    /// The required parameters are seeded from the declaration's own default, and a required one with no default
    /// gets the smallest value its kind allows - because the operation refuses a step missing a value its kind
    /// cannot be evaluated without, and inventing a value here would be this pane deciding what a blur means.
    /// </summary>
    private void OnAddPrimitive(object? sender, RoutedEventArgs e)
    {
        if (SelectedFilter() is not { } filter || SelectedDefinition() is not { } definition)
        {
            return;
        }

        Dictionary<string, object?> parameters = PrimitiveJson(definition);
        parameters["name"] = filter.Name;

        // An empty insertion point appends, which is what the operation does when `index` is not given at all.
        if (int.TryParse(AddIndexBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int at))
        {
            parameters["index"] = at;
        }

        Run("filter.addPrimitive", parameters);
    }

    private void OnRemovePrimitive(object? sender, RoutedEventArgs e)
    {
        if (SelectedFilter() is not { } filter || PrimitiveList.SelectedIndex < 0)
        {
            return;
        }

        Run("filter.removePrimitive", new { name = filter.Name, index = PrimitiveList.SelectedIndex });
    }

    // ---------------------------------------------------------------- editors, from the declaration

    /// <summary>
    /// Builds one editor per parameter the selected primitive's kind declares, of the shape that kind calls for: a
    /// box for a number or a colour, a list of the declared words for a choice, and a name for a buffer - because a
    /// buffer is the wiring and is edited by name rather than as a value.
    ///
    /// The value shown comes from the **model**, not from the declaration's default, so the control describes the
    /// step that is actually there. That is why <see cref="ValueOf"/> exists: the declaration gives the name a
    /// parameter is set by, and the record member holding it is occasionally a different word (`numOctaves` is
    /// `Octaves`, `values` is `Matrix`), so the read side needs the same small map the operation's own write side
    /// keeps. A parameter added to the declaration appears here without this method changing.
    /// </summary>
    private void BuildParameterEditors()
    {
        PrimitiveParameters.Children.Clear();

        if (SelectedPrimitive() is not { } primitive ||
            FilterPrimitiveRegistry.Find(primitive.Kind.ToString()) is not { } definition)
        {
            return;
        }

        foreach (FilterParameter parameter in definition.Parameters)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

            var label = new TextBlock
            {
                Text = parameter.Name,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
            };

            Control editor;
            if (parameter.Kind == FilterParameterKind.Choice && parameter.Choices is { Length: > 0 } choices)
            {
                var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                foreach (string choice in choices)
                {
                    combo.Items.Add(choice);
                }

                combo.SelectedItem = ValueOf(primitive, parameter.Name) as string;
                combo.SelectionChanged += (_, _) => CommitChoice(combo);
                editor = combo;
            }
            else
            {
                var box = new TextBox { Text = DisplayValue(primitive, parameter.Name) };
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        CommitParameter(box);
                        e.Handled = true;
                    }
                };
                box.LostFocus += (_, _) => CommitParameter(box);
                editor = box;
            }

            // The name travels on the control rather than in a closure per parameter, which is what keeps this loop
            // something that can be read - and what lets a test ask which parameter an editor is for.
            editor.Tag = parameter.Name;
            ToolTip.SetTip(editor, parameter.Meaning);
            ToolTip.SetTip(label, parameter.Meaning);

            Grid.SetColumn(label, 0);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(label);
            grid.Children.Add(editor);
            PrimitiveParameters.Children.Add(grid);
        }
    }

    private void CommitChoice(ComboBox combo)
    {
        if (_syncing || combo.Tag is not string name || combo.SelectedItem is not string value)
        {
            return;
        }

        RunParameter(name, value);
    }

    /// <summary>
    /// Writes one edited parameter, through the operation that owns that parameter.
    ///
    /// A buffer is **wiring**, not a value, so it goes to <c>filter.connectPrimitive</c>; everything else goes to
    /// <c>filter.setPrimitiveParameter</c>. The panel does not decide which is which: the declaration's kind does.
    /// </summary>
    private void CommitParameter(TextBox box)
    {
        if (_syncing || box.Tag is not string name || SelectedFilter() is not { } filter ||
            SelectedPrimitive() is not { } primitive ||
            FilterPrimitiveRegistry.Find(primitive.Kind.ToString()) is not { } definition ||
            definition.Parameter(name) is not { } parameter)
        {
            return;
        }

        string text = box.Text?.Trim() ?? string.Empty;
        if (parameter.Kind == FilterParameterKind.Buffer)
        {
            Run("filter.connectPrimitive", new Dictionary<string, object?>
            {
                ["name"] = filter.Name,
                ["index"] = PrimitiveList.SelectedIndex,
                [name] = text,
            });
            return;
        }

        if (!TryValue(parameter, text, out object? value))
        {
            ShowMessage(parameter.Kind == FilterParameterKind.Color
                ? $"{parameter.Name} takes a colour as [r,g,b] with components 0-255"
                : $"{parameter.Name} takes a number");
            return;
        }

        RunParameter(name, value!);
    }

    private void RunParameter(string parameter, object value)
    {
        if (SelectedFilter() is not { } filter)
        {
            return;
        }

        Run("filter.setPrimitiveParameter", new Dictionary<string, object?>
        {
            ["name"] = filter.Name,
            ["index"] = PrimitiveList.SelectedIndex,
            ["parameter"] = parameter,
            ["value"] = value,
        });
    }

    /// <summary>
    /// A typed value in the shape its kind takes: a number, a comma-separated list of numbers (the shape a colour
    /// matrix's twenty values come in), or a colour read as bytes or as hex.
    ///
    /// The panel does not decide whether the value is *allowed* - the range of a number and the words of a choice
    /// belong to the declaration, and the operation refuses what they do not permit with a reason naming the bound.
    /// All this decides is what was typed.
    /// </summary>
    private static bool TryValue(FilterParameter parameter, string text, out object? value)
    {
        value = null;

        switch (parameter.Kind)
        {
            case FilterParameterKind.Choice:
            case FilterParameterKind.Buffer:
                value = text;
                return true;

            case FilterParameterKind.Color:
                if (!TryColour(text, out int[] colour))
                {
                    return false;
                }

                value = colour;
                return true;

            default:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    value = number;
                    return true;
                }

                double[]? list = TryNumberList(text);
                if (list is null)
                {
                    return false;
                }

                value = list;
                return true;
        }
    }

    private static double[]? TryNumberList(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var numbers = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    /// <summary>A colour as the operations take one: bytes, or a hex string. The model's own channels are 0-1.</summary>
    private static bool TryColour(string text, out int[] colour)
    {
        colour = Array.Empty<int>();

        if (text.Length == 0)
        {
            return false;
        }

        if (!text.Contains(','))
        {
            try
            {
                colour = Bytes(HexColor.Parse(text));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        var values = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }

            values[i] = Math.Clamp(values[i], 0, 255);
        }

        colour = values;
        return true;
    }

    private static int[] Bytes(ColorRgb colour) => new[]
    {
        (int)Math.Round(Math.Clamp(colour.R, 0.0, 1.0) * 255),
        (int)Math.Round(Math.Clamp(colour.G, 0.0, 1.0) * 255),
        (int)Math.Round(Math.Clamp(colour.B, 0.0, 1.0) * 255),
    };

    /// <summary>
    /// The value a primitive currently holds for a parameter the declaration names.
    ///
    /// The declaration names a parameter by the word the operations set it with, and the record member holding it is
    /// occasionally a different word - `numOctaves` is `Octaves`, `values` is `Matrix` - so this is the read half of
    /// the map the operation's own write side keeps. Both halves exist because the model is a record with a member
    /// per value rather than a dictionary keyed by parameter name.
    /// </summary>
    private static object? ValueOf(FilterPrimitive primitive, string parameter) => parameter switch
    {
        "in" => primitive.Input ?? string.Empty,
        "in2" => primitive.Input2 ?? string.Empty,
        "result" => primitive.Result,
        "radius" => primitive.Radius,
        "dx" => primitive.Dx,
        "dy" => primitive.Dy,
        "floodColor" => primitive.FloodColor is { } flood ? Bytes(flood) : null,
        "floodOpacity" => primitive.FloodOpacity,
        "operator" => primitive.Operator,
        "mode" => primitive.Mode,
        "scale" => primitive.Scale,
        "xChannel" => primitive.XChannel,
        "yChannel" => primitive.YChannel,
        "type" => primitive.Type,
        "baseFrequency" => primitive.BaseFrequency,
        "numOctaves" => primitive.Octaves,
        "seed" => primitive.Seed,
        "values" => primitive.Matrix,
        "surfaceScale" => primitive.SurfaceScale,
        "specularConstant" => primitive.SpecularConstant,
        "specularExponent" => primitive.SpecularExponent,
        "diffuseConstant" => primitive.DiffuseConstant,
        "lightingColor" => primitive.LightingColor is { } light ? Bytes(light) : null,
        "azimuth" => primitive.Azimuth,
        "elevation" => primitive.Elevation,
        _ => null,
    };

    private static string DisplayValue(FilterPrimitive primitive, string parameter)
        => ValueOf(primitive, parameter) switch
        {
            null => string.Empty,
            string text => text,
            double number => number.ToString("0.####", CultureInfo.InvariantCulture),
            int whole => whole.ToString(CultureInfo.InvariantCulture),
            double[] list => string.Join(",", list.Select(n => n.ToString("0.####", CultureInfo.InvariantCulture))),
            int[] bytes => string.Join(",", bytes),
            object other => other.ToString() ?? string.Empty,
        };

    // ---------------------------------------------------------------- the region

    /// <summary>
    /// Writes the filter's own settings: where it is evaluated and what it answers with.
    ///
    /// Nothing is sent when nothing was changed - the fields are committed on losing focus, and a filter that was
    /// only looked at should not be rewritten and journaled as an edit.
    /// </summary>
    private void CommitRegion()
    {
        if (_vm is null || _syncing || SelectedFilter() is not { } filter)
        {
            return;
        }

        if (RegionSignature() == _regionShown)
        {
            return;
        }

        var parameters = new Dictionary<string, object?> { ["name"] = filter.Name };
        foreach ((string member, TextBox box) in new (string, TextBox)[]
                 {
                     ("x", RegionXBox), ("y", RegionYBox), ("width", RegionWidthBox), ("height", RegionHeightBox),
                 })
        {
            string text = box.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                ShowMessage($"the region's {member} takes a number");
                return;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                ShowMessage($"the region's {member} takes a number");
                return;
            }

            parameters[member] = number;
        }

        parameters["userSpace"] = RegionUnitsBox.IsChecked == true;
        parameters["output"] = OutputBox.Text?.Trim() ?? string.Empty;

        Run("filter.setRegion", parameters);
    }

    /// <summary>The region fields as one string, which is what says whether a person changed any of them.</summary>
    private string RegionSignature()
        => string.Join(
            "|",
            RegionXBox.Text, RegionYBox.Text, RegionWidthBox.Text, RegionHeightBox.Text,
            OutputBox.Text, RegionUnitsBox.IsChecked == true ? "user" : "object");

    // ---------------------------------------------------------------- reading the document

    private FilterSpec? SelectedFilter()
        => _vm is not null && FilterList.SelectedIndex >= 0 && FilterList.SelectedIndex < _filterNames.Count
            ? _vm.Document.FindFilter(_filterNames[FilterList.SelectedIndex])
            : null;

    private FilterPrimitive? SelectedPrimitive()
    {
        FilterSpec? filter = SelectedFilter();
        int index = PrimitiveList.SelectedIndex;
        return filter is not null && index >= 0 && index < filter.Primitives.Count
            ? filter.Primitives[index]
            : null;
    }

    /// <summary>The definition of the kind the Add box names - which is also the kind a New filter starts with.</summary>
    private FilterPrimitiveDefinition? SelectedDefinition()
        => AddKindBox.SelectedItem is string kind ? FilterPrimitiveRegistry.Find(kind) : null;

    /// <summary>One primitive as the operations take one: its kind, and a value for each required parameter.</summary>
    private static Dictionary<string, object?> PrimitiveJson(FilterPrimitiveDefinition definition)
    {
        var json = new Dictionary<string, object?>(StringComparer.Ordinal) { ["kind"] = definition.Kind };

        foreach (FilterParameter parameter in definition.Required)
        {
            // A required buffer is never seeded: an absent input means the previous step's result, which is SVG's
            // own rule, and naming one here would be this pane inventing wiring.
            if (parameter.Kind == FilterParameterKind.Buffer)
            {
                continue;
            }

            json[parameter.Name] = parameter.Kind switch
            {
                FilterParameterKind.Choice => parameter.Default ?? parameter.Choices?.FirstOrDefault() ?? string.Empty,
                FilterParameterKind.Color => new[] { 0, 0, 0 },
                _ => SeedNumber(parameter),
            };
        }

        return json;
    }

    private static double SeedNumber(FilterParameter parameter)
    {
        if (parameter.Default is { Length: > 0 } text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double declared))
        {
            return declared;
        }

        return double.IsNegativeInfinity(parameter.Minimum) ? 0.0 : parameter.Minimum;
    }

    /// <summary>
    /// Re-reads the whole pane from the document.
    ///
    /// Everything shown comes from the model - the library, the graph, the values - so a filter edited by a driver
    /// while this pane is open shows what the document holds rather than what the pane last saw.
    /// </summary>
    public void Refresh()
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            RefreshFilters();
            RefreshPrimitives();
            BuildParameterEditors();
            RefreshRegion(SelectedFilter());
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshFilters()
    {
        string? keep = SelectedFilter()?.Name;
        _filterNames = _vm!.Document.Filters.Select(filter => filter.Name).ToList();

        FilterList.ItemsSource = _filterNames;

        int index = keep is null ? 0 : _filterNames.IndexOf(keep);
        FilterList.SelectedIndex = _filterNames.Count == 0 ? -1 : Math.Max(index, 0);
    }

    /// <summary>
    /// The selected filter's steps, in graph order, with what each reads and what it calls its answer.
    ///
    /// The order is shown because it decides what an unwired step reads; the wiring is shown beside it because the
    /// order alone does not say what a wired one reads, and the two together are the graph.
    /// </summary>
    private void RefreshPrimitives()
    {
        FilterSpec? filter = SelectedFilter();
        var rows = new List<PrimitiveRow>();

        if (filter is not null)
        {
            for (int i = 0; i < filter.Primitives.Count; i++)
            {
                FilterPrimitive primitive = filter.Primitives[i];
                string wiring = string.Join(
                    " ",
                    new[] { primitive.Input, primitive.Input2 }.Where(name => !string.IsNullOrEmpty(name)));

                rows.Add(new PrimitiveRow(
                    i,
                    FilterPrimitiveRegistry.Find(primitive.Kind.ToString())?.Kind ?? primitive.Kind.ToString(),
                    primitive.Result,
                    wiring));
            }
        }

        int keep = PrimitiveList.SelectedIndex;
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            PrimitiveList.ItemsSource = rows;
            PrimitiveList.SelectedIndex = rows.Count == 0 ? -1 : Math.Clamp(keep, 0, rows.Count - 1);
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    /// <summary>Rebuilds the editors and the region fields for the step and filter now selected.</summary>
    private void RefreshSelectedPrimitive()
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            BuildParameterEditors();
            RefreshRegion(SelectedFilter());
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void RefreshRegion(FilterSpec? filter)
    {
        RegionXBox.Text = Format(filter?.X);
        RegionYBox.Text = Format(filter?.Y);
        RegionWidthBox.Text = Format(filter?.Width);
        RegionHeightBox.Text = Format(filter?.Height);
        OutputBox.Text = filter?.Output ?? string.Empty;
        RegionUnitsBox.IsChecked = filter is not null && !filter.ObjectBoundingBox;
        _regionShown = RegionSignature();
    }

    private static string Format(double? number)
        => number?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

    private void ShowMessage(string text)
    {
        FilterMessage.Text = text;
        FilterMessage.IsVisible = true;
    }

    private void ClearMessage()
    {
        FilterMessage.Text = string.Empty;
        FilterMessage.IsVisible = false;
    }

    /// <summary>One row of the primitive list: where the step sits, what it is, what it reads and what it calls it.</summary>
    private sealed record PrimitiveRow(int Index, string Kind, string ResultName, string Wiring);
}
