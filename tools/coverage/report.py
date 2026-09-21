"""Merge actual Coverlet JSON hits, preserving every distinct branch and source file.

No percentage rounding in the full-coverage gate, no file/class exclusions, no test-code coverage.
A branch is identified by its method and IL edge, not merely its source line. Coverlet's line
metric is the union of executable source lines; branch coverage remains the stricter edge metric.
GLSL and real-game acceptance are deliberately not represented by a C# percentage.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

MODULES = {"VintageRTX.Core.dll", "VintageRTX.dll"}

def report(root: Path, inputs: list[Path], output: Path) -> dict:
    files: dict[str, dict] = {}
    modules: set[str] = set()
    if not inputs:
        raise ValueError("No raw coverage reports found")
    for path in inputs:
        raw = json.loads(path.read_text(encoding="utf-8-sig"))
        for module, documents in raw.items():
            module = module.replace("\\", "/").rsplit("/", 1)[-1]
            if module not in MODULES:
                raise ValueError(f"Unexpected instrumented module: {module}")
            modules.add(module)
            for document, classes in documents.items():
                document = document.replace("\\", "/")
                offset = document.find("/src/")
                relative = document[offset + 1:] if offset >= 0 else document
                if not relative.startswith("src/") or "/obj/" in relative or "/bin/" in relative:
                    raise ValueError(f"Unexpected production source path: {document}")
                item = files.setdefault(relative, {"module": module, "lines": {}, "branches": {}})
                if item["module"] != module:
                    raise ValueError(f"Source compiled into conflicting modules: {relative}")
                for cls, methods in classes.items():
                    for method, data in methods.items():
                        for line, hits in data["Lines"].items():
                            line = int(line)
                            item["lines"][line] = item["lines"].get(line, 0) + int(hits)
                        for edge in data["Branches"]:
                            key = (cls, method, edge["Line"], edge["Offset"], edge["EndOffset"], edge["Path"], edge["Ordinal"])
                            item["branches"][key] = item["branches"].get(key, 0) + int(edge["Hits"])
    if modules != MODULES:
        raise ValueError(f"Missing production modules: {MODULES - modules}")
    expected = {p.relative_to(root).as_posix() for p in (root / "src").rglob("*.cs")
                if not {"obj", "bin"}.intersection(p.relative_to(root).parts)}
    missing = sorted(expected - files.keys())
    totals = dict(lines=0, linesCovered=0, branches=0, branchesCovered=0)
    rows = []
    for source, item in sorted(files.items()):
        path = root / source
        if not path.is_file():
            raise ValueError(f"Missing source backing coverage: {source}")
        row = {"file": source, "module": item["module"], "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
               "lines": len(item["lines"]), "linesCovered": sum(n > 0 for n in item["lines"].values()),
               "branches": len(item["branches"]), "branchesCovered": sum(n > 0 for n in item["branches"].values()),
               "uncoveredLines": sorted(line for line, n in item["lines"].items() if n == 0),
               "uncoveredBranches": [{"class": key[0], "method": key[1], "line": key[2], "offset": key[3],
                                      "endOffset": key[4], "path": key[5], "ordinal": key[6]}
                                     for key, n in sorted(item["branches"].items()) if n == 0]}
        for key in totals:
            totals[key] += row[key]
        rows.append(row)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    result = {"commit": commit, "scope": "All instrumentable C# production sources in src, both assemblies",
              "collector": "coverlet.collector 10.0.1", "totals": totals, "missingSources": missing,
              "files": rows, "full": not missing and all(totals[k] > 0 and totals[k] == totals[k + "Covered"] for k in ("lines", "branches"))}
    output.mkdir(parents=True, exist_ok=True)
    (output / "summary.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    table = ["# Production C# coverage", "", f"Commit: `{commit}`", "",
             "| Source | Lines hit/total | Branches hit/total |", "|---|---:|---:|"]
    table.extend(f"| `{r['file']}` | {r['linesCovered']}/{r['lines']} | {r['branchesCovered']}/{r['branches']} |" for r in rows)
    table += ["", f"Missing source reports: {missing}", "", f"Full line AND branch coverage: **{result['full']}**",
              "", "GLSL execution and in-game rendering are separate acceptance criteria, not included in this C# metric."]
    (output / "summary.md").write_text("\n".join(table) + "\n", encoding="utf-8")
    print(json.dumps({"commit": commit, "totals": totals, "missingSources": missing, "full": result["full"]}, indent=2))
    return result

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--input", type=Path, default=Path("artifacts/coverage"))
    parser.add_argument("--output", type=Path, default=Path("artifacts/coverage-report"))
    parser.add_argument("--require-full", action="store_true")
    args = parser.parse_args()
    result = report(args.root.resolve(), sorted(args.input.rglob("coverage.json")), args.output)
    if args.require_full and not result["full"]:
        raise SystemExit("100% production line/branch coverage is NOT reached; see the complete uncovered-edge inventory.")
