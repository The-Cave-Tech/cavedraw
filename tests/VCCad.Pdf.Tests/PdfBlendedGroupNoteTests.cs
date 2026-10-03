using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A blend mode on a group is no longer declared, because the page applies it.**
///
/// This file used to assert the opposite. `PaintItem` wrote an ExtGState `/BM` for an item's blend and deliberately
/// not for an `ArtGroup`, and the loss was named in the export notes rather than left silent:
///
/// ```csharp
/// string? blendGs = item is ArtGroup ? null : blendStates.Gs(item.BlendMode);
/// ```
///
/// The exclusion was right - a `/BM` on a group's contents composites each child against the page rather than the
/// group against the page, which is a different picture - but the answer was missing. PDF expresses "composite this
/// as a unit" with an isolated transparency group, and the exporter now writes one (`PdfFormObjects`), so the note
/// that said the mode would not reach the page would itself be false.
///
/// The assertions are still a **pair**, so the note cannot come back quietly on a document that has nothing to
/// declare, and they check the positive half too: the blended group is a form XObject and the unblended one is not.
/// </summary>
public class PdfBlendedGroupNoteTests
{
    private static (byte[] Pdf, IReadOnlyList<string> Notes) Export(BlendMode groupBlend)
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

        byte[] pdf = PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);
        return (pdf, notes);
    }

    /// <summary>The acceptance: nothing is declared, and the group really is written as a form.</summary>
    [Fact]
    public void ABlendOnAGroupIsAppliedRatherThanDeclared()
    {
        (byte[] pdf, IReadOnlyList<string> notes) = Export(BlendMode.Multiply);
        string latin = System.Text.Encoding.Latin1.GetString(pdf);

        Assert.DoesNotContain(notes, note => note.Contains("does not apply", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, note => note.Contains("grp", StringComparison.Ordinal));

        Assert.Contains("/Subtype /Form", latin, StringComparison.Ordinal);
        Assert.Contains("/Group << /S /Transparency /I true /K false >>", latin, StringComparison.Ordinal);
    }

    /// <summary>**The control.** A group with no blend is not wrapped, so an ordinary document gains no object.</summary>
    [Fact]
    public void AGroupWithoutABlendIsNotWrapped()
    {
        (byte[] pdf, IReadOnlyList<string> notes) = Export(BlendMode.Normal);
        string latin = System.Text.Encoding.Latin1.GetString(pdf);

        Assert.DoesNotContain(notes, note => note.Contains("does not apply", StringComparison.Ordinal));
        Assert.DoesNotContain("/Subtype /Form", latin, StringComparison.Ordinal);
    }
}
