"""Count colour-setting operators in a PDF's content streams."""
import re
import sys
import zlib


def text_of(path):
    raw = open(path, "rb").read()
    out = []
    for m in re.finditer(rb"(?<!end)stream\r?\n", raw):
        start = m.end()
        end = raw.find(b"endstream", start)
        try:
            out.append(zlib.decompress(raw[start:end]).decode("latin-1"))
        except Exception:
            pass
    return "\n".join(out)


for path in sys.argv[1:]:
    text = text_of(path)
    cmyk = len(re.findall(r"(?m)^[\d.]+ [\d.]+ [\d.]+ [\d.]+ [kK]$", text))
    rgb = len(re.findall(r"(?m)^[\d.]+ [\d.]+ [\d.]+ [rR]g$", text))
    gray = len(re.findall(r"(?m)^[\d.]+ [gG]$", text))
    print("%-46s k/K=%-6d rg/RG=%-6d g/G=%d" % (path.split("/")[-1], cmyk, rgb, gray))
