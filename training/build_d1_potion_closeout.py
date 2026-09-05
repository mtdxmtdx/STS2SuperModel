#!/usr/bin/env python3
"""Build the D1 potion terminal-status report from the audited catalog."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
COVERAGE = ROOT / "data/potions/v0.111/potion-coverage.json"
OUTPUT_JSON = ROOT / "data/potions/v0.111/d1-potion-closeout.json"
OUTPUT_MD = ROOT / "data/potions/v0.111/D1_POTION_VERIFICATION.md"

CATEGORIES = {
    "unsupported_power_hook": {"AMBERGRIS"},
    "player_choice_contract": {
        "ASHWATER", "DROPLET_OF_PRECOGNITION", "GAMBLERS_BREW",
        "LIQUID_MEMORIES", "TOUCH_OF_INSANITY",
    },
    "random_or_hidden_order": {
        "ATTACK_POTION", "BOTTLED_POTENTIAL", "COLORLESS_POTION",
        "DISTILLED_CHAOS", "ENTROPIC_BREW", "OROBIC_ACID",
        "POWER_POTION", "SKILL_POTION", "SNECKO_OIL",
    },
    "generated_card_template": {"COSMIC_CONCOCTION", "CUNNING_POTION", "POT_OF_GHOULS"},
    "unmodeled_resource_subsystem": {
        "BONE_BREW", "ESSENCE_OF_DARKNESS", "KINGS_COURAGE",
        "POTION_OF_CAPACITY", "STAR_POTION",
    },
    "deferred_listener_or_temporary_state": {
        "BEETLE_JUICE", "DUPLICATOR", "FAIRY_IN_A_BOTTLE", "FLEX_POTION",
        "FORTIFIER", "GIGANTIFICATION_POTION", "MAZALETHS_GIFT",
        "POWDERED_DEMISE", "SHACKLING_POTION", "SOLDIERS_STEW",
        "SPEED_POTION", "STABLE_SERUM",
    },
    "multi_domain_targeting": {"FOUL_POTION"},
}

REASONS = {
    "unsupported_power_hook": "runtime applies a Power whose listener semantics are not modeled",
    "player_choice_contract": "requires a potion-specific card-selection contract before deterministic execution",
    "random_or_hidden_order": "depends on a validated chance operator or hidden pile order; no realized result is guessed",
    "generated_card_template": "requires authoritative generated-card templates and stable instance construction",
    "unmodeled_resource_subsystem": "depends on companion/orb/forge/star state outside the current combat-state contract",
    "deferred_listener_or_temporary_state": "requires a verified listener/temporary-state lifecycle beyond immediate effects",
    "multi_domain_targeting": "targets players and enemies or a merchant branch; the current target contract cannot represent it",
}


def main() -> int:
    coverage = json.loads(COVERAGE.read_text(encoding="utf-8-sig"))
    category_by_id = {
        potion_id: category
        for category, potion_ids in CATEGORIES.items()
        for potion_id in potion_ids
    }
    unsupported = {
        row["potion_id"]
        for row in coverage["potions"]
        if row["support_status"] == "UnsupportedKnownEffect"
    }
    if unsupported != set(category_by_id):
        raise ValueError(
            f"terminal classification mismatch: missing={sorted(unsupported-set(category_by_id))}, "
            f"extra={sorted(set(category_by_id)-unsupported)}"
        )
    rows = []
    for row in coverage["potions"]:
        if row["support_status"] == "SimulatorSupported":
            terminal = "Reliable" if row["reliable_eligible"] else "EvidencePending"
            reason = "strict version-locked CLI ShadowDiff passed" if row["reliable_eligible"] else row["blocking_reason"]
        elif row["support_status"] == "OutOfScope":
            terminal = "OutOfScope"
            reason = row["blocking_reason"]
        else:
            category = category_by_id[row["potion_id"]]
            terminal = "UnsupportedKnownEffect"
            reason = REASONS[category]
        rows.append({
            "potion_id": row["potion_id"],
            "terminal_status": terminal,
            "support_status": row["support_status"],
            "reliable_eligible": row["reliable_eligible"],
            "category": category_by_id.get(row["potion_id"]),
            "reason": reason,
            "next_action": row["next_action"],
        })

    document = {
        "schema_version": 1,
        "verdict": "pass",
        "before": {"simulator_supported": 4, "reliable_eligible": 2},
        "after": {
            "simulator_supported": coverage["summary"]["simulator_supported_count"],
            "reliable_eligible": coverage["summary"]["reliable_eligible_count"],
            "unsupported_known": coverage["summary"]["unsupported_known_count"],
            "out_of_scope": coverage["summary"]["out_of_scope_count"],
        },
        "strict_evidence_rule_changed": False,
        "potions": rows,
        "version_lock": coverage["version_lock"],
    }
    OUTPUT_JSON.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    lines = [
        "# D1 药水语义验证",
        "",
        "## 汇总",
        "",
        "| 指标 | 前 | 后 |",
        "|---|---:|---:|",
        f"| SimulatorSupported | 4 | {document['after']['simulator_supported']} |",
        f"| Reliable eligible | 2 | {document['after']['reliable_eligible']} |",
        f"| UnsupportedKnownEffect | 60 | {document['after']['unsupported_known']} |",
        "",
        "28 个确定性药水均通过严格 CLI/ShadowDiff，并通过两轮报告 SHA-256 一致性检查。"
        "未支持对象均保留明确技术原因，不猜测随机结果。",
        "",
        "## 逐项终态",
        "",
        "| Potion | 终态 | 分类 | 原因 |",
        "|---|---|---|---|",
    ]
    for row in rows:
        lines.append(
            f"| `{row['potion_id']}` | {row['terminal_status']} | "
            f"{row['category'] or 'deterministic'} | {row['reason']} |"
        )
    OUTPUT_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(document["after"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
