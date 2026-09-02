"""Data contracts for the local global-decision annotation GUI."""

from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def context_hash(context: dict[str, Any]) -> str:
    return hashlib.sha256(canonical_json(context).encode("utf-8")).hexdigest().upper()


@dataclass
class RunContext:
    schema_version: str = "global-run-context-v1"
    game_version: str = "v0.111.0"
    game_commit: str = "41cef1ea"
    assembly_sha256: str = ""
    cli_protocol_version: str = "0.2.0"
    character: str = "IRONCLAD"
    player_count: int = 1
    ascension: int = 0
    game_mode: str = "standard"
    modifiers: list[str] = field(default_factory=list)
    unlock_profile_hash: str = ""
    run_seed: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @property
    def run_context_hash(self) -> str:
        return context_hash(self.to_dict())


@dataclass
class DecisionRecord:
    record_id: str
    session_id: str
    run_context_hash: str
    episode_id: str
    branch_id: str
    decision_index: int
    act: int | None
    floor: int | None
    node_id: str | None
    node_coord: dict[str, int] | None
    node_type: str | None
    decision_type: str
    public_state_before: dict[str, Any]
    public_state_hash_before: str | None
    legal_actions: list[dict[str, Any]]
    selected_action: dict[str, Any]
    public_state_after: dict[str, Any] | None
    public_state_hash_after: str | None
    source_type: str
    source_id: str
    action_source: str
    provenance: str
    sl_status: str
    label_quality: str
    annotator_id: str | None = None
    combat_summary: dict[str, Any] | None = None
    outcome_source: str = "not_observed"
    notes: str = ""
    manual_override_fields: list[str] = field(default_factory=list)
    partial_episode: bool = False
    created_at_utc: str = field(default_factory=utc_now)
    confidence: float | None = None
    map_source: str = "unknown"
    state_source: str = "unknown"
    next_node: str | None = None
    realized_outcome: dict[str, Any] | None = None
    hp_before: int | None = None
    hp_after: int | None = None
    gold_before: int | None = None
    gold_after: int | None = None
    deck_diff: dict[str, Any] | None = None
    relic_diff: dict[str, Any] | None = None
    potion_diff: dict[str, Any] | None = None
    schema_version: str = "global-decision-record-v1"
    run_seed: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def new_context(payload: dict[str, Any]) -> RunContext:
    allowed = {field_name for field_name in RunContext.__dataclass_fields__}
    values = {key: value for key, value in payload.items() if key in allowed}
    if "modifiers" not in values or values["modifiers"] is None:
        values["modifiers"] = []
    values["character"] = str(values.get("character", "IRONCLAD")).upper()
    values["player_count"] = int(values.get("player_count", 1))
    values["ascension"] = int(values.get("ascension", 0))
    return RunContext(**values)
