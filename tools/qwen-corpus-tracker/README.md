# qwen-corpus-tracker

Measures render fidelity of the importer against independent PDF renderers, using
the qwen vision model as the judge so progress is tracked over time.

For a deterministic sample of the veraPDF corpus it renders the source PDF and our
importer's re-export with poppler, then asks qwen to score visual similarity
(0-100). Results append to a CSV.

Usage:

    QWEN_BASE=https://your-endpoint.example/v1 QWEN_KEY=... \
    python3 track.py <corpus-root> <our-exports-dir> [sample-size] [seed] [report.csv]

Requires `pdftoppm` (poppler) on PATH and network access to the qwen endpoint.
