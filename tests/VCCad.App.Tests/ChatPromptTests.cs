using VCCad.App.Ai;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Pins the system prompt. The prompt is the assistant's behaviour, not decoration:
/// the model used to be handed purely operational advice and no statement of what it
/// was for, so it waited to be told that pagination or text editing were in scope.
///
/// These are content assertions on purpose. If the scope paragraph is dropped in a
/// later edit, the failure should say which promise was lost.
/// </summary>
public class ChatPromptTests
{
    private static readonly string Prompt = EditorAgent.BuildSystemPrompt();

    private static void Mentions(string phrase)
        => Assert.Contains(phrase, Prompt, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ThePromptSaysWhatTheAssistantIs()
    {
        Mentions("automation layer");
        Mentions("vector graphics");
        Mentions("VCCad");
        Mentions("whole application");
        Mentions("Anything a person can do");
    }

    [Fact]
    public void ThePromptStatesTheWholeApplicationScopeIncludingPaginationAndText()
    {
        // The user named these two specifically. A model that is not told they are in
        // scope does not reach for them.
        Mentions("pagination");
        Mentions("imposition");
        Mentions("text editing");
        Mentions("typography");
        Mentions("layers");
        Mentions("import");
        Mentions("export");
        Mentions("colour");
        Mentions("artboards");
    }

    [Fact]
    public void ThePromptTellsTheModelToActRatherThanReciteCapabilities()
    {
        Mentions("call operations and make it happen");
        Mentions("do not ask the person to restate");
        Mentions("ambiguous");
        Mentions("Never invent an operation");
        Mentions("catalog");
    }

    [Fact]
    public void ThePromptLeadsWithIdentityAndScopeBeforeOperationalDetail()
    {
        int identity = Prompt.IndexOf("WHO YOU ARE", StringComparison.Ordinal);
        int scope = Prompt.IndexOf("WHAT YOU ARE FOR", StringComparison.Ordinal);
        int method = Prompt.IndexOf("HOW YOU WORK", StringComparison.Ordinal);
        int rules = Prompt.IndexOf("Operational rules learned from real failures", StringComparison.Ordinal);

        Assert.True(identity >= 0 && scope > identity && method > scope && rules > method,
            "the prompt must run identity → scope → method → operational rules");
    }

    [Fact]
    public void TheHardWonOperationalRulesSurvive()
    {
        // Learned from real failures; losing any of these is a regression even though
        // the prompt was rewritten around them.
        Mentions("UPPER CUP");
        Mentions("UpperCup");
        Mentions("object.list pages");
        Mentions("object.find");
        Mentions("do not repeat a failing call");
        Mentions("text.centerIn");
        Mentions("capture.screenshot");
        Mentions("selection.set");
        Mentions("history.learn");
        Mentions("history.search");
        Mentions("Worked example");
    }
}
