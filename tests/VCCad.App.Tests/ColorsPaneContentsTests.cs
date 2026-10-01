using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What the colour picker is allowed to contain.
///
/// A colour picker chooses a colour. The **fill rule** - whether a self-crossing or holed outline means
/// nonzero or even-odd - is a property of the outline's geometry, not of its colour, and it was removed from
/// this pane once already for exactly that reason. It came back, which is why this is a test and not another
/// comment: the last removal left nothing behind that would notice the regression.
///
/// The capability is not being removed with the control. `style.setFillRule` is in the operation registry, so
/// a person reaches it from the diagnostics overlay's Operations tab and a driver reaches it over the API.
/// </summary>
public class ColorsPaneContentsTests
{
    private static (Window Window, ColorsPane Pane) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new ColorsPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = pane };
        window.Show();
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        return (window, pane);
    }

    /// <summary>No control in the picker is labelled with the fill rule.</summary>
    [AvaloniaFact]
    public void ThePickerHasNoFillRuleControl()
    {
        (Window window, ColorsPane pane) = Host();
        try
        {
            var labelled = pane.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => t is not null)
                .ToList();

            Assert.DoesNotContain("Rule", labelled);

            // And nothing is still pointing at a control that no longer exists: a dangling x:Name shows up
            // as a null here rather than as a crash, which is how the last removal could have gone unnoticed.
            Assert.DoesNotContain(
                pane.GetVisualDescendants().OfType<ComboBox>(),
                box => box.Name == "FillRuleBox");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What the control was for is still reachable, as an operation.</summary>
    [Fact]
    public void TheFillRuleIsStillAnOperation()
        => Assert.Contains(EditorOperations.All, o => o.Name == "style.setFillRule");
}
