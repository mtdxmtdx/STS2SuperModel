#!/usr/bin/env python3
"""Attach deterministic teacher labels to TrainingDecisionRecord JSONL.

The worker is deliberately a protocol adapter: a production invocation passes
``--evaluator`` pointing at a process that reconstructs CombatSnapshot and
calls CombatSearchSession/Expectimax.  The request/response contract is
versioned and deterministic.  When no evaluator is available, the optional
heuristic fallback emits *Estimated* labels and never claims Reliable.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import subprocess
from collections import defaultdict
from pathlib import Path
from typing import Any, Callable, Iterable

from snapshot_adapter import rebuild_combat_snapshot


LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
}
OBJECTIVES = ("Balanced", "HighestDamage", "MinimumLoss")
CONFIDENCE_ORDER = {"Reliable": 0, "Estimated": 1, "LowConfidence": 2, "Uncalculable": 3}


def stable_hash(*parts: object) -> str:
    payload = "|".join("" if part is None else str(part) for part in parts)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _action_id(action: dict[str, Any]) -> str:
    value = action.get("action_id")
    if not isinstance(value, str) or not value:
        raise ValueError("every action candidate requires a stable action_id")
    return value


def legal_actions(record: dict[str, Any]) -> list[dict[str, Any]]:
    actions = record.get("legal_actions") or record.get("action_candidates") or []
    if not isinstance(actions, list):
        raise ValueError("legal_actions must be a list")
    result = []
    seen: set[str] = set()
    for action in actions:
        if not isinstance(action, dict) or not action.get("legal", False):
            continue
        action_id = _action_id(action)
        if action_id in seen:
            raise ValueError(f"duplicate stable action_id: {action_id}")
        seen.add(action_id)
        result.append(action)
    return result


def _numeric(mapping: Any, key: str) -> float:
    value = mapping.get(key, 0) if isinstance(mapping, dict) else 0
    return float(value) if isinstance(value, (int, float)) else 0.0


def _heuristic_scores(record: dict[str, Any], actions: list[dict[str, Any]]) -> dict[str, dict[str, float]]:
    """Conservative fallback used only when the C# evaluator is unavailable."""
    scores: dict[str, dict[str, float]] = {}
    for action in actions:
        aid = _action_id(action)
        source = action.get("source_model_id", "")
        card = next((c for c in (record.get("public_state", {}).get("hand", []) or [])
                     if c.get("instance_id") == action.get("source_instance_id")), {})
        stats = card.get("stats", {}) if isinstance(card, dict) else {}
        damage = _numeric(stats, "damage")
        block = _numeric(stats, "block")
        cost = _numeric(action, "effective_energy_cost")
        if action.get("kind") == "EndTurn":
            damage, block = 0.0, 0.0
        elif action.get("kind") == "UsePotion":
            block = max(block, 2.0)
        balanced = damage + 0.4 * block - 0.1 * cost
        scores[aid] = {
            "Balanced": balanced,
            "HighestDamage": damage,
            "MinimumLoss": block + 0.25 * damage - 0.1 * cost,
            "death_probability": 0.0,
        }
    return scores


def _top_k(scores: dict[str, dict[str, float]], objective: str, k: int) -> list[dict[str, Any]]:
    ordered = sorted(scores.items(), key=lambda item: (-item[1][objective], item[0]))
    return [
        {"action_id": aid, "value": values[objective], "rank": rank,
         "death_probability": values.get("death_probability", 0.0)}
        for rank, (aid, values) in enumerate(ordered[: max(1, k)], 1)
    ]


def _fallback_label(record: dict[str, Any], top_k: int) -> dict[str, Any]:
    actions = legal_actions(record)
    if not actions:
        return {
            "objectives": {name: {"best_actions": [], "value": 0.0, "action_values": {}} for name in OBJECTIVES},
            "teacher_best_actions": [], "teacher_top_k": [], "action_values": {},
            "death_probability": 0.0, "search_budget_ms": 0, "expanded_nodes": 0,
            "chance_branch": {"produced": False, "kind": "none"},
            "confidence": "Uncalculable", "search_complete": False,
            "label_quality": "Uncalculable",
            "risk_events": ["no_legal_actions"],
        }
    scores = _heuristic_scores(record, actions)
    objectives = {}
    for name in OBJECTIVES:
        ranking = _top_k(scores, name, top_k)
        objectives[name] = {
            "best_actions": [item["action_id"] for item in ranking[:1]],
            "value": ranking[0]["value"] if ranking else 0.0,
            "action_values": {aid: values[name] for aid, values in scores.items()},
        }
    balanced = objectives["Balanced"]
    return {
        "objectives": objectives,
        "teacher_best_actions": balanced["best_actions"],
        "teacher_top_k": _top_k(scores, "Balanced", top_k),
        "action_values": balanced["action_values"],
        "death_probability": 0.0,
        "search_budget_ms": 0,
        "expanded_nodes": 0,
        "chance_branch": {"produced": False, "kind": "none"},
        "confidence": "Estimated",
        "label_quality": "EstimatedByHeuristic",
        "search_complete": False,
        "risk_events": ["heuristic_teacher_fallback", "teacher_evaluator_unavailable"],
    }


class TeacherWorker:
    def __init__(self, evaluator: Callable[[dict[str, Any]], dict[str, Any]] | None = None,
                 top_k: int = 5, allow_heuristic_fallback: bool = False) -> None:
        self.evaluator = evaluator
        self.top_k = max(1, top_k)
        self.allow_heuristic_fallback = allow_heuristic_fallback

    def _request(self, record: dict[str, Any]) -> dict[str, Any]:
        actions = legal_actions(record)
        teacher_snapshot = record.get("teacher_snapshot") or record.get("teacher_state")
        if teacher_snapshot is None and not self.allow_heuristic_fallback:
            raise ValueError("teacher snapshot is missing")
        reconstructed, reconstruction_warnings = rebuild_combat_snapshot(
            teacher_snapshot or {}, record.get("public_state") or {})
        request = {
            "protocol": "sts2.teacher-evaluator.v1",
            "record_id": record.get("record_id"),
            "public_state": record.get("public_state"),
            "teacher_snapshot": teacher_snapshot,
            # A C# bridge can skip lossy reconstruction when the producer has
            # already emitted a full CombatSnapshot payload.
            "combat_snapshot": record.get("combat_snapshot") or reconstructed,
            "reconstruction_warnings": reconstruction_warnings,
            "legal_actions": actions,
            "search": {"objectives": list(OBJECTIVES), "top_k": self.top_k},
            "version": LOCK,
        }
        if self.evaluator is not None:
            label = dict(self.evaluator(request))
            if reconstruction_warnings:
                label["reconstruction_warnings"] = reconstruction_warnings
            return label
        if self.allow_heuristic_fallback:
            return _fallback_label(record, self.top_k)
        raise ValueError("no CombatSearchSession/Expectimax evaluator configured")

    def process(self, record: dict[str, Any]) -> dict[str, Any]:
        for key, expected in LOCK.items():
            if record.get(key) != expected:
                raise ValueError(f"{key}={record.get(key)!r}, expected {expected!r}")
        label = self._request(record)
        if not isinstance(label, dict):
            raise ValueError("evaluator response must be a JSON object")
        output = dict(record)
        output.update(label)
        output.setdefault("risk_events", [])
        output.setdefault("label_quality", {
            "Reliable": "ExactComplete" if output.get("search_complete") else "BudgetBound",
            "Estimated": "EstimatedByHeuristic",
            "LowConfidence": "EstimatedByHeuristic",
            "Uncalculable": "Uncalculable",
        }.get(output.get("confidence"), "Uncalculable"))
        reconstruction_warnings = output.get("reconstruction_warnings") or []
        if reconstruction_warnings:
            output["risk_events"] = sorted(set(output["risk_events"]) | set(reconstruction_warnings))
        if output.get("confidence") == "Reliable" and output.get("risk_events"):
            output["confidence"] = "Estimated"
        if not output.get("teacher_best_actions"):
            output["confidence"] = "Uncalculable"
            output["search_complete"] = False
            output["risk_events"] = sorted(set(output["risk_events"]) | {"teacher_label_missing"})
        return output


def aggregate_hidden_states(records: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in records:
        public_hash = record.get("state_hash_public") or record.get("public_state_hash")
        if public_hash:
            groups[str(public_hash)].append(record)
    result = []
    for public_hash, group in sorted(groups.items()):
        teacher_hashes = sorted({str(row.get("state_hash_teacher", "")) for row in group})
        values: dict[str, list[float]] = defaultdict(list)
        optimal: dict[str, int] = defaultdict(int)
        for row in group:
            for aid, value in (row.get("action_values") or {}).items():
                if isinstance(value, (int, float)):
                    values[aid].append(float(value))
            for aid in row.get("teacher_best_actions", []) or []:
                optimal[str(aid)] += 1
        means = {aid: sum(vals) / len(vals) for aid, vals in values.items() if vals}
        variances = {
            aid: (sum((value - means[aid]) ** 2 for value in vals) / (len(vals) - 1) if len(vals) > 1 else 0.0)
            for aid, vals in values.items() if vals
        }
        frequencies = {aid: count / len(group) for aid, count in optimal.items()}
        result.append({
            "public_state_hash": public_hash,
            "teacher_state_hashes": teacher_hashes,
            "action_value_mean": means,
            "action_value_variance": variances,
            "optimal_action_frequency": frequencies,
            "hidden_state_sensitive": len(set(tuple(sorted(row.get("teacher_best_actions", []) or [])) for row in group)) > 1,
        })
    return result


def _command_evaluator(command: str) -> Callable[[dict[str, Any]], dict[str, Any]]:
    argv = command.split()

    def evaluate(request: dict[str, Any]) -> dict[str, Any]:
        proc = subprocess.run(argv, input=json.dumps(request, ensure_ascii=False) + "\n",
                              capture_output=True, text=True, check=False)
        if proc.returncode != 0:
            raise RuntimeError(f"teacher evaluator exited {proc.returncode}: {proc.stderr[-500:]}")
        return json.loads(proc.stdout.strip().splitlines()[-1])
    return evaluate


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--aggregate-output", type=Path)
    parser.add_argument("--evaluator", help="Executable receiving teacher-evaluator.v1 JSON on stdin")
    parser.add_argument("--top-k", type=int, default=5)
    parser.add_argument("--allow-heuristic-fallback", action="store_true")
    args = parser.parse_args()
    evaluator = _command_evaluator(args.evaluator) if args.evaluator else None
    worker = TeacherWorker(evaluator, args.top_k, args.allow_heuristic_fallback)
    records = [json.loads(line) for line in args.input.read_text(encoding="utf-8").splitlines() if line.strip()]
    labelled = [worker.process(record) for record in records]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text("\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in labelled) + "\n", encoding="utf-8")
    if args.aggregate_output:
        args.aggregate_output.parent.mkdir(parents=True, exist_ok=True)
        args.aggregate_output.write_text(json.dumps(aggregate_hidden_states(labelled), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"records": len(labelled), "output": str(args.output), "aggregate": str(args.aggregate_output) if args.aggregate_output else None}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
