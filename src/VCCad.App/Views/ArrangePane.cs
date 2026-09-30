using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views;

/// <summary>
/// The Align panel: the six alignments and the distributions as buttons.
///
/// Buttons rather than menu entries, for the same reason the Pathfinder has them - this is a thing a
/// person does repeatedly while looking at the artwork - and every button is **named**, so `ui.find`
/// finds it and `ui.click` presses it. The panel is a way in, not a second implementation: each button
/// calls the same session method the operation calls.
///
/// A button is **disabled unless the selection can use it**: aligning needs two objects, distributing
/// needs three (two are already evenly spaced, so there is nothing between them to even out).
/// </summary>
public sealed class ArrangePane : UserControl
{
    private EditorViewModel? _viewModel;
    private readonly List<(Button Button, int MinObjects, string Tip, string Needs)> _buttons = new();

    public ArrangePane()
    {
        Name = "ArrangePane";
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

    private EditorViewModel ViewModel
        => _viewModel ?? throw new InvalidOperationException("The pane has not been attached to an editor.");

    private Control Build()
    {
        var panel = new StackPanel
        {
            Name = "ArrangeContent",
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(8),
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Align",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });

        var horizontal = new WrapPanel { Orientation = Orientation.Horizontal, Name = "AlignHorizontalButtons" };
        horizontal.Children.Add(MakeButton("AlignLeft", "Left", 2,
            "Line up the left edges",
            () => ViewModel.AlignSelection(ArrangeAxis.Horizontal, ArrangeEdge.Start)));
        horizontal.Children.Add(MakeButton("AlignCentre", "Centre", 2,
            "Line up the horizontal centres",
            () => ViewModel.AlignSelection(ArrangeAxis.Horizontal, ArrangeEdge.Centre)));
        horizontal.Children.Add(MakeButton("AlignRight", "Right", 2,
            "Line up the right edges",
            () => ViewModel.AlignSelection(ArrangeAxis.Horizontal, ArrangeEdge.End)));
        panel.Children.Add(horizontal);

        var vertical = new WrapPanel { Orientation = Orientation.Horizontal, Name = "AlignVerticalButtons" };
        vertical.Children.Add(MakeButton("AlignTop", "Top", 2,
            "Line up the top edges",
            () => ViewModel.AlignSelection(ArrangeAxis.Vertical, ArrangeEdge.Start)));
        vertical.Children.Add(MakeButton("AlignMiddle", "Middle", 2,
            "Line up the vertical centres",
            () => ViewModel.AlignSelection(ArrangeAxis.Vertical, ArrangeEdge.Centre)));
        vertical.Children.Add(MakeButton("AlignBottom", "Bottom", 2,
            "Line up the bottom edges",
            () => ViewModel.AlignSelection(ArrangeAxis.Vertical, ArrangeEdge.End)));
        panel.Children.Add(vertical);

        panel.Children.Add(new TextBlock
        {
            Text = "Distribute",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        });

        var distribute = new WrapPanel { Orientation = Orientation.Horizontal, Name = "DistributeButtons" };
        distribute.Children.Add(MakeButton("DistributeHorizontal", "Horiz", 3,
            "Even out the horizontal gaps, first object first",
            () => ViewModel.DistributeSelection(ArrangeAxis.Horizontal, ArrangeAnchor.Start)));
        distribute.Children.Add(MakeButton("DistributeHorizontalEnd", "Horiz <-", 3,
            "Even out the horizontal gaps, last object first",
            () => ViewModel.DistributeSelection(ArrangeAxis.Horizontal, ArrangeAnchor.End)));
        distribute.Children.Add(MakeButton("DistributeVertical", "Vert", 3,
            "Even out the vertical gaps, first object first",
            () => ViewModel.DistributeSelection(ArrangeAxis.Vertical, ArrangeAnchor.Start)));
        distribute.Children.Add(MakeButton("DistributeVerticalEnd", "Vert <-", 3,
            "Even out the vertical gaps, last object first",
            () => ViewModel.DistributeSelection(ArrangeAxis.Vertical, ArrangeAnchor.End)));
        panel.Children.Add(distribute);

        return panel;
    }

    private Button MakeButton(string name, string text, int minObjects, string tip, Action run)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            MinWidth = 62,
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
                ViewModel.ReportStatus(error.Message);
            }

            Refresh();
        };

        _buttons.Add((button, minObjects, tip, $"Select at least {minObjects} object(s)"));
        return button;
    }

    private void Refresh()
    {
        if (_viewModel is null)
        {
            return;
        }

        int selected = _viewModel.SelectedObjects.Count;

        foreach ((Button button, int minObjects, string tip, string needs) in _buttons)
        {
            button.IsEnabled = selected >= minObjects;
            ToolTip.SetTip(button, button.IsEnabled ? tip : needs);
        }
    }
}
