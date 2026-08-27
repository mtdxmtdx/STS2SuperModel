#!/usr/bin/env python3
"""Probe card stats structure for a given deck fixture."""
import json
import subprocess
import sys

CLI = r"D:\STS2BestChoice\STS2SuperModel\sts2-cli-v0111\src\Sts2Headless\bin\Debug\net9.0\Sts2Headless.exe"


def main() -> None:
    fixture = sys.argv[1]
    with open(fixture, encoding="utf-8") as f:
        cmds = [line.strip() for line in f if line.strip()]
    proc = subprocess.Popen(
        [CLI], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1,
    )
    for cmd in cmds:
        proc.stdin.write(cmd + "\n")
        proc.stdin.flush()
        line = proc.stdout.readline()
        while line and not line.startswith("{"):
            line = proc.stdout.readline()
        if not line:
            break
        try:
            data = json.loads(line)
        except json.JSONDecodeError:
            continue
        if data.get("type") == "combat_snapshot":
            o = data["public_observation"]
            print("round:", o.get("round"), "energy:", o.get("energy"),
                  "hp:", o["player"]["hp"], "block:", o["player"]["block"],
                  "draw:", o.get("draw_pile_count"), "discard:", o.get("discard_pile_count"),
                  "exhaust:", o.get("exhaust_pile_count"))
            for c in o.get("hand", []):
                print(json.dumps({k: c.get(k) for k in ("index", "instance_id", "id", "name", "cost", "type", "stats", "target_type", "can_play")}, ensure_ascii=False))
            pp = o.get("player_powers")
            print("player_powers:", [(p["id"], p["amount"]) for p in (pp or [])])
            for e in o.get("enemies", []):
                print("enemy:", e["instance_id"], "hp", e["hp"], "intents", e.get("intents"), "powers", [(p["id"], p["amount"]) for p in (e.get("powers") or [])])
    proc.stdin.close()
    proc.wait(timeout=30)


if __name__ == "__main__":
    main()
