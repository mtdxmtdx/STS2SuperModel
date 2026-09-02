"""Small JSON-lines client for the existing sts2-cli-v0111 process."""

from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

from models import RunContext


class CliError(RuntimeError):
    pass


class CliSession:
    def __init__(self, executable: str | Path, *, timeout: float = 30.0, env: dict[str, str] | None = None):
        self.executable = str(executable)
        self.timeout = timeout
        self.env = env or {}
        self.process: subprocess.Popen[str] | None = None
        self._stdout: queue.Queue[str] = queue.Queue()
        self._stderr: queue.Queue[str] = queue.Queue()
        self._reader_threads: list[threading.Thread] = []

    @property
    def running(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def start(self, context: RunContext) -> dict[str, Any]:
        if self.running:
            raise CliError("CLI session is already running")
        path = Path(self.executable)
        if not path.exists():
            raise CliError(f"CLI executable not found: {path}")
        env = os.environ.copy()
        env.update(self.env)
        command = [sys.executable, str(path)] if path.suffix.lower() == ".py" else [str(path)]
        self.process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            cwd=str(path.parent),
            env=env,
        )
        self._start_reader(self.process.stdout, self._stdout, "stdout")
        self._start_reader(self.process.stderr, self._stderr, "stderr")
        ready = self._next_json(timeout=self.timeout)
        if ready.get("type") != "ready":
            self.close()
            raise CliError(f"CLI did not become ready: {ready}")
        if not ready.get("compatible", False):
            self.close()
            raise CliError(str(ready.get("compatibility_error") or "CLI compatibility check failed"))
        return self.send(
            {
                "cmd": "start_run",
                "character": context.character,
                "ascension": context.ascension,
                "seed": context.run_seed,
                "lang": "en",
            }
        )

    def send(self, command: dict[str, Any]) -> dict[str, Any]:
        if self.process is None or self.process.stdin is None or not self.running:
            raise CliError("CLI session is not running")
        self.process.stdin.write(json.dumps(command, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        return self._next_json(timeout=self.timeout)

    def get_map(self) -> dict[str, Any]:
        return self.send({"cmd": "get_map"})

    def get_combat_snapshot(self, view: str = "public") -> dict[str, Any]:
        return self.send({"cmd": "get_combat_snapshot", "view": view})

    def execute_action(self, action: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
        payload: dict[str, Any] = {"cmd": "action", "action": action}
        if args:
            payload["args"] = args
        return self.send(payload)

    def _start_reader(self, stream: Any, target: queue.Queue[str], name: str) -> None:
        def read() -> None:
            try:
                for line in iter(stream.readline, ""):
                    target.put(line.rstrip("\r\n"))
            finally:
                try:
                    stream.close()
                except Exception:
                    pass

        thread = threading.Thread(target=read, name=f"sts2-seed-gui-{name}", daemon=True)
        thread.start()
        self._reader_threads.append(thread)

    def _next_json(self, *, timeout: float) -> dict[str, Any]:
        deadline = threading.Event()
        # queue.Queue timeout is sufficient here; the event simply keeps the
        # local variable explicit for readability in tracebacks.
        del deadline
        while True:
            try:
                line = self._stdout.get(timeout=timeout)
            except queue.Empty as exc:
                diagnostics = []
                while True:
                    try:
                        diagnostics.append(self._stderr.get_nowait())
                    except queue.Empty:
                        break
                suffix = f" stderr={diagnostics[-3:]}" if diagnostics else ""
                raise CliError(f"Timed out waiting for CLI JSON response.{suffix}") from exc
            try:
                value = json.loads(line)
            except json.JSONDecodeError:
                # Some runtime libraries can print diagnostics to stdout. The
                # protocol response is still the next JSON line.
                continue
            if not isinstance(value, dict):
                continue
            return value

    def close(self) -> None:
        process = self.process
        self.process = None
        if process is None:
            return
        try:
            if process.stdin is not None:
                process.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
                process.stdin.flush()
        except Exception:
            pass
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)
        for stream in (process.stdin, process.stdout, process.stderr):
            try:
                if stream is not None:
                    stream.close()
            except Exception:
                pass

    def __enter__(self) -> "CliSession":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()


def default_cli_path(project_root: Path | None = None) -> Path:
    root = project_root or Path(__file__).resolve().parents[1]
    roots = [root, root.parent / "STS2SuperModel", root.parent.parent / "STS2SuperModel"]
    candidates = [base / "sts2-cli-v0111" / "src" / "Sts2Headless" / "bin" / configuration / "net9.0" / executable for base in roots for configuration in ("Debug", "Release") for executable in ("Sts2Headless.exe", "Sts2Headless")]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]
