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

        new Feature("rasterEffect", true,
            "Written. A blur or a glow is a pixel operation with no PDF operator, so the stroke is drawn with every " +
            "stroke the path has, the effect graphs run over those pixels and the answer is placed as an image with " +
            "its coverage in an /SMask - the route the canvas already takes. The first stroke that carries any " +
            "effect supplies them, which is exact for a single-stroke path and the honest limit of drawing a path " +
            "as one picture. A gradient or hatch fill cannot be rasterised by this build, so that case exports as " +
            "vectors without the effect and says so in the export notes rather than in silence."),

        new Feature("filter", true,
            "Written. A filter is a raster operation, so the filtered object is drawn into its filter region, the " +
            "graph runs over those pixels and the answer is placed as an image with its coverage in an /SMask - " +
            "the same route the canvas takes, and the only one PDF has for a blur. A graph that reads " +
            "BackgroundImage cannot be evaluated by an exporter that draws one item at a time, and a region past " +
            "what this build will allocate is not, so those two export unfiltered and say so in the export notes " +
            "rather than in silence."),

        new Feature("blendMode", true,
            "Written. A leaf item's blend, and a stroke's, are composited with what is already on the page: a " +
            "PDF blend is the graphics state's /BM, so the mode becomes an ExtGState and the paint switches to it " +
            "with gs before it is drawn - a q/Q around a stroke keeps the mode off the strokes beside it in the " +
            "stack, and around a leaf item keeps it off the items painted after it."),

        new Feature("blendModeGroup", false,
            "Not written. CSS composites a group as a unit against the backdrop, which PDF expresses with an " +
            "isolated transparency group - a form XObject with /Group << /S /Transparency /I true /K false >>. " +
            "This exporter emits no form XObjects, and blending each child against the backdrop separately is a " +
            "different picture rather than a cheaper one, so a group's blend is left out and named here."),
    };

    /// <summary>The declaration for a feature, or null when this list does not know about it.</summary>
    public static Feature? Find(string name)
        => All.FirstOrDefault(feature => feature.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The features the export does **not** carry, which is what a warning is drawn from.</summary>
    public static IEnumerable<Feature> Lossy => All.Where(feature => !feature.Written);
}
