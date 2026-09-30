using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views;

/// <summary>
/// The Pathfinder panel: the five boolean operations as buttons.
///
/// Buttons rather than menu entries, because this is a thing a person does repeatedly while looking at
/// the artwork - and every button is **named**, so `ui.find` finds it and `ui.click` presses it. The
/// panel is a way in, not a second implementation: it and the operation call the same session method, so
/// there is one definition of what union means.
///
/// A button is **disabled when the selection cannot use it**, with the reason in its tooltip. A control
/// that is present and does nothing when pressed is worse than one that says why it cannot - the reason
/// is the useful part, and it is what makes the panel teachable without a manual.
/// </summary>
public sealed class PathfinderPane : UserControl
{
    private EditorViewModel? _viewModel;
    private readonly List<(Button Button, int MinPaths, string Tip, string Needs)> _buttons = new();

    public PathfinderPane()
    {
        Name = "PathfinderPane";
    }

    /// <summary>The buttons, for tests: what they are called and whether they can be used.</summary>
    public IReadOnlyList<(string Name, bool Enabled)> Buttons
        => _buttons.Select(b => (b.Button.Name ?? string.Empty, b.Button.IsEnabled)).ToArray();

    /// <summary>The tooltip a button is showing, which is where the reason lives when it is disabled.</summary>
    public string TipFor(string name)
    {
        Button? button = _buttons.Select(b => b.Button).FirstOrDefault(b => b.Name == name);
        return button is null ? string.Empty : ToolTip.GetTip(button)?.ToString() ?? string.Empty;
    }

    public void Attach(EditorViewModel viewModel)
    {
        _viewModel = viewModel;
        Content = Build();
        _viewModel.SelectionChanged += (_, _) => Refresh();
        Refresh();
    }

    private Control Build()
    {
        var panel = new StackPanel
        {
            Name = "PathfinderContent",
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(8),
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Pathfinder",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });

        var booleans = new WrapPanel { Orientation = Orientation.Horizontal, Name = "PathfinderButtons" };
        booleans.Children.Add(MakeButton("PathfinderUnion", "Union", 2,
            "Merge the selected paths into one region",
            () => ViewModel.BooleanSelection(BooleanOp.Union)));
        booleans.Children.Add(MakeButton("PathfinderSubtract", "Subtract", 2,
            "Remove the front paths from the back-most one",
            () => ViewModel.BooleanSelection(BooleanOp.Subtract)));
        booleans.Children.Add(MakeButton("PathfinderIntersect", "Intersect", 2,
            "Keep only what every selected path covers",
            () => ViewModel.BooleanSelection(BooleanOp.Intersect)));
        booleans.Children.Add(MakeButton("PathfinderExclude", "Exclude", 2,
            "Keep what an odd number of the selected paths cover",
            () => ViewModel.BooleanSelection(BooleanOp.Exclude)));
        booleans.Children.Add(MakeButton("PathfinderDivide", "Divide", 2,
            "Cut the selected paths into their separate regions",
            () => ViewModel.DivideSelection()));

        panel.Children.Add(booleans);

        var compound = new WrapPanel { Orientation = Orientation.Horizontal, Name = "PathfinderCompoundButtons" };
        compound.Children.Add(MakeButton("PathfinderRelease", "Release", 1,
            "Take the selected compound paths apart into their outlines",
            () => ViewModel.ReleaseCompoundSelection()));
        compound.Children.Add(MakeButton("PathfinderReverse", "Reverse", 1,
            "Turn the first outline inside out: a hole becomes an island",
            () => ViewModel.ReverseSubpath(0)));

        panel.Children.Add(compound);
        return panel;
    }

    private EditorViewModel ViewModel
        => _viewModel ?? throw new InvalidOperationException("The pane has not been attached to an editor.");

    private Button MakeButton(string name, string text, int minPaths, string tip, Action run)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            MinWidth = 74,
        };

        ToolTip.SetTip(button, tip);

        button.Click += (_, _) =>
        {
            try
            {
                run();
            }
            catch (InvalidOperationException error)
            {
                // The button should have been disabled, so this is reached only when the selection
                // changed between the check and the press. Report it rather than failing silently.
                ViewModel.ReportStatus(error.Message);
            }

            Refresh();
        };

        _buttons.Add((button, minPaths, tip, $"Select at least {minPaths} path(s)"));
        return button;
    }

    /// <summary>
    /// A button is usable when enough paths are selected. Today that is all any of them needs, and
    /// saying it in one place means a later operation with a real precondition has somewhere to put it.
    /// </summary>
    private void Refresh()
    {
        if (_viewModel is null)
        {
            return;
        }

        int selected = _viewModel.SelectedObjects.Count;

        foreach ((Button button, int minPaths, string tip, string needs) in _buttons)
        {
            button.IsEnabled = selected >= minPaths;
            ToolTip.SetTip(button, button.IsEnabled ? tip : needs);
        }
    }
}
