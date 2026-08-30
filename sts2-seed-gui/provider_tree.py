"""JSON-lines adapter for shadow/CLI transition providers.

Provider protocol:
  request:  {"cmd":"enumerate_outcomes", "state":{}, "action":{}, "depth":N}
  response: {"type":"outcomes", "outcomes":[{"probability":..., "next_node":...}]}

The adapter validates explicit probabilities and never invents random branches.
"""

from __future__ import annotations

import json
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

from teacher_search import SearchError


class ProviderError(RuntimeError):
    pass


class JsonlProvider:
    def __init__(self, executable: str | Path, timeout: float = 30.0):
        path = Path(executable)
        command = [sys.executable, str(path)] if path.suffix.lower() == ".py" else [str(path)]
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", bufsize=1, cwd=str(path.parent))
        self.timeout = timeout
        self._responses: queue.Queue[str] = queue.Queue()
        self._reader = threading.Thread(target=self._read_stdout, daemon=True)
        self._reader.start()

    def _read_stdout(self) -> None:
        stream = self.process.stdout
        if stream is None:
            return
        for line in iter(stream.readline, ""):
            self._responses.put(line.rstrip("\r\n"))

    def enumerate(self, state: dict[str, Any], action: dict[str, Any], depth: int) -> list[dict[str, Any]]:
        if self.process.stdin is None or self.process.stdout is None:
            raise ProviderError("provider streams are unavailable")
        self.process.stdin.write(json.dumps({"cmd": "enumerate_outcomes", "state": state, "action": action, "depth": depth}, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        try:
            line = self._responses.get(timeout=self.timeout)
        except queue.Empty as exc:
            raise ProviderError(f"provider response timed out after {self.timeout}s") from exc
        if not line:
            error = self.process.stderr.read() if self.process.stderr else ""
            raise ProviderError(f"provider exited without response: {error[-500:]}")
        response = json.loads(line)
        if response.get("type") == "error":
            raise ProviderError(str(response.get("message") or "provider error"))
        outcomes = response.get("outcomes")
        if not isinstance(outcomes, list) or not outcomes:
            raise ProviderError("provider response requires non-empty outcomes")
        probabilities = [item.get("probability") for item in outcomes if isinstance(item, dict)]
        if len(probabilities) != len(outcomes) or any(not isinstance(value, (int, float)) or value < 0 for value in probabilities):
            raise ProviderError("provider outcomes require non-negative numeric probabilities")
        total = sum(float(value) for value in probabilities)
        if abs(total - 1.0) > 1e-6:
            raise ProviderError(f"provider probabilities must sum to 1 (got {total})")
        return outcomes

    def close(self) -> None:
        if self.process.stdin:
            try:
                self.process.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
                self.process.stdin.flush()
            except Exception:
                pass
        try:
            self.process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=2)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            try:
                if stream is not None:
                    stream.close()
            except Exception:
                pass

    def __enter__(self) -> "JsonlProvider":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()


def build_tree(root: dict[str, Any], provider: JsonlProvider, depth: int) -> dict[str, Any]:
    if depth < 1:
        raise SearchError("depth must be at least 1")
    actions = root.get("actions") or []
    if not isinstance(actions, list) or not actions:
        raise SearchError("root requires a non-empty actions list")
    result = {"state": root.get("state") or {}, "actions": []}
    for action in actions:
        if not isinstance(action, dict) or not action.get("action_id"):
            raise SearchError("each action requires action_id")
        outcomes = provider.enumerate(result["state"], action, depth)
        normalized: list[dict[str, Any]] = []
        for outcome in outcomes:
            item = dict(outcome)
            child = item.get("next_node")
            if depth > 1 and not isinstance(child, dict) and isinstance(item.get("next_state"), dict):
                child = {"state": item["next_state"], "actions": item.get("actions") or []}
            if depth > 1 and isinstance(child, dict) and child.get("actions"):
                item["next_node"] = build_tree(child, provider, depth - 1)
                item.pop("next_state", None)
                item.pop("actions", None)
            normalized.append(item)
        result["actions"].append({key: value for key, value in {"action_id": str(action["action_id"]), "cli_action": action.get("cli_action"), "args": action.get("args") or {}, "outcomes": normalized}.items() if value is not None})
    return result
