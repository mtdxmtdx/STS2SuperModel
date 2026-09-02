from __future__ import annotations

import hashlib
from pathlib import Path

import pytest

from training.global_decision.global_feature_manifest import (
    FORBIDDEN_INPUT_FIELDS,
    FEATURE_VECTOR_FIELDS,
    MANIFEST_VERSION,
    build_feature_manifest,
    validate_feature_manifest,
)


def _root() -> Path:
    return Path(__file__).resolve().parents[1] / "data" / "global_prototype"


def test_manifest_has_exact_version_and_public_contract() -> None:
    manifest = build_feature_manifest(_root())
    for key in ("feature_schema_version", "game_version", "game_branch", "game_commit", "assembly_sha256", "semantic_catalog_version", "simulator_version", "scorer_version", "prototype_model_version", "source_data_sha256", "public_input_fields", "forbidden_input_fields", "label_source", "quality", "reliable"):
        assert key in manifest
    assert manifest["manifest_version"] == MANIFEST_VERSION
    assert manifest["seed_in_feature_vector"] is False
    assert manifest["teacher_snapshot_in_manifest"] is False
    assert manifest["reliable"] is False
    assert manifest["reliable_count"] == 0
    assert not ({field.lower() for field in FEATURE_VECTOR_FIELDS} & {field.lower() for field in FORBIDDEN_INPUT_FIELDS})
    assert "teacher_snapshot" in manifest["forbidden_input_fields"]
    assert "teacher_snapshot" not in manifest


def test_version_mismatch_fails_closed() -> None:
    manifest = build_feature_manifest(_root())
    changed = dict(manifest, game_version="v0.110.0")
    with pytest.raises(ValueError, match="version mismatch"):
        validate_feature_manifest(changed, manifest)


def test_manifest_build_is_read_only_and_deterministic() -> None:
    source = _root() / "global-synthetic-act1-v0.jsonl"
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    first = build_feature_manifest(_root())
    second = build_feature_manifest(_root())
    after = hashlib.sha256(source.read_bytes()).hexdigest()
    assert first == second
    assert before == after == first["source_data_sha256"]
