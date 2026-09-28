using VCCad.Core.Model;
using VCCad.Pdf;

namespace VCCad.App.Fonts;

/// <summary>One font a document uses, and whether its programme came with the file.</summary>
/// <param name="BaseFont">The PDF font name, e.g. <c>NPFRLV+CenturyGothic-Bold</c>.</param>
/// <param name="FamilyName">The registered family name when the programme is embedded.</param>
/// <param name="Embedded">Whether the file carries the programme.</param>
/// <param name="Runs">How many text runs use it.</param>
public sealed record FontUsageEntry(string BaseFont, string? FamilyName, bool Embedded, int Runs);

/// <summary>
/// What a document's text is actually drawn with.
///
/// A PDF need not embed its fonts, and one that does not is rendered with a
/// substitute — the same thing every viewer does. That is a fidelity limit the person
/// has to be told about, because the substitute is a different design: it will not
/// match the original letterforms, and the difference is visible.
/// </summary>
public static class FontUsage
{
    /// <summary>Every font the document uses, most-used first.</summary>
    public static IReadOnlyList<FontUsageEntry> Report(CadDocument document)
    {
        var fonts = new Dictionary<string, FontUsageEntry>(StringComparer.Ordinal);

        void Collect(IEnumerable<LayerItem> items)
        {
            foreach (TextItem text in items.OfType<TextItem>())
            {
                foreach (TextRun run in text.Runs)
                {
                    // Key on the font the document asked for. Keying on the rendering
                    // family would collapse every substituted font into one entry,
                    // because they all render with the same bundled face.
                    string key = run.EmbeddedFont?.FamilyName
                                 ?? "substitute:" + (run.SourceFont ?? run.FontFamily);
                    fonts.TryGetValue(key, out FontUsageEntry? existing);
                    fonts[key] = new FontUsageEntry(
                        run.EmbeddedFont?.BaseFont ?? run.SourceFont ?? run.FontFamily,
                        run.EmbeddedFont?.FamilyName,
                        run.EmbeddedFont is not null,
                        (existing?.Runs ?? 0) + 1);
                }
            }
        }

        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                Collect(layer.Children);
            }
        }

        Collect(document.Orphans.Children);

        return fonts.Values.OrderByDescending(f => f.Runs).ToArray();
    }

    /// <summary>
    /// A sentence naming any font the document needs that neither the machine's URW
    /// faces nor a metric-compatible platform clone can supply, or null when everything
    /// is covered. A PDF that simply does not embed Helvetica is not a problem — every
    /// viewer supplies it — so only a genuine gap is worth interrupting the person for.
    /// </summary>
    public static string? Warning(CadDocument document)
    {
        IReadOnlyList<string> missing = StandardFontResolver.Missing(document);
        if (missing.Count > 0)
        {
            return $"⚠ no font for {string.Join(", ", missing)} — install the URW base-35 fonts " +
                   "(fonts-urw-base35, or Ghostscript)";
        }

        IReadOnlyList<string> unresolved = Unresolved(document);
        return unresolved.Count == 0
            ? null
            : $"⚠ embedded font failed to load: {string.Join(", ", unresolved)}";
    }

    /// <summary>
    /// Fonts whose programme is embedded but which the canvas could not load. This is
    /// always a defect — the file carries what we need — so it is reported separately
    /// from an ordinary substitution.
    /// </summary>
    public static IReadOnlyList<string> Unresolved(CadDocument document)
        => Report(document)
            .Where(f => f.Embedded && f.FamilyName is not null &&
                        !EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(f.FamilyName, out _))
            .Select(f => f.BaseFont)
            .ToArray();

    /// <summary>How each font in the document is actually drawn.</summary>
    public static IReadOnlyList<(FontUsageEntry Font, string Source)> Detail(CadDocument document)
    {
        var detail = new List<(FontUsageEntry, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(IEnumerable<LayerItem> items)
        {
            foreach (TextItem text in items.OfType<TextItem>())
            {
                foreach (TextRun run in text.Runs)
                {
                    string key = run.EmbeddedFont?.FamilyName ?? run.SourceFont ?? run.FontFamily;
                    if (seen.Add(key))
                    {
                        detail.Add((new FontUsageEntry(
                            run.EmbeddedFont?.BaseFont ?? run.SourceFont ?? run.FontFamily,
                            run.EmbeddedFont?.FamilyName,
                            run.EmbeddedFont is not null,
                            0), StandardFontResolver.Describe(run)));
                    }
                }
            }
        }

        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                Collect(layer.Children);
            }
        }

        Collect(document.Orphans.Children);
        return detail;
    }
}
