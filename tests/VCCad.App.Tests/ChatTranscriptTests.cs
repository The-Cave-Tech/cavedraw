using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Ai;
using VCCad.App.Views;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The assistant transcript has to keep the newest text in sight. A person watching
/// the model work should never have to scroll to find out what it just said, and the
/// panel is handed the whole conversation again on every step, so the interesting
/// case is not the first fill but the append after it.
///
/// The regression this pins: the view used to clear its list and refill it, then ask
/// for the newest row. Replacing the items destroys the row containers, so the scroll
/// request had nothing to act on and the panel stayed at the top while the turn
/// scrolled on underneath it - offset 0 against a maximum of 3660 in this fixture.
/// </summary>
public class ChatTranscriptTests
{
    /// <summary>A conversation long enough to overflow the small window it is shown in.</summary>
    private static ChatEntry[] Conversation(int count)
        => Enumerable.Range(0, count)
            .Select(i => new ChatEntry(
                i % 2 == 0 ? "user" : "assistant",
                $"entry {i}\n{new string('x', 80)}"))
            .ToArray();

    /// <summary>Shows a transcript in a fixed-size window and waits for layout.</summary>
    private static void InWindow(double width, double height, Action<ChatTranscript> body)
    {
        var transcript = new ChatTranscript();
        var window = new Window { Width = width, Height = height, Content = transcript };
        window.Show();
        Settle();

        try
        {
            body(transcript);
        }
        finally
        {
            window.Close();
        }
    }

    private static ScrollViewer Scroller(ChatTranscript transcript)
        => transcript.GetVisualDescendants().OfType<ScrollViewer>().First();

    [AvaloniaFact]
    public void TheNewestEntryStaysVisibleAsTheConversationGrows()
    {
        InWindow(320, 180, transcript =>
        {
            ChatEntry[] entries = Conversation(40);
            transcript.Show(entries);
            Settle();

            // Guard the fixture: if it fits, "showing the latest" is true for free and
            // this test would pass without scrolling anything.
            ScrollViewer scroller = Scroller(transcript);
            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height,
                "the fixture must overflow, or this test proves nothing");
            Assert.True(transcript.IsShowingLatest, "the newest entry must be in view after the first fill");

            // The next step appends; the view must follow rather than stay at the top.
            transcript.Show([.. entries, new ChatEntry("assistant", "the final answer")]);
            Settle();
            Assert.True(transcript.IsShowingLatest, "the newest entry must be in view after an append");
        });
    }

    [AvaloniaFact]
    public void ATranscriptThatFitsReportsItselfAsFullyVisible()
    {
        InWindow(320, 400, transcript =>
        {
            transcript.Show([new ChatEntry("assistant", "short")]);
            Settle();

            Assert.Equal(1, transcript.EntryCount);
            Assert.True(transcript.IsShowingLatest, "a transcript that fits needs no scrolling");
        });
    }

    [AvaloniaFact]
    public void ResettingTheConversationEmptiesTheView()
    {
        InWindow(320, 240, transcript =>
        {
            transcript.Show(Conversation(6));
            Settle();
            Assert.Equal(6, transcript.EntryCount);

            // A reset is a clear, not an append: the old rows must go.
            transcript.Show([]);
            Settle();
            Assert.Equal(0, transcript.EntryCount);
            Assert.True(transcript.IsShowingLatest);
        });
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
