using System.Text;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// SVG export through the operation registry, which is the way everything is done here - a person saving a file and
/// a driver asking for one run the same code.
/// </summary>
public class SvgExportOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host(out PathItem path)
    {
        var vm = new EditorViewModel();
        path = new PathItem { Name = "line", Fill = FillSpec.Solid(new ColorRgb(0, 1, 0)) };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(10, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(110, 20)));
        path.Stroke = new StrokeSpec(true, new ColorRgb(1, 0, 0), 5, StrokeCap.Round, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, new DashPattern(new[] { 4.0, 2.0 }, 1.5));

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        return new AutomationContext { ViewModel = vm };
    }

    private static string Decode(JsonElement result)
        => Encoding.UTF8.GetString(Convert.FromBase64String(result.GetProperty("svgBase64").GetString()!));

    [Fact]
    public void ExportSvgReturnsAFileWithTheStrokesOnIt()
    {
        AutomationContext context = Host(out _);

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSvg", default));

        string svg = Decode(result);

        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#ff0000\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-width=\"5\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-linecap=\"round\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-dasharray=\"4 2\"", svg, StringComparison.Ordinal);
        Assert.Contains("stroke-dashoffset=\"1.5\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#00ff00\"", svg, StringComparison.Ordinal);
    }

    /// <summary>**What is exported is what the importer reads**, which is the round trip the pair has to satisfy.</summary>
    [Fact]
    public void ExportedSvgImportsBackWithTheSameStroke()
    {
        AutomationContext context = Host(out PathItem original);

        string svg = Decode(JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSvg", default)));

        StrokeSpec back = SvgReader.Read(svg).Document.AllPaths().Single().Stroke;

        Assert.Equal(original.Stroke.Width, back.Width, 6);
        Assert.Equal(original.Stroke.Cap, back.Cap);
        Assert.Equal(original.Stroke.Dash.Segments.ToArray(), back.Dash.Segments.ToArray());
        Assert.Equal(original.Stroke.Dash.Offset, back.Dash.Offset, 6);
        Assert.Equal(original.Stroke.Color.R, back.Color.R, 3);
    }

    /// <summary>One page exports that page, with its own canvas rather than the whole document's.</summary>
    [Fact]
    public void ExportSvgCanWriteASinglePage()
    {
        AutomationContext context = Host(out _);
        context.Document.AddArtboard(new Size2D(50, 40), "second");

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSvg", Params(new { page = 1 })));

        string svg = Decode(result);

        Assert.Contains("viewBox=\"0 0 50 40\"", svg, StringComparison.Ordinal);

        // The first artboard's object is not in the second page's file.
        Assert.DoesNotContain("stroke=\"#ff0000\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveSvgToFileWritesAFile()
    {
        AutomationContext context = Host(out _);
        string path = Path.Combine(Path.GetTempPath(), $"vccad-svg-out-{Guid.NewGuid():N}.svg");

        try
        {
            JsonElement result = JsonSerializer.SerializeToElement(
                EditorOperations.Invoke(context, "document.saveSvgToFile", Params(new { path })));

            Assert.True(File.Exists(path));
            Assert.True(result.GetProperty("characters").GetInt32() > 0);

            // And the file is one this application can read back, which is the point of writing it.
            Assert.Single(SvgReader.ReadFile(path).Document.AllPaths());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
