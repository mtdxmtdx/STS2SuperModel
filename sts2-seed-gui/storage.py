"""SQLite-backed draft storage and JSONL export for annotation sessions."""

from __future__ import annotations

import json
import hashlib
import sqlite3
import uuid
from collections import Counter
from contextlib import contextmanager
from pathlib import Path
from typing import Any

from models import DecisionRecord, RunContext, utc_now


class AnnotationStore:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.path)
        connection.row_factory = sqlite3.Row
        return connection

    @contextmanager
    def _open(self):
        connection = self._connect()
        try:
            yield connection
            connection.commit()
        finally:
            connection.close()

    def _init_db(self) -> None:
        with self._open() as connection:
            connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY,
                    context_json TEXT NOT NULL,
                    session_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS decisions (
                    record_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    decision_index INTEGER NOT NULL,
                    record_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    FOREIGN KEY(session_id) REFERENCES sessions(session_id)
                );
                CREATE INDEX IF NOT EXISTS idx_decisions_session ON decisions(session_id, decision_index);
                """
            )

    def create_session(self, context: RunContext, source: dict[str, Any] | None = None) -> dict[str, Any]:
        session_id = f"session-{uuid.uuid4().hex[:12]}"
        now = utc_now()
        session = {
            "session_id": session_id,
            "context": context.to_dict(),
            "run_context_hash": context.run_context_hash,
            "source": source or {},
            "map": None,
            "public_state": {},
            "state_hash": None,
            "current_node": None,
            "decisions": [],
            "checkpoints": [],
            "run_history": None,
            "created_at_utc": now,
            "updated_at_utc": now,
        }
        with self._open() as connection:
            connection.execute(
                "INSERT INTO sessions VALUES (?, ?, ?, ?, ?)",
                (session_id, json.dumps(session["context"], ensure_ascii=False, sort_keys=True), json.dumps(session, ensure_ascii=False), now, now),
            )
        return session

    def get_session(self, session_id: str) -> dict[str, Any] | None:
        with self._open() as connection:
            row = connection.execute("SELECT session_json FROM sessions WHERE session_id = ?", (session_id,)).fetchone()
        return json.loads(row["session_json"]) if row else None

    def list_sessions(self) -> list[dict[str, Any]]:
        with self._open() as connection:
            rows = connection.execute("SELECT session_json FROM sessions ORDER BY updated_at_utc DESC").fetchall()
        return [json.loads(row["session_json"]) for row in rows]

    def save_session(self, session: dict[str, Any]) -> None:
        session["updated_at_utc"] = utc_now()
        with self._open() as connection:
            connection.execute(
                "UPDATE sessions SET context_json = ?, session_json = ?, updated_at_utc = ? WHERE session_id = ?",
                (
                    json.dumps(session["context"], ensure_ascii=False, sort_keys=True),
                    json.dumps(session, ensure_ascii=False),
                    session["updated_at_utc"],
                    session["session_id"],
                ),
            )

    def add_decision(self, record: DecisionRecord) -> dict[str, Any]:
        value = record.to_dict()
        with self._open() as connection:
            connection.execute(
                "INSERT INTO decisions VALUES (?, ?, ?, ?, ?)",
                (record.record_id, record.session_id, record.decision_index, json.dumps(value, ensure_ascii=False), record.created_at_utc),
            )
        session = self.get_session(record.session_id)
        if session is None:
            raise KeyError(record.session_id)
        session.setdefault("decisions", []).append(value)
        self.save_session(session)
        return value

    def export_jsonl(self, session_id: str, output: str | Path) -> dict[str, Any]:
        session = self.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        target = Path(output)
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("w", encoding="utf-8", newline="\n") as handle:
            for decision in session.get("decisions", []):
                handle.write(json.dumps(decision, ensure_ascii=False, sort_keys=True) + "\n")
        digest = hashlib.sha256(target.read_bytes()).hexdigest().upper()
        decisions = session.get("decisions", [])
        manifest = {
            "manifest_version": "global-behavior-manifest-v1",
            "schema_version": "global-decision-record-v1",
            "session_id": session_id,
            "run_context_hash": session.get("run_context_hash"),
            "context": session.get("context") or {},
            "source": session.get("source") or {},
            "jsonl_path": str(target),
            "jsonl_sha256": digest,
            "row_count": len(decisions),
            "decision_type_counts": dict(Counter(str(item.get("decision_type", "unknown")) for item in decisions)),
            "label_quality_counts": dict(Counter(str(item.get("label_quality", "unknown")) for item in decisions)),
            "sl_status_counts": dict(Counter(str(item.get("sl_status", "unknown")) for item in decisions)),
            "generated_at_utc": utc_now(),
        }
        manifest_path = target.with_name(target.stem + ".manifest.json")
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        return {"path": str(target), "manifest_path": str(manifest_path), "sha256": digest, "row_count": len(decisions)}

    def validate_session(self, session_id: str) -> dict[str, Any]:
        session = self.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        errors: list[dict[str, Any]] = []
        context = session.get("context") or {}
        if not context.get("run_seed"):
            errors.append({"code": "missing_seed", "message": "RunContext.run_seed is required"})
        if not session.get("run_context_hash"):
            errors.append({"code": "missing_context_hash", "message": "run_context_hash is required"})
        records = session.get("decisions", [])
        seen_ids: set[str] = set()
        def forbidden_public_keys(value: Any, prefix: str = "") -> list[str]:
            found: list[str] = []
            if isinstance(value, dict):
                for key, child in value.items():
                    name = str(key).lower()
                    path = f"{prefix}.{key}" if prefix else str(key)
                    if any(token in name for token in ("rng", "future", "teacher", "audit")):
                        found.append(path)
                    found.extend(forbidden_public_keys(child, path))
            elif isinstance(value, list):
                for index, child in enumerate(value):
                    found.extend(forbidden_public_keys(child, f"{prefix}[{index}]"))
            return found
        for index, record in enumerate(records):
            if record.get("schema_version") != "global-decision-record-v1":
                errors.append({"code": "schema_version_mismatch", "index": index, "value": record.get("schema_version")})
            if not record.get("run_seed"):
                errors.append({"code": "missing_record_seed", "index": index})
            record_id = str(record.get("record_id") or "")
            if not record_id or record_id in seen_ids:
                errors.append({"code": "duplicate_or_missing_record_id", "index": index})
            seen_ids.add(record_id)
            selected = record.get("selected_action") or {}
            selected_id = selected.get("action_id") if isinstance(selected, dict) else None
            if not selected_id:
                errors.append({"code": "missing_selected_action", "index": index})
            legal = record.get("legal_actions") or []
            legal_ids = {item.get("action_id") for item in legal if isinstance(item, dict)}
            if legal_ids and selected_id not in legal_ids:
                errors.append({"code": "selected_action_not_legal", "index": index, "action_id": selected_id})
            leaked = forbidden_public_keys(record.get("public_state_before") or {})
            leaked.extend(forbidden_public_keys(record.get("public_state_after") or {}))
            if leaked:
                errors.append({"code": "public_view_leakage", "index": index, "paths": leaked})
        return {
            "ok": not errors,
            "session_id": session_id,
            "error_count": len(errors),
            "errors": errors,
            "row_count": len(records),
            "reliable_row_count": sum(1 for item in records if item.get("label_quality") == "ExactPublic" and item.get("sl_status") == "verified_no_sl"),
            "unknown_sl_row_count": sum(1 for item in records if item.get("sl_status") == "unknown"),
        }

    def export_teacher_jsonl(self, session_id: str, output: str | Path) -> dict[str, Any]:
        session = self.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        target = Path(output)
        target.parent.mkdir(parents=True, exist_ok=True)
        rows = session.get("teacher_records", [])
        with target.open("w", encoding="utf-8", newline="\n") as handle:
            for row in rows:
                handle.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
        digest = hashlib.sha256(target.read_bytes()).hexdigest().upper()
        manifest = {
            "manifest_version": "global-teacher-manifest-v1",
            "schema_version": "global-teacher-record-v1",
            "session_id": session_id,
            "run_context_hash": session.get("run_context_hash"),
            "jsonl_path": str(target),
            "jsonl_sha256": digest,
            "row_count": len(rows),
            "source_type": "counterfactual_branch",
            "label_quality_counts": dict(Counter(str(row.get("label_quality", "unknown")) for row in rows)),
            "generated_at_utc": utc_now(),
        }
        manifest_path = target.with_name(target.stem + ".manifest.json")
        manifest["manifest_path"] = str(manifest_path)
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        return {"path": str(target), "manifest_path": str(manifest_path), "sha256": digest, "row_count": len(rows)}

    def export_reliable_jsonl(self, session_id: str, output: str | Path) -> dict[str, Any]:
        session = self.get_session(session_id)
        if session is None:
            raise KeyError(session_id)
        all_rows = session.get("decisions", [])
        rows = [row for row in all_rows if row.get("label_quality") == "ExactPublic" and row.get("sl_status") == "verified_no_sl"]
        target = Path(output)
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("w", encoding="utf-8", newline="\n") as handle:
            for row in rows:
                handle.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
        digest = hashlib.sha256(target.read_bytes()).hexdigest().upper()
        manifest = {
            "manifest_version": "global-reliable-behavior-manifest-v1",
            "schema_version": "global-decision-record-v1",
            "session_id": session_id,
            "run_context_hash": session.get("run_context_hash"),
            "jsonl_path": str(target),
            "jsonl_sha256": digest,
            "row_count": len(rows),
            "source_row_count": len(all_rows),
            "excluded_row_count": len(all_rows) - len(rows),
            "selection": {"label_quality": "ExactPublic", "sl_status": "verified_no_sl"},
            "generated_at_utc": utc_now(),
        }
        manifest_path = target.with_name(target.stem + ".manifest.json")
        manifest["manifest_path"] = str(manifest_path)
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        return {"path": str(target), "manifest_path": str(manifest_path), "sha256": digest, "row_count": len(rows), "excluded_row_count": len(all_rows) - len(rows)}
