"""Audit PDF content operators across a whole corpus.

The per-file version answers "does this file use Tc?". This answers the question worth
asking: across thousands of real files, which operators appear, how often, and in how many
different documents? An operator used once in one file is a curiosity; one used in half the
corpus is a gap that shows in half the work.

Usage:  python corpus_operator_audit.py <corpus-dir> [limit]
"""
import pathlib
import re
import sys
import zlib
from collections import Counter

# The operators the importer handles, so the report can subtract them.
HANDLED = {
    "b", "B", "b*", "B*", "BDC", "BMC", "BT", "c", "cm", "CS", "cs", "d", "Do", "EMC",
    "F", "f", "f*", "g", "G", "gs", "h", "j", "J", "k", "K", "l", "m", "M", "n", "q",
    "Q", "re", "RG", "rg", "s", "S", "SC", "sc", "SCN", "scn", "T*", "Td", "TD", "Tf",
    "Tj", "TJ", "TL", "Tm", "T*", "v", "w", "W", "y", "'", '"',
}

# Every operator PDF defines. Anything here that is not in HANDLED is a candidate gap.
ALL = HANDLED | {
    "BI", "ID", "EI", "BX", "EX", "ET", "sh", "W*", "ri", "i", "d0", "d1", "DP", "MP",
    "Tc", "Tw", "Tz", "Tr", "Ts",
}


def streams(raw):
    """Only the streams that hold page content.

    Decompressing every stream pulls in embedded font programmes, which are binary and
    full of the letters that also name operators — auditing those reports hundreds of
    thousands of uses of "Q" and tells you nothing. A content stream is text: if a
    meaningful share of its bytes are not printable, it is something else.
    """
    out = []
    for m in re.finditer(rb"(?<!end)stream\r?\n", raw):
        start = m.end()
        end = raw.find(b"endstream", start)
        if end < 0:
            continue
        try:
            data = zlib.decompress(raw[start:end])
        except Exception:
            data = raw[start:end]

        if data.count(b"\n") == 0:
            continue

        printable = sum(1 for b in data if 9 <= b <= 13 or 32 <= b <= 126)
        if printable < len(data) * 0.9:
            continue

        out.append(data.decode("latin-1"))
    return "\n".join(out)


def main():
    root = pathlib.Path(sys.argv[1])
    limit = int(sys.argv[2]) if len(sys.argv) > 2 else 300

    files = sorted(root.rglob("*.pdf"))[:limit]
    if not files:
        print("no PDFs under", root)
        return 1

    appearances = Counter()   # documents containing the operator
    uses = Counter()          # total occurrences

    for path in files:
        try:
            text = streams(path.read_bytes())
        except Exception:
            continue
        if not text:
            continue
        for op in ALL:
            n = len(re.findall(r"(?<![A-Za-z])" + re.escape(op) + r"(?![A-Za-z])", text))
            if n:
                appearances[op] += 1
                uses[op] += n

    print(f"audited {len(files)} files under {root}\n")
    print("%-6s %-12s %-10s %s" % ("op", "documents", "uses", "handled"))
    print("-" * 44)
    for op, count in appearances.most_common():
        print("%-6s %-12d %-10d %s"
              % (op, count, uses[op], "yes" if op in HANDLED else "NO"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
