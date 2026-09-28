"""Compare word box heights between two PDFs, page by page.

word_geometry.py reports where words sit. This reports how tall the boxes are, which is a
different question: a bounding box comes from the font's declared metrics as much as from
where the text was placed, so a box that is 0.448pt taller in one file than the other can
mean a font descriptor disagrees rather than a baseline being wrong.

If the boxes are the same height and shifted, text was placed differently. If the heights
differ, the metrics differ.

Usage:  python word_heights.py <file-a.pdf> <file-b.pdf> [pages]
"""
import pathlib
import re
import subprocess
import sys

WORD = re.compile(
    r'<word xMin="([-\d.]+)" yMin="([-\d.]+)" xMax="([-\d.]+)" yMax="([-\d.]+)">(.*?)</word>')


def to_wsl(path):
    text = pathlib.Path(path).as_posix()
    if text.startswith("C:/"):
        return "/mnt/c/" + text[3:]
    marker = "wsl.localhost/Ubuntu/"
    if marker in text:
        return "/" + text.split(marker, 1)[1]
    return text


def boxes(path, page):
    result = subprocess.run(
        ["wsl", "-e", "pdftotext", "-bbox", "-f", str(page), "-l", str(page), to_wsl(path), "-"],
        capture_output=True, errors="replace", encoding="utf-8")
    return [(float(m.group(2)), float(m.group(4)), m.group(5)) for m in WORD.finditer(result.stdout)]


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    a, b = sys.argv[1], sys.argv[2]
    pages = int(sys.argv[3]) if len(sys.argv) > 3 else 12

    for page in range(1, pages + 1):
        left, right = boxes(a, page), boxes(b, page)
        if not left or not right:
            print(f"page {page}: no words on one side")
            continue

        # Matched by text so a difference in height is not a difference in which words exist.
        by_text = {}
        for ymin, ymax, text in right:
            by_text.setdefault(text, []).append(ymax - ymin)

        deltas = []
        for ymin, ymax, text in left:
            if by_text.get(text):
                deltas.append(by_text[text].pop(0) - (ymax - ymin))

        if not deltas:
            print(f"page {page}: nothing matched")
            continue

        deltas.sort()
        print("page %-3d words %4d/%-4d  median height delta %7.4f  min %7.4f  max %7.4f"
              % (page, len(left), len(right), deltas[len(deltas) // 2], deltas[0], deltas[-1]))

    return 0


if __name__ == "__main__":
    sys.exit(main())
