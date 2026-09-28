#!/usr/bin/env python3
"""
Compare poppler's text extraction against VCCad's import, page by page.

Poppler is the reference: it is the extractor behind most PDF tooling and it reports
the words it finds and where it put them. Anything poppler sees that our model does not
is text we are losing; anything we place more than a point or two away is misaligned.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
REF = ROOT / "artifacts" / "ref" / "lillie-all.xml"
MODEL = ROOT / "artifacts" / "model.json"


def reference_pages():
    """poppler words per page, in top-left origin PDF points."""
    text = REF.read_text(encoding="utf-8")
    pages = []
    for page in re.finditer(r"<page width=\"[\d.]+\" height=\"[\d.]+\">(.*?)</page>", text, re.S):
        words = [
            (float(x0), float(y0), float(x1), float(y1), w)
            for x0, y0, x1, y1, w in re.findall(
                r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="([\d.]+)" yMax="([\d.]+)">([^<]*)</word>',
                page.group(1),
            )
        ]
        pages.append(words)
    return pages


def model_text_items(document):
    """Our TextItems, grouped by artboard index, with origin and text."""
    by_artboard = {}
    artboards = document.get("artboards") or []
    for index, artboard in enumerate(artboards):
        items = []

        def walk(children):
            for child in children or []:
                if child.get("kind") == "group" or "children" in child:
                    walk(child.get("children"))
                runs = child.get("runs")
                if runs:
                    text = "".join(r.get("text", "") for r in runs)
                    items.append(
                        {
                            "x": child.get("origin", {}).get("x"),
                            "y": child.get("origin", {}).get("y"),
                            "text": text,
                            "runs": runs,
                        }
                    )

        for layer in artboard.get("layers") or []:
            walk(layer.get("children"))
        by_artboard[index] = items
    return by_artboard


def main():
    pages = reference_pages()
    model = json.loads(MODEL.read_text(encoding="utf-8"))
    document = model["result"] if "result" in model else model
    ours = model_text_items(document)

    print(f"poppler pages: {len(pages)}   our artboards: {len(ours)}")
    print()

    total_ref = total_found = 0
    for index, words in enumerate(pages):
        items = ours.get(index, [])
        our_text = " ".join(i["text"] for i in items)
        our_words = set(re.findall(r"[A-Za-z0-9,.'\"()/+-]+", our_text))

        ref_words = [w for _, _, _, _, w in words]
        missing = [
            (x, y, w) for x, y, _, _, w in words
            if not any(tok in w or w in tok for tok in our_words)
        ]
        total_ref += len(ref_words)
        total_found += len(ref_words) - len(missing)

        print(f"page {index + 1}: poppler {len(ref_words):3d} words, our text items {len(items):3d}, missing {len(missing)}")
        for x, y, w in missing[:12]:
            print(f"      MISSING ({x:7.1f},{y:7.1f}) {w!r}")

    print()
    print(f"TOTAL: {total_found}/{total_ref} reference words present in our model "
          f"({100.0 * total_found / max(total_ref, 1):.1f}%)")


if __name__ == "__main__":
    sys.exit(main())
