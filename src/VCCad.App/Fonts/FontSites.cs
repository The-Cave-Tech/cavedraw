namespace VCCad.App.Fonts;

/// <summary>One place a missing font can be looked for, and what that site's licensing is like.</summary>
/// <param name="Name">What to call the site on screen.</param>
/// <param name="Search">Its search URL, with <c>{0}</c> where the escaped font name goes.</param>
/// <param name="Licence">What is known about the licences of the faces it carries.</param>
public sealed record FontSource(string Name, string Search, string Licence)
{
    /// <summary>The URL to look this font up at.</summary>
    public string UrlFor(string font)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, Search, Uri.EscapeDataString(font));
}

/// <summary>
/// **Where to get a font this machine cannot draw** (issue #262).
///
/// One list, used by the `fonts.findMissing` operation for a driver and by the chooser's *Search for font* control for a
/// person, so the two cannot tell different stories about where a face can be found.
///
/// Deliberately short, and deliberately with a licence note on every entry: the sites differ enormously in what they carry,
/// and "free" on one of them means free for personal use far more often than free for commercial work. Installing a face
/// whose licence forbids it is the person's decision to make - but knowingly, with the note in front of them. Nothing here
/// downloads anything, and nothing is ever redistributed with this application.
/// </summary>
public static class FontSites
{
    /// <summary>The places worth looking, in the order a person would try them.</summary>
    public static IReadOnlyList<FontSource> All { get; } = new FontSource[]
    {
        new("Google Fonts", "https://fonts.google.com/?query={0}",
            "Open-source faces, free to install and use."),
        new("Font Squirrel", "https://www.fontsquirrel.com/fonts/list/search?q={0}",
            "Only faces whose licence permits commercial use, each with its licence file."),
        new("MyFonts", "https://www.myfonts.com/search/{0}/",
            "Commercial foundry marketplace: licensed, usually paid."),
        new("DaFont", "https://www.dafont.com/search.php?q={0}",
            "Mixed licences - free for personal use far more often than for commercial; check each face."),
        new("Fonts In Use", "https://fontsinuse.com/search?q={0}",
            "A reference for identifying a face and who publishes it, rather than a download."),
    };

    /// <summary>The searchable places, which is everything except the identification reference.</summary>
    public static IReadOnlyList<FontSource> Downloads { get; }
        = All.Where(s => s.Name != "Fonts In Use").ToArray();

    /// <summary>What to tell a person about installing a face once they have one.</summary>
    public const string InstallHint =
        "Install a face into your own font directory and this application picks it up; nothing is redistributed with it. " +
        "fonts.list then reports the face itself instead of a substitute.";
}
