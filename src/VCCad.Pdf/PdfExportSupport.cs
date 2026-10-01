namespace VCCad.Pdf;

/// <summary>
/// What the PDF exporter can and cannot write, declared in **one** place.
///
/// The effects issue asks for this: "where an effect cannot be written to PDF (blur is the honest case), the panel
/// says so on the effect rather than leaving a person to find out from the exported file - a warning in the panel,
/// drawn from the same list of what the exporter supports." A warning is only worth anything if it is true, so this
/// list is not prose: `PdfExportSupportTests` **derives** each answer from the exported bytes - a feature is written
/// when turning it on changes the file and is not when it does not - and fails if this declaration and the exporter
/// disagree. Adding support for one of them therefore cannot leave a stale warning behind.
///
/// The declarations are about **fidelity**, not about whether the file is valid: an unsupported feature exports as
/// the artwork without it, which is a legal PDF and not the picture the author drew.
/// </summary>
public static class PdfExportSupport
{
    /// <summary>One thing a document can contain, and whether the PDF export carries it.</summary>
    public sealed record Feature(string Name, bool Written, string Note);

    public static IReadOnlyList<Feature> All { get; } = new[]
    {
        new Feature("gradient", true,
            "Written as a PDF shading."),

        new Feature("widthProfile", true,
            "Written as the outline the profile draws, through the same builder the canvas uses."),

        new Feature("outlineEffect", true,
            "Written as the outline it produces - the effects are geometry, not a decoration, so a roughened stroke " +
            "exports as the roughened shape."),

        new Feature("rasterEffect", false,
            "Not written. A blur or a glow is a pixel operation, and exporting one needs the stroke rasterised " +
            "into an image and placed; the exporter draws vectors only."),

        new Feature("filter", false,
            "Not written. A filtered object exports unfiltered - the filter is carried in the document and in the " +
            "SVG export, but nothing rasterises it for PDF."),

        new Feature("blendMode", false,
            "Not written. The value travels in the sidecar and the SVG export; the PDF is painted without it."),
    };

    /// <summary>The declaration for a feature, or null when this list does not know about it.</summary>
    public static Feature? Find(string name)
        => All.FirstOrDefault(feature => feature.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The features the export does **not** carry, which is what a warning is drawn from.</summary>
    public static IEnumerable<Feature> Lossy => All.Where(feature => !feature.Written);
}
