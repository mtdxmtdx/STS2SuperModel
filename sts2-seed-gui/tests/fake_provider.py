import json
import sys

for line in sys.stdin:
    command = json.loads(line)
    if command.get("cmd") == "quit":
        break
    if command.get("cmd") == "enumerate_outcomes":
        action_id = (command.get("action") or {}).get("action_id", "unknown")
        value = 1.0 if action_id.endswith("good") else 0.0
        print(json.dumps({"type": "outcomes", "outcomes": [{"probability": 0.75, "value": value}, {"probability": 0.25, "value": value / 2}]}), flush=True)
    else:
        print(json.dumps({"type": "error", "message": "unknown command"}), flush=True)
