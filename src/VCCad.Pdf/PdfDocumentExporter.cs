using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VCCad.Core.Model;
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
            CollectAlphas(artboard, 1.0, alphas);
        }

        var alphaStates = new PdfAlphaStates(assembler, alphas);

        // Every embedded image is written back into the file as its own XObject, keeping
        // the colour space the document stores — a CMYK scan stays a CMYK scan. The
        // resource dictionary is shared across pages, so an image is written once however
        // many pages place it; unused entries in a resource dictionary are legal.
        var imageObjects = new PdfImageObjects(assembler, AllImages(document));

        // Gradient shadings are allocated while an artboard's content stream is built
        // (each usage carries its own geometry), so the content is written first and the
        // resource dictionary - which must name every shading - is assembled afterwards.
        var shadingObjects = new PdfShadingObjects(assembler, notes ?? new List<string>());
        var contents = new byte[document.Artboards.Count][];
        for (int i = 0; i < document.Artboards.Count; i++)
        {
            contents[i] = BuildArtboardContent(
                document.Artboards[i], embedder, alphaStates, imageObjects, shadingObjects);
        }

        string resources =
            $"/Resources << {embedder.FontDict()}{alphaStates.Dict()}{imageObjects.Dict()}{shadingObjects.Dict()}>>";

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
    private static byte[] BuildArtboardContent(Artboard artboard, PdfFontEmbedder embedder, PdfAlphaStates alphaStates, PdfImageObjects? images = null, PdfShadingObjects? shadings = null)
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
                    PaintItem(ops, item, AffineTransform.Identity, 1.0, alphaStates, images, shadings);
                }
            }
        }

        // Text objects (page-local coordinates, same frame as paths).
        foreach (TextItem text in AllTextItems(artboard))
        {
            if (!text.IsVisible)
            {
                continue;
            }

            // Text is clipped like anything else. It used to be painted with no clip at all,
            // because paths and images go through PaintItem and text has its own loop — so a
            // clipped label came out unclipped, showing text the file had hidden. The
            // Transparency Guide clips its page furniture this way.
            bool clipped = text.IsClipped;
            if (clipped)
            {
                ops.Add("q");
                foreach (ClipSpec clip in text.Clips)
                {
                    AppendClip(ops, clip, AffineTransform.Identity);
                }
            }

            WriteText(ops, text, embedder, alphaStates);

            if (clipped)
            {
                ops.Add("Q");
            }
        }

        if (ops.Count == 0)
        {
            ops.Add("% empty artboard"); // keep the stream non-empty & valid
        }

        return Encoding.UTF8.GetBytes(string.Join("\n", ops) + "\n");
    }

    /// <summary>
    /// Recursively emits an item. <paramref name="toDoc"/> maps the item's local
    /// coordinates into artboard (document) space — the composition of every
    /// ancestor group transform; <paramref name="opacity"/> is the accumulated
    /// product of ancestor opacities (reserved: emitted in a later sprint when the
    /// ExtGState task lands, see project plan M4).
    /// </summary>
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

    private static void PaintItem(List<string> ops, LayerItem item, AffineTransform toDoc, double opacity, PdfAlphaStates alphaStates, PdfImageObjects? images = null, PdfShadingObjects? shadings = null)
    {
        if (!item.IsEffectivelyVisible())
        {
            return;
        }

        // Text is written by its own loop, not here. It used to reach this method anyway,
        // because the clip is emitted before the switch that dispatches on the item's kind —
        // so every text object opened a q with a clip and closed it again having drawn
        // nothing, while the text itself was written with no clip at all. Text is clipped in
        // the loop that writes it.
        if (item is TextItem)
        {
            return;
        }

        // A clip is emitted around the item rather than baked into its geometry, because
        // that is what it is: the item is drawn whole and the outline limits what shows.
        // Several clips intersect, which is what successive W n operators do.
        bool clipped = item.IsClipped;
        if (clipped)
        {
            ops.Add("q");
            foreach (ClipSpec clip in item.Clips)
            {
                AppendClip(ops, clip, toDoc);
            }
        }

        switch (item)
        {
            case PathItem path:
                PaintPath(ops, path, toDoc, opacity, alphaStates, shadings);
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
                    PaintItem(ops, child, childToDoc, opacity * group.Opacity, alphaStates, images, shadings);
                }

                break;
        }

        if (clipped)
        {
            ops.Add("Q");
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
    /// Emits the fill/stroke operators for one path. Contours are separated into
    /// closed (fillable) and open (stroke-only), so the renderer never fills an
    /// open path as PDF would implicitly do.
    /// </summary>
    private static void PaintPath(List<string> ops, PathItem path, AffineTransform toDoc, double opacity, PdfAlphaStates alphaStates, PdfShadingObjects? shadings = null)
    {
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

        if (strokeVisible)
        {
            ops.Add(ColorOperator(path.Stroke.Color, path.SourceStrokeCmyk, stroke: true));
            ops.Add($"{CapToPdf(path.Stroke.Cap)} J");
            ops.Add($"{JoinToPdf(path.Stroke.Join)} j");
            if (path.Stroke.Join == StrokeJoin.Miter)
            {
                ops.Add($"{Num(path.Stroke.MiterLimit)} M");
            }

            if (!path.Stroke.Dash.IsEmpty)
            {
                string array = string.Join(' ', path.Stroke.Dash.Segments.Select(v => Num(Math.Max(0.0, v * strokeScale))));
                ops.Add($"[{array}] {Num(path.Stroke.Dash.Offset * strokeScale)} d");
            }
        }

        var closed = contours.Where(c => c.IsClosed).ToList();
        var open = contours.Where(c => !c.IsClosed).ToList();

        // --- Fill (all contours; `f` implicitly closes open subpaths) --------
        // A gradient fill is a native PDF shading: the shape becomes the clip and `sh`
        // paints the ramp through it, so the gradient never escapes its shape. A gradient
        // PDF cannot represent falls back to the fill's flat colour (reported by
        // PdfShadingObjects).
        ShadingPaint? shading = null;
        if (fillVisible && path.Fill.Gradient is { } gradient && shadings is not null)
        {
            shading = shadings.NameFor(gradient, path.BoundingBox(), toDoc);
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

        if (!strokeVisible)
        {
            return;
        }

        // --- Stroke: honour Inside/Outside by clipping ---------------------
        // PDF has no stroke alignment, so an aligned stroke is drawn at double
        // width and clipped to the inside or outside of the path.
        if (alphaStates.HasTransparency)
        {
            ops.Add($"{alphaStates.NameFor(path.Stroke.Color.A * opacity)} gs");
        }

        bool aligned = path.Stroke.Alignment != StrokeAlignment.Center && closed.Count > 0;
        if (closed.Count > 0)
        {
            if (!aligned)
            {
                ops.Add($"{Num(width)} w");
                WriteContours(ops, closed);
                ops.Add("S");
            }
            else
            {
                ops.Add("q");
                if (path.Stroke.Alignment == StrokeAlignment.Inside)
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

                ops.Add($"{Num(width * 2)} w");
                WriteContours(ops, closed);
                ops.Add("S");
                ops.Add("Q");
            }
        }

        // --- Open contours: always centre-stroked --------------------------
        if (open.Count > 0)
        {
            ops.Add($"{Num(width)} w");
            WriteContours(ops, open);
            ops.Add("S");
        }
    }

    private static void CollectAlphas(Artboard artboard, double opacity, List<double> alphas)
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
                CollectItemAlphas(item, opacity * layer.Opacity, alphas);
            }
        }
    }

    private static void CollectItemAlphas(LayerItem item, double opacity, List<double> alphas)
    {
        if (!item.IsEffectivelyVisible())
        {
            return;
        }

        switch (item)
        {
            case PathItem path:
                alphas.Add(path.Fill.Color.A * opacity * path.Opacity);
                alphas.Add(path.Stroke.Color.A * opacity * path.Opacity);
                break;
            case TextItem text:
                alphas.Add(text.Color.A * opacity);
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    CollectItemAlphas(child, opacity * group.Opacity, alphas);
                }

                break;
        }
    }

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
        List<string> ops, TextItem text, PdfFontEmbedder embedder, PdfAlphaStates alphaStates)
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
        TextRun first = text.Runs[0];
        double firstAscent = first.PlacedAscentEm > 0
            ? first.PlacedAscentEm
            : first.EmbeddedFont!.Ascent / 1000.0;
        double depth = firstAscent * first.FontSize;
        double ox = text.Origin.X - (sin * depth);
        double oy = text.Origin.Y + (cos * depth);

        ops.Add(ColorOperator(text.Color, text.SourceCmyk, stroke: false));
        if (alphaStates.HasTransparency)
        {
            ops.Add($"{alphaStates.NameFor(text.Color.A)} gs");
        }

        ops.Add("BT");
        ops.Add($"{Num(cos)} {Num(sin)} {Num(sin)} {Num(-cos)} {Num(ox)} {Num(oy)} Tm");

        string? current = null;
        double currentSize = 0;

        foreach (TextRun run in text.Runs)
        {
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
    /// Writes a block's runs as separate text objects, one per run, each with its own
    /// matrix. Used for everything the single-object path cannot describe.
    /// </summary>
    private static void WriteText(List<string> ops, TextItem text, PdfFontEmbedder embedder, PdfAlphaStates alphaStates)
    {
        if (TryWriteRunsAsOneTextObject(ops, text, embedder, alphaStates))
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

            void Flush()
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
                double depth = yOffset + (ascent * run.FontSize);
                double ox = text.Origin.X - (sin * depth) + (cos * pen);
                double oy = text.Origin.Y + (cos * depth) + (sin * pen);

                ops.Add(ColorOperator(text.Color, text.SourceCmyk, stroke: false));
                if (alphaStates.HasTransparency)
                {
                    ops.Add($"{alphaStates.NameFor(text.Color.A)} gs");
                }

                ops.Add("BT");
                ops.Add($"{resource} {Num(run.FontSize)} Tf");
                // R(rot) with a y-flip for upright glyphs, times the advance scale.
                ops.Add($"{Num(sx * cos)} {Num(sx * sin)} {Num(sin)} {Num(-cos)} {Num(ox)} {Num(oy)} Tm");
                ops.Add($"<{hex}> Tj");
                ops.Add("ET");
                hex.Clear();
            }

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
                            continue;
                        }

                        hex.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
                        lineAdvance += font.Advance1000(gid) * run.FontSize / 1000.0;
                    }

                    // The display line's own top, including the paragraph leading that
                    // each explicit break contributes.
                    yOffset = (li * lineHeight) + (NewlinesBefore(text, displayLines, li) * text.ParagraphSpacing);
                    Flush();
                }
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
