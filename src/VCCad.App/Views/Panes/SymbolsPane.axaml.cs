using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The symbol library (issue #135), as the person's half of the definitions operations.
///
/// **Every control here runs the operation a driver runs**, and every test about this pane is about the model it
/// left behind rather than about a control having been set: a button that only moved a list would pass a weaker test
/// and do nothing. The counts on each row are the issue's own requirement - a person editing a definition expects
/// every instance to change, and how many places use it is what makes that visible before they edit rather than
/// after. A refusal is shown, because deleting a definition that is still referenced is refused rather than done,
/// and a silent no-op reads as the control being broken.
/// </summary>
public sealed partial class SymbolsPane : UserControl
{
    private EditorViewModel? _vm;
    private bool _syncing;
    private readonly List<string> _names = new();

    public SymbolsPane()
    {
        InitializeComponent();

        SymbolList.SelectionChanged += (_, _) => OnSelectionChanged();
        CreateButton.Click += (_, _) => CreateFromSelection();
        PlaceButton.Click += (_, _) => Place();
        RenameButton.Click += (_, _) => Rename();
        DeleteButton.Click += (_, _) => Delete();
    }

    /// <summary>The definition the list has selected, or null while it holds none.</summary>
    public string? SelectedDefinition { get; private set; }

    /// <summary>What the last control reported, including a refusal - the pane's own readout for a test to read.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>Names the list is showing, in the order it shows them: a test reads the library through the pane.</summary>
    public IReadOnlyList<string> ListedNames => _names;

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => OnUiThread(Refresh);
        vm.SelectionChanged += (_, _) => OnUiThread(Refresh);
        Refresh();
    }

    // ------------------------------------------------------------------
    // The library, read through `definition.list`
    // ------------------------------------------------------------------

    /// <summary>
    /// Fills the list from the operation, not from the document: the panel and a driver must read the same answer,
    /// and the count is the operation's own report of how many instances name each definition.
    /// </summary>
    public void Refresh()
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            JsonElement listed = JsonSerializer.SerializeToElement(
                EditorOperations.Invoke(new AutomationContext { ViewModel = _vm }, "definition.list", default));

            _names.Clear();
            SymbolList.Items.Clear();

            int total = 0;
            foreach (JsonElement entry in listed.EnumerateArray())
            {
                string name = entry.GetProperty("name").GetString() ?? string.Empty;
                int usedBy = entry.GetProperty("usedBy").GetInt32();
                total += usedBy;

                _names.Add(name);
                SymbolList.Items.Add($"{name}  ({usedBy} {(usedBy == 1 ? "place" : "places")})");
            }

            EmptyHint.IsVisible = _names.Count == 0;
            CountText.Text = _names.Count == 0
                ? string.Empty
                : $"{_names.Count} definition(s), {total} place(s) in this document";

            // The selection is kept by name where it still exists, so a refresh after a rename does not clear it.
            int keep = SelectedDefinition is null ? -1 : _names.IndexOf(SelectedDefinition);
            SymbolList.SelectedIndex = keep;
            if (keep < 0)
            {
                SelectedDefinition = null;
            }

            MessageText.Text = Message;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSelectionChanged()
    {
        if (_syncing)
        {
            return;
        }

        int index = SymbolList.SelectedIndex;
        SelectedDefinition = index >= 0 && index < _names.Count ? _names[index] : null;
        if (SelectedDefinition is { Length: > 0 })
        {
            // Putting the name in the box is what makes Rename a one-field action rather than a second dialog.
            NameBox.Text = SelectedDefinition;
        }
    }

    // ------------------------------------------------------------------
    // The four actions, each one operation
    // ------------------------------------------------------------------

    /// <summary>Selected artwork becomes a definition, and an instance stands where it was.</summary>
    public void CreateFromSelection()
    {
        string? name = NameBox.Text is { Length: > 0 } typed ? typed : null;
        JsonElement result = Invoke("definition.create", new { name });
        Report(result, "created");
        Refresh();
    }

    /// <summary>An instance of the selected definition, placed on the artboard.</summary>
    public void Place()
    {
        if (SelectedDefinition is not { Length: > 0 } name)
        {
            Message = "select a definition to place";
            Refresh();
            return;
        }

        JsonElement result = Invoke("definition.place", new { name, x = 100.0, y = 100.0 });
        Report(result, "placed");
        Refresh();
    }

    /// <summary>The selected definition is renamed, and every instance that names it follows.</summary>
    public void Rename()
    {
        if (SelectedDefinition is not { Length: > 0 } from)
        {
            Message = "select a definition to rename";
            Refresh();
            return;
        }

        string to = NameBox.Text ?? string.Empty;
        JsonElement result = Invoke("definition.rename", new { from, to });
        Report(result, "renamed");
        if (result.TryGetProperty("renamed", out JsonElement renamed) && renamed.GetBoolean())
        {
            SelectedDefinition = to;
        }

        Refresh();
    }

    /// <summary>Deleted while nothing references it; refused, with the places named, while something does.</summary>
    public void Delete()
    {
        if (SelectedDefinition is not { Length: > 0 } name)
        {
            Message = "select a definition to delete";
            Refresh();
            return;
        }

        JsonElement result = Invoke("definition.delete", new { name });
        Report(result, "deleted");
        Refresh();
    }

    /// <summary>
    /// Turns an operation's answer into the pane's message: the count of what happened, or the refusal the file
    /// would otherwise say nothing about.
    /// </summary>
    private void Report(JsonElement result, string verb)
    {
        if (result.TryGetProperty("refusal", out JsonElement refusal) &&
            refusal.ValueKind == JsonValueKind.String)
        {
            Message = refusal.GetString() ?? string.Empty;
            return;
        }

        string what = verb switch
        {
            "created" when result.TryGetProperty("name", out JsonElement made) => $"{made.GetString()} created",
            "placed" => $"{SelectedDefinition} placed",
            "renamed" when result.TryGetProperty("updatedInstances", out JsonElement count) =>
                $"renamed; {count.GetInt32()} instance(s) follow",
            "deleted" => $"{SelectedDefinition} deleted",
            _ => verb,
        };

        Message = what;
    }

    private JsonElement Invoke(string operation, object parameters)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            new AutomationContext { ViewModel = _vm! }, operation, JsonSerializer.SerializeToElement(parameters)));

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
