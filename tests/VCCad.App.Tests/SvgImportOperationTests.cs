using System.Text;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Importing SVG through the operation registry - the only way anything gets done here, so a person and a driver
/// do the same thing. Until these existed the reader was a library nobody could call.
/// </summary>
public class SvgImportOperationTests
{
    private const string Sample =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\" viewBox=\"0 0 100 50\">" +
        "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\"/>" +
        "<circle cx=\"50\" cy=\"25\" r=\"10\"/>" +
        "<path d=\"M0 0 L10 10\"/></svg>";

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host()
    {
        var vm = new EditorViewModel();
        return new AutomationContext { ViewModel = vm };
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ImportSvgReportsWhatItMade()
    {
        AutomationContext context = Host();

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.importSvg", Params(new { svgBase64 = Base64(Sample) })));

        Assert.Equal(3, result.GetProperty("objects").GetInt32());
        Assert.Equal(1, result.GetProperty("byElement").GetProperty("rect").GetInt32());
        Assert.Equal(1, result.GetProperty("byElement").GetProperty("circle").GetInt32());
        Assert.Equal(1, result.GetProperty("byElement").GetProperty("path").GetInt32());

        // And it is a real document with real geometry, not a tab with an empty one.
        CadDocument document = context.Document;
        Assert.Equal(75.0, document.Artboards[0].Width, 6);
        Assert.Equal(3, document.AllPaths().Count());
    }

    [Fact]
    public void ImportSvgNamesTheDocument()
    {
        AutomationContext context = Host();

        EditorOperations.Invoke(context, "document.importSvg",
            Params(new { svgBase64 = Base64(Sample), name = "Pattern 4" }));

        Assert.Equal("Pattern 4", context.Document.Name);
    }

    [Fact]
    public void ImportSvgFileTakesTheNameFromTheFile()
    {
        AutomationContext context = Host();
        string path = Path.Combine(Path.GetTempPath(), $"vccad-svg-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, Sample);

        try
        {
            JsonElement result = JsonSerializer.SerializeToElement(
                EditorOperations.Invoke(context, "document.importSvgFile", Params(new { path })));

            Assert.Equal(3, result.GetProperty("objects").GetInt32());
            Assert.Equal(Path.GetFileNameWithoutExtension(path), context.Document.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// **A `use` is imported as a link, and the link survives a save.**
    ///
    /// The instance carries the id it came from, so an edit to the definition can reach every instance of it.
    /// A sidecar that dropped the id would turn every instance into an anonymous copy, and the file would look
    /// identical until somebody edited the definition and only one of them changed.
    /// </summary>
    [Fact]
    public void AnInstanceSurvivesSaveAndReload()
    {
        AutomationContext context = Host();
        string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
            "width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use xlink:href=\"#box\" x=\"20\" y=\"30\"/></svg>";

        EditorOperations.Invoke(context, "document.importSvg", Params(new { svgBase64 = Base64(svg) }));

        ArtGroup instance = context.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("box", instance.SourceId);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(context.Document));

        ArtGroup back = reloaded.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("box", back.SourceId);
        Assert.Equal(20.0, back.Transform.Transform(new Point2D(0, 0)).X, 6);
        Assert.Equal(30.0, back.Transform.Transform(new Point2D(0, 0)).Y, 6);
    }

    /// <summary>A reference to something the file does not contain is reported by the operation, not dropped.</summary>
    [Fact]
    public void AMissingReferenceIsReportedByTheOperation()
    {
        AutomationContext context = Host();
        string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"50\" height=\"50\">" +
            "<use href=\"#nowhere\"/></svg>";

        JsonElement result = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, "document.importSvg", Params(new { svgBase64 = Base64(svg) })));

        JsonElement missing = result.GetProperty("missing");
        Assert.Equal(1, missing.GetArrayLength());
        Assert.Equal("nowhere", missing[0].GetString());
    }
    [Fact]
    public void AMalformedSvgIsReported()
    {
        AutomationContext context = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "document.importSvg",
                Params(new { svgBase64 = Base64("<svg><rect") })));

        Assert.Contains("well-formed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileThatDoesNotExistIsReported()
    {
        AutomationContext context = Host();

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "document.importSvgFile",
                Params(new { path = "C:/nothing/here.svg" })));
    }

    [Fact]
    public void Base64ThatIsNotBase64IsReported()
    {
        AutomationContext context = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "document.importSvg",
                Params(new { svgBase64 = "not base64 at all !!" })));

        Assert.Contains("not base64", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
