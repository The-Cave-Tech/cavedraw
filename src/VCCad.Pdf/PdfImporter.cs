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

        double x = 0;
        const double gap = 40;
        for (int i = 0; i < pages.Count; i++)
        {
            Size2D size = pages[i];
            Artboard artboard = document.AddArtboard(size, $"Page {i + 1}", new Point2D(x, 0));
            artboard.AddLayer("Layer 1");
            x += size.Width + gap;
        }

        if (pages.Count == 0)
        {
            Artboard artboard = document.AddArtboard(PageSizes.A4Landscape, "Page 1");
            artboard.AddLayer("Layer 1");
        }

        return document;
    }

    private static CadDocument BuildFromPages(Parsing.PdfFile file,
        IReadOnlyList<Dictionary<string, object?>> pageDicts)
    {
        var document = new CadDocument { Name = "Imported" };
        double x = 0;
        const double gap = 40;

        for (int i = 0; i < pageDicts.Count; i++)
        {
            Dictionary<string, object?> page = pageDicts[i];
            (double w, double h) = MediaBox(file, page);
            Artboard artboard = document.AddArtboard(new Size2D(w, h), $"Page {i + 1}", new Point2D(x, 0));

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

            foreach (PdfImportedItem imported in new PdfContentImporter(file, h).ParsePage(page))
            {
                LayerFor(imported.Layer).AddItem(imported.Item);
            }

            if (layers.Count == 0)
            {
                artboard.AddLayer("Imported");
            }

            x += w + gap;
        }

        return document;
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
