using Avalonia.Headless.XUnit;
using VCCad.App.Controls;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The busy indicator. While the assistant has control the editor is locked, and the
/// only sign of it used to be the Cancel button inside the diagnostics panel - easy
/// to have closed, and silent during a long step. These tests pin the two things that
/// make the spinner that sign: it appears exactly while the assistant is busy, and it
/// actually turns.
/// </summary>
public class BusySpinnerTests
{
    [AvaloniaFact]
    public void TheSpinnerAppearsOnlyWhileItIsSpinning()
    {
        var spinner = new BusySpinner();

        Assert.False(spinner.IsSpinning);
        Assert.False(spinner.IsVisible, "an idle spinner must not be on screen");

        spinner.IsSpinning = true;
        Assert.True(spinner.IsVisible, "a spinning pizza must be visible");

        spinner.IsSpinning = false;
        Assert.False(spinner.IsVisible);
    }

    [AvaloniaFact]
    public void TurningAdvancesTheAngleAndWrapsAtAFullTurn()
    {
        var spinner = new BusySpinner { DegreesPerTick = 30 };
        double start = spinner.Angle;

        spinner.Advance();
        Assert.Equal(30, spinner.Angle, 6);

        // Eleven more 30-degree steps completes the turn and wraps back to the start.
        for (int i = 0; i < 11; i++)
        {
            spinner.Advance();
        }

        Assert.Equal(start, spinner.Angle, 6);
    }

    [AvaloniaFact]
    public void TheBadgeAppearsOnlyWhileTheAssistantHasControl()
    {
        var badge = new AssistantBusyIndicator();

        Assert.False(badge.IsBusy, "the badge starts idle");
        Assert.False(badge.IsVisible, "an idle badge must not be on screen");

        badge.SetBusy(true);
        Assert.True(badge.IsBusy);
        Assert.True(badge.IsVisible, "the corner of the view must show the assistant is working");
        Assert.True(badge.Spinner.IsVisible, "the badge must actually be spinning");
        Assert.True(badge.Spinner.IsSpinning);

        badge.SetBusy(false);
        Assert.False(badge.IsBusy);
        Assert.False(badge.IsVisible);
        Assert.False(badge.Spinner.IsSpinning);
    }
}
