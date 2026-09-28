#!/usr/bin/env python3
"""Export the active VCCad document to PDF, so the same extractor can read both."""
import base64
import json
import pathlib
import sys
import urllib.request

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
OUT = ROOT / "artifacts" / "ours.pdf"

body = json.dumps({"op": "document.exportPdf", "params": {}}).encode()
request = urllib.request.Request(
    "http://127.0.0.1:5099/api/v1/invoke",
    data=body,
    headers={"Content-Type": "application/json"},
)
with urllib.request.urlopen(request, timeout=600) as response:
    payload = json.loads(response.read().decode())

if not payload.get("ok"):
    sys.exit(f"export failed: {payload}")

data = base64.b64decode(payload["result"]["pdfBase64"] if "pdfBase64" in payload["result"]
                        else payload["result"])
OUT.write_bytes(data)
print(f"wrote {OUT.name}: {len(data):,} bytes")
