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
    ///
    /// **Every part of this is news the person has to get.** The status bar appends this
    /// string to whatever it is showing, so a fact that reaches only an operation reply
    /// reaches only half the audience — the parity rule backwards. A width or variant the
    /// file states is the same kind of fact as a substitution: the run is drawn in the
    /// family's own face and not the one the file asked for, and the person looking at the
    /// page is the one who has to know.
    /// </summary>
    public static string? Warning(CadDocument document)
    {
        var parts = new List<string>();

        IReadOnlyList<string> missing = StandardFontResolver.Missing(document);
        if (missing.Count > 0)
        {
            parts.Add($"⚠ no font for {string.Join(", ", missing)} — install the URW base-35 fonts " +
                      "(fonts-urw-base35, or Ghostscript)");
        }

        IReadOnlyList<string> unresolved = Unresolved(document);
        if (unresolved.Count > 0)
        {
            parts.Add($"⚠ embedded font failed to load: {string.Join(", ", unresolved)}");
        }

        // A face request that was not honoured follows the two font checks, because a font
        // that could not be supplied at all is the more urgent of the two.
        parts.AddRange(UnselectedFaceRequests(document).Select(sentence => "⚠ " + sentence));

        return parts.Count == 0 ? null : string.Join("   ", parts);
    }

    /// <summary>
    /// The widths and variants the document states that this build draws in the family's own face.
    ///
    /// A run names one family and the face is chosen from it by weight and slant, so a
    /// <c>font-stretch</c> or <c>font-variant</c> the file wrote is kept faithfully and then not
    /// selected by. That is the one loss a reader of the document cannot see — the sidecar holds
    /// the value and the drawing ignores it — so it is said out loud. This is the person-facing
    /// half of the sentence the SVG reader puts in its own warnings: the reader's copy is what
    /// <c>document.importSvg</c> returns to a driver, and this is what the status bar shows, so
    /// the two audiences are told the same thing. Only a value the file actually states is
    /// reported — <c>normal</c> is stored as absence — so an ordinary import carries nothing.
    /// </summary>
    /// <remarks>
    /// The wording is deliberately the reader's, word for word, because two sentences about one
    /// fact would be two reports. The reader's copy is the canonical one (#161 tests it); the
    /// App test that pins this surface reads that copy and demands it here, so a change to either
    /// wording fails rather than drifting into two reports of the same thing.
    ///
    /// Reached through <see cref="CadDocument.AllItems"/>, so a run inside a group is found: Inkscape
    /// wraps its text in groups, and a width written inside a `g` is exactly the case this must catch.
    /// </remarks>
    public static IReadOnlyList<string> UnselectedFaceRequests(CadDocument document)
    {
        var said = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (TextItem text in document.AllItems().OfType<TextItem>())
        {
            foreach (TextRun run in text.Runs)
            {
                if (run.FontStretch is { Length: > 0 } stretch && seen.Add("font-stretch=" + stretch))
                {
                    said.Add(
                        $"font-stretch=\"{stretch}\" is kept on the run, and no face is selected by width: " +
                        "the face is chosen by family, weight and slant, so the run draws in the family's own face");
                }

                if (run.FontVariant is { Length: > 0 } variant && seen.Add("font-variant=" + variant))
                {
                    said.Add(
                        $"font-variant=\"{variant}\" is kept on the run, and no face is selected by variant: " +
                        "the face is chosen by family, weight and slant, so the run draws in the family's own face");
                }
            }
        }

        return said;
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
