using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.Core.Model;
using VCCad.Core.Svg;

namespace VCCad.App.Tests;

/// <summary>
/// What two rasters of the same page actually measured.
///
/// The point of keeping the numbers rather than a bool is that a fidelity claim has to be a
/// measurement: "identical" is only interesting next to the size it was measured at, the number of
/// pixels that differed and by how much. A failure message that carries these is a report; one that
/// says "images differ" is a mystery.
/// </summary>
public sealed record RenderComparison(
    int Width,
    int Height,
    int OtherWidth,
    int OtherHeight,
    long Pixels,
    long DifferingPixels,
    double MeanAbsoluteError,
    int MaxChannelDifference,
    int Inset = 0)
{
    /// <summary>Whether the two rasters are the same shape, which is the first thing a diff must know.</summary>
    public bool SizesMatch => Width == OtherWidth && Height == OtherHeight;

    /// <summary>The share of compared pixels with any channel more than the tolerance away.</summary>
    public double DifferingProportion => Pixels == 0 ? 0.0 : (double)DifferingPixels / Pixels;

    /// <summary>True when this measurement ignored a border, which changes what the number means.</summary>
    public bool IsInset => Inset > 0;

    public override string ToString()
    {
        string measured = string.Create(
            CultureInfo.InvariantCulture,
            $"sizes {Width}x{Height} vs {OtherWidth}x{OtherHeight}" +
            $" ({(SizesMatch ? "match" : "MISMATCH")}), compared {Pixels} px," +
            $" mean|delta| {MeanAbsoluteError:F4}/255, max|delta| {MaxChannelDifference}," +
            $" differing {DifferingPixels}/{(Pixels == 0 ? 1 : Pixels)}" +
            $" ({DifferingProportion * 100.0:F3}%)");

        return IsInset
            ? measured + string.Create(CultureInfo.InvariantCulture, $", {Inset} px inset ignored")
            : measured;
    }
}

/// <summary>
/// Renders the same page twice — once with the editor's own renderer
/// (<see cref="VCCad.App.Views.PageRenderer"/>) and once with Inkscape, fed the SVG the editor
/// exports — and measures how far apart the two pictures are.
///
/// **Why a second renderer at all.** Our renderer measured against itself agrees perfectly and says
/// nothing. Every fidelity question ("is a 3 pt stroke three points wide?", "is that corner joined
/// the way the file says?") needs an implementation that was written by somebody else and can
/// therefore disagree. Inkscape is that implementation here: it is installed, it runs headless, and
/// it reads the *same document* through the SVG path rather than through a parallel model.
///
/// **The unit bridge.** The model's coordinates are PDF points and <see cref="SvgWriter"/> writes
/// them as SVG user units, which SVG defines as px (96 to the inch). Inkscape's <c>--export-dpi</c>
/// is dots per inch of the *physical* page, so the dpi that gives one pixel per model unit is
/// <c>pointDpi * 96 / 72</c>, not <c>pointDpi</c>. Passing the point dpi straight through would
/// render Inkscape's side at 75% and the diff would report a size mismatch that means nothing.
///
/// **Not a reference renderer for PDF.** This compares the editor's model render against the SVG
/// export of that same model. It measures the canvas and the SVG writer against a foreign
/// implementation; it does not measure the PDF exporter. That comparison — PDF through poppler —
/// already exists in <c>tools/qwen-corpus-tracker</c>.
///
/// **The editor's chrome is in the picture.** <c>PageRenderer</c> renders through the same
/// <c>CanvasWorkspace</c> a person sees, so the raster carries what the editor draws on top of the
/// artwork and the SVG does not: a one-pixel grey page frame around the artboard, and the dashed
/// bounding box and eight handles of anything that happens to be selected. Both are real
/// differences, both are the editor rather than the document, and a caller has to deal with them —
/// <see cref="Compare"/>'s <c>inset</c> for the frame, and clearing the selection before rendering
/// (as <c>scripts/compare-inkscape.ps1</c> does) for the handles.
/// </summary>
public static class InkscapeComparison
{
    /// <summary>SVG user units per model point: SVG's 96 units per inch over the model's 72.</summary>
    public const double SvgUnitsPerPoint = 96.0 / 72.0;

    /// <summary>How long a headless Inkscape render may take before it is treated as hung.</summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Inkscape, or <c>null</c> when this machine has none. <c>VCCAD_INKSCAPE</c> wins so a run can
    /// point at a specific build; then the usual install locations, then the PATH.
    ///
    /// Set <c>VCCAD_NO_INKSCAPE</c> to anything to pretend there is none. "Green here is not green
    /// there" applies to an optional tool exactly as much as to a font: the only way to check that a
    /// machine without Inkscape still passes is to make this machine behave like one, and the
    /// alternative — renaming the install directory — needs administrator rights.
    /// </summary>
    public static string? LocateInkscape()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VCCAD_NO_INKSCAPE")))
        {
            return null;
        }

        string? configured = Environment.GetEnvironmentVariable("VCCAD_INKSCAPE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (string candidate in new[]
                 {
                     @"C:\Program Files\Inkscape\bin\inkscape.exe",
                     @"C:\Program Files (x86)\Inkscape\bin\inkscape.exe",
                     "/usr/bin/inkscape",
                     "/usr/local/bin/inkscape",
                     "/snap/bin/inkscape",
                     "/Applications/Inkscape.app/Contents/MacOS/inkscape",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            string executable = OperatingSystem.IsWindows() ? "inkscape.exe" : "inkscape";
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), executable);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not a reason to fail locating a tool.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Rasterises an SVG with Inkscape at the dpi that makes one pixel per model point.
    ///
    /// The page area is exported rather than the drawing bounds, because the drawing bounds move
    /// whenever the artwork moves and the whole comparison is "is the artwork in the same place on
    /// the same sheet".
    /// </summary>
    /// <exception cref="InvalidOperationException">Inkscape is missing, failed, or wrote no file.</exception>
    public static byte[] RasteriseWithInkscape(string svg, double pointDpi, string? inkscapePath = null)
    {
        string inkscape = inkscapePath ?? LocateInkscape()
            ?? throw new InvalidOperationException(
                "Inkscape was not found. Set VCCAD_INKSCAPE to inkscape.exe, or install Inkscape.");

        double exportDpi = pointDpi * SvgUnitsPerPoint;
        string directory = Path.Combine(
            Path.GetTempPath(), "vccad-inkscape-compare", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string svgPath = Path.Combine(directory, "page.svg");
            string pngPath = Path.Combine(directory, "page.png");
            File.WriteAllText(svgPath, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var startInfo = new ProcessStartInfo(inkscape)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory,
            };
            startInfo.ArgumentList.Add("--export-type=png");
            startInfo.ArgumentList.Add("--export-area-page");
            startInfo.ArgumentList.Add("--export-dpi=" + exportDpi.ToString("0.####", CultureInfo.InvariantCulture));
            // Without this the export is transparent where the SVG paints nothing and the diff
            // measures the editor's white paper against Inkscape's alpha, not the artwork.
            startInfo.ArgumentList.Add("--export-background=#ffffff");
            startInfo.ArgumentList.Add("--export-background-opacity=255");
            startInfo.ArgumentList.Add("--export-filename=" + pngPath);
            startInfo.ArgumentList.Add(svgPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Inkscape could not be started: " + inkscape);

            // Drained on their own tasks rather than with ReadToEnd, so the timeout below is what
            // bounds the wait: reading to the end of a pipe on a hung process blocks for ever.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)RenderTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                throw new InvalidOperationException(
                    $"Inkscape did not finish within {RenderTimeout.TotalSeconds:0} s rendering {exportDpi:F4} dpi.");
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (!File.Exists(pngPath))
            {
                throw new InvalidOperationException(
                    $"Inkscape exited {process.ExitCode} without writing a PNG. stdout: {stdout} stderr: {stderr}");
            }

            return File.ReadAllBytes(pngPath);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that will not go away is not a test failure.
            }
        }
    }

    /// <summary>The three things a comparison is made of: the page's SVG and both rasters of it.</summary>
    public sealed record RenderPair(string Svg, byte[] VccadPng, byte[] InkscapePng);

    /// <summary>
    /// Renders one page twice: exports it to SVG, rasterises that with Inkscape, and rasterises the
    /// same page with the editor's renderer.
    ///
    /// The rasters are returned rather than immediately measured so a caller can measure the same
    /// pair more than once — the whole page and then the page without its border, for instance —
    /// without paying for a second Inkscape run.
    ///
    /// Set <c>VCCAD_RENDER_ARTIFACTS</c> to a directory to keep the SVG and both PNGs, which is what
    /// makes a number somebody reports checkable by somebody else — the same three files through
    /// <c>tools/inkscape-compare/compare.py</c> have to give the same measurement.
    /// </summary>
    /// <param name="renderWithVccad">
    /// The editor's renderer. Passed in rather than called directly so the harness does not have to
    /// own a <see cref="VCCad.App.Views.PageRenderer"/> workspace, and so a test can substitute a
    /// fixed raster when it is checking the comparison maths rather than the renderer.
    /// </param>
    public static RenderPair RenderBoth(
        CadDocument document,
        int pageIndex,
        double dpi,
        Func<CadDocument, int, double, byte[]?> renderWithVccad,
        string? inkscapePath = null)
    {
        byte[]? ours = renderWithVccad(document, pageIndex, dpi)
            ?? throw new InvalidOperationException(
                $"the editor's renderer returned nothing for page {pageIndex} at {dpi} dpi");

        string svg = SvgWriter.Write(document, pageIndex);
        byte[] theirs = RasteriseWithInkscape(svg, dpi, inkscapePath);

        SaveArtifacts(svg, ours, theirs);
        return new RenderPair(svg, ours, theirs);
    }

    /// <summary>The whole harness in one call: render both sides and measure them.</summary>
    public static RenderComparison Run(
        CadDocument document,
        int pageIndex,
        double dpi,
        Func<CadDocument, int, double, byte[]?> renderWithVccad,
        int channelTolerance = 8,
        int inset = 0,
        string? inkscapePath = null)
    {
        RenderPair pair = RenderBoth(document, pageIndex, dpi, renderWithVccad, inkscapePath);
        return Compare(pair.VccadPng, pair.InkscapePng, channelTolerance, inset);
    }

    /// <summary>Keeps the three files behind a measurement, when asked where to put them.</summary>
    private static void SaveArtifacts(string svg, byte[] vccadPng, byte[] inkscapePng)
    {
        string? directory = Environment.GetEnvironmentVariable("VCCAD_RENDER_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "page.svg"), svg, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(directory, "vccad.png"), vccadPng);
            File.WriteAllBytes(Path.Combine(directory, "inkscape.png"), inkscapePng);
        }
        catch (IOException exception)
        {
            // Artifacts are a convenience; failing to keep them must not fail a measurement.
            Console.WriteLine("could not keep render artifacts: " + exception.Message);
        }
    }

    /// <summary>
    /// Measures two PNG rasters. When the sizes differ the overlap is compared and
    /// <see cref="RenderComparison.SizesMatch"/> records the mismatch — a diff that threw here
    /// would hide the number a person needs to see.
    /// </summary>
    /// <param name="inset">
    /// Pixels to ignore along every edge. The editor's page render carries the artboard frame it
    /// draws on screen and a foreign rasteriser has no reason to, so a caller that wants to know
    /// about the *artwork* rather than about the chrome measures with the border cropped away. The
    /// inset is recorded in the result, because a number that quietly ignores part of the page is
    /// not the same number.
    /// </param>
    public static RenderComparison Compare(byte[] pngA, byte[] pngB, int channelTolerance = 8, int inset = 0)
    {
        Pixels a = Decode(pngA, "first");
        Pixels b = Decode(pngB, "second");

        int width = Math.Min(a.Width, b.Width) - (2 * inset);
        int height = Math.Min(a.Height, b.Height) - (2 * inset);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inset), inset, $"{inset} px of inset leaves nothing of a {a.Width}x{a.Height} image");
        }

        long pixels = (long)width * height;
        long differing = 0;
        long total = 0;
        int max = 0;

        for (int y = 0; y < height; y++)
        {
            int rowA = (y + inset) * a.Stride;
            int rowB = (y + inset) * b.Stride;
            for (int x = 0; x < width; x++)
            {
                int atA = rowA + ((x + inset) * 4);
                int atB = rowB + ((x + inset) * 4);

                int largest = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(a.Bytes[atA + channel] - b.Bytes[atB + channel]);
                    total += delta;
                    if (delta > largest)
                    {
                        largest = delta;
                    }
                }

                if (largest > max)
                {
                    max = largest;
                }

                if (largest > channelTolerance)
                {
                    differing++;
                }
            }
        }

        // Alpha is deliberately out of the mean: both sides are exported over white, so an alpha
        // difference would only ever be a decode artefact rather than a rendering difference.
        double mean = pixels == 0 ? 0.0 : (double)total / (pixels * 3);

        return new RenderComparison(a.Width, a.Height, b.Width, b.Height, pixels, differing, mean, max, inset);
    }

    private readonly record struct Pixels(int Width, int Height, int Stride, byte[] Bytes);

    private static Pixels Decode(byte[] png, string which)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var bitmap = new Bitmap(stream);
            int width = bitmap.PixelSize.Width;
            int height = bitmap.PixelSize.Height;
            int stride = width * 4;
            var bytes = new byte[stride * height];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            return new Pixels(width, height, stride, bytes);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"the {which} image is not a PNG this platform can decode ({png.Length} bytes)", exception);
        }
    }
}
