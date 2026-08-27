#!/usr/bin/env python3
"""Print teacher snapshot details from a fixture run."""
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
        if data.get("type") == "combat_snapshot" and "teacher_snapshot" in data:
            ts = data["teacher_snapshot"]
            po = data["public_observation"]
            print("== teacher snapshot ==")
            print("hand:", [c["instance_id"] for c in ts.get("hand", [])])
            print("draw_pile:", [c["instance_id"] for c in ts.get("draw_pile", [])])
            print("discard_pile:", [c["instance_id"] for c in ts.get("discard_pile", [])])
            print("relics:", [(r["id"], r.get("counter")) for r in ts.get("relics", [])])
            print("rng:", ts.get("rng_counters"))
            print("public hand:", [c["instance_id"] for c in po.get("hand", [])])
            print("public draw_count:", po.get("draw_pile_count"))
        elif data.get("decision") == "combat_play" and "public_observation" not in data:
            pass
        elif "public_observation" in data:
            po = data["public_observation"]
            print("== combat observation (decision:", data.get("decision"), ") ==")
            print("hand:", [c["instance_id"] for c in po.get("hand", [])])
        elif data.get("decision") == "combat_play":
            print("== combat row ==")
            print("hand:", [c["instance_id"] for c in data.get("hand", [])])
    proc.stdin.close()
    proc.wait(timeout=30)


if __name__ == "__main__":
    main()
