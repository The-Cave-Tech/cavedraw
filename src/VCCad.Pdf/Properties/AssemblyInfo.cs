using System.Runtime.CompilerServices;

// The assembler is an implementation detail, but the export tests construct a
// deliberately sidecar-less PDF through it to exercise the reader's error paths.
// FilterRasteriser is the other one: the rasteriser is where "did the filter change the
// picture" is answerable without a second renderer, so the test suite draws through it
// directly rather than only through the exported bytes.
[assembly: InternalsVisibleTo("VCCad.Pdf.Tests")]
