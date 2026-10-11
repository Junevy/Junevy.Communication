"""Find duplicated code blocks across C# files (normalized, comments and trivial lines skipped).

Usage: python dupscan.py <window> <file>...   Prints clusters of identical windows found in >= 2 places.
"""
import re
import sys
from collections import defaultdict

window = int(sys.argv[1])
files = sys.argv[2:]

TRIVIAL = re.compile(r"^[{}();,\s]*$")


def normalize(line):
    line = line.strip()
    if not line or line.startswith("//") or line.startswith("///") or line.startswith("using ") or TRIVIAL.match(line):
        return None
    line = re.sub(r"\s+", " ", line)
    return line


index = defaultdict(list)
for path in files:
    with open(path, encoding="utf-8-sig") as handle:
        raw = handle.read().splitlines()
    kept = [(no + 1, normalize(text)) for no, text in enumerate(raw)]
    kept = [(no, text) for no, text in kept if text is not None]
    for start in range(len(kept) - window + 1):
        chunk = tuple(text for _, text in kept[start:start + window])
        index[chunk].append((path, kept[start][0]))

# Merge overlapping windows per file pair into ranges.
pairs = defaultdict(list)
for chunk, places in index.items():
    if len(places) < 2:
        continue
    key = tuple(sorted({p for p, _ in places}))
    pairs[key].append(sorted(places))

reported = 0
for key, groups in sorted(pairs.items(), key=lambda kv: -len(kv[1])):
    lines = sorted({line for group in groups for (p, line) in group if p == key[0]})
    print(f"{len(groups):3d} windows  {' <-> '.join(key)}  (first file lines {lines[0]}..{lines[-1]})")
    reported += 1
    if reported >= 40:
        break
