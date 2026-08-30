import json
import sys
from pathlib import Path

print(json.dumps({"type": "ready", "version": "0.2.0", "compatible": True}), flush=True)
for line in sys.stdin:
    command = json.loads(line)
    if command.get("cmd") == "quit":
        break
    if command.get("cmd") == "start_run":
        print(json.dumps({"type": "decision", "decision": "map_select", "rows": [], "post_state_hash": "A" * 64}), flush=True)
    elif command.get("cmd") == "get_map":
        print(json.dumps({"type": "map", "context": {"act": 1}, "rows": [[{"col": 0, "row": 1, "type": "Monster", "children": [], "current": True, "visited": False}]], "post_state_hash": "B" * 64}), flush=True)
    elif command.get("cmd") == "action":
        print(json.dumps({"type": "decision", "decision": "map_select", "post_state_hash": "C" * 64}), flush=True)
    elif command.get("cmd") == "write_continue_save":
        path = Path(command["path"])
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("fake-save", encoding="utf-8")
        print(json.dumps({"type": "saved", "path": str(path)}), flush=True)
    elif command.get("cmd") == "load_save":
        print(json.dumps({"type": "loaded", "post_state_hash": "A" * 64}), flush=True)
    else:
        print(json.dumps({"type": "error", "message": "unknown"}), flush=True)
