"""Show the text blocks of one content stream, so a text layout can be read directly.

Usage:  python show_text_blocks.py <file.pdf> [stream-index]
"""
import pathlib
import re
import sys
import zlib


def text_streams(path):
    """Streams that hold text, whatever filter they use.

    A stream that will not decompress is skipped rather than crashing: page content is
    usually Flate, but a file may use another filter or none at all.
    """
    raw = pathlib.Path(path).read_bytes()
    out = []
    for m in re.finditer(rb"(?<!end)stream\r?\n", raw):
        start = m.end()
        end = raw.find(b"endstream", start)
        if end < 0:
            continue
        chunk = raw[start:end]
        methods = [None]
        try:
            import lzw  # noqa: F401  (optional)
        except Exception:
            pass

        data = None
        try:
            data = zlib.decompress(chunk)
        except Exception:
            # Not Flate; take it as it is, since some files store content plainly.
            printable = sum(1 for b in chunk if 9 <= b <= 13 or 32 <= b <= 126)
            if len(chunk) and printable >= len(chunk) * 0.9:
                data = chunk

        if data is None:
            continue

        text = data.decode("latin-1", "replace")
        if re.search(r"(?<![A-Za-z])BT(?![A-Za-z])", text) and \
           re.search(r"(?<![A-Za-z])T[Jj](?![A-Za-z])", text):
            out.append(text)
    return out


def main():
    path = sys.argv[1]
    index = int(sys.argv[2]) if len(sys.argv) > 2 else 1

    streams = text_streams(path)
    print(f"{len(streams)} text streams")

    text = streams[index - 1]
    tj = len(re.findall(r"(?<![A-Za-z])T[Jj](?![A-Za-z])", text))
    print(f"stream {index}: {len(text)} bytes, {tj} show operators\n")

    lines = [l.strip() for l in text.splitlines()]
    n = 0
    for i, line in enumerate(lines):
        if line != "BT":
            continue
        n += 1
        if n > 6:
            break
        print("---")
        for inner in lines[i:i + 8]:
            print("   ", inner[:78])


if __name__ == "__main__":
    main()
