#!/usr/bin/env python3
"""Build a deterministic 100-state teacher-label smoke dataset from traces."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "training" / "collectors"))
from teacher_worker import TeacherWorker, aggregate_hidden_states  # noqa: E402


def load_records(paths: list[Path]) -> list[dict]:
    rows: list[dict] = []
    for path in sorted(paths, key=lambda item: item.as_posix()):
        for line_no, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if not line.strip():
                continue
            row = json.loads(line)
            if row.get("public_state") is not None:
                # Input is already normalized by trace_to_training; this guard
                # prevents accidental use of teacher-only trace rows.
                defaults = {
                    "simulator_version": "cli-v0111-headless",
                    "scorer_version": "not-applicable",
                    "semantic_database_version": "game-runtime-v0111",
                    "feature_schema_version": "1",
                    "model_version": "none",
                }
                for key, value in defaults.items():
                    if row.get(key) in (None, "", "unknown"):
                        row[key] = value
                relics = row.get("public_state", {}).get("player", {}).get("relics", []) or []
                required_relic_fields = {
                    "id", "name", "description", "vars", "dynamic_vars", "counter",
                    "runtime_type", "source", "evidence", "source_version",
                }
                if any(not required_relic_fields.issubset(relic) for relic in relics if isinstance(relic, dict)):
                    continue
                rows.append(row)
    return rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", action="append", required=True, type=Path,
                        help="TrainingDecisionRecord JSONL; repeat for multiple shards")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--aggregate-output", required=True, type=Path)
    parser.add_argument("--count", type=int, default=100)
    parser.add_argument("--evaluator", help="Optional CombatSearchSession/Expectimax bridge executable")
    parser.add_argument("--top-k", type=int, default=5)
    parser.add_argument("--allow-heuristic-fallback", action="store_true")
    args = parser.parse_args()
    if args.count <= 0:
        raise SystemExit("--count must be positive")
    records = load_records(args.input)
    # Stable ordering makes repeated runs independent of filesystem order.
    records.sort(key=lambda row: (str(row.get("episode_id", "")), int(row.get("round", 0)),
                                  str(row.get("state_hash_public", "")), str(row.get("record_id", ""))))
    if len(records) < args.count:
        raise SystemExit(f"need {args.count} records, found {len(records)}")
    records = records[: args.count]
    evaluator = None
    if args.evaluator:
        from teacher_worker import _command_evaluator
        evaluator = _command_evaluator(args.evaluator)
    worker = TeacherWorker(evaluator=evaluator, top_k=args.top_k,
                           allow_heuristic_fallback=args.allow_heuristic_fallback)
    labelled = [worker.process(row) for row in records]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":"))
                                      for row in labelled) + "\n", encoding="utf-8")
    args.aggregate_output.parent.mkdir(parents=True, exist_ok=True)
    args.aggregate_output.write_text(json.dumps(aggregate_hidden_states(labelled),
                                                 ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"records": len(labelled), "output": str(args.output),
                      "aggregate": str(args.aggregate_output)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
