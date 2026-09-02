"""Audit semantic random-source references against the v0.111 registry.

This is a static contract check for the combat shadow simulator.  It does not
inspect or expose RNG state; it only verifies that every RandomSource used by
Core semantics resolves to a registered operator.
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


SOURCE_RE = re.compile(r"RandomSource\s*:\s*(?:RngSnapshotSet\.)?([A-Za-z0-9_]+|\"[^\"]+\")")
CONST_RE = re.compile(r'public const string\s+(\w+)\s*=\s*"([^"]+)"')
REGISTRY_RE = re.compile(r"Spec\(RngSnapshotSet\.(\w+)")
STREAM_RE = re.compile(r"RngSnapshotSet\.([A-Za-z0-9_]+)")


def audit(core_dir: Path) -> dict[str, object]:
    random_model = core_dir / "Model" / "RandomModel.cs"
    random_text = random_model.read_text(encoding="utf-8")
    constants = dict(CONST_RE.findall(random_text))
    registered = {constants[name] for name in REGISTRY_RE.findall(random_text) if name in constants}

    references: dict[str, list[str]] = {}
    for path in sorted(core_dir.rglob("*.cs")):
        if "\\obj\\" in str(path) or "\\bin\\" in str(path):
            continue
        text = path.read_text(encoding="utf-8", errors="ignore")
        # RandomModel.cs declares the stream constants and registry itself;
        # exclude that declaration file so the audit measures semantic usage.
        tokens = list(SOURCE_RE.findall(text))
        if path.name != "RandomModel.cs":
            tokens.extend(token for token in STREAM_RE.findall(text) if token != "Empty")
        for token in tokens:
            source = token[1:-1] if token.startswith('"') else constants.get(token, token)
            if source.lower() == "null":
                continue
            references.setdefault(source, []).append(str(path.relative_to(core_dir)))

    referenced = set(references)
    unregistered = sorted(referenced - registered)
    registry_unused = sorted(registered - referenced)
    return {
        "version": "v0.111.0",
        "registry_operator_count": len(registered),
        "registered_operators": sorted(registered),
        "referenced_random_sources": sorted(referenced),
        "unregistered_references": unregistered,
        "registered_but_unreferenced": registry_unused,
        "reference_files": {
            source: sorted(set(paths)) for source, paths in sorted(references.items())
        },
        "verdict": "pass" if not unregistered else "fail",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--core-dir",
        type=Path,
        default=Path(__file__).resolve().parents[2] / "STS2BestChoice" / "STS2BestChoice.Core",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "data" / "random-operator-audit.json",
    )
    args = parser.parse_args()
    result = audit(args.core_dir)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        f"random_operator_audit: registry={result['registry_operator_count']} "
        f"references={len(result['referenced_random_sources'])} "
        f"unregistered={len(result['unregistered_references'])} "
        f"verdict={result['verdict']}"
    )
    return 0 if result["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
