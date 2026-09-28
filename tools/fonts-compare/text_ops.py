#!/usr/bin/env python3
"""How much text does the original draw, versus how many text items we imported?"""
import pathlib
import re

ROOT = pathlib.Path(r"C:\Users\submu\vccad-win")
QDF = ROOT / "artifacts" / "ref" / "orig-qdf.pdf"

text = QDF.read_text(encoding="latin-1")
streams = re.findall(r"stream\n(.*?)\nendstream", text, re.S)
content = [s for s in streams if re.search(r"\b(BT|re|m|l|c)\b", s)]

show = re.compile(r"(?<![A-Za-z])(?:Tj|TJ|'|\")(?![A-Za-z])")
bt = re.compile(r"(?<![A-Za-z])BT(?![A-Za-z])")
et = re.compile(r"(?<![A-Za-z])ET(?![A-Za-z])")
tf = re.compile(r"/[A-Za-z0-9]+\s+[\d.]+\s+Tf")

print("content streams:", len(content))
print("BT blocks      :", sum(len(bt.findall(s)) for s in content))
print("text show ops  :", sum(len(show.findall(s)) for s in content))
print("Tf selections  :", sum(len(tf.findall(s)) for s in content))
print("text items we imported: 107 (12 pages)")
print()

fonts = {}
for stream in content:
    for name in tf.findall(stream):
        key = name.split()[0]
        fonts[key] = fonts.get(key, 0) + 1
print("font resources selected:", dict(sorted(fonts.items(), key=lambda kv: -kv[1])[:10]))
