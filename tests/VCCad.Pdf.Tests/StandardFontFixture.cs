using VCCad.Pdf;

/// <summary>
/// Whether this machine can supply a standard PDF font programme.
///
/// VCCad bundles no fonts: Helvetica, Times, Courier, Symbol and ZapfDingbats are
/// supplied by the URW Core 35 files the machine already has (the distro package, or
/// Ghostscript's own directory). Tests that need a programme to embed skip when none is
/// installed, so a bare build agent stays green while still checking everything that
/// does not depend on a font being present.
/// </summary>
internal static class StandardFontFixture
{
    public static bool Available { get; } =
        StandardFontFiles.TryReadProgram(new StandardFace(StandardFontKind.Sans, false, false)) is not null;
}
