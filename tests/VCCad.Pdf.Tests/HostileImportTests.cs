using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Hostile-input tests for the PDF importer and the lossless sidecar.
///
/// These are adversarial on purpose. A parser is a parser: the input is whatever
/// the file says it is, and the file may be truncated, lying, or written by
/// something that is not a PDF writer at all. Two rules govern every case here:
///
/// 1. A <b>silent wrong answer is worse than a crash.</b> If malformed input
///    produces a plausible-looking document, that is a defect even though nothing
///    threw.
/// 2. Malformed input must be <b>handled or refused with a clear message</b> —
///    never an unhandled exception and never a hang.
///
/// Tests whose names are statements (…IsRefused, …IsNotSilentlyAccepted) fail
/// against the current build and pin real defects; they are deliberately left
/// failing. See the class-level report in the hand-off note. Tests that pin
/// behaviour that is already correct are grouped at the bottom.
/// </summary>
public class HostileImportTests
{
    private readonly ITestOutputHelper _out;
    public HostileImportTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------------
    // Defect 1 — input that is not a PDF at all is silently reported as a
    // blank A4 page rather than refused.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> NonPdfInputs()
    {
        yield return new object[] { "empty file", Array.Empty<byte>() };
        yield return new object[] { "three arbitrary bytes", new byte[] { 1, 2, 3 } };
        yield return new object[] { "wrong magic (PNG)", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } };
        yield return new object[] { "header then prose", Encoding.ASCII.GetBytes("%PDF-1.4\nthis is not a pdf, it is a note about one") };
        yield return new object[] { "header only", Encoding.ASCII.GetBytes("%PDF-1.4\n") };
        yield return new object[] { "header then binary noise", Encoding.Latin1.GetBytes("%PDF-1.4\n" + new string('\u00ff', 256)) };
        yield return new object[] { "xref header with no body", Encoding.ASCII.GetBytes("xref\n0 2\n0000000000 65535 f \n0000000009 00000 n \ntrailer\n<< /Size 2 >>\nstartxref\n") };
        yield return new object[] { "valid header, truncated real PDF", Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n") };
    }

    [Theory]
    [MemberData(nameof(NonPdfInputs))]
    public void InputThatIsNotAPdfIsRefusedRatherThanImportedAsABlankA4(string name, byte[] bytes)
    {
        // Corpus-test rule: the public API must not throw on a file the vector
        // importer cannot read, BECAUSE that is a real PDF. This is not a PDF at
        // all, and the fallback gives the person a blank A4 artboard with no
        // indication that nothing was read. A silent wrong answer.
        Exception? thrown = Record.Exception(() => PdfImporter.Import(bytes));

        if (thrown is not null)
        {
            _out.WriteLine($"{name}: refused with {thrown.GetType().Name}: {thrown.Message}");
            return;
        }

        CadDocument doc = PdfImporter.Import(bytes);
        Artboard first = doc.Artboards[0];
        Assert.Fail(
            $"{name}: Import returned a plausible document instead of refusing — " +
            $"artboards={doc.Artboards.Count}, first={first.Width:0.##}x{first.Height:0.##}, " +
            $"items={doc.Artboards.Sum(a => a.Layers.Sum(l => l.Children.Count))}. " +
            "Nothing in the result says the file could not be read.");
    }

    // ------------------------------------------------------------------
    // Defect 2 — a MediaBox holding NaN or an overflowing number becomes an
    // artboard of that size. No error, and the document can no longer be saved.
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> NonFiniteMediaBoxes()
    {
        yield return new object[] { "NaN", "[NaN NaN NaN NaN]" };
        yield return new object[] { "NaN upper corner", "[0 0 NaN NaN]" };
        yield return new object[] { "overflowing exponent", "[0 0 1e400 1e400]" };
        yield return new object[] { "negative overflow", "[-1e308 -1e308 1e308 1e308]" };
        yield return new object[] { "lowercase infinity", "[0 0 -infinity Infinity]" };
    }

    /// <summary>
    /// The fallback page-size scanner parses MediaBox numbers with
    /// <c>double.TryParse</c>, which accepts "NaN" and "Infinity" and overflows
    /// "1e400" to infinity. Nothing downstream validates the result.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonFiniteMediaBoxes))]
    public void AMediaBoxWithNonFiniteNumbersDoesNotBecomeANonFiniteArtboard(string name, string box)
    {
        byte[] bytes = PdfWithoutCatalogFor(box);

        CadDocument doc;
        try
        {
            doc = PdfImporter.Import(bytes);
        }
        catch (Exception ex)
        {
            _out.WriteLine($"{name}: refused with {ex.GetType().Name} — acceptable");
            return;
        }

        Artboard artboard = doc.Artboards[0];
        Assert.True(
            double.IsFinite(artboard.Width) && double.IsFinite(artboard.Height),
            $"{name}: MediaBox {box} imported as an artboard of {artboard.Width}x{artboard.Height}");
        Assert.True(
            artboard.Width > 0 && artboard.Height > 0,
            $"{name}: MediaBox {box} imported as an empty artboard of {artboard.Width}x{artboard.Height}");
    }

    /// <summary>
    /// Same input, seen from the other end: the importer produces a document the
    /// serializer cannot represent, so "open it, then save it" fails inside JSON
    /// with a message about infinity that names neither the file nor the artboard.
    /// </summary>
    [Fact]
    public void ADocumentFromANonFiniteMediaBoxCanStillBeSaved()
    {
        CadDocument doc = PdfImporter.Import(PdfWithoutCatalogFor("[NaN NaN NaN NaN]"));

        Exception? thrown = Record.Exception(() => VccadDocumentSerializer.Serialize(doc));
        Assert.True(thrown is null,
            $"the importer handed back a document that cannot be saved: {thrown?.GetType().Name}: {thrown?.Message}");
    }

    private static byte[] PdfWithoutCatalogFor(string mediaBox)
        => Encoding.Latin1.GetBytes(
            $"%PDF-1.4\n1 0 obj\n<< /Type /Page /MediaBox {mediaBox} >>\nendobj\n%%EOF\n");

    // ------------------------------------------------------------------
    // Defect 3 — a declared xref /Count is trusted as a loop bound, so a tiny
    // file can ask the importer to do billions of iterations.
    // ------------------------------------------------------------------

    /// <summary>
    /// Declared-count parse work. Without a bogus count this file parses in
    /// well under a millisecond; with one it produces one loop iteration per
    /// declared entry (measured ~3.8 ns each on the dev box, so 2e9 ≈ 7–8 s and
    /// 1e12 ≈ an hour). The work is proportional to a number in the file, not to
    /// the file.
    /// </summary>
    [Fact]
    public void AnXrefDeclaringBillionsOfEntriesCannotMakeATwoHundredByteFileParseForSeconds()
    {
        byte[] bytes = XrefDeclaring(2_000_000_000);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool finished = Task.Run(() => PdfImporter.Import(bytes)).Wait(TimeSpan.FromSeconds(1));
        elapsed.Stop();

        Assert.True(finished,
            $"a {bytes.Length}-byte file declaring 2,000,000,000 xref entries was still parsing " +
            $"after {elapsed.ElapsedMilliseconds} ms: the xref loop iterates the declared count " +
            "rather than the bytes actually present.");
    }

    private static byte[] XrefDeclaring(long count)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        long xrefOffset = sb.Length;
        sb.Append("xref\n0 ").Append(count).Append('\n');
        sb.Append("0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size ").Append(count).Append(" >>\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    // Defect 4 — deeply nested arrays recurse without a depth guard. The
    // failure mode is a StackOverflowException, which is uncatchable and kills
    // the process, so this cannot be a plain failing test: it would abort the
    // whole run and hide every other result. It is recorded here as a repro
    // that a person can run deliberately.
    // ------------------------------------------------------------------

    /// <summary>
    /// REPRO (do not remove the Skip while the defect stands):
    /// 8,000 nested "[" bytes in a 16 kB file crash the test host with an
    /// uncatchable StackOverflowException inside <c>PdfReader.ReadArray</c> →
    /// <c>ReadObject</c>. Depth 6,000 survived on this machine; 8,000 crashed.
    /// The same recursion is reached from the content-stream operand reader.
    /// Requirement: refuse nesting beyond a documented limit with a catchable
    /// exception.
    /// </summary>
    [Fact(Skip =
        "Process-killing defect, not a test failure: depth 8,000 gives an uncatchable " +
        "StackOverflowException that aborts the entire test run (verified on the dev box). " +
        "Remove this Skip once PdfReader enforces a depth limit.")]
    public void DeeplyNestedArraysAreRefusedWithACatchableException()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n" + Nesting(8_000) + "\nendobj\n");

        try
        {
            PdfImporter.Import(bytes);
        }
        catch (Exception ex) when (ex is not StackOverflowException)
        {
            _out.WriteLine($"refused with {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Assert.Fail("a document nested 8,000 arrays deep did not raise a catchable refusal.");
    }

    private static string Nesting(int depth) => new string('[', depth) + new string(']', depth);

    // ------------------------------------------------------------------
    // Behaviour that is already correct — pinned so it stays that way.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("truncated zlib", new byte[] { 0x78, 0x9C, 0x00, 0x01 })]
    [InlineData("zlib header then prose", new byte[] { 0x78, 0x9C, 0x6E, 0x6F, 0x74, 0x20, 0x64, 0x65, 0x66, 0x6C, 0x61, 0x74, 0x65 })]
    [InlineData("zstd magic then garbage", new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x02, 0x03 })]
    public void GarbagePrivateDataIsIgnoredRatherThanThrown(string name, byte[] payload)
    {
        Assert.False(Ai.AiPrivateDataCodec.TryInflate(payload, out byte[] inflated));
        Assert.Empty(inflated);
        Assert.False(Ai.AiPrivateDataCodec.TryDecompressZstd(payload, out byte[] decompressed));
        Assert.Empty(decompressed);
    }

    [Fact]
    public void APdfCarryingGarbageIllustratorPrivateDataStillImports()
    {
        byte[] bytes = Encoding.Latin1.GetBytes(
            "%PDF-1.4\n%AI12_CompressedData" + new string('X', 256) + "\n%%EOF\n");

        CadDocument doc = PdfImporter.Import(bytes);

        Assert.NotNull(doc);
        Assert.Null(doc.AiPrivateData);
    }

    [Fact]
    public void ClippingToAnEmptyOrNonFiniteRectangleTerminatesAndKeepsNothing()
    {
        var path = new PathItem();
        SubPath square = path.AddSubPath(true);
        foreach (Point2D p in new[]
                 {
                     new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10),
                 })
        {
            square.AppendNode(p);
        }

        Assert.Empty(PathClipper.Clip(new[] { square }, Rect2D.Empty));
        Assert.Empty(PathClipper.Clip(new[] { square }, new Rect2D(double.NaN, double.NaN, double.NaN, double.NaN)));
    }

    [Fact]
    public void ClippingPathologicalGeometryTerminates()
    {
        var huge = new PathItem();
        SubPath line = huge.AddSubPath(false);
        line.AppendNode(new Point2D(-1e300, 0));
        line.AppendNode(new Point2D(1e300, 0));

        var nan = new PathItem();
        SubPath broken = nan.AddSubPath(false);
        broken.AppendNode(new Point2D(double.NaN, 0));
        broken.AppendNode(new Point2D(10, 10));

        Assert.Empty(PathClipper.Clip(new[] { line }, new Rect2D(0, 0, 10, 10)));
        Assert.Empty(PathClipper.Clip(new[] { broken }, new Rect2D(0, 0, 10, 10)));
    }

    [Fact]
    public void AnUnterminatedOrZeroLengthStreamDoesNotThrow()
    {
        foreach (string body in new[]
                 {
                     "<< /Length 0 >>\nstream\n\nendstream",
                     "<< /Length 0 >>\nstream\n",
                     "<< /Length 999999999 >>\nstream\n",
                     "<< /Length -5 >>\nstream\nabc",
                 })
        {
            var assembler = new PdfAssembler();
            int catalog = assembler.Allocate();
            int pages = assembler.Allocate();
            int page = assembler.Allocate();
            int content = assembler.Allocate();
            assembler.SetBody(content, body);
            assembler.SetBody(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 200 200] /Contents {content} 0 R /Resources << >> >>");
            assembler.SetBody(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
            assembler.SetBody(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");

            Exception? thrown = Record.Exception(() => PdfImporter.Import(assembler.Serialize(catalog)));
            Assert.Null(thrown);
        }
    }
}
