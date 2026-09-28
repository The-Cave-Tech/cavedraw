using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VCCad.App.Views.Panes;

/// <summary>
/// A node in the object browser. The tree is data-bound (not built from controls)
/// so Avalonia can virtualise it — essential for documents with thousands of
/// objects. Visibility toggling writes straight through to the model.
/// </summary>
public sealed class ObjectNode : INotifyPropertyChanged
{
    private static readonly Lazy<Bitmap> EyeOn = new(() => Load("eye"));
    private static readonly Lazy<Bitmap> EyeOff = new(() => Load("eye-off"));

    private bool _isVisible;
    private bool _isExpanded;
    private readonly Action<bool>? _visibilitySetter;

    public ObjectNode(string name, object? tag, bool isVisible, bool isExpanded, Action<bool>? visibilitySetter)
    {
        Name = name;
        Tag = tag;
        _isVisible = isVisible;
        _isExpanded = isExpanded;
        _visibilitySetter = visibilitySetter;
    }

    public string Name { get; }

    public object? Tag { get; }

    /// <summary>
    /// How deep the row sits, which is what the vertical rules at its left are drawn from.
    /// The other rows are fixed at startup so the guides are a plain list to bind.
    /// </summary>
    public int Depth { get; private set; }

    /// <summary>One entry per level, so the template draws that many vertical rules.</summary>
    public IReadOnlyList<int> DepthGuides { get; private set; } = Array.Empty<int>();

    /// <summary>
    /// The object's own shape, scaled to its box, or null when there is nothing to draw.
    /// Set by the panel, which knows the size; a geometry rather than a picture so a tree of
    /// thousands of rows does not allocate thousands of bitmaps.
    /// </summary>
    public Avalonia.Media.Geometry? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Raise(nameof(Thumbnail));
            Raise(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    private Avalonia.Media.Geometry? _thumbnail;

    /// <summary>Sets the row's depth and the rules that show it.</summary>
    public void SetDepth(int depth)
    {
        Depth = depth;
        DepthGuides = Enumerable.Range(0, depth).ToArray();
        Raise(nameof(Depth));
        Raise(nameof(DepthGuides));
    }

    public ObservableCollection<ObjectNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            _visibilitySetter?.Invoke(value);
            Raise(nameof(IsVisible));
            Raise(nameof(EyeSource));
            Raise(nameof(LabelOpacity));
        }
    }

    /// <summary>Right-justified eye / eye-off icon (shared bitmaps).</summary>
    public Bitmap EyeSource => _isVisible ? EyeOn.Value : EyeOff.Value;

    /// <summary>Dim the label when hidden.</summary>
    public double LabelOpacity => _isVisible ? 1.0 : 0.5;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static Bitmap Load(string name)
        => new(AssetLoader.Open(new Uri($"avares://VCCad.App/Assets/Icons/{name}.png")));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(property);
    }

    private void Raise(string? property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
