"""Compare the text of each page between a PDF and another engine's reading of it.

A pixel diff says a page is wrong. This says *how* it is wrong, and the distinction
matters: a page whose words are present but whose letters come out scrambled has a
placement problem, not a missing-text problem, and the two need different fixes.

Measured on the Transparency Guide's page 6, which draws one glyph per Tj with its own Tm:

    reference   INTRODUCTION TO TRANSPARENC Y
    ours        INTROUDTCIONTOTRASNAPERNCY

The same 471 words against 480, but the letters are out of order, which is what a pixel
diff cannot tell you.

Usage:  python page_text_compare.py <file-a.pdf> <file-b.pdf> [pages]
"""
import difflib
import pathlib
import subprocess
import sys


def page_text(path, page):
    """The text of one page, as poppler extracts it."""
    result = subprocess.run(
        ["wsl", "-e", "pdftotext", "-f", str(page), "-l", str(page), str(path), "-"],
        capture_output=True, errors="replace", encoding="utf-8")
    return result.stdout if result.returncode == 0 else ""


def to_wsl(path):
    """A path poppler inside WSL can open.

    Three shapes turn up: a Windows path under the /mnt/c mount, a UNC path into the WSL
    filesystem, and an already-Unix path. Only the first needs rewriting to reach the
    file; the others are passed through, and a path that is not rewritten is not a path
    poppler will silently find something else at.
    """
    text = pathlib.Path(path).as_posix()

    if text.startswith("C:/"):
        return "/mnt/c/" + text[3:]

    marker = "wsl.localhost/Ubuntu/"
    if marker in text:
        return "/" + text.split(marker, 1)[1]

    return text


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    a, b = sys.argv[1], sys.argv[2]
    pages = int(sys.argv[3]) if len(sys.argv) > 3 else 12

    for page in range(1, pages + 1):
        left = page_text(to_wsl(a), page)
        right = page_text(to_wsl(b), page)

        lw = len(left.split())
        rw = len(right.split())

        # A word ratio near 1 with a low character ratio is the scrambled-letters case:
        # the same words are there, in the wrong order or the wrong places.
        words = difflib.SequenceMatcher(None, left.split(), right.split()).ratio()
        chars = difflib.SequenceMatcher(None, left, right).ratio()

        flag = ""
        if words < 0.98:
            flag = "  <-- words differ"
        elif chars < 0.98:
            flag = "  <-- same words, letters out of order"

        print("page %-3d words %4d/%-4d word-ratio %.3f  char-ratio %.3f%s"
              % (page, lw, rw, words, chars, flag))

    return 0


if __name__ == "__main__":
    sys.exit(main())
