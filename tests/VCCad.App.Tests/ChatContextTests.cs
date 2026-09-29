using VCCad.App.Ai;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Pins context compression. It used to replace the middle of the conversation with
/// the literal string "(earlier steps omitted to stay within the context window)" —
/// deletion dressed up as compression. The region thrown away is where the reasoning
/// about the current job lives, so the model re-decided work it had already done and
/// forgot facts the person had given it.
///
/// These tests drive <see cref="ContextCompressor"/> directly: no model call is
/// involved in the decision, so the behaviour is deterministic.
/// </summary>
public class ChatContextTests
{
    private static LlmMessage User(string text) => new("user", text);

    private static LlmMessage Assistant(string text) => new("assistant", text);

    private static string Filler(char c, int length) => new(c, length);

    [Fact]
    public void TheBudgetComesFromTheEndpointWindowNotAFixedNumber()
    {
        // 48000 was a magic number that left most of a 94k-token window unused.
        LlmOptions options = new();
        int derived = (int)(LlmOptions.DefaultContextTokens * LlmOptions.HistoryShareOfContext * LlmOptions.CharsPerToken);

        Assert.Equal(derived, options.HistoryChars);
        Assert.NotEqual(48000, options.HistoryChars);

        // A different window gives a different budget...
        options.ContextTokens = 10_000;
        Assert.Equal(20_000, options.HistoryChars);

        // ...and an explicit budget still wins.
        options.HistoryChars = 1234;
        Assert.Equal(1234, options.HistoryChars);
    }

    [Fact]
    public void CompressedHistoryKeepsTheUsersOwnWords()
    {
        // The compressor floors the budget at MinBudgetChars, so the fixture has to be
        // bigger than that floor to cross it.
        var compressor = new ContextCompressor(ContextCompressor.MinBudgetChars);
        var history = new List<LlmMessage>
        {
            new("system", "SYSTEM PROMPT"),
            User("first task"),
            User("Remember this codeword exactly: BANANA-77"),
            Assistant(Filler('a', 1200)),
            User("what next?"),
            Assistant(Filler('b', 1200)),
        };

        IReadOnlyList<LlmMessage>? region = compressor.SelectRegion(history);

        Assert.NotNull(region);
        Assert.Contains(region!, m => (m.Text ?? string.Empty).Contains("BANANA-77", StringComparison.Ordinal));

        List<LlmMessage> rebuilt = compressor.Apply(history, ContextCompressor.Digest(region!));

        // The system prompt and the original task stay...
        Assert.Equal("system", rebuilt[0].Role);
        Assert.Equal("first task", rebuilt[1].Text);

        // ...the middle is replaced by a summary that still carries the codeword...
        Assert.Contains(rebuilt, ContextCompressor.IsSummaryMessage);
        Assert.Contains("BANANA-77", compressor.Summary, StringComparison.Ordinal);

        // ...and the deletion marker is gone for good.
        Assert.DoesNotContain(rebuilt, m => (m.Text ?? string.Empty).Contains(
            "earlier steps omitted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheSummaryAccumulatesAcrossRepeatedCompressions()
    {
        var compressor = new ContextCompressor(ContextCompressor.MinBudgetChars);
        var history = new List<LlmMessage>
        {
            new("system", "SYSTEM PROMPT"),
            User("lay out the label sheet"),
            User("decision ALPHA: keep the red layer"),
            Assistant(Filler('a', 1200)),
            User("carry on"),
            Assistant(Filler('b', 1200)),
        };

        IReadOnlyList<LlmMessage>? first = compressor.SelectRegion(history);
        Assert.NotNull(first);
        history = compressor.Apply(history, ContextCompressor.Digest(first!));
        Assert.Contains("ALPHA", compressor.Summary, StringComparison.Ordinal);

        // A later stretch of work goes over budget again.
        history.Add(User("decision BETA: impose four-up on A4"));
        history.Add(Assistant(Filler('c', 1200)));
        history.Add(User("now what?"));
        history.Add(Assistant(Filler('d', 1200)));

        IReadOnlyList<LlmMessage>? second = compressor.SelectRegion(history);
        Assert.NotNull(second);
        history = compressor.Apply(history, ContextCompressor.Digest(second!));

        // Both decisions are still there: the first compression was not undone, it
        // was folded into the second. That is the whole point of retention.
        Assert.Equal(2, compressor.CompressionCount);
        Assert.Contains("ALPHA", compressor.Summary, StringComparison.Ordinal);
        Assert.Contains("BETA", compressor.Summary, StringComparison.Ordinal);

        // The preserved summary did not lose the original task either.
        Assert.Equal("lay out the label sheet", history[1].Text);
        Assert.Equal("system", history[0].Role);
    }

    [Fact]
    public void CompressionOnlyCutsAtAUserBoundarySoToolResultsStayWithTheirRequest()
    {
        // The endpoint rejects a tool message whose requesting assistant message is
        // gone, so the cut point must be a user turn.
        var history = new List<LlmMessage>
        {
            new("system", "SYSTEM"),
            User("task"),
            User("second instruction"),
            new("assistant", string.Empty, ToolCalls: new[] { new LlmToolCall("1", "vccad_operation", "{\"op\":\"object.list\"}") }),
            new("tool", "{\"ok\":true}"),
            User("third instruction"),
        };

        Assert.True(ContextCompressor.TrySelectRegion(history, out int resume, out _, out _));
        Assert.Equal("third instruction", history[resume].Text);
        Assert.Equal("user", history[resume].Role);
    }

    [Fact]
    public void AFittingConversationIsLeftAlone()
    {
        var compressor = new ContextCompressor(100000);
        var history = new List<LlmMessage>
        {
            new("system", "SYSTEM"),
            User("task"),
            Assistant("done"),
            User("and now?"),
            Assistant("done too"),
        };

        Assert.Null(compressor.SelectRegion(history));
        Assert.Equal(0, compressor.CompressionCount);
        Assert.Empty(compressor.Summary);
    }

    [Fact]
    public void TheSummaryIsNeverDuplicatedWhenItSitsAtTheTaskPosition()
    {
        // If the first compression has no plain user task at index 1, the summary ends
        // up there. A later compression must replace it, not carry it forward twice.
        var compressor = new ContextCompressor(ContextCompressor.MinBudgetChars);
        var history = new List<LlmMessage>
        {
            new("system", "S"),
            Assistant("notes ALPHA " + Filler('x', 1500)),
            User("ask one"),
            Assistant(Filler('y', 1500)),
            User("ask two"),
            Assistant(Filler('z', 1500)),
        };

        IReadOnlyList<LlmMessage>? first = compressor.SelectRegion(history);
        Assert.NotNull(first);
        history = compressor.Apply(history, ContextCompressor.Digest(first!));
        Assert.True(ContextCompressor.IsSummaryMessage(history[1]));
        Assert.Contains("ALPHA", compressor.Summary, StringComparison.Ordinal);

        history.Add(User("ask three"));
        history.Add(Assistant(Filler('w', 1500)));

        IReadOnlyList<LlmMessage>? second = compressor.SelectRegion(history);
        Assert.NotNull(second);
        history = compressor.Apply(history, ContextCompressor.Digest(second!));

        Assert.Single(history, ContextCompressor.IsSummaryMessage);
        Assert.Contains("ALPHA", compressor.Summary, StringComparison.Ordinal);
        Assert.Contains("ask two", compressor.Summary, StringComparison.Ordinal);
        Assert.Equal("system", history[0].Role);
        Assert.Equal("ask three", history[^2].Text);
    }

    [Fact]
    public void TheDigestRecordsWhatWorkedWhatFailedAndWhatWasDecided()
    {
        IReadOnlyList<LlmMessage> region = new List<LlmMessage>
        {
            User("make the label Upper Cup"),
            new("assistant", string.Empty, ToolCalls: new[] { new LlmToolCall("1", "vccad_operation", "{\"op\":\"text.update\"}") }),
            new("tool", "{\"ok\":false,\"error\":\"no selection\"}", ToolCallId: "1"),
            Assistant("I could not find the label."),
        };

        string digest = ContextCompressor.Digest(region);

        Assert.Contains("text.update", digest, StringComparison.Ordinal);
        Assert.Contains("FAILED", digest, StringComparison.Ordinal);
        Assert.Contains("Upper Cup", digest, StringComparison.Ordinal);
        Assert.Contains("could not find", digest, StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryNeverGrowsWithoutBound()
    {
        var compressor = new ContextCompressor(ContextCompressor.MinBudgetChars);
        var history = new List<LlmMessage> { new("system", "SYSTEM"), User("task") };
        for (int round = 0; round < 12; round++)
        {
            history.Add(User($"decision {round}: {new string('x', 400)}"));
            history.Add(Assistant(new string('y', 700)));
            history.Add(User($"turn {round}?"));
            history.Add(Assistant(new string('z', 700)));

            IReadOnlyList<LlmMessage>? region = compressor.SelectRegion(history);
            if (region is null)
            {
                continue;
            }

            history = compressor.Apply(history, ContextCompressor.Digest(region));
        }

        Assert.True(compressor.CompressionCount >= 2, "the loop should have compressed repeatedly");
        Assert.True(compressor.Summary.Length <= ContextCompressor.MaxSummaryChars + 64,
            $"summary grew to {compressor.Summary.Length} characters");
    }

    [Fact]
    public void RebuildKeepsTheTwoAnchorsAndTheNewestWork()
    {
        var compressor = new ContextCompressor(ContextCompressor.MinBudgetChars);
        var history = new List<LlmMessage>
        {
            new("system", "SYSTEM PROMPT"),
            User("original task"),
            User("middle " + Filler('m', 1200)),
            Assistant(Filler('n', 1200)),
            User("newest request"),
        };

        IReadOnlyList<LlmMessage>? region = compressor.SelectRegion(history);
        Assert.NotNull(region);
        List<LlmMessage> rebuilt = compressor.Apply(history, ContextCompressor.Digest(region!));

        Assert.Equal("SYSTEM PROMPT", rebuilt[0].Text);
        Assert.Equal("original task", rebuilt[1].Text);
        Assert.True(ContextCompressor.IsSummaryMessage(rebuilt[2]));
        Assert.Equal("newest request", rebuilt[^1].Text);
    }
}
