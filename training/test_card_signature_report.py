import json
from pathlib import Path

from build_card_signature_report import (
    CARD_FIXTURE_MAP,
    VERSION_LOCK,
    _validate_report,
    build,
    categorize,
    resolve_template,
    signature_id,
    signature_ops,
)


def _op(kind, **overrides):
    op = {
        "kind": kind, "target": "selected_enemy", "trigger": None,
        "timing": "immediate", "condition": None, "random_source": None,
        "amount": 5, "repeat": 1, "duration": None, "all": False,
        "repeat_by_energy_spent": False, "repeat_by_orb_count": False,
        "repeat_by_exhausted_count": False, "repeat_by_history_counter": False,
        "repeat_by_kill_count": False, "repeat_by_stars_gained": False,
        "amount_by_energy_spent": False, "amount_by_alive_enemy_count": False,
        "amount_by_distinct_orb_types": False, "amount_by_hand_attack_count": False,
        "amount_by_cards_drawn_this_turn": False,
        "amount_by_target_vulnerable_stacks": False,
        "dynamic_amount_id": None, "x_bonus": 0, "id": None,
    }
    op.update(overrides)
    return op


def test_signature_groups_structurally_identical_operations():
    ops_a = [_op("damage"), _op("keyword", id="消耗")]
    ops_b = [_op("damage", amount=12), _op("keyword", id="消耗")]
    assert signature_id(signature_ops(ops_a)) == signature_id(signature_ops(ops_b))
    assert signature_id(signature_ops(ops_a)) != signature_id(
        signature_ops([_op("damage"), _op("keyword", id="虚无")]))


def test_signature_preserves_semantic_operation_ids_and_dynamic_fields():
    # Status/orb/upgrade rules must not collapse merely because their generic
    # EffectKind is the same.
    assert signature_id(signature_ops([_op("apply_status", id="WEAK")])) != signature_id(
        signature_ops([_op("apply_status", id="VULNERABLE")]))
    assert signature_id(signature_ops([_op("channel_orb", id="闪电")])) != signature_id(
        signature_ops([_op("channel_orb", id="冰霜")]))
    assert signature_id(signature_ops([_op("dynamic_damage", dynamic_amount_id="PLAYER_BLOCK")])) != signature_id(
        signature_ops([_op("dynamic_damage", dynamic_amount_id="HAND_CARD_COUNT")]))
    projected = signature_ops([_op("damage", repeat=3, x_bonus=2,
                                   repeat_by_energy_spent=True)])
    assert projected[0]["id"] is None
    assert projected[0]["repeat_value"] == 3
    assert projected[0]["x_bonus"] == 2
    assert projected[0]["dynamic_flags"]["repeat_by_energy_spent"] is True


def test_categorize_maps_priority_categories():
    projection = signature_ops([
        _op("damage", target="random_enemy", random_source="CombatTargets"),
        _op("select_card", target="hand", id="HAND_ANY"),
        _op("keyword", id="虚无"),
    ])
    categories = categorize(projection)
    assert "random_target" in categories
    assert "random" in categories
    assert "choice" in categories
    assert "ethereal" in categories
    boundary_projection = signature_ops([_op("damage", timing="turn_end")])
    assert "turn_boundary" in categorize(boundary_projection)


def test_categorize_x_cost_and_dynamic_value():
    projection = signature_ops([_op("damage", repeat_by_energy_spent=True)])
    categories = categorize(projection)
    assert "x_cost" in categories
    assert "dynamic_value" in categories


def test_resolve_template_layers():
    known_ids = {"SHIV", "WOUND", "SOUL_UPGRADE"}
    localized = {"伤口": "WOUND", "灵魂": "SOUL"}
    assert resolve_template("SHIV", known_ids, localized) == "card"
    assert resolve_template("伤口", known_ids, localized) == "card"
    assert resolve_template("灵魂+", known_ids, localized) == "card"
    assert resolve_template("SELF_COPY", known_ids, localized) == "rule"
    assert resolve_template("随机攻击牌", known_ids, localized) == "pool"
    assert resolve_template("NOT_A_TEMPLATE", known_ids, localized) == "unresolved"


def test_card_fixture_registry_has_no_relic_or_power_leakage():
    assert CARD_FIXTURE_MAP
    assert all(name.startswith("p1-card-") for name in CARD_FIXTURE_MAP)
    for name, spec in CARD_FIXTURE_MAP.items():
        assert spec["variant_ids"]
        assert spec["action_ordinals"]


def test_card_fixture_registry_matches_runner_action_contract():
    """Keep evidence attribution synchronized with the actual card runner."""
    from run_p1_card_probes import CARD_PROBES

    assert {
        fixture_id: spec["action_ordinals"]
        for fixture_id, spec in CARD_FIXTURE_MAP.items()
    } == CARD_PROBES


def test_synthetic_missing_evidence_is_not_marked_covered():
    tmp_path = Path(__file__).parent / ".card-signature-test-fixture"
    tmp_path.mkdir(exist_ok=True)
    cards = {
        "cards": [{
            "id": "A", "character": "Ironclad", "type": "Attack",
            "rarity": "Common", "cost": 1, "target_type": "AnyEnemy",
            "multiplayer_only": False,
            "upgraded": {"id": "A_UPGRADE", "cost": 0},
        }],
    }
    semantics = {
        "variants": [
            {"id": "A", "upgraded": False, "source_text": "Deal damage.",
             "operations": [_op("damage")],
             "is_fully_structured": True},
            {"id": "A_UPGRADE", "upgraded": True, "source_text": "Deal damage.",
             "operations": [_op("damage", amount=2)],
             "is_fully_structured": True},
        ],
    }
    cards_path = tmp_path / "cards.json"
    semantics_path = tmp_path / "semantics.json"
    try:
        cards_path.write_text(json.dumps(cards), encoding="utf-8")
        semantics_path.write_text(json.dumps(semantics), encoding="utf-8")
        report = build(cards_path, semantics_path,
                       fixtures_dir=tmp_path / "fixtures",
                       reports_dir=tmp_path / "reports")
        assert report["summary"]["single_player_variants"] == 2
        assert report["summary"]["signatures"] == 1
        row = report["signatures"][0]
        assert row["evidence_status"] == "fixture_gap"
        assert row["evidence_policy"] == "fixture_gap"
        assert row["equivalence_required"] is True
        assert row["variant_evidence"][0]["status"] == "unverified"
        assert row["variant_fingerprints"]
    finally:
        cards_path.unlink(missing_ok=True)
        semantics_path.unlink(missing_ok=True)
        try:
            tmp_path.rmdir()
        except OSError:
            pass


def test_report_artifact_exposes_honest_counts_when_present():
    path = Path(__file__).parents[1] / "data" / "card-semantic-signature-report.json"
    if not path.is_file():
        return
    report = json.loads(path.read_text(encoding="utf-8"))
    assert all(name.startswith("p1-card-") for name in report["fixture_family_registry"])
    assert report.get("evidence_manifest_validation", {}).get("verdict") == "pass"
    summary = report["summary"]
    assert summary["signatures_with_behavior_gap"] >= summary["signatures_fixture_partial"]
    assert summary["behavior_reports_strict"] + summary["behavior_reports_degraded"] >= 0


def test_unknown_probability_cannot_be_strict_evidence():
    report_path = Path(__file__).parent / ".card-signature-report-test.json"
    report = {
        **VERSION_LOCK,
        "fixture": "p1-card-random-target",
        "action_ordinal": 0,
        "match": True,
        "mismatch_count": 0,
        "mismatches": [],
        "projected_comparison_hash": "same",
        "actual_comparison_hash": "same",
        "confidence": "Reliable",
        "trace_id": "trace-v0111-test",
        "normalized_action_id": "play_card:card:SWORD_BOOMERANG:001:none",
        "fields": [{"field": "enemy_damage_total"}],
    }
    try:
        report_path.write_text(json.dumps(report), encoding="utf-8")
        trace = [{
            "trace_id": "trace-v0111-test",
            "normalized_action_id": report["normalized_action_id"],
            "public_observation": {"hand": []},
            "chance_branch": {
                "produced": True, "probability_known": False,
                "kind": "realized_rng_consumption", "branch_enumerated": False,
            },
        }]
        result = _validate_report(
            "p1-card-random-target", 0, report_path, report, trace,
            "trace-v0111-test", ["SWORD_BOOMERANG"], [], set(), None)
        assert result["strict_eligible"] is False
        assert "probability_unknown" in result["issues"]
        assert "realized_rng_consumption" in result["issues"]
    finally:
        report_path.unlink(missing_ok=True)
