using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using VCCad.Core.Model;
using VCCad.Pdf.Fonts;

namespace VCCad.App.Fonts;

/// <summary>
/// Serves imported embedded font programmes to Avalonia at runtime, so the canvas
/// renders imported text with the original face instead of a substitute. Avalonia
/// creates a glyph typeface from the raw programme, letting us draw by glyph id
/// (needed for bare CFF, which has no Unicode cmap).
///
/// Two platform realities shape this class:
///
/// <list type="number">
/// <item>
/// <b>Bare CFF is not an sfnt container.</b> PDF embeds it directly
/// (<c>/FontFile3 /Subtype /Type1C</c>), but platform font managers only load
/// sfnt, so the programme is wrapped by <see cref="CffSfnt"/> before being handed
/// over. Without that step the original outlines are simply unavailable.
/// </item>
/// <item>
/// <b>The global font manager answers with a fallback face.</b> Asking
/// <c>FontManager.Current</c> for an unknown family still reports success and
/// returns the default typeface. A caller drawing by glyph id would then index the
/// imported ids into an unrelated font and paint garbage — which is exactly what
/// happens if this class is bypassed. Draw by glyph id only through
/// <see cref="TryGetEmbeddedGlyphTypeface"/>.
/// </item>
/// </list>
/// </summary>
public sealed class EmbeddedFontManager : IFontCollection
{
    private static readonly EmbeddedFontManager Instance = new();
    private static readonly Dictionary<string, EmbeddedFont> Fonts = new(StringComparer.OrdinalIgnoreCase);
    private static bool _registered;

    private readonly Dictionary<string, IGlyphTypeface> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IFontManagerImpl? _impl;

    private static readonly MethodInfo? StreamFactory = typeof(IFontManagerImpl).GetMethod(
        "TryCreateGlyphTypeface",
        new[] { typeof(Stream), typeof(FontSimulations), typeof(IGlyphTypeface).MakeByRefType() });

    public Uri Key { get; } = new("fonts:vccad-embedded");

    /// <summary>Registers every embedded programme used by the document.</summary>
    public static void Register(IEnumerable<EmbeddedFont> fonts)
    {
        bool added = false;
        foreach (EmbeddedFont font in fonts)
        {
            if (font.Program.Length > 0 && Fonts.TryAdd(font.FamilyName, font))
            {
                added = true;
            }
        }

        if (!_registered)
        {
            FontManager.Current.AddFontCollection(Instance);
            _registered = true;
        }
        else if (added)
        {
            Instance._cache.Clear();
        }
    }

    void IFontCollection.Initialize(IFontManagerImpl fontManager) => _impl = fontManager;

    /// <summary>
    /// Resolves the glyph typeface for an imported programme, strictly from this
    /// collection.
    ///
    /// Callers that draw <em>by glyph id</em> must use this rather than
    /// <c>FontManager.Current</c>. When a family is unknown the global manager
    /// answers with its fallback face and still reports success, so the imported
    /// glyph ids end up indexing an unrelated font and the canvas paints garbage
    /// (observed on the Win32 backend). Returning false here lets the caller fall
    /// back to the readable decoded text instead.
    /// </summary>
    public static bool TryGetEmbeddedGlyphTypeface(string familyName, out IGlyphTypeface glyphTypeface)
        => Instance.TryResolve(familyName, out glyphTypeface);

    /// <summary>True when a programme with this family name has been registered.</summary>
    public static bool IsRegistered(string familyName) => Fonts.ContainsKey(familyName);

    /// <summary>
    /// Forgets every registered programme. The registry is process-wide, so tests that
    /// register their own fonts must reset it or they bleed into each other.
    /// </summary>
    public static void Reset()
    {
        Fonts.Clear();
        Instance._cache.Clear();
    }

    /// <summary>How many programmes are registered.</summary>
    public static int RegisteredCount => Fonts.Count;

    /// <summary>Whether Avalonia has handed us the platform font manager yet.</summary>
    public static bool IsInitialized => Instance._impl is not null;

    private bool TryResolve(string familyName, out IGlyphTypeface glyphTypeface)
    {
        glyphTypeface = null!;
        if (_impl is null || !Fonts.TryGetValue(familyName, out EmbeddedFont? font))
        {
            return false;
        }

        if (_cache.TryGetValue(familyName, out IGlyphTypeface? cached))
        {
            glyphTypeface = cached;
            return true;
        }

        // The programme as PDF stores it. TrueType/OpenType containers load
        // directly; bare CFF does not, so fall back to an sfnt wrapper.
        if (TryCreate(font.Program, out IGlyphTypeface created))
        {
            _cache[familyName] = created;
            glyphTypeface = created;
            return true;
        }

        if (CffSfnt.IsBareCff(font.Program))
        {
            byte[]? wrapped = CffSfnt.Wrap(
                font.Program,
                font.FamilyName,
                font.Ascent,
                font.Descent,
                font.FontBBox,
                unitsPerEm: 1000);

            if (wrapped is not null && TryCreate(wrapped, out created))
            {
                _cache[familyName] = created;
                glyphTypeface = created;
                return true;
            }
        }

        // The family is ours but the platform refused the programme: report failure
        // so the caller substitutes readable text rather than drawing garbage.
        return false;
    }

    /// <summary>
    /// Invokes the platform font manager's stream overload.
    /// <c>IFontManagerImpl.TryCreateGlyphTypeface(Stream, ...)</c> is public at
    /// runtime but hidden from compile-time metadata, so it is invoked reflectively.
    /// </summary>
    private bool TryCreate(byte[] program, out IGlyphTypeface glyphTypeface)
    {
        glyphTypeface = null!;
        if (_impl is null || StreamFactory is null)
        {
            return false;
        }

        using Stream stream = new MemoryStream(program, writable: false);
        object?[] args = { stream, FontSimulations.None, null };
        if (StreamFactory.Invoke(_impl, args) is true && args[2] is IGlyphTypeface created)
        {
            glyphTypeface = created;
            return true;
        }

        return false;
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
        FontStretch stretch, out IGlyphTypeface glyphTypeface)
        => TryResolve(familyName, out glyphTypeface);

    public bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight, FontStretch stretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        typeface = default;
        return false;
    }

    public int Count => Fonts.Count;

    public FontFamily this[int index] => new(Fonts.Keys.ElementAt(index));

    public IEnumerator<FontFamily> GetEnumerator()
        => Fonts.Keys.Select(f => new FontFamily(f)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        // Programmes are cached statically; nothing to release here.
    }

    public bool TryGetFamilyTypefaces(string familyName, out IReadOnlyList<Typeface> familyTypefaces)
    {
        if (Fonts.ContainsKey(familyName))
        {
            familyTypefaces = new[] { new Typeface(new FontFamily(familyName)) };
            return true;
        }

        familyTypefaces = Array.Empty<Typeface>();
        return false;
    }
}
