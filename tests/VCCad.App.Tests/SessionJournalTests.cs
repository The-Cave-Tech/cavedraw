using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Which operations are worth journaling for crash recovery.
///
/// Only edits to the document belong in the command queue. The first version of this was
/// a deny-list of reads, so every operation nobody had thought about counted as an edit —
/// including <c>view.fit</c>, which the application runs on every launch. It therefore
/// wrote a journal every time, and the recovery prompt reappeared every time the app was
/// opened. The person dismissed it, used the app, and found it waiting again.
///
/// A name in the read-only list is a claim that the operation changes nothing, and a wrong
/// claim is worse than a missing one: the queue then records the edits around a mutation
/// and skips the mutation itself, so replaying the queue produces a different document. The
/// assertions below are therefore made against the journal's own record — the queue it
/// wrote after a real registry call — rather than against the classification in isolation.
/// </summary>
public class SessionJournalTests
{
    [Theory]
    [InlineData("view.fit")]
    [InlineData("view.zoom")]
    [InlineData("view.centerOn")]
    [InlineData("view.toScreen")]
    [InlineData("tool.set")]
    [InlineData("selection.clear")]
    [InlineData("selection.set")]
    [InlineData("ui.find")]
    [InlineData("ui.click")]
    [InlineData("ui.dump")]
    [InlineData("document.list")]
    [InlineData("document.summary")]
    [InlineData("document.dump")]
    [InlineData("document.verifyRoundTrip")]
    [InlineData("object.find")]
    [InlineData("object.list")]
    [InlineData("text.runs")]
    [InlineData("text.caret")]
    [InlineData("pane.list")]
    [InlineData("fonts.list")]
    [InlineData("history.search")]
    [InlineData("app.operations")]
    [InlineData("image.exportPng")]
    [InlineData("filter.list")]
    [InlineData("filter.kinds")]
    [InlineData("filter.read")]
    public void ReadingIsNotAnEdit(string name)
    {
        Assert.False(SessionJournal.IsMutation(name), $"{name} must not be journaled");
    }

    [Theory]
    [InlineData("object.create")]
    [InlineData("object.delete")]
    [InlineData("object.transform")]
    [InlineData("object.arrange")]
    [InlineData("image.insert")]
    [InlineData("text.update")]
    [InlineData("text.style")]
    [InlineData("artboard.setBounds")]
    [InlineData("layer.add")]
    [InlineData("document.new")]
    [InlineData("document.importPdf")]
    [InlineData("filter.create")]
    [InlineData("filter.setRegion")]
    [InlineData("filter.delete")]
    [InlineData("filter.addPrimitive")]
    [InlineData("filter.removePrimitive")]
    [InlineData("filter.connectPrimitive")]
    [InlineData("filter.setPrimitiveParameter")]
    [InlineData("filter.apply")]
    public void EditingIsAnEdit(string name)
    {
        Assert.True(SessionJournal.IsMutation(name), $"{name} must be journaled");
    }

    [Fact]
    public void AnOperationNobodyHasHeardOfIsNotAnEdit()
    {
        // The allow-list exists so an unclassified operation is treated as harmless
        // rather than as an edit. Guessing the other way is what caused the prompt to
        // reappear on every launch.
        Assert.False(SessionJournal.IsMutation("something.new.entirely"));
        Assert.False(SessionJournal.IsMutation(""));
    }

    /// <summary>
    /// `filter.apply` attaches a filter to `LayerItem.FilterId`, which is document state
    /// like any other appearance edit. It was listed as read-only, so the queue recorded
    /// the filter being created and then said nothing about the item that started drawing
    /// through it — a replay from that queue loses the edit, and the diary, which is a
    /// product feature, reports that nothing happened.
    /// </summary>
    [Fact]
    public void ApplyingAFilterLeavesAJournalEntryThatSaysTheDocumentChanged()
    {
        using var journal = JournalGuard.Capture();
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "filter.create", Parameters(FilterGraph()));

        int before = SessionJournal.ReadQueue().Count;
        EditorOperations.Invoke(context, "filter.apply", Parameters(new { name = "journal-probe" }));

        Assert.Equal("journal-probe", path.FilterId);

        IReadOnlyList<string> after = SessionJournal.ReadQueue();
        Assert.True(after.Count > before, "filter.apply changed the document but wrote nothing to the journal");

        // The queue is one file in the person's profile, so a second process running these
        // tests appends to it between the two reads — which is exactly what happened while
        // this was being written. What is asserted is therefore the entry this call wrote,
        // not whatever happens to be last.
        Assert.Contains(
            after.Skip(before),
            line => line.Contains("\"op\":\"filter.apply\"", StringComparison.Ordinal));
    }

    /// <summary>The counterpart: a filter operation that only reads must not grow the queue.</summary>
    [Fact]
    public void ReadingTheFilterLibraryLeavesNoJournalEntry()
    {
        using var journal = JournalGuard.Capture();
        (AutomationContext context, _) = Host();
        EditorOperations.Invoke(context, "filter.create", Parameters(FilterGraph()));

        int before = SessionJournal.ReadQueue().Count;
        EditorOperations.Invoke(context, "filter.list", default);
        EditorOperations.Invoke(context, "filter.kinds", default);
        EditorOperations.Invoke(context, "filter.read", default);

        IEnumerable<string> appended = SessionJournal.ReadQueue().Skip(before);
        foreach (string op in new[] { "filter.list", "filter.kinds", "filter.read" })
        {
            Assert.DoesNotContain(
                appended,
                line => line.Contains($"\"op\":\"{op}\"", StringComparison.Ordinal));
        }
    }

    private static JsonElement Parameters(object value) => JsonSerializer.SerializeToElement(value);

    private static object FilterGraph() => new
    {
        name = "journal-probe",
        primitives = new object[] { new { kind = "gaussianBlur", @in = "SourceAlpha", radius = 3 } },
    };

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    /// <summary>
    /// The journal lives at a fixed path in the person's own profile with no injectable
    /// location, so a test that exercises the real record puts back what it found. The file
    /// it writes is the one the next launch offers to recover from, and the suite is not
    /// entitled to spend a developer's own recovery journal to prove a point.
    /// </summary>
    private sealed class JournalGuard : IDisposable
    {
        private readonly string _directory;
        private readonly byte[]? _journal;
        private readonly byte[]? _snapshot;

        private JournalGuard(string directory, byte[]? journal, byte[]? snapshot)
        {
            _directory = directory;
            _journal = journal;
            _snapshot = snapshot;
        }

        public static JournalGuard Capture()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VCCad", "recovery");
            string journal = Path.Combine(directory, "journal.ndjson");
            string snapshot = Path.Combine(directory, "snapshot.json");
            return new JournalGuard(
                directory,
                File.Exists(journal) ? File.ReadAllBytes(journal) : null,
                File.Exists(snapshot) ? File.ReadAllBytes(snapshot) : null);
        }

        public void Dispose()
        {
            Restore(Path.Combine(_directory, "journal.ndjson"), _journal);
            Restore(Path.Combine(_directory, "snapshot.json"), _snapshot);
        }

        private static void Restore(string path, byte[]? contents)
        {
            if (contents is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            File.WriteAllBytes(path, contents);
        }
    }
}
