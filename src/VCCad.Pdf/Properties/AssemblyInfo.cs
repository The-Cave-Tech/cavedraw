using System.Runtime.CompilerServices;

// The assembler is an implementation detail, but the export tests construct a
// deliberately sidecar-less PDF through it to exercise the reader's error paths.
[assembly: InternalsVisibleTo("VCCad.Pdf.Tests")]
