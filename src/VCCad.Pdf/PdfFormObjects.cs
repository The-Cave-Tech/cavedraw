using System.Text;
using VCCad.Core.Model;

namespace VCCad.Pdf;

/// <summary>
/// Writes a group's content as a **form XObject with an isolated transparency group**, which is how PDF says
/// "composite this as a unit".
///
/// CSS blends a group as one picture: the group is drawn on its own, then that result is composited with what is
/// behind it. PDF has no operator for that among the leaf painters - a `/BM` in the graphics state composites each
/// *paint* with the backdrop as it is drawn, which is a different picture - and expresses it instead as a form
/// XObject whose `/Group` is `/S /Transparency /I true /K false`: isolated, so its contents see a transparent
/// backdrop rather than the page, and non-knockout, so overlapping content inside accumulates normally.
///
/// This is the same shape as <see cref="PdfImageObjects"/> and <see cref="PdfShadingObjects"/>: the walk allocates an
/// object as it reaches something that needs one, and the page's resource dictionary names it afterwards.
///
/// **The content is written in page space.** The walk's `toDoc` frame is already composed into the operators, so the
/// form carries no matrix of its own and the `/BBox` is the page box - a box the artwork is known to be inside, so
/// nothing legitimate is clipped by it.
/// </summary>
internal sealed class PdfFormObjects
{
    private readonly PdfAssembler _assembler;
    private readonly string _box;
    private readonly List<(string Name, int Object, byte[] Content)> _forms = new();
    private int _count;

    /// <param name="box">The `/BBox` every form here is given, which is the artboard's own page box: the content is
    /// written in page space and the artwork is inside the page, so nothing legitimate is clipped by it.</param>
    public PdfFormObjects(PdfAssembler assembler, string box)
    {
        _assembler = assembler;
        _box = box;
    }

    /// <summary>
    /// Writes one group's content and returns the resource name to paint it with. The resource dictionary is not
    /// known yet - the walk has not finished discovering fonts, images and shadings - so it is patched in
    /// <see cref="Finish"/>, which is why the assembler allows a body to be set more than once.
    /// </summary>
    public string Add(IEnumerable<string> content)
    {
        string name = $"Fm{++_count}";
        int number = _assembler.Allocate();
        byte[] compressed = PdfDocumentExporter.CompressBytes(Encoding.UTF8.GetBytes(string.Join("\n", content) + "\n"));

        _forms.Add((name, number, compressed));
        _assembler.SetBody(number, Body(compressed, _box, string.Empty));
        return name;
    }

    /// <summary>
    /// Gives every form the resource dictionary its content actually draws with. The page's dictionary is passed
    /// whole: a form may name a font, an image, a shading or a state from anywhere in it, and an unused entry in a
    /// resource dictionary is legal while a missing one is a blank page.
    /// </summary>
    public void Finish(string resources)
    {
        foreach ((_, int number, byte[] content) in _forms)
        {
            _assembler.SetBody(number, Body(content, _box, resources));
        }
    }

    /// <summary>True once at least one group was written as a form.</summary>
    public bool Any => _forms.Count > 0;

    /// <summary>
    /// The <c>/XObject</c> entries, without the dictionary around them.
    ///
    /// A page's resource dictionary carries **one** `/XObject` key, so these entries are merged with the images' into
    /// one dictionary rather than each contributing its own - a second key under the same name would be a duplicate
    /// the reader resolves however it likes, losing whichever it did not keep.
    /// </summary>
    public string Entries()
    {
        var builder = new StringBuilder();
        foreach ((string name, int number, _) in _forms)
        {
            builder.Append('/').Append(name).Append(' ').Append(number).Append(" 0 R ");
        }

        return builder.ToString();
    }

    private static byte[] Body(byte[] compressed, string box, string resources)
        => PdfDocumentExporter.MakeStreamObject(
            compressed,
            $" /Type /XObject /Subtype /Form /BBox [{box}] " +
            "/Group << /S /Transparency /I true /K false >>" +
            (resources.Length == 0 ? string.Empty : resources));
}
