#!/usr/bin/env python3
"""Capture deterministic CLI traces with a teacher snapshot at each decision."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / "sts2-cli-v0111/src/Sts2Headless/bin/Debug/net9.0/Sts2Headless.exe"
if not CLI.is_file():
    CLI = Path(r"D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111\src\Sts2Headless\bin\Debug\net9.0\Sts2Headless.exe")
FIXTURE_DIR = ROOT / "training/fixtures"
DATA_DIR = ROOT / "data"


def send(proc: subprocess.Popen[str], command: dict) -> dict | None:
    assert proc.stdin is not None and proc.stdout is not None
    proc.stdin.write(json.dumps(command, ensure_ascii=False) + "\n")
    proc.stdin.flush()
    line = proc.stdout.readline()
    while line and not line.lstrip().startswith("{"):
        line = proc.stdout.readline()
    if not line:
        return None
    try:
        return json.loads(line)
    except json.JSONDecodeError:
        return None


def capture_fixture(fixture: Path, output: Path, seed_suffix: str = "") -> tuple[bool, str]:
    output.unlink(missing_ok=True)
    env = os.environ.copy()
    env["STS2_TRACE_PATH"] = str(output)
    proc = subprocess.Popen(
        [str(CLI)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1, env=env,
    )
    try:
        commands = [json.loads(line) for line in fixture.read_text(encoding="utf-8").splitlines() if line.strip()]
        if seed_suffix:
            for command in commands:
                if command.get("cmd") == "start_run" and "seed" in command:
                    command["seed"] = f"{command['seed']}{seed_suffix}"
        for command in commands:
            response = send(proc, command)
            if response is None:
                return False, "CLI ended before response"
            if response.get("type") in ("error", "save_error"):
                return False, str(response.get("message", response))
            # Public snapshots and action decision rows are both useful join
            # points; capture teacher state immediately around each one.
            if command.get("cmd") == "get_combat_snapshot" and command.get("view") == "public":
                teacher_response = send(proc, {"cmd": "get_combat_snapshot", "view": "teacher"})
                if teacher_response is None:
                    return False, "teacher snapshot response missing"
            elif command.get("cmd") == "action":
                teacher_response = send(proc, {"cmd": "get_combat_snapshot", "view": "teacher"})
                if teacher_response is None:
                    return False, "teacher snapshot response missing"
            if command.get("cmd") == "quit":
                break
        try:
            if proc.stdin is not None:
                proc.stdin.close()
        except OSError:
            pass
        proc.wait(timeout=20)
    except (OSError, subprocess.TimeoutExpired, json.JSONDecodeError) as exc:
        proc.kill()
        proc.wait(timeout=10)
        return False, str(exc)
    return output.is_file(), "ok" if output.is_file() else "trace missing"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit-fixtures", type=int, default=25)
    parser.add_argument("--prefix", default="teacher-matrix")
    parser.add_argument("--repeat", type=int, default=1)
    args = parser.parse_args()
    if args.repeat <= 0:
        raise SystemExit("--repeat must be positive")
    fixtures = sorted(FIXTURE_DIR.glob("p0-*-commands.jsonl"))[: max(1, args.limit_fixtures)]
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    successes = 0
    total = len(fixtures) * args.repeat
    for repeat in range(args.repeat):
        suffix = "" if repeat == 0 else f"-r{repeat}"
        for fixture in fixtures:
            name = fixture.name.removesuffix("-commands.jsonl")
            output = DATA_DIR / f"{args.prefix}-{name}{suffix}-trace.jsonl"
            ok, detail = capture_fixture(fixture, output, suffix)
            print(("PASS" if ok else "FAIL"), name, suffix, detail)
            successes += int(ok)
    print(f"{successes}/{total} fixtures captured")
    return 0 if successes == total else 1


if __name__ == "__main__":
    raise SystemExit(main())
