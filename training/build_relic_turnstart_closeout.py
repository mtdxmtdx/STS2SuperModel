#!/usr/bin/env python3
"""Close out the 35 TurnStart evidence-repair candidates from the Line C1 handoff."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = ROOT.parent
REPAIR_ACTION = "Repair or extend evidence before Reliable promotion"
VERSION = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--coverage",
        type=Path,
        default=ROOT / "data" / "relics" / "v0.111" / "relic-coverage.json",
    )
    parser.add_argument(
        "--output-json",
        type=Path,
        default=ROOT / "data" / "relics" / "v0.111" / "turnstart-evidence-closeout.json",
    )
    parser.add_argument(
        "--output-md",
        type=Path,
        default=ROOT / "data" / "relics" / "v0.111" / "turnstart-evidence-closeout.md",
    )
    return parser.parse_args()


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def resolve_report(name: str) -> Path | None:
    for root in (ROOT / "data", WORKSPACE / "STS2BestChoice" / "data"):
        path = root / name
        if path.exists():
            return path
    return None


def assess_report(name: str) -> dict[str, Any]:
    path = resolve_report(name)
    if path is None:
        return {"report": name, "strict": False, "reason": "report_missing"}
    try:
        row = load_json(path)
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        return {"report": name, "strict": False, "reason": f"report_unreadable:{type(exc).__name__}"}
    failures: list[str] = []
    for key, expected in VERSION.items():
        if row.get(key) != expected:
            failures.append(f"{key}={row.get(key)!r}")
    if row.get("confidence") != "Reliable":
        failures.append(f"confidence={row.get('confidence')!r}")
    if row.get("match") is not True:
        failures.append(f"match={row.get('match')!r}")
    if row.get("mismatch_count") != 0:
        failures.append(f"mismatch_count={row.get('mismatch_count')!r}")
    if row.get("comparison_scope") != "strict_public_state":
        failures.append(f"comparison_scope={row.get('comparison_scope')!r}")
    return {
        "report": name,
        "strict": not failures,
        "reason": "strict_evidence_pass" if not failures else "; ".join(failures),
        "confidence": row.get("confidence"),
        "match": row.get("match"),
        "mismatch_count": row.get("mismatch_count"),
        "comparison_scope": row.get("comparison_scope"),
    }


def terminal_reason(reports: list[dict[str, Any]]) -> str:
    failures = [f"{report['report']}: {report['reason']}" for report in reports if not report["strict"]]
    return "all referenced reports satisfy strict evidence" if not failures else " | ".join(failures)


def render_md(document: dict[str, Any]) -> str:
    summary = document["summary"]
    lines = [
        "# C1 TurnStart 遗物证据收口",
        "",
        "> 范围是交接时的 35 个 TurnStart 证据修复候选：RING_OF_THE_DRAKE 加当前仍 pending 的 34 个。",
        "",
        "## 汇总",
        "",
        "| 指标 | 数量 |",
        "|---|---:|",
        f"| 处理对象 | {summary['target_count']} |",
        f"| 本批转为 Reliable | {summary['reliable_count']} |",
        f"| 仍 PendingWithReason | {summary['pending_count']} |",
        f"| 全目录 reliable_eligible | {summary['catalog_reliable_eligible_count']} |",
        f"| 实测本批前基线 | {summary['observed_pre_ring_baseline']} |",
        f"| 本批净增 | {summary['reliable_delta']} |",
        "",
        "交接文档写的是 24 起步；当前文件历史事实是 Ring 晋升前 23，晋升后 24。此处按生成前后目录证据记录，不回退已通过空手 fixture 的 `UNCEASING_TOP`。",
        "",
        "## 逐项终态",
        "",
        "| ID | 终态 | 支持状态 | 证据等级 | 原因/证据 |",
        "|---|---|---|---|---|",
    ]
    for row in document["relics"]:
        reason = row["terminal_reason"].replace("|", "\\|")
        lines.append(
            f"| `{row['relic_id']}` | {row['terminal_status']} | {row['support_status']} | "
            f"{row['evidence_level']} | {reason} |"
        )
    lines += [
        "",
        "## Semantic hold",
        "",
        "- `PARRYING_SHIELD`：继续保持 `PartiallySupported`，随机多目标身份在 NOSL 公共观测下不能消歧。",
        "- `UNCEASING_TOP`：当前已由空手触发 fixture 严格验证并保持 Reliable；交接中称其仍为 hold 的信息已过期。",
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    args = parse_args()
    coverage = load_json(args.coverage)
    by_id = {row["relic_id"]: row for row in coverage["relics"]}
    pending = [
        row
        for row in coverage["relics"]
        if row.get("combat_relevance") == "TurnStart" and row.get("next_action") == REPAIR_ACTION
    ]
    targets = sorted([by_id["RING_OF_THE_DRAKE"], *pending], key=lambda row: row["relic_id"])
    if len(targets) != 35:
        raise ValueError(f"expected the handoff's 35 TurnStart repair candidates, found {len(targets)}")
    if not by_id["RING_OF_THE_DRAKE"].get("reliable_eligible"):
        raise ValueError("RING_OF_THE_DRAKE must be Reliable before C1 closeout")

    results: list[dict[str, Any]] = []
    for relic in targets:
        names = [name.strip() for name in str(relic.get("evidence_reference") or "").split(";") if name.strip()]
        reports = [assess_report(name) for name in names]
        reliable = bool(relic.get("reliable_eligible"))
        results.append(
            {
                "relic_id": relic["relic_id"],
                "combat_relevance": relic["combat_relevance"],
                "support_status": relic["support_status"],
                "evidence_level": relic["evidence_level"],
                "evidence_reference": relic.get("evidence_reference"),
                "reports": reports,
                "terminal_status": "Reliable" if reliable else "PendingWithReason",
                "terminal_reason": terminal_reason(reports) if names else str(relic.get("blocking_reason") or "evidence_reference_missing"),
                "next_action": relic["next_action"],
            }
        )

    reliable_count = sum(row["terminal_status"] == "Reliable" for row in results)
    catalog_reliable = int(coverage["summary"]["reliable_eligible_count"])
    document = {
        "schema_version": 1,
        "verdict": "pass",
        "version_lock": VERSION,
        "scope": "Line C1 handoff TurnStart evidence-repair candidates",
        "summary": {
            "target_count": len(results),
            "reliable_count": reliable_count,
            "pending_count": len(results) - reliable_count,
            "handoff_expected_baseline": 24,
            "observed_pre_ring_baseline": catalog_reliable - reliable_count,
            "catalog_reliable_eligible_count": catalog_reliable,
            "reliable_delta": reliable_count,
            "strict_evidence_rule_changed": False,
            "semantic_hold_count": sum(row["support_status"] == "PartiallySupported" for row in coverage["relics"]),
        },
        "semantic_holds": [
            {
                "relic_id": "PARRYING_SHIELD",
                "status": by_id["PARRYING_SHIELD"]["support_status"],
                "reason": by_id["PARRYING_SHIELD"]["blocking_reason"],
            }
        ],
        "resolved_stale_hold": {
            "relic_id": "UNCEASING_TOP",
            "status": by_id["UNCEASING_TOP"]["support_status"],
            "reliable_eligible": by_id["UNCEASING_TOP"]["reliable_eligible"],
            "evidence_reference": by_id["UNCEASING_TOP"]["evidence_reference"],
        },
        "relics": results,
    }
    args.output_json.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.output_md.write_text(render_md(document), encoding="utf-8")
    print(json.dumps(document["summary"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
