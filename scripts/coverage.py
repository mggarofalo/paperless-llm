"""Summarize Coverlet Cobertura and enforce meaningful module coverage floors."""
from pathlib import Path
import json
import sys
import xml.etree.ElementTree as ET

files = list(Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/coverage").rglob("coverage.cobertura.xml"))
if len(files) != 1:
    sys.exit(f"Expected one coverage report, found {len(files)}. Use a fresh results directory.")
report = ET.parse(files[0]).getroot()
floors = {"PaperlessLlm": (90, 77), "PaperlessLlm.Eval": (82, 74)}
modules = {}
for package in report.findall("./packages/package"):
    modules[package.attrib["name"]] = {kind: round(float(package.attrib[kind + "-rate"]) * 100, 2)
                                        for kind in ("line", "branch")}
failures = []
for name, (line, branch) in floors.items():
    actual = modules.get(name, {})
    if actual.get("line", 0) < line or actual.get("branch", 0) < branch:
        failures.append(f"{name} requires at least {line}% line / {branch}% branch coverage")
print(json.dumps({"modules": modules, "floors": floors, "failures": failures}, indent=2))
sys.exit(bool(failures))
