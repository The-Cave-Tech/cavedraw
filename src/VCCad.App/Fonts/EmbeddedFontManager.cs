using System.Collections;
using System.Reflection;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using VCCad.Core.Model;

namespace VCCad.App.Fonts;

/// <summary>
/// Serves imported embedded font programmes to Avalonia at runtime, so the canvas
/// renders imported text with the original face instead of a substitute. Avalonia
/// creates a glyph typeface from the raw programme, letting us draw by glyph id
/// (needed for bare CFF, which has no Unicode cmap).
/// </summary>
public sealed class EmbeddedFontManager : IFontCollection
{
    private static readonly EmbeddedFontManager Instance = new();
    private static readonly Dictionary<string, (byte[] Program, FontSimulations Simulations)> Fonts =
        new(StringComparer.OrdinalIgnoreCase);
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
            if (font.Program.Length > 0 && Fonts.TryAdd(font.FamilyName, (font.Program, FontSimulations.None)))
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

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
        FontStretch stretch, out IGlyphTypeface glyphTypeface)
    {
        glyphTypeface = null!;
        if (_impl is null || !Fonts.TryGetValue(familyName, out (byte[] Program, FontSimulations Simulations) entry))
        {
            return false;
        }

        if (_cache.TryGetValue(familyName, out IGlyphTypeface? cached))
        {
            glyphTypeface = cached;
            return true;
        }

        using Stream stream = new MemoryStream(entry.Program);
        // IFontManagerImpl.TryCreateGlyphTypeface(Stream, ...) is public at runtime
        // but hidden from compile-time metadata, so invoke it reflectively.
        object?[] args = { stream, entry.Simulations, null };
        if (StreamFactory is not null && StreamFactory.Invoke(_impl, args) is true && args[2] is IGlyphTypeface created)
        {
            _cache[familyName] = created;
            glyphTypeface = created;
            return true;
        }

        return false;
    }

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
