using System.Text.Json;
using VCCad.App.Automation;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The application diary: the searchable record of everything that happens, and the
/// store that lets the assistant reuse work it has already done.
/// </summary>
public sealed class InteractionLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "vccad-diary-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test artefact left behind is not worth failing a run over.
        }
    }

    [Fact]
    public void RecordsUiApiAndModelWorkInOneStream()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession("test");

        diary.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.press", "Button#Run \"Run\"");
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "object.create", details: "{\"type\":\"ellipse\"}");
        diary.Record(InteractionKind.Llm, InteractionCategory.Model, "llm.chat", details: "draw a circle");
        diary.Record(InteractionKind.Ui, InteractionCategory.Key, "key.down", details: "Ctrl+S", success: false);

        IReadOnlyList<InteractionRecord> tail = diary.Tail(10);

        Assert.Equal(5, tail.Count); // session.start + four entries
        Assert.Equal(InteractionKind.Ui, tail[1].Kind);
        Assert.Equal(InteractionKind.Api, tail[2].Kind);
        Assert.Equal(InteractionKind.Llm, tail[3].Kind);
        Assert.False(tail[4].Success);
        Assert.All(tail, r => Assert.Equal(diary.SessionId, r.SessionId));
        Assert.All(tail, r => Assert.NotEqual(default, r.TimestampUtc));
    }

    [Fact]
    public void EntriesAreDurableAcrossApplicationRuns()
    {
        var first = new InteractionLog(_directory);
        first.StartSession("run 1");
        first.Record(InteractionKind.Api, InteractionCategory.Operation, "object.create", details: "{\"n\":1}");

        // A second instance stands in for the next launch of the editor.
        var second = new InteractionLog(_directory);
        second.StartSession("run 2");

        IReadOnlyList<InteractionRecord> all = second.All().ToArray();
        Assert.Contains(all, r => r.Name == "object.create");
        Assert.Contains(all, r => r.Name == "session.start" && r.Details == "run 1");
        Assert.Equal(2, second.Sessions().Count);
    }

    [Fact]
    public void SearchFindsAPastSkillFromDifferentWording()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession();
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "document.new");
        diary.Learn("US size 10 bodice block",
            "Draft the bodice front block: outer rectangle, then the piece label.");

        // The words of the query are not the words of the title.
        IReadOnlyList<InteractionRecord> matches = diary.Search("drafting a bodice pattern block");

        Assert.NotEmpty(matches);
        Assert.Equal(InteractionKind.Skill, matches[0].Kind);
        Assert.Equal("US size 10 bodice block", matches[0].Target);
    }

    [Fact]
    public void LearningRecordsTheStepsThatAchievedTheTask()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession();
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "artboard.add", details: "{\"width\":595}");
        diary.Record(InteractionKind.Llm, InteractionCategory.Model, "llm.chat", details: "thinking");

        InteractionRecord skill = diary.Learn("A4 artboard workflow", "How to start a pattern sheet.");

        Assert.NotNull(skill.Details);
        Assert.Contains("artboard.add", skill.Details!, StringComparison.Ordinal);
        Assert.Contains("595", skill.Details!, StringComparison.Ordinal);

        // The model request is context, not a reusable step, and must not be listed.
        Assert.DoesNotContain("llm.chat", skill.Details!, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillsAreRetrievableOnTheirOwn()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession();
        diary.Learn("Size 12 sleeve block", "Draft a sleeve block for size 12.");
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "object.create", details: "unrelated");

        IReadOnlyList<InteractionRecord> skills = diary.Skills();

        Assert.Single(skills);
        Assert.Equal("Size 12 sleeve block", skills[0].Target);
        Assert.Single(diary.Skills("sleeve"));
        Assert.Empty(diary.Skills("nothing-matches-this"));
    }

    [Fact]
    public void NotesFreezeDecisionsIntoTheDiary()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession();

        InteractionRecord note = diary.Note("Chose PDF/A-2b because veraPDF signs it off.", target: "export");

        Assert.Equal(InteractionCategory.Note, note.Category);
        Assert.Contains(diary.Search("veraPDF"), r => r.Sequence == note.Sequence);
    }

    [Fact]
    public void ExportWritesTheWholeHistory()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession();
        diary.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.press", "Canvas");
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "view.fit");

        string path = Path.Combine(_directory, "export.jsonl");
        int written = diary.Export(path);

        Assert.Equal(3, written);
        Assert.Equal(3, File.ReadAllLines(path).Length);
        Assert.Contains(File.ReadAllLines(path), line => line.Contains("pointer.press", StringComparison.Ordinal));
    }

    [Fact]
    public void StatsReportWhatIsInTheDiary()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession();
        diary.Record(InteractionKind.Ui, InteractionCategory.Pointer, "pointer.press");
        diary.Record(InteractionKind.Ui, InteractionCategory.Key, "key.down");
        diary.Learn("Something reusable");

        string json = JsonSerializer.Serialize(diary.Stats());

        Assert.Contains("\"skills\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"Ui\":2", json, StringComparison.Ordinal);
        Assert.Contains(_directory.Replace("\\", "\\\\"), json, StringComparison.Ordinal);
    }

    [Fact]
    public void RetrievalIgnoresStopWordsAndSingleLetters()
    {
        string[] terms = InteractionLog.Terms("Can you please draft the a US size 10 bodice for me?");

        Assert.Contains("draft", terms);
        Assert.Contains("bodice", terms);
        Assert.Contains("size", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("please", terms);
        Assert.DoesNotContain("a", terms);
    }

    [Fact]
    public void SessionFilteringSeparatesRuns()
    {
        var diary = new InteractionLog(_directory);
        diary.StartSession("run 1");
        string first = diary.SessionId;
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "object.create");

        diary.StartSession("run 2");
        string second = diary.SessionId;
        diary.Record(InteractionKind.Api, InteractionCategory.Operation, "view.fit");

        Assert.All(diary.Session(first), r => Assert.Equal(first, r.SessionId));
        Assert.Contains(diary.Session(second), r => r.Name == "view.fit");
        Assert.DoesNotContain(diary.Session(second), r => r.Name == "object.create");
    }

    [Fact]
    public void DiaryOperationsArePartOfTheSurface()
    {
        // The diary is only useful if both the person and the model can reach it, so
        // it must be in the same registry as everything else.
        foreach (string op in new[]
                 {
                     "history.stats", "history.tail", "history.search", "history.sessions",
                     "history.session", "history.skills", "history.learn", "history.note", "history.export",
                 })
        {
            Assert.True(EditorOperations.TryGet(op, out EditorOperation found), $"{op} is not registered");
            Assert.NotNull(found.Handler);
        }
    }
}
