"""Batch materialization of global teacher labels from branch-tree JSONL."""

from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from teacher_search import search_payload


def generate(input_path: str | Path, output_path: str | Path) -> dict[str, Any]:
    source = Path(input_path)
    target = Path(output_path)
    records: list[dict[str, Any]] = []
    for line_no, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        item = json.loads(line)
        if not isinstance(item, dict):
            raise ValueError(f"{source}:{line_no}: input row must be an object")
        result = search_payload(item)
        records.append({
            "teacher_record_id": f"batch-teacher-{len(records):08d}",
            "session_id": item.get("session_id"),
            "run_context_hash": item.get("run_context_hash"),
            "episode_id": item.get("episode_id"),
            "branch_id": item.get("branch_id", "main"),
            "parent_checkpoint_id": item.get("parent_checkpoint_id"),
            "public_state": (item.get("root") or {}).get("state") or {},
            "legal_actions": (item.get("root") or {}).get("actions") or [],
            "action_values": result["action_values"],
            "teacher_best_actions": result["best_actions"],
            "teacher_value": result["value"],
            "root_state_hash": result["root_state_hash"],
            "nodes_evaluated": result["nodes_evaluated"],
            "source_type": "counterfactual_branch",
            "label_quality": item.get("label_quality", "CounterfactualTeacher"),
            "created_at_utc": datetime.now(timezone.utc).isoformat(),
        })
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("".join(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n" for row in records), encoding="utf-8")
    digest = hashlib.sha256(target.read_bytes()).hexdigest().upper()
    manifest = {
        "manifest_version": "global-teacher-batch-manifest-v1",
        "source": str(source),
        "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest().upper(),
        "path": str(target),
        "sha256": digest,
        "row_count": len(records),
        "label_quality_counts": {},
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
    }
    for row in records:
        quality = str(row["label_quality"])
        manifest["label_quality_counts"][quality] = manifest["label_quality_counts"].get(quality, 0) + 1
    manifest_path = target.with_name(target.stem + ".manifest.json")
    manifest["manifest_path"] = str(manifest_path)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(json.dumps(generate(args.input, args.output), ensure_ascii=False, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
