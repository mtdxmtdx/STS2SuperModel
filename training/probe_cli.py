#!/usr/bin/env python3
"""Run a CLI command fixture and print the last combat snapshot summary."""
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
    snapshots = []
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
            snapshots.append(data)
    proc.stdin.close()
    proc.wait(timeout=30)
    for snap in snapshots:
        o = snap["public_observation"]
        print("round:", o.get("round"), "energy:", o.get("energy"),
              "hp:", o["player"]["hp"], "block:", o["player"]["block"])
        pp = o.get("player_powers")
        print("  player_powers:", [(p["id"], p["amount"]) for p in (pp or [])])
        for e in o.get("enemies", []):
            pw = [(p["id"], p["amount"]) for p in (e.get("powers") or [])]
            intents = [(i.get("type"), i.get("total_damage") or i.get("damage"))
                       for i in e.get("intents", [])]
            print("  enemy:", e["instance_id"], "hp", e["hp"], "intents", intents, "powers", pw)
        print("  hand:", [c["instance_id"] for c in o.get("hand", [])])
        print("  draw_count:", o.get("draw_pile_count"), "discard:", o.get("discard_pile_count"))
        print("---")


if __name__ == "__main__":
    main()
