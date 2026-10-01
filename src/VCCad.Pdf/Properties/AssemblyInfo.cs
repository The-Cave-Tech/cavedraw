using System.Runtime.CompilerServices;

// The assembler is an implementation detail, but the export tests construct a
// deliberately sidecar-less PDF through it to exercise the reader's error paths.
// FilterRasteriser is the other one: the rasteriser is where "did the filter change the
// picture" is answerable without a second renderer, so the test suite draws through it
// directly rather than only through the exported bytes.
//
// VCCad.App.Tests is here for the canvas-versus-PDF agreement facts: the only reader that can
// disagree with the exporter about where art is placed is the one that parses the page
// (PdfImporter.TryImportVector), and `Import` cannot stand in for it because it prefers our own
// lossless sidecar and hands back the model the exporter was given (issue #170).
[assembly: InternalsVisibleTo("VCCad.Pdf.Tests")]
[assembly: InternalsVisibleTo("VCCad.App.Tests")]
