using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The agreement test the filter issue names: the same filter reaching the canvas and reaching the SVG export
/// produces the same drawing.**
///
/// The two halves are real. The canvas is the actual <see cref="CanvasWorkspace"/>, rendered to a bitmap and read
/// back; the export is the real <c>SvgWriter</c> output, read back into a model by <c>SvgReader</c> - the reader
/// another viewer's file would come through. What is asserted is **the pixels**, because "the export carries the
/// filter" is not a claim a substring can make.
///
/// **The PDF export writes no filter.** `PdfExportSupport` records it and <c>document.exportSupport</c> reports
/// it: a filtered object exports to PDF as the artwork without the filter. So what is proven here is
/// canvas-versus-SVG only; canvas-versus-PDF is not proven, because there is no filter in that file to compare.
/// </summary>
public class FilterIdentityTests
{
    private const int Width = 600;
    private const int Height = 500;

    private static (Window Window, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();
        return (window, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static byte[] Pixels(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        target.Render(window);

        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    /// <summary>How many pixels are part-dark - what a soft shadow lays down and a hard edge does not.</summary>
    private static int MidTones(byte[] pixels)
    {
        int mid = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double luminance = ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
            if (luminance is > 0.15 and < 0.85)
            {
                mid++;
            }
        }

        return mid;
    }

    /// <summary>
    /// The graph is built entirely through the operations - created, then extended, wired, given a region and
    /// applied step by step - so what reaches the canvas is what a driver could have asked for rather than a model
    /// written by hand.
    ///
    /// The filter's answer is an **intermediate** result, not the last step with a name, so a dropped `Output` is
    /// visible in the picture rather than only in the model.
    /// </summary>
    private static (Window Window, EditorViewModel ViewModel, PathItem Path) FilteredDocument()
    {
        (Window window, EditorViewModel viewModel) = Host();
        Artboard board = viewModel.Document.Artboards[0];

        PathItem rect = PathFactory.CreateRectangle("block", new Rect2D(board.X + 120, board.Y + 120, 90, 90));
        rect.Fill = FillSpec.Solid(ColorRgb.Black);
        rect.Strokes.Clear();
        rect.Strokes.Add(StrokeSpec.None);
        board.Layers[0].AddItem(rect);
        viewModel.SelectObject(rect);
        Settle();

        var context = new AutomationContext { ViewModel = viewModel };
        Edit(context, "filter.create",
            """{"name":"drop","primitives":[{"kind":"gaussianBlur","in":"SourceAlpha","radius":4,"result":"soft"}]}""");
        Edit(context, "filter.addPrimitive",
            """{"name":"drop","kind":"offset","dx":6,"dy":6,"result":"moved"}""");
        Edit(context, "filter.addPrimitive",
            """{"name":"drop","kind":"flood","floodColor":[0,0,0],"floodOpacity":0.6,"result":"ink"}""");
        Edit(context, "filter.addPrimitive",
            """{"name":"drop","kind":"composite","in":"ink","in2":"moved","operator":"in","result":"shadow"}""");
        Edit(context, "filter.addPrimitive",
            """{"name":"drop","kind":"blend","in":"SourceGraphic","in2":"shadow","mode":"normal","result":"shaded"}""");
        Edit(context, "filter.connectPrimitive", """{"name":"drop","index":1,"in":"soft"}""");
        Edit(context, "filter.setRegion",
            """{"name":"drop","x":-0.35,"y":-0.35,"width":1.7,"height":1.7,"output":"shadow"}""");
        Edit(context, "filter.apply", """{"name":"drop"}""");

        Settle();
        return (window, viewModel, rect);
    }

    private static void Edit(AutomationContext context, string op, string parameters)
        => EditorOperations.Invoke(context, op, JsonSerializer.Deserialize<JsonElement>(parameters));

    [AvaloniaFact]
    public void TheSameFilterDrawsTheSameOnTheCanvasAndThroughTheSvgExport()
    {
        (Window window, EditorViewModel viewModel, PathItem path) = FilteredDocument();

        FilterSpec original = viewModel.Document.FindFilter("drop")!;
        Assert.Equal(5, original.Primitives.Count);
        Assert.Equal("shadow", original.Output);
        Assert.Equal("drop", path.FilterId);

        byte[] canvas = Pixels(window);

        // What the export carries, read back through the reader. Structurally it must be the same graph, and that
        // is asserted before the pixels so a failure says which half broke.
        string svg = SvgWriter.Write(viewModel.Document);
        CadDocument exported = SvgReader.Read(svg).Document;
        FilterSpec back = exported.FindFilter("drop")!;

        Assert.Equal(original, back);
        PathItem referenced = Assert.Single(exported.AllPaths(), item => item.FilterId is not null);
        Assert.Equal("drop", referenced.FilterId);

        // The canvas now draws through the filter that came back from the exported file, on the same geometry at
        // the same zoom - so any difference in the picture belongs to the export and to nothing else.
        viewModel.Document.AddFilter(back);
        viewModel.NotifyDocumentChanged();
        Settle();

        byte[] throughExport = Pixels(window);

        Assert.Equal(canvas, throughExport);

        // And the picture is genuinely filtered: a shadow's ramp is there, which a comparison of two unfiltered
        // renders would not have.
        Assert.True(MidTones(canvas) > 500, $"the filter should have drawn a shadow: {MidTones(canvas)} mid-tones");
    }

    /// <summary>
    /// The comparison above is only worth something because removing the filter changes the picture - a test that
    /// would pass with the filter absent is not an agreement test.
    /// </summary>
    [AvaloniaFact]
    public void TheAgreementComparisonIsNotVacuous()
    {
        (Window window, EditorViewModel viewModel, PathItem path) = FilteredDocument();

        byte[] filtered = Pixels(window);

        path.FilterId = null;
        viewModel.NotifyDocumentChanged();
        Settle();

        byte[] plain = Pixels(window);

        Assert.NotEqual(filtered, plain);
        Assert.True(MidTones(filtered) > MidTones(plain),
            $"a soft shadow lays down more tones than a hard edge: {MidTones(filtered)} against {MidTones(plain)}");
    }
}
