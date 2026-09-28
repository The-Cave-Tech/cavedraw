using System.Globalization;
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
    public static CadDocument Import(byte[] pdfBytes)
    {
        CadDocument document = ImportCore(pdfBytes);

        // Illustrator private data lives outside the PDF content model, so it is
        // captured separately and attached here, once, on every import path. A
        // document restored from our own sidecar already carries the payload and is
        // therefore left untouched.
        if (document.AiPrivateData is null)
        {
            document.AiPrivateData = Ai.AiPrivateDataExtractor.ExtractPrivateData(pdfBytes);
        }

        return document;
    }

    /// <summary>
    /// The import itself: sidecar, then vector, then structural fallback. Split out
    /// of <see cref="Import"/> so the private-data capture happens exactly once
    /// without being repeated at each of the early returns below.
    /// </summary>
    private static CadDocument ImportCore(byte[] pdfBytes)
    {
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
            var file = new Parsing.PdfFile(pdfBytes);
            var pageDicts = EnumeratePages(file).ToList();
            if (pageDicts.Count > 0)
            {
                return BuildFromPages(file, pageDicts);
            }
        }
        catch (Exception)
        {
            // fall through to the structural (page-size only) import
        }

        // 3) Structural fallback: one artboard per page, non-uniform sizes allowed.
        var document = new CadDocument { Name = "Imported" };
        IReadOnlyList<Size2D> pages = ReadPageSizes(pdfBytes);
        IReadOnlyList<Point2D> origins = GridOrigins(pages);

        for (int i = 0; i < pages.Count; i++)
        {
            Artboard artboard = document.AddArtboard(pages[i], $"Page {i + 1}", origins[i]);
            artboard.AddLayer("Layer 1");
        }

        if (pages.Count == 0)
        {
            Artboard artboard = document.AddArtboard(PageSizes.A4Landscape, "Page 1");
            artboard.AddLayer("Layer 1");
        }

        return document;
    }

    /// <summary>
    /// Test/benchmark hook: runs only the real vector-import path (no sidecar, no
    /// structural fallback) and reports whether it succeeded. Used by the veraPDF
    /// corpus sweep to measure genuine parsing coverage.
    /// </summary>
    internal static bool TryImportVector(byte[] pdfBytes, out CadDocument? document)
    {
        try
        {
            var file = new Parsing.PdfFile(pdfBytes);
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
        IReadOnlyList<Dictionary<string, object?>> pageDicts)
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
            Artboard artboard = document.AddArtboard(sizes[i], $"Page {i + 1}", origins[i]);

            // Preserve the PDF's optional-content layers (Illustrator layers). Items
            // drawn outside any marked-content block land in a default layer.
            var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
            Layer LayerFor(string? name)
            {
                string key = string.IsNullOrWhiteSpace(name) ? "Imported" : name;
                if (!layers.TryGetValue(key, out Layer? layer))
                {
                    layer = artboard.AddLayer(key);
                    layers[key] = layer;
                }

                return layer;
            }

            foreach (PdfImportedItem imported in
                     new PdfContentImporter(file, h).ParsePage(page))
            {
                LayerFor(imported.Layer).AddItem(imported.Item);
            }

            if (layers.Count == 0)
            {
                artboard.AddLayer("Imported");
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
                return (Math.Abs(urx - llx), Math.Abs(ury - lly));
            }

            current = dict.GetValueOrDefault("Parent");
        }

        return (PageSizes.A4Landscape.Width, PageSizes.A4Landscape.Height);
    }

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
                sizes.Add(new Size2D(Math.Abs(urx - llx), Math.Abs(ury - lly)));
            }

            index = close + 1;
        }

        return sizes;
    }
}
