using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views.Panes;

/// <summary>
/// The brush editor: one editor whose middle section is the parameter set of the selected engine, with a preview
/// drawn from the same geometry the canvas draws, and the selection's own brush reported beside it.
///
/// **Every control here calls the operation a driver calls.** Creating a brush is <c>brush.create</c>, changing one
/// of its parameters is <c>brush.set</c>, filling a pattern slot is <c>brush.setTile</c>, renaming is
/// <c>brush.rename</c>, deleting is <c>brush.delete</c> and sweeping the selection with one is <c>brush.apply</c>.
/// Nothing in this file writes the model, and nothing in it holds a second copy of what a brush is: the panel is a
/// view of the document's brush library, which is why a brush a driver creates appears here by existing and why
/// there is no draft brush to fall out of step with the document's.
///
/// **The tabs are the kinds this build makes, asked rather than listed.** The list comes from <c>brush.kinds</c>,
/// which reports what <c>brush.create</c> accepts, so an engine that lands in the registry gets a tab without this
/// file being touched - and an engine this build cannot make gets none, because a tab over a kind that cannot exist
/// is chrome describing something that is not there.
///
/// **A brush's kind is its identity, not a field.** <c>brush.create</c> takes the kind and no operation changes it
/// afterwards, because the members of one kind have no meaning in another: a nib's roundness is not a pattern
/// brush's spacing, and re-reading one as the other is how a file's value gets invented. So the tabs choose what a
/// **new** brush will be, and selecting a brush in the library moves the tabs to that brush's kind. Switching to
/// another tab then offers a new brush of that kind rather than pretending the selected one changed kind.
/// </summary>
public partial class BrushesPane : UserControl
{
    /// <summary>What the library says when the pane is on a brush the document has not got yet.</summary>
    public const string NewBrushLabel = "(new brush)";

    /// <summary>What an asset picker says for "this slot names no artwork".</summary>
    public const string NoAssetLabel = "(none)";

    /// <summary>The word every mixed readout uses, so the pane says the same thing in every place.</summary>
    public const string MixedWord = "mixed";

    private const string DefaultBrushName = "Brush";

    /// <summary>Marks the pickers that list the document's artwork, so a document edit reaches every one of them.</summary>
    private const string AssetTag = "asset";

    private EditorViewModel? _vm;
    private bool _syncing;

    private readonly BrushPreviewView _preview = new();
    private readonly List<string> _kinds = new();
    private readonly Dictionary<string, Control> _fields = new(StringComparer.Ordinal);

    /// <summary>The items an asset picker offers, by the label it shows.</summary>
    private readonly List<string> _assetLabels = new();
    private readonly Dictionary<string, Guid> _assetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _labelsById = new();

    /// <summary>The library brush being edited, or null while nothing is selected.</summary>
    private string? _editing;

    private BrushKind _builtKind;
    private bool _built;
    private bool _tabsBuilt;

    public BrushesPane()
    {
        InitializeComponent();

        PreviewHost.Child = _preview;

        BrushLibraryBox.SelectionChanged += (_, _) => OnLibraryChanged();
        KindTabs.SelectionChanged += (_, _) => OnKindChanged();

        NewBrushButton.Click += (_, _) => NewBrush();
        RenameBrushButton.Click += (_, _) => Rename();
        DeleteBrushButton.Click += (_, _) => Delete();
        ApplyToSelectionButton.Click += (_, _) => ApplyToSelection();
    }

    // ------------------------------------------------------------------
    // Attach and refresh
    // ------------------------------------------------------------------

    public void Attach(EditorViewModel vm)
    {
        _vm = vm;
        vm.DocumentChanged += (_, _) => OnUiThread(Refresh);
        vm.SelectionChanged += (_, _) => OnUiThread(Refresh);

        // The inspected stroke is shared state the appearance panel writes, so a change can arrive on any thread -
        // `style.inspectStroke` runs its handler on the caller's thread rather than marshalling. Everything below is
        // UI work, so it is posted when it did not arrive on the UI thread.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.InspectedStroke))
            {
                OnUiThread(Refresh);
            }
        };

        Refresh();
    }

    public void Detach()
    {
        _vm = null;
    }

    /// <summary>
    /// Runs UI work on the UI thread, wherever the change that asked for it happened - the discipline
    /// <see cref="ColorsPane"/> took after #178.
    /// </summary>
    private static void OnUiThread(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            work();
        }
        else
        {
            Dispatcher.UIThread.Post(work);
        }
    }

    /// <summary>
    /// The kinds this build's <c>brush.create</c> makes, asked of the registry rather than listed here.
    ///
    /// The question is the registry's own, so the answer cannot drift from the switch that would refuse a create: a
    /// kind the answer omits is a kind <c>brush.create</c> would throw on, and a tab over it would be a control that
    /// cannot work.
    /// </summary>
    private string[] RegistryKinds()
    {
        if (_vm is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return EditorOperations.Invoke(
                new AutomationContext { ViewModel = _vm }, "brush.kinds", default) as string[]
                ?? Array.Empty<string>();
        }
        catch (EditorOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private void Refresh()
    {
        if (_vm is null)
        {
            return;
        }

        CadDocument document = _vm.Document;
        BrushSpec? library = Library;

        _syncing = true;
        try
        {
            // A refresh is the panel catching up with the document, and a refusal it reported is about the state the
            // document was in a moment ago. Leaving it up would have the panel describing two states at once.
            Status(string.Empty);

            if (!_tabsBuilt)
            {
                BuildTabs();
            }

            RefreshLibrary(document, library);
            RefreshAssets(document);

            BrushKind kind = library?.Kind ?? DraftKind();
            SelectTab(kind);
            EnsureParameters(kind);
            RefreshAssetPickers();

            // A kind is what the tabs choose, so a selection with no brush shows the model's own defaults for the
            // kind the tabs are on: a new brush's defaults are the model's rather than this panel's guesses.
            BrushSpec shown = library ?? Defaults(kind);
            ShowValues(shown);

            BrushNameBox.Text = library?.Name ?? NextBrushName(document);
            RenameBrushButton.IsEnabled = library is not null;
            DeleteBrushButton.IsEnabled = library is not null;
            ApplyToSelectionButton.IsEnabled = library is not null;

            _preview.Show(document, library);
        }
        finally
        {
            _syncing = false;
        }

        RefreshApplicationLabel();
    }

    private BrushSpec? Library
        => _vm is not null && _editing is { } name ? _vm.Document.FindBrush(name) : null;

    /// <summary>The kind the tabs are on, which is what New brush would make.</summary>
    private BrushKind DraftKind()
        => KindTabs.SelectedIndex >= 0 && KindTabs.SelectedIndex < _kinds.Count
            ? ParseKind(_kinds[KindTabs.SelectedIndex])
            : BrushKind.Calligraphic;

    private static BrushKind ParseKind(string name)
        => Enum.TryParse(name, ignoreCase: true, out BrushKind kind) ? kind : BrushKind.Calligraphic;

    private static string NextBrushName(CadDocument document)
    {
        if (!document.Brushes.Any(brush => brush.Name == DefaultBrushName))
        {
            return DefaultBrushName;
        }

        for (int i = 2; ; i++)
        {
            string candidate = DefaultBrushName + " " + i.ToString(CultureInfo.InvariantCulture);
            if (!document.Brushes.Any(brush => brush.Name == candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The library list: the new-brush state first, then every brush **in this document**, in the order the document
    /// holds them. A panel that listed brushes from anywhere else would be implying a global library that does not
    /// exist.
    /// </summary>
    private void RefreshLibrary(CadDocument document, BrushSpec? library)
    {
        var names = new List<string> { NewBrushLabel };
        names.AddRange(document.Brushes.Select(brush => brush.Name));

        BrushLibraryBox.ItemsSource = names;
        BrushLibraryBox.SelectedIndex = library is null ? 0 : names.IndexOf(library.Name);
    }

    /// <summary>
    /// The items an asset picker offers: the document's own artwork, by name.
    ///
    /// Two items can share a name, and a picker whose entries cannot be told apart cannot be used to choose between
    /// them - so a repeated name carries a short id. An unnamed item carries one too, because "(unnamed)" twice is
    /// the same problem.
    /// </summary>
    private void RefreshAssets(CadDocument document)
    {
        _assetLabels.Clear();
        _assetIds.Clear();
        _labelsById.Clear();

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LayerItem item in document.AllItems())
        {
            string name = string.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name.Trim();
            seen.TryGetValue(name, out int count);
            seen[name] = count + 1;
            if (count > 0)
            {
                name = $"{name} ({item.Id.ToString()[..4]})";
            }

            _assetLabels.Add(name);
            _assetIds[name] = item.Id;
            _labelsById[item.Id] = name;
        }
    }

    // ------------------------------------------------------------------
    // The tabs, and the parameters of the selected engine
    // ------------------------------------------------------------------

    private void BuildTabs()
    {
        _kinds.Clear();
        KindTabs.Items.Clear();
        _kinds.AddRange(RegistryKinds());

        foreach (string kind in _kinds)
        {
            KindTabs.Items.Add(new TabItem
            {
                Header = char.ToUpperInvariant(kind[0]) + kind[1..],
                Content = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 4, 0, 0) },
            });
        }

        _tabsBuilt = true;
    }

    private void SelectTab(BrushKind kind)
    {
        int index = _kinds.FindIndex(name => ParseKind(name) == kind);
        KindTabs.SelectedIndex = index < 0 ? 0 : index;
    }

    /// <summary>
    /// Switching engine. With a library brush selected this leaves the library and offers a **new** brush of the
    /// chosen kind, because the kind is part of a brush's identity and no operation changes it - see the class
    /// remarks. With nothing selected it only changes which parameter set is being described.
    /// </summary>
    private void OnKindChanged()
    {
        if (_syncing || _vm is null)
        {
            return;
        }

        if (_editing is not null && Library is { } library && library.Kind != DraftKind())
        {
            _editing = null;
        }

        Refresh();
    }

    private void OnLibraryChanged()
    {
        if (_syncing || _vm is null)
        {
            return;
        }

        string? chosen = BrushLibraryBox.SelectedItem as string;
        _editing = chosen is null || chosen == NewBrushLabel ? null : chosen;
        Refresh();
    }

    /// <summary>
    /// Builds the parameter controls of one engine, once per engine.
    ///
    /// The controls come from the members the model states for that kind - not from a list of possibilities - and
    /// each carries the **operation parameter name** it commits through, so a control and the operation it calls
    /// cannot come apart: one word travels from the label, to the box, to <c>brush.set</c>.
    /// </summary>
    private void EnsureParameters(BrushKind kind)
    {
        if (_built && _builtKind == kind)
        {
            return;
        }

        _fields.Clear();
        _builtKind = kind;
        _built = true;

        if (ParameterHostFor(kind) is not { } host)
        {
            return;
        }

        host.Children.Clear();

        foreach (BrushField field in FieldsFor(kind))
        {
            Control control = BuildField(field);
            _fields[field.Parameter] = control;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var label = new TextBlock
            {
                Text = field.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(control, 1);
            row.Children.Add(label);
            row.Children.Add(control);
            host.Children.Add(row);
        }

        if (kind == BrushKind.Pattern)
        {
            BuildTileEditors(host);
        }
    }

    /// <summary>The stack a kind's controls live in: that kind's own tab, which is the middle section.</summary>
    private StackPanel? ParameterHostFor(BrushKind kind)
    {
        int index = _kinds.FindIndex(name => ParseKind(name) == kind);
        return index >= 0 && index < KindTabs.Items.Count && KindTabs.Items[index] is TabItem tab
            ? tab.Content as StackPanel
            : null;
    }

    private Control BuildField(BrushField field)
    {
        switch (field.Kind)
        {
            case FieldKind.Choice:
            {
                var combo = new ComboBox
                {
                    Name = "Field_" + field.Parameter,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = field.Choices,
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string chosen)
                    {
                        Commit(field.Parameter, chosen);
                    }
                };
                return combo;
            }

            case FieldKind.Bool:
            {
                var check = new CheckBox { Name = "Field_" + field.Parameter };
                check.IsCheckedChanged += (_, _) => Commit(field.Parameter, check.IsChecked == true);
                return check;
            }

            case FieldKind.Asset:
            {
                var combo = new ComboBox
                {
                    Name = "Field_" + field.Parameter,
                    Tag = AssetTag,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = AssetChoices(),
                };
                combo.SelectionChanged += (_, _) =>
                    Commit(field.Parameter, AssetId(combo.SelectedItem as string));
                return combo;
            }

            default:
            {
                var box = new TextBox { Name = "Field_" + field.Parameter };
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Commit(field.Parameter, Number(box.Text));
                        e.Handled = true;
                    }
                };
                box.LostFocus += (_, _) => Commit(field.Parameter, Number(box.Text));
                return box;
            }
        }
    }

    /// <summary>The id behind a picker label, or null for "(none)" and for a slot that names nothing.</summary>
    private Guid? AssetId(string? label)
        => label is null || label == NoAssetLabel ? null : _assetIds.GetValueOrDefault(label);

    private List<string> AssetChoices()
    {
        var choices = new List<string> { NoAssetLabel };
        choices.AddRange(_assetLabels);
        return choices;
    }

    /// <summary>
    /// Re-offers the document's artwork to every picker, so a drawing that gained an item offers it without the
    /// panel being reopened - an asset a person cannot choose because the list is stale is the panel describing a
    /// document that has moved on.
    /// </summary>
    private void RefreshAssetPickers()
    {
        foreach (Control control in _fields.Values)
        {
            if (control is ComboBox combo && (combo.Tag as string) == AssetTag)
            {
                combo.ItemsSource = AssetChoices();
            }
        }
    }

    /// <summary>
    /// The five tile slots of a pattern brush, each with the artwork it names and the controls that are that tile's
    /// own. One slot at a time through <c>brush.setTile</c>, which is what that operation exists for: a tile carries
    /// controls of its own, and one operation taking five slots and five sets of controls would be five operations
    /// wearing a hat.
    /// </summary>
    private void BuildTileEditors(StackPanel host)
    {
        foreach (PatternTileKind slot in Enum.GetValues<PatternTileKind>())
        {
            string name = SlotName(slot);

            host.Children.Add(new TextBlock
            {
                Text = name.ToUpperInvariant(),
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });

            var asset = new ComboBox
            {
                Name = "Tile_" + name,
                Tag = AssetTag,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = AssetChoices(),
            };
            asset.SelectionChanged += (_, _) => CommitTile(name, "asset", AssetId(asset.SelectedItem as string));
            host.Children.Add(asset);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,10,Auto,*") };
            TextBox rotation = NumberBox("Tile_" + name + "_rotation", value => CommitTile(name, "rotation", value));
            TextBox scale = NumberBox("Tile_" + name + "_scale", value => CommitTile(name, "scale", value));
            TextBlock rotationLabel = SmallLabel("Turn");
            TextBlock scaleLabel = SmallLabel("Scale");

            Grid.SetColumn(rotationLabel, 0);
            Grid.SetColumn(rotation, 1);
            Grid.SetColumn(scaleLabel, 3);
            Grid.SetColumn(scale, 4);
            grid.Children.Add(rotationLabel);
            grid.Children.Add(rotation);
            grid.Children.Add(scaleLabel);
            grid.Children.Add(scale);
            host.Children.Add(grid);

            var flips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            flips.Children.Add(FlipBox("Tile_" + name + "_flipAcross", "Across",
                value => CommitTile(name, "flipAcross", value)));
            flips.Children.Add(FlipBox("Tile_" + name + "_flipAlong", "Along",
                value => CommitTile(name, "flipAlong", value)));
            host.Children.Add(flips);

            _fields["tile." + name + ".asset"] = asset;
            _fields["tile." + name + ".rotation"] = rotation;
            _fields["tile." + name + ".scale"] = scale;
        }
    }

    private static TextBlock SmallLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBox NumberBox(string name, Action<double?> commit)
    {
        var box = new TextBox { Name = name };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                commit(Number(box.Text));
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => commit(Number(box.Text));
        return box;
    }

    private static CheckBox FlipBox(string name, string label, Action<bool> commit)
    {
        var check = new CheckBox { Name = name, Content = label };
        check.IsCheckedChanged += (_, _) => commit(check.IsChecked == true);
        return check;
    }

    // ------------------------------------------------------------------
    // What each engine takes
    // ------------------------------------------------------------------

    private enum FieldKind
    {
        Number,
        Bool,
        Choice,
        Asset,
    }

    /// <summary>
    /// One control of one engine: what it says, the <c>brush.set</c> parameter it commits through, and how to read
    /// that parameter off a brush.
    ///
    /// The reader is only ever a **display** reader. Writing goes the other way - through the registry, by parameter
    /// name - so this file holds no second implementation of what a parameter means, and a value the operation
    /// clamps shows up as what it became rather than as what was typed.
    /// </summary>
    private sealed record BrushField(
        string Label,
        string Parameter,
        FieldKind Kind,
        Func<BrushSpec, object?> Show,
        string[]? Choices = null);

    private static readonly string[] StretchChoices =
    {
        nameof(ArtStretch.StretchToFit), nameof(ArtStretch.ScaleProportionally), nameof(ArtStretch.Repeat),
    };

    private static readonly string[] ColourisationChoices =
    {
        nameof(ArtColourisation.None), nameof(ArtColourisation.Tint), nameof(ArtColourisation.TintAndShade),
    };

    private static BrushField[] FieldsFor(BrushKind kind) => kind switch
    {
        BrushKind.Art => new BrushField[]
        {
            new("Asset", "asset", FieldKind.Asset, brush => brush.ArtAsset),
            new("Size", "size", FieldKind.Number, brush => brush.Diameter),
            new("Stretch", "stretch", FieldKind.Choice, brush => brush.Stretch.ToString(), StretchChoices),
            new("Flip across", "flipAcross", FieldKind.Bool, brush => brush.FlipAcross),
            new("Flip along", "flipAlong", FieldKind.Bool, brush => brush.FlipAlong),
            new("Colourisation", "colourisation", FieldKind.Choice,
                brush => brush.Colourisation.ToString(), ColourisationChoices),
        },

        BrushKind.Pattern => new BrushField[]
        {
            new("Size", "size", FieldKind.Number, brush => brush.Diameter),
            new("Spacing", "spacing", FieldKind.Number, brush => brush.PatternSpacing),
            new("Corner threshold", "cornerThreshold", FieldKind.Number,
                brush => brush.PatternCornerThresholdDegrees),
        },

        BrushKind.Scatter => new BrushField[]
        {
            new("Asset", "asset", FieldKind.Asset, brush => brush.ScatterSpec?.Asset),
            new("Size", "size", FieldKind.Number, brush => brush.Diameter),
            new("Spacing", "spacing", FieldKind.Number, brush => brush.ScatterSpec?.Spacing.Value),
            new("Spacing ±", "spacingRandomness", FieldKind.Number,
                brush => brush.ScatterSpec?.Spacing.Randomness),
            new("Rotation", "rotation", FieldKind.Number, brush => brush.ScatterSpec?.Rotation.Value),
            new("Rotation ±", "rotationRandomness", FieldKind.Number,
                brush => brush.ScatterSpec?.Rotation.Randomness),
            new("Scale", "scale", FieldKind.Number, brush => brush.ScatterSpec?.Scale.Value),
            new("Scale ±", "scaleRandomness", FieldKind.Number,
                brush => brush.ScatterSpec?.Scale.Randomness),
            new("Offset", "offset", FieldKind.Number, brush => brush.ScatterSpec?.Offset.Value),
            new("Offset ±", "offsetRandomness", FieldKind.Number,
                brush => brush.ScatterSpec?.Offset.Randomness),
            new("Opacity", "opacity", FieldKind.Number, brush => brush.ScatterSpec?.Opacity.Value),
            new("Opacity ±", "opacityRandomness", FieldKind.Number,
                brush => brush.ScatterSpec?.Opacity.Randomness),
        },

        BrushKind.Bristle => BristleFields,

        _ => new BrushField[]
        {
            new("Angle", "angle", FieldKind.Number, brush => brush.AngleDegrees),
            new("Roundness", "roundness", FieldKind.Number, brush => brush.Roundness),
            new("Diameter", "diameter", FieldKind.Number, brush => brush.Diameter),
        },
    };

    /// <summary>
    /// The controls a bristle brush's bundle is edited through, beside the shared size.
    ///
    /// The size is the bundle's width across the stroke and is stated on the brush rather than in the spec, for the
    /// reason every other kind's size is: it is the same member whichever kind the brush is. Everything else is the
    /// bundle's own, and each one is written through the registry by parameter name like the rest.
    /// </summary>
    private static readonly BrushField[] BristleFields =
    {
        new("Size", "size", FieldKind.Number, brush => brush.Diameter),
        new("Bristles", "count", FieldKind.Number, brush => brush.BristleSpec?.Count),
        new("Length", "length", FieldKind.Number, brush => brush.BristleSpec?.Length),
        new("Stiffness", "stiffness", FieldKind.Number, brush => brush.BristleSpec?.Stiffness),
        new("Thickness", "thickness", FieldKind.Number, brush => brush.BristleSpec?.Thickness),
        new("Spread", "spread", FieldKind.Number, brush => brush.BristleSpec?.Spread),
        new("Randomness", "randomness", FieldKind.Number, brush => brush.BristleSpec?.Randomness),
        new("Pressure spread", "pressureSpread", FieldKind.Number, brush => brush.BristleSpec?.PressureSpread),
        new("Tilt turn", "tiltTurn", FieldKind.Number, brush => brush.BristleSpec?.TiltTurn),
        new("Colour jitter", "colourJitter", FieldKind.Number, brush => brush.BristleSpec?.ColourJitter),
    };

    /// <summary>
    /// The brush a kind starts from: the model's own factory, so a new brush's defaults are the model's rather than
    /// this panel's guesses.
    /// </summary>
    private static BrushSpec Defaults(BrushKind kind) => kind switch
    {
        BrushKind.Art => BrushSpec.Art(DefaultBrushName, null, 12.0),
        BrushKind.Pattern => BrushSpec.Pattern(DefaultBrushName, 12.0),
        BrushKind.Scatter => BrushSpec.Scatter(DefaultBrushName, null, 12.0),
        BrushKind.Bristle => BrushSpec.Bristle(DefaultBrushName, 24.0),
        _ => BrushSpec.Calligraphic(DefaultBrushName, 45.0, 0.25, 12.0),
    };

    // ------------------------------------------------------------------
    // Showing, and committing
    // ------------------------------------------------------------------

    /// <summary>Writes a brush's values into the controls of the selected engine.</summary>
    private void ShowValues(BrushSpec brush)
    {
        foreach (BrushField field in FieldsFor(brush.Kind))
        {
            if (_fields.TryGetValue(field.Parameter, out Control? control))
            {
                Write(control, field, field.Show(brush));
            }
        }

        if (brush.Kind != BrushKind.Pattern)
        {
            return;
        }

        foreach (PatternTileKind slot in Enum.GetValues<PatternTileKind>())
        {
            string name = SlotName(slot);
            PatternTileSpec? tile = brush.Tile(slot);

            if (_fields.TryGetValue("tile." + name + ".asset", out Control? asset) && asset is ComboBox combo)
            {
                combo.SelectedItem = tile?.Asset is { } id && _labelsById.TryGetValue(id, out string? label)
                    ? label
                    : NoAssetLabel;
            }

            WriteNumber(_fields, "tile." + name + ".rotation", tile?.RotationDegrees ?? 0.0);
            WriteNumber(_fields, "tile." + name + ".scale", tile?.Scale ?? 1.0);
        }
    }

    private static void WriteNumber(Dictionary<string, Control> fields, string key, double value)
    {
        if (fields.TryGetValue(key, out Control? control) && control is TextBox box)
        {
            box.Text = Text(value);
        }
    }

    private void Write(Control control, BrushField field, object? value)
    {
        switch (field.Kind)
        {
            case FieldKind.Choice when control is ComboBox combo:
                combo.SelectedItem = value as string;
                break;

            case FieldKind.Bool when control is CheckBox check:
                check.IsChecked = value is bool on && on;
                break;

            case FieldKind.Asset when control is ComboBox assets:
                assets.SelectedItem = value is Guid id && _labelsById.TryGetValue(id, out string? label)
                    ? label
                    : NoAssetLabel;
                break;

            case FieldKind.Number when control is TextBox box:
                box.Text = value is double number ? Text(number) : string.Empty;
                break;
        }
    }

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double? Number(string? text)
        => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
           double.IsFinite(value)
            ? value
            : null;

    /// <summary>
    /// Writes one parameter of the edit, through the operation a driver would call.
    ///
    /// It goes to <c>brush.set</c>, which re-points every stroke that uses the brush - that is what makes a brush an
    /// asset rather than a copy, and it is why this control is not allowed to write the model itself.
    /// </summary>
    private void Commit(string parameter, object? value)
    {
        if (_vm is null || _syncing)
        {
            return;
        }

        if (Library is not { } library)
        {
            // Nothing is being edited, so there is no brush this could honestly be written to. New brush is what
            // makes one exist; a field typed over a brush that is not there would be a value the document never sees.
            Status("Choose a brush, or create one with New brush.");
            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = library.Name,
            [parameter] = value,
        };

        if (Invoke("brush.set", parameters) is null)
        {
            return;
        }

        Refresh();
    }

    /// <summary>Fills or clears one slot of a pattern brush's tile set, through <c>brush.setTile</c>.</summary>
    private void CommitTile(string slot, string parameter, object? value)
    {
        if (_vm is null || _syncing || Library is not { } library)
        {
            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = library.Name,
            ["slot"] = slot,
            [parameter] = value,
        };

        if (Invoke("brush.setTile", parameters) is null)
        {
            return;
        }

        Refresh();
    }

    // ------------------------------------------------------------------
    // New, rename, delete, apply
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates the brush the pane is describing, through <c>brush.create</c>, with the name in the box and the kind
    /// the tabs are on.
    ///
    /// The brush appears in the library immediately and is then edited **live** through <c>brush.set</c>, which is
    /// what keeps this panel a view of the document rather than a form that has to be reconciled with it: there is
    /// one brush, and it is the document's from the moment it exists.
    /// </summary>
    private void NewBrush()
    {
        if (_vm is null)
        {
            return;
        }

        string name = (BrushNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Status("A brush needs a name: strokes refer to it by name.");
            return;
        }

        BrushKind kind = DraftKind();
        if (Invoke("brush.create", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["kind"] = kind.ToString().ToLowerInvariant(),
            }) is null)
        {
            return;
        }

        _editing = name;
        Refresh();
        Status($"Created '{name}'.");
    }

    /// <summary>Renames the brush, together with every stroke that named it - what <c>brush.rename</c> does.</summary>
    private void Rename()
    {
        if (_vm is null || Library is not { } library)
        {
            return;
        }

        string name = (BrushNameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0 || name == library.Name)
        {
            return;
        }

        if (Invoke("brush.rename", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["from"] = library.Name,
                ["to"] = name,
            }) is null)
        {
            return;
        }

        _editing = name;
        Refresh();
    }

    private void Delete()
    {
        if (_vm is null || Library is not { } library)
        {
            return;
        }

        if (Invoke("brush.delete", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = library.Name,
            }) is null)
        {
            return;
        }

        _editing = null;
        Refresh();
        Status($"Deleted '{library.Name}'.");
    }

    /// <summary>
    /// Sweeps the selection's strokes with this brush, through <c>brush.apply</c>.
    ///
    /// The **inspected** stroke index travels when there is one, the same way the stroke inspector's own picker
    /// does: a panel describing stroke 2 of 3 that wrote every stroke of the stack would be editing two strokes
    /// nobody was looking at. With nothing inspected there is no stroke to single out, so the operation's own
    /// default applies - every stroke of every selected path.
    /// </summary>
    private void ApplyToSelection()
    {
        if (_vm is null || Library is not { } library)
        {
            return;
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = library.Name };
        if (_vm.ActiveSession.SelectedPaths().FirstOrDefault() is { } path &&
            _vm.InspectedStroke >= 0 && _vm.InspectedStroke < path.Strokes.Count)
        {
            parameters["strokeIndex"] = _vm.InspectedStroke;
        }

        if (Invoke("brush.apply", parameters) is null)
        {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// What the selection is swept with: the brush every selected path agrees on, or **mixed**.
    ///
    /// Never the first path's brush presented as everyone's - a person reading "Chisel" beside a selection that half
    /// carries it would conclude the whole selection does, and then apply a brush believing they were replacing one
    /// they were adding to.
    /// </summary>
    private void RefreshApplicationLabel()
    {
        if (_vm is null)
        {
            ApplicationLabel.Text = string.Empty;
            return;
        }

        var names = new List<string?>();
        int index = _vm.InspectedStroke;
        foreach (PathItem path in _vm.ActiveSession.SelectedPaths())
        {
            if (index >= 0 && index < path.Strokes.Count)
            {
                names.Add(path.Strokes[index].Brush?.Name);
            }
        }

        if (names.Count == 0)
        {
            ApplicationLabel.Text = "Nothing selected to sweep.";
            return;
        }

        bool agreed = names.Skip(1).All(name => name == names[0]);
        ApplicationLabel.Text = !agreed
            ? MixedWord + ": the selection's strokes do not all carry one brush."
            : names[0] is { } single
                ? $"Selection is swept with '{single}'."
                : "Selection has no brush.";
    }

    // ------------------------------------------------------------------
    // Running an operation, the way a driver would
    // ------------------------------------------------------------------

    private object? Invoke(string operation, Dictionary<string, object?> parameters)
    {
        try
        {
            return EditorOperations.Invoke(
                new AutomationContext { ViewModel = _vm },
                operation,
                JsonSerializer.SerializeToElement(parameters));
        }
        catch (EditorOperationException ex)
        {
            // A refusal is information a person can act on - a name already taken, a slot naming artwork that is
            // gone - and a control handler that threw would surface as a crash rather than as a refusal.
            Status(ex.Message);
            return null;
        }
    }

    private void Status(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.IsVisible = message.Length > 0;
    }

    private static string SlotName(PatternTileKind slot) => slot switch
    {
        PatternTileKind.InnerCorner => "innerCorner",
        PatternTileKind.OuterCorner => "outerCorner",
        _ => slot.ToString().ToLowerInvariant(),
    };

    // ------------------------------------------------------------------
    // What a test reads
    // ------------------------------------------------------------------

    /// <summary>
    /// The control for one parameter, by the operation parameter name it commits through.
    ///
    /// Exposed because the parameter controls are built from the model rather than declared in XAML, so they sit in
    /// no namescope for `FindControl` to walk. A test asks for `"roundness"` or `"tile.side.rotation"` and gets the
    /// control the panel built for it.
    /// </summary>
    internal Control? Field(string parameter) => _fields.GetValueOrDefault(parameter);

    /// <summary>The engines the tabs offer, which is the list <c>brush.kinds</c> reported.</summary>
    internal IReadOnlyList<string> KindNames => _kinds;

    /// <summary>The preview the pane shows, so a test can ask what it is drawing rather than that it exists.</summary>
    internal BrushPreviewView Preview => _preview;
}
