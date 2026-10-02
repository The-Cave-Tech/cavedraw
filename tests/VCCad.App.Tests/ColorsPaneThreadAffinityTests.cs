using System.Text.Json;
using Avalonia.Headless.XUnit;
using VCCad.App.Automation;
using VCCad.App.Picking;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The colour pane against the thread rule, not against a colour.
///
/// <see cref="EditorColorState.Shared"/> is process-wide, and the pane subscribes to it for the
/// life of the panel rather than for the life of a test. So a change made anywhere reaches the pane
/// on the thread that made it - and <c>color.pickScreen</c> genuinely makes one on a background
/// continuation. The pane must not write its controls there.
///
/// **Why this is not in <c>ScreenPickTests</c>, and why it is deterministic when that test is not.**
/// Reported as #178, `ScreenPickTests.PickingReadsTheScreenRatherThanAnythingCached` failed about
/// one full-suite run in twelve and never alone. The reason is that it is a plain <c>[Fact]</c>: it
/// runs on the runner's own thread, while the pane that receives its colour change was left
/// subscribed by an <c>[AvaloniaFact]</c> in another class. Whether those two facts meet depends on
/// what else is in the suite and on which thread the runner hands the test, which is exactly the
/// kind of signal that trains a person to re-run until green. This test states the condition the
/// full suite only sometimes produced - a live pane, a pick from another thread - and produces it
/// itself, so it fails or passes for the same reason every time it runs, alone or in a suite.
/// </summary>
public class ColorsPaneThreadAffinityTests : IDisposable
{
    private readonly ColorRgb? _wasPicked = EditorColorState.Shared.LastPicked;

    /// <summary>A screen that always answers with one colour, so the pick cannot fail of its own accord.</summary>
    private sealed class FakeScreen : IScreenColourSampler
    {
        public bool IsSupported => true;

        public ColorRgb? Sample(int x, int y) => ColorRgb.FromBytes(18, 52, 86);
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    public void Dispose()
    {
        EditorColorState.Shared.RestorePicked(_wasPicked);
        ScreenColour.ResetSampler();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A pick made off the UI thread must not reach a control on that thread.
    ///
    /// The pane is attached and stays attached for the body of the test - the state the other
    /// colour-pane tests leave behind, because the pane is built for the panel and not for the test
    /// and nothing takes it out of a visual tree it was never put in. That pane is precisely the one
    /// the colour change lands on, so this is the full suite's condition rather than a fresh pane
    /// that nothing else can reach.
    /// </summary>
    [AvaloniaFact]
    public void APickFromAnotherThreadDoesNotWriteAControlOnThatThread()
    {
        var pane = new ColorsPane();
        pane.Attach(new EditorViewModel());
        ScreenColour.Sampler = new FakeScreen();

        try
        {
            Exception? failure = null;

            // A thread of its own, not the thread pool: the pick has to be off the UI thread and the
            // assertion has to be about that, not about which worker the pool happened to hand over.
            var picking = new Thread(() =>
            {
                try
                {
                    EditorOperations.Invoke(
                        new AutomationContext { ViewModel = new EditorViewModel() },
                        "color.pickAt",
                        Params(new { x = 1, y = 1 }));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            picking.Start();
            picking.Join();

            // The pick itself has to have happened, or "no exception" would also be true of a
            // pick that never reached the pane at all.
            Assert.Equal(ColorRgb.FromBytes(18, 52, 86), EditorColorState.Shared.LastPicked);
            Assert.True(failure is null, failure?.ToString());
        }
        finally
        {
            pane.Detach();
            EditorColorState.Shared.RestorePicked(_wasPicked);
        }
    }
}
