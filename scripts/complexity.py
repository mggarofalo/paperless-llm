"""Gate maintained C#/JavaScript functions. Run with pinned lizard (see requirements-quality.txt)."""
from pathlib import Path
import json
import sys
import lizard

root = Path(__file__).resolve().parents[1]
ignored = {"bin", "obj", "node_modules"}
files = [path for folder in ("src", "runner", "scripts") for path in (root / folder).rglob("*")
         if path.suffix in {".cs", ".mjs", ".js"} and not ignored.intersection(path.parts)
         and not path.name.endswith(".test.mjs")]
functions = [(path.relative_to(root).as_posix(), function) for path in files
             for function in lizard.analyze_file(str(path)).function_list]
if not functions:
    sys.exit("No functions found; complexity gate did not run.")
violations = [{"file": path, "function": function.name, "line": function.start_line,
               "complexity": function.cyclomatic_complexity}
              for path, function in functions if function.cyclomatic_complexity > 10]
print(json.dumps({"files": len(files), "functions": len(functions), "maximum": max(
    function.cyclomatic_complexity for _, function in functions), "limit": 10, "violations": violations}, indent=2))
sys.exit(bool(violations))
