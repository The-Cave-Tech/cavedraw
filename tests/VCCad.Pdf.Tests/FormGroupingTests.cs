using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A form XObject is the file's own grouping, and it survives the import.
///
/// A form names a piece of artwork and draws it as one thing. Inlining its contents into the
/// page flattened that away: the Layers panel showed a layer holding 227 objects in one list
/// where the file has a group holding a clipping mask holding a handful, and nothing about
/// the document's shape survived.
///
/// The children keep page coordinates and the group's transform is identity, so this is a
/// change to the tree and not to the picture.
/// </summary>
public class FormGroupingTests
{
    /// <summary>
    /// A page drawing one form. The form's content and its optional-content layer are passed
    /// in, because both decide whether the group is made.
    /// </summary>
    private static byte[] PageWithForm(string formContent, string? markedLayer = null)
    {
        string pageContent = markedLayer is null
            ? "q /Fm0 Do Q"
            : $"q /OC /MC0 BDC /Fm0 Do EMC Q";

        string layerSetup = markedLayer is null
            ? string.Empty
            : "/OCProperties << /OCGs [6 0 R] /D << /Order [6 0 R] >> >> ";

        string occg = markedLayer is null
            ? string.Empty
            : $"/OCProperties 7 0 R ";

        var bodies = new List<(string Head, string? Stream)>
        {
            ("<< /Type /Catalog /Pages 2 0 R " + layerSetup + ">>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Fm0 5 0 R >> "
                + (markedLayer is null ? string.Empty : "/Properties << /MC0 6 0 R >> ")
                + ">> /Contents 4 0 R >>", null),
            (null, pageContent),
            ("<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] "
                + "/Resources << >> >>", formContent),
            (markedLayer is null ? "<< >>" : $"<< /Type /OCG /Name ({markedLayer}) >>", null),
        };

        bodies[1] = ($"<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null);

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(builder.Length);
            (string head, string? stream) = bodies[i];

            if (stream is null)
            {
                builder.Append($"{i + 1} 0 obj\n{head}\nendobj\n");
            }
            else
            {
                builder.Append($"{i + 1} 0 obj\n"
                    + (head is null
                        ? $"<< /Length {stream.Length} >>"
                        : head.Replace(">>", $"/Length {stream.Length} >>"))
                    + $"\nstream\n{stream}\nendstream\nendobj\n");
            }
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private const string ThreeBars =
        "0 0 0 rg 0 0 100 20 re f 0 0 0 rg 0 40 100 20 re f 0 0 0 rg 0 80 100 20 re f";

    private const string OneBar = "0 0 0 rg 0 0 100 20 re f";

    private static IReadOnlyList<LayerItem> Top(CadDocument document)
        => document.Artboards[0].Layers[0].Children;

    [Fact]
    public void AFormDrawingSeveralThingsBecomesAGroup()
    {
        CadDocument document = PdfImporter.Import(PageWithForm(ThreeBars));

        ArtGroup group = Assert.IsType<ArtGroup>(Assert.Single(Top(document)));
        Assert.Equal(3, group.Children.Count);
        Assert.All(group.Children, c => Assert.IsType<PathItem>(c));

        // Identity transform and page coordinates, so the picture is unchanged.
        Assert.Equal(1.0, group.Transform.A, 6);
        Assert.Equal(0.0, group.Transform.B, 6);
        Assert.Equal(0.0, group.Transform.C, 6);
        Assert.Equal(1.0, group.Transform.D, 6);
        Assert.Equal(0.0, group.Transform.E, 6);
        Assert.Equal(0.0, group.Transform.F, 6);
    }

    [Fact]
    public void AFormDrawingOneThingIsNotWrappedInAGroupOfOne()
    {
        // Every page whose content is a single form would gain a level that says nothing.
        CadDocument document = PdfImporter.Import(PageWithForm(OneBar));

        Assert.IsType<PathItem>(Assert.Single(Top(document)));
    }

    [Fact]
    public void AGroupIsInTheLayerItsContentsAreIn()
    {
        CadDocument document = PdfImporter.Import(PageWithForm(ThreeBars, markedLayer: "Layer 1"));

        Assert.Equal("Layer 1", document.Artboards[0].Layers[0].Name);
        Assert.IsType<ArtGroup>(Assert.Single(Top(document)));
    }

    [Fact]
    public void TheGroupEndsUpUnderItsArtboardWithItsChildren()
    {
        CadDocument document = PdfImporter.Import(PageWithForm(ThreeBars));
        ArtGroup group = Assert.IsType<ArtGroup>(Assert.Single(Top(document)));

        // Membership is real, not a copy: the children's container is the group, which is
        // what OwningLayer and ArtboardOffset walk.
        Assert.All(group.Children, c => Assert.Same(group, c.Container));
        Assert.Equal(document.Artboards[0].Layers[0], group.OwningLayer());
    }

    [Fact]
    public void GroupingDoesNotLoseAnythingTheFormDrew()
    {
        CadDocument document = PdfImporter.Import(PageWithForm(ThreeBars));
        ArtGroup group = Assert.IsType<ArtGroup>(Assert.Single(Top(document)));

        // Unlike a clip, a group adds a level: the count of drawn objects is what it was.
        Assert.Equal(3, group.Children.Count);
    }
}
