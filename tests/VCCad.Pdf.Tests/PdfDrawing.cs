using System.IO.Compression;
using System.Text;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The **drawing** in an exported file, which is not the whole file.
///
/// The export embeds the document's own JSON as a sidecar, so changing any model value changes the bytes - a
/// feature that draws nothing at all still changes the file. Asking "did the file change" would therefore answer
/// yes for everything and prove nothing, so a fidelity question has to be asked of the streams that draw.
///
/// Found through the **page tree**, not by looking for streams whose bytes happen to look like operators. The
/// file also carries the ICC profile, the XMP packet and the sidecar, and matching substrings inside those is a
/// false positive waiting to happen - which is exactly what a loose `" re"` search did: the sidecar's JSON
/// satisfies it, so every feature looked written.
/// </summary>
internal static class PdfDrawing
{
    /// <summary>Every page's content streams, decoded and concatenated.</summary>
    public static string Of(byte[] pdf)
    {
        var text = new StringBuilder();

        foreach ((Dictionary<string, object?> _, string content) in Pages(pdf))
        {
            text.Append(content);
        }

        return text.ToString();
    }

    /// <summary>The pages of a document, as (dictionary, decoded content).</summary>
    public static List<(Dictionary<string, object?> Page, string Content)> Pages(byte[] pdf)
    {
        var file = new PdfFile(pdf);
        var pages = new List<(Dictionary<string, object?>, string)>();

        if (file.FindCatalog() is not { } catalog ||
            file.GetObject(catalog) is not Dictionary<string, object?> root ||
            file.ResolveDict(root.GetValueOrDefault("Pages")) is not { } tree)
        {
            return pages;
        }

        foreach (Dictionary<string, object?> page in PageNodes(file, tree))
        {
            var text = new StringBuilder();
            foreach (int number in PageContents(file, page))
            {
                if (file.GetObject(number) is PdfStream stream)
                {
                    text.Append(Encoding.Latin1.GetString(file.GetStreamData(stream))).Append('\n');
                }
            }

            pages.Add((page, text.ToString()));
        }

        return pages;
    }

    /// <summary>An image XObject in a file, with the object number its dictionary was parsed from.</summary>
    public sealed record ImageObject(int Number, Dictionary<string, object?> Dict, byte[] Samples)
    {
        public bool Is(string colourSpace)
            => (Dict.GetValueOrDefault("ColorSpace") as PdfName)?.Value == colourSpace;

        public double NumberValue(string key) => Convert.ToDouble(Dict[key]);
    }

    /// <summary>The image XObjects in a file.</summary>
    public static List<ImageObject> Images(byte[] pdf)
    {
        var file = new PdfFile(pdf);
        var images = new List<ImageObject>();

        foreach (int number in file.ObjectNumbers)
        {
            if (file.GetObject(number) is PdfStream stream &&
                stream.Dict.GetValueOrDefault("Subtype") is PdfName { Value: "Image" })
            {
                images.Add(new ImageObject(number, stream.Dict, file.GetStreamData(stream)));
            }
        }

        return images;
    }

    private static IEnumerable<Dictionary<string, object?>> PageNodes(
        PdfFile file, Dictionary<string, object?> node)
    {
        if (node.GetValueOrDefault("Kids") is List<object?> kids)
        {
            foreach (object? kid in kids)
            {
                if (file.ResolveDict(kid) is { } child)
                {
                    foreach (Dictionary<string, object?> page in PageNodes(file, child))
                    {
                        yield return page;
                    }
                }
            }

            yield break;
        }

        yield return node;
    }

    private static IEnumerable<int> PageContents(PdfFile file, Dictionary<string, object?> node)
    {
        object? contents = node.GetValueOrDefault("Contents");
        if (file.Resolve(contents) is List<object?> many)
        {
            foreach (object? entry in many)
            {
                if (file.Resolve(entry) is PdfStream && entry is PdfRef reference)
                {
                    yield return reference.Number;
                }
            }

            yield break;
        }

        if (contents is PdfRef single)
        {
            yield return single.Number;
        }
    }
}
