"""Deterministic Beam/DP route planner over the public map graph."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping, Sequence

from .combat_summary_proxy import estimate_combat_summary
from .contracts import stable_hash, validate_public_payload
from .deck_health import deck_health
from .route_features import normalize_map_graph, path_sort_key, route_features


@dataclass(frozen=True)
class RouteCandidate:
    path: tuple[str, ...]
    score: float
    features: Mapping[str, Any]
    summaries: Mapping[str, Mapping[str, Any]]

    def to_dict(self) -> dict[str, Any]:
        return {"path": list(self.path), "score": self.score, "features": dict(self.features), "summaries": {k: dict(v) for k, v in self.summaries.items()}}


@dataclass(frozen=True)
class RoutePlan:
    state_public_hash: str
    algorithm: str
    horizon: int
    beam_width: int
    candidates: tuple[RouteCandidate, ...]
    selected_path: tuple[str, ...]

    def to_dict(self) -> dict[str, Any]:
        return {
            "state_public_hash": self.state_public_hash,
            "algorithm": self.algorithm,
            "horizon": self.horizon,
            "beam_width": self.beam_width,
            "selected_path": list(self.selected_path),
            "candidates": [candidate.to_dict() for candidate in self.candidates],
            "quality": "EstimatedByHeuristic",
            "source": "global-route-prototype",
            "reliable": False,
        }


class RoutePlanner:
    """Plan from visible topology and retain every enumerated candidate path."""

    def __init__(self, *, horizon: int = 5, beam_width: int = 8, algorithm: str = "beam") -> None:
        if horizon < 1 or beam_width < 1:
            raise ValueError("horizon and beam_width must be positive")
        if algorithm not in {"beam", "dp"}:
            raise ValueError("algorithm must be beam or dp")
        self.horizon = horizon
        self.beam_width = beam_width
        self.algorithm = algorithm

    @staticmethod
    def _paths(graph: Mapping[str, Any], start: str | None, horizon: int) -> list[tuple[str, ...]]:
        if not start or start not in graph["nodes"]:
            return []
        result: list[tuple[str, ...]] = []

        def visit(node_id: str, path: tuple[str, ...]) -> None:
            if path:
                result.append(path)
            if len(path) >= horizon:
                return
            for child in graph["children"].get(node_id, ()):
                if child not in path:
                    visit(child, path + (child,))

        for child in graph["children"].get(start, ()):
            visit(child, (child,))
        return result

    @staticmethod
    def _score(state: Mapping[str, Any], features: Mapping[str, Any]) -> float:
        hp_ratio = float(features["hp_ratio"])
        gold = float(features["gold"])
        health = features["deck_health"]
        # Soft trade-offs: elites are valuable but their risk is explicitly
        # priced; low HP shifts weight to campfires and survival.
        survival_weight = 1.0 + 8.0 * max(0.0, 0.55 - hp_ratio)
        elite_value = (0.30 + 0.65 * hp_ratio) * float(features["elite_count"])
        campfire_value = (0.25 + 1.60 * max(0.0, 0.55 - hp_ratio)) * float(features["campfire_count"])
        shop_value = min(1.0, gold / 150.0) * 0.35 * float(features["shop_count"])
        combat_readiness = 0.45 * float(health.get("frontload_score", 0.0)) + 0.30 * float(health.get("block_score", 0.0)) + 0.25 * float(health.get("draw_score", 0.0))
        path_value = 0.06 * float(features["path_length"]) + 0.20 * combat_readiness
        risk_cost = survival_weight * (float(features["death_risk_proxy"]) + 0.025 * float(features["expected_hp_loss_proxy"]))
        return round(elite_value + campfire_value + shop_value + path_value - risk_cost, 6)

    def plan(self, state: Mapping[str, Any]) -> RoutePlan:
        validate_public_payload(state)
        graph = normalize_map_graph(state.get("visible_map_graph") or {})
        state_hash = str(state.get("state_public_hash") or stable_hash({k: state.get(k) for k in ("character", "act", "floor", "hp", "max_hp", "gold", "deck_public", "visible_map_graph")}))
        paths = self._paths(graph, str(state.get("current_node") or graph.get("current") or ""), self.horizon)
        candidates: list[RouteCandidate] = []
        for path in paths:
            summaries = {node_id: estimate_combat_summary(state, graph["nodes"][node_id]).to_dict() for node_id in path if node_id in graph["nodes"]}
            features = route_features(state, path, node_summaries=summaries)
            candidates.append(RouteCandidate(path, self._score(state, features), features, summaries))
        candidates.sort(key=lambda item: (-item.score, path_sort_key(item.path, graph)))
        if not candidates:
            selected: tuple[str, ...] = ()
        elif self.algorithm == "dp":
            selected = self._dp_select(candidates)
        else:
            # The complete candidate list is retained; beam_width only marks
            # the expansion budget/selection frontier for downstream callers.
            selected = candidates[: self.beam_width][0].path
        return RoutePlan(state_hash, self.algorithm, self.horizon, self.beam_width, tuple(candidates), selected)

    @staticmethod
    def _dp_select(candidates: Sequence[RouteCandidate]) -> tuple[str, ...]:
        # Candidates are already deterministic and scored.  DP keeps the best
        # path per first node, then selects the best frontier path.
        best_by_first: dict[str, RouteCandidate] = {}
        for candidate in candidates:
            first = candidate.path[0]
            if first not in best_by_first or (candidate.score, tuple(candidate.path)) > (best_by_first[first].score, tuple(best_by_first[first].path)):
                best_by_first[first] = candidate
        return max(best_by_first.values(), key=lambda item: (item.score, tuple(reversed(item.path)))).path

    def replan(self, state: Mapping[str, Any]) -> RoutePlan:
        """Re-read the current public state; callers invoke after every node."""
        return self.plan(state)
