namespace VCCad.Pdf;

/// <summary>
/// Locates the URW Core 35 font files on the machine.
///
/// These are the conventional supply for the standard PDF fonts — what Ghostscript and
/// Inkscape use, carrying the original Helvetica/Times/Courier metrics. They are
/// AGPL-3, so they are never redistributed with VCCad; they are looked for where the
/// operating system or Ghostscript would already have put them, and can be fetched
/// into the user's own directory when a document needs one and none is installed.
///
/// Kept in this assembly (rather than the UI layer) because PDF *export* needs them
/// too: a document that names Helvetica without embedding it still has to have a
/// programme embedded in the output for the file to be self-contained.
/// </summary>
public static class StandardFontFiles
{
    private static readonly Dictionary<StandardFace, string?> Cache = new();

    /// <summary>Upstream revision the install action fetches from.</summary>
    public const string UpstreamTag = "20200910";

    /// <summary>Where the URW project publishes the individual faces.</summary>
    public const string UpstreamBase =
        "https://raw.githubusercontent.com/ArtifexSoftware/urw-base35-fonts/" + UpstreamTag + "/fonts/";

    /// <summary>Environment override, mostly for tests and unusual installs.</summary>
    public static string? OverrideDirectory => Environment.GetEnvironmentVariable("VCCAD_URW_FONTS");

    /// <summary>Where a copy installed on the user's behalf lives.</summary>
    public static string UserFontDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OverrideDirectory))
            {
                return OverrideDirectory;
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.Combine(Path.GetTempPath(), "vccad");
            }

            return Path.Combine(root, "VCCad", "fonts");
        }
    }

    /// <summary>Directories searched for URW font files, most specific first.</summary>
    public static IReadOnlyList<string> SearchDirectories()
    {
        var roots = new List<string> { UserFontDirectory };

        roots.Add("/usr/share/fonts/opentype/urw-base35");
        roots.Add("/usr/share/fonts/type1/urw-base35");
        roots.Add("/usr/share/fonts/urw-base35");
        roots.Add("/usr/local/share/fonts/urw-base35");

        // Ghostscript keeps the same fonts beside its interpreter.
        foreach (string gs in SafeDirectories("/usr/share/ghostscript"))
        {
            roots.Add(Path.Combine(gs, "Resource", "Font"));
            roots.Add(Path.Combine(gs, "fonts"));
        }

        foreach (string programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
            {
                foreach (string gs in SafeDirectories(Path.Combine(programFiles, "gs")))
                {
                    roots.Add(Path.Combine(gs, "Resource", "Font"));
                }
            }
        }

        return roots.Where(Directory.Exists).ToArray();
    }

    /// <summary>
    /// Directories searched for a face of **any** kind, including the platform's own font folder.
    ///
    /// Deliberately separate from <see cref="SearchDirectories"/>: widening that one would change how the fourteen
    /// *standard* faces resolve, which is a decision of its own. This is only for the lookup below, which asks a
    /// different question - "is there a face anywhere that can draw these characters" rather than "where is Helvetica".
    /// </summary>
    public static IReadOnlyList<string> CoverageDirectories()
    {
        var roots = new List<string>(SearchDirectories());

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(fonts))
        {
            roots.Add(fonts);
        }

        roots.Add("/usr/share/fonts");
        roots.Add("/usr/local/share/fonts");
        roots.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"));

        return roots.Where(Directory.Exists).ToArray();
    }

    /// <summary>
    /// The file for a face on this machine that has a glyph for **every** code point asked for, or null.
    ///
    /// This is the answer to a run the standard-font chain cannot draw - a document in a script the URW faces do not
    /// cover. Nothing is shipped: the face is read from where it already lives, which is the rule that applies to
    /// Helvetica as much as to anything else.
    ///
    /// Only **glyf** programmes are read, because that is what <see cref="TrueTypeFont"/> parses. A CFF-only face is
    /// skipped rather than mis-read, so the answer is "no face found" and the caller declares the loss.
    /// </summary>
    public static string? TryFindCovering(IReadOnlyCollection<int> codePoints)
    {
        if (codePoints.Count == 0)
        {
            return null;
        }

        foreach (string directory in CoverageDirectories())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.ttf", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                try
                {
                    VCCad.Pdf.Fonts.TrueTypeFont candidate = new(File.ReadAllBytes(file));
                    if (Covers(candidate, codePoints))
                    {
                        return file;
                    }
                }
                catch (Exception)
                {
                    // Not a programme this reader understands - a CFF-only face, or not a font at all.
                }
            }
        }

        return null;
    }

    private static bool Covers(VCCad.Pdf.Fonts.TrueTypeFont font, IReadOnlyCollection<int> codePoints)
    {
        foreach (int codePoint in codePoints)
        {
            if (codePoint is < 0 or > 0xFFFF || font.GlyphFor((char)codePoint) == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The file for a face, or null. Ghostscript ships the fonts without a file
    /// extension, so that form is tried as well as <c>.otf</c> and <c>.ttf</c>.
    /// </summary>
    public static string? TryFind(StandardFace face)
    {
        if (Cache.TryGetValue(face, out string? cached))
        {
            return cached;
        }

        string fileName = StandardFonts.UrwFileName(face);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string? path = null;

        foreach (string directory in SearchDirectories())
        {
            // The .ttf (glyf) build is preferred over the .otf (CFF) one. Both are the
            // same design with the same metrics, but an exported PDF embeds a programme
            // as /FontFile2 + /CIDFontType2, which requires TrueType glyf outlines —
            // embedding CFF data there is a font/type mismatch that strict readers warn
            // about. Ghostscript ships the fonts without an extension at all.
            foreach (string candidate in new[]
                     {
                         stem + ".ttf", fileName, stem, stem + ".t1", stem.ToLowerInvariant(),
                     })
            {
                string full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                {
                    path = full;
                    break;
                }
            }

            if (path is not null)
            {
                break;
            }
        }

        Cache[face] = path;
        return path;
    }

    /// <summary>Whether the face is on this machine.</summary>
    public static bool Exists(StandardFace face) => TryFind(face) is not null;

    /// <summary>The programme, or null when the face is not installed.</summary>
    public static byte[]? TryReadProgram(StandardFace face)
    {
        if (TryFind(face) is not { } path)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets what was found, after an install changes what is on disk.</summary>
    public static void Invalidate() => Cache.Clear();

    private static IEnumerable<string> SafeDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }
}
