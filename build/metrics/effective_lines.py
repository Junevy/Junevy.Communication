"""Count the effective lines of C# code in each given directory (plan P2.5, section 3).

Usage: python build/metrics/effective_lines.py <directory> [<directory> ...]

A line is effective when it is not blank after stripping surrounding whitespace and does not start
with "//", so XML documentation lines are not counted. Directories named obj or bin are skipped.
"""
import os
import sys

SKIPPED_DIRECTORIES = ("obj", "bin")


def count_effective_lines(directory):
    total = 0
    for root, dirs, files in os.walk(directory):
        dirs[:] = [name for name in dirs if name not in SKIPPED_DIRECTORIES]
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(root, name), encoding="utf-8-sig") as handle:
                for line in handle:
                    text = line.strip()
                    if text and not text.startswith("//"):
                        total += 1
    return total


for target in sys.argv[1:]:
    print(f"{count_effective_lines(target):6d}  {target}")
