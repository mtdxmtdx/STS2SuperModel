"""Local HTTP GUI backend for manually transcribing expert global decisions."""

from __future__ import annotations

import argparse
import json
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from cli_session import CliError, CliSession, default_cli_path
from models import DecisionRecord, context_hash, new_context, utc_now
from storage import AnnotationStore
from teacher_search import root_from_cli_response, search_payload
from provider_tree import JsonlProvider, build_tree


ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = ROOT.parent
DEFAULT_DATA_DIR = ROOT / "data"


def json_response(handler: BaseHTTPRequestHandler, value: Any, status: int = 200) -> None:
    body = json.dumps(value, ensure_ascii=False).encode("utf-8")
    handler.send_response(status)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(body)))
    handler.send_header("Cache-Control", "no-store")
    handler.end_headers()
    handler.wfile.write(body)


def read_json(handler: BaseHTTPRequestHandler) -> dict[str, Any]:
    length = int(handler.headers.get("Content-Length", "0"))
    if length <= 0:
        return {}
    value = json.loads(handler.rfile.read(length).decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("JSON request body must be an object")
    return value


def path_id(path: str, prefix: str) -> str | None:
    parts = [part for part in path.split("/") if part]
    if len(parts) == 3 and parts[0] == "api" and parts[1] == "sessions" and parts[2] not in {""}:
        return parts[2]
    return None


def extract_run_summary(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    histories = data.get("map_point_history") or []
    floors = []
    for act_index, act_history in enumerate(histories, start=1):
        for floor_index, point in enumerate(act_history or [], start=1):
            if not isinstance(point, dict):
                continue
            stats = (point.get("player_stats") or [{}])[0] or {}
            rooms = point.get("rooms") or []
            floors.append(
                {
                    "act": act_index,
                    "floor": floor_index,
                    "map_point_type": point.get("map_point_type"),
                    "rooms": rooms,
                    "hp": stats.get("current_hp"),
                    "max_hp": stats.get("max_hp"),
                    "gold": stats.get("current_gold"),
                    "damage_taken": stats.get("damage_taken", 0),
                    "hp_healed": stats.get("hp_healed", 0),
                    "gold_gained": stats.get("gold_gained", 0),
                    "gold_spent": stats.get("gold_spent", 0),
                    "card_choices": stats.get("card_choices") or [],
                    "cards_gained": stats.get("cards_gained") or [],
                    "cards_removed": stats.get("cards_removed") or [],
                    "cards_transformed": stats.get("cards_transformed") or [],
                    "upgraded_cards": stats.get("upgraded_cards") or [],
                    "relic_choices": stats.get("relic_choices") or [],
                    "potion_choices": stats.get("potion_choices") or [],
                    "event_choices": stats.get("event_choices") or [],
                    "ancient_choice": stats.get("ancient_choice") or [],
                    "rest_site_choices": stats.get("rest_site_choices") or [],
                    "raw_player_stats": stats,
                }
            )
    return {
        "source_path": str(path),
        "run": {key: data.get(key) for key in ("schema_version", "build_id", "seed", "ascension", "game_mode", "acts", "modifiers", "win", "was_abandoned")},
        "player": (data.get("players") or [None])[0],
        "floors": floors,
    }


def align_run_to_map(summary: dict[str, Any], map_value: dict[str, Any] | None) -> dict[str, Any]:
    """Align observed floor types to a CLI map when one is available."""
    if not map_value or not isinstance(map_value.get("rows"), list):
        return {"status": "not_available", "matched_count": 0, "mismatch_count": 0, "items": []}
    nodes: dict[int, list[dict[str, Any]]] = {}
    for row in map_value.get("rows", []):
        if not isinstance(row, list):
            continue
        for node in row:
            if isinstance(node, dict) and isinstance(node.get("row"), int):
                nodes.setdefault(node["row"], []).append(node)
    items: list[dict[str, Any]] = []
    mismatch_count = 0
    for floor in summary.get("floors", []):
        floor_number = floor.get("floor")
        candidates = nodes.get(floor_number, [])
        observed = str(floor.get("map_point_type") or "").lower()
        matched = any(str(node.get("type") or "").lower() == observed for node in candidates) if observed else bool(candidates)
        if not matched:
            mismatch_count += 1
        items.append({"act": floor.get("act"), "floor": floor_number, "observed_type": floor.get("map_point_type"), "candidate_count": len(candidates), "matched": matched})
    return {"status": "ok", "matched_count": len(items) - mismatch_count, "mismatch_count": mismatch_count, "items": items}


class Application:
    def __init__(self, data_dir: Path):
        self.data_dir = data_dir
        self.store = AnnotationStore(data_dir / "annotation.sqlite")
        self.cli: dict[str, CliSession] = {}
        self.lock = threading.RLock()

    def create_session(self, payload: dict[str, Any]) -> dict[str, Any]:
        context = new_context(payload.get("context") or payload)
        source = payload.get("source") or {}
        session = self.store.create_session(context, source)
        cli_path = payload.get("cli_path")
        start_cli = bool(payload.get("start_cli", bool(cli_path)))
        if start_cli:
            executable = Path(cli_path) if cli_path else default_cli_path(PROJECT_ROOT)
            try:
                cli = CliSession(executable, env={"STS2_LIB": str(payload["sts2_lib"])} if payload.get("sts2_lib") else {})
                start_result = cli.start(context)
                with self.lock:
                    self.cli[session["session_id"]] = cli
                session["cli"] = {"started": True, "executable": str(executable), "start_result": start_result}
                if start_result.get("decision") == "map_select":
                    session["public_state"] = start_result
                    session["state_hash"] = start_result.get("post_state_hash")
            except (CliError, OSError) as exc:
                session["cli"] = {"started": False, "error": str(exc), "executable": str(executable)}
        self.store.save_session(session)
        return session

    def get_cli(self, session_id: str) -> CliSession:
        with self.lock:
            cli = self.cli.get(session_id)
        if cli is None or not cli.running:
            raise CliError("CLI session is not running; create the session with start_cli=true")
        return cli

    def refresh_map(self, session_id: str) -> dict[str, Any]:
        response = self.get_cli(session_id).get_map()
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        session["map"] = response
        session["public_state"] = response
        session["state_hash"] = response.get("post_state_hash")
        self.store.save_session(session)
        return response

    def add_decision(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        selected = payload.get("selected_action")
        if selected is None:
            raise ValueError("selected_action is required")
        if isinstance(selected, str):
            selected = {"action_id": selected}
        if not isinstance(selected, dict):
            raise ValueError("selected_action must be a string or object")

        execute = bool(payload.get("execute", False))
        response: dict[str, Any] | None = None
        if execute:
            command = payload.get("cli_command") or {}
            action = command.get("action") or selected.get("cli_action")
            if not action:
                raise ValueError("execute=true requires cli_command.action or selected_action.cli_action")
            response = self.get_cli(session_id).execute_action(str(action), command.get("args") or {})

        state_before = payload.get("public_state_before") or session.get("public_state") or {}
        state_after = payload.get("public_state_after") or (response if response else None)
        legal_actions = payload.get("legal_actions") or []
        has_public_pair = bool(state_before) and isinstance(state_after, dict)
        quality = payload.get("label_quality") or ("ExactPublic" if has_public_pair and legal_actions else "ObservedPartial")
        record = DecisionRecord(
            record_id=f"global-decision-{uuid.uuid4().hex[:12]}",
            session_id=session_id,
            run_context_hash=session["run_context_hash"],
            episode_id=str(payload.get("episode_id") or session_id),
            branch_id=str(payload.get("branch_id") or "main"),
            decision_index=len(session.get("decisions", [])),
            act=payload.get("act"),
            floor=payload.get("floor"),
            node_id=payload.get("node_id"),
            node_coord=payload.get("node_coord"),
            node_type=payload.get("node_type"),
            decision_type=str(payload.get("decision_type") or "unknown"),
            public_state_before=state_before,
            public_state_hash_before=payload.get("public_state_hash_before") or session.get("state_hash") or (context_hash(state_before) if state_before else None),
            legal_actions=legal_actions,
            selected_action=selected,
            public_state_after=state_after,
            public_state_hash_after=payload.get("public_state_hash_after") or ((response or {}).get("post_state_hash") if response else None) or (context_hash(state_after) if state_after else None),
            source_type=str(payload.get("source_type") or (session.get("source") or {}).get("type") or "expert_video"),
            source_id=str(payload.get("source_id") or (session.get("source") or {}).get("id") or "manual"),
            action_source=str(payload.get("action_source") or "human_expert_observed"),
            provenance=str(payload.get("provenance") or "video_manual_transcription"),
            sl_status=str(payload.get("sl_status") or (session.get("source") or {}).get("sl_status") or "unknown"),
            label_quality=quality,
            combat_summary=payload.get("combat_summary"),
            video_timestamp=payload.get("video_timestamp"),
            expert_id=payload.get("expert_id") or (session.get("source") or {}).get("expert_id"),
            outcome_source=str(payload.get("outcome_source") or ("cli_observed" if response else "not_observed")),
            notes=str(payload.get("notes") or ""),
            manual_override_fields=list(payload.get("manual_override_fields") or []),
            partial_episode=bool(payload.get("partial_episode", False)),
            confidence=float(payload["confidence"]) if payload.get("confidence") not in (None, "") else None,
            map_source=str(payload.get("map_source") or ("cli_observed" if session.get("map") else "unknown")),
            state_source=str(payload.get("state_source") or ("cli_observed" if state_before else "unknown")),
            next_node=payload.get("next_node"),
            realized_outcome=payload.get("realized_outcome"),
            hp_before=payload.get("hp_before"),
            hp_after=payload.get("hp_after"),
            gold_before=payload.get("gold_before"),
            gold_after=payload.get("gold_after"),
            deck_diff=payload.get("deck_diff"),
            relic_diff=payload.get("relic_diff"),
            potion_diff=payload.get("potion_diff"),
            schema_version=str(payload.get("schema_version") or "global-decision-record-v1"),
            run_seed=str((session.get("context") or {}).get("run_seed") or ""),
        )
        value = self.store.add_decision(record)
        session = self.store.get_session(session_id) or session
        if state_after:
            session["public_state"] = state_after
            session["state_hash"] = record.public_state_hash_after
        session["current_node"] = payload.get("node_id") or session.get("current_node")
        self.store.save_session(session)
        return {"record": value, "cli_response": response}

    def create_checkpoint(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        checkpoint_id = f"checkpoint-{uuid.uuid4().hex[:12]}"
        checkpoint: dict[str, Any] = {
            "checkpoint_id": checkpoint_id,
            "label": str(payload.get("label") or checkpoint_id),
            "branch_id": str(payload.get("branch_id") or "main"),
            "created_at_utc": utc_now(),
            "decision_count": len(session.get("decisions", [])),
            "map": session.get("map"),
            "public_state": session.get("public_state") or {},
            "state_hash": session.get("state_hash"),
            "current_node": session.get("current_node"),
            "cli_save_path": None,
            "cli_response": None,
        }
        if bool(payload.get("save_cli", False)):
            checkpoint_dir = self.data_dir / "checkpoints" / session_id
            checkpoint_dir.mkdir(parents=True, exist_ok=True)
            save_path = checkpoint_dir / f"{checkpoint_id}.run"
            try:
                response = self.get_cli(session_id).send({"cmd": "write_continue_save", "path": str(save_path)})
                checkpoint["cli_save_path"] = str(save_path)
                checkpoint["cli_response"] = response
            except CliError as exc:
                checkpoint["cli_response"] = {"type": "error", "message": str(exc)}
        session.setdefault("checkpoints", []).append(checkpoint)
        self.store.save_session(session)
        return checkpoint

    def restore_checkpoint(self, session_id: str, checkpoint_id: str, load_cli: bool = False) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        checkpoint = next((item for item in session.get("checkpoints", []) if item.get("checkpoint_id") == checkpoint_id), None)
        if checkpoint is None:
            raise KeyError(checkpoint_id)
        cli_response = None
        if load_cli and checkpoint.get("cli_save_path"):
            cli_response = self.get_cli(session_id).send({"cmd": "load_save", "path": checkpoint["cli_save_path"]})
            session["public_state"] = cli_response
            session["state_hash"] = cli_response.get("post_state_hash")
        else:
            session["map"] = checkpoint.get("map")
            session["public_state"] = checkpoint.get("public_state") or {}
            session["state_hash"] = checkpoint.get("state_hash")
            session["current_node"] = checkpoint.get("current_node")
        session["restored_from_checkpoint"] = checkpoint_id
        self.store.save_session(session)
        return {"checkpoint": checkpoint, "session": session, "cli_response": cli_response}

    def create_branch(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        parent = payload.get("parent_checkpoint_id")
        if parent and not any(item.get("checkpoint_id") == parent for item in session.get("checkpoints", [])):
            raise ValueError(f"unknown parent checkpoint: {parent}")
        branch = {
            "branch_id": f"branch-{uuid.uuid4().hex[:12]}",
            "name": str(payload.get("name") or "counterfactual"),
            "parent_checkpoint_id": parent,
            "created_at_utc": utc_now(),
        }
        session.setdefault("branches", []).append(branch)
        self.store.save_session(session)
        return branch

    def import_run(self, session_id: str, path: str) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        summary = extract_run_summary(Path(path))
        summary["map_alignment"] = align_run_to_map(summary, session.get("map"))
        session["run_history"] = summary
        if not session["context"].get("run_seed") and summary.get("run", {}).get("seed"):
            session["context"]["run_seed"] = summary["run"]["seed"]
            refreshed_context = new_context(session["context"])
            session["context"] = refreshed_context.to_dict()
            session["run_context_hash"] = refreshed_context.run_context_hash
        self.store.save_session(session)
        return summary

    def export(self, session_id: str, output: str | None = None) -> dict[str, Any]:
        target = Path(output) if output else self.data_dir / "global_behavior.jsonl"
        return self.store.export_jsonl(session_id, target)

    def validate(self, session_id: str) -> dict[str, Any]:
        return self.store.validate_session(session_id)

    def diff(self, session_id: str, record_id: str | None = None) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        records = session.get("decisions", [])
        record = next((item for item in records if item.get("record_id") == record_id), None) if record_id else (records[-1] if records else None)
        if record is None:
            raise KeyError(record_id or "latest decision")
        before = record.get("public_state_before") or {}
        after = record.get("public_state_after") or {}
        keys = sorted(set(before) | set(after))
        changed = {key: {"before": before.get(key), "after": after.get(key)} for key in keys if before.get(key) != after.get(key)}
        return {"record_id": record.get("record_id"), "decision_type": record.get("decision_type"), "changed_public_fields": changed, "deck_diff": record.get("deck_diff"), "relic_diff": record.get("relic_diff"), "potion_diff": record.get("potion_diff"), "realized_outcome": record.get("realized_outcome"), "pre_state_hash": record.get("public_state_hash_before"), "post_state_hash": record.get("public_state_hash_after")}

    def add_teacher_record(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        parent = payload.get("parent_checkpoint_id")
        if not parent or not any(item.get("checkpoint_id") == parent for item in session.get("checkpoints", [])):
            raise ValueError("parent_checkpoint_id must reference an existing checkpoint")
        action_values = payload.get("action_values") or []
        if not isinstance(action_values, list):
            raise ValueError("action_values must be a list")
        record = {
            "teacher_record_id": f"global-teacher-{uuid.uuid4().hex[:12]}",
            "schema_version": "global-teacher-record-v1",
            "session_id": session_id,
            "run_context_hash": session.get("run_context_hash"),
            "run_seed": str((session.get("context") or {}).get("run_seed") or ""),
            "episode_id": str(payload.get("episode_id") or session_id),
            "branch_id": str(payload.get("branch_id") or "main"),
            "parent_checkpoint_id": parent,
            "public_state": payload.get("public_state") or session.get("public_state") or {},
            "legal_actions": payload.get("legal_actions") or [],
            "action_values": action_values,
            "teacher_best_actions": payload.get("teacher_best_actions") or [],
            "teacher_value": payload.get("teacher_value"),
            "source_type": "counterfactual_branch",
            "label_quality": str(payload.get("label_quality") or "CounterfactualTeacher"),
            "created_at_utc": utc_now(),
            "notes": str(payload.get("notes") or ""),
        }
        session.setdefault("teacher_records", []).append(record)
        self.store.save_session(session)
        return record

    def export_teacher(self, session_id: str, output: str | None = None) -> dict[str, Any]:
        target = Path(output) if output else self.data_dir / "global_teacher.jsonl"
        return self.store.export_teacher_jsonl(session_id, target)

    def export_reliable(self, session_id: str, output: str | None = None) -> dict[str, Any]:
        target = Path(output) if output else self.data_dir / "global_behavior.reliable.jsonl"
        return self.store.export_reliable_jsonl(session_id, target)

    @staticmethod
    def _teacher_heuristic(response: dict[str, Any]) -> float | None:
        """Extract a conservative scalar from a CLI post-action response."""
        state = response.get("public_state") if isinstance(response.get("public_state"), dict) else (response.get("player") if isinstance(response.get("player"), dict) else response)
        if not isinstance(state, dict):
            return None
        hp = state.get("hp", state.get("current_hp"))
        max_hp = state.get("max_hp")
        gold = state.get("gold", state.get("current_gold"))
        if not any(isinstance(value, (int, float)) for value in (hp, max_hp, gold)):
            return None
        score = 0.0
        if isinstance(hp, (int, float)):
            score += float(hp)
        if isinstance(max_hp, (int, float)) and max_hp > 0:
            score += 25.0 * float(hp or 0) / float(max_hp)
        if isinstance(gold, (int, float)):
            score += 0.02 * float(gold)
        if state.get("win") is True or state.get("victory") is True:
            score += 1000.0
        if state.get("dead") is True or state.get("game_over") is True:
            score -= 1000.0
        return round(score, 6)

    def evaluate_teacher(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        """Evaluate one-step counterfactual actions from a CLI checkpoint.

        This is an explicit heuristic bridge for the future global teacher. It
        never writes to the human decision list and always restores the parent
        checkpoint before returning.
        """
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        checkpoint_id = str(payload.get("parent_checkpoint_id") or "")
        checkpoint = next((item for item in session.get("checkpoints", []) if item.get("checkpoint_id") == checkpoint_id), None)
        if checkpoint is None:
            raise ValueError("parent_checkpoint_id must reference an existing checkpoint")
        if not checkpoint.get("cli_save_path"):
            raise ValueError("checkpoint requires save_cli=true for teacher evaluation")
        actions = payload.get("actions") or []
        if not isinstance(actions, list) or not actions:
            raise ValueError("actions must be a non-empty list")
        values: list[dict[str, Any]] = []
        try:
            for item in actions:
                if not isinstance(item, dict) or not item.get("action_id"):
                    raise ValueError("each action requires action_id")
                self.restore_checkpoint(session_id, checkpoint_id, load_cli=True)
                response = self.get_cli(session_id).execute_action(str(item.get("cli_action") or item["action_id"]), item.get("args") or {})
                values.append({"action_id": str(item["action_id"]), "value": self._teacher_heuristic(response), "response": response})
        finally:
            self.restore_checkpoint(session_id, checkpoint_id, load_cli=True)
        numeric = [item for item in values if isinstance(item.get("value"), (int, float))]
        best_value = max((item["value"] for item in numeric), default=None)
        best_actions = [item["action_id"] for item in numeric if item["value"] == best_value] if best_value is not None else []
        teacher_payload = {
            "parent_checkpoint_id": checkpoint_id,
            "action_values": [{"action_id": item["action_id"], "value": item["value"]} for item in values],
            "teacher_best_actions": best_actions,
            "teacher_value": best_value,
            "legal_actions": actions,
            "notes": "one-step heuristic bridge; replace with global counterfactual evaluator",
            "label_quality": "EstimatedByHeuristic",
        }
        record = self.add_teacher_record(session_id, teacher_payload)
        return {"record": record, "evaluations": values}

    def search_teacher(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        parent = str(payload.get("parent_checkpoint_id") or "")
        if not parent or not any(item.get("checkpoint_id") == parent for item in session.get("checkpoints", [])):
            raise ValueError("parent_checkpoint_id must reference an existing checkpoint")
        search_input = dict(payload)
        if not isinstance(search_input.get("root"), dict) and isinstance(payload.get("cli_response"), dict):
            search_input["root"] = root_from_cli_response(payload["cli_response"], payload.get("outcomes_by_action"))
        result = search_payload(search_input)
        record = self.add_teacher_record(session_id, {
            "parent_checkpoint_id": parent,
            "episode_id": payload.get("episode_id"),
            "branch_id": payload.get("branch_id"),
            "public_state": search_input.get("root", {}).get("state") or session.get("public_state") or {},
            "legal_actions": search_input.get("root", {}).get("actions") or [],
            "action_values": result["action_values"],
            "teacher_best_actions": result["best_actions"],
            "teacher_value": result["value"],
            "label_quality": str(payload.get("label_quality") or "CounterfactualTeacher"),
            "notes": f"expectimax depth={payload.get('depth', 1)} nodes={result['nodes_evaluated']}",
        })
        return {"record": record, "search": result}

    def build_cli_tree(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        """Build a deterministic probability tree by replaying CLI branches.

        Each CLI execution is a single observed outcome (probability 1.0).
        Stochastic hidden outcomes must be supplied by a simulator provider and
        are intentionally not guessed here.
        """
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        checkpoint_id = str(payload.get("parent_checkpoint_id") or "")
        checkpoint = next((item for item in session.get("checkpoints", []) if item.get("checkpoint_id") == checkpoint_id), None)
        if checkpoint is None or not checkpoint.get("cli_save_path"):
            raise ValueError("parent_checkpoint_id must reference a checkpoint saved with save_cli=true")
        actions = payload.get("actions") or []
        if not isinstance(actions, list) or not actions:
            raise ValueError("actions must be a non-empty list")
        depth = int(payload.get("depth", 1))
        if depth < 1 or depth > 4:
            raise ValueError("depth must be between 1 and 4")
        tree_dir = self.data_dir / "teacher-trees" / session_id
        tree_dir.mkdir(parents=True, exist_ok=True)
        cli = self.get_cli(session_id)

        def save_cli_state() -> str:
            path = tree_dir / f"branch-{uuid.uuid4().hex[:12]}.run"
            response = cli.send({"cmd": "write_continue_save", "path": str(path)})
            if response.get("type") == "error" or not path.exists():
                raise CliError(str(response.get("message") or "CLI did not write branch save"))
            return str(path)

        def expand(save_path: str, specs: list[dict[str, Any]], remaining: int, state: dict[str, Any] | None = None) -> dict[str, Any]:
            root: dict[str, Any] = {"state": state or {}, "actions": []}
            for spec in specs:
                if not isinstance(spec, dict) or not spec.get("action_id"):
                    raise ValueError("each CLI tree action requires action_id")
                self.restore_checkpoint(session_id, checkpoint_id, load_cli=True) if save_path == checkpoint["cli_save_path"] else cli.send({"cmd": "load_save", "path": save_path})
                response = cli.execute_action(str(spec.get("cli_action") or spec["action_id"]), spec.get("args") or {})
                reward = self._teacher_heuristic(response)
                if reward is None:
                    reward = 0.0
                outcome: dict[str, Any] = {"probability": 1.0, "value": reward}
                child_public = root_from_cli_response(response, {}).get("state") if isinstance(response, dict) else {}
                child_specs = root_from_cli_response(response, {}).get("actions") if isinstance(response, dict) else []
                if remaining > 1 and isinstance(child_specs, list) and child_specs:
                    child_save = save_cli_state()
                    child_tree = expand(child_save, child_specs, remaining - 1, child_public if isinstance(child_public, dict) else {})
                    outcome = {"probability": 1.0, "next_node": {"state": child_tree.get("state") or {}, "immediate_reward": reward, "actions": child_tree.get("actions") or []}}
                root["actions"].append({"action_id": str(spec["action_id"]), "cli_action": spec.get("cli_action"), "args": spec.get("args") or {}, "outcomes": [outcome]})
            return root

        try:
            root = expand(str(checkpoint["cli_save_path"]), actions, depth, checkpoint.get("public_state") or {})
        finally:
            self.restore_checkpoint(session_id, checkpoint_id, load_cli=True)
        result: dict[str, Any] = {"parent_checkpoint_id": checkpoint_id, "depth": depth, "root": root}
        if payload.get("persist"):
            result.update(self.search_teacher(session_id, {"parent_checkpoint_id": checkpoint_id, "depth": depth, "root": root, "label_quality": "EstimatedByHeuristic"}))
        return result

    def build_provider_tree(self, session_id: str, payload: dict[str, Any]) -> dict[str, Any]:
        session = self.store.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        parent = str(payload.get("parent_checkpoint_id") or "")
        if not parent or not any(item.get("checkpoint_id") == parent for item in session.get("checkpoints", [])):
            raise ValueError("parent_checkpoint_id must reference an existing checkpoint")
        provider_path = payload.get("provider_path")
        if not provider_path:
            raise ValueError("provider_path is required")
        root = payload.get("root")
        if not isinstance(root, dict):
            raise ValueError("root is required")
        depth = int(payload.get("depth", 1))
        if depth < 1 or depth > 8:
            raise ValueError("depth must be between 1 and 8")
        with JsonlProvider(str(provider_path)) as provider:
            tree = build_tree(root, provider, depth)
        result = {"parent_checkpoint_id": parent, "depth": depth, "root": tree, "provider_path": str(provider_path)}
        if payload.get("persist"):
            result.update(self.search_teacher(session_id, {"parent_checkpoint_id": parent, "depth": depth, "root": tree, "label_quality": "CounterfactualTeacher"}))
        return result

    def close(self) -> None:
        with self.lock:
            sessions = list(self.cli.values())
            self.cli.clear()
        for cli in sessions:
            cli.close()


class Handler(BaseHTTPRequestHandler):
    server_version = "STS2SeedGui/0.1"

    @property
    def application(self) -> Application:
        return self.server.application  # type: ignore[attr-defined]

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            if path == "/api/health":
                json_response(self, {"ok": True, "service": "sts2-seed-gui", "default_cli": str(default_cli_path(PROJECT_ROOT))})
                return
            if path == "/api/sessions":
                json_response(self, {"sessions": self.application.store.list_sessions()})
                return
            session_id = path_id(path, "sessions")
            if session_id:
                session = self.application.store.get_session(session_id)
                if session is None:
                    json_response(self, {"error": "session not found"}, 404)
                else:
                    json_response(self, session)
                return
            if path in {"/", "/index.html"}:
                body = (ROOT / "static" / "index.html").read_bytes()
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            if path.startswith("/static/"):
                file_path = (ROOT / path.removeprefix("/static/")).resolve()
                static_root = (ROOT / "static").resolve()
                if static_root not in file_path.parents or not file_path.is_file():
                    json_response(self, {"error": "not found"}, 404)
                else:
                    body = file_path.read_bytes()
                    content_type = "text/css" if file_path.suffix == ".css" else "application/javascript"
                    self.send_response(200)
                    self.send_header("Content-Type", f"{content_type}; charset=utf-8")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                return
            json_response(self, {"error": "not found"}, 404)
        except Exception as exc:
            json_response(self, {"error": f"{type(exc).__name__}: {exc}"}, 400)

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            payload = read_json(self)
            if path == "/api/sessions":
                json_response(self, self.application.create_session(payload), 201)
                return
            parts = [part for part in path.split("/") if part]
            if len(parts) < 3 or parts[0:2] != ["api", "sessions"]:
                json_response(self, {"error": "not found"}, 404)
                return
            session_id = parts[2]
            if len(parts) == 4 and parts[3] == "refresh-map":
                json_response(self, self.application.refresh_map(session_id))
                return
            if len(parts) == 4 and parts[3] == "decisions":
                json_response(self, self.application.add_decision(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "checkpoints":
                json_response(self, self.application.create_checkpoint(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "restore-checkpoint":
                if not payload.get("checkpoint_id"):
                    raise ValueError("checkpoint_id is required")
                json_response(self, self.application.restore_checkpoint(session_id, str(payload["checkpoint_id"]), bool(payload.get("load_cli", False))))
                return
            if len(parts) == 4 and parts[3] == "branches":
                json_response(self, self.application.create_branch(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "import-run":
                if not payload.get("path"):
                    raise ValueError("path is required")
                json_response(self, self.application.import_run(session_id, str(payload["path"])))
                return
            if len(parts) == 4 and parts[3] == "export":
                json_response(self, self.application.export(session_id, payload.get("output")))
                return
            if len(parts) == 4 and parts[3] == "validate":
                json_response(self, self.application.validate(session_id))
                return
            if len(parts) == 4 and parts[3] == "diff":
                json_response(self.application.diff(session_id, payload.get("record_id")))
                return
            if len(parts) == 4 and parts[3] == "teacher-records":
                json_response(self.application.add_teacher_record(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "teacher-evaluate":
                json_response(self.application.evaluate_teacher(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "teacher-search":
                json_response(self.application.search_teacher(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "teacher-cli-tree":
                json_response(self.application.build_cli_tree(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "teacher-provider-tree":
                json_response(self.application.build_provider_tree(session_id, payload), 201)
                return
            if len(parts) == 4 and parts[3] == "teacher-export":
                json_response(self.application.export_teacher(session_id, payload.get("output")))
                return
            if len(parts) == 4 and parts[3] == "export-reliable":
                json_response(self.application.export_reliable(session_id, payload.get("output")))
                return
            json_response(self, {"error": "not found"}, 404)
        except KeyError as exc:
            json_response(self, {"error": f"session not found: {exc}"}, 404)
        except Exception as exc:
            json_response(self, {"error": f"{type(exc).__name__}: {exc}"}, 400)

    def log_message(self, *_: Any) -> None:
        return


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--data-dir", type=Path, default=DEFAULT_DATA_DIR)
    args = parser.parse_args()
    application = Application(args.data_dir)
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.application = application  # type: ignore[attr-defined]
    print(f"STS2 Seed GUI listening on http://{args.host}:{args.port}/", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        application.close()
        server.server_close()


if __name__ == "__main__":
    main()
