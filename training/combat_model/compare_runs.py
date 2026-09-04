"""Compare model-run manifests without changing promotion state."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("manifests", nargs="+", type=Path)
    args = parser.parse_args()
    rows: list[dict[str, Any]] = []
    for path in args.manifests:
        manifest = json.loads(path.read_text(encoding="utf-8"))
        rows.append({
            "model_id": manifest.get("model_id"),
            "model_stage": manifest.get("model_stage"),
            "source_manifest": str(path),
            "training_rows": manifest.get("training_rows"),
            "validation_rows": manifest.get("validation_rows"),
            "test_rows": manifest.get("test_rows"),
            "challenge_rows": manifest.get("challenge_rows"),
            "best_epoch": manifest.get("best_epoch"),
            "epochs_completed": manifest.get("epochs_completed"),
            "validation_top1": manifest.get("validation_top1"),
            "test_top1": manifest.get("test_top1"),
            "challenge_top1": manifest.get("challenge_top1"),
            "test_ndcg_at_3": manifest.get("test_ndcg_at_3"),
            "test_regret_fixed": manifest.get("test_regret_fixed"),
            "onnx_parity_status": manifest.get("onnx_decision_parity_verdict") or
                                  ("legacy_numeric_only" if manifest.get("onnx_sha256") else "not_exported"),
            "promotion_status": manifest.get("promotion_status", "candidate_or_baseline"),
        })
    report = {"schema_version": 1, "runs": rows, "comparison_policy": "metrics are run-local; challenge values are not cross-comparable when split sources differ"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"verdict": "pass", "runs": len(rows), "output": str(args.output)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
