using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Pdf;

namespace VCCad.App.Fonts;

/// <summary>
/// Chooses the face a text run is drawn with when the document did not embed one.
///
/// A PDF may name any of the fourteen standard fonts — Helvetica, Times, Courier,
/// Symbol, ZapfDingbats — and embed nothing at all, expecting the viewer to supply
/// them. The supply, in the order every viewer uses:
///
///   1. **URW Core 35** from this machine (the distro package, or Ghostscript's own
///      font directory). These carry the original metrics, and they are what
///      Ghostscript and Inkscape use. They are AGPL-3, so they are located here and
///      never redistributed with VCCad.
///   2. **A metric-compatible clone the platform already has**: Arial, Times New
///      Roman, Courier New. This is precisely what Chrome's PDF engine substitutes
///      for Helvetica/Times/Courier, and it is why a Windows machine with no
///      Ghostscript still renders such a document correctly.
///   3. The family the importer recorded, so a document that asks for a font nobody
///      has still gets *something*, and <see cref="Describe"/> says so.
///
/// URW faces found on disk are read and registered into the embedded-font collection,
/// so "Nimbus Sans" resolves by name exactly like a font that arrived inside a PDF.
/// </summary>
public static class StandardFontResolver
{
    private static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static bool _scanned;

    /// <summary>Registers every URW face found on this machine. Safe to call often.</summary>
    public static int RegisterAvailable()
    {
        int added = 0;
        foreach (StandardFace face in AllFaces())
        {
            if (TryRegister(face))
            {
                added++;
            }
        }

        _scanned = true;
        return added;
    }

    /// <summary>The family to draw a run with.</summary>
    /// <summary>
    /// The family to draw a run with.
    ///
    /// Three cases, and telling them apart is the whole job:
    ///
    ///  * **The run carries a programme.** Draw with it. A PDF's own font is never replaced.
    ///  * **The document asked for a name it did not embed.** Supply it from the standard
    ///    chain — the machine's URW faces, else a metric-compatible clone. A PDF is
    ///    entitled to name Helvetica and embed nothing, and every viewer supplies it.
    ///  * **The person chose the face.** Use it. Someone who picks Consolas from the font
    ///    list means Consolas, and routing their choice through the standard chain would
    ///    draw every one of the couple of hundred fonts we offer as the same face.
    ///
    /// The distinction between the last two is <see cref="TextRun.SourceFont"/>: it records
    /// the name the *document* asked for, and styling a run clears it.
    /// </summary>
    public static string FamilyFor(TextRun run)
    {
        if (run.EmbeddedFont is not null)
        {
            return run.FontFamily;
        }

        if (run.SourceFont is not { Length: > 0 } source)
        {
            return run.FontFamily;
        }

        StandardFonts.TryResolve(source, run.Bold, run.Italic, out StandardFace face);

        if (UrwFamilyFor(face) is { } urw)
        {
            return urw;
        }

        if (StandardFonts.CloneFamily(face) is { } clone && IsAvailable(clone))
        {
            return clone;
        }

        return run.FontFamily;
    }

    /// <summary>Family names the font picker should offer.</summary>
    /// <summary>
    /// Every family the font picker should offer: the platform's own fonts, plus the URW
    /// faces we supply for the standard PDF names.
    ///
    /// Offering only the three standard families is wrong for an editor — a person
    /// setting type wants the fonts on their machine. The standard faces are listed first
    /// because those are the ones a PDF can name without embedding.
    /// </summary>
    public static IReadOnlyList<string> OfferedFamilies()
    {
        var families = new List<string>(StandardFamilyNames());
        var seen = new HashSet<string>(families, StringComparer.OrdinalIgnoreCase);

        // Then everything installed on this machine, by real family name.
        try
        {
            foreach (FontFamily installed in FontManager.Current.SystemFonts
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(installed.Name) && seen.Add(installed.Name))
                {
                    families.Add(installed.Name);
                }
            }
        }
        catch (Exception)
        {
            // A platform that will not enumerate its fonts still gets the standard set.
        }

        return families;
    }

    /// <summary>
    /// The standard families the picker puts first, which are the ones a PDF may name
    /// without embedding.
    ///
    /// Separate from <see cref="OfferedFamilies"/> so the two can be told apart: how many
    /// of the offered names are standard faces and how many are the machine's own is the
    /// question the font report has to answer, and subtracting a list from itself is how
    /// that answer goes wrong.
    /// </summary>
    public static IReadOnlyList<string> StandardFamilyNames()
    {
        var families = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (StandardFontKind kind in new[]
                 {
                     StandardFontKind.Sans, StandardFontKind.Serif, StandardFontKind.Mono,
                 })
        {
            var face = new StandardFace(kind, false, false);
            string urw = StandardFonts.UrwFamily(face);
            string family = StandardFontFiles.Exists(face)
                ? urw
                : StandardFonts.CloneFamily(face) is { } clone && IsAvailable(clone) ? clone : urw;

            if (seen.Add(family))
            {
                families.Add(family);
            }
        }

        return families;
    }

    /// <summary>How a run's face was chosen, for the font report and the status bar.</summary>
    public static string Describe(TextRun run)
    {
        if (run.EmbeddedFont is not null)
        {
            return "embedded programme";
        }

        StandardFonts.TryResolve(run.SourceFont ?? run.FontFamily, run.Bold, run.Italic, out StandardFace face);
        string wanted = run.SourceFont ?? run.FontFamily;
        string style = StyleSuffix(face);

        if (UrwFamilyFor(face) is { } urw)
        {
            // The weight is part of the answer. "Nimbus Sans" for a bold run says the
            // family and not the face, so a person cannot tell whether the bold they asked
            // for is a real cut or the shaper leaning the regular one — and those look
            // different.
            //
            // Whether the cut is installed is asked of the file system, not of the font
            // manager. A family whose bold face is present but not yet registered would
            // otherwise be reported as synthesised — a false alarm about the very thing this
            // report exists to be right about, and one that came and went between runs.
            return StandardFontFiles.Exists(face)
                ? $"URW {urw}{style} (installed on this machine)"
                : IsAvailable(urw)
                    ? $"URW {urw} (installed, but no {style.Trim().ToLowerInvariant()} face " +
                      "— it is synthesised)"
                    : $"unavailable — {wanted} is not embedded, and no standard font is installed";
        }

        if (StandardFonts.CloneFamily(face) is { } clone && IsAvailable(clone))
        {
            return FaceExists(clone, face)
                ? $"metric-compatible {clone}{style} (installed on this machine)"
                : $"metric-compatible {clone} (installed, but no {style.Trim().ToLowerInvariant()} " +
                  "face — it is synthesised)";
        }

        return $"unavailable — {wanted} is not embedded, and no standard font is installed";
    }

    /// <summary>A face's weight and slant, as a family name suffix: " Bold Italic".</summary>
    private static string StyleSuffix(StandardFace face)
    {
        var parts = new List<string>();
        if (face.Bold)
        {
            parts.Add("Bold");
        }

        if (face.Italic)
        {
            parts.Add("Italic");
        }

        return parts.Count == 0 ? string.Empty : " " + string.Join(' ', parts);
    }

    /// <summary>
    /// Whether the machine really has this weight and slant, rather than a family whose
    /// bold or italic will be made up by the shaper.
    /// </summary>
    private static bool FaceExists(string family, StandardFace face)
    {
        try
        {
            var typeface = new Typeface(
                family,
                face.Italic ? FontStyle.Italic : FontStyle.Normal,
                face.Bold ? FontWeight.Bold : FontWeight.Normal);
            return FontManager.Current.TryGetGlyphTypeface(typeface, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when the platform can resolve a family.</summary>
    public static bool IsAvailable(string family)
    {
        try
        {
            return FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Families a document needs that neither URW nor a clone can supply. These are the
    /// faces worth offering to download.
    /// </summary>
    public static IReadOnlyList<string> Missing(CadDocument document)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(IEnumerable<LayerItem> items)
        {
            foreach (TextItem text in items.OfType<TextItem>())
            {
                foreach (TextRun run in text.Runs)
                {
                    if (run.EmbeddedFont is not null)
                    {
                        continue;
                    }

                    StandardFonts.TryResolve(run.SourceFont ?? run.FontFamily, run.Bold, run.Italic,
                        out StandardFace face);

                    if (StandardFontFiles.Exists(face))
                    {
                        continue;
                    }

                    if (StandardFonts.CloneFamily(face) is { } clone && IsAvailable(clone))
                    {
                        continue;
                    }

                    missing.Add($"{run.SourceFont ?? run.FontFamily} → {StandardFonts.UrwFamily(face)}");
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
        return missing.ToArray();
    }

    /// <summary>
    /// Faces this document needs that are not the real URW face — either supplied by a
    /// metric-compatible platform clone, or not available at all. These are the ones
    /// worth offering to install: a clone has the same widths but not the same
    /// letterforms, so the page is the right shape and the wrong design.
    /// </summary>
    public static IReadOnlyList<string> Installable(CadDocument document)
    {
        var offered = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(IEnumerable<LayerItem> items)
        {
            foreach (TextItem text in items.OfType<TextItem>())
            {
                foreach (TextRun run in text.Runs)
                {
                    if (run.EmbeddedFont is not null)
                    {
                        continue;
                    }

                    StandardFonts.TryResolve(run.SourceFont ?? run.FontFamily, run.Bold, run.Italic,
                        out StandardFace face);

                    if (StandardFontFiles.Exists(face))
                    {
                        continue; // already the real face
                    }

                    string wanted = run.SourceFont ?? run.FontFamily;
                    bool clone = StandardFonts.CloneFamily(face) is { } c && IsAvailable(c);
                    offered.Add(clone
                        ? $"{wanted} → {StandardFonts.UrwFamily(face)} (currently a metric-compatible stand-in)"
                        : $"{wanted} → {StandardFonts.UrwFamily(face)} (missing)");
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
        return offered.ToArray();
    }

    /// <summary>Every face a standard font can need.</summary>
    public static IEnumerable<StandardFace> AllFaces()
    {
        foreach (StandardFontKind kind in new[]
                 {
                     StandardFontKind.Sans, StandardFontKind.Serif, StandardFontKind.Mono,
                     StandardFontKind.Symbol, StandardFontKind.Dingbats,
                 })
        {
            yield return new StandardFace(kind, false, false);
            if (kind is StandardFontKind.Symbol or StandardFontKind.Dingbats)
            {
                continue;
            }

            yield return new StandardFace(kind, true, false);
            yield return new StandardFace(kind, false, true);
            yield return new StandardFace(kind, true, true);
        }
    }

    /// <summary>Forgets registrations, after an install changes what is on disk.</summary>
    public static void Invalidate()
    {
        Registered.Clear();
        _scanned = false;
        StandardFontFiles.Invalidate();
    }

    /// <summary>
    /// Downloads URW faces into the user's font directory and registers them.
    ///
    /// This is the "download and install" action offered when a document needs a
    /// standard font the machine does not have. Installing for the user is what keeps
    /// this compatible with an MIT licence: the fonts are fetched from upstream at the
    /// user's request, not redistributed inside the application.
    /// </summary>
    /// <param name="faces">Faces to fetch; all of them when null.</param>
    /// <param name="http">Client to use, for testing.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<IReadOnlyList<string>> InstallAsync(
        IEnumerable<StandardFace>? faces = null,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(StandardFontFiles.UserFontDirectory);
        http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var installed = new List<string>();

        foreach (StandardFace face in faces ?? AllFaces())
        {
            // Fetch the .ttf (glyf) build. The .otf build of the same face is a CFF
            // programme, and an exported PDF embeds a programme as /FontFile2 with a
            // /CIDFontType2 descendant — which requires TrueType outlines. Same design,
            // same metrics, but only the glyf build is legal in that slot.
            string fileName = Path.GetFileNameWithoutExtension(StandardFonts.UrwFileName(face)) + ".ttf";
            if (File.Exists(Path.Combine(StandardFontFiles.UserFontDirectory, fileName)))
            {
                continue;
            }

            string destination = Path.Combine(StandardFontFiles.UserFontDirectory, fileName);

            byte[] data;
            try
            {
                data = await http
                    .GetByteArrayAsync(StandardFontFiles.UpstreamBase + fileName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not download {fileName} ({ex.Message}). Install the URW base-35 fonts " +
                    "from your package manager instead: fonts-urw-base35, or Ghostscript.", ex);
            }

            if (data.Length == 0)
            {
                continue;
            }

            await File.WriteAllBytesAsync(destination, data, cancellationToken).ConfigureAwait(false);
            installed.Add(fileName);
        }

        Invalidate();
        RegisterAvailable();
        return installed;
    }

    private static string? UrwFamilyFor(StandardFace face)
    {
        if (!_scanned)
        {
            RegisterAvailable();
        }

        string family = StandardFonts.UrwFamily(face);
        return EmbeddedFontManager.IsRegistered(family) ? family : null;
    }

    private static bool TryRegister(StandardFace face)
    {
        string family = StandardFonts.UrwFamily(face);
        if (Registered.Contains(family) || EmbeddedFontManager.IsRegistered(family))
        {
            return false;
        }

        if (StandardFontFiles.TryReadProgram(face) is not { Length: > 0 } program)
        {
            return false;
        }

        EmbeddedFontManager.Register(new[]
        {
            new EmbeddedFont
            {
                Format = EmbeddedFontFormat.TrueType,
                Program = program,
                BaseFont = Path.GetFileNameWithoutExtension(StandardFonts.UrwFileName(face)),
                FamilyName = family,
            },
        });

        Registered.Add(family);
        return true;
    }
}
