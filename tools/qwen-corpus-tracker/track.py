#!/usr/bin/env python3
"""Qwen-based render-fidelity progress tracker.

For a deterministic sample of the veraPDF corpus, renders the source PDF and our
importer's re-export with a compliant renderer (poppler), then asks the qwen
vision model (OpenAI-compatible endpoint) to score how visually close they are.
Writes append-only results so progress can be tracked over time.

Env:
  QWEN_BASE   default https://your-endpoint.example/v1
  QWEN_KEY    default <your-api-key>
  QWEN_MODEL  default qwen3.8-27b
"""
import os, sys, csv, json, base64, random, subprocess, tempfile, re, time
from concurrent.futures import ThreadPoolExecutor
from urllib import request

BASE = os.environ.get("QWEN_BASE", "https://your-endpoint.example/v1")
KEY = os.environ.get("QWEN_KEY", "<your-api-key>")
MODEL = os.environ.get("QWEN_MODEL", "qwen3.8-27b")

CORPUS = sys.argv[1] if len(sys.argv) > 1 else "/tmp/opencode/veraPDF-corpus"
OURS = sys.argv[2] if len(sys.argv) > 2 else "/tmp/opencode/rc/out"
N = int(sys.argv[3]) if len(sys.argv) > 3 else 24
SEED = int(sys.argv[4]) if len(sys.argv) > 4 else 7
REPORT = sys.argv[5] if len(sys.argv) > 5 else "/tmp/opencode/rc/qwen_progress.csv"

PROMPT = (
    "Image 1 is a PDF page rendered by poppler (the reference). Image 2 is the "
    "same page imported into our editor and re-exported, rendered by the same "
    "poppler. Score the visual similarity of Image 2 to Image 1 from 0 to 100 "
    "(100 = indistinguishable). Then list at most 3 concrete differences. "
    "Reply as: SCORE: <n>\\nDIFFS: <short list>."
)


def render(pdf, out_base):
    try:
        subprocess.run(["pdftoppm", "-jpeg", "-scale-to", "640", "-f", "1", "-l", "1",
                        pdf, out_base], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       timeout=60)
    except Exception:
        return None
    cand = [f for f in os.listdir(os.path.dirname(out_base))
            if os.path.basename(f).startswith(os.path.basename(out_base)) and f.endswith(".jpg")]
    return os.path.join(os.path.dirname(out_base), cand[0]) if cand else None


def ask(img1, img2):
    content = [{"type": "text", "text": PROMPT}]
    for p in (img1, img2):
        b = base64.b64encode(open(p, "rb").read()).decode()
        content.append({"type": "image_url", "image_url": {"url": "data:image/jpeg;base64," + b}})
    payload = {"model": MODEL, "max_tokens": 2500, "messages": [{"role": "user", "content": content}]}
    req = request.Request(BASE + "/chat/completions", data=json.dumps(payload).encode(),
                          headers={"Authorization": "Bearer " + KEY, "Content-Type": "application/json"})
    for attempt in range(3):
        try:
            r = json.load(request.urlopen(req, timeout=240))
            return r["choices"][0]["message"].get("content") or ""
        except Exception as e:
            if attempt == 2:
                return f"ERROR: {e}"
            time.sleep(3)


def score_of(text):
    m = re.search(r"SCORE:\s*(\d{1,3})", text or "")
    return int(m.group(1)) if m else None


def one(rel):
    src = os.path.join(CORPUS, rel)
    ours = os.path.join(OURS, rel)
    if not os.path.exists(ours):
        return (rel, None, "missing")
    td = tempfile.mkdtemp(dir="/tmp/opencode/rc")
    try:
        a = render(src, os.path.join(td, "s"))
        b = render(ours, os.path.join(td, "o"))
        if not a or not b:
            return (rel, None, "unrenderable")
        txt = ask(a, b)
        return (rel, score_of(txt), txt.replace("\n", " ")[:160])
    finally:
        for f in os.listdir(td):
            try: os.remove(os.path.join(td, f))
            except OSError: pass
        try: os.rmdir(td)
        except OSError: pass


def main():
    status = os.path.join(OURS, "_status.tsv")
    rows = []
    with open(status) as fh:
        next(fh)
        for line in fh:
            p = line.rstrip("\n").split("\t")
            if len(p) >= 6 and p[0] == "ok" and int(p[2]) > 0:
                rows.append(p[5])
    random.seed(SEED)
    random.shuffle(rows)
    # stratify: take a spread across the shuffled list
    sample = rows[:N]
    results = []
    with ThreadPoolExecutor(max_workers=3) as ex:
        for r in ex.map(one, sample):
            results.append(r)
            print(f"  {str(r[1]):>4}  {r[0][:70]}")
    scores = [r[1] for r in results if r[1] is not None]
    new = not os.path.exists(REPORT)
    with open(REPORT, "a", newline="") as fh:
        w = csv.writer(fh)
        if new:
            w.writerow(["file", "score", "note"])
        w.writerows(results)
    print(f"\nsampled={len(results)} scored={len(scores)} mean={sum(scores)/len(scores):.1f}" if scores
          else "\nno scores")


if __name__ == "__main__":
    main()
