"""Approximate C# method lengths by brace matching. Usage: python methodlen.py <min_lines> <file>..."""
import re
import sys

SIG = re.compile(r"^\s*(public|private|protected|internal)\b[^=;]*\)\s*(where [^{]*)?$")
SIG_INLINE = re.compile(r"^\s*(public|private|protected|internal)\b[^=;]*\)\s*\{")

minimum = int(sys.argv[1])
rows = []
for path in sys.argv[2:]:
    with open(path, encoding="utf-8-sig") as handle:
        lines = handle.read().splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        if (SIG.match(line) or SIG_INLINE.match(line)) and " class " not in line and " struct " not in line:
            # find opening brace
            j = i
            while j < len(lines) and "{" not in lines[j]:
                if lines[j].rstrip().endswith(";") or "=>" in lines[j]:
                    break
                j += 1
            if j < len(lines) and "{" in lines[j]:
                depth = 0
                k = j
                while k < len(lines):
                    depth += lines[k].count("{") - lines[k].count("}")
                    if depth == 0:
                        break
                    k += 1
                length = k - i + 1
                if length >= minimum:
                    name = re.sub(r"\s+", " ", line.strip())[:110]
                    rows.append((length, path, i + 1, name))
                i = k
        i += 1

for length, path, no, name in sorted(rows, reverse=True):
    print(f"{length:4d}  {path}:{no}  {name}")
