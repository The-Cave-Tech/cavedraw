using VCCad.Core.Model;

namespace VCCad.Api.Services;

/// <summary>
/// A document plus the undo stack attached to the session that opened it.
/// Every automation session owns its own stacks (see project plan M3), so two
/// scripted clients can work on copies without corrupting each other's history.
/// </summary>
public sealed class DocumentSession
{
    /// <summary>The live document. RPC mutations act on this instance.</summary>
    public required CadDocument Document { get; init; }

    /// <summary>Undo history for edits performed through the current session.</summary>
    public required VCCad.Core.Commands.CommandStack Stack { get; init; }
}

/// <summary>
/// Stores live documents by id for the lifetime of the process. This is an
/// in-memory seed implementation; the persistence layer (PDF + sidecar files on
/// disk / object storage) is scheduled under M4.
/// </summary>
public interface IDocumentStore
{
    /// <summary>Lists all stored documents as lightweight summaries.</summary>
    IReadOnlyList<CadDocument> List();

    /// <summary>Gets a stored document or null when it does not exist.</summary>
    CadDocument? Find(Guid id);

    /// <summary>Registers a new document (the caller owns the undo stack too).</summary>
    void Add(DocumentSession session);

    /// <summary>Removes and returns the removed document, or null.</summary>
    CadDocument? Remove(Guid id);
}

/// <summary>In-memory <see cref="IDocumentStore"/>, guarded by a lock (web host).</summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly Dictionary<Guid, DocumentSession> _documents = new();
    private readonly object _gate = new();

    public IReadOnlyList<CadDocument> List()
    {
        lock (_gate)
        {
            return _documents.Values.Select(d => d.Document).ToArray();
        }
    }

    public CadDocument? Find(Guid id)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(id, out DocumentSession? session) ? session.Document : null;
        }
    }

    public void Add(DocumentSession session)
    {
        lock (_gate)
        {
            _documents[session.Document.Id] = session;
        }
    }

    public CadDocument? Remove(Guid id)
    {
        lock (_gate)
        {
            if (_documents.Remove(id, out DocumentSession? session))
            {
                return session.Document;
            }

            return null;
        }
    }
}
