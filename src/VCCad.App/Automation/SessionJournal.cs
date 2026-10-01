using System.Text;
using System.Text.Json;
using VCCad.Core.Model;
using VCCad.Core.Serialization;

namespace VCCad.App.Automation;

/// <summary>
/// The command queue, kept on disk so a crash does not lose the session.
///
/// Every mutating operation is recorded as it happens — its name and its parameters —
/// together with a snapshot of the document. That pairing is deliberate: the snapshot is
/// what gets restored, and the queue is what says how the document got there, which is
/// what a person wants to see when they are asked whether to recover.
///
/// The queue is also the reason this is replayable at all. Every edit in this application
/// goes through one operation registry, so a list of operation calls is a complete
/// description of what happened rather than a partial one.
///
/// A clean exit removes the journal. Its presence at startup therefore means the last run
/// ended badly, and that is exactly when recovery should be offered.
/// </summary>
public static class SessionJournal
{
    /// <summary>
    /// The operation families that change the document, and so belong in the journal.
    ///
    /// This is an allow-list on purpose. The first version was a deny-list of reads, which
    /// meant every operation nobody had thought about — <c>view.fit</c> among them — was
    /// treated as an edit. The application fits the view on every launch, so it wrote a
    /// journal every time, and the recovery prompt therefore reappeared every time: the
    /// person dismissed it, used the app, and found it waiting again next morning. Naming
    /// what counts is the only way that cannot happen.
    /// </summary>
    private static readonly string[] MutatingPrefixes =
    {
        "object.", "artboard.", "layer.", "image.insert", "text.update", "text.style",
        "document.new", "document.importPdf", "document.openFromServer", "gradient.", "filter.",
    };

    /// <summary>Members of those families that only read.</summary>
    private static readonly string[] ReadOnlyMembers =
    {
        "object.list", "object.find", "object.get", "artboard.list", "layer.list",
        "image.exportPng", "gradient.get", "gradient.sample",
        "filter.list", "filter.kinds", "filter.read", "filter.apply",
    };

    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VCCad",
        "recovery");

    private static readonly string JournalPath = Path.Combine(Directory, "journal.ndjson");
    private static readonly string SnapshotPath = Path.Combine(Directory, "snapshot.json");

    private static readonly object Gate = new();
    private static DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;

    /// <summary>True when a previous run left work behind.</summary>
    public static bool HasRecoverableSession => File.Exists(SnapshotPath);

    /// <summary>Whether an operation changes the document and so belongs in the journal.</summary>
    public static bool IsMutation(string name)
    {
        foreach (string read in ReadOnlyMembers)
        {
            if (name.Equals(read, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (string prefix in MutatingPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records one command. The document snapshot is rewritten at most once a second, so
    /// a drag does not write the whole model on every pointer move.
    /// </summary>
    public static void Record(string name, string parametersJson, CadDocument document)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);

                string line = JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow.ToString("O"),
                    op = name,
                    parameters = parametersJson,
                });

                File.AppendAllText(JournalPath, line + "\n", Encoding.UTF8);

                // A long session would otherwise grow the queue without bound. The
                // snapshot is what gets restored, so the queue only has to describe the
                // recent past; keeping the last few thousand commands is plenty for the
                // person deciding whether to recover.
                var journal = new FileInfo(JournalPath);
                if (journal.Exists && journal.Length > 2 * 1024 * 1024)
                {
                    string[] lines = File.ReadAllLines(JournalPath);
                    File.WriteAllLines(JournalPath, lines.TakeLast(2000), Encoding.UTF8);
                }

                if (DateTimeOffset.UtcNow - _lastSnapshot < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                _lastSnapshot = DateTimeOffset.UtcNow;
                WriteSnapshot(document);
            }
        }
        catch (Exception)
        {
            // Recording must never be the reason an edit fails.
        }
    }

    /// <summary>Writes the document being edited, so a crash has something to restore.</summary>
    public static void WriteSnapshot(CadDocument document)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(SnapshotPath, VccadDocumentSerializer.SerializeToBytes(document));
            }
        }
        catch (Exception)
        {
            // As above: a failed autosave must not surface as a failed edit.
        }
    }

    /// <summary>The journal so far, oldest first.</summary>
    public static IReadOnlyList<string> ReadQueue()
    {
        try
        {
            return File.Exists(JournalPath)
                ? File.ReadAllLines(JournalPath).Where(l => l.Length > 0).ToList()
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The document left behind by the interrupted run, or null.</summary>
    public static CadDocument? TryRecover()
    {
        try
        {
            return File.Exists(SnapshotPath)
                ? VccadDocumentSerializer.Deserialize(File.ReadAllBytes(SnapshotPath))
                : null;
        }
        catch (Exception)
        {
            // A snapshot written by a crashing process can be truncated; treat it as
            // nothing rather than failing the launch.
            return null;
        }
    }

    /// <summary>Clears the journal; called on a clean exit.</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(JournalPath))
                {
                    File.Delete(JournalPath);
                }

                if (File.Exists(SnapshotPath))
                {
                    File.Delete(SnapshotPath);
                }
            }
        }
        catch (Exception)
        {
            // Nothing useful to do; the next launch will simply offer recovery again.
        }
    }
}
