using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VCCad.App.Automation;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Point-and-click automation has to reach the control a person's click would reach.
///
/// Three defects are pinned here, the hit test from two sides. A text field was refused
/// outright by <c>ui.click</c> ("not directly clickable"), so the caret could never be
/// placed. <c>ui.setValue</c> wrote Text without committing it, so a panel that commits on
/// Enter or on leaving the field never saw the change and its next refresh overwrote the box
/// — the operation reported success and nothing happened. And the click hit test scanned the
/// visual descendants and kept the LAST control whose rectangle contained the point, which is
/// traversal order and not z-order: a control that was not on top took the click, silently,
/// with the operation still reporting success. That last one is the bug that shipped.
/// </summary>
public class UiAutomationClickTests
{
    /// <summary>A control that records the pointer presses it is sent.</summary>
    private sealed class PressProbe : Border
    {
        public int Presses { get; private set; }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Presses++;
            base.OnPointerPressed(e);
        }
    }

    /// <summary>
    /// Hosts content in a real headless window and runs a layout pass.
    ///
    /// The click path needs a TopLevel to translate window coordinates and to hit test
    /// against, and both the text field and the probe need laid-out bounds, so a bare
    /// control is not enough.
    /// </summary>
    private static Window Host(Control content)
    {
        var window = new Window
        {
            Width = 240,
            Height = 160,
            Content = content,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // A render pass is what applies z-order to the visual tree, and the hit test asks
        // the tree for what is on top. Without it the overlay's paint order is not settled
        // and the result depends on what ran before.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void ClickingATextBoxPutsTheCaretInIt()
    {
        var box = new TextBox { Name = "Field", Text = "abc", Width = 120, Height = 24 };
        Window window = Host(box);
        try
        {
            string result = UiAutomation.Click(box);

            Assert.Contains("focused", result, StringComparison.OrdinalIgnoreCase);
            Assert.True(box.IsFocused, "clicking a text field must put the caret in it");
            Assert.Equal(3, box.CaretIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SetValueAppliesToAFieldThatCommitsWhenItLosesFocus()
    {
        // This is the shape of the colour pane's hex entry: the value is read when focus
        // leaves the box. Setting Text alone leaves the model untouched, and the panel's
        // next refresh puts the old text back.
        string? committed = null;
        var box = new TextBox { Name = "Field", Text = "old", Width = 120, Height = 24 };
        box.LostFocus += (_, _) => committed = box.Text;

        Window window = Host(box);
        try
        {
            UiAutomation.SetValue(box, "new");

            Assert.Equal("new", box.Text);
            Assert.Equal("new", committed);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickReachesTheTopmostControlNotTheLastOneTheTraversalVisited()
    {
        // Paint order and traversal order disagree here, which is the shape of the bug. The
        // target is painted on top (higher ZIndex) but added first; the overlay is added
        // second, so a descendant walk reaches it last. The old hit test kept the last control
        // it visited, so a control that was not on top took the click — silently, while the
        // operation reported success.
        var target = new PressProbe
        {
            Name = "Target",
            Background = Brushes.Blue,
            ZIndex = 10,
        };
        var overlay = new PressProbe
        {
            Name = "Overlay",
            Background = Brushes.Red,
            ZIndex = 0,
        };

        var panel = new Panel();
        panel.Children.Add(target);
        panel.Children.Add(overlay);

        Window window = Host(panel);
        try
        {
            string result = UiAutomation.Click(target);

            Assert.Equal(1, target.Presses);
            Assert.Equal(0, overlay.Presses);
            Assert.Contains(nameof(PressProbe), result, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickSkipsAnOverlayThatRefusesHitTesting()
    {
        // Avalonia's own adorner layers sit above the content, cover the whole window and
        // take no input. A hit test that only asks "does this rectangle contain the point"
        // delivers the click to them.
        var target = new PressProbe { Name = "Target", Background = Brushes.Blue };
        var overlay = new PressProbe
        {
            Name = "Overlay",
            Background = Brushes.Red,
            IsHitTestVisible = false,
        };

        var panel = new Panel();
        panel.Children.Add(target);
        panel.Children.Add(overlay);

        Window window = Host(panel);
        try
        {
            UiAutomation.Click(target);

            Assert.Equal(1, target.Presses);
            Assert.Equal(0, overlay.Presses);
        }
        finally
        {
            window.Close();
        }
    }
}
