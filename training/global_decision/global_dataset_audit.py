"""Read-only quality audit for global Prototype data and smoke artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any, Iterable, Mapping

from .contracts import FORBIDDEN_PUBLIC_KEYS, validate_public_payload
from .model.dataset import load_rows, split_name


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _forbidden_paths(value: Any, path: str = "$") -> list[str]:
    found: list[str] = []
    if isinstance(value, Mapping):
        for key, child in value.items():
            key_text = str(key).lower()
            if key_text in FORBIDDEN_PUBLIC_KEYS:
                found.append(f"{path}.{key}")
            found.extend(_forbidden_paths(child, f"{path}.{key}"))
    elif isinstance(value, list):
        for index, child in enumerate(value):
            found.extend(_forbidden_paths(child, f"{path}[{index}]"))
    return found


def _read_jsonl(path: Path) -> tuple[list[dict[str, Any]], int]:
    rows: list[dict[str, Any]] = []
    malformed = 0
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            value = json.loads(line)
            if not isinstance(value, dict):
                malformed += 1
            else:
                rows.append(value)
        except json.JSONDecodeError:
            malformed += 1
    return rows, malformed


def _load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _candidate_kind(candidate: Mapping[str, Any]) -> str:
    text = " ".join(str(candidate.get(key, "")) for key in ("action_id", "action_type", "candidate_role", "option_id")).lower().replace(":", "_")
    if "skip" in text:
        return "skip"
    if "buy_card" in text or "shop_card" in text:
        return "buy_card"
    if "buy_relic" in text or "shop_relic" in text:
        return "buy_relic"
    if "buy_potion" in text or "shop_potion" in text:
        return "buy_potion"
    if "remove" in text:
        return "remove"
    if "smith" in text or "upgrade" in text:
        return "smith"
    if "rest" in text:
        return "rest"
    if "leave" in text:
        return "leave"
    if "proceed" in text:
        return "proceed"
    if "cancel" in text:
        return "cancel"
    return "normal"


def _iter_report_candidates(value: Any) -> Iterable[Mapping[str, Any]]:
    if not isinstance(value, Mapping):
        return
    for key in ("scenarios", "results"):
        groups = value.get(key)
        if not isinstance(groups, Mapping):
            continue
        for scenario in groups.values():
            if not isinstance(scenario, Mapping):
                continue
            candidates = scenario.get("candidates", [])
            if isinstance(candidates, list):
                for candidate in candidates:
                    if isinstance(candidate, Mapping):
                        # Route candidates store proxy summaries rather than
                        # action metadata; they are handled separately.
                        yield candidate


def _report_files(root: Path) -> list[Path]:
    return sorted(root.rglob("*.json"))


def _audit_smoke(root: Path) -> dict[str, Any]:
    reports = _report_files(root)
    report_count = 0
    reliable_true = 0
    quality_violations = 0
    unknown_count = 0
    stable_id_missing = 0
    illegal_count = 0
    candidate_count = 0
    coverage: dict[str, Any] = {}
    report_hash_checks: dict[str, bool] = {}
    for path in reports:
        if path.name == "global-quality-audit.json":
            continue
        try:
            value = _load_json(path)
        except (OSError, json.JSONDecodeError):
            continue
        report_count += 1
        if isinstance(value, Mapping):
            if value.get("reliable") is True:
                reliable_true += 1
            if "quality" in value and value.get("quality") != "EstimatedByHeuristic":
                quality_violations += 1
            if "label_source" in value and value.get("label_source") != "EstimatedByHeuristic":
                quality_violations += 1
            report_hash = value.get("report_sha256")
            for candidate in _iter_report_candidates(value):
                candidate_count += 1
                illegal_count += int(candidate.get("legal") is False)
                if candidate.get("reliable") is True:
                    reliable_true += 1
                stable_id_missing += int(candidate.get("stable_id_missing") is True)
                if candidate.get("quality") not in (None, "EstimatedByHeuristic"):
                    quality_violations += 1
                if candidate.get("label_source") not in (None, "EstimatedByHeuristic"):
                    quality_violations += 1
                if candidate.get("semantic_status") in {"unknown", "Uncalculable", "uncertainty"}:
                    unknown_count += 1
                if candidate.get("semantic_id") is None and "path" not in candidate and _candidate_kind(candidate) == "normal" and candidate.get("semantic_status") is None:
                    quality_violations += 1
            if "scenarios" in value and isinstance(value["scenarios"], Mapping):
                for name, scenario in value["scenarios"].items():
                    if not isinstance(scenario, Mapping):
                        continue
                    candidates = scenario.get("candidates", [])
                    if not isinstance(candidates, list):
                        continue
                    kinds = {_candidate_kind(item) for item in candidates if isinstance(item, Mapping)}
                    coverage[str(name)] = {
                        "candidate_count": len(candidates),
                        "kinds": sorted(kinds),
                        "has_unique_paths": len({tuple(item.get("path", [])) for item in candidates if isinstance(item, Mapping) and isinstance(item.get("path"), list)}) == len([item for item in candidates if isinstance(item, Mapping) and isinstance(item.get("path"), list)]),
                    }
        # Every smoke manifest stores the hash of its paired report.
        if path.name.endswith("manifest.json") and isinstance(value, Mapping) and value.get("report_sha256"):
            stem = path.name.replace("-manifest.json", "-report.json").replace("-smoke-manifest.json", "-smoke-report.json")
            paired = path.with_name(stem)
            if paired.exists():
                normalized = paired.read_text(encoding="utf-8").replace("\r\n", "\n").encode("utf-8")
                report_hash_checks[str(paired)] = hashlib.sha256(normalized).hexdigest() == str(value["report_sha256"])
    return {
        "report_count": report_count,
        "candidate_count": candidate_count,
        "illegal_count": illegal_count,
        "unknown_count": unknown_count,
        "stable_id_missing": stable_id_missing,
        "reliable_true": reliable_true,
        "quality_violations": quality_violations,
        "coverage": coverage,
        "report_hash_checks": report_hash_checks,
    }


def _model_repeat(root: Path) -> dict[str, Any]:
    checkpoint = root / "model_smoke" / "global-reward-prototype.pt"
    data = root / "global-synthetic-act1-v0.jsonl"
    if not checkpoint.exists():
        return {"available": False, "repeat_equal": False}
    try:
        import torch
        from .model.candidate_scorer import GlobalRewardCandidateScorer
        from .model.dataset import load_rows
        from .model.encoder import CandidateEncoder, GlobalRewardEncoder
        from .model.evaluate_prototype import evaluate_model

        payload = torch.load(checkpoint, map_location="cpu", weights_only=False)
        model = GlobalRewardCandidateScorer(payload["state_dim"], payload["candidate_dim"], payload["hidden_dim"])
        model.load_state_dict(payload["state_dict"])
        rows = load_rows(data)
        state_encoder, candidate_encoder = GlobalRewardEncoder(), CandidateEncoder()
        first = evaluate_model(rows, model, state_encoder, candidate_encoder)
        second = evaluate_model(rows, model, state_encoder, candidate_encoder)
        return {"available": True, "repeat_equal": first == second, "first_digest": first.get("prediction_digest"), "second_digest": second.get("prediction_digest")}
    except Exception as exc:  # pragma: no cover - surfaced in audit output
        return {"available": True, "repeat_equal": False, "error": f"{type(exc).__name__}: {exc}"}


def audit_global_prototype(root: Path) -> dict[str, Any]:
    data_path = root / "global-synthetic-act1-v0.jsonl"
    manifest_path = root / "global-synthetic-act1-v0-manifest.json"
    rows, malformed = _read_jsonl(data_path)
    manifest = _load_json(manifest_path)
    schema_versions = sorted({str(row.get("schema_version")) for row in rows})
    state_schema_versions = sorted({str(row.get("state_public", {}).get("schema_version")) for row in rows if isinstance(row.get("state_public"), Mapping)})
    split_by_scenario: dict[str, str] = {}
    split_conflicts = 0
    public_leakage_count = 0
    forbidden_field_count = 0
    duplicate_action_ids = 0
    stable_id_missing = 0
    reward_skip_rows = 0
    candidate_count = 0
    illegal_count = 0
    quality_violations = 0
    unknown_count = 0
    for row in rows:
        scenario_id = str(row.get("scenario_id", ""))
        split = split_name(scenario_id)
        if scenario_id in split_by_scenario and split_by_scenario[scenario_id] != split:
            split_conflicts += 1
        split_by_scenario[scenario_id] = split
        state = row.get("state_public", {})
        paths = _forbidden_paths(state)
        forbidden_field_count += len(paths)
        if paths:
            public_leakage_count += 1
        try:
            validate_public_payload(state)
        except ValueError:
            public_leakage_count += 1
        if row.get("label_source") != "EstimatedByHeuristic" or row.get("quality") != "EstimatedByHeuristic":
            quality_violations += 1
        candidates = row.get("offer_snapshot", {}).get("candidates", [])
        ids: list[str] = []
        for candidate in candidates if isinstance(candidates, list) else []:
            if not isinstance(candidate, Mapping):
                continue
            candidate_count += 1
            if candidate.get("legal") is False:
                illegal_count += 1
            action_id = candidate.get("action_id")
            if action_id is None:
                stable_id_missing += 1
            else:
                ids.append(str(action_id))
            if _candidate_kind(candidate) == "skip":
                reward_skip_rows += 1
            if candidate.get("semantic_status") in {"unknown", "Uncalculable", "uncertainty"}:
                unknown_count += 1
        duplicate_action_ids += len(ids) - len(set(ids))
    smoke = _audit_smoke(root)
    all_report_hashes = smoke["report_hash_checks"]
    repeat_files_equal = (root / "global-synthetic-act1-v0.jsonl").exists() and (root / "global-synthetic-act1-v0.repeat.jsonl").exists() and _sha256(root / "global-synthetic-act1-v0.jsonl") == _sha256(root / "global-synthetic-act1-v0.repeat.jsonl")
    model_repeat = _model_repeat(root)
    split_counts = {name: sum(value == name for value in split_by_scenario.values()) for name in ("train", "validation", "test")}
    required_smoke = {
        "shop": {"buy_card", "buy_relic", "buy_potion", "remove", "leave"},
        "campfire": {"rest", "smith", "leave"},
        "event": {"normal", "proceed", "leave", "cancel"},
        "ancient": {"normal", "proceed", "leave", "cancel"},
    }
    coverage_failures: dict[str, list[str]] = {}
    for domain, required in required_smoke.items():
        observed: set[str] = set()
        for name, value in smoke["coverage"].items():
            if domain in name:
                observed.update(value.get("kinds", []))
        missing = sorted(required - observed)
        if missing:
            coverage_failures[domain] = missing
    reliable_count = int(manifest.get("reliable_count", 0) or 0) + smoke["reliable_true"]
    quality_violations += smoke["quality_violations"]
    unknown_count += smoke["unknown_count"]
    audit = {
        "audit_id": "global-prototype-quality-v0",
        "passed": True,
        "read_only": True,
        "source_data_sha256": _sha256(data_path),
        "manifest_sha256": _sha256(manifest_path),
        "jsonl": {"rows": len(rows), "malformed": malformed, "schema_versions": schema_versions, "state_schema_versions": state_schema_versions, "manifest_schema_version": manifest.get("schema_version")},
        "splits": {"counts": split_counts, "scenario_count": len(split_by_scenario), "scenario_split_conflicts": split_conflicts},
        "public_leakage_count": public_leakage_count,
        "forbidden_field_count": forbidden_field_count,
        "stable_id_missing": stable_id_missing + int(smoke.get("stable_id_missing", 0) or 0),
        "duplicate_action_ids": duplicate_action_ids,
        "reliable_count": reliable_count,
        "label_quality_violations": quality_violations,
        "unknown_or_uncalculable_count": unknown_count,
        "candidate_statistics": {"dataset_candidates": candidate_count, "dataset_illegal": illegal_count, "smoke_candidates": smoke["candidate_count"], "smoke_illegal": smoke["illegal_count"]},
        "coverage": {"reward_skip_rows": reward_skip_rows, "smoke": smoke["coverage"], "coverage_failures": coverage_failures},
        "repeatability": {"repeat_jsonl_equal": repeat_files_equal, "smoke_report_hashes": all_report_hashes, "model_prediction": model_repeat},
        "prototype_reliable_flags": {"dataset_manifest_reliable_count": int(manifest.get("reliable_count", 0) or 0), "smoke_reliable_true": smoke["reliable_true"]},
    }
    audit["passed"] = all(
        [
            malformed == 0,
            len(schema_versions) == 1,
            split_conflicts == 0,
            public_leakage_count == 0,
            forbidden_field_count == 0,
            duplicate_action_ids == 0,
            stable_id_missing == 0,
            reliable_count == 0,
            quality_violations == 0,
            not coverage_failures,
            repeat_files_equal,
            model_repeat.get("repeat_equal") is True,
            all(all_report_hashes.values()) if all_report_hashes else True,
        ]
    )
    return audit


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("data/global_prototype"))
    parser.add_argument("--output", type=Path, default=Path("data/global_prototype/global-quality-audit.json"))
    args = parser.parse_args()
    audit = audit_global_prototype(args.root)
    args.output.write_text(json.dumps(audit, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"passed": audit["passed"], "public_leakage_count": audit["public_leakage_count"], "reliable_count": audit["reliable_count"], "rows": audit["jsonl"]["rows"], "output": str(args.output)}, sort_keys=True))
    return 0 if audit["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
