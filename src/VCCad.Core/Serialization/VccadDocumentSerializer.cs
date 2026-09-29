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

internal sealed record StrokeDto(bool Visible, ColorDto? Color, double Width, StrokeCap Cap, StrokeJoin Join, double MiterLimit, StrokeAlignment Alignment, double[]? Dash = null, double DashOffset = 0.0);

internal sealed record NodeDto(Point2D Anchor, Point2D InHandle, Point2D OutHandle);

internal sealed record SubPathDto(bool Closed, NodeDto[] Nodes);

/// <summary>
/// An embedded font programme and the metrics needed to re-emit it.
///
/// All of it travels: this is the file's own font, and the fidelity rule is that what we
/// did not have to change, we do not change. Dropping it here meant a save-and-reload
/// silently lost the embedded programme and the original glyph codes, and the document
/// could then only be re-exported by substituting a different face.
/// </summary>
internal sealed record EmbeddedFontDto(
    EmbeddedFontFormat Format,
    string Program,
    bool Composite,
    string BaseFont,
    string FamilyName,
    int FirstChar,
    double[] Widths,
    double MissingWidth,
    string? ToUnicode,
    string? EncodingName,
    string? BaseEncoding,
    (int Code, string Name)[] Differences,
    string DescendantSubtype,
    string DescendantBaseFont,
    string? Type0Encoding,
    string? Type0EncodingStream,
    string CidSystemInfo,
    double DefaultWidth,
    string WidthsSpec,
    string? CidToGidMapName,
    string? CidToGidMapStream,
    int Flags,
    double[] FontBBox,
    double ItalicAngle,
    double Ascent,
    double Descent,
    double CapHeight,
    double StemV);

internal sealed record TextRunDto(
    string Text,
    string FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    double? AdvanceWidth = null,
    string? SourceFont = null,
    string? RawCodes = null,
    ushort[]? GlyphIds = null,
    EmbeddedFontDto? Embedded = null,
    double GapAfter = 0,
    double PlacedAscentEm = 0);

internal sealed record PathDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    FillDto Fill,
    StrokeDto Stroke,
    double Opacity,
    SubPathDto[] SubPaths,
    double[]? SourceFillCmyk = null,
    double[]? SourceStrokeCmyk = null) : ItemDto;

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
    TextAlignment Alignment,
    double FrameWidth,
    double LineSpacing,
    double ParagraphSpacing,
    TextRunDto[] Runs,
    double[]? SourceCmyk = null) : ItemDto;

/// <summary>
/// An embedded raster image. Samples travel as base64 because they are bytes, not text;
/// they are stored decoded, so a reload reproduces the image bit for bit without having
/// to know how it was compressed.
/// </summary>
internal sealed record ImageDto(
    Guid Id,
    string Name,
    bool IsVisible,
    bool IsLocked,
    int PixelWidth,
    int PixelHeight,
    int BitsPerComponent,
    ImageColorSpace ColorSpace,
    string Samples,
    string Palette,
    string Mask,
    Rect2D Placement,
    double[]? Decode = null,
    double[]? ColourKey = null,
    string? Filter = null,
    string? MaskFilter = null) : ItemDto;

/// <summary>A clip path as it travels in the sidecar: an outline and its rule.</summary>
internal sealed record ClipDto(FillRule Rule, SubPathDto[] SubPaths);

/// <summary>
/// Discriminated union over the possible layer items. System.Text.Json picks the
/// concrete type from the <c>$kind</c> property written by the converter below.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PathDto), "path")]
[JsonDerivedType(typeof(GroupDto), "group")]
[JsonDerivedType(typeof(TextDto), "text")]
[JsonDerivedType(typeof(ImageDto), "image")]
internal abstract record ItemDto
{
    /// <summary>
    /// The clip paths that were in force when the item was painted, outermost first.
    ///
    /// On the base rather than on each kind, because every kind of item can be clipped —
    /// a clipped photograph and a clipped paragraph are the same problem. Dropping it
    /// would not fail anything; it would silently lose the clip on the next save.
    /// </summary>
    public ClipDto[]? Clips { get; init; }

    public static ItemDto From(LayerItem item) => item switch
    {
        PathItem path => ToPath(path) with { Clips = ToClips(item) },
        ArtGroup group => ToGroup(group) with { Clips = ToClips(item) },
        TextItem text => ToText(text) with { Clips = ToClips(item) },
        ImageItem image => ToImage(image) with { Clips = ToClips(item) },
        _ => throw new NotSupportedException($"Unsupported layer item type {item.GetType().Name}."),
    };

    private static ClipDto[]? ToClips(LayerItem item)
        => item.Clips.Count == 0
            ? null
            : item.Clips.Select(c => new ClipDto(
                c.Rule,
                c.SubPaths.Select(sp => new SubPathDto(
                    sp.IsClosed,
                    sp.Nodes.Select(n => new NodeDto(n.Anchor, n.InHandle, n.OutHandle)).ToArray()))
                    .ToArray())).ToArray();

    /// <summary>Puts the clips back onto an item that has just been read.</summary>
    internal static LayerItem WithClips(LayerItem item, ItemDto dto)
    {
        foreach (ClipDto clip in dto.Clips ?? Array.Empty<ClipDto>())
        {
            var spec = new ClipSpec { Rule = clip.Rule };
            foreach (SubPathDto sub in VccadDocumentSerializer.RequireArray(clip.SubPaths, nameof(clip.SubPaths)))
            {
                var restored = new SubPath { IsClosed = sub.Closed };
                foreach (NodeDto node in VccadDocumentSerializer.RequireArray(sub.Nodes, nameof(sub.Nodes)))
                {
                    restored.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
                }

                spec.SubPaths.Add(restored);
            }

            item.Clips.Add(spec);
        }

        return item;
    }

    private static TextDto ToText(TextItem t) => new(
        t.Id,
        t.Name,
        t.IsVisible,
        t.IsLocked,
        t.Origin,
        new ColorDto(t.Color.R, t.Color.G, t.Color.B, t.Color.A),
        t.RotationRadians,
        t.Alignment,
        t.FrameWidth,
        t.LineSpacing,
        t.ParagraphSpacing,
        t.Runs.Select(ToRun).ToArray(),
        t.SourceCmyk);

    private static TextRunDto ToRun(TextRun r) => new(
        r.Text,
        r.FontFamily,
        r.FontSize,
        r.Bold,
        r.Italic,
        r.AdvanceWidth,
        r.SourceFont,
        r.RawCodes,
        r.GlyphIds,
        r.EmbeddedFont is { } font ? ToEmbedded(font) : null,
        r.GapAfter,
        r.PlacedAscentEm);

    private static EmbeddedFontDto ToEmbedded(EmbeddedFont f) => new(
        f.Format,
        Convert.ToBase64String(f.Program),
        f.Composite,
        f.BaseFont,
        f.FamilyName,
        f.FirstChar,
        f.Widths,
        f.MissingWidth,
        f.ToUnicode is null ? null : Convert.ToBase64String(f.ToUnicode),
        f.EncodingName,
        f.BaseEncoding,
        f.Differences.ToArray(),
        f.DescendantSubtype,
        f.DescendantBaseFont,
        f.Type0Encoding,
        f.Type0EncodingStream is null ? null : Convert.ToBase64String(f.Type0EncodingStream),
        f.CidSystemInfo,
        f.DefaultWidth,
        f.WidthsSpec,
        f.CidToGidMapName,
        f.CidToGidMapStream is null ? null : Convert.ToBase64String(f.CidToGidMapStream),
        f.Flags,
        f.FontBBox,
        f.ItalicAngle,
        f.Ascent,
        f.Descent,
        f.CapHeight,
        f.StemV);

    private static ImageDto ToImage(ImageItem i) => new(
        i.Id,
        i.Name,
        i.IsVisible,
        i.IsLocked,
        i.PixelWidth,
        i.PixelHeight,
        i.BitsPerComponent,
        i.ColorSpace,
        Convert.ToBase64String(i.Samples),
        Convert.ToBase64String(i.Palette),
        Convert.ToBase64String(i.Mask),
        i.Placement,
        i.Decode,
        i.ColourKey,
        i.Filter,
        i.MaskFilter);

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
            sp.Nodes.Select(n => new NodeDto(n.Anchor, n.InHandle, n.OutHandle)).ToArray())).ToArray(),
        p.SourceFillCmyk,
        p.SourceStrokeCmyk);

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
            s.Width, s.Cap, s.Join, s.MiterLimit, s.Alignment,
            s.Dash.IsEmpty ? null : s.Dash.Segments.ToArray(), s.Dash.Offset);
}

/// <summary>Explicit restoration from DTO back into a live model graph.</summary>
internal static class ItemDtoExtensions
{
    public static LayerItem ToModel(this ItemDto dto) => dto switch
    {
        PathDto p => ItemDto.WithClips(p.ToModel(), p),
        GroupDto g => ItemDto.WithClips(g.ToModel(), g),
        TextDto t => ItemDto.WithClips(t.ToModel(), t),
        ImageDto i => ItemDto.WithClips(i.ToModel(), i),
        _ => throw new NotSupportedException($"Unknown DTO kind {dto.GetType().Name}."),
    };

    private static TextRun ToModel(this TextRunDto dto)
    {
        var run = new TextRun
        {
            Text = dto.Text,
            FontFamily = dto.FontFamily,
            FontSize = dto.FontSize,
            Bold = dto.Bold,
            Italic = dto.Italic,
            AdvanceWidth = dto.AdvanceWidth,
            SourceFont = dto.SourceFont,
            RawCodes = dto.RawCodes,
            GlyphIds = dto.GlyphIds,
            EmbeddedFont = dto.Embedded is { } e ? ToModel(e) : null,
            GapAfter = dto.GapAfter,
            PlacedAscentEm = dto.PlacedAscentEm,
        };

        return run;
    }

    private static EmbeddedFont ToModel(this EmbeddedFontDto dto) => new()
    {
        Format = dto.Format,
        Program = Convert.FromBase64String(dto.Program),
        Composite = dto.Composite,
        BaseFont = dto.BaseFont,
        FamilyName = dto.FamilyName,
        FirstChar = dto.FirstChar,
        Widths = dto.Widths,
        MissingWidth = dto.MissingWidth,
        ToUnicode = dto.ToUnicode is null ? null : Convert.FromBase64String(dto.ToUnicode),
        EncodingName = dto.EncodingName,
        BaseEncoding = dto.BaseEncoding,
        Differences = dto.Differences,
        DescendantSubtype = dto.DescendantSubtype,
        DescendantBaseFont = dto.DescendantBaseFont,
        Type0Encoding = dto.Type0Encoding,
        Type0EncodingStream = dto.Type0EncodingStream is null
            ? null
            : Convert.FromBase64String(dto.Type0EncodingStream),
        CidSystemInfo = dto.CidSystemInfo,
        DefaultWidth = dto.DefaultWidth,
        WidthsSpec = dto.WidthsSpec,
        CidToGidMapName = dto.CidToGidMapName,
        CidToGidMapStream = dto.CidToGidMapStream is null
            ? null
            : Convert.FromBase64String(dto.CidToGidMapStream),
        Flags = dto.Flags,
        FontBBox = dto.FontBBox,
        ItalicAngle = dto.ItalicAngle,
        Ascent = dto.Ascent,
        Descent = dto.Descent,
        CapHeight = dto.CapHeight,
        StemV = dto.StemV,
    };

    private static ImageItem ToModel(this ImageDto i)
    {
        var item = new ImageItem
        {
            Name = i.Name,
            IsVisible = i.IsVisible,
            IsLocked = i.IsLocked,
            PixelWidth = i.PixelWidth,
            PixelHeight = i.PixelHeight,
            BitsPerComponent = i.BitsPerComponent,
            ColorSpace = i.ColorSpace,
            Samples = Convert.FromBase64String(i.Samples),
            Palette = Convert.FromBase64String(i.Palette),
            Mask = Convert.FromBase64String(i.Mask),
            Placement = i.Placement,
            Decode = i.Decode,
            ColourKey = i.ColourKey,
            Filter = i.Filter,
            MaskFilter = i.MaskFilter,
        };
        item.RestoreIdentity(i.Id);
        return item;
    }

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
            Alignment = t.Alignment,
        };
        item.FrameWidth = t.FrameWidth;
        item.LineSpacing = t.LineSpacing;
        item.ParagraphSpacing = t.ParagraphSpacing;
        item.SourceCmyk = t.SourceCmyk;
        item.RestoreIdentity(t.Id);
        foreach (TextRunDto run in VccadDocumentSerializer.RequireArray(t.Runs, nameof(t.Runs)))
        {
            item.Runs.Add(run.ToModel());
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
            SourceFillCmyk = p.SourceFillCmyk,
            SourceStrokeCmyk = p.SourceStrokeCmyk,
        };
        path.RestoreIdentity(p.Id);
        foreach (SubPathDto sp in VccadDocumentSerializer.RequireArray(p.SubPaths, nameof(p.SubPaths)))
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
        foreach (ItemDto child in VccadDocumentSerializer.RequireArray(g.Children, nameof(g.Children)))
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
                s.Width, s.Cap, s.Join, s.MiterLimit, s.Alignment,
                new DashPattern(s.Dash ?? Array.Empty<double>(), s.DashOffset))
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

/// <summary>
/// The Illustrator private-data payload as it travels in the sidecar: decoded
/// text plus the container format it came from. Kept null (and therefore absent
/// from the JSON, see <see cref="DocumentDto.AiPrivateData"/>) whenever the
/// document carries no Illustrator data, so sidecars and PDF bytes of ordinary
/// documents are unchanged.
/// </summary>
internal sealed record AiPrivateDataDto(string Text, AiPrivateDataFormat Format);

internal sealed record DocumentDto(
    int Version,
    Guid Id,
    string Name,
    ArtboardDto[] Artboards,
    ItemDto[] Orphans,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiPrivateDataDto? AiPrivateData = null);

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
        try
        {
            return JsonSerializer.Serialize(dto, Options);
        }
        catch (ArgumentException ex)
        {
            throw NotWritable(document, ex);
        }
    }

    /// <summary>Serializes a document to UTF-8 bytes (for embedding and hashing).</summary>
    public static byte[] SerializeToBytes(CadDocument document)
    {
        DocumentDto dto = ToDto(document);
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(dto, Options);
        }
        catch (ArgumentException ex)
        {
            throw NotWritable(document, ex);
        }
    }

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
    {
        ValidateFiniteArtboards(d);
        return new(
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
            d.Orphans.Children.Select(ItemDto.From).ToArray(),
            d.AiPrivateData is null
                ? null
                : new AiPrivateDataDto(d.AiPrivateData.Text, d.AiPrivateData.Format));
    }

    private static CadDocument ToModel(DocumentDto dto)
    {
        if (dto.Version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Document format v{dto.Version} is newer than this build supports (v{CurrentVersion}).");
        }

        ArtboardDto[] artboards = RequireArray(dto.Artboards, nameof(dto.Artboards));
        ItemDto[] orphans = RequireArray(dto.Orphans, nameof(dto.Orphans));

        var document = new CadDocument { Name = dto.Name };
        document.RestoreIdentity(dto.Id);
        foreach (ArtboardDto a in artboards)
        {
            RequireFiniteArtboard(a);
            var artboard = new Artboard(new Size2D(a.Width, a.Height), new Point2D(a.X, a.Y)) { Name = a.Name };
            artboard.RestoreIdentity(a.Id);
            foreach (LayerDto l in RequireArray(a.Layers, nameof(a.Layers)))
            {
                var layer = new Layer
                {
                    Name = l.Name,
                    IsVisible = l.IsVisible,
                    IsLocked = l.IsLocked,
                    Opacity = l.Opacity,
                };
                layer.RestoreIdentity(l.Id);
                foreach (ItemDto item in RequireArray(l.Items, nameof(l.Items)))
                {
                    layer.AddItem(item.ToModel());
                }

                artboard.AddLayer(layer);
            }

            document.AddArtboard(artboard);
        }

        foreach (ItemDto orphan in orphans)
        {
            document.Orphans.AddItem(orphan.ToModel());
        }

        // A sidecar written before the payload existed has no member here; a
        // payload property that is present but null means "no Illustrator data".
        if (dto.AiPrivateData is { } ai)
        {
            document.AiPrivateData = new AiPrivateData(ai.Text ?? string.Empty, ai.Format);
        }

        EnsureUniqueIdentities(document);
        return document;
    }

    /// <summary>
    /// A structural array that a well-formed sidecar always carries. System.Text.Json
    /// leaves a missing member null; dereferencing it is a
    /// <see cref="NullReferenceException"/> that names neither the document nor the
    /// member, so every array the reader walks is checked here first.
    /// </summary>
    internal static T[] RequireArray<T>(T[]? values, string member)
        => values ?? throw new JsonException($"Serialized document is missing the '{member}' array.");

    /// <summary>
    /// Refuses an artboard whose rectangle is not finite before it reaches layout.
    ///
    /// System.Text.Json parses the quoted string <c>"1e400"</c> as a number (its
    /// UTF-8 parser reports overflow as infinity) while refusing <c>"NaN"</c>,
    /// <c>"Infinity"</c> and a bare <c>1e400</c>. Validating the parsed value
    /// therefore catches every spelling, and a non-finite artboard — which would
    /// otherwise compute NaN fits and silently draw nothing — is refused by name.
    /// </summary>
    private static void RequireFiniteArtboard(ArtboardDto a)
    {
        if (double.IsFinite(a.X) && double.IsFinite(a.Y)
            && double.IsFinite(a.Width) && double.IsFinite(a.Height))
        {
            return;
        }

        throw new JsonException(
            $"Artboard '{a.Name}' ({a.Id}) has a non-finite rectangle: "
            + $"X={a.X}, Y={a.Y}, Width={a.Width}, Height={a.Height}. "
            + "A non-finite artboard cannot be laid out, fitted or rendered.");
    }

    /// <summary>
    /// Refuses a document holding a non-finite artboard before any JSON is written.
    ///
    /// System.Text.Json's own refusal is an <see cref="ArgumentException"/> about
    /// "positive and negative infinity" that names neither the document nor the
    /// artboard, so a person whose imported page arrived with a NaN MediaBox has
    /// nothing to act on. Naming the value and the artboard it sits on turns the
    /// failure into a diagnosis. (Keeping a non-finite value out of the model in the
    /// first place is the importer's job; this is the last line of defence.)
    /// </summary>
    private static void ValidateFiniteArtboards(CadDocument document)
    {
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            Artboard a = document.Artboards[i];
            if (double.IsFinite(a.X) && double.IsFinite(a.Y)
                && double.IsFinite(a.Width) && double.IsFinite(a.Height))
            {
                continue;
            }

            throw new ArgumentException(
                $"Document '{document.Name}' cannot be saved: artboard {i} '{a.Name}' has a non-finite "
                + $"rectangle (X={a.X}, Y={a.Y}, Width={a.Width}, Height={a.Height}). A non-finite "
                + "artboard cannot be laid out, fitted or rendered.", nameof(document));
        }
    }

    private static ArgumentException NotWritable(CadDocument document, ArgumentException inner)
        => new(
            $"Document '{document.Name}' contains a number that cannot be written as JSON: {inner.Message}",
            nameof(document),
            inner);

    /// <summary>
    /// Gives every object a distinct identity.
    ///
    /// A crafted sidecar can carry the same id on two items. Restoring both verbatim
    /// leaves two objects answering to one id, and every id-addressed operation then
    /// silently picks one of them — a wrong answer, not a crash. The first holder
    /// keeps the id; each later duplicate is renumbered, so the drawing still loads
    /// and both objects stay addressable.
    /// </summary>
    private static void EnsureUniqueIdentities(CadDocument document)
    {
        var seen = new HashSet<Guid>();
        document.RestoreIdentity(UniqueIdentity(document.Id, seen));
        foreach (Artboard artboard in document.Artboards)
        {
            artboard.RestoreIdentity(UniqueIdentity(artboard.Id, seen));
            foreach (Layer layer in artboard.Layers)
            {
                layer.RestoreIdentity(UniqueIdentity(layer.Id, seen));
                foreach (LayerItem item in layer.Children)
                {
                    EnsureUniqueIdentity(item, seen);
                }
            }
        }

        foreach (LayerItem item in document.Orphans.Children)
        {
            EnsureUniqueIdentity(item, seen);
        }
    }

    private static void EnsureUniqueIdentity(LayerItem item, HashSet<Guid> seen)
    {
        item.RestoreIdentity(UniqueIdentity(item.Id, seen));
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                EnsureUniqueIdentity(child, seen);
            }
        }
    }

    private static Guid UniqueIdentity(Guid id, HashSet<Guid> seen)
    {
        if (seen.Add(id))
        {
            return id;
        }

        Guid fresh;
        do
        {
            fresh = Guid.NewGuid();
        }
        while (!seen.Add(fresh));

        return fresh;
    }
}
