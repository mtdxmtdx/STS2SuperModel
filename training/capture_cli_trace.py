#!/usr/bin/env python3
"""Capture a reproducible v0.111 CLI trace for engine/shadow comparison."""

from __future__ import annotations

import argparse
import json
import os
import queue
import subprocess
import sys
import threading
from pathlib import Path


LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "version": "0.2.0",
}


class JsonLineReader:
    def __init__(self, stream):
        self._queue: queue.Queue[str | None] = queue.Queue()
        self._thread = threading.Thread(target=self._read, args=(stream,), daemon=True)
        self._thread.start()

    def _read(self, stream) -> None:
        for line in stream:
            self._queue.put(line)
        self._queue.put(None)

    def next(self, timeout_seconds: float) -> dict:
        while True:
            try:
                line = self._queue.get(timeout=timeout_seconds)
            except queue.Empty as exc:
                raise TimeoutError(f"CLI returned no JSON within {timeout_seconds:g}s") from exc
            if line is None:
                raise RuntimeError("CLI exited before returning JSON")
            line = line.strip()
            if line.startswith("{"):
                return json.loads(line)


def capture(
    executable: Path,
    commands: list[dict],
    output: Path,
    library: Path | None = None,
    response_timeout_seconds: float = 30,
) -> int:
    env = os.environ.copy()
    if library:
        env["STS2_LIB"] = str(library)
    env["STS2_TRACE_PATH"] = str(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        output.unlink()
    process = subprocess.Popen(
        [str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, bufsize=1, env=env,
    )
    reader = JsonLineReader(process.stdout)
    try:
        ready = reader.next(response_timeout_seconds)
        for key, expected in LOCK.items():
            if ready.get(key) != expected:
                raise RuntimeError(f"version gate failed: {key}={ready.get(key)!r}, expected {expected!r}")
        if ready.get("compatible") is not True:
            raise RuntimeError(f"CLI incompatible: {ready.get('compatibility_error')}")
        for command in commands:
            process.stdin.write(json.dumps(command, ensure_ascii=False) + "\n")
            process.stdin.flush()
            response = reader.next(response_timeout_seconds)
            if response.get("trace_status") == "failed":
                raise RuntimeError(f"command failed: {response}")
        process.stdin.write('{"cmd":"quit"}\n')
        process.stdin.flush()
        reader.next(response_timeout_seconds)
        process.wait(timeout=10)
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
    if not output.exists():
        raise RuntimeError("CLI did not write trace output")
    return sum(1 for line in output.open(encoding="utf-8") if line.strip())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True, type=Path)
    parser.add_argument("--library", type=Path)
    parser.add_argument("--commands", required=True, type=Path, help="JSONL command file")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--response-timeout", type=float, default=30)
    args = parser.parse_args()
    with args.commands.open(encoding="utf-8") as handle:
        commands = [json.loads(line) for line in handle if line.strip()]
    print(capture(args.executable, commands, args.output, args.library, args.response_timeout))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
