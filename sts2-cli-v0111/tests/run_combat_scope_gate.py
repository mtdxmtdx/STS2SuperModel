"""P1 Lane C combat-scope quality gate runner.

Runs the target pytest suites (baseline consistency, baseline combat and the
new combat-scope gate tests), prints a per-test summary to the console and
writes a machine-readable JSON report to
``docs/gate-results/combat-scope-gate.json`` (committed to the repo).

Usage (from anywhere):
    python tests/run_combat_scope_gate.py

Exit code: 0 when every collected test passed and the required gate metrics
are satisfied, non-zero otherwise.

Out of scope by design (not part of this gate): map strategy, shop/reward/
event decision logic, save-load completeness, full-run automation.
"""

import datetime
import json
import os
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
GATE_RESULT_DIR = os.path.join(ROOT, "docs", "gate-results")
GATE_RESULT_PATH = os.path.join(GATE_RESULT_DIR, "combat-scope-gate.json")

TARGET_SUITES = [
    os.path.join("tests", "test_v0111_consistency.py"),
    os.path.join("tests", "test_combat.py"),
    os.path.join("tests", "test_combat_scope_gate.py"),
]

VERSION_LOCK = {
    "game": "v0.111.0",
    "commit": "41cef1ea",
    "assembly_sha256": ("0861BFA1DF347538D932F22D580E75420F08082792EB914E53B48"
                        "82764ACDBE9"),
    "protocol": "0.2.0",
    "trace_schema": 1,
}

# Gate metrics mapped to nodeid substrings provided by the gate test file.
METRIC_TEST_MARKERS = {
    "stable_replay_ok": (
        "TestCrossProcessStableIdentity::test_identical_transcripts_across_two_processes",
        "TestIndexShiftInvariance::test_same_options_survive_hand_rearrangement"
        "_across_processes",
    ),
    "trace_schema_ok": (
        "TraceRecovery",                       # failure metadata / flush / kill / append
        "test_training_trace_and_snapshot_contract",  # baseline trace contract
    ),
}


class _ReportCollector:
    """Pytest plugin that records every test report."""

    def __init__(self):
        self.records = []  # list of dicts: nodeid/outcome/duration

    def pytest_runtest_logreport(self, report):
        if report.when in ("call",) or (
                report.when == "setup" and report.outcome != "passed"):
            self.records.append({
                "nodeid": report.nodeid,
                "outcome": report.outcome,
                "when": report.when,
            })


def _metric_ok(records, markers):
    matching = [r for r in records
                if any(marker in r["nodeid"] for marker in markers)]
    return bool(matching) and all(r["outcome"] == "passed" for r in matching)


def main():
    collector = _ReportCollector()
    args = ["-q", "--no-header", "-p", "no:cacheprovider"] + TARGET_SUITES
    exit_code = pytest.main(args, plugins=[collector])

    records = collector.records
    # de-duplicate: a setup failure also emits a call-less record only once
    seen = {}
    for record in records:
        key = record["nodeid"]
        if key not in seen or record["outcome"] == "failed":
            seen[key] = record["outcome"]
    passed = sorted(node for node, outcome in seen.items() if outcome == "passed")
    failed = sorted(node for node, outcome in seen.items() if outcome == "failed")
    skipped = sorted(node for node, outcome in seen.items()
                     if outcome not in ("passed", "failed"))

    public_leakage_tests = [n for n in seen
                            if "TestPublicTeacherIsolation" in n]
    public_leakage_count = 0 if (public_leakage_tests
                                 and all(seen[n] == "passed"
                                         for n in public_leakage_tests)) else -1

    metrics = {}
    record_list = [{"nodeid": node, "outcome": outcome}
                   for node, outcome in seen.items()]
    for metric, markers in METRIC_TEST_MARKERS.items():
        metrics[metric] = _metric_ok(record_list, markers)

    report = {
        "total": len(passed) + len(failed),
        "passed_count": len(passed),
        "failed": failed,
        "skipped": skipped,
        "version_lock": VERSION_LOCK,
        "stable_replay_ok": metrics["stable_replay_ok"],
        "trace_schema_ok": metrics["trace_schema_ok"],
        "public_leakage_count": public_leakage_count,
        "pytest_exit_code": int(exit_code),
        "generated_at": datetime.datetime.now(
            datetime.timezone.utc).isoformat(timespec="seconds"),
    }

    print("\n===== combat-scope gate summary =====")
    print(f"total collected : {report['total']}")
    print(f"passed          : {report['passed_count']}")
    print(f"failed          : {len(report['failed'])}")
    for node in report["failed"]:
        print(f"  FAIL {node}")
    if report["skipped"]:
        print(f"skipped         : {len(report['skipped'])}")
        for node in report["skipped"]:
            print(f"  SKIP {node}")
    print(f"version_lock    : game={VERSION_LOCK['game']} "
          f"commit={VERSION_LOCK['commit']} "
          f"protocol={VERSION_LOCK['protocol']} "
          f"trace_schema={VERSION_LOCK['trace_schema']}")
    print(f"stable_replay_ok     : {report['stable_replay_ok']}")
    print(f"trace_schema_ok      : {report['trace_schema_ok']}")
    print(f"public_leakage_count : {report['public_leakage_count']}")
    print(f"generated_at    : {report['generated_at']}")

    os.makedirs(GATE_RESULT_DIR, exist_ok=True)
    with open(GATE_RESULT_PATH, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2, ensure_ascii=False)
        handle.write("\n")
    print(f"report written  : {os.path.relpath(GATE_RESULT_PATH, ROOT)}")

    overall_ok = (
        exit_code == 0
        and report["total"] > 0
        and not failed
        and report["stable_replay_ok"]
        and report["trace_schema_ok"]
        and report["public_leakage_count"] == 0
    )
    print(f"OVERALL         : {'GREEN' if overall_ok else 'RED'}")
    return 0 if overall_ok else 1


if __name__ == "__main__":
    sys.exit(main())
