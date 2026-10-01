#!/usr/bin/env python3
"""
Ask the vision model what actually differs between our render and the reference.

A pixel delta can say *where* two rasters differ but never *what* is wrong: a filled
glyph counter, a dropped letter and a lost fill all look like the same red blob. This
sends the reference and our render of the same page to the model and records its reading.
"""
import base64
import io
import json
import pathlib
import sys
import urllib.request

from PIL import Image, ImageDraw

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
WORK = ROOT / "artifacts" / "catalogue"
BASE = os.environ.get("QWEN_BASE", "")
KEY = os.environ.get("QWEN_KEY", "")
MODEL = os.environ.get("QWEN_MODEL", "")

PROMPT = (
    "These two images are the SAME page of a PDF pattern, rendered by two different "
    "engines. The FIRST image is the reference (correct). The SECOND image is our "
    "renderer's output.\n\n"
    "Compare them carefully and list EVERY visible difference. Be specific and concrete:\n"
    "- text: quote the exact string in the reference and what ours shows instead "
    "(e.g. 'DEVANT ET DOS' vs 'DEVANT ET D')\n"
    "- letters that are missing, or whose enclosed hole (counter) is filled solid\n"
    "- shapes or filled areas present in the reference but absent, white or wrongly "
    "coloured in ours (name the colour and the approximate position)\n"
    "- line work present in one and not the other\n\n"
    "Work through the page systematically: header, labels, each pattern piece, the "
    "bottom footer. If a region matches, say nothing about it. Report only differences. "
    "Be exhaustive but concrete. Do not speculate about causes."
)


def panel(png: pathlib.Path, label: str, width: int) -> Image.Image:
    image = Image.open(png).convert("RGB")
    scale = width / image.width
    image = image.resize((width, int(image.height * scale)), Image.LANCZOS)
    canvas = Image.new("RGB", (image.width, image.height + 22), (255, 255, 255))
    canvas.paste(image, (0, 22))
    draw = ImageDraw.Draw(canvas)
    draw.text((6, 5), label, fill=(200, 0, 0))
    draw.line((0, 21, canvas.width, 21), fill=(200, 0, 0), width=1)
    return canvas


def encode(image: Image.Image) -> str:
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=85)
    return base64.b64encode(buffer.getvalue()).decode()


def ask(page: int) -> str:
    ref = WORK / "ref" / f"p-{page:02d}.png"
    ours = WORK / "ours" / f"p-{page:02d}.png"
    if not ref.exists() or not ours.exists():
        return "(no rasters)"

    body = {
        "model": MODEL,
        "temperature": 0,
        "max_tokens": 4000,
        "messages": [
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": PROMPT},
                    {"type": "image_url",
                     "image_url": {"url": "data:image/jpeg;base64," + encode(panel(ref, "REFERENCE", 900))}},
                    {"type": "image_url",
                     "image_url": {"url": "data:image/jpeg;base64," + encode(panel(ours, "OURS", 900))}},
                ],
            }
        ],
    }

    request = urllib.request.Request(
        BASE + "/chat/completions",
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", "Authorization": "Bearer " + KEY},
    )
    try:
        with urllib.request.urlopen(request, timeout=900) as response:
            payload = json.loads(response.read().decode())
        message = payload["choices"][0]["message"]
        # A reasoning model can return only its reasoning trace; fall back to it rather
        # than recording an empty finding.
        text = message.get("content") or message.get("reasoning") or "(empty response)"
        return text.strip()
    except Exception as exc:  # noqa: BLE001 - report, do not crash the sweep
        detail = ""
        if hasattr(exc, "read"):
            try:
                detail = exc.read().decode()[:400]
            except Exception:
                detail = ""
        return f"(model error: {exc} {detail})"


def main():
    pages = [int(a) for a in sys.argv[1:]] or list(range(1, 13))
    out = WORK / "VISION-FINDINGS.md"
    lines = [
        "# Vision findings — what the model sees",
        "",
        f"Model: `{MODEL}`. For each page it was shown the reference render and ours, and",
        "asked to list every visible difference.",
        "",
    ]
    for page in pages:
        print(f"page {page}...", flush=True)
        text = ask(page)
        lines.append(f"## Page {page}")
        lines.append("")
        lines.append(text)
        lines.append("")
        out.write_text("\n".join(lines), encoding="utf-8")

    print(f"\nwrote {out}")


if __name__ == "__main__":
    sys.exit(main())
