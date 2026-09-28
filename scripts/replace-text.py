#!/usr/bin/env python3
"""replace-text.py — exact-substring file patching for the agent harness.

The harness's built-in edit tool cannot operate on this repository (the 9p mount
exposes the tree over \\\\wsl.localhost\\Ubuntu\\..., which does not support the
hardlink+rename the tool uses). Agents therefore stage the search and replacement
fragments as files and call this script, which performs a literal, counted
replacement and fails loudly rather than silently corrupting a source file.

Usage:
    replace-text.py <target-file> <search-file> <replacement-file> [--all] [--count N]

  <target-file>       path to the file to modify
  <search-file>       file whose entire contents are the literal text to find
  <replacement-file>  file whose entire contents replace the match
                      (use /dev/null to delete)
  --all               replace every occurrence (default: exactly one required)
  --count N           require exactly N occurrences before replacing

Exit codes: 0 ok, 1 precondition failure (not found / ambiguous), 2 usage.
"""
import sys
from pathlib import Path


def main(argv: list[str]) -> int:
    args = [a for a in argv[1:] if not a.startswith("--")]
    flags = {a for a in argv[1:] if a.startswith("--")}
    if len(args) != 3:
        print(__doc__)
        return 2

    target, search_file, repl_file = args
    search = Path(search_file).read_text(encoding="utf-8")
    repl = "" if repl_file == "/dev/null" else Path(repl_file).read_text(encoding="utf-8")

    if search == "":
        print("replace-text: search text is empty; refusing to patch", file=sys.stderr)
        return 1

    path = Path(target)
    text = path.read_text(encoding="utf-8")
    found = text.count(search)

    expected = None
    if "--count" in flags:
        i = argv.index("--count")
        expected = int(argv[i + 1])

    if "--all" in flags:
        if found == 0:
            print(f"replace-text: search text not found in {target}", file=sys.stderr)
            return 1
    elif expected is not None:
        if found != expected:
            print(f"replace-text: expected {expected} occurrences in {target}, found {found}", file=sys.stderr)
            return 1
    elif found != 1:
        print(f"replace-text: expected exactly 1 occurrence in {target}, found {found}", file=sys.stderr)
        return 1

    path.write_text(text.replace(search, repl), encoding="utf-8")
    print(f"replace-text: patched {target} ({found} occurrence(s))")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
