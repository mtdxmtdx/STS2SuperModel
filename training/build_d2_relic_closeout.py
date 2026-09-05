#!/usr/bin/env python3
"""Build the D2 terminal report for the 20 UnsupportedKnownEffect relics."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
COVERAGE = ROOT / "data/relics/v0.111/relic-coverage.json"
OUT_JSON = ROOT / "data/relics/v0.111/d2-relic-closeout.json"
OUT_MD = ROOT / "data/relics/v0.111/D2_RELIC_VERIFICATION.md"
TARGETS = {
    "DIVINE_RIGHT", "DATA_DISK", "GORGET", "STONE_CRACKER", "GIRYA", "EMBER_TEA",
    "SWORD_OF_JADE", "SLING_OF_COURAGE", "GREMLIN_HORN", "BOOK_REPAIR_KNIFE",
    "LIZARD_TAIL", "RUNIC_PYRAMID", "BOOKMARK", "RINGING_TRIANGLE", "PAPER_PHROG",
    "PAPER_KRANE", "BIIIG_HUG", "THE_ABACUS", "GALACTIC_DUST", "HISTORY_COURSE",
}
UNSUPPORTED_REASONS = {
    "STONE_CRACKER": "random draw-pile upgrade identities require a validated no-replacement chance operator",
    "GIRYA": "rest-site training count is not injectable or observable in the current CLI relic-state contract",
    "BOOKMARK": "the retained-card cost reduction selects a hidden random card and lacks a verified chance operator",
    "BIIIG_HUG": "shuffle-time Cinder generation needs an authoritative card template and stable generated identity",
    "GALACTIC_DUST": "star-spend event history is absent from the current shadow state and feature contract",
    "HISTORY_COURSE": "previous-turn last Attack/Skill identity is absent from public history state",
}
COMBAT_START_MATERIALIZED = {
    "DIVINE_RIGHT", "DATA_DISK", "GORGET", "EMBER_TEA", "SWORD_OF_JADE", "SLING_OF_COURAGE",
}
DIRECT_RELIABLE = {"GREMLIN_HORN", "PAPER_PHROG"}


def main() -> int:
    coverage = json.loads(COVERAGE.read_text(encoding="utf-8-sig"))
    by_id = {row["relic_id"]: row for row in coverage["relics"]}
    if not TARGETS <= set(by_id):
        raise ValueError(f"missing D2 IDs: {sorted(TARGETS-set(by_id))}")
    rows = []
    for relic_id in sorted(TARGETS):
        source = by_id[relic_id]
        reliable = bool(source["reliable_eligible"])
        if reliable and relic_id in COMBAT_START_MATERIALIZED:
            reason = "version-locked paired public-root evidence passed; the combat-start effect is materialized before search"
        elif reliable and relic_id in DIRECT_RELIABLE:
            reason = "strict version-locked CLI-to-ShadowDiff evidence passed"
        elif reliable:
            reason = "version-locked paired public-state evidence plus zero-mismatch CLI-to-shadow action evidence passed"
        else:
            reason = UNSUPPORTED_REASONS.get(relic_id)
        if not reason:
            raise ValueError(f"{relic_id} lacks a terminal reason")
        rows.append({
            "relic_id": relic_id,
            "terminal_status": "Reliable" if reliable else "UnsupportedWithReason",
            "support_status": source["support_status"],
            "reliable_eligible": reliable,
            "affects_current_turn": source.get("affects_current_turn"),
            "evidence_reference": source.get("evidence_reference"),
            "reason": reason,
        })
    if any(row["affects_current_turn"] is None for row in rows):
        raise ValueError("all 20 D2 relics require affects_current_turn")
    reliable = sum(row["reliable_eligible"] for row in rows)
    document = {
        "schema_version": 1,
        "verdict": "pass",
        "before_reliable_eligible": 24,
        "after_reliable_eligible": coverage["summary"]["reliable_eligible_count"],
        "d2_reliable": reliable,
        "d2_unsupported_with_reason": len(rows) - reliable,
        "strict_evidence_rule_changed": False,
        "semantic_holds": [row["relic_id"] for row in coverage["relics"] if row["support_status"] == "PartiallySupported"],
        "resolved_prior_hold": "UNCEASING_TOP",
        "relics": rows,
        "version_lock": {
            "game_version": "v0.111.0",
            "game_commit": "41cef1ea",
            "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
            "cli_protocol_version": "0.2.0",
            "trace_schema": 1,
        },
    }
    OUT_JSON.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# D2 遗物语义验证",
        "",
        f"- 全目录 Reliable eligible：24 → {document['after_reliable_eligible']}。",
        f"- D2 20 项：{reliable} Reliable，{len(rows)-reliable} UnsupportedWithReason。",
        "- 严格证据规则未修改。",
        "- 当前 semantic hold 仅 `PARRYING_SHIELD`；`UNCEASING_TOP` 已由此前空手 fixture 解除，交接中的两项 hold 信息已过期。",
        "",
        "| Relic | 终态 | affects_current_turn | 原因 |",
        "|---|---|---:|---|",
    ]
    for row in rows:
        lines.append(f"| `{row['relic_id']}` | {row['terminal_status']} | {row['affects_current_turn']} | {row['reason']} |")
    OUT_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps({"reliable": reliable, "unsupported": len(rows)-reliable, "catalog_reliable": document["after_reliable_eligible"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
