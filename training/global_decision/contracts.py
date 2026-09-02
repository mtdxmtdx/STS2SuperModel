"""Versioned, JSON-friendly contracts for the global decision prototype.

The contracts intentionally stay independent from the combat-specific training
records.  Public state validation is conservative: a field name that exposes
future RNG, ordered piles, or teacher state fails fast instead of being
silently dropped.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import hashlib
import json
from typing import Any, Mapping, Optional


class PublicStateLeakageError(ValueError):
    """Raised when a public observation contains privileged/future data."""


FORBIDDEN_PUBLIC_KEYS = frozenset(
    {
        "run_seed",
        "seed",
        "rng_raw_words",
        "rng_state",
        "rng_snapshot",
        "future_draw_order",
        "ordered_future_pile",
        "ordered_pile",
        "teacher_snapshot",
        "teacher_only_state",
        "actual_hidden_outcome",
        "future_nodes",
        "future_rewards",
        "future_shop",
    }
)


def _walk_forbidden(value: Any, path: str = "$") -> list[str]:
    found: list[str] = []
    if isinstance(value, Mapping):
        for key, child in value.items():
            key_text = str(key)
            if key_text.lower() in FORBIDDEN_PUBLIC_KEYS:
                found.append(f"{path}.{key_text}")
            found.extend(_walk_forbidden(child, f"{path}.{key_text}"))
    elif isinstance(value, (list, tuple)):
        for index, child in enumerate(value):
            found.extend(_walk_forbidden(child, f"{path}[{index}]"))
    return found


def validate_public_payload(payload: Mapping[str, Any]) -> None:
    """Validate that a public payload has no privileged/future fields."""

    found = _walk_forbidden(payload)
    if found:
        raise PublicStateLeakageError(", ".join(found))


def canonical_json(value: Any) -> str:
    """Return deterministic UTF-8 JSON used for IDs and repeat checks."""

    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def stable_hash(value: Any) -> str:
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def _copy_mapping(value: Optional[Mapping[str, Any]]) -> dict[str, Any]:
    return dict(value or {})


@dataclass(frozen=True)
class GlobalRunKey:
    schema_version: str
    feature_schema_version: str
    game_version: str
    game_branch: str
    game_commit: str
    assembly_sha256: str
    cli_protocol_version: str
    simulator_version: str
    semantic_catalog_version: str
    scorer_version: str
    model_version: str
    character: str
    player_count: int
    ascension: int
    game_mode: str
    modifiers: tuple[str, ...] = ()
    unlock_profile_hash: str = ""
    run_seed: Optional[str] = None
    run_context_hash: str = ""
    manifest_hash: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "feature_schema_version": self.feature_schema_version,
            "game_version": self.game_version,
            "game_branch": self.game_branch,
            "game_commit": self.game_commit,
            "assembly_sha256": self.assembly_sha256,
            "cli_protocol_version": self.cli_protocol_version,
            "simulator_version": self.simulator_version,
            "semantic_catalog_version": self.semantic_catalog_version,
            "scorer_version": self.scorer_version,
            "model_version": self.model_version,
            "character": self.character,
            "player_count": self.player_count,
            "ascension": self.ascension,
            "game_mode": self.game_mode,
            "modifiers": list(self.modifiers),
            "unlock_profile_hash": self.unlock_profile_hash,
            "run_seed": self.run_seed,
            "run_context_hash": self.run_context_hash,
            "manifest_hash": self.manifest_hash,
        }

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "GlobalRunKey":
        return cls(
            schema_version=str(data["schema_version"]),
            feature_schema_version=str(data["feature_schema_version"]),
            game_version=str(data["game_version"]),
            game_branch=str(data["game_branch"]),
            game_commit=str(data["game_commit"]),
            assembly_sha256=str(data["assembly_sha256"]),
            cli_protocol_version=str(data["cli_protocol_version"]),
            simulator_version=str(data["simulator_version"]),
            semantic_catalog_version=str(data["semantic_catalog_version"]),
            scorer_version=str(data["scorer_version"]),
            model_version=str(data["model_version"]),
            character=str(data["character"]),
            player_count=int(data["player_count"]),
            ascension=int(data["ascension"]),
            game_mode=str(data["game_mode"]),
            modifiers=tuple(str(x) for x in data.get("modifiers", [])),
            unlock_profile_hash=str(data.get("unlock_profile_hash", "")),
            run_seed=data.get("run_seed"),
            run_context_hash=str(data.get("run_context_hash", "")),
            manifest_hash=str(data.get("manifest_hash", "")),
        )


@dataclass(frozen=True)
class GlobalActionCandidate:
    action_id: Optional[str]
    action_type: str
    semantic_id: Optional[str]
    transport_action: str
    transport_args: Mapping[str, Any] = field(default_factory=dict)
    target_id: Optional[str] = None
    legal: bool = True
    restriction_reason: Optional[str] = None
    candidate_index: int = 0
    offer_snapshot_hash: str = ""
    parent_decision_id: str = ""
    continuation_id: Optional[str] = None
    candidate_role: str = "option"
    candidate_semantic_features_hash: str = ""
    opportunity_cost: Mapping[str, Any] = field(default_factory=dict)
    source_confidence: str = "unknown"

    def to_dict(self) -> dict[str, Any]:
        return {
            "action_id": self.action_id,
            "action_type": self.action_type,
            "semantic_id": self.semantic_id,
            "transport_action": self.transport_action,
            "transport_args": dict(self.transport_args),
            "target_id": self.target_id,
            "legal": self.legal,
            "restriction_reason": self.restriction_reason,
            "candidate_index": self.candidate_index,
            "offer_snapshot_hash": self.offer_snapshot_hash,
            "parent_decision_id": self.parent_decision_id,
            "continuation_id": self.continuation_id,
            "candidate_role": self.candidate_role,
            "candidate_semantic_features_hash": self.candidate_semantic_features_hash,
            "opportunity_cost": dict(self.opportunity_cost),
            "source_confidence": self.source_confidence,
        }


@dataclass(frozen=True)
class GlobalOfferSnapshot:
    offer_snapshot_hash: str
    decision_type: str
    candidates: tuple[GlobalActionCandidate, ...]
    candidate_order: tuple[str, ...]
    visible_context_hash: str
    legal_actions_complete: bool
    source: str
    screen_version: str = "1"
    omitted_ids: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return {
            "offer_snapshot_hash": self.offer_snapshot_hash,
            "decision_type": self.decision_type,
            "candidates": [candidate.to_dict() for candidate in self.candidates],
            "candidate_order": list(self.candidate_order),
            "visible_context_hash": self.visible_context_hash,
            "legal_actions_complete": self.legal_actions_complete,
            "source": self.source,
            "screen_version": self.screen_version,
            "omitted_ids": list(self.omitted_ids),
        }

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "GlobalOfferSnapshot":
        candidates = tuple(
            GlobalActionCandidate(
                action_id=item.get("action_id"),
                action_type=str(item.get("action_type", "")),
                semantic_id=item.get("semantic_id"),
                transport_action=str(item.get("transport_action", "")),
                transport_args=_copy_mapping(item.get("transport_args")),
                target_id=item.get("target_id"),
                legal=bool(item.get("legal", True)),
                restriction_reason=item.get("restriction_reason"),
                candidate_index=int(item.get("candidate_index", 0)),
                offer_snapshot_hash=str(item.get("offer_snapshot_hash", data.get("offer_snapshot_hash", ""))),
                parent_decision_id=str(item.get("parent_decision_id", "")),
                continuation_id=item.get("continuation_id"),
                candidate_role=str(item.get("candidate_role", "option")),
                candidate_semantic_features_hash=str(item.get("candidate_semantic_features_hash", "")),
                opportunity_cost=_copy_mapping(item.get("opportunity_cost")),
                source_confidence=str(item.get("source_confidence", "unknown")),
            )
            for item in data.get("candidates", [])
        )
        return cls(
            offer_snapshot_hash=str(data["offer_snapshot_hash"]),
            decision_type=str(data["decision_type"]),
            candidates=candidates,
            candidate_order=tuple(str(x) for x in data.get("candidate_order", [])),
            visible_context_hash=str(data.get("visible_context_hash", "")),
            legal_actions_complete=bool(data.get("legal_actions_complete", False)),
            source=str(data.get("source", "unknown")),
            screen_version=str(data.get("screen_version", "1")),
            omitted_ids=tuple(str(x) for x in data.get("omitted_ids", [])),
        )


@dataclass(frozen=True)
class GlobalRunStatePublic:
    schema_version: str
    state_public_hash: str
    character: str
    act: int
    floor: int
    ascension: int
    current_node: Optional[str]
    current_room_type: str
    hp: float
    max_hp: float
    gold: int
    deck_public: tuple[Mapping[str, Any], ...] = ()
    relic_public: tuple[Mapping[str, Any], ...] = ()
    potion_public: tuple[Mapping[str, Any], ...] = ()
    visible_map_graph: Mapping[str, Any] = field(default_factory=dict)
    visible_encounter_profile: Mapping[str, Any] = field(default_factory=dict)
    visible_options: tuple[Mapping[str, Any], ...] = ()
    visible_offers: tuple[Mapping[str, Any], ...] = ()
    legal_actions: tuple[GlobalActionCandidate, ...] = ()
    public_history: tuple[Mapping[str, Any], ...] = ()
    combat_summary: Optional[Mapping[str, Any]] = None
    field_completeness: Mapping[str, Any] = field(default_factory=dict)
    provenance: Mapping[str, Any] = field(default_factory=dict)
    legal_actions_complete: bool = False

    def to_dict(self) -> dict[str, Any]:
        payload = {
            "schema_version": self.schema_version,
            "state_public_hash": self.state_public_hash,
            "character": self.character,
            "act": self.act,
            "floor": self.floor,
            "ascension": self.ascension,
            "current_node": self.current_node,
            "current_room_type": self.current_room_type,
            "hp": self.hp,
            "max_hp": self.max_hp,
            "gold": self.gold,
            "deck_public": [dict(x) for x in self.deck_public],
            "relic_public": [dict(x) for x in self.relic_public],
            "potion_public": [dict(x) for x in self.potion_public],
            "visible_map_graph": dict(self.visible_map_graph),
            "visible_encounter_profile": dict(self.visible_encounter_profile),
            "visible_options": [dict(x) for x in self.visible_options],
            "visible_offers": [dict(x) for x in self.visible_offers],
            "legal_actions": [x.to_dict() for x in self.legal_actions],
            "public_history": [dict(x) for x in self.public_history],
            "combat_summary": dict(self.combat_summary) if self.combat_summary is not None else None,
            "field_completeness": dict(self.field_completeness),
            "provenance": dict(self.provenance),
            "legal_actions_complete": self.legal_actions_complete,
        }
        validate_public_payload(payload)
        return payload

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "GlobalRunStatePublic":
        validate_public_payload(data)
        actions = tuple(
            GlobalActionCandidate(
                action_id=item.get("action_id"),
                action_type=str(item.get("action_type", "")),
                semantic_id=item.get("semantic_id"),
                transport_action=str(item.get("transport_action", "")),
                transport_args=_copy_mapping(item.get("transport_args")),
                target_id=item.get("target_id"),
                legal=bool(item.get("legal", True)),
                restriction_reason=item.get("restriction_reason"),
                candidate_index=int(item.get("candidate_index", 0)),
                offer_snapshot_hash=str(item.get("offer_snapshot_hash", "")),
                parent_decision_id=str(item.get("parent_decision_id", "")),
                continuation_id=item.get("continuation_id"),
                candidate_role=str(item.get("candidate_role", "option")),
                candidate_semantic_features_hash=str(item.get("candidate_semantic_features_hash", "")),
                opportunity_cost=_copy_mapping(item.get("opportunity_cost")),
                source_confidence=str(item.get("source_confidence", "unknown")),
            )
            for item in data.get("legal_actions", [])
        )
        return cls(
            schema_version=str(data["schema_version"]),
            state_public_hash=str(data["state_public_hash"]),
            character=str(data["character"]),
            act=int(data["act"]),
            floor=int(data["floor"]),
            ascension=int(data.get("ascension", 0) or 0),
            current_node=data.get("current_node"),
            current_room_type=str(data.get("current_room_type", "unknown")),
            hp=float(data["hp"]),
            max_hp=float(data["max_hp"]),
            gold=int(data["gold"]),
            deck_public=tuple(_copy_mapping(x) for x in data.get("deck_public", [])),
            relic_public=tuple(_copy_mapping(x) for x in data.get("relic_public", [])),
            potion_public=tuple(_copy_mapping(x) for x in data.get("potion_public", [])),
            visible_map_graph=_copy_mapping(data.get("visible_map_graph")),
            visible_encounter_profile=_copy_mapping(data.get("visible_encounter_profile")),
            visible_options=tuple(_copy_mapping(x) for x in data.get("visible_options", [])),
            visible_offers=tuple(_copy_mapping(x) for x in data.get("visible_offers", [])),
            legal_actions=actions,
            public_history=tuple(_copy_mapping(x) for x in data.get("public_history", [])),
            combat_summary=_copy_mapping(data["combat_summary"]) if data.get("combat_summary") else None,
            field_completeness=_copy_mapping(data.get("field_completeness")),
            provenance=_copy_mapping(data.get("provenance")),
            legal_actions_complete=bool(data.get("legal_actions_complete", False)),
        )


@dataclass(frozen=True)
class GlobalDeckEvaluationRecord:
    decision_id: str
    offer_snapshot_hash: str
    state_public_hash: str
    candidate_action_id: Optional[str]
    delta_v_next_fight: Optional[float]
    delta_v_act: Optional[float]
    delta_v_run: Optional[float]
    delta_risk: Optional[float]
    expected_hp_loss_delta: Optional[float]
    teacher_mean: Optional[float]
    teacher_variance: Optional[float]
    teacher_ci95: Optional[float]
    label_source: str
    quality: str
    version_metadata: Mapping[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "decision_id": self.decision_id,
            "offer_snapshot_hash": self.offer_snapshot_hash,
            "state_public_hash": self.state_public_hash,
            "candidate_action_id": self.candidate_action_id,
            "delta_v_next_fight": self.delta_v_next_fight,
            "delta_v_act": self.delta_v_act,
            "delta_v_run": self.delta_v_run,
            "delta_risk": self.delta_risk,
            "expected_hp_loss_delta": self.expected_hp_loss_delta,
            "teacher_mean": self.teacher_mean,
            "teacher_variance": self.teacher_variance,
            "teacher_ci95": self.teacher_ci95,
            "label_source": self.label_source,
            "quality": self.quality,
            "version_metadata": dict(self.version_metadata),
        }


@dataclass(frozen=True)
class GlobalDatasetManifest:
    dataset_id: str
    schema_version: str
    stage: str
    game_version: str
    game_branch: str
    game_commit: str
    assembly_sha256: str
    cli_protocol_version: str
    simulator_version: str
    semantic_catalog_version: str
    feature_schema_version: str
    model_version: str
    generator_config_hash: str
    feature_config_hash: str
    split_policy: str
    row_count: int
    state_count: int
    action_count: int
    reliable_count: int
    estimated_count: int
    uncalculable_count: int
    source_hashes: tuple[str, ...]
    created_at_utc: str
    label_source: str = "EstimatedByHeuristic"
    public_leakage_count: int = 0
    stable_id_missing: int = 0
    quality_counts: Mapping[str, int] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return {
            "dataset_id": self.dataset_id,
            "schema_version": self.schema_version,
            "stage": self.stage,
            "game_version": self.game_version,
            "game_branch": self.game_branch,
            "game_commit": self.game_commit,
            "assembly_sha256": self.assembly_sha256,
            "cli_protocol_version": self.cli_protocol_version,
            "simulator_version": self.simulator_version,
            "semantic_catalog_version": self.semantic_catalog_version,
            "feature_schema_version": self.feature_schema_version,
            "model_version": self.model_version,
            "generator_config_hash": self.generator_config_hash,
            "feature_config_hash": self.feature_config_hash,
            "split_policy": self.split_policy,
            "row_count": self.row_count,
            "state_count": self.state_count,
            "action_count": self.action_count,
            "reliable_count": self.reliable_count,
            "estimated_count": self.estimated_count,
            "uncalculable_count": self.uncalculable_count,
            "source_hashes": list(self.source_hashes),
            "created_at_utc": self.created_at_utc,
            "label_source": self.label_source,
            "public_leakage_count": self.public_leakage_count,
            "stable_id_missing": self.stable_id_missing,
            "quality_counts": dict(self.quality_counts),
        }
