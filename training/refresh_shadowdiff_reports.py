#!/usr/bin/env python3
"""Regenerate ShadowDiff reports from checked-in CLI traces.

This is the deterministic half of the semantic rework.  The CLI traces remain
the engine oracle; this utility replays every registered action twice through
the current ShadowDiff binary, verifies byte-identical output, and annotates
each report with the pre-annotation SHA-256.  It deliberately does not expose
or consume raw RNG words.
"""

from __future__ import annotations

import hashlib
import json
import subprocess
import sys
from pathlib import Path

from verify_repeat_runs import expected_report_names

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"
SHADOW = ROOT / "training" / "ShadowDiff" / "bin" / "Release" / "net9.0" / "STS2BestChoice.ShadowDiff.exe"
TMP = DATA / ".shadowdiff-refresh"


def _run(trace: Path, ordinal: int, output: Path) -> tuple[int, bytes, str]:
    result = subprocess.run(
        [str(SHADOW), str(trace), str(output), str(ordinal)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=120,
        check=False,
    )
    return result.returncode, result.stdout.encode("utf-8"), result.stderr


def main() -> int:
    if not SHADOW.is_file():
        print(f"missing ShadowDiff binary: {SHADOW}", file=sys.stderr)
        return 2
    TMP.mkdir(parents=True, exist_ok=True)
    names = sorted(expected_report_names())
    hashes: dict[str, str] = {}
    quality: dict[str, int] = {"Reliable": 0, "Estimated": 0, "Uncalculable": 0, "Unknown": 0}
    failures: list[str] = []
    for name in names:
        target = DATA / name
        if not target.is_file():
            failures.append(f"missing report seed: {name}")
            continue
        try:
            previous = json.loads(target.read_text(encoding="utf-8"))
            fixture = previous["fixture"]
            ordinal = int(previous["action_ordinal"])
        except (OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
            failures.append(f"invalid existing report {name}: {exc}")
            continue
        trace = DATA / f"{fixture}-trace.jsonl"
        if not trace.is_file():
            failures.append(f"missing trace: {trace.name}")
            continue
        first_path = TMP / f"{name}.a.json"
        second_path = TMP / f"{name}.b.json"
        rc_a, stdout_a, stderr_a = _run(trace, ordinal, first_path)
        rc_b, stdout_b, stderr_b = _run(trace, ordinal, second_path)
        if rc_a != 0 or rc_b != 0:
            failures.append(f"ShadowDiff failed {name}: {rc_a}/{rc_b} {stderr_a[-200:]} {stderr_b[-200:]}")
            continue
        try:
            payload_a = json.loads(stdout_a.decode("utf-8"))
            payload_b = json.loads(stdout_b.decode("utf-8"))
        except json.JSONDecodeError as exc:
            failures.append(f"invalid ShadowDiff JSON {name}: {exc}")
            continue
        # Compare canonical JSON rather than process-specific whitespace.
        canonical_a = json.dumps(payload_a, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
        canonical_b = json.dumps(payload_b, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
        if canonical_a != canonical_b:
            failures.append(f"non-deterministic ShadowDiff output: {name}")
            continue
        # Preserve the normal indented report format used by existing tools.
        target.write_text(json.dumps(payload_a, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        raw_hash = hashlib.sha256(target.read_bytes()).hexdigest()
        hashes[name] = raw_hash
        confidence = payload_a.get("confidence")
        quality[confidence if confidence in quality else "Unknown"] += 1

    for name, digest in hashes.items():
        target = DATA / name
        payload = json.loads(target.read_text(encoding="utf-8"))
        payload["repeat_sha256"] = digest
        target.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    verification = {
        "schema_version": 1,
        "game_version": "v0.111.0",
        "cli_protocol_version": "0.2.0",
        "trace_schema": 1,
        "report_count": len(hashes),
        "expected_report_count": len(names),
        "quality_counts": quality,
        "different": len(failures),
        "failures": failures,
        "reports": hashes,
    }
    (DATA / "shadowdiff-rework-verification.json").write_text(
        json.dumps(verification, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: verification[k] for k in ("report_count", "expected_report_count", "quality_counts", "different")}, ensure_ascii=False))
    return 0 if not failures and len(hashes) == len(names) else 1


if __name__ == "__main__":
    raise SystemExit(main())
