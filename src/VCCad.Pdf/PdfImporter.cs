using System.Globalization;
using System.IO;
using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// Imports PDF files into VCCad documents.
///
/// Two paths:
/// <list type="bullet">
/// <item><b>VCCad PDFs</b> (with our embedded sidecar) restore the full lossless
/// model — artboards, orphans, styles, artboard positions.</item>
/// <item><b>Foreign PDFs</b> are imported structurally: each page's MediaBox
/// becomes an artboard of that exact size (pages need not be uniform), laid out
/// left-to-right on the pasteboard. Full vector-content import is a larger task
/// scheduled separately; this guarantees multipage structure and correct sizes.</item>
/// </list>
/// </summary>
public static class PdfImporter
{
    /// <summary>Imports <paramref name="pdfBytes"/> into a document.</summary>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a PDF at all: no <c>%PDF-</c> header, or a header with
    /// neither a readable page nor any PDF completion marker. Refusing is the
    /// point — a blank A4 would tell the caller an import succeeded when nothing
    /// was read.
    /// </exception>
    public static CadDocument Import(byte[] pdfBytes)
        => Import(pdfBytes, password: null, out _);

    /// <summary>
    /// Imports a PDF that needs a password to open.
    ///
    /// Encryption protects streams and strings, not structure, so a protected file parses perfectly and
    /// yields **nothing** - it opens empty rather than failing. A password that opens it is what turns
    /// that back into a drawing, which is why this path exists at all.
    /// </summary>
    public static CadDocument Import(byte[] pdfBytes, string? password)
        => Import(pdfBytes, password, out _);

    /// <summary>
    /// Imports <paramref name="pdfBytes"/> and additionally reports every lossy
    /// approximation the import had to make — an unsupported shading type approximated by
    /// the closest supported one, a radial inner radius the model cannot hold. An
    /// approximation that is not reported is a silently wrong document.
    /// </summary>
    public static CadDocument Import(byte[] pdfBytes, out IReadOnlyList<string> notes)
        => Import(pdfBytes, password: null, out notes);

    /// <summary>The same, with a password, and reporting every lossy approximation.</summary>
    public static CadDocument Import(byte[] pdfBytes, string? password, out IReadOnlyList<string> notes)
    {
        var collected = new List<string>();
        notes = collected;
        CadDocument document = ImportCore(pdfBytes, collected, password);

        // Illustrator private data lives outside the PDF content model, so it is
        // captured separately and attached here, once, on every import path. A
        // document restored from our own sidecar already carries the payload and is
        // therefore left untouched.
        if (document.AiPrivateData is null)
        {
            document.AiPrivateData = Ai.AiPrivateDataExtractor.ExtractPrivateData(pdfBytes);
        }

        // What the file was protected with travels with the document, read from the /Encrypt
        // dictionary - which is not itself encrypted, so the permissions are readable even when the
        // file could not be opened, which is exactly when a person most needs telling.
        document.Security ??= ReadSecurity(pdfBytes, password);

        return document;
    }

    /// <summary>
    /// What the file was protected with, or null when it was not protected.
    ///
    /// Guarded by a byte scan for <c>/Encrypt</c> because this runs on every import and a second
    /// parse of a corpus-sized file is not free; an unprotected file costs one scan and no parse.
    /// </summary>
    private static DocumentSecurity? ReadSecurity(byte[] pdfBytes, string? password = null)
    {
        if (!Contains(pdfBytes, "/Encrypt"))
        {
            return null;
        }

        try
        {
            var file = new Parsing.PdfFile(pdfBytes, password);
            if (!file.IsEncrypted)
            {
                return null;
            }

            Encryption.PdfStandardSecurity? security = file.Security;

            return new DocumentSecurity(
                security?.Cipher ?? "not opened",
                file.Permissions,
                security is not null,
                security?.OpenedWithOwnerPassword ?? false);
        }
        catch (Exception)
        {
            // A file we cannot parse is a file we cannot describe; the import itself has already
            // reported why.
            return null;
        }
    }

    private static bool Contains(byte[] data, string token)
    {
        int[] wanted = token.Select(c => (int)c).ToArray();

        for (int i = 0; i + wanted.Length <= data.Length; i++)
        {
            int j = 0;
            while (j < wanted.Length && data[i + j] == wanted[j])
            {
                j++;
            }

            if (j == wanted.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The import itself: refusal, then sidecar, then vector, then structural
    /// fallback. Split out of <see cref="Import"/> so the private-data capture
    /// happens exactly once without being repeated at each of the early returns.
    /// </summary>
    private static CadDocument ImportCore(byte[] pdfBytes, List<string> notes, string? password = null)
    {
        // 0) A file that is not a PDF at all is refused here, before any parser
        //    gets a chance to fall through and hand back a fabricated A4.
        if (!HasPdfHeader(pdfBytes))
        {
            throw new InvalidDataException(
                $"Not a PDF: the {pdfBytes.Length}-byte input has no %PDF- header in its " +
                "first 1024 bytes.");
        }

        // 1) Our own lossless sidecar wins.
        try
        {
            return PdfSidecarReader.ReadDocument(pdfBytes);
        }
        catch (Exception)
        {
            // Not one of our PDFs (no valid sidecar) — fall through.
        }

        // 2) Full vector import: parse the PDF object model and content streams.
        try
        {
            var file = new Parsing.PdfFile(pdfBytes, password);
            var pageDicts = EnumeratePages(file).ToList();
            if (pageDicts.Count > 0)
            {
                return BuildFromPages(file, pageDicts, notes);
            }
        }
        catch (Parsing.PdfNestingLimitException)
        {
            // Hostile nesting: refuse rather than swallow the refusal and
            // substitute a blank page for a file that was deliberately crafted.
            throw;
        }
        catch (Exception)
        {
            // fall through to the structural (page-size only) import
        }

        // 3) Structural fallback: one artboard per page, non-uniform sizes allowed.
        //    This is the path for a real PDF whose object model or xref is damaged
        //    but whose page objects are still intact — the public API must hand a
        //    caller a usable document there, not throw (see VeraPdfCorpusTests).
        var document = new CadDocument { Name = "Imported" };
        IReadOnlyList<Size2D> pages = ReadPageSizes(pdfBytes);
        IReadOnlyList<Point2D> origins = GridOrigins(pages);

        for (int i = 0; i < pages.Count; i++)
        {
            Artboard artboard = document.AddArtboard(pages[i], $"Page {i + 1}", origins[i]);
            artboard.AddLayer("Layer 1");
        }

        // 4) Nothing readable at all. A complete-but-empty PDF (a header and an
        //    end marker, no pages) is a legitimate blank document; anything else
        //    is not a PDF document and must be refused, not silently accepted.
        if (pages.Count == 0)
        {
            if (!HasPdfCompletionMarker(pdfBytes))
            {
                throw new InvalidDataException(
                    $"Not a readable PDF: the {pdfBytes.Length}-byte input has a %PDF- header " +
                    "but no readable page, no page-size MediaBox and no completion marker " +
                    "(%%EOF / startxref / trailer).");
            }

            Artboard artboard = document.AddArtboard(PageSizes.A4Landscape, "Page 1");
            artboard.AddLayer("Layer 1");
        }

        return document;
    }

    /// <summary>
    /// True when the bytes carry a PDF header (<c>%PDF-</c>) near the start. ISO
    /// 32000-1 §7.5.2 puts it on the first line; readers tolerate a little leading
    /// junk, so the first 1024 bytes are searched.
    /// </summary>
    private static bool HasPdfHeader(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length, 1024);
        ReadOnlySpan<byte> header = new[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' };
        return bytes.AsSpan(0, limit).IndexOf(header) >= 0;
    }

    /// <summary>
    /// True when the bytes carry a PDF completion marker. Used only to distinguish
    /// an empty-but-complete PDF (accepted as a blank page) from a fragment that is
    /// not a PDF document (refused).
    /// </summary>
    private static bool HasPdfCompletionMarker(byte[] bytes)
    {
        int length = Math.Min(bytes.Length, 1024);
        string tail = Encoding.Latin1.GetString(bytes, bytes.Length - length, length);
        return tail.Contains("%%EOF", StringComparison.Ordinal)
               || tail.Contains("startxref", StringComparison.Ordinal)
               || tail.Contains("trailer", StringComparison.Ordinal);
    }

    /// <summary>
    /// Test/benchmark hook: runs only the real vector-import path (no sidecar, no
    /// structural fallback) and reports whether it succeeded. Used by the veraPDF
    /// corpus sweep to measure genuine parsing coverage.
    /// </summary>
    internal static bool TryImportVector(byte[] pdfBytes, out CadDocument? document)
        => TryImportVector(pdfBytes, password: null, out document);

    /// <summary>The same, with a password, for a protected file.</summary>
    internal static bool TryImportVector(byte[] pdfBytes, string? password, out CadDocument? document)
    {
        try
        {
            var file = new Parsing.PdfFile(pdfBytes, password);
            var pageDicts = EnumeratePages(file).ToList();
            if (pageDicts.Count > 0)
            {
                document = BuildFromPages(file, pageDicts);
                return true;
            }
        }
        catch (Exception)
        {
            // fall through
        }

        document = null;
        return false;
    }

    private static CadDocument BuildFromPages(Parsing.PdfFile file,
        IReadOnlyList<Dictionary<string, object?>> pageDicts, List<string>? notes = null)
    {
        var document = new CadDocument { Name = "Imported" };

        // Page sizes are resolved first so the whole sheet can be laid out as a
        // grid: a single row of pages is unusable once fitted (see GridOrigins).
        var sizes = new List<Size2D>(pageDicts.Count);
        foreach (Dictionary<string, object?> page in pageDicts)
        {
            (double w, double h) = MediaBox(file, page);
            sizes.Add(new Size2D(w, h));
        }

        IReadOnlyList<Point2D> origins = GridOrigins(sizes);

        for (int i = 0; i < pageDicts.Count; i++)
        {
            Dictionary<string, object?> page = pageDicts[i];
            double h = sizes[i].Height;
            double w = sizes[i].Width;
            Artboard artboard = document.AddArtboard(sizes[i], $"Page {i + 1}", origins[i]);

            // Preserve the PDF's optional-content layers (Illustrator layers). Items
            // drawn outside any marked-content block land in a default layer.
            var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
            Layer LayerFor(string? name)
            {
                // No invented name. Content the file does not put in an optional-content layer belongs to
                // the page, and an unnamed layer says exactly that - "Imported" was a name no file
                // contains, and it appeared in the tree as though the document said it.
                string key = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
                if (!layers.TryGetValue(key, out Layer? layer))
                {
                    layer = artboard.AddLayer(key.Length == 0 ? null : key);
                    layers[key] = layer;
                }

                return layer;
            }

            foreach (PdfImportedItem imported in
                     new PdfContentImporter(file, h, w, notes).ParsePage(page))
            {
                LayerFor(imported.Layer).AddItem(imported.Item);
            }

            if (layers.Count == 0)
            {
                // A page whose content asked for no layer at all still needs one to hold it.
                artboard.AddLayer();
            }
        }

        return document;
    }

    /// <summary>Gap left between pages when laying out an imported document.</summary>
    internal const double PageGap = 40;

    /// <summary>
    /// Aspect ratio the page grid aims for: close to a landscape working area.
    /// </summary>
    private const double TargetSheetAspect = 1.4;

    /// <summary>
    /// Positions for a document's pages, arranged as a grid rather than one long row.
    ///
    /// Laying pages out left to right is the obvious thing and the wrong one: an
    /// eight-page A4 pattern becomes a 4837 x 814 pt strip (5.9:1), so fitting it to
    /// the window lands at ~13% zoom and every page is an unreadable sliver. Choosing
    /// the column count whose overall shape is closest to <see cref="TargetSheetAspect"/>
    /// gives the same eight pages as a 4x2 block at ~39% zoom.
    ///
    /// Sizes may differ between pages (and within a row), so column widths and row
    /// heights are the maxima of what they contain.
    /// </summary>
    internal static IReadOnlyList<Point2D> GridOrigins(IReadOnlyList<Size2D> sizes)
    {
        var origins = new Point2D[sizes.Count];
        if (sizes.Count == 0)
        {
            return origins;
        }

        if (sizes.Count == 1)
        {
            origins[0] = new Point2D(0, 0);
            return origins;
        }

        double averageWidth = sizes.Average(s => Math.Max(1, s.Width));
        double averageHeight = sizes.Average(s => Math.Max(1, s.Height));

        int columns = 1;
        double bestScore = double.MaxValue;
        for (int candidate = 1; candidate <= sizes.Count; candidate++)
        {
            int candidateRows = (int)Math.Ceiling(sizes.Count / (double)candidate);
            double width = (candidate * averageWidth) + ((candidate - 1) * PageGap);
            double height = (candidateRows * averageHeight) + ((candidateRows - 1) * PageGap);
            double score = Math.Abs(Math.Log(width / height / TargetSheetAspect));
            if (score < bestScore - 1e-9)
            {
                bestScore = score;
                columns = candidate;
            }
        }

        int rows = (int)Math.Ceiling(sizes.Count / (double)columns);
        var columnWidths = new double[columns];
        var rowHeights = new double[rows];
        for (int i = 0; i < sizes.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            columnWidths[column] = Math.Max(columnWidths[column], sizes[i].Width);
            rowHeights[row] = Math.Max(rowHeights[row], sizes[i].Height);
        }

        var columnX = new double[columns];
        double x = 0;
        for (int c = 0; c < columns; c++)
        {
            columnX[c] = x;
            x += columnWidths[c] + PageGap;
        }

        var rowY = new double[rows];
        double y = 0;
        for (int r = 0; r < rows; r++)
        {
            rowY[r] = y;
            y += rowHeights[r] + PageGap;
        }

        for (int i = 0; i < sizes.Count; i++)
        {
            origins[i] = new Point2D(columnX[i % columns], rowY[i / columns]);
        }

        return origins;
    }

    private static IEnumerable<Dictionary<string, object?>> EnumeratePages(Parsing.PdfFile file)
    {
        int? catalog = file.FindCatalog();
        if (catalog is null || file.GetObject(catalog.Value) is not Dictionary<string, object?> root)
        {
            yield break;
        }

        foreach (Dictionary<string, object?> page in Walk(file, root.GetValueOrDefault("Pages"), 0))
        {
            yield return page;
        }
    }

    private static IEnumerable<Dictionary<string, object?>> Walk(Parsing.PdfFile file, object? node, int depth)
    {
        if (depth > 32 || file.ResolveDict(node) is not { } dict)
        {
            yield break;
        }

        string type = dict.GetValueOrDefault("Type") is Parsing.PdfName n ? n.Value : string.Empty;
        if (type == "Pages" && file.Resolve(dict.GetValueOrDefault("Kids")) is List<object?> kids)
        {
            foreach (object? kid in kids)
            {
                foreach (Dictionary<string, object?> page in Walk(file, kid, depth + 1))
                {
                    yield return page;
                }
            }
        }
        else if (type == "Page")
        {
            yield return dict;
        }
    }

    private static (double Width, double Height) MediaBox(Parsing.PdfFile file, Dictionary<string, object?> page)
    {
        object? current = page;
        for (int depth = 0; depth < 32 && current is not null; depth++)
        {
            if (file.ResolveDict(current) is not { } dict)
            {
                break;
            }

            if (file.Resolve(dict.GetValueOrDefault("MediaBox")) is List<object?> box && box.Count >= 4)
            {
                double llx = ToNum(file.Resolve(box[0]));
                double lly = ToNum(file.Resolve(box[1]));
                double urx = ToNum(file.Resolve(box[2]));
                double ury = ToNum(file.Resolve(box[3]));
                double width = Math.Abs(urx - llx);
                double height = Math.Abs(ury - lly);
                if (IsUsableSize(width, height))
                {
                    return (width, height);
                }

                // A MediaBox holding NaN, Infinity or an overflowing number is not
                // a page size; keep walking up the tree rather than minting a
                // non-finite artboard the serializer cannot represent.
            }

            current = dict.GetValueOrDefault("Parent");
        }

        return (PageSizes.A4Landscape.Width, PageSizes.A4Landscape.Height);
    }

    /// <summary>A page size is usable only when it is finite and has area.</summary>
    private static bool IsUsableSize(double width, double height)
        => double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;

    private static double ToNum(object? value) => value switch
    {
        double d => d,
        long l => l,
        _ => 0.0,
    };

    /// <summary>
    /// Reads page MediaBox sizes in document order. This is a focused scanner, not
    /// a general PDF parser: it finds page objects (<c>/Type /Page</c>, excluding
    /// <c>/Pages</c>) and parses the following <c>/MediaBox [llx lly urx ury]</c>.
    /// Adequate for VCCad output and most straightforward PDFs.
    /// </summary>
    public static IReadOnlyList<Size2D> ReadPageSizes(byte[] pdfBytes)
    {
        string text = Encoding.Latin1.GetString(pdfBytes);
        var sizes = new List<Size2D>();
        int index = 0;

        while (true)
        {
            int page = text.IndexOf("/Type /Page", index, StringComparison.Ordinal);
            if (page < 0)
            {
                break;
            }

            // Exclude "/Type /Pages" (the page-tree node).
            int after = page + "/Type /Page".Length;
            bool isPages = after < text.Length && text[after] == 's';
            if (isPages)
            {
                index = after;
                continue;
            }

            int media = text.IndexOf("/MediaBox", after, StringComparison.Ordinal);
            if (media < 0)
            {
                break;
            }

            int open = text.IndexOf('[', media);
            int close = open >= 0 ? text.IndexOf(']', open) : -1;
            if (open < 0 || close < 0)
            {
                index = media + 1;
                continue;
            }

            string[] parts = text[(open + 1)..close]
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double llx) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lly) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double urx) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double ury))
            {
                double width = Math.Abs(urx - llx);
                double height = Math.Abs(ury - lly);
                if (IsUsableSize(width, height))
                {
                    sizes.Add(new Size2D(width, height));
                    index = close + 1;
                    continue;
                }

                // double.TryParse accepts "NaN" and "Infinity" and overflows
                // "1e400" to infinity, and the difference of two finite extremes
                // ("[-1e308 -1e308 1e308 1e308]") overflows too. The page is real
                // even though its box is not: keep the page and give it the
                // default size rather than a non-finite artboard.
                sizes.Add(PageSizes.A4Landscape);
            }

            index = close + 1;
        }

        return sizes;
    }
}
