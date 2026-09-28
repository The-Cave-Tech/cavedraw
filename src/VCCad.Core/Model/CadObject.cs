using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VCCad.Core.Model;

/// <summary>
/// Base class for every node in the document object graph.
///
/// Responsibilities:
/// <list type="bullet">
/// <item>A stable <see cref="Id"/> that survives serialisation so the API, the UI and
/// the test suite can address objects without relying on positional indices.</item>
/// <item>A <c>Name</c> for panels and asset export.</item>
/// <item><see cref="INotifyPropertyChanged"/> so both the Avalonia docking UI and the
/// change bus (M2/M3) observe edits through one standard mechanism.</item>
/// <item>A back-pointer to the containing <see cref="IItemContainer"/> used by
/// commands that must locate or remove an object (remove-command inverse data,
/// hit testing, layer promotion).</item>
/// </list>
/// </summary>
public abstract class CadObject : INotifyPropertyChanged
{
    /// <summary>
    /// Identity, generated on construction and preserved by cloning and by the
    /// lossless serializer. The setter is private; the serializer restores a
    /// persisted id through <see cref="RestoreIdentity"/>.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Restores a persisted identity (used only during deserialization
    /// so that API references survive a save/load round-trip).</summary>
    internal void RestoreIdentity(Guid id) => Id = id;

    private string _name = string.Empty;

    /// <summary>Human-readable label shown in the Layers panel and tooltips.</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private bool _nameIsUserSet;

    /// <summary>
    /// Whether a person gave this object its name.
    ///
    /// A name that was typed is theirs and lasts. A name that was not is a guess at what the
    /// thing is, and is recomputed as the geometry changes — a path that began as a line and
    /// was dragged into a curve should read "Curve", but one somebody called "Left sleeve"
    /// should keep saying so.
    /// </summary>
    public bool NameIsUserSet
    {
        get => _nameIsUserSet;
        set => SetField(ref _nameIsUserSet, value);
    }

    /// <summary>
    /// The object that directly contains this one (a <see cref="Layer"/> or an
    /// <see cref="ArtGroup"/>), or <c>null</c> when detached. Maintained by the
    /// container's Add/Remove methods — see <see cref="IItemContainer"/>.
    /// </summary>
    public IItemContainer? Container { get; internal set; }

    /// <summary>Raised when a simple property on this object changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Sets a backing field and raises <see cref="PropertyChanged"/> when the value
    /// actually differs. Returns true when a change was applied.
    /// </summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>Helper for geometry-mutating code that changes many fields at once.</summary>
    protected void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
