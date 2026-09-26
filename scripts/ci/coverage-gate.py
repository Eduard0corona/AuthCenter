#!/usr/bin/env python3
"""Fails when merged Cobertura coverage falls under the minimums.

usage: coverage-gate.py <cobertura.xml> <minimum line %> <minimum branch %>
"""
import sys
import xml.etree.ElementTree as ElementTree

path, minimum_lines, minimum_branches = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
root = ElementTree.parse(path).getroot()
lines = float(root.get("line-rate", "0")) * 100
branches = float(root.get("branch-rate", "0")) * 100
print(f"Line coverage {lines:.1f}% (minimum {minimum_lines:.0f}%), branch coverage {branches:.1f}% (minimum {minimum_branches:.0f}%).")
if lines < minimum_lines or branches < minimum_branches:
    print("Coverage fell under the minimum: add tests for the new code, or explain in the pull request why the minimum should move.", file=sys.stderr)
    sys.exit(1)
