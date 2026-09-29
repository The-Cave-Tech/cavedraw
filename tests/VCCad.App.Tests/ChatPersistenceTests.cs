using VCCad.App.Ai;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Pins conversation continuity. The transcript used to be a plain in-memory list, so
/// every restart threw the conversation away even though the diary still held all of
/// it, session-tagged, for ninety days. Restoring it is what makes "this conversation
/// continues an earlier session" true rather than aspirational.
/// </summary>
public class ChatPersistenceTests : IDisposable
{
    private readonly string _root;

    public ChatPersistenceTests()
        => _root = Path.Combine(Path.GetTempPath(), "vccad-chat-" + Guid.NewGuid().ToString("N")[..10]);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private string NewDiaryDirectory() => Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);

    private static InteractionRecord Speak(InteractionLog diary, string name, string text)
        => diary.Record(InteractionKind.Llm, InteractionCategory.Note, name, details: text, tags: new[] { "chat" });

    [Fact]
    public void TheTranscriptIsRestoredFromTheDiaryOfAnEarlierProcess()
    {
        string directory = NewDiaryDirectory();

        // One run of the editor: the person says something, the assistant answers.
        var first = new InteractionLog(directory);
        first.StartSession("run one");
        Speak(first, EditorAgent.UserEntryName, "Remember this codeword exactly: BANANA-77");
        Speak(first, EditorAgent.AssistantEntryName, "Noted: BANANA-77.");

        // The process exits. The next run opens the same diary.
        var second = new InteractionLog(directory);
        second.StartSession("run two");

        IReadOnlyList<ChatEntry> restored = EditorAgent.RestoreTranscript(second);

        Assert.Equal(2, restored.Count);
        Assert.Equal("user", restored[0].Role);
        Assert.Contains("BANANA-77", restored[0].Text, StringComparison.Ordinal);
        Assert.Equal("assistant", restored[1].Role);
        Assert.Contains("Noted", restored[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewAgentStartsWithTheConversationFromTheLastRun()
    {
        string directory = NewDiaryDirectory();
        var earlier = new InteractionLog(directory);
        Speak(earlier, EditorAgent.UserEntryName, "the client wants an A4 imposition");
        Speak(earlier, EditorAgent.AssistantEntryName, "Understood — four-up on A4.");

        var diary = new InteractionLog(directory);
        var context = new AutomationContext { ViewModel = new EditorViewModel(), History = diary };
        var agent = new EditorAgent(context, new VllmClient(new LlmOptions { ApiKey = "test" }), diary);

        Assert.Contains(agent.Transcript, e => e.Role == "user" && e.Text.Contains("A4 imposition", StringComparison.Ordinal));
        Assert.Contains(agent.Transcript, e => e.Role == "assistant" && e.Text.Contains("four-up", StringComparison.Ordinal));

        // The Assistant tab reads as a conversation, so the restored turns are
        // visible; an explanatory line marks where the earlier session begins.
        Assert.Contains(agent.Transcript, e => e.Role == "action" && e.Text.Contains("restored", StringComparison.Ordinal));
    }

    [Fact]
    public void AResetStopsRestorationAtTheMarker()
    {
        string directory = NewDiaryDirectory();
        var diary = new InteractionLog(directory);
        Speak(diary, EditorAgent.UserEntryName, "old conversation");
        Speak(diary, EditorAgent.AssistantEntryName, "old answer");
        diary.Record(InteractionKind.Llm, InteractionCategory.Note, EditorAgent.ResetEntryName, details: "cleared");
        Speak(diary, EditorAgent.UserEntryName, "after the reset");

        IReadOnlyList<ChatEntry> restored = EditorAgent.RestoreTranscript(diary);

        ChatEntry only = Assert.Single(restored);
        Assert.Equal("after the reset", only.Text);
    }

    [Fact]
    public void ClearingTheConversationIsRecordedSoItStaysClearedAcrossARestart()
    {
        string directory = NewDiaryDirectory();
        var diary = new InteractionLog(directory);
        Speak(diary, EditorAgent.UserEntryName, "something said earlier");

        var context = new AutomationContext { ViewModel = new EditorViewModel(), History = diary };
        var agent = new EditorAgent(context, new VllmClient(new LlmOptions { ApiKey = "test" }), diary);

        Assert.NotEmpty(agent.Transcript);
        agent.Reset();

        Assert.Empty(agent.Transcript);
        Assert.Contains(diary.Tail(50), r => r.Name == EditorAgent.ResetEntryName);

        // A later run must not resurrect what was cleared.
        var next = new EditorAgent(
            new AutomationContext { ViewModel = new EditorViewModel(), History = diary },
            new VllmClient(new LlmOptions { ApiKey = "test" }),
            diary);
        Assert.Empty(next.Transcript);
    }

    [Fact]
    public void AnAgentWithoutADiaryStillStarts()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        var agent = new EditorAgent(context, new VllmClient(new LlmOptions { ApiKey = "test" }));

        Assert.Empty(agent.Transcript);
        Assert.Empty(agent.CompressedSummary);
    }
}
