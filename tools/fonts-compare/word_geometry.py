"""Compare the geometry of each word between two PDFs, page by page.

A pixel diff says a page is wrong; this says *where*. Poppler reports every word's
bounding box, so the offset of each word can be measured rather than estimated, and a
median offset that grows down the page is a line-spacing error while a constant one is a
placement error.

Usage:  python word_geometry.py <file-a.pdf> <file-b.pdf> [pages]
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


def words(path, page):
    result = subprocess.run(
        ["wsl", "-e", "pdftotext", "-bbox", "-f", str(page), "-l", str(page),
         to_wsl(path), "-"],
        capture_output=True, errors="replace", encoding="utf-8")
    out = []
    for m in WORD.finditer(result.stdout):
        out.append((float(m.group(1)), float(m.group(2)), m.group(5)))
    return out


def median(values):
    values = sorted(values)
    return values[len(values) // 2] if values else 0.0


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    a, b = sys.argv[1], sys.argv[2]
    pages = int(sys.argv[3]) if len(sys.argv) > 3 else 12

    for page in range(1, pages + 1):
        left = words(a, page)
        right = words(b, page)

        # Match on the text of the word, in order, so a mismatch is reported rather than
        # silently paired with the wrong word.
        dx, dy = [], []
        i = j = 0
        while i < len(left) and j < len(right):
            if left[i][2] == right[j][2]:
                dx.append(right[j][0] - left[i][0])
                dy.append(right[j][1] - left[i][1])
                i += 1
                j += 1
            elif j + 1 < len(right) and left[i][2] == right[j + 1][2]:
                j += 1
            else:
                i += 1

        if not dx:
            print(f"page {page}: no words matched")
            continue

        print("page %-3d words %4d/%-4d  median dx %6.3f  median dy %6.3f  "
              "max |dx| %6.2f  max |dy| %6.2f"
              % (page, len(left), len(right), median(dx), median(dy),
                 max(abs(v) for v in dx), max(abs(v) for v in dy)))

    return 0


if __name__ == "__main__":
    sys.exit(main())
