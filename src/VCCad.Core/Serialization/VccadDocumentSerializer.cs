using System.Text.Json;
using System.Text.Json.Serialization;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Serialization;

// ---------------------------------------------------------------------------
// DTOs — the lossless wire format of the VCCad document model.
//
// These records mirror the object graph 1:1 and carry a version tag. Keeping a
// dedicated DTO layer (rather than decorating the model with serialisation
// attributes) gives us three things:
//   1. A stable, versioned on-disk contract that survives refactors of the live
//      model classes.
//   2. Full control over numeric formatting and null handling so two identical
//      documents always serialise to identical bytes (determinism the round-trip
//      tests depend on).
//   3. No attributes/polymorphism metadata leaking into the pure model.
// ---------------------------------------------------------------------------

internal sealed record ColorDto(double R, double G, double B, double A = 1.0);

internal sealed record FillDto(bool Visible, ColorDto? Color, FillRule Rule);

internal sealed record StrokeDto(bool Visible, ColorDto? Color, double Width, StrokeCap Cap, StrokeJoin Join, double MiterLimit, StrokeAlignment Alignment);

internal sealed record NodeDto(Point2D Anchor, Point2D InHandle, Point2D OutHandle);

internal sealed record SubPathDto(bool Closed, NodeDto[] Nodes);

internal sealed record TextRunDto(string Text, string FontFamily, double FontSize, bool Bold, bool Italic);

internal sealed record PathDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    FillDto Fill,
    StrokeDto Stroke,
    double Opacity,
    SubPathDto[] SubPaths) : ItemDto;

internal sealed record GroupDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    AffineTransform Transform,
    double Opacity,
    ItemDto[] Children) : ItemDto;

internal sealed record TextDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    Point2D Origin,
    ColorDto Color,
    double RotationRadians,
    TextRunDto[] Runs) : ItemDto;

/// <summary>
/// Discriminated union over the possible layer items. System.Text.Json picks the
/// concrete type from the <c>$kind</c> property written by the converter below.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PathDto), "path")]
[JsonDerivedType(typeof(GroupDto), "group")]
[JsonDerivedType(typeof(TextDto), "text")]
internal abstract record ItemDto
{
    public static ItemDto From(LayerItem item) => item switch
    {
        PathItem path => ToPath(path),
        ArtGroup group => ToGroup(group),
        TextItem text => ToText(text),
        _ => throw new NotSupportedException($"Unsupported layer item type {item.GetType().Name}."),
    };

    private static TextDto ToText(TextItem t) => new(
        t.Id,
        t.Name,
        t.IsVisible,
        t.IsLocked,
        t.Origin,
        new ColorDto(t.Color.R, t.Color.G, t.Color.B, t.Color.A),
        t.RotationRadians,
        t.Runs.Select(r => new TextRunDto(r.Text, r.FontFamily, r.FontSize, r.Bold, r.Italic)).ToArray());

    private static PathDto ToPath(PathItem p) => new(
        p.Id,
        p.Name,
        p.IsVisible,
        p.IsLocked,
        ToFill(p.Fill),
        ToStroke(p.Stroke),
        p.Opacity,
        p.SubPaths.Select(sp => new SubPathDto(
            sp.IsClosed,
            sp.Nodes.Select(n => new NodeDto(n.Anchor, n.InHandle, n.OutHandle)).ToArray())).ToArray());

    private static GroupDto ToGroup(ArtGroup g) => new(
        g.Id,
        g.Name,
        g.IsVisible,
        g.IsLocked,
        g.Transform,
        g.Opacity,
        g.Children.Select(From).ToArray());

    private static FillDto ToFill(FillSpec f)
        => new(f.IsVisible, f.IsVisible ? new ColorDto(f.Color.R, f.Color.G, f.Color.B, f.Color.A) : null, f.Rule);

    private static StrokeDto ToStroke(StrokeSpec s)
        => new(s.IsVisible, s.IsVisible ? new ColorDto(s.Color.R, s.Color.G, s.Color.B, s.Color.A) : null,
            s.Width, s.Cap, s.Join, s.MiterLimit, s.Alignment);
}

/// <summary>Explicit restoration from DTO back into a live model graph.</summary>
internal static class ItemDtoExtensions
{
    public static LayerItem ToModel(this ItemDto dto) => dto switch
    {
        PathDto p => p.ToModel(),
        GroupDto g => g.ToModel(),
        TextDto t => t.ToModel(),
        _ => throw new NotSupportedException($"Unknown DTO kind {dto.GetType().Name}."),
    };

    private static TextItem ToModel(this TextDto t)
    {
        var item = new TextItem
        {
            Name = t.Name,
            IsVisible = t.IsVisible,
            IsLocked = t.IsLocked,
            Origin = t.Origin,
            Color = new ColorRgb(t.Color.R, t.Color.G, t.Color.B, t.Color.A),
            RotationRadians = t.RotationRadians,
        };
        item.RestoreIdentity(t.Id);
        foreach (TextRunDto run in t.Runs)
        {
            item.Runs.Add(new TextRun
            {
                Text = run.Text,
                FontFamily = run.FontFamily,
                FontSize = run.FontSize,
                Bold = run.Bold,
                Italic = run.Italic,
            });
        }

        return item;
    }

    private static PathItem ToModel(this PathDto p)
    {
        var path = new PathItem
        {
            Name = p.Name,
            IsVisible = p.IsVisible,
            IsLocked = p.IsLocked,
            Fill = p.Fill.ToModel(),
            Stroke = p.Stroke.ToModel(),
            Opacity = p.Opacity,
        };
        path.RestoreIdentity(p.Id);
        foreach (SubPathDto sp in p.SubPaths)
        {
            var sub = path.AddSubPath(sp.Closed);
            foreach (NodeDto n in sp.Nodes)
            {
                sub.Nodes.Add(new PathNode(n.Anchor, n.InHandle, n.OutHandle));
            }
        }

        return path;
    }

    private static ArtGroup ToModel(this GroupDto g)
    {
        var group = new ArtGroup
        {
            Name = g.Name,
            IsVisible = g.IsVisible,
            IsLocked = g.IsLocked,
            Transform = g.Transform,
            Opacity = g.Opacity,
        };
        group.RestoreIdentity(g.Id);
        foreach (ItemDto child in g.Children)
        {
            group.AddItem(child.ToModel());
        }

        return group;
    }

    private static FillSpec ToModel(this FillDto f)
        => f.Visible && f.Color is not null
            ? FillSpec.Solid(new ColorRgb(f.Color.R, f.Color.G, f.Color.B, f.Color.A), f.Rule)
            : FillSpec.None;

    private static StrokeSpec ToModel(this StrokeDto s)
        => s.Visible && s.Color is not null
            ? new StrokeSpec(true, new ColorRgb(s.Color.R, s.Color.G, s.Color.B, s.Color.A),
                s.Width, s.Cap, s.Join, s.MiterLimit, s.Alignment)
            : StrokeSpec.None;
}

internal sealed record ArtboardDto(
    Guid Id,
    string Name,
    double X,
    double Y,
    double Width,
    double Height,
    LayerDto[] Layers);

internal sealed record LayerDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    double Opacity,
    ItemDto[] Items);

internal sealed record DocumentDto(
    int Version,
    Guid Id,
    string Name,
    ArtboardDto[] Artboards,
    ItemDto[] Orphans);

/// <summary>
/// Lossless, deterministic serializer for <see cref="CadDocument"/>.
///
/// "Lossless" means: <c>Deserialize(Serialize(doc))</c> yields a document that is
/// structurally identical to the original — same hierarchy, geometry, styling and
/// identifiers. This is the format embedded in the PDF sidecar (M4) and used over
/// the automation API, so its determinism is load-bearing.
///
/// Format version 1. On any future breaking change bump <see cref="DocumentDto.Version"/>
/// and add migration logic here; never mutate old payloads in place.
/// </summary>
public static class VccadDocumentSerializer
{
    /// <summary>Format version written by this build of the serializer.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        // Stable output: no whitespace, fixed member order from the record
        // declarations, explicit nulls only where meaningful.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        // Enums travel as readable strings, not opaque ints, so sidecars survive
        // enum renumbering and are human-auditable.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes a document to a compact JSON string.</summary>
    public static string Serialize(CadDocument document)
    {
        DocumentDto dto = ToDto(document);
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Serializes a document to UTF-8 bytes (for embedding and hashing).</summary>
    public static byte[] SerializeToBytes(CadDocument document)
        => JsonSerializer.SerializeToUtf8Bytes(ToDto(document), Options);

    /// <summary>Deserializes a document previously produced by <see cref="Serialize"/>.</summary>
    public static CadDocument Deserialize(string json)
    {
        DocumentDto? dto = JsonSerializer.Deserialize<DocumentDto>(json, Options);
        if (dto is null)
        {
            throw new FormatException("Serialized document is empty.");
        }

        return ToModel(dto);
    }

    /// <summary>Deserializes a document from UTF-8 bytes.</summary>
    public static CadDocument Deserialize(byte[] bytes)
    {
        DocumentDto? dto = JsonSerializer.Deserialize<DocumentDto>(bytes, Options);
        if (dto is null)
        {
            throw new FormatException("Serialized document is empty.");
        }

        return ToModel(dto);
    }

    private static DocumentDto ToDto(CadDocument d)
        => new(
            CurrentVersion,
            d.Id,
            d.Name,
            d.Artboards.Select(a => new ArtboardDto(
                a.Id,
                a.Name,
                a.X,
                a.Y,
                a.Width,
                a.Height,
                a.Layers.Select(l => new LayerDto(
                    l.Id,
                    l.Name,
                    l.IsVisible,
                    l.IsLocked,
                    l.Opacity,
                    l.Children.Select(ItemDto.From).ToArray())).ToArray())).ToArray(),
            d.Orphans.Children.Select(ItemDto.From).ToArray());

    private static CadDocument ToModel(DocumentDto dto)
    {
        if (dto.Version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Document format v{dto.Version} is newer than this build supports (v{CurrentVersion}).");
        }

        var document = new CadDocument { Name = dto.Name };
        document.RestoreIdentity(dto.Id);
        foreach (ArtboardDto a in dto.Artboards)
        {
            var artboard = new Artboard(new Size2D(a.Width, a.Height), new Point2D(a.X, a.Y)) { Name = a.Name };
            artboard.RestoreIdentity(a.Id);
            foreach (LayerDto l in a.Layers)
            {
                var layer = new Layer
                {
                    Name = l.Name,
                    IsVisible = l.IsVisible,
                    IsLocked = l.IsLocked,
                    Opacity = l.Opacity,
                };
                layer.RestoreIdentity(l.Id);
                foreach (ItemDto item in l.Items)
                {
                    layer.AddItem(item.ToModel());
                }

                artboard.AddLayer(layer);
            }

            document.AddArtboard(artboard);
        }

        foreach (ItemDto orphan in dto.Orphans ?? Array.Empty<ItemDto>())
        {
            document.Orphans.AddItem(orphan.ToModel());
        }

        return document;
    }
}
