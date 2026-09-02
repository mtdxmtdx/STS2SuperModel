"""Build and validate a read-only feature/version manifest for global prototypes."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any, Mapping


MANIFEST_VERSION = "global-feature-manifest-v1"
SCORER_VERSION = "candidate-conditioned-scorer-v1"
PUBLIC_INPUT_FIELDS = [
    "schema_version", "state_public_hash", "character", "act", "floor", "ascension",
    "current_node", "current_room_type", "hp", "max_hp", "gold", "deck_public",
    "relic_public", "potion_public", "visible_map_graph", "visible_encounter_profile",
    "visible_options", "visible_offers", "legal_actions", "legal_actions_complete",
]
FEATURE_VECTOR_FIELDS = [
    "character", "act", "floor", "ascension", "hp", "max_hp", "hp_ratio", "gold",
    "deck_public", "relic_public", "potion_public", "visible_encounter_profile",
    "candidate_semantic_id", "candidate_tags", "candidate_action_type",
    "candidate_cost", "candidate_legality_mask", "candidate_opportunity_cost",
]
FORBIDDEN_INPUT_FIELDS = [
    "seed", "run_seed", "rng_state", "rng_raw_words", "rng_snapshot", "future_draw_order",
    "ordered_future_pile", "ordered_pile", "future_map", "future_nodes", "future_rewards",
    "future_shop", "teacher_snapshot", "teacher_only_state", "actual_hidden_outcome",
]
METADATA_ONLY_FIELDS = ["seed", "run_seed", "run_context_hash"]


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _load(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected object: {path}")
    return value


def _versions(dataset: Mapping[str, Any], model: Mapping[str, Any]) -> dict[str, str]:
    return {
        "feature_schema_version": str(dataset["feature_schema_version"]),
        "game_version": str(dataset["game_version"]),
        "game_branch": str(dataset["game_branch"]),
        "game_commit": str(dataset["game_commit"]),
        "assembly_sha256": str(dataset["assembly_sha256"]),
        "semantic_catalog_version": str(dataset["semantic_catalog_version"]),
        "simulator_version": str(dataset["simulator_version"]),
        "scorer_version": SCORER_VERSION,
        "prototype_model_version": str(model.get("model_id", "global-reward-policy-prototype-v0")),
    }


def build_feature_manifest(root: Path) -> dict[str, Any]:
    """Read source manifests and return a deterministic compatibility contract."""

    dataset_path = root / "global-synthetic-act1-v0.jsonl"
    dataset_manifest_path = root / "global-synthetic-act1-v0-manifest.json"
    model_manifest_path = root / "model_smoke" / "global-reward-prototype-manifest.json"
    dataset = _load(dataset_manifest_path)
    model = _load(model_manifest_path)
    versions = _versions(dataset, model)
    forbidden_lower = {field.lower() for field in FORBIDDEN_INPUT_FIELDS}
    vector_forbidden = sorted(field for field in FEATURE_VECTOR_FIELDS if field.lower() in forbidden_lower)
    if vector_forbidden:
        raise ValueError(f"forbidden feature vector fields: {vector_forbidden}")
    return {
        "manifest_version": MANIFEST_VERSION,
        "manifest_type": "global_public_feature_contract",
        **versions,
        "prototype_model_stage": str(model.get("model_stage", "prototype")),
        "source_data_path": "data/global_prototype/global-synthetic-act1-v0.jsonl",
        "source_data_sha256": _sha256(dataset_path),
        "source_dataset_manifest_sha256": _sha256(dataset_manifest_path),
        "source_model_manifest_sha256": _sha256(model_manifest_path),
        "public_input_fields": list(PUBLIC_INPUT_FIELDS),
        "feature_vector_fields": list(FEATURE_VECTOR_FIELDS),
        "forbidden_input_fields": list(FORBIDDEN_INPUT_FIELDS),
        "metadata_only_fields": list(METADATA_ONLY_FIELDS),
        "seed_in_feature_vector": False,
        "teacher_snapshot_in_manifest": False,
        "label_source": "EstimatedByHeuristic",
        "quality": "EstimatedByHeuristic",
        "reliable": False,
        "reliable_count": 0,
        "compatibility_policy": "exact-match-all-version-fields-and-source-hashes",
    }


def validate_feature_manifest(manifest: Mapping[str, Any], expected: Mapping[str, Any]) -> None:
    """Fail closed when a model/data manifest is mixed across versions."""

    fields = (
        "manifest_version", "feature_schema_version", "game_version", "game_branch", "game_commit",
        "assembly_sha256", "semantic_catalog_version", "simulator_version", "scorer_version",
        "prototype_model_version", "source_data_sha256", "source_dataset_manifest_sha256",
        "source_model_manifest_sha256",
    )
    mismatches = {
        field: (manifest.get(field), expected.get(field))
        for field in fields
        if manifest.get(field) != expected.get(field)
    }
    if mismatches:
        raise ValueError(f"global feature manifest version mismatch: {mismatches}")
    if manifest.get("seed_in_feature_vector") is not False:
        raise ValueError("seed is not allowed in feature vector")
    if manifest.get("teacher_snapshot_in_manifest") is not False:
        raise ValueError("teacher snapshot is not allowed in manifest")
    if manifest.get("reliable") is not False or int(manifest.get("reliable_count", 0) or 0) != 0:
        raise ValueError("prototype manifest cannot be Reliable")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("data/global_prototype"))
    parser.add_argument("--output", type=Path, default=Path("data/global_prototype/global-feature-manifest.json"))
    args = parser.parse_args()
    manifest = build_feature_manifest(args.root)
    validate_feature_manifest(manifest, manifest)
    args.output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "manifest_version": manifest["manifest_version"], "source_data_sha256": manifest["source_data_sha256"], "reliable": manifest["reliable"], "seed_in_feature_vector": manifest["seed_in_feature_vector"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
