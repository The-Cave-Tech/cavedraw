using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A blend mode on a group is declared, because the page does not apply it.**
///
/// `PaintItem` writes an ExtGState `/BM` for an item's blend mode, but deliberately not for an `ArtGroup`:
///
/// ```csharp
/// string? blendGs = item is ArtGroup ? null : blendStates.Gs(item.BlendMode);
/// ```
///
/// That exclusion is **correct** - a `/BM` on a group's contents composites each child against the page rather than
/// the group against the page, which is the wrong picture; the right treatment is an isolated transparency group,
/// which this exporter does not write yet. What was wrong was doing it **silently**: a person sets a blend on a
/// group, sees it on the canvas, and finds it gone from the exported page with nothing said.
///
/// The assertion is a **pair** - the blended group is reported and an unblended one is not - so the note marks a
/// real loss rather than appearing on every document containing a group.
/// </summary>
public class PdfBlendedGroupNoteTests
{
    private static IReadOnlyList<string> NotesFor(BlendMode groupBlend)
    {
        CadDocument document = CadDocument.CreateDefault("Grouped");
        var path = new PathItem { Name = "inside", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(40, 40)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(120, 40)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(120, 120)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(40, 120)));

        var group = new ArtGroup { Name = "grp", BlendMode = groupBlend };
        group.AddItem(path);
        document.Artboards[0].Layers[0].AddItem(group);

        PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);
        return notes;
    }

    /// <summary>The acceptance: the mode is named, with the group it was set on.</summary>
    [Fact]
    public void ABlendOnAGroupIsDeclared()
    {
        IReadOnlyList<string> notes = NotesFor(BlendMode.Multiply);

        string? declared = notes.FirstOrDefault(
            note => note.Contains("does not apply", StringComparison.Ordinal));

        Assert.True(declared is not null,
            $"the loss must be declared; the notes were: [{string.Join(" | ", notes)}]");

        Assert.Contains("grp", declared!, StringComparison.Ordinal);
        Assert.Contains("multiply", declared!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>**The control.** A group with no blend produces no such note.</summary>
    [Fact]
    public void AGroupWithoutABlendIsNotDeclared()
    {
        IReadOnlyList<string> notes = NotesFor(BlendMode.Normal);

        Assert.DoesNotContain(notes, note => note.Contains("does not apply", StringComparison.Ordinal));
    }
}
