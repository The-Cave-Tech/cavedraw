using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Text;
using VCCad.Core.Serialization;
using VCCad.Pdf.Fonts;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// Exports a <see cref="CadDocument"/> to a valid PDF 1.7 byte stream.
///
/// Model → PDF mapping rules:
/// <list type="bullet">
/// <item>Each artboard becomes one PDF page whose MediaBox equals the artboard
/// rectangle; the page's user space is the artboard's top-left-origin space with
/// the Y axis flipped (PDF user space grows upward). This flip is encoded as a
/// single <c>cm</c> matrix at the start of each content stream, so all path
/// coordinates below it are simply the model's own numbers.</item>
/// <item>Every path item maps onto native PDF path operators: straight segments
/// become <c>l</c> and cubic segments <c>c</c> — never flattened, which is why the
/// <em>rendered</em> geometry is exact (curves stay curves in PDF).</item>
/// <item>Fill and stroke, colours (DeviceRGB), stroke width, caps, joins and the
/// fill rule are all honoured. A closed contour is painted with the fill; open
/// contours are stroked only — the same rule the model applies (brief: closed
/// paths are filled).</item>
/// <item>Group transforms are <em>baked into geometry</em> before emission: this
/// avoids accumulating <c>cm</c> state and keeps every exported path self-contained.
/// Stroke widths are scaled by √|det| of the group transform as a uniform
/// approximation (PDF strokes cannot be sheared; noted for the M4 fidelity task).</item>
/// </list>
///
/// Lossless round-trip (ADR-03): the complete document model is embedded as an
/// embedded-file attachment (<c>vccad-document</c>), compressed with FlateDecode.
/// Any viewer shows the faithful artwork; only VCCad reads the attachment back.
/// </summary>
public static class PdfDocumentExporter
{
    /// <summary>Name under which the lossless model attachment is embedded.</summary>
    public const string SidecarFileName = "vccad-document";

    /// <summary>Exports the document and returns the complete PDF bytes.</summary>
    public static byte[] Export(CadDocument document, IReadOnlyList<string>? history = null)
    {
        using var buffer = new MemoryStream();
        Export(document, buffer, history);
        return buffer.ToArray();
    }

    /// <summary>
    /// Exports the document and additionally reports every lossy approximation the export
    /// had to make - a gradient spread PDF cannot express, per-stop opacity, a freeform
    /// gradient with no native PDF shading. An approximation that is reported is honest;
    /// one that is not is a silently wrong document.
    /// </summary>
    public static byte[] Export(CadDocument document, out IReadOnlyList<string> notes,
        IReadOnlyList<string>? history = null)
    {
        var collected = new List<string>();
        using var buffer = new MemoryStream();
        Export(document, buffer, history, collected);
        notes = collected;
        return buffer.ToArray();
    }

    /// <summary>
    /// Exports the document into <paramref name="output"/>.
    ///
    /// <paramref name="history"/> is our own private data: the command queue that produced
    /// the document. Carrying it in the file means a document remembers how it was made,
    /// not just what it looks like, and a session restored from a file can show the steps
    /// that built it. Stored as a separate catalog stream beside the model sidecar, so a
    /// reader that does not care about it can ignore both.
    /// </summary>
    public static void Export(CadDocument document, Stream output,
        IReadOnlyList<string>? history = null)
        => Export(document, output, history, null);

    private static void Export(CadDocument document, Stream output,
        IReadOnlyList<string>? history, List<string>? notes)
    {
        var assembler = new PdfAssembler();

        // ------------------------------------------------------------------
        // Object numbering plan (contiguous; references may point forward).
        //   1  Catalog       2  Pages tree       3  Names tree
        //   4  Embedded sidecar stream   5  FileSpec
        //   6..  per artboard: page object + content stream, interleaved.
        // ------------------------------------------------------------------
        int catalogNumber = assembler.Allocate();
        int pagesNumber = assembler.Allocate();
        int sidecarStreamNumber = assembler.Allocate();

        var pageNumbers = new int[document.Artboards.Count];
        var contentNumbers = new int[document.Artboards.Count];
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            pageNumbers[i] = assembler.Allocate();
            contentNumbers[i] = assembler.Allocate();
        }

        // ------------------------------------------------------------------
        // Illustrator private data. A document that carries a decoded .ai payload
        // re-emits it so the export is again a valid Illustrator document, and so
        // VCCad can import it back. Strictly conditional: a document without a
        // payload allocates nothing and its PDF bytes are unchanged.
        // ------------------------------------------------------------------
        Ai.AiPrivateDataDocument? aiDocument = document.AiPrivateData is { IsEmpty: false } aiPayload
            ? Ai.AiPrivateDataDocument.FromPrivateData(aiPayload)
            : null;
        int aiStreamNumber = aiDocument is null ? 0 : assembler.Allocate();
        string pagePieceInfo = aiStreamNumber == 0
            ? string.Empty
            : " " + Ai.AiPrivateDataEmbedder.PieceInfo(aiStreamNumber);
        string catalogCreatorInfo = aiStreamNumber == 0
            ? string.Empty
            : Ai.AiPrivateDataEmbedder.CatalogCreatorInfo() + " ";

        // ------------------------------------------------------------------
        // Fonts: collect text usage and embed the bundled fonts (FontFile2).
        // ------------------------------------------------------------------
        var usage = new Dictionary<FontKey, HashSet<int>>();
        var embeddedFonts = new List<EmbeddedFont>();
        foreach (Artboard artboard in document.Artboards)
        {
            ScanText(artboard, usage, embeddedFonts);
        }

        var embedder = new PdfFontEmbedder(assembler, usage, embeddedFonts);

        var alphas = new List<double>();
        foreach (Artboard artboard in document.Artboards)
        {
            CollectAlphas(document, artboard, 1.0, alphas, 0);
        }

        var alphaStates = new PdfAlphaStates(assembler, alphas);

        // Blend states are **allocated while the content is written**, because a mode only becomes a resource when
        // something actually switches to it. Alpha is collected first (the painter has to know whether any
        // transparency exists at all), but a blend needs no such scan: the switch is written at the paint it
        // belongs to and the resource dictionary is assembled afterwards.
        var blendStates = new PdfBlendStates(assembler);

        // Every embedded image is written back into the file as its own XObject, keeping
        // the colour space the document stores — a CMYK scan stays a CMYK scan. The
        // resource dictionary is shared across pages, so an image is written once however
        // many pages place it; unused entries in a resource dictionary are legal.
        var imageObjects = new PdfImageObjects(assembler, AllImages(document), notes ?? new List<string>());

        // Gradient shadings are allocated while an artboard's content stream is built
        // (each usage carries its own geometry), so the content is written first and the
        // resource dictionary - which must name every shading - is assembled afterwards.
        var shadingObjects = new PdfShadingObjects(assembler, notes ?? new List<string>());
        var contents = new byte[document.Artboards.Count][];
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            contents[i] = BuildArtboardContent(
                document.Artboards[i], document, embedder, alphaStates, blendStates, imageObjects, shadingObjects,
                notes ?? new List<string>());
        }

        // One `/ExtGState` key carrying both halves. Two dictionaries under the same key would be a duplicate
        // entry, and a reader that kept the later one would lose every alpha state.
        string extGState = ExtGStateDict(alphaStates.Entries, blendStates.Entries);

        string resources =
            $"/Resources << {embedder.FontDict()}{extGState}{imageObjects.Dict()}{shadingObjects.Dict()}>>";

        // ------------------------------------------------------------------
        // Sidecar: lossless model JSON, zlib (RFC 1950) compressed — the PDF
        // FlateDecode filter uses exactly the zlib wrapper, so a conformant
        // reader can decompress it without custom code.
        // ------------------------------------------------------------------
        byte[] modelJson = VccadDocumentSerializer.SerializeToBytes(document);
        byte[] sidecarStream = Compress(modelJson);

        // Stored as a plain catalog stream rather than an /EmbeddedFiles
        // attachment: PDF/A (veraPDF clause 6.8) only permits embedded *files*
        // that are themselves PDF/A, and our model JSON is not.
        assembler.SetBody(sidecarStreamNumber, MakeStreamObject(sidecarStream));

        // The command queue, in our own catalog stream. Absent when there is nothing to
        // record, so an export of a freshly opened file stays as small as it was.
        int historyStreamNumber = history is { Count: > 0 } ? assembler.Allocate() : 0;
        if (historyStreamNumber != 0)
        {
            byte[] historyBytes = Compress(Encoding.UTF8.GetBytes(string.Join("\n", history!)));
            assembler.SetBody(historyStreamNumber, MakeStreamObject(historyBytes));
        }

        // ------------------------------------------------------------------
        // Document metadata: /Info, a file /ID and an XMP packet. PDF/A (which
        // dominates the veraPDF corpus) requires all three.
        // ------------------------------------------------------------------
        int infoNumber = assembler.Allocate();
        int metadataNumber = assembler.Allocate();
        int iccNumber = assembler.Allocate();
        int outputIntentNumber = assembler.Allocate();

        // ------------------------------------------------------------------
        // Content streams and page objects.
        // ------------------------------------------------------------------
        var kidList = new StringBuilder();
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            Artboard artboard = document.Artboards[i];

            // Content streams are FlateDecode-filtered like the sidecar; the raw
            // operator text is compressed here before being wrapped.
            assembler.SetBody(contentNumbers[i], MakeStreamObject(Compress(contents[i])));

            assembler.SetBody(
                pageNumbers[i],
                $"<< /Type /Page /Parent {pagesNumber} 0 R " +
                $"/MediaBox [0 0 {Num(artboard.Width)} {Num(artboard.Height)}] " +
                $"/Contents {contentNumbers[i]} 0 R " +
                resources + pagePieceInfo + " >>");

            kidList.Append(pageNumbers[i]).Append(" 0 R ");
        }

        // ------------------------------------------------------------------
        // Document trailer: catalog → pages → kids.
        // ------------------------------------------------------------------
        assembler.SetBody(
            pagesNumber,
            $"<< /Type /Pages /Kids [{kidList}] /Count {document.Artboards.Count} >>");
        if (aiDocument is not null)
        {
            // The payload is stored as its own FlateDecode stream, exactly like
            // Illustrator stores /AIPrivateData: the filter is the block-level zlib
            // compression our extractor expects to find in the raw stream bytes.
            assembler.SetBody(
                aiStreamNumber,
                MakeStreamObject(Compress(Ai.AiPrivateDataEmbedder.EncodePayload(aiDocument.Text))));
        }

        assembler.SetBody(
            catalogNumber,
            $"<< /Type /Catalog /Pages {pagesNumber} 0 R " +
            $"/VCCadDocument {sidecarStreamNumber} 0 R " +
            (historyStreamNumber != 0 ? $"/VCCadHistory {historyStreamNumber} 0 R " : string.Empty) +
            catalogCreatorInfo +
            $"/Metadata {metadataNumber} 0 R /OutputIntents [{outputIntentNumber} 0 R] >>");

        string now = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        assembler.SetBody(
            infoNumber,
            $"<< /Title ({EscapeText(document.Name)}) /Producer (VCCad) /Creator (VCCad) " +
            $"/CreationDate (D:{now}Z) /ModDate (D:{now}Z) >>");
        assembler.SetBody(
            metadataNumber,
            MakeStreamObject(Compress(Encoding.UTF8.GetBytes(BuildXmp(document.Name))),
                " /Type /Metadata /Subtype /XML"));

        // DeviceRGB is only allowed under PDF/A with an RGB output intent; embed
        // the sRGB profile so the colour space is defined.
        assembler.SetBody(iccNumber, MakeStreamObject(Compress(LoadSrgbProfile()), " /N 3"));
        assembler.SetBody(
            outputIntentNumber,
            $"<< /Type /OutputIntent /S /GTS_PDFA1 " +
            $"/OutputConditionIdentifier (sRGB IEC61966-2.1) /Info (sRGB IEC61966-2.1) " +
            $"/DestOutputProfile {iccNumber} 0 R >>");

        // File identifier: two byte strings. A stable first half makes repeated
        // saves correlatable; the second half changes with the content.
        string id = Convert.ToHexString(SHA256.HashData(modelJson)).ToLowerInvariant();

        byte[] bytes = assembler.Serialize(catalogNumber, $"/Info {infoNumber} 0 R /ID [<{id}> <{id}>]");
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Renders one artboard to a content stream. See the class remarks for the
    /// mapping rules. The returned bytes are the plain content (uncompressed);
    /// callers wrap them via <see cref="MakeStreamObject"/>.
    /// </summary>
    /// <summary>
    /// The page's <c>/ExtGState</c> resource dictionary, from every group of states that shares the key.
    ///
    /// One key, one dictionary: alpha (<c>/ca</c>, <c>/CA</c>) and blend (<c>/BM</c>) both live under
    /// <c>/ExtGState</c>, and writing two dictionaries under it would be a duplicate entry - a reader that kept
    /// the later one would lose every alpha state in the file, which is a silent change to the whole picture.
    /// Empty when there is nothing to name, so a caller can concatenate the result unconditionally.
    /// </summary>
    private static string ExtGStateDict(params IEnumerable<(string Name, int Object)>[] groups)
    {
        var sb = new StringBuilder();
        int count = 0;

        foreach (IEnumerable<(string Name, int Object)> group in groups)
        {
            foreach ((string name, int obj) in group)
            {
                if (count == 0)
                {
                    sb.Append("/ExtGState << ");
                }

                sb.Append(name).Append(' ').Append(obj).Append(" 0 R ");
                count++;
            }
        }

        if (count == 0)
        {
            return string.Empty;
        }

        return sb.Append(">> ").ToString();
    }

    private static byte[] BuildArtboardContent(
        Artboard artboard,
        CadDocument document,
        PdfFontEmbedder embedder,
        PdfAlphaStates alphaStates,
        PdfBlendStates blendStates,
        PdfImageObjects? images = null,
        PdfShadingObjects? shadings = null,
        List<string>? notes = null)
    {
        var ops = new List<string>();

        // Path coordinates are stored relative to the artboard's top-left, so the
        // page flip is simply  px = x ; py = Height − y  (no artboard offset).
        double e = 0;
        double f = artboard.Height;
        ops.Add($"{Num(1)} 0 0 {Num(-1)} {Num(e)} {Num(f)} cm");

        if (artboard.IsVisible)
        {
            foreach (Layer layer in artboard.Layers)
            {
                if (!layer.IsEffectivelyVisible)
                {
                    continue;
                }

                foreach (LayerItem item in layer.Children)
                {
                    PaintItem(ops, item, AffineTransform.Identity, 1.0, alphaStates, blendStates, embedder, images,
                        shadings, document, notes);
                }
            }
        }

        if (ops.Count == 0)
        {
            ops.Add("% empty artboard"); // keep the stream non-empty & valid
        }

        return Encoding.UTF8.GetBytes(string.Join("\n", ops) + "\n");
    }

    /// <summary>
    /// Emits the placement operators for one embedded image.
    ///
    /// A PDF image is painted into the unit square, so the placement becomes the CTM. The
    /// four model-space corners are taken through the current transform and the matrix is
    /// built from them, which keeps images inside transformed groups correct — and the
    /// vertical flip is folded in, because the unit square's origin is the image's
    /// bottom-left while the model's origin is its top-left.
    /// </summary>
    private static void PaintImage(List<string> ops, ImageItem image, AffineTransform toDoc,
        PdfImageObjects? images)
    {
        if (images is null || !images.TryName(image, out string name))
        {
            return;
        }

        Rect2D box = image.Placement;
        if (box.IsEmpty)
        {
            return;
        }

        Point2D topLeft = toDoc.Transform(new Point2D(box.Left, box.Top));
        Point2D topRight = toDoc.Transform(new Point2D(box.Right, box.Top));
        Point2D bottomLeft = toDoc.Transform(new Point2D(box.Left, box.Bottom));

        // The unit square's origin goes to the placement's top-left, and the v axis runs
        // the other way down the box - which is what the original file does too. Mapping
        // (0,0) to the bottom-left instead measures worse on page 1 (RMSE 15.4 to 17.1),
        // so this orientation is the one that reproduces the reference.
        double a = topRight.X - topLeft.X;
        double b = topRight.Y - topLeft.Y;
        double c = bottomLeft.X - topLeft.X;
        double d = bottomLeft.Y - topLeft.Y;

        ops.Add("q");
        ops.Add($"{Num(a)} {Num(b)} {Num(c)} {Num(d)} {Num(topLeft.X)} {Num(topLeft.Y)} cm");
        ops.Add($"/{name} Do");
        ops.Add("Q");
    }

    /// <summary>
    /// Recursively emits an item. <paramref name="toDoc"/> maps the item's local
    /// coordinates into artboard (document) space — the composition of every
    /// ancestor group transform; <paramref name="opacity"/> is the accumulated
    /// product of ancestor opacities (reserved: emitted in a later sprint when the
    /// ExtGState task lands, see project plan M4).
    ///
    /// **Text is painted here too**, not by a loop of its own. It used to have one, and the walk returned early for
    /// a <see cref="TextItem"/> because of it - which meant every group clip this method opens enclosed the paths
    /// and images inside the group and not the text, while the canvas (which pushes each group's clip as it
    /// descends) cropped both. Text was written at the identity frame until #164 gave it the group's frame, and
    /// an ancestor's clip is the half that giving it a frame could not reach: a second loop that re-derives the
    /// frame is a second place for it to disagree, so the walk is the only one left.
    /// </summary>
    private static void PaintItem(List<string> ops, LayerItem item, AffineTransform toDoc, double opacity, PdfAlphaStates alphaStates, PdfBlendStates blendStates, PdfFontEmbedder embedder, PdfImageObjects? images = null, PdfShadingObjects? shadings = null, CadDocument? document = null, List<string>? notes = null, int artDepth = 0)
    {
        if (!item.IsEffectivelyVisible())
        {
            return;
        }

        // **A leaf item's blend is the graphics state's `/BM`.** PDF composites the paint with whatever is already
        // on the group's backdrop, which is exactly what the model means by an item's blend, and CSS's
        // `mix-blend-mode` on a leaf element is the same picture - so no isolation is needed.
        //
        // **A group's blend is not this**, and is deliberately not written here: CSS composites a group as a unit,
        // which PDF expresses with an **isolated transparency group** - a form XObject with
        // `/Group << /S /Transparency /I true /K false >>` - and this exporter emits no form XObjects. Blending
        // each child against the backdrop instead would be a different picture, which is worse than leaving it
        // out and saying so in `PdfExportSupport`.
        string? blendGs = item is ArtGroup ? null : blendStates.Gs(item.BlendMode);

        // **A group's blend is not a per-object `/BM`, and dropping it silently is its own defect.** A `/BM` on a
        // group's *contents* would composite each child against the page rather than the group against the page,
        // which is the wrong picture - so the correct treatment is an isolated transparency group, which this
        // exporter does not write yet. Until it does, the loss is declared rather than ignored: a person who set a
        // blend on a group is shown it on the canvas and would otherwise find it gone from the page with nothing
        // said, which is the same silent loss #195's fix was about.
        if (item is ArtGroup { BlendMode: not BlendMode.Normal } blendedGroup)
        {
            notes?.Add(
                $"the group '{blendedGroup.Name}' has blend mode '{blendedGroup.BlendMode.ToSvgName()}', "
                + "which the exported page does not apply");
        }

        // A clip is emitted around the item rather than baked into its geometry, because
        // that is what it is: the item is drawn whole and the outline limits what shows.
        // Several clips intersect, which is what successive W n operators do.
        //
        // One item's clips go through here once, for every kind of item - a text block's own clips included,
        // which is what #164 fixed separately while text still had its own loop. Nothing else writes them, so
        // there is no second clip stack to disagree with this one.
        //
        // The `q`/`Q` is the item's own either way. For a blend it is what keeps the state off the items painted
        // after it; the item's own clips then go inside the same pair.
        bool clipped = item.Clips.Count > 0;
        bool wrapped = clipped || blendGs is not null;
        if (wrapped)
        {
            ops.Add("q");

            if (blendGs is not null)
            {
                ops.Add(blendGs);
            }

            if (clipped)
            {
                foreach (ClipSpec clip in item.Clips)
                {
                    AppendClip(ops, clip, toDoc);
                }
            }
        }

        switch (item)
        {
            case PathItem path:
                PaintPath(ops, path, toDoc, opacity, alphaStates, blendStates, embedder, shadings, images, document,
                    notes, artDepth);
                break;

            case TextItem text:
                // Inside the group's q/clips and the block's own, both of which are already open, and in the frame
                // the block is placed in - the same `toDoc` the paths beside it are painted with.
                WriteText(ops, text, embedder, alphaStates, toDoc, notes);
                break;

            case ImageItem image:
                PaintImage(ops, image, toDoc, images);
                break;

            case ArtGroup group:
                // Compose is defined as "apply argument first, then this", which is
                // exactly local→parent→doc as the walk descends.
                AffineTransform childToDoc = toDoc.Compose(group.Transform);
                foreach (LayerItem child in group.Children)
                {
                    PaintItem(ops, child, childToDoc, opacity * group.Opacity, alphaStates, blendStates, embedder,
                        images, shadings, document, notes, artDepth);
                }

                break;
        }

        if (wrapped)
        {
            ops.Add("Q");
        }
    }

    /// <summary>
    /// The colour-setting operator for a fill or a stroke.
    ///
    /// With stored ink values the original DeviceCMYK is written back verbatim, and the
    /// page is painted with the colour it arrived with. Without them - a document authored
    /// here, or one already in RGB - the RGB is written instead.
    /// </summary>
    private static string ColorOperator(ColorRgb color, double[]? cmyk, bool stroke)
    {
        if (cmyk is { Length: >= 4 })
        {
            return $"{Num(cmyk[0])} {Num(cmyk[1])} {Num(cmyk[2])} {Num(cmyk[3])} " +
                   (stroke ? "K" : "k");
        }

        return $"{Num(color.R)} {Num(color.G)} {Num(color.B)} " + (stroke ? "RG" : "rg");
    }

    /// <summary>
    /// The DeviceCMYK to write a run's fill with: the block's original ink values, but only for a run that draws
    /// in the block's own colour.
    ///
    /// The model records the CMYK the file painted the block with, on the block. A run carrying a colour of its
    /// own has no recorded ink, so writing the block's would paint it a colour the file never named - and the RGB
    /// the run states is written instead.
    /// </summary>
    private static double[]? RunCmyk(TextItem text, TextRun run) => run.Color is null ? text.SourceCmyk : null;

    /// <summary>Every embedded image in the document, in a stable order.</summary>
    private static IEnumerable<ImageItem> AllImages(CadDocument document)
    {
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (ImageItem image in FlattenImages(layer.Children))
                {
                    yield return image;
                }
            }
        }

        foreach (ImageItem image in FlattenImages(document.Orphans.Children))
        {
            yield return image;
        }
    }

    private static IEnumerable<ImageItem> FlattenImages(IReadOnlyList<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            switch (item)
            {
                case ImageItem image:
                    yield return image;
                    break;
                case ArtGroup group:
                    foreach (ImageItem nested in FlattenImages(group.Children))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Emits one clip: the outline, then <c>W</c> (or <c>W*</c>) and <c>n</c>, which is how
    /// PDF says to restrict painting to a path without painting the path itself.
    /// </summary>
    private static void AppendClip(List<string> ops, ClipSpec clip, AffineTransform toDoc)
    {
        foreach (SubPath sub in clip.SubPaths)
        {
            if (sub.Nodes.Count == 0)
            {
                continue;
            }

            Point2D start = toDoc.Transform(sub.Nodes[0].Anchor);
            ops.Add($"{Num(start.X)} {Num(start.Y)} m");

            // A straight segment is a cubic whose handles sit on its ends, so one form
            // covers both without asking which it is.
            int last = sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;
            for (int i = 0; i < last; i++)
            {
                PathNode from = sub.Nodes[i];
                PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                Point2D c1 = toDoc.Transform(from.OutHandle);
                Point2D c2 = toDoc.Transform(to.InHandle);
                Point2D end = toDoc.Transform(to.Anchor);
                ops.Add($"{Num(c1.X)} {Num(c1.Y)} {Num(c2.X)} {Num(c2.Y)} "
                        + $"{Num(end.X)} {Num(end.Y)} c");
            }

            if (sub.IsClosed)
            {
                ops.Add("h");
            }
        }

        ops.Add(clip.Rule == FillRule.EvenOdd ? "W*" : "W");
        ops.Add("n");
    }

    /// <summary>
    /// How deep the art a stroke's brush maps is nested, matching the canvas's cap so a cycle stops at the same
    /// place on the screen and in the file.
    ///
    /// One statement of the cap rather than one per walk, because the **alpha scan** has to reach exactly as deep
    /// as the painting does: an alpha the painter asks for and the scan never saw is a graphics state that does not
    /// exist, so a brush's translucent artwork would be written opaque.
    /// </summary>
    private const int MaxArtDepth = 8;

    /// <summary>
    /// Emits the fill/stroke operators for one path. Contours are separated into
    /// closed (fillable) and open (stroke-only), so the renderer never fills an
    /// open path as PDF would implicitly do.
    /// </summary>
    private static void PaintPath(List<string> ops, PathItem path, AffineTransform toDoc, double opacity, PdfAlphaStates alphaStates, PdfBlendStates blendStates, PdfFontEmbedder embedder, PdfShadingObjects? shadings = null, PdfImageObjects? images = null, CadDocument? document = null, List<string>? notes = null, int artDepth = 0)
    {
        // A **stroke's raster effects** are the same kind of thing and take the same route, reached from the stroke
        // side: the path is drawn with every stroke it has, the effect graphs run over those pixels, and the answer
        // is placed as an image. The frame is asked of `SelectionEngine` for the same reason the filter branch asks
        // it - one statement of where the artwork is drawn - and the effects are checked first because that is the
        // order the canvas draws in (`RasterFiltersFor` before the object's filter), so a path carrying both does
        // not come out differently on the screen and in the file.
        if (images is not null &&
            FilterRasteriser.RasteriseStrokeEffects(
                path, SelectionEngine.ToWorld(path), opacity,
                notes ?? new List<string>()) is { } stroked &&
            images.AddFilteredImage(stroked.Pixels, RasterEffectName(path)) is { } strokedResource)
        {
            ops.Add("q");
            ops.Add(FilterRasteriser.Placement(stroked, WorldToPage(path)) + " cm");
            ops.Add($"/{strokedResource} Do");
            ops.Add("Q");
            return;
        }

        // A filter is a raster operation, so an object that has one is drawn into pixels and the graph runs over
        // them - the same route the canvas takes, and the only one PDF has for a blur. Nothing else about the item
        // changes; a path with no filter, or one this build cannot carry, falls through to the vectors below.
        //
        // **The shape's box, the frame it is rasterised in and the answer are all read in world coordinates** -
        // the frame the canvas gives its filtered objects and the frame `FilterRasteriser` documents. Both are
        // asked of `SelectionEngine`, which states that frame once (the artboard origin composed on the OUTSIDE of
        // the enclosing group transforms, `ToWorld`); the exporter does not restate the composition, so the
        // drawing and the file cannot come to two answers about where a filtered object sits (#168). The picture
        // is then written into the page, whose content stream is the artboard's own frame, so the artboard origin
        // is taken back off on the way out - a translation of the answer, not a second composition of the frame,
        // and so not two rules that merely happen to agree for a translation.
        if (document is not null &&
            path.FilterId is { Length: > 0 } filterId &&
            document.FindFilter(filterId) is { } filter &&
            FilterRasteriser.Rasterise(
                path, filter, SelectionEngine.WorldBounds(new[] { path }),
                SelectionEngine.ToWorld(path), opacity,
                notes ?? new List<string>()) is { } picture &&
            images?.AddFilteredImage(picture.Pixels, filter.Name) is { } resource)
        {
            ops.Add("q");
            ops.Add(FilterRasteriser.Placement(picture, WorldToPage(path)) + " cm");
            ops.Add($"/{resource} Do");
            ops.Add("Q");
            return;
        }

        // Bake geometry into artboard space: anchors and both handles per node.
        var contours = new List<Contour>();
        foreach (SubPath sub in path.SubPaths)
        {
            if (sub.Nodes.Count < 2)
            {
                continue; // a single stray node paints nothing
            }

            var contour = new Contour { IsClosed = sub.IsClosed };
            foreach (PathNode node in sub.Nodes)
            {
                contour.Start ??= toDoc.Transform(node.Anchor); // first anchor = contour start
                contour.Points.Add(toDoc.Transform(node.Anchor));
                contour.InHandles.Add(toDoc.Transform(node.InHandle));
                contour.OutHandles.Add(toDoc.Transform(node.OutHandle));
            }

            contours.Add(contour);
        }

        if (contours.Count == 0)
        {
            return;
        }

        bool anyClosed = contours.Any(c => c.IsClosed);
        bool fillVisible = path.Fill.IsVisible && anyClosed;
        bool strokeVisible = path.Stroke.HasVisibleOutline;

        if (!fillVisible && !strokeVisible)
        {
            return;
        }

        // Scaling for stroke width. The group transform was baked into geometry;
        // the width must track the same scale to look right. For an affine with
        // determinant δ, the uniform scale factor is √|δ| (exact for rotation +
        // uniform scale, a documented approximation for shears — see class remarks).
        double det = toDoc.A * toDoc.D - toDoc.C * toDoc.B;
        double strokeScale = Math.Sqrt(Math.Abs(det));
        double width = Math.Max(0.0, path.Stroke.Width * strokeScale);

        // --- Style setup ---------------------------------------------------
        if (fillVisible)
        {
            // Paint with the ink values the file used when we have them.
            //
            // This was reverted once, on the strength of the page RMSE getting worse, and
            // that was the wrong call: the measurements disagreed because the importer was
            // only maintaining the ink state for k/K and not for CS/SCN, which this sample
            // uses for almost everything - so every stroked line was written with a stale
            // black. Rendering our CMYK export directly settled it: the page grey comes out
            // (189,188,188), exactly the reference, where the RGB path gives 178. A viewer
            // colour-manages DeviceCMYK; it does not apply the naive (1-c)(1-k) the model
            // uses, so converting at import loses something that cannot be recovered.
            ops.Add(ColorOperator(path.Fill.Color, path.SourceFillCmyk, stroke: false));
        }

        // The stroke's own graphics state - colour, cap, join, miter, dash - is set per stroke where the
        // strokes are drawn, below. It used to be set here, once, which is the same thing for a path with one
        // stroke and wrong for a path with several: those are all part of PDF's graphics state, so a stack
        // shares one and the last writer would win.

        var closed = contours.Where(c => c.IsClosed).ToList();
        var open = contours.Where(c => !c.IsClosed).ToList();

        // --- Fill (all contours; `f` implicitly closes open subpaths) --------
        // A gradient fill is a native PDF shading: the shape becomes the clip and `sh`
        // paints the ramp through it, so the gradient never escapes its shape. A gradient
        // PDF cannot represent falls back to the fill's flat colour (reported by
        // PdfShadingObjects).
        ShadingPaint? shading = null;
        string? fieldName = null;
        string? fieldMatrix = null;

        if (fillVisible && path.Fill.Gradient is { } fillGradient)
        {
            Rect2D localBox = path.BoundingBox();

            if (GradientField.NeedsSampling(fillGradient))
            {
                // No shading can express these - see GradientField - so the field is sampled and the
                // picture is drawn through the object's own placement, clipped to its outline: that
                // is what makes it a fill of THIS shape rather than a rectangle behind it. The
                // parallelogram comes from the placement matrix, so a rotated object is sampled in
                // the frame it is drawn in.
                if (images is not null)
                {
                    Point2D origin = toDoc.Transform(new Point2D(localBox.X, localBox.Y));
                    Point2D alongU = toDoc.Transform(new Point2D(localBox.Right, localBox.Y));
                    Point2D alongV = toDoc.Transform(new Point2D(localBox.X, localBox.Bottom));
                    Vector2D u = new(alongU.X - origin.X, alongU.Y - origin.Y);
                    Vector2D v = new(alongV.X - origin.X, alongV.Y - origin.Y);

                    fieldName = images.AddSampledGradient(fillGradient, origin, u, v);
                    fieldMatrix = $"{Num(u.X)} {Num(u.Y)} {Num(v.X)} {Num(v.Y)} " +
                                  $"{Num(origin.X)} {Num(origin.Y)}";
                }
            }
            else if (shadings is not null)
            {
                shading = shadings.NameFor(fillGradient, localBox, toDoc);
            }
        }

        if (fillVisible && contours.Count > 0)
        {
            if (shading is { } paint)
            {
                ops.Add("q");
                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(path.Fill.Color.A * opacity)} gs");
                }

                WriteContours(ops, contours);
                ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "W* n" : "W n");
                if (paint.Matrix is not null)
                {
                    ops.Add($"{paint.Matrix} cm");
                }

                ops.Add($"/{paint.ResourceName} sh");
                ops.Add("Q");
            }
            else if (fieldName is { } field)
            {
                ops.Add("q");
                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(path.Fill.Color.A * opacity)} gs");
                }

                WriteContours(ops, contours);
                ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "W* n" : "W n");
                ops.Add($"{fieldMatrix} cm");
                ops.Add($"/{field} Do");
                ops.Add("Q");
            }
            else
            {
                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(path.Fill.Color.A * opacity)} gs");
                }

                WriteContours(ops, contours);
                ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "f*" : "f");
            }
        }

        // --- Hatch: the clipped line art, not a native pattern ---------------
        // A PDF has no need to carry a hatch as a pattern. The lines clipped to the path **are** what the file
        // should contain, and they are the same segments the canvas draws, so the two cannot disagree about the
        // shape - which is the whole reason the generator does the clipping rather than the renderer.
        if (fillVisible && path.Fill.Hatch is { } hatch && contours.Count > 0)
        {
            IReadOnlyList<HatchSegment> hatchSegments = HatchGenerator.Segments(
                hatch, PathFlattener.Flatten(path), path.Fill.Rule, path.BoundingBox());

            if (hatchSegments.Count > 0)
            {
                ops.Add("q");
                WriteContours(ops, contours);
                ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "W* n" : "W n");

                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(path.Stroke.Color.A * opacity)} gs");
                }

                ops.Add(ColorOperator(path.Stroke.Color, path.SourceStrokeCmyk, stroke: true));

                // One stroke per family rather than per line: the width and the dash belong to the family, and a
                // hatch of two hundred lines should be two hundred `l` operators, not two hundred `S` operators.
                foreach (HatchLineSpec family in hatch.Lines)
                {
                    List<HatchSegment> familySegments = hatchSegments.Where(s => s.Line == family).ToList();
                    if (familySegments.Count == 0)
                    {
                        continue;
                    }

                    ops.Add($"{Num(Math.Max(0.0, family.Width * strokeScale))} w");
                    if (!family.Dash.IsEmpty)
                    {
                        string array = string.Join(
                            ' ', family.Dash.Segments.Select(v => Num(Math.Max(0.0, v * strokeScale))));
                        ops.Add($"[{array}] {Num(family.Dash.Offset * strokeScale)} d");
                    }

                    foreach (HatchSegment segment in familySegments)
                    {
                        Point2D a = toDoc.Transform(segment.A);
                        Point2D b = toDoc.Transform(segment.B);
                        ops.Add($"{Num(a.X)} {Num(a.Y)} m {Num(b.X)} {Num(b.Y)} l");
                    }

                    ops.Add("S");
                    ops.Add("[] 0 d");
                }

                ops.Add("Q");
            }
        }

        // --- Strokes, bottom to top -----------------------------------------
        // Each stroke states its own colour, cap, join, miter limit, dash and width before it is drawn. Those
        // are all PDF graphics state, so a stack of strokes shares one - which means every stroke has to say
        // what it is, including "no dash", or it is drawn with whatever the stroke before it set.
        foreach (StrokeSpec stroke in path.Strokes)
        {
            if (!stroke.HasVisibleOutline)
            {
                continue;
            }

            // The stroke's own opacity, multiplied with the item's - the same product the canvas paints, because
            // an object at 50% whose second stroke is at 50% is a quarter-covered pixel. Read through
            // `EffectiveOpacity` so an unstated opacity is opaque here and in the painter, rather than the two
            // renderers each deciding for themselves what the absence means.
            double strokeOpacity = opacity * stroke.EffectiveOpacity;

            // **A stroke's blend is its own, and it is entered and left around that stroke alone.** The stack is
            // why: a blend belongs to one stroke of it, so the state has to be taken after the stroke below is
            // painted and given back before the one above starts - otherwise the whole stack blends, or the
            // strokes after it do. `q`/`Q` is the only way to give a graphics state back, because `gs` writes
            // parameters rather than replacing the state wholesale.
            //
            // Normal is PDF's default, so it writes nothing: a stroke that states no blend, or states Normal,
            // produces byte-for-byte the file it did before this existed.
            string? strokeBlend = blendStates.Gs(stroke.Blend);

            // **The art the brush maps, written over the stroke the pen drew.** A plan is a width or an outline
            // and an art brush is neither, so the placements are resolved from the shared seam and each piece is
            // written as the asset's own artwork under the placement's transform. The canvas resolves the same
            // seam, which is what stops the screen and the file disagreeing about where the art sits.
            //
            // **The transform is written, not baked into the artwork**, and that is what carries a turned
            // **raster** placement: an `ImageItem` holds an axis-aligned placement and two mirrors and no
            // rotation, so a piece turned to a tangent is a matrix the file has to state. That matrix is the
            // image's own `cm`, and a vector asset's frame is the same combination applied to its coordinates -
            // so both kinds take one route.
            //
            // The depth cap matches the canvas's, for its reason: an art brush names an item by id, that item may
            // be a path with an art brush of its own, and a cycle through two brushes would otherwise recurse
            // forever.
            //
            // **A pattern brush's tiles are written through here too.** They are artwork placed along the path in
            // the same sense, and `PlacedArt.Resolve` answers for both kinds - so one route writes both, and the
            // screen and the file cannot disagree about where a tile sits.
            //
            // **A scatter brush's copies are written through here as well**, each under its own placement's matrix
            // and at its own opacity: a copy's turn, size and offset are the placement, which is the matrix a reader
            // applies, so the scatter needs no third writing route either.
            void WriteStrokeArt()
            {
                if (document is null || artDepth >= MaxArtDepth ||
                    stroke.Brush is not { } brush || (!brush.IsArt && !brush.IsPattern && !brush.IsScatter))
                {
                    return;
                }

                foreach (PlacedArt piece in PlacedArt.Resolve(document, path, brush, strokeScale, stroke.Pen))
                {
                    // The placement is the frame the asset is **written in**, not a `cm` wrapped around its own
                    // coordinates, because that is how this writer states every other item's frame: a path's
                    // anchors are written transformed, and an image's placement is written as its matrix. It also
                    // means the turn is on the numbers a renderer reads, rather than on a stack it has to follow.
                    AffineTransform placed = toDoc.Compose(piece.Placement.Transform);

                    ops.Add("q");
                    PaintItem(ops, piece.Asset, placed, strokeOpacity * piece.Opacity, alphaStates, blendStates, embedder, images, shadings,
                        document, notes, artDepth + 1);
                    ops.Add("Q");
                }
            }

            // What this stroke is drawn as is decided in one place, in the model, so the canvas and this writer
            // cannot come to different conclusions about a stroke that varies along its length.
            StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke, strokeScale);
            double strokeWidth = plan.Width;

            // A stroke that varies in width is drawn as its **outline filled**, not stroked: PDF has one width
            // per stroke, so there is no variable-width stroke to write. Filling the region the stroke covers is
            // not an approximation of it - it is the same region, and the outline is what the canvas fills too.
            //
            // Note this sets the **fill** colour, because the outline is filled. The stroke's own colour is what
            // it is filled with, and none of the stroke graphics state below applies.
            if (plan.IsOutline)
            {
                if (strokeBlend is not null)
                {
                    ops.Add("q");
                    ops.Add(strokeBlend);
                }

                ops.Add(ColorOperator(
                    stroke.Color,
                    ReferenceEquals(stroke, path.Strokes[0]) ? path.SourceStrokeCmyk : null,
                    stroke: false));
                WriteOutline(ops, plan.Outlines, toDoc, stroke, strokeOpacity, alphaStates, plan.Paints);
                WriteStrokeArt();

                if (strokeBlend is not null)
                {
                    ops.Add("Q");
                }

                continue;
            }

            if (strokeBlend is not null)
            {
                ops.Add("q");
                ops.Add(strokeBlend);
            }

            // The original ink values belong to the item rather than to a stroke, so they describe the first
            // one. Writing them for every stroke would paint the later ones in a colour they never had.
            ops.Add(ColorOperator(
                stroke.Color,
                ReferenceEquals(stroke, path.Strokes[0]) ? path.SourceStrokeCmyk : null,
                stroke: true));
            ops.Add($"{CapToPdf(stroke.Cap)} J");
            ops.Add($"{JoinToPdf(stroke.Join)} j");
            if (stroke.Join == StrokeJoin.Miter)
            {
                ops.Add($"{Num(stroke.MiterLimit)} M");
            }

            if (!stroke.Dash.IsEmpty)
            {
                string array = string.Join(' ', stroke.Dash.Segments.Select(v => Num(Math.Max(0.0, v * strokeScale))));
                ops.Add($"[{array}] {Num(stroke.Dash.Offset * strokeScale)} d");
            }
            else
            {
                ops.Add("[] 0 d");
            }

            if (alphaStates.HasTransparency)
            {
                ops.Add($"{alphaStates.NameFor(stroke.Color.A * strokeOpacity)} gs");
            }

            // --- Stroke: honour Inside/Outside by clipping ------------------
            // PDF has no stroke alignment, so an aligned stroke is drawn at double
            // width and clipped to the inside or outside of the path.
            bool aligned = stroke.Alignment != StrokeAlignment.Center && closed.Count > 0;
            if (closed.Count > 0)
            {
                if (!aligned)
                {
                    ops.Add($"{Num(strokeWidth)} w");
                    WriteContours(ops, closed);
                    ops.Add("S");
                }
                else
                {
                    ops.Add("q");
                    if (stroke.Alignment == StrokeAlignment.Inside)
                    {
                        WriteContours(ops, closed);
                        ops.Add(path.Fill.Rule == FillRule.EvenOdd ? "W* n" : "W n");
                    }
                    else
                    {
                        // Outside = everything except the path interior (even-odd).
                        ops.Add($"{Num(-10000)} {Num(-10000)} m {Num(20000)} {Num(-10000)} l " +
                                $"{Num(20000)} {Num(20000)} l {Num(-10000)} {Num(20000)} l h");
                        WriteContours(ops, closed);
                        ops.Add("W* n");
                    }

                    ops.Add($"{Num(strokeWidth * 2)} w");
                    WriteContours(ops, closed);
                    ops.Add("S");
                    ops.Add("Q");
                }
            }

            // --- Open contours: always centre-stroked ----------------------
            if (open.Count > 0)
            {
                ops.Add($"{Num(strokeWidth)} w");
                WriteContours(ops, open);
                ops.Add("S");
            }

            WriteStrokeArt();

            if (strokeBlend is not null)
            {
                ops.Add("Q");
            }
        }
    }

    /// <summary>
    /// The name a stroke's raster effects are reported under in the export notes.
    ///
    /// The first stroke that carries any is the one the whole path is drawn with - the same one
    /// <see cref="FilterRasteriser.RasteriseStrokeEffects"/> uses - so the note names the effect that decided the
    /// picture rather than one of the others on the stack.
    /// </summary>
    private static string RasterEffectName(PathItem path)
    {
        foreach (StrokeSpec stroke in path.Strokes)
        {
            if (stroke.AllRasterEffects is { Count: > 0 } effects)
            {
                return $"raster effect {effects[0].Kind}";
            }
        }

        return "raster effect";
    }

    /// <summary>
    /// World coordinates as this page's content stream states them: the artboard origin taken back off.
    ///
    /// A page is one artboard whose MediaBox is that artboard's own rectangle, so the content stream never carries
    /// the origin - a page's grid position lives in the canvas's layout and nowhere in the file. An item's world
    /// frame (<see cref="SelectionEngine.ToWorld"/>) therefore differs from the page's frame by exactly the
    /// artboard origin, and this is that difference and nothing else. It is a translation, so unlike two orders of
    /// the same composition it cannot disagree with a group transform - which is what #168 was: the offset
    /// composed inside the item frame, so a rotation turned it instead of moving it.
    ///
    /// The enclosing group frames are deliberately **not** a parameter. A frame that is "the item's own
    /// coordinates under every enclosing group transform" is <see cref="SelectionEngine.ToWorld"/>'s business, and
    /// a second caller deriving it here is the defect this method exists to leave behind.
    /// </summary>
    private static AffineTransform WorldToPage(PathItem path)
    {
        Vector2D offset = path.ArtboardOffset();
        return AffineTransform.CreateTranslation(-offset.X, -offset.Y);
    }

    private static void CollectAlphas(
        CadDocument document, Artboard artboard, double opacity, List<double> alphas, int artDepth)
    {
        if (!artboard.IsVisible)
        {
            return;
        }

        foreach (Layer layer in artboard.Layers)
        {
            if (!layer.IsEffectivelyVisible)
            {
                continue;
            }

            foreach (LayerItem item in layer.Children)
            {
                CollectItemAlphas(document, item, opacity * layer.Opacity, alphas, artDepth);
            }
        }
    }

    private static void CollectItemAlphas(
        CadDocument document, LayerItem item, double opacity, List<double> alphas, int artDepth)
    {
        if (!item.IsEffectivelyVisible())
        {
            return;
        }

        switch (item)
        {
            case PathItem path:
                alphas.Add(path.Fill.Color.A * opacity * path.Opacity);

                // **Every** stroke, not the bottom one, and not a sample of them: the alpha states are built from
                // this list, so an alpha no stroke contributed to does not exist as a name. A stack whose top
                // stroke is translucent while the bottom one is opaque would otherwise find `HasTransparency`
                // false and write no `gs` at all - the stroke drawn at full strength while the exporter believed
                // it had honoured the opacity. Reading `path.Stroke` here is precisely the "first stroke only"
                // mistake the stack was introduced to end, one layer below where it was being looked for.
                foreach (StrokeSpec stroke in path.Strokes)
                {
                    alphas.Add(stroke.Color.A * opacity * path.Opacity * stroke.EffectiveOpacity);
                }

                // **The artwork a brush places is alpha-bearing too.** A piece is painted at the stroke's opacity
                // times the piece's own, and the asset's own colours multiply in inside it - so every alpha that
                // painting will ask for has to be in this list, or the state it asks for does not exist. Without
                // this a scatter brush's translucent copies found `HasTransparency` false and were written fully
                // opaque: the file quietly disagreeing with the canvas, which is exactly the defect the fidelity
                // rule exists to catch. The art and pattern kinds are walked the same way, because a translucent
                // asset mapped by either of them asks for an alpha in precisely the same sense.
                if (artDepth < MaxArtDepth)
                {
                    foreach (StrokeSpec stroke in path.Strokes)
                    {
                        if (stroke.Brush is not { } brush ||
                            (!brush.IsArt && !brush.IsPattern && !brush.IsScatter))
                        {
                            continue;
                        }

                        foreach (PlacedArt piece in PlacedArt.Resolve(document, path, brush, 1.0, stroke.Pen))
                        {
                            CollectItemAlphas(
                                document,
                                piece.Asset,
                                opacity * path.Opacity * stroke.EffectiveOpacity * piece.Opacity,
                                alphas,
                                artDepth + 1);
                        }
                    }
                }

                break;
            case TextItem text:
                // Every colour the block draws with, not only the block's own: a run may carry its own, and a
                // semi-transparent one needs its alpha state to exist even when the block's colour is opaque.
                alphas.Add(text.Color.A * opacity);
                foreach (TextRun run in text.Runs)
                {
                    if (run.Color is { } colour && colour.A != text.Color.A)
                    {
                        alphas.Add(colour.A * opacity);
                    }
                }

                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    CollectItemAlphas(document, child, opacity * group.Opacity, alphas, artDepth);
                }

                break;
        }
    }

    /// <summary>
    /// Every text block on an artboard, for the font scan.
    ///
    /// This is the one text walk left that is not <see cref="PaintItem"/>: usage is collected before anything is
    /// painted, and a font's programme has to be embedded whether or not the block ends up painted.
    /// </summary>
    private static IEnumerable<TextItem> AllTextItems(Artboard artboard)
    {
        foreach (Layer layer in artboard.Layers)
        {
            foreach (LayerItem item in layer.Children)
            {
                foreach (TextItem text in FlattenText(item))
                {
                    yield return text;
                }
            }
        }
    }

    private static IEnumerable<TextItem> FlattenText(LayerItem item)
    {
        switch (item)
        {
            case TextItem text:
                yield return text;
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    foreach (TextItem t in FlattenText(child))
                    {
                        yield return t;
                    }
                }

                break;
        }
    }

    private static void ScanText(Artboard artboard, Dictionary<FontKey, HashSet<int>> usage,
        List<EmbeddedFont> embeddedFonts)
    {
        foreach (TextItem text in AllTextItems(artboard))
        {
            foreach (TextRun run in text.Runs)
            {
                // Imported runs that carry their original programme are emitted
                // verbatim, so they need no bundled substitute.
                if (run.EmbeddedFont is { } embedded && run.RawCodes is { Length: > 0 })
                {
                    if (!embeddedFonts.Contains(embedded))
                    {
                        embeddedFonts.Add(embedded);
                    }

                    continue;
                }

                var key = new FontKey(run.FontFamily, run.Bold, run.Italic);
                if (!usage.TryGetValue(key, out HashSet<int>? codes))
                {
                    usage[key] = codes = new HashSet<int>();
                }

                foreach (char ch in run.Text)
                {
                    if (ch != '\n')
                    {
                        codes.Add(ch);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Writes a block whose runs all carry their own embedded programme as a <em>single</em>
    /// text object, with each run's trailing gap as a <c>TJ</c> adjustment.
    ///
    /// The reason is what an extractor sees. Runs written as separate <c>BT … ET</c> objects
    /// are separate text objects, and a reader starts a new word at each; the
    /// Transparency Guide, which letter-spaces a heading by drawing one show operation per
    /// pair of glyphs, came out as "IN TR OD UC TI ON" where the file says "INTRODUCTION".
    /// Inside one object the pen simply continues, and an adjustment puts the room back
    /// between the glyphs rather than after them — which is exactly what the file did.
    ///
    /// Returns false when the block needs the general path: substituted fonts (whose
    /// horizontal scaling is per run), explicit newlines, or runs the pass-through cannot
    /// describe.
    /// </summary>
    private static bool TryWriteRunsAsOneTextObject(
        List<string> ops, TextItem text, PdfFontEmbedder embedder, PdfAlphaStates alphaStates,
        AffineTransform toDoc)
    {
        if (text.Runs.Count == 0)
        {
            return true;
        }

        foreach (TextRun run in text.Runs)
        {
            if (run.EmbeddedFont is null || run.RawCodes is not { Length: > 0 } ||
                run.Text.Contains('\n'))
            {
                return false;
            }
        }

        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);

        // The baseline of the first run: the block's top-left plus one ascent down the
        // text's own up axis. Later runs continue that baseline, so only this one needs a
        // matrix.
        //
        // The mirror is part of that walk rather than a rule beside it: the canvas places a block
        // as `R(S(p - origin)) + origin` (CanvasWorkspace.PaintText), so a vertical flip sends the
        // ascent up the block's own y axis and the baseline lands on the other side of the origin.
        // One sign, read through the one member that states it.
        TextRun first = text.Runs[0];
        double firstAscent = first.PlacedAscentEm > 0
            ? first.PlacedAscentEm
            : first.EmbeddedFont!.Ascent / 1000.0;
        double depth = firstAscent * first.FontSize * text.YSign;
        double ox = text.Origin.X - (sin * depth);
        double oy = text.Origin.Y + (cos * depth);

        // The fill the first run draws with. Setting it before `BT` keeps a block of one colour byte-for-byte
        // what it was; a run that states another colour changes it inside the object below.
        ColorRgb colour = text.ColourOf(first);
        ops.Add(ColorOperator(colour, RunCmyk(text, first), stroke: false));
        if (alphaStates.HasTransparency)
        {
            ops.Add($"{alphaStates.NameFor(colour.A)} gs");
        }

        ops.Add("BT");
        ops.Add(TextMatrix(cos, sin, sin, -cos, ox, oy, toDoc, text.XSign, text.YSign));

        string? current = null;
        double currentSize = 0;

        foreach (TextRun run in text.Runs)
        {
            // A run may carry its own colour, and this path deliberately writes the whole block as ONE text
            // object - so the change has to be written *inside* it, between the runs. Splitting here instead
            // would undo the reason the path exists: one object is what makes an extractor read a
            // letter-spaced heading as one word.
            ColorRgb runColour = text.ColourOf(run);
            if (runColour != colour)
            {
                ops.Add(ColorOperator(runColour, RunCmyk(text, run), stroke: false));
                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(runColour.A)} gs");
                }

                colour = runColour;
            }

            string resource = embedder.NameForEmbedded(run.EmbeddedFont!);
            if (resource != current || Math.Abs(run.FontSize - currentSize) > 1e-9)
            {
                ops.Add($"{resource} {Num(run.FontSize)} Tf");
                current = resource;
                currentSize = run.FontSize;
            }

            // The room this run's glyphs are spread over, as character spacing.
            //
            // It goes on every glyph rather than in one lump at the end. A lump is what the
            // file avoided in the first place: the Transparency Guide spaces its headings
            // with an adjustment between every pair of glyphs, and a single 0.4 em jump after
            // two of them is wide enough that a reader calls it a word break — which is why
            // the page still read "IN TR OD UC TI ON" even once the whole block was one text
            // object. Spread evenly, the spacing looks like the letter-spacing it is.
            int glyphs = run.EmbeddedFont!.Composite
                ? Math.Max(1, run.RawCodes!.Length / 2)
                : Math.Max(1, run.RawCodes!.Length);
            double spacing = run.GapAfter / glyphs;

            if (Math.Abs(spacing) > 1e-9)
            {
                ops.Add($"{Num(spacing)} Tc");
            }

            var hex = new StringBuilder();
            foreach (char code in run.RawCodes!)
            {
                hex.Append(((int)code & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
            }

            ops.Add($"[<{hex}>] TJ");

            if (Math.Abs(spacing) > 1e-9)
            {
                ops.Add("0 Tc");
            }
        }

        ops.Add("ET");
        return true;
    }

    /// <summary>
    /// The <c>Tm</c> for one run: the block's own text matrix taken through <paramref name="toDoc"/>, the frame the
    /// block is placed in.
    ///
    /// A text matrix is a general 2×3 affine map — the same six numbers a <c>cm</c> writes — so an enclosing group
    /// transform composes into it exactly, with nothing dropped: a rotation, a mirror or a non-uniform scale in the
    /// group all land in the six coefficients, and the glyph space, the baseline and the run's advance move with
    /// them. The font size is multiplied in *after* <c>Tm</c> by PDF, so scaling the frame scales the face, which is
    /// what "the group makes this text twice as big" means (issue #164).
    ///
    /// <paramref name="xSign"/> and <paramref name="ySign"/> are the block's own mirror (-1 for a flipped axis), and
    /// they are the same kind of number: a mirror **is** a scale of the text space, so it is a sign on the two
    /// coefficients of the axis it flips and nothing else is needed. The canvas composes it identically —
    /// <c>R(S(p - origin)) + origin</c> in <c>CanvasWorkspace.PaintText</c> — so a flipped block leaves the file
    /// flipped, and the sign sits inside the group frame rather than beside it, which is why a mirrored block in a
    /// transformed group is turned *and* flipped (issue #171). Both signs default to 1, so an unflipped block's
    /// numbers are the ones it always had.
    ///
    /// Composing here rather than pushing a <c>cm</c> and restoring it keeps each text object self-contained, which
    /// is how the rest of this exporter writes geometry. With no enclosing group the composed matrix is the
    /// original one to the last bit — <c>Identity.Compose</c> is exact — so an untransformed document's operators
    /// are unchanged.
    /// </summary>
    private static string TextMatrix(double a, double b, double c, double d, double e, double f,
        AffineTransform toDoc, double xSign = 1.0, double ySign = 1.0)
    {
        AffineTransform placed = toDoc.Compose(
            new AffineTransform(a * xSign, b * xSign, c * ySign, d * ySign, e, f));
        return $"{Num(placed.A)} {Num(placed.B)} {Num(placed.C)} {Num(placed.D)} " +
               $"{Num(placed.E)} {Num(placed.F)} Tm";
    }

    /// <summary>
    /// Writes a block's runs as separate text objects, one per run, each with its own
    /// matrix. Used for everything the single-object path cannot describe.
    /// </summary>
    private static void WriteText(List<string> ops, TextItem text, PdfFontEmbedder embedder,
        PdfAlphaStates alphaStates, AffineTransform toDoc, List<string>? notes = null)
    {
        // **A vertical column cannot go through the one-text-object path.** It emits a single `Tm` for the whole
        // block, and a column's characters are not on one baseline - they are separated down the page. The per-run
        // loop below places each one from the layout's own glyph positions instead.
        bool column = text.WritingMode != TextWritingMode.HorizontalTb;

        // **Nor can per-character positions of either axis.** `TryWriteRunsAsOneTextObject` writes one `Tm` and one
        // show for the block's text, which has no way to state that a character sits off the baseline or somewhere of
        // its own along the line - and a `y`/`dy` or `x`/`dx` list is exactly that statement. Those lists come from
        // the SVG reader, which never produces an embedded programme, so the reachable case is a run drawn with a
        // face from this machine.
        bool offsetGlyphs = text.Runs.Any(
            r => r.PositionOffsets is { Length: > 0 } || r.InlineOffsets is { Length: > 0 });
        if (!column && !offsetGlyphs && TryWriteRunsAsOneTextObject(ops, text, embedder, alphaStates, toDoc))
        {
            return;
        }

        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);
        double y = text.Origin.Y;

        // How far along the text's own baseline the next run starts.
        //
        // Every run is emitted as its own BT/ET with an explicit Tm, and that Tm used to be
        // the block's origin for each of them — so a block with more than one run drew every
        // run at the same place, one on top of the other. A glyph-per-show-operation file
        // arrives with runs to merge, so this is not a rare shape: the doubled text on the
        // Transparency Guide's page 6 was exactly it.
        double pen = 0.0;

        foreach (TextRun run in text.Runs)
        {
            EmbeddedFont? embeddedFont = run.EmbeddedFont;
            bool embeddedRun = embeddedFont is not null && run.RawCodes is { Length: > 0 };
            var key = new FontKey(run.FontFamily, run.Bold, run.Italic);
            TrueTypeFont? font = embeddedRun ? null : embedder.FontFor(key);
            string resource = embeddedRun ? embedder.NameForEmbedded(embeddedFont!) : embedder.NameFor(key);

            var hex = new StringBuilder();
            // The model stores the block's top-left; PDF places text on the
            // baseline, so the baseline sits one ascent down the (rotated) text
            // axis. Ascent is per run.
            //
            // An imported run carries the ascent it was placed with, and that is the one to
            // subtract. Working it out again from the face gets a different number for a
            // substituted font — the importer used the standard-font table, this would use
            // the installed TrueType's ascender — and the text lands beside where it was.
            double ascent = run.PlacedAscentEm > 0
                ? run.PlacedAscentEm
                : embeddedRun
                    ? embeddedFont!.Ascent / 1000.0
                    : font is null || font.UnitsPerEm == 0 ? VCCad.Core.Text.TextMeasurement.TypicalAscentEm : (double)font.Ascender / font.UnitsPerEm;
            double targetAdvance = embeddedRun ? 0.0 : run.AdvanceWidth ?? 0.0;
            bool multiLine = !embeddedRun && run.Text.Contains('\n');
            double lineAdvance = 0;
            double yOffset = y - text.Origin.Y; // model units down the block

            void Flush(double? originX = null, double? originY = null)
            {
                if (hex.Length == 0)
                {
                    return;
                }

                double sx = !multiLine && targetAdvance > 0 && lineAdvance > 0
                    ? targetAdvance / lineAdvance
                    : 1.0;

                // Baseline origin in model space: top-left + R(rot)*(0, y + ascent),
                // moved along the baseline by everything already set on this line.
                //
                // Both movements run along the block's **own** axes, so a mirror reverses them: a
                // vertical flip sends the line stack and the ascent up the block's y axis, a horizontal
                // one sends the pen back along its x axis. Reading the signs off the block here keeps
                // the mirror the block's, rather than a second rule about this path (issue #171).
                double depth = (yOffset + (ascent * run.FontSize)) * text.YSign;
                double penX = pen * text.XSign;

                // **A column supplies its own origin.** Every other block's placement is the block's origin
                // advanced along its own axes by the run and the pen; a column's is where the layout says that
                // character goes, because its characters are separated down the page rather than along a line.
                double ox = originX ?? (text.Origin.X - (sin * depth) + (cos * penX));
                double oy = originY ?? (text.Origin.Y + (cos * depth) + (sin * penX));

                // The run's own colour, through the one member that answers it. A block may hold several
                // colours, and setting the block's for every run is what painted a two-coloured line in one.
                ColorRgb runColour = text.ColourOf(run);
                ops.Add(ColorOperator(runColour, RunCmyk(text, run), stroke: false));
                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(runColour.A)} gs");
                }

                ops.Add("BT");
                ops.Add($"{resource} {Num(run.FontSize)} Tf");
                // R(rot) with a y-flip for upright glyphs, times the advance scale, taken through the block's
                // accumulated frame - the group transform - and carrying the block's own mirror.
                ops.Add(TextMatrix(sx * cos, sx * sin, sin, -cos, ox, oy, toDoc, text.XSign, text.YSign));
                ops.Add($"<{hex}> Tj");
                ops.Add("ET");
                hex.Clear();
            }

            // A character with no glyph in the face that resolved is dropped below; counted here so the run can be
            // declared rather than silently shortened.
            int undrawn = 0;

            if (embeddedRun)
            {
                // Pass-through: write the original glyph codes so the embedded
                // programme renders exactly as in the source document.
                foreach (char code in run.RawCodes!)
                {
                    hex.Append(((int)code & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                // Break the emitted lines where the editor breaks them.
                //
                // Previously export only honoured explicit newlines, so a block wrapped
                // into a frame came out on the page as one long line — the exported
                // document was not the document that was on screen. The wrap rule now
                // lives in TextWrapping and is shared with the model and the canvas.
                List<TextWrapping.LineRange> displayLines = TextWrapping.Lines(text);
                int charBase = FlattenedOffsetOf(text, run);
                double lineHeight = run.FontSize * text.LineSpacing;

                // A column's characters are placed one by one where the layout puts them, and so are the characters
                // of a run that states its own per-character positions - from the same source the canvas draws from,
                // so the page and the screen cannot disagree about where a list put a character.
                bool perCharacter = column ||
                    run.PositionOffsets is { Length: > 0 } ||
                    run.InlineOffsets is { Length: > 0 };
                VCCad.Core.Text.TextLayout glyphLayout = perCharacter
                    ? VCCad.Core.Text.TextLayoutEngine.Compute(text)
                    : VCCad.Core.Text.TextLayout.Empty;

                for (int li = 0; li < displayLines.Count; li++)
                {
                    TextWrapping.LineRange line = displayLines[li];
                    int from = Math.Max(line.Start, charBase);
                    int to = Math.Min(line.Start + line.Length, charBase + run.Text.Length);
                    if (to <= from)
                    {
                        continue;
                    }

                    hex.Clear();
                    lineAdvance = 0;

                    for (int i = from; i < to; i++)
                    {
                        char ch = run.Text[i - charBase];
                        if (ch == '\n')
                        {
                            continue;
                        }

                        int gid = font!.GlyphFor(ch);
                        if (gid == 0)
                        {
                            // **A character the face cannot draw is a loss worth declaring.** Skipping it silently
                            // is what left a blank page for a script the resolved face does not cover: the canvas
                            // drew it (Skia shapes, and falls back), the page drew nothing, and nothing said so.
                            undrawn++;
                            continue;
                        }

                        hex.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
                        lineAdvance += font.Advance1000(gid) * run.FontSize / 1000.0;

                        // **One show operation per character when the layout places them.** The line-level flush
                        // below stays for a run whose characters share a baseline; here each character is placed
                        // where the layout says, which is the same source the canvas draws from - `GlyphBox.X` across,
                        // `GlyphBox.Y` down the column.
                        if (perCharacter && i < glyphLayout.Glyphs.Count)
                        {
                            VCCad.Core.Text.GlyphBox glyph = glyphLayout.Glyphs[i];
                            Flush(text.Origin.X + glyph.X, text.Origin.Y + glyph.Y);
                        }
                    }

                    // The display line's own top, including the paragraph leading that
                    // each explicit break contributes.
                    yOffset = (li * lineHeight) + (NewlinesBefore(text, displayLines, li) * text.ParagraphSpacing);
                    Flush();
                }
            }

            // **Declare a run the resolved face could not draw.** A person whose document is written in a script
            // the chosen face does not cover watched their text disappear into a blank page and was told nothing.
            if (undrawn > 0)
            {
                string sample = run.Text.Length <= 40 ? run.Text : run.Text[..40] + "\u2026";
                notes?.Add(
                    $"{undrawn} of {run.Text.Length} characters in '{sample}' have no glyph in "
                    + $"'{run.FontFamily}' and were not drawn");
            }

            Flush();

            // The pen moves by what this run takes to set. For a wrapped run the lines are
            // stacked rather than continued, so only the last line's width carries forward
            // — which is what a person reading the block expects of the next run.
            pen += multiLine
                ? LastLineAdvance(text, run)
                : run.AdvanceWidth ?? 0.0;
        }
    }

    /// <summary>
    /// How wide the last display line of a wrapped run is, in model units, so the run
    /// after it starts where that line ended rather than at the run's beginning.
    /// </summary>
    private static double LastLineAdvance(TextItem text, TextRun run)
    {
        List<TextWrapping.LineRange> lines = TextWrapping.Lines(text);
        if (lines.Count == 0)
        {
            return run.AdvanceWidth ?? 0.0;
        }

        int charBase = FlattenedOffsetOf(text, run);
        TextWrapping.LineRange last = lines[^1];
        int from = Math.Max(last.Start, charBase);
        int to = Math.Min(last.Start + last.Length, charBase + run.Text.Length);
        if (to <= from)
        {
            return 0.0;
        }

        // Measured with the same source the model uses, so the pen matches the layout.
        return VCCad.Core.Text.TextMeasurement.AdvanceOf(run, to - charBase)
             - VCCad.Core.Text.TextMeasurement.AdvanceOf(run, from - charBase);
    }

    /// <summary>Where a run begins in the block's flattened text.</summary>
    private static int FlattenedOffsetOf(TextItem text, TextRun run)
    {
        int offset = 0;
        foreach (TextRun candidate in text.Runs)
        {
            if (ReferenceEquals(candidate, run))
            {
                return offset;
            }

            offset += candidate.Text.Length;
        }

        return offset;
    }

    /// <summary>How many explicit line breaks fall before a display line.</summary>
    private static int NewlinesBefore(TextItem text, List<TextWrapping.LineRange> lines, int index)
    {
        (string flat, _) = TextWrapping.Flatten(text);
        int start = lines[index].Start;
        int count = 0;

        for (int i = 0; i < start && i < flat.Length; i++)
        {
            if (flat[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Writes the outlines a stroke covers, filled.
    ///
    /// PDF has one width per stroke, so a stroke that varies along its length cannot be written as a stroke. It
    /// is written as what it *is*: the region filled, with the **nonzero** rule, which is what makes the
    /// overlapping corners of a mitred outline fill rather than cancel.
    ///
    /// The outlines come from <see cref="StrokeOutlineBuilder"/> already scaled, so this only places and fills
    /// them - the geometry is not decided here, because the canvas has to decide it the same way.
    ///
    /// <paramref name="paints"/>, when the plan carries it, is the colour of each loop - a bristle brush's own
    /// bristles. Each loop is then written under its own colour rather than all of them under the stroke's, which
    /// is what stops a colour jitter being recorded in the model and dropped at the page.
    /// </summary>
    private static void WriteOutline(
        List<string> ops,
        IReadOnlyList<IReadOnlyList<Point2D>> loops,
        AffineTransform toDoc,
        StrokeSpec stroke,
        double opacity,
        PdfAlphaStates? alphaStates = null,
        IReadOnlyList<ColorRgb>? paints = null)
    {
        if (loops.Count == 0)
        {
            return;
        }

        if (alphaStates is { HasTransparency: true })
        {
            ops.Add($"{alphaStates.NameFor(stroke.Color.A * opacity)} gs");
        }

        for (int i = 0; i < loops.Count; i++)
        {
            if (paints is not null)
            {
                ColorRgb paint = i < paints.Count ? paints[i] : stroke.Color;
                ops.Add(ColorOperator(paint, null, stroke: false));
            }

            IReadOnlyList<Point2D> loop = loops[i];
            for (int p = 0; p < loop.Count; p++)
            {
                Point2D point = toDoc.Transform(loop[p]);
                ops.Add($"{Num(point.X)} {Num(point.Y)} {(p == 0 ? "m" : "l")}");
            }

            ops.Add("h");

            // One fill per loop when the loops differ in colour, and one for the whole region when they do not -
            // which is what keeps the overlapping mitred corners of an ordinary outline filling as one shape.
            if (paints is not null)
            {
                ops.Add("f");
            }
        }

        if (paints is null)
        {
            ops.Add("f");
        }
    }

    /// <summary>Writes the raw geometry of several contours into the current path.</summary>
    private static void WriteContours(List<string> ops, List<Contour> contours)
    {
        foreach (Contour c in contours)
        {
            ops.Add($"{Num(c.Start!.Value.X)} {Num(c.Start.Value.Y)} m");
            for (int i = 0; i < c.Points.Count - 1; i++)
            {
                Point2D start = c.Points[i];
                Point2D p1 = c.OutHandles[i];
                Point2D p2 = c.InHandles[i + 1];
                Point2D end = c.Points[i + 1];
                WriteSegment(ops, start, p1, p2, end);
            }

            if (c.IsClosed)
            {
                // Final contour segment closes back to the start node.
                Point2D start = c.Points[^1];
                Point2D p1 = c.OutHandles[^1];
                Point2D p2 = c.InHandles[0];
                Point2D end = c.Start!.Value;
                WriteSegment(ops, start, p1, p2, end);
                ops.Add("h");
            }
        }
    }

    /// <summary>
    /// Writes a single cubic as <c>c</c>, collapsing to a straight <c>l</c> when it
    /// is the degenerate straight form (control points coincident with endpoints).
    /// The model stores lines exactly that way, so the comparison is a real,
    /// reachable case — not just defensive.
    /// </summary>
    private static void WriteSegment(List<string> ops, Point2D start, Point2D p1, Point2D p2, Point2D end)
    {
        bool isLine = p1.NearlyEquals(start) && p2.NearlyEquals(end);
        if (isLine)
        {
            ops.Add($"{Num(end.X)} {Num(end.Y)} l");
        }
        else
        {
            ops.Add($"{Num(p1.X)} {Num(p1.Y)} {Num(p2.X)} {Num(p2.Y)} {Num(end.X)} {Num(end.Y)} c");
        }
    }

    /// <summary>Maps the model cap enum to the PDF linecap code.</summary>
    private static int CapToPdf(StrokeCap cap) => cap switch
    {
        StrokeCap.Butt => 0,
        StrokeCap.Round => 1,
        StrokeCap.Square => 2,
        _ => 0,
    };

    /// <summary>Maps the model join enum to the PDF linejoin code.</summary>
    private static int JoinToPdf(StrokeJoin join) => join switch
    {
        StrokeJoin.Miter => 0,
        StrokeJoin.Round => 1,
        StrokeJoin.Bevel => 2,
        _ => 0,
    };

    /// <summary>Formats a number for PDF (PDF numbers do not allow exponents).</summary>
    internal static string Num(double value)
    {
        // Six decimals, which is a millionth of a point — about 1/72,000,000 inch, far below
        // any device resolution and far below the tolerances this project measures itself
        // against. Round-trip formatting wrote every digit a double can carry, and a
        // coordinate came out as "453.9427933037": correct, and eleven digits of noise on
        // every number in a document of three thousand objects. The export was 54 times the
        // size of the original file it came from, and this was most of the reason.
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        string text = value.ToString("0.######", CultureInfo.InvariantCulture);

        // "-0" is a legitimate rounding result and a needless surprise; PDF readers accept
        // it, but nothing reads better for it.
        return text == "-0" ? "0" : text;
    }

    /// <summary>
    /// Wraps raw (already zlib-compressed) bytes into a PDF stream object body
    /// carrying a <c>/Filter /FlateDecode</c> entry, with the compressed length
    /// written into the dictionary.
    /// </summary>
    /// <summary>Minimal but valid XMP packet carrying the document title.</summary>
    private static string BuildXmp(string title)
    {
        string safe = EscapeXml(title);
        return "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
               "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
               " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
               "  <rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n" +
               "   <pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>\n" +
               "  </rdf:Description>\n" +
               "  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
               $"   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{safe}</rdf:li></rdf:Alt></dc:title>\n" +
               "  </rdf:Description>\n" +
               "  <rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n" +
               "   <xmp:CreatorTool>VCCad</xmp:CreatorTool>\n" +
               "  </rdf:Description>\n" +
               " </rdf:RDF>\n" +
               "</x:xmpmeta>\n" +
               "<?xpacket end=\"w\"?>";
    }

    private static byte[] LoadSrgbProfile()
    {
        using Stream? stream = typeof(PdfDocumentExporter).Assembly
            .GetManifestResourceStream("VCCad.Pdf.Resources.sRGB.icc");
        if (stream is null)
        {
            return Array.Empty<byte>();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>zlib compression, for helpers that write their own stream objects.</summary>
    internal static byte[] CompressBytes(byte[] data) => Compress(data);

    internal static byte[] MakeStreamObject(byte[] data, string extraDict = "")
    {
        string dict = $"<< /Length {data.Length}{extraDict} /Filter /FlateDecode >>\nstream\n";
        byte[] head = Encoding.ASCII.GetBytes(dict);
        byte[] tail = Encoding.ASCII.GetBytes("\nendstream");
        var result = new byte[head.Length + data.Length + tail.Length];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(data, 0, result, head.Length, data.Length);
        Buffer.BlockCopy(tail, 0, result, head.Length + data.Length, tail.Length);
        return result;
    }

    /// <summary>zlib (RFC 1950) compression — identical framing to PDF FlateDecode.</summary>
    internal static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    /// <summary>Inflates a zlib (RFC 1950) stream produced by <see cref="Compress"/>.</summary>
    internal static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Collected geometry of one subpath in destination (page) space.</summary>
    private sealed class Contour
    {
        public bool IsClosed;
        public Point2D? Start;
        public readonly List<Point2D> Points = new();
        public readonly List<Point2D> InHandles = new();
        public readonly List<Point2D> OutHandles = new();
    }
}
