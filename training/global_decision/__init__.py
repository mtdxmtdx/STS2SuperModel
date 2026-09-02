"""Global decision prototype contracts and deterministic helpers."""

from .contracts import (
    GlobalActionCandidate,
    GlobalDatasetManifest,
    GlobalDeckEvaluationRecord,
    GlobalOfferSnapshot,
    GlobalRunKey,
    GlobalRunStatePublic,
    PublicStateLeakageError,
    canonical_json,
    stable_hash,
    validate_public_payload,
)
from .route_planner import RouteCandidate, RoutePlan, RoutePlanner
from .combat_summary_proxy import CombatSummaryProxy, estimate_combat_summary

__all__ = [
    "GlobalActionCandidate",
    "GlobalDatasetManifest",
    "GlobalDeckEvaluationRecord",
    "GlobalOfferSnapshot",
    "GlobalRunKey",
    "GlobalRunStatePublic",
    "PublicStateLeakageError",
    "canonical_json",
    "stable_hash",
    "validate_public_payload",
    "CombatSummaryProxy",
    "RouteCandidate",
    "RoutePlan",
    "RoutePlanner",
    "estimate_combat_summary",
]
