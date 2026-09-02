#!/usr/bin/env python3
"""Emit the relic/card gap baseline + inventory artifacts required by
RELIC_CARD_GAP_COMPLETION_PLAN.md sections 5.2 and 11.

Reads the freshly regenerated relic catalog/coverage, the card semantic
verification report, and the registered P0/P1 probe matrices, then writes:

  data/relic-card-gap-baseline.json
  data/relic-card-gap-inventory.json
"""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"
GAME_VERSION = "v0.111.0"
GAME_COMMIT = "41cef1ea"
ASSEMBLY = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"
CLI_PROTOCOL = "0.2.0"


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--baseline-suffix", default="",
        help="Suffix for the baseline file name. Batches after R0 must pass "
             "'-after' so the R0-locked relic-card-gap-baseline.json stays "
             "byte-identical; only new after-files are added.")
    args = parser.parse_args()

    catalog = load(DATA / "relics/v0.111/relic-catalog.json")
    coverage = load(DATA / "relics/v0.111/relic-coverage.json")
    cards = load(DATA / "card-semantic-verification.json")
    coverage_by_id = {
        item["relic_id"]: item for item in coverage.get("relics", [])
    }

    relic_status = Counter(r["support_status"] for r in catalog["relics"])
    relevance_unsupported = Counter(
        r["combat_relevance"] for r in catalog["relics"]
        if r["support_status"] == "unsupported_known_effect")
    relevance_unknown = Counter(
        r["combat_relevance"] for r in catalog["relics"]
        if r["support_status"] == "unknown")
    relevance_out_of_scope = Counter(
        r["combat_relevance"] for r in catalog["relics"]
        if r["support_status"] == "out_of_scope")

    repeat_quality = {}
    repeat_path = DATA / "p1-repeat-verification.json"
    if repeat_path.is_file():
        try:
            repeat_quality = load(repeat_path).get("quality_counts", {})
        except (OSError, json.JSONDecodeError):
            repeat_quality = {}
    quality_text = ", ".join(
        f"{key}={repeat_quality.get(key, 0)}"
        for key in ("Reliable", "Estimated", "Uncalculable", "Unknown")
    )
    baseline = {
        "schema_version": 1,
        "game_version": GAME_VERSION,
        "game_commit": GAME_COMMIT,
        "assembly_sha256": ASSEMBLY,
        "cli_protocol_version": CLI_PROTOCOL,
        "relics": {
            "total": len(catalog["relics"]),
            "support_status": dict(relic_status),
            "unsupported_by_combat_relevance": dict(relevance_unsupported),
            "unknown_by_combat_relevance": dict(relevance_unknown),
            "out_of_scope_by_combat_relevance": dict(relevance_out_of_scope),
            "reliable_eligible": coverage["summary"]["reliable_eligible_count"],
            "batch1_completed": [
                "SHURIKEN", "KUNAI", "ORNAMENTAL_FAN", "LETTER_OPENER",
                "RAINBOW_RING", "MERCURY_HOURGLASS", "MR_STRUGGLES", "SAI",
                "CANDELABRA", "CHANDELIER", "FAKE_HAPPY_FLOWER", "FAKE_ORICHALCUM",
            ],
            "batch1_evidence": (
                "Evidence quality is recorded per report; current registered matrix: "
                f"{quality_text}. Reliable promotion requires strict scope and exact chance metadata."
            ),
        },
        "cards": {
            "total_variants": cards["all"]["variants"],
            "fully_structured": cards["all"]["fully_structured"],
            "unstructured": cards["all"]["variants"] - cards["all"]["fully_structured"],
            "single_player_scope": cards["single_player_combat_scope"]["variants"],
            "single_player_fully_structured": cards["single_player_combat_scope"]["fully_structured"],
            "single_player_unparsed_clauses": cards["single_player_combat_scope"]["unparsed_clauses"],
            "immediately_executable": cards["all"]["immediately_executable"],
            "multiplayer_only_variants": cards["multiplayer_only_variants"],
            "attribution_note": (
                "All 68 unstructured variants fall outside the single-player "
                "combat scope (multiplayer/ally-only). The 77 multiplayer-only "
                "variants are OutOfScope for the single-player Reliable set by "
                "contract."),
            "high_risk_single_player_categories": cards["semantic_categories"],
        },
        "known_simulator_gaps": [
            "Enemy non-attack intents (Buff/Defend) carry no effect amounts in "
            "the public observation, so the shadow cannot replay those enemy "
            "turns (encountered on SEAPUNK_WEAK round 3->4).",
            "Shuffle-stream card ORDER after a draw-pile reshuffle is not "
            "reproducible without live RNG state; counters are mirrored and "
            "hand comparison falls back to count-only.",
        ],
    }
    # Registered ShadowDiff report matrix: derive counts from the probe
    # registries so new fixture families are picked up automatically.
    from verify_repeat_runs import expected_report_names

    registered = sorted(
        name for name in expected_report_names() if (DATA / name).is_file())
    repeat_path = DATA / "p1-repeat-verification.json"
    repeat_summary = "not-run"
    if repeat_path.is_file():
        repeat_payload = json.loads(repeat_path.read_text(encoding="utf-8"))
        repeat_summary = (
            f"verdict={repeat_payload.get('verdict')}, "
            f"report_count={repeat_payload.get('report_count')}, "
            f"different={len(repeat_payload.get('different_reports') or [])}, "
            f"missing={len(repeat_payload.get('missing_reports') or [])}, "
            f"added={len(repeat_payload.get('added_reports') or [])}, "
            f"unexpected={len(repeat_payload.get('unexpected_reports') or [])}")
    shadow_matrix = {
        "registered_report_count": len(registered),
        "p0_reports": sum(1 for n in registered if n.startswith("p0-")),
        "p1_power_reports": sum(1 for n in registered
                                if n.startswith("p1-csharp-") and "relic-" not in n
                                and "card-" not in n),
        "p1_relic_reports": sum(1 for n in registered if "relic-" in n),
        "p1_card_reports": sum(1 for n in registered if "card-" in n),
        "repeat_verification": repeat_summary,
    }
    baseline["shadow_diff_matrix"] = shadow_matrix
    baseline_path = DATA / f"relic-card-gap-baseline{args.baseline_suffix}.json"
    baseline_path.write_text(
        json.dumps(baseline, ensure_ascii=False, indent=2), encoding="utf-8")

    # Per-object inventory.
    inventory = []
    for r in catalog["relics"]:
        coverage_entry = coverage_by_id.get(r["relic_id"], {})
        handler_supported = r["support_status"] in {
            "simulator_supported", "partially_supported"
        }
        evidence_eligible = bool(coverage_entry.get("evidence_eligible", False))
        reliable_eligible = bool(coverage_entry.get("reliable_eligible", False))
        runtime_probed = bool(coverage_entry.get("runtime_probed", False))
        status = r["support_status"]
        blocking_reason = coverage_entry.get("blocking_reason")
        next_action = coverage_entry.get("next_action")
        if blocking_reason is None:
            if reliable_eligible:
                blocking_reason = None
            elif status == "out_of_scope":
                blocking_reason = (
                    "OutOfScope: non-combat relic; no in-combat hooks "
                    "(map/shop/reward/event or cross-combat effects only)"
                )
            elif handler_supported:
                blocking_reason = coverage_entry.get(
                    "evidence_source",
                    "Evidence pending: strict CLI/ShadowDiff eligibility not met",
                )
            else:
                blocking_reason = "In-combat hook not yet implemented in DeterministicSimulator"

        if next_action is None:
            if reliable_eligible or status == "out_of_scope":
                next_action = "None" if status == "out_of_scope" else "None"
            elif handler_supported:
                next_action = "Repair or extend evidence before Reliable promotion"
            elif status == "unknown":
                next_action = "Confirm OutOfScope attribution"
            else:
                next_action = "Implement handler + fixture in next batch"

        inventory.append({
            "object_type": "relic",
            "stable_id": r["relic_id"],
            "display_name": r["canonical_name"],
            "game_version": GAME_VERSION,
            "scope": r["combat_relevance"],
            "structured": True,
            "simulator_supported": handler_supported,
            "runtime_handler_resolvable": handler_supported,
            "runtime_probed": runtime_probed,
            "evidence_eligible": evidence_eligible,
            "reliable_eligible": reliable_eligible,
            "support_status": status,
            "evidence_level": r["evidence_level"],
            "evidence_reference": r["evidence_reference"] if runtime_probed else None,
            "blocking_reason": blocking_reason,
            "next_action": next_action,
        })

    single = cards["single_player_combat_scope"]
    inventory.append({
        "object_type": "card-set",
        "stable_id": "SINGLE_PLAYER_VARIANTS",
        "display_name": "Single-player combat card variants",
        "game_version": GAME_VERSION,
        "scope": "single_player",
        "structured": single["fully_structured"] == single["variants"],
        "simulator_supported": single["simulator_executable"] == single["variants"],
        "runtime_handler_resolvable": single["runtime_handler_resolvable"] == single["variants"],
        "runtime_probed": False,
        "support_status": "partially_supported",
        "evidence_level": "heuristic_inferred",
        "evidence_reference": "data/card-semantic-verification.json",
        "blocking_reason": "Behavior evidence (CLI ShadowDiff) exists only for semantic patterns covered by P0/P1 fixtures",
        "next_action": "Extend per-semantic-pattern fixtures per plan C2 layer 3",
    })
    inventory.append({
        "object_type": "card-set",
        "stable_id": "MULTIPLAYER_ONLY_VARIANTS",
        "display_name": "Multiplayer/ally-only card variants",
        "game_version": GAME_VERSION,
        "scope": "multiplayer",
        "structured": False,
        "simulator_supported": False,
        "runtime_handler_resolvable": False,
        "runtime_probed": False,
        "support_status": "out_of_scope",
        "evidence_level": "unknown",
        "evidence_reference": None,
        "blocking_reason": "Multiplayer/ally effects excluded from single-player combat scope by contract",
        "next_action": "None (OutOfScope)",
    })

    inv_doc = {
        "schema_version": 1,
        "game_version": GAME_VERSION,
        "game_commit": GAME_COMMIT,
        "assembly_sha256": ASSEMBLY,
        "cli_protocol_version": CLI_PROTOCOL,
        "objects": inventory,
    }
    (DATA / "relic-card-gap-inventory.json").write_text(
        json.dumps(inv_doc, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"baseline + inventory written: {len(inventory)} inventory objects")
    print(f"relics: supported={relic_status['simulator_supported']} "
          f"unsupported={relic_status['unsupported_known_effect']} "
          f"unknown={relic_status['unknown']} "
          f"out_of_scope={relic_status['out_of_scope']} "
          f"unverifiable={relic_status['unverifiable_by_cli']} "
          f"uncalculable={relic_status['uncalculable']} "
          f"registered_reports={shadow_matrix['registered_report_count']}")


if __name__ == "__main__":
    main()
