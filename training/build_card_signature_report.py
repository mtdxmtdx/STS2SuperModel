#!/usr/bin/env python3
"""Build the card semantic-signature report required by
RELIC_CARD_GAP_COMPLETION_PLAN.md batches C0/C1.

Groups every single-player combat card variant by its semantic signature
(structural tuple of operations), maps each signature to the simulator
handler families (Core EffectKind), the versioned random operators, the
stable choice contract, and the CLI/ShadowDiff fixture families that carry
behavior evidence. Machine checks produce gap lists instead of silently
passing.

Outputs:
  data/card-semantic-signature-report.json
  data/card-semantic-evidence-manifest.json
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / "data"
GAME_VERSION = "v0.111.0"

# Evidence and source locks are kept here instead of inferred from whatever
# files happen to be present in training/fixtures.  The old builder globbed
# every *-commands.jsonl (including relic/power probes), which made a category
# look verified merely because an unrelated file existed.
VERSION_LOCK = {
    "game_version": "v0.111.0",
    "game_commit": "41cef1ea",
    "assembly_sha256": "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9",
    "cli_protocol_version": "0.2.0",
    "trace_schema": 1,
}

_DYNAMIC_FLAG_KEYS = (
    "repeat_by_energy_spent", "repeat_by_orb_count", "repeat_by_exhausted_count",
    "repeat_by_history_counter", "repeat_by_kill_count", "repeat_by_stars_gained",
    "amount_by_energy_spent", "amount_by_alive_enemy_count",
    "amount_by_distinct_orb_types", "amount_by_hand_attack_count",
    "amount_by_cards_drawn_this_turn", "amount_by_target_vulnerable_stacks",
)

# This is the only fixture allow-list used by the card evidence builder.  Each
# entry names the card variant(s) intentionally placed in that fixture and the
# action ordinals that the card runner passes to ShadowDiff.  Relic, power and
# P0 fixture names are deliberately absent.
CARD_FIXTURE_MAP: dict[str, dict[str, Any]] = {
    "p1-card-exhaust-self": {"variant_ids": ["MOLTEN_FIST"], "action_ordinals": [0, 1]},
    "p1-card-ethereal": {"variant_ids": ["DEFILE"], "action_ordinals": [0, 1]},
    "p1-card-retain": {
        "variant_ids": ["SNAKEBITE"], "setup_variant_ids": ["STRIKE_SILENT"],
        "action_ordinals": [0, 1, 2],
    },
    "p1-card-innate": {"variant_ids": ["BACKSTAB"], "action_ordinals": [0, 1]},
    "p1-card-discard-select": {"variant_ids": ["SURVIVOR"], "action_ordinals": [0]},
    # These fixtures exercise a chance/choice operator.  A report from one
    # realized outcome is not sufficient to promote the semantic family:
    # the trace must explicitly enumerate the branch probabilities.
    "p1-card-random-exhaust": {
        "variant_ids": ["TRUE_GRIT"], "action_ordinals": [0, 1],
        "requires_branch_enumeration": True,
    },
    "p1-card-random-target": {
        "variant_ids": ["SWORD_BOOMERANG"], "action_ordinals": [0],
        "requires_branch_enumeration": True,
    },
    "p1-card-choice-copy": {
        "variant_ids": ["DUAL_WIELD"], "action_ordinals": [0],
        "requires_branch_enumeration": True,
    },
    "p1-card-generate": {"variant_ids": ["ANGER"], "action_ordinals": [0, 1]},
    "p1-card-generate-shiv": {
        "variant_ids": ["LEADING_STRIKE", "SHIV"], "action_ordinals": [0, 1, 2],
    },
    "p1-card-x-cost": {"variant_ids": ["WHIRLWIND"], "action_ordinals": [0, 1]},
    "p1-card-auto-play": {"variant_ids": ["HAVOC"], "action_ordinals": [0, 1]},
    "p1-card-move-upgrade-base": {
        "variant_ids": ["HEADBUTT"], "setup_variant_ids": ["STRIKE_IRONCLAD"],
        "action_ordinals": [0, 1, 2],
    },
    "p1-card-move-upgrade-up": {
        "variant_ids": ["HEADBUTT_UPGRADE"], "setup_variant_ids": ["STRIKE_IRONCLAD"],
        "action_ordinals": [0, 1, 2],
    },
}

# Semantic operation kind -> simulator handler family (Core EffectKind
# members executed by DeterministicSimulator / ShadowSimulationTransitions).
HANDLER_MAP: dict[str, list[str]] = {
    "damage": ["EffectKind.Damage", "EffectKind.RandomEnemyDamage (target=random_enemy)"],
    "dynamic_damage": ["EffectKind.DynamicDamage"],
    "lose_enemy_hp": ["EffectKind.LoseEnemyHp"],
    "outbreak": ["EffectKind.Outbreak"],
    "block": ["EffectKind.Block", "EffectKind.RandomEnemyDamage (random_source block rider)"],
    "dynamic_block": ["EffectKind.DynamicBlock"],
    "heal": ["EffectKind.Heal"],
    "gain_energy": ["EffectKind.GainEnergy"],
    "modify_max_hp": ["EffectKind.ModifyMaxHp"],
    "draw": ["EffectKind.Draw", "EffectKind.DrawToHandSize", "EffectKind.DrawUntilNonAttack"],
    "apply_status": ["EffectKind.ApplyStatus", "EffectKind.RandomEnemyStatus (target=random_enemy)"],
    "multiply_status": ["EffectKind.MultiplyStatus"],
    "discard_cards": ["EffectKind.DiscardCards", "EffectKind.DiscardHand"],
    "exhaust_cards": ["EffectKind.ExhaustCards", "EffectKind.ExhaustHand", "EffectKind.ExhaustStatusCards"],
    "random_exhaust_cards": ["EffectKind.RandomExhaustCards", "EffectKind.RandomExhaustAttackAndGrow"],
    "exhaust_non_attacks_and_block": ["EffectKind.ExhaustNonAttacksAndBlock"],
    "discard_hand_then_draw_same": ["EffectKind.DiscardHandThenDrawSame"],
    "discard_hand_and_generate": ["EffectKind.DiscardHandAndGenerate"],
    "reboot": ["EffectKind.Reboot"],
    "generate_card": ["EffectKind.GenerateCards", "EffectKind.GenerateRandomCards"],
    "auto_play_card": [
        "EffectKind.AutoPlayFromDrawPile", "EffectKind.AutoPlayShivsFromExhaust",
        "EffectKind.AutoPlaySelfFromPile", "EffectKind.AutoPlayEtherealFromExhaust",
    ],
    "select_card": [
        "EffectKind.ChooseDrawToHand", "EffectKind.ChooseDrawToExhaust",
        "EffectKind.ChooseDiscardToHand", "EffectKind.ChooseDiscardToDrawTop",
        "EffectKind.ChooseHandToDrawTop", "EffectKind.CopyChosenHandCard",
        "EffectKind.ModifySelectedHandCard",
    ],
    "copy_selected_card": ["EffectKind.CopyChosenHandCard"],
    "lose_hp": ["EffectKind.LoseHp"],
    "channel_orb": ["EffectKind.ChannelOrbs"],
    "evoke_orb": ["EffectKind.EvokeOrbs"],
    "trigger_orb_passive": ["EffectKind.TriggerOrbPassives"],
    "modify_orb_capacity": ["EffectKind.ModifyOrbCapacity"],
    "summon": ["EffectKind.Summon", "EffectKind.KillCompanion"],
    "forge": ["EffectKind.Forge"],
    "move_card": [
        "EffectKind.ChooseDiscardToDrawTop", "EffectKind.ChooseHandToDrawTop",
        "EffectKind.MoveRandomRareDrawToHand", "EffectKind.MoveAllZeroCostDiscardToHand",
        "EffectKind.ReturnSelfToHandAfterSkills", "EffectKind.DelayedReturnSelfToHand",
        "EffectKind.MoveKingsBladeToHand",
    ],
    "modify_cost": ["EffectKind.ModifyHandCosts", "EffectKind.CapHandCosts",
                     "EffectKind.ModifyPlayedCardCost"],
    "modify_card_damage": ["EffectKind.ModifyPlayedCardDamage", "EffectKind.ModifySelectedHandCard"],
    "modify_card_block": ["EffectKind.ModifyPlayedCardBlock", "EffectKind.ModifySelectedHandCard"],
    "modify_hand_costs": ["EffectKind.ModifyHandCosts", "EffectKind.CapHandCosts"],
    "clear_enemy_block_and_artifact": ["EffectKind.ClearEnemyBlockAndArtifact"],
    "play_restriction": ["EffectKind.PlayRestriction"],
    "kill_all_doomed_enemies": ["EffectKind.KillAllDoomedEnemies"],
    "upgrade_card": ["EffectKind.UpgradeCards"],
    "transform_cards": ["EffectKind.TransformCards"],
    "companion_damage": ["EffectKind.CompanionDamage"],
}

# Keyword ids that map to combat keyword flags / turn-boundary pipelines.
KEYWORD_HANDLERS: dict[str, str] = {
    "消耗": "Exhaust destination flag (exhaust-pile migration + exhaust triggers)",
    "虚无": "Ethereal turn-end exhaust pipeline",
    "保留": "Retain turn-end pipeline (EffectKind.RetainHand for hand-wide)",
    "固有": "Innate opening-draw pipeline",
    "永恒": "Eternal flag (cannot be exhausted)",
    "奇巧": "Confused cost modifier (CombatEnergyCosts stream)",
    "不能被打出": "Unplayable restriction (EffectKind.PlayRestriction)",
    "（抽0张牌）": "Opening-draw-size modifier",
    "RETAIN_HAND": "EffectKind.RetainHand",
    "DISCARD_DRAWN_NONZERO_COST": "EffectKind.DiscardDrawnNonZeroCost",
    "RETURN_SELF_TO_HAND_AFTER_SKILLS": "EffectKind.ReturnSelfToHandAfterSkills",
    "NIGHTMARE_CHOOSE": "Choice pipeline (EffectKind.CopyChosenHandCard)",
    "GENERATED_CARD_FREE_THIS_TURN": "Free-this-turn cost pipeline",
    "GENERATED_CARDS_FREE_THIS_COMBAT": "Free-this-combat cost pipeline",
    "FIRST_CARDS_FREE_EACH_TURN": "Free-cards-each-turn cost pipeline",
    "QUICKSAND_COUNTER": "Quicksand counter status pipeline",
}

# Keyword ids outside the single-player combat scope (map/reward/event or
# multiplayer-only bookkeeping); variants carrying only these are OutOfScope.
NON_COMBAT_KEYWORDS = {
    "EXTRA_CARD_REWARD", "GOLD_REWARD", "RANDOM_POTION_REWARD",
    "MAP_BONUS_GOLD_LOCATION", "UNLOCK_SPECIAL_EVENT", "HATCH_AT_REST_SITE",
    "REMOVE_FROM_DECK_AFTER_COMBATS", "LOSE_GOLD", "KILL_OSTY", "FLEE",
}

# Versioned random operators (plan C2 layer 2): every random operation must
# reference one of these; the sources are the engine RNG streams exported by
# the CLI teacher snapshot and mirrored by the shadow.
RANDOM_OPERATORS = {
    "CombatTargets": "random_op:CombatTargets@v0.111.0",
    "CombatCardSelection": "random_op:CombatCardSelection@v0.111.0",
    "Shuffle": "random_op:Shuffle@v0.111.0",
    "CombatCardGeneration": "random_op:CombatCardGeneration@v0.111.0",
}

# Special generate_card templates that are stable generation-rule ids rather
# than plain catalog card ids (handled by the GenerateCards /
# GenerateRandomCards pipeline in DeterministicSimulator).
GENERATION_RULES: dict[str, str] = {
    "SELF_COPY": "self-copy of the playing card (discard-pile return)",
    "SELF_COPY_ZERO_COST": "self-copy with 0-cost modifier",
    "NIGHTMARE_COPIES": "NIGHTMARE_CHOOSE choice copies",
    "SOUL_PLUS": "upgraded SOUL companion card",
    "INKY_SHIV": "Ink-generated shiv variant",
    "QUASAR_COLORLESS": "Quasar colorless generation rule",
    "QUASAR_COLORLESS_UPGRADED": "Quasar colorless upgraded generation rule",
    "SPLASH_OTHER_ATTACK": "copy of another attack in hand",
    "SPLASH_OTHER_ATTACK_UPGRADED": "upgraded copy of another attack in hand",
    "ABUNDANCE_POWER_CHOICE": "Abundance power choice generation",
    "ABUNDANCE_POWER_CHOICE_UPGRADED": "Abundance power choice upgraded generation",
}

# 随机* pool descriptors resolve through the versioned
# random_op:CombatCardGeneration operator instead of a fixed template.
RANDOM_POOL_PREFIX = "随机"


def build_localized_reverse_index(localization_path: Path) -> dict[str, str]:
    """Chinese card title -> canonical card id (from the CLI localization)."""
    index: dict[str, str] = {}
    if not localization_path.is_file():
        return index
    doc = json.loads(localization_path.read_text(encoding="utf-8"))
    for key, value in doc.items():
        if key.endswith(".title"):
            index.setdefault(value, key[: -len(".title")])
    return index


def resolve_template(template: str | None, known_ids: set[str],
                     localized: dict[str, str]) -> str:
    """Return 'card' | 'rule' | 'pool' | 'unresolved' for a template id."""
    if not template:
        return "unresolved"
    if template in known_ids:
        return "card"
    if template in GENERATION_RULES:
        return "rule"
    if template.startswith(RANDOM_POOL_PREFIX) or template == "RANDOM_ANY":
        return "pool"
    canonical = localized.get(template)
    if canonical is None and template.endswith("+"):
        upgraded = localized.get(template[:-1], "")
        canonical = f"{upgraded}_UPGRADE" if upgraded else None
    if canonical and canonical in known_ids:
        return "card"
    return "unresolved"
# Candidate fixture families for each card semantic category.  This registry
# intentionally contains *only* card fixtures from CARD_FIXTURE_MAP.  An empty
# list means that the category currently has no behavioral witness and must be
# reported as a gap; it must never be treated as covered by a relic/power
# fixture with a similar name.
CATEGORY_FIXTURES: dict[str, list[str]] = {
    "exhaust": ["p1-card-exhaust-self", "p1-card-innate"],
    "ethereal": ["p1-card-ethereal"],
    "retain": ["p1-card-retain"],
    "innate": ["p1-card-innate"],
    "discard_select": ["p1-card-discard-select"],
    "random_discard_or_exhaust": ["p1-card-random-exhaust"],
    "random_target": ["p1-card-random-target"],
    # Generic random operations (random card pools, random upgrades, etc.) do
    # not have a witness until a fixture for that exact operator exists.
    "random": [],
    "choice": ["p1-card-choice-copy", "p1-card-discard-select"],
    "generate": ["p1-card-generate", "p1-card-generate-shiv"],
    "random_generate": [],
    "x_cost": ["p1-card-x-cost"],
    "upgrade_delta": ["p1-card-move-upgrade-base", "p1-card-move-upgrade-up"],
    "auto_play": ["p1-card-auto-play"],
    "move": ["p1-card-move-upgrade-base", "p1-card-move-upgrade-up"],
    "power_or_relic_trigger": [],
    "turn_boundary": ["p1-card-ethereal", "p1-card-retain", "p1-card-innate"],
    "dynamic_value": [],
}


def _amount_class(op: dict) -> str:
    if op.get("amount_by_energy_spent"):
        return "by_energy"
    if op.get("dynamic_amount_id") or any(
            op.get(k) for k in (
                "amount_by_alive_enemy_count",
                "amount_by_distinct_orb_types", "amount_by_hand_attack_count",
                "amount_by_cards_drawn_this_turn", "amount_by_target_vulnerable_stacks")):
        return "dynamic"
    return "fixed"


def _repeat_class(op: dict) -> str:
    if op.get("repeat_by_energy_spent"):
        return "by_energy"
    if op.get("repeat_by_orb_count") or op.get("repeat_by_exhausted_count") or \
            op.get("repeat_by_history_counter") or op.get("repeat_by_kill_count") or \
            op.get("repeat_by_stars_gained"):
        return "dynamic"
    if (op.get("repeat") or 1) > 1:
        return "multi"
    return "single"


def signature_ops(operations: list[dict]) -> list[dict]:
    """Structural projection of an operation list.

    Amounts are intentionally left out so base/upgraded variants can share a
    structural family, but every semantic discriminator is retained.  The old
    implementation only retained ``id`` for a handful of operation kinds;
    that collapsed unrelated statuses, orb types, dynamic-value rules and
    upgrade targets into one signature.  Keep IDs and dynamic flags for every
    kind and retain the repeat/x parameters needed to distinguish behavior.
    """
    projection = []
    for op in operations:
        kind = op.get("kind")
        amount = op.get("amount")
        if isinstance(amount, (int, float)):
            amount_sign = -1 if amount < 0 else 1 if amount > 0 else 0
        else:
            amount_sign = None
        entry: dict = {
            "kind": kind,
            "id": op.get("id"),
            "target": op.get("target"),
            "trigger": op.get("trigger"),
            "timing": op.get("timing"),
            "condition": op.get("condition"),
            "random_source": op.get("random_source"),
            "amount_class": _amount_class(op),
            "amount_sign": amount_sign,
            "repeat_class": _repeat_class(op),
            "repeat_value": op.get("repeat", 1),
            "x_bonus": op.get("x_bonus", 0),
            "dynamic_amount_id": op.get("dynamic_amount_id"),
            "dynamic_flags": {
                key: bool(op.get(key, False)) for key in _DYNAMIC_FLAG_KEYS
            },
            "duration": op.get("duration"),
            "all": bool(op.get("all")),
        }
        projection.append(entry)
    return projection


def signature_id(projection: list[dict]) -> str:
    canonical = json.dumps(projection, ensure_ascii=False, sort_keys=True)
    return "sig-" + hashlib.sha1(canonical.encode("utf-8")).hexdigest()[:12]


def categorize(projection: list[dict]) -> list[str]:
    cats: set[str] = set()
    for op in projection:
        kind = op.get("kind")
        target = op.get("target")
        random_source = op.get("random_source")
        if kind == "keyword":
            kid = op.get("id")
            if kid == "消耗":
                cats.add("exhaust")
            elif kid == "虚无":
                cats.add("ethereal")
            elif kid == "保留" or kid == "RETAIN_HAND":
                cats.add("retain")
            elif kid == "固有":
                cats.add("innate")
        elif kind == "exhaust_cards":
            cats.add("exhaust")
        elif kind == "random_exhaust_cards":
            cats.add("random_discard_or_exhaust")
        elif kind == "discard_cards" and target == "hand":
            cats.add("discard_select" if not random_source else "random_discard_or_exhaust")
        elif kind in ("damage", "apply_status") and target == "random_enemy":
            cats.add("random_target")
        elif kind in ("select_card", "copy_selected_card"):
            cats.add("choice")
        elif kind == "generate_card":
            cats.add("generate")
        elif kind == "auto_play_card":
            cats.add("auto_play")
        elif kind == "move_card":
            cats.add("move")
            if isinstance(op.get("id"), str) and op["id"].startswith("CHOOSE"):
                cats.add("choice")
        elif kind in ("apply_status", "draw", "damage") and op.get("trigger"):
            cats.add("power_or_relic_trigger")
        # Any RNG source is a random semantic requirement, even when the
        # operation is not a direct random-enemy damage/status operation (for
        # example random card generation, random upgrades or random moves).
        random_template = (
            kind == "generate_card"
            and isinstance(op.get("id"), str)
            and (op["id"].startswith(RANDOM_POOL_PREFIX) or op["id"] == "RANDOM_ANY")
        )
        if random_source or random_template:
            cats.add("random")
            if kind == "generate_card":
                cats.add("random_generate")
        # Non-immediate timing and explicit trigger phases require a turn
        # boundary/trigger witness rather than a generic immediate-action
        # fixture.
        if op.get("timing") not in (None, "immediate") or op.get("trigger"):
            cats.add("turn_boundary")
        if op.get("amount_class") in ("dynamic", "by_energy") or op.get("repeat_class") in ("dynamic", "by_energy"):
            cats.add("dynamic_value")
        if op.get("repeat_class") == "by_energy" or op.get("amount_class") == "by_energy":
            cats.add("x_cost")
    return sorted(cats)


def variant_ids_from_cards(cards_doc: dict) -> set[str]:
    ids: set[str] = set()
    for card in cards_doc["cards"]:
        ids.add(card["id"])
        upgraded = card.get("upgraded")
        if upgraded:
            ids.add(upgraded["id"])
    return ids


def _canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _sha256(path: Path) -> str | None:
    if not path.is_file():
        return None
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _relative(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT.parent.resolve()).as_posix()
    except ValueError:
        return path.resolve().as_posix()


def _normalize_variant_id(value: str | None) -> str | None:
    if not value:
        return None
    value = value.removeprefix("CARD.")
    if value.endswith("+"):
        return value[:-1] + "_UPGRADE"
    return value


def _variant_payload(variant: dict[str, Any], meta: dict[str, Any]) -> dict[str, Any]:
    """Full semantic witness used to detect numeric/upgrade differences."""
    return {
        "variant_id": variant.get("id"),
        "upgraded": bool(variant.get("upgraded", meta.get("upgraded", False))),
        "character": meta.get("character"),
        "type": meta.get("type"),
        "rarity": meta.get("rarity"),
        "cost": meta.get("cost"),
        "target_type": meta.get("target_type"),
        "operations": variant.get("operations") or [],
    }


def _variant_delta_flags(variants: list[dict[str, Any]], metadata: dict[str, dict[str, Any]]) -> dict[str, Any]:
    payloads = [_variant_payload(v, metadata.get(v.get("id", ""), {})) for v in variants]
    numeric_payloads = []
    discriminator_payloads = []
    for payload in payloads:
        numeric_payloads.append({
            "cost": payload["cost"],
            "operations": [
                {"amount": op.get("amount"), "repeat": op.get("repeat"), "x_bonus": op.get("x_bonus")}
                for op in payload["operations"]
            ],
        })
        discriminator_payloads.append([
            {
                "kind": op.get("kind"),
                "id": op.get("id"),
                "dynamic_amount_id": op.get("dynamic_amount_id"),
                "random_source": op.get("random_source"),
                "target": op.get("target"),
                "trigger": op.get("trigger"),
                "timing": op.get("timing"),
                "condition": op.get("condition"),
            }
            for op in payload["operations"]
        ])
    upgraded = any(bool(p["upgraded"]) or str(p["variant_id"]).endswith("_UPGRADE") for p in payloads)
    return {
        "has_numeric_delta": len({_canonical_json(v) for v in numeric_payloads}) > 1,
        "has_discriminator_delta": len({_canonical_json(v) for v in discriminator_payloads}) > 1,
        "has_full_delta": len({_canonical_json(v) for v in payloads}) > 1,
        "has_upgrade_variant": upgraded,
        "variant_fingerprints": [
            {
                "variant_id": p["variant_id"],
                "upgraded": p["upgraded"],
                "fingerprint": hashlib.sha256(_canonical_json(p).encode("utf-8")).hexdigest(),
                "numeric_fingerprint": hashlib.sha256(
                    _canonical_json(numeric_payloads[i]).encode("utf-8")).hexdigest(),
                "discriminator_fingerprint": hashlib.sha256(
                    _canonical_json(discriminator_payloads[i]).encode("utf-8")).hexdigest(),
            }
            for i, p in enumerate(payloads)
        ],
    }


def _is_gameplay_action(action_id: str | None) -> bool:
    return bool(action_id) and (
        action_id.startswith("play_card:")
        or action_id.startswith("use_potion:")
        or action_id == "end_turn"
        or action_id.startswith("end_turn:")
    )


def _load_trace(path: Path) -> tuple[list[dict[str, Any]], str | None]:
    if not path.is_file():
        return [], None
    rows: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            rows.append(value)
    trace_id = next((row.get("trace_id") for row in rows if row.get("trace_id")), None)
    return rows, trace_id


def _trace_action_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    # Keep this filter byte-for-byte equivalent to run_p1_card_probes.py and
    # ShadowDiff's action ordinal definition.
    return [
        row for row in rows
        if _is_gameplay_action(row.get("normalized_action_id"))
        and isinstance(row.get("public_observation"), dict)
    ]


def _trace_action_rows_with_indices(rows: list[dict[str, Any]]) -> list[tuple[int, dict[str, Any]]]:
    """Return action rows together with their source line indexes."""
    return [
        (index, row) for index, row in enumerate(rows)
        if _is_gameplay_action(row.get("normalized_action_id"))
        and isinstance(row.get("public_observation"), dict)
    ]


def _collect_trace_card_ids(rows: list[dict[str, Any]]) -> set[str]:
    ids: set[str] = set()

    def visit(value: Any) -> None:
        if isinstance(value, dict):
            raw_id = value.get("id")
            instance_id = value.get("instance_id")
            if isinstance(raw_id, str) and (raw_id.startswith("CARD.") or raw_id in ("SHIV", "SHIV_UPGRADE")):
                normalized = _normalize_variant_id(raw_id)
                if normalized:
                    ids.add(normalized)
            if isinstance(instance_id, str) and instance_id.startswith("card:"):
                pieces = instance_id.split(":")
                if len(pieces) >= 2:
                    normalized = _normalize_variant_id(pieces[1])
                    if normalized:
                        ids.add(normalized)
            for key in ("hand", "draw_pile", "discard_pile", "exhaust_pile", "cards"):
                if key in value:
                    visit(value[key])
        elif isinstance(value, list):
            for item in value:
                visit(item)

    for row in rows:
        for key in ("public_observation", "teacher_snapshot"):
            if key in row:
                visit(row[key])
    return ids


def _action_variant_ids(action_id: str | None, declared: list[str], trace_card_ids: set[str]) -> list[str]:
    if action_id and action_id.startswith("play_card:"):
        pieces = action_id.split(":")
        if len(pieces) >= 3:
            normalized = _normalize_variant_id(pieces[2])
            return [normalized] if normalized else []
    # Boundary reports (end_turn after an Ethereal/Retain card) do not carry a
    # card model in normalized_action_id.  Associate only declared variants
    # that are actually present in the trace, never every category member.
    return [variant_id for variant_id in declared if variant_id in trace_card_ids]


def _expected_report_name(fixture_id: str, ordinal: int, ordinals: list[int]) -> str:
    suffix = f"-{ordinal}" if len(ordinals) > 1 or ordinal != 0 else ""
    return f"p1-csharp-{fixture_id.removeprefix('p1-')}-diff-report{suffix}.json"


def _validate_report(
    fixture_id: str,
    ordinal: int,
    report_path: Path,
    report: dict[str, Any] | None,
    trace_rows: list[dict[str, Any]],
    trace_id: str | None,
    declared_variant_ids: list[str],
    setup_variant_ids: list[str],
    trace_card_ids: set[str],
    expected_repeat_sha256: str | None = None,
    command_exists: bool = True,
    requires_branch_enumeration: bool = False,
) -> dict[str, Any]:
    issues: list[str] = []
    if not command_exists:
        issues.append("command_file_missing")
    if not trace_rows:
        issues.append("trace_missing_or_empty")
    # A trace is part of the evidence, not just a convenient way to locate an
    # action.  Reject a report whose trace was produced under another game /
    # protocol/schema lock, even when the report itself has the right fields.
    trace_meta = next((row for row in trace_rows if isinstance(row, dict)), None)
    if trace_meta is None:
        issues.append("trace_metadata_missing")
    else:
        for key, expected in VERSION_LOCK.items():
            if trace_meta.get(key) != expected:
                issues.append(f"trace_version_mismatch:{key}")
        if not trace_id:
            issues.append("trace_id_missing")
    action_rows = _trace_action_rows_with_indices(trace_rows)
    action_pair = action_rows[ordinal] if 0 <= ordinal < len(action_rows) else None
    action_row = action_pair[1] if action_pair else None
    report_action_id = report.get("normalized_action_id") if report else None
    # For an end_turn row, only cards present immediately before that action
    # may be attributed to the evidence.  Looking through the entire trace can
    # accidentally associate a card generated later in the fixture.
    pre_action_card_ids = trace_card_ids
    if action_pair:
        pre_action_card_ids = _collect_trace_card_ids(trace_rows[:action_pair[0]])
    observed_action_variant_ids = _action_variant_ids(
        report_action_id, declared_variant_ids, pre_action_card_ids)
    setup_action_variant_ids = [
        variant_id for variant_id in observed_action_variant_ids
        if variant_id in setup_variant_ids and variant_id not in declared_variant_ids
    ]
    source_variant_ids = [
        variant_id for variant_id in observed_action_variant_ids
        if variant_id in declared_variant_ids
    ]

    if report is None:
        issues.append("missing_report")
    else:
        if report.get("fixture") != fixture_id:
            issues.append("fixture_id_mismatch")
        if report.get("action_ordinal") != ordinal:
            issues.append("action_ordinal_mismatch")
        for key, expected in VERSION_LOCK.items():
            if report.get(key) != expected:
                issues.append(f"version_mismatch:{key}")
        if report.get("match") is not True:
            issues.append("match_false")
        if report.get("mismatch_count") != 0:
            issues.append("mismatch_count_nonzero")
        if report.get("mismatches") not in ([], None):
            issues.append("mismatches_not_empty")
        if report.get("projected_comparison_hash") != report.get("actual_comparison_hash"):
            issues.append("comparison_hash_mismatch")
        repeat_hash = report.get("repeat_sha256")
        report_hash_valid = isinstance(repeat_hash, str) and bool(
            re.fullmatch(r"[0-9a-f]{64}", repeat_hash))
        if expected_repeat_sha256:
            if not re.fullmatch(r"[0-9a-f]{64}", expected_repeat_sha256):
                issues.append("repeat_manifest_hash_invalid")
            elif not ((report_hash_valid and repeat_hash == expected_repeat_sha256)
                      or _sha256(report_path) == expected_repeat_sha256):
                issues.append("repeat_manifest_hash_mismatch")
        elif not report_hash_valid:
            issues.append("repeat_sha256_missing_or_invalid")
        else:
            # An embedded hash alone is not a repeat-run proof: it can be
            # copied together with a stale report.  Require the signed/global
            # p1-repeat-verification manifest for strict eligibility.
            issues.append("repeat_manifest_missing")
        if report.get("confidence") != "Reliable":
            issues.append("confidence_not_reliable")
        if trace_id and report.get("trace_id") != trace_id:
            issues.append("trace_id_mismatch")
        if action_row is None:
            issues.append("trace_action_ordinal_missing")
        elif report_action_id != action_row.get("normalized_action_id"):
            issues.append("normalized_action_id_mismatch")

        chance = action_row.get("chance_branch") if action_row else None
        if isinstance(chance, dict) and chance.get("produced"):
            if chance.get("probability_known") is not True:
                issues.append("probability_unknown")
            if chance.get("kind") == "realized_rng_consumption":
                issues.append("realized_rng_consumption")
            if chance.get("branch_enumerated") is not True:
                issues.append("branch_not_enumerated")
        elif requires_branch_enumeration:
            # For random/choice fixtures, a single deterministic realization
            # is never a complete witness.  Keep it degraded until the trace
            # contains an explicit probability-bearing branch enumeration.
            issues.append("required_branch_not_produced")
        if requires_branch_enumeration and isinstance(chance, dict):
            if chance.get("probability_known") is not True:
                issues.append("probability_unknown")
            if chance.get("branch_enumerated") is not True:
                issues.append("branch_not_enumerated")

    if not source_variant_ids and not setup_action_variant_ids:
        issues.append("source_variant_not_observed")
    elif any(variant_id not in declared_variant_ids and variant_id not in setup_variant_ids
             for variant_id in observed_action_variant_ids):
        issues.append("source_variant_not_declared")

    quality = "verified" if not issues else "degraded"
    return {
        "fixture_id": fixture_id,
        "action_ordinal": ordinal,
        "report_id": report_path.name,
        "report_path": _relative(report_path),
        "report_sha256": _sha256(report_path),
        "repeat_sha256": expected_repeat_sha256 or (
            report.get("repeat_sha256") if isinstance(report, dict) else None),
        "normalized_action_id": report_action_id,
        "source_variant_ids": sorted(set(source_variant_ids)),
        "setup_variant_ids": sorted(setup_action_variant_ids),
        "quality": quality,
        "strict_eligible": not issues,
        "issues": sorted(set(issues)),
        "comparison_fields": [
            field.get("field") for field in (report or {}).get("fields", [])
            if isinstance(field, dict) and isinstance(field.get("field"), str)
        ],
        "probability_known": (
            (action_row or {}).get("chance_branch", {}).get("probability_known")
            if isinstance((action_row or {}).get("chance_branch"), dict) else None
        ),
    }


def _collect_fixture_evidence(
    fixtures_dir: Path,
    reports_dir: Path,
    repeat_hashes: dict[str, str] | None = None,
) -> dict[str, dict[str, Any]]:
    evidence: dict[str, dict[str, Any]] = {}
    for fixture_id, spec in sorted(CARD_FIXTURE_MAP.items()):
        command_path = fixtures_dir / f"{fixture_id}-commands.jsonl"
        trace_path = reports_dir / f"{fixture_id}-trace.jsonl"
        trace_rows, trace_id = _load_trace(trace_path)
        trace_card_ids = _collect_trace_card_ids(trace_rows)
        report_records: list[dict[str, Any]] = []
        for ordinal in spec["action_ordinals"]:
            report_path = reports_dir / _expected_report_name(
                fixture_id, ordinal, spec["action_ordinals"])
            report: dict[str, Any] | None = None
            if report_path.is_file():
                try:
                    parsed = json.loads(report_path.read_text(encoding="utf-8"))
                    if isinstance(parsed, dict):
                        report = parsed
                except json.JSONDecodeError:
                    report = None
            report_records.append(_validate_report(
                fixture_id,
                ordinal,
                report_path,
                report,
                trace_rows,
                trace_id,
                list(spec["variant_ids"]),
                list(spec.get("setup_variant_ids", [])),
                trace_card_ids,
                (repeat_hashes or {}).get(report_path.name),
                command_exists=command_path.is_file(),
                requires_branch_enumeration=bool(
                    spec.get("requires_branch_enumeration", False)),
            ))
        evidence[fixture_id] = {
            "fixture_id": fixture_id,
            "command_file": _relative(command_path),
            "command_sha256": _sha256(command_path),
            "trace_file": _relative(trace_path),
            "trace_sha256": _sha256(trace_path),
            "declared_variant_ids": list(spec["variant_ids"]),
            "setup_variant_ids": list(spec.get("setup_variant_ids", [])),
            "expected_action_ordinals": list(spec["action_ordinals"]),
            "trace_id": trace_id,
            "repeat_manifest": _relative(reports_dir / "p1-repeat-verification.json")
            if repeat_hashes is not None else None,
            "trace_card_ids": sorted(trace_card_ids),
            "reports": report_records,
            "strict_report_count": sum(bool(r["strict_eligible"]) for r in report_records),
            "degraded_report_count": sum(not r["strict_eligible"] for r in report_records),
            "strict_target_report_count": sum(
                bool(r["strict_eligible"] and r["source_variant_ids"])
                for r in report_records),
            "degraded_target_report_count": sum(
                bool((not r["strict_eligible"]) and r["source_variant_ids"])
                for r in report_records),
        }
    return evidence


def _build(
    cards_path: Path,
    semantics_path: Path,
    fixtures_dir: Path,
    reports_dir: Path,
    repeat_manifest_path: Path | None = None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    cards_doc = json.loads(cards_path.read_text(encoding="utf-8-sig"))
    semantics = json.loads(semantics_path.read_text(encoding="utf-8-sig"))
    known_ids = variant_ids_from_cards(cards_doc)
    localized = build_localized_reverse_index(
        ROOT / "sts2-cli-v0111/localization_zhs/cards.json")

    metadata: dict[str, dict[str, Any]] = {}
    for card in cards_doc["cards"]:
        base_meta = {
            "id": card["id"],
            "character": card.get("character"),
            "type": card.get("type"),
            "rarity": card.get("rarity"),
            "cost": card.get("cost"),
            "target_type": card.get("target_type"),
            "multiplayer_only": card.get("multiplayer_only", False),
            "upgraded": False,
        }
        metadata[card["id"]] = base_meta
        upgraded = card.get("upgraded")
        if upgraded:
            metadata[upgraded["id"]] = {
                **base_meta,
                "id": upgraded["id"],
                "cost": upgraded.get("cost", card.get("cost")),
                "target_type": upgraded.get("target_type", card.get("target_type")),
                "upgraded": True,
                "base_id": card["id"],
            }

    single = [
        variant for variant in semantics["variants"]
        if metadata.get(variant["id"]) is not None
        and not metadata[variant["id"]].get("multiplayer_only", False)
    ]
    repeat_path = repeat_manifest_path or reports_dir / "p1-repeat-verification.json"
    repeat_hashes: dict[str, str] | None = None
    if repeat_path.is_file():
        try:
            repeat_payload = json.loads(repeat_path.read_text(encoding="utf-8"))
            if repeat_payload.get("verdict") == "pass" and isinstance(repeat_payload.get("sha256"), dict):
                repeat_hashes = {
                    str(name): str(digest)
                    for name, digest in repeat_payload["sha256"].items()
                    if isinstance(name, str) and isinstance(digest, str)
                }
        except json.JSONDecodeError:
            repeat_hashes = None
    fixture_evidence = _collect_fixture_evidence(fixtures_dir, reports_dir, repeat_hashes)
    # The per-card direct matrix is maintained separately from the small
    # semantic-pattern fixture registry.  Import only rows whose witness
    # manifest has passed its own provenance/repeat checks; timeout, mismatch
    # and non-play rows remain degraded and never become Reliable evidence.
    direct_witness_path = reports_dir / "card-direct-witness-manifest.json"
    direct_witness_rows: list[dict[str, Any]] = []
    direct_witness_summary: dict[str, Any] = {}
    if direct_witness_path.is_file():
        try:
            direct_manifest = json.loads(direct_witness_path.read_text(encoding="utf-8"))
            if isinstance(direct_manifest, dict) and direct_manifest.get("validation", {}).get("verdict") == "pass":
                direct_witness_rows = [
                    row for row in direct_manifest.get("rows", [])
                    if isinstance(row, dict) and isinstance(row.get("variant_id"), str)
                ]
                direct_witness_summary = direct_manifest.get("summary", {})
        except (OSError, json.JSONDecodeError):
            direct_witness_rows = []
    evidence_by_variant: dict[str, list[dict[str, Any]]] = {}
    for fixture_id, fixture in fixture_evidence.items():
        for record in fixture["reports"]:
            for variant_id in record["source_variant_ids"]:
                evidence_by_variant.setdefault(variant_id, []).append({
                    **record,
                    "fixture_id": fixture_id,
                })
    for witness in direct_witness_rows:
        report_id = witness.get("report")
        strict = bool(witness.get("main_reliable_eligible"))
        witness_issues = [] if strict else [
            str(issue) for issue in witness.get("issues", []) if isinstance(issue, str)
        ]
        if not strict and not witness_issues:
            witness_issues = ["direct_witness_not_reliable"]
        evidence_by_variant.setdefault(witness["variant_id"], []).append({
            "fixture_id": "p1-card-direct-matrix",
            "report_id": report_id or "",
            "strict_eligible": strict,
            "source_variant_ids": [witness["variant_id"]],
            "issues": witness_issues,
        })

    groups: dict[str, list[tuple[dict[str, Any], list[dict[str, Any]]]]] = {}
    projection_canonicals: dict[str, set[str]] = {}
    for variant in single:
        projection = signature_ops(variant.get("operations") or [])
        canonical = _canonical_json(projection)
        sig = signature_id(projection)
        groups.setdefault(sig, []).append((variant, projection))
        projection_canonicals.setdefault(sig, set()).add(canonical)

    signatures: list[dict[str, Any]] = []
    machine = Counter()
    for sig, members in sorted(groups.items()):
        variant_rows = [item[0] for item in members]
        projections = [item[1] for item in members]
        ops = projections[0]
        handlers: list[str] = []
        gaps: list[str] = []
        random_operators: list[str] = []
        choice_contracts: list[str] = []
        non_combat_only = bool(ops) and all(
            op.get("kind") == "keyword" and op.get("id") in NON_COMBAT_KEYWORDS
            for op in ops)
        for op in ops:
            kind = op.get("kind")
            if kind == "keyword":
                kid = op.get("id")
                handler = KEYWORD_HANDLERS.get(kid)
                if handler:
                    handlers.append(handler)
                elif kid in NON_COMBAT_KEYWORDS:
                    handlers.append(f"OutOfScope (non-combat keyword {kid})")
                else:
                    gaps.append(f"unmapped keyword id {kid}")
                    machine["unmapped_keywords"] += 1
            else:
                mapped = HANDLER_MAP.get(kind)
                if mapped:
                    handlers.extend(mapped)
                else:
                    gaps.append(f"unmapped operation kind {kind}")
                    machine["unmapped_kinds"] += 1
            if op.get("random_source"):
                operator = RANDOM_OPERATORS.get(op["random_source"])
                if operator:
                    random_operators.append(operator)
                else:
                    gaps.append(f"unknown random source {op['random_source']}")
                    machine["unknown_random_sources"] += 1
            if kind in ("select_card", "copy_selected_card"):
                op_id = op.get("id")
                if op_id:
                    for variant_id in sorted({row["id"] for row in variant_rows}):
                        choice_contracts.append(f"choice_id={variant_id}#{op_id}")
                else:
                    gaps.append("select operation without stable rule id")
                    machine["select_without_id"] += 1
            if kind == "generate_card":
                template = op.get("id")
                resolution = resolve_template(template, known_ids, localized)
                if resolution == "unresolved":
                    gaps.append(f"generate template {template} not resolvable")
                    machine["template_unresolved"] += 1
                elif resolution == "pool":
                    machine["templates_pool"] += 1
                elif resolution == "rule":
                    machine["templates_rule"] += 1
                else:
                    machine["templates_card"] += 1

        categories = set(categorize(ops))
        deltas = _variant_delta_flags(variant_rows, metadata)
        if deltas["has_upgrade_variant"]:
            categories.add("upgrade_delta")
        if deltas["has_numeric_delta"]:
            categories.add("numeric_delta")
        categories = sorted(categories)
        candidate_families = sorted({
            family for cat in categories for family in CATEGORY_FIXTURES.get(cat, [])
        })
        missing_families = sorted({
            family for family in candidate_families
            if family not in fixture_evidence
            or fixture_evidence[family]["strict_report_count"] == 0
        })

        variant_evidence: list[dict[str, Any]] = []
        for variant_id in sorted({row["id"] for row in variant_rows}):
            records = evidence_by_variant.get(variant_id, [])
            strict_records = [r for r in records if r["strict_eligible"]]
            degraded_records = [r for r in records if not r["strict_eligible"]]
            if strict_records:
                variant_status = "direct_fixture"
            elif degraded_records:
                variant_status = "fixture_degraded"
            else:
                variant_status = "unverified"
            variant_evidence.append({
                "variant_id": variant_id,
                "status": variant_status,
                "fixture_ids": sorted({r["fixture_id"] for r in records}),
                "report_ids": sorted({r["report_id"] for r in records}),
                "strict_report_ids": sorted({r["report_id"] for r in strict_records}),
                "degraded_report_ids": sorted({r["report_id"] for r in degraded_records}),
                "issues": sorted({issue for r in records for issue in r["issues"]}),
            })

        # A numeric-only upgraded variant can use an explicit equivalence
        # proof when its operation discriminator is byte-identical to a base
        # variant that has repeat-verified direct evidence.  This does not
        # fabricate a runtime probe: the proof is recorded separately and
        # cites the version-locked semantic catalog.
        proof_ids: list[str] = []
        evidence_by_id = {item["variant_id"]: item for item in variant_evidence}
        fingerprint_by_id = {
            item["variant_id"]: item.get("discriminator_fingerprint")
            for item in deltas["variant_fingerprints"]
        }
        direct_ids = {
            item["variant_id"] for item in variant_evidence
            if item["status"] == "direct_fixture"
        }
        for variant_id, evidence_item in evidence_by_id.items():
            if evidence_item["status"] != "unverified" or not variant_id.endswith("_UPGRADE"):
                continue
            base_id = variant_id.removesuffix("_UPGRADE")
            if base_id not in direct_ids:
                continue
            if fingerprint_by_id.get(base_id) != fingerprint_by_id.get(variant_id):
                continue
            evidence_item["status"] = "equivalence_proven"
            evidence_item["issues"] = []
            proof_ids.append(f"equiv-upgrade:{sig}:{base_id}:{variant_id}")

        status_by_variant = {row["variant_id"]: row["status"] for row in variant_evidence}
        all_verified = all(status_by_variant.values()) and all(
            status in ("direct_fixture", "equivalence_proven")
            for status in status_by_variant.values())
        has_strict = any(status == "direct_fixture" for status in status_by_variant.values())
        has_degraded = any(status == "fixture_degraded" for status in status_by_variant.values())
        priority_required = bool(categories or random_operators or gaps or deltas["has_full_delta"])
        if non_combat_only:
            evidence_status = "out_of_scope"
            evidence_note = "only non-combat keyword operations"
        elif all_verified:
            evidence_status = "verified_all_variants"
            evidence_note = "every variant has strict direct evidence or an explicit equivalence proof"
        elif has_strict:
            evidence_status = "partial_fixture"
            evidence_note = "strict evidence exists for only a subset of variants"
        elif has_degraded:
            evidence_status = "fixture_degraded"
            evidence_note = "fixture exists but report quality/probability is not Reliable"
        elif priority_required:
            evidence_status = "fixture_gap"
            evidence_note = "semantic behavior requires a direct fixture or equivalence proof"
        else:
            evidence_status = "handler_only"
            evidence_note = "handler mapping exists without behavior evidence"

        evidence_policy = {
            "verified_all_variants": "fixture_covered",
            "partial_fixture": "fixture_partial",
            "fixture_degraded": "fixture_degraded",
            "fixture_gap": "fixture_gap",
            "handler_only": "simulator_handler_only",
            "out_of_scope": "out_of_scope",
        }[evidence_status]
        evidence_gaps = list(gaps)
        for row in variant_evidence:
            if row["status"] not in ("direct_fixture", "equivalence_proven"):
                evidence_gaps.append(f"variant_without_strict_evidence:{row['variant_id']}")
        for family in missing_families:
            evidence_gaps.append(f"fixture_without_strict_report:{family}")

        signatures.append({
            "signature_id": sig,
            "variant_count": len(variant_rows),
            "variant_ids": sorted({row["id"] for row in variant_rows}),
            "variant_fingerprints": deltas["variant_fingerprints"],
            "sample_text": variant_rows[0].get("source_text", "")[:80],
            "operations": ops,
            "handler_families": sorted(set(handlers)),
            "random_operators": sorted(set(random_operators)),
            "choice_contracts": sorted(set(choice_contracts)),
            "categories": categories,
            "evidence_status": evidence_status,
            "evidence_policy": evidence_policy,
            "evidence_note": evidence_note,
            "out_of_scope_non_combat": non_combat_only,
            "behavior_fixture_families": candidate_families,
            "missing_behavior_fixtures": missing_families,
            "variant_evidence": variant_evidence,
            "evidence_gaps": sorted(set(evidence_gaps)),
            "equivalence_proof_ids": proof_ids,
            "equivalence_required": not all_verified and not non_combat_only,
        })
        machine["signatures_total"] += 1

    status_counts = Counter(s["evidence_status"] for s in signatures)
    variant_status_counts = Counter(
        row["status"] for s in signatures for row in s["variant_evidence"]
    )
    strict_reports = sum(f["strict_report_count"] for f in fixture_evidence.values())
    degraded_reports = sum(f["degraded_report_count"] for f in fixture_evidence.values())
    strict_target_reports = sum(
        f["strict_target_report_count"] for f in fixture_evidence.values())
    degraded_target_reports = sum(
        f["degraded_target_report_count"] for f in fixture_evidence.values())
    direct_witness_strict = sum(
        1 for row in direct_witness_rows if row.get("main_reliable_eligible") is True)
    direct_witness_degraded = sum(
        1 for row in direct_witness_rows if row.get("main_reliable_eligible") is not True)
    machine_checks = {
        "unmapped_operation_kinds": machine["unmapped_kinds"],
        "unmapped_keywords": machine["unmapped_keywords"],
        "unknown_random_sources": machine["unknown_random_sources"],
        "select_operations_without_rule_id": machine["select_without_id"],
        "generate_templates_unresolvable": machine["template_unresolved"],
        "generate_templates_by_resolution": {
            "card": machine["templates_card"],
            "rule": machine["templates_rule"],
            "pool": machine["templates_pool"],
        },
        "signature_projection_collisions": sum(
            1 for values in projection_canonicals.values() if len(values) > 1),
    }
    source = {
        "cards": _relative(cards_path),
        "cards_sha256": _sha256(cards_path),
        "semantics": _relative(semantics_path),
        "semantics_sha256": _sha256(semantics_path),
    }
    summary = {
        "single_player_variants": len(single),
        "signatures": len(signatures),
        "out_of_scope_non_combat_signatures": status_counts["out_of_scope"],
        "machine_checks": machine_checks,
        "signatures_behavior_verified": status_counts["verified_all_variants"],
        "signatures_fixture_partial": status_counts["partial_fixture"],
        "signatures_fixture_degraded": status_counts["fixture_degraded"],
        "signatures_with_behavior_gap": sum(
            count for status, count in status_counts.items()
            if status not in ("verified_all_variants", "out_of_scope")),
        "signatures_simulator_handler_only": status_counts["handler_only"],
        "variants_behavior_verified": variant_status_counts["direct_fixture"],
        "variants_fixture_degraded": variant_status_counts["fixture_degraded"],
        "variants_without_behavior_evidence": variant_status_counts["unverified"],
        "behavior_reports_strict": strict_reports,
        "behavior_reports_degraded": degraded_reports,
        "behavior_reports_strict_with_target": strict_target_reports,
        "behavior_reports_degraded_with_target": degraded_target_reports,
        "reliable_eligible_signatures": status_counts["verified_all_variants"],
        "direct_witness_rows": len(direct_witness_rows),
        "direct_witness_strict_rows": direct_witness_strict,
        "direct_witness_degraded_rows": direct_witness_degraded,
    }
    manifest = {
        "schema_version": 1,
        "builder_version": "card-signature-evidence-v2",
        "game_version": GAME_VERSION,
        "version_lock": VERSION_LOCK,
        "source": source,
        "fixture_registry": list(fixture_evidence.values()),
        "direct_witness_manifest": _relative(direct_witness_path)
        if direct_witness_rows else None,
        "signature_evidence": [
            {
                "signature_id": s["signature_id"],
                "variant_ids": s["variant_ids"],
                "status": s["evidence_status"],
                "variant_evidence": s["variant_evidence"],
                "equivalence_proof_ids": s["equivalence_proof_ids"],
            }
            for s in signatures
        ],
        "equivalence_proofs": [
            {
                "proof_id": proof_id,
                "type": "numeric_upgrade_same_discriminator",
                "semantic_catalog": _relative(semantics_path),
                "semantic_catalog_sha256": _sha256(semantics_path),
            }
            for signature in signatures
            for proof_id in signature["equivalence_proof_ids"]
        ],
        "summary": {
            **summary,
            "fixtures": len(fixture_evidence),
            "reports": strict_reports + degraded_reports,
        },
        "reliable_eligibility": {
            "requires_all_variants_strict_direct_or_equivalence": True,
            "requires_probability_known_for_random_actions": True,
            "requires_zero_mismatches": True,
            "requires_exact_version_lock": True,
        },
    }
    manifest_errors: list[str] = []
    fixture_ids = {fixture["fixture_id"] for fixture in manifest["fixture_registry"]}
    if any(not fixture_id.startswith("p1-card-") for fixture_id in fixture_ids):
        manifest_errors.append("non_card_fixture_in_registry")
    for fixture in manifest["fixture_registry"]:
        if fixture["fixture_id"] not in CARD_FIXTURE_MAP:
            manifest_errors.append(f"fixture_not_in_allowlist:{fixture['fixture_id']}")
        for record in fixture["reports"]:
            if record["fixture_id"] != fixture["fixture_id"]:
                manifest_errors.append(f"report_fixture_mismatch:{record['report_id']}")
            if record["strict_eligible"] and not record["report_sha256"]:
                manifest_errors.append(f"strict_report_without_hash:{record['report_id']}")
    signature_ids = {signature["signature_id"] for signature in manifest["signature_evidence"]}
    if len(signature_ids) != len(manifest["signature_evidence"]):
        manifest_errors.append("duplicate_signature_evidence")
    manifest["validation"] = {
        "verdict": "pass" if not manifest_errors else "fail",
        "errors": sorted(set(manifest_errors)),
    }
    report = {
        "schema_version": 2,
        "builder_version": "card-signature-evidence-v2",
        "game_version": GAME_VERSION,
        "source": source,
        "version_lock": VERSION_LOCK,
        "summary": summary,
        "random_operator_registry": RANDOM_OPERATORS,
        "fixture_family_registry": sorted(CARD_FIXTURE_MAP),
        "card_fixture_map": CARD_FIXTURE_MAP,
        "direct_witness_manifest": _relative(direct_witness_path)
        if direct_witness_rows else None,
        "evidence_manifest": "data/card-semantic-evidence-manifest.json",
        "evidence_manifest_validation": manifest["validation"],
        "evidence_policy_definitions": {
            "fixture_covered": "all in-scope variants have strict direct evidence or an explicit equivalence proof",
            "fixture_partial": "strict direct evidence covers only some variants",
            "fixture_degraded": "fixture/report exists but quality or probability is degraded",
            "fixture_gap": "no strict direct evidence and a behavior witness is required",
            "simulator_handler_only": "no direct behavior witness; handler mapping only",
            "out_of_scope": "non-combat-only operation set",
        },
        "signatures": signatures,
        "equivalence_proofs": [
            {
                "proof_id": proof_id,
                "type": "numeric_upgrade_same_discriminator",
                "semantic_catalog": _relative(semantics_path),
                "semantic_catalog_sha256": _sha256(semantics_path),
            }
            for signature in signatures
            for proof_id in signature["equivalence_proof_ids"]
        ],
    }
    return report, manifest


def build(
    cards_path: Path,
    semantics_path: Path,
    fixtures_dir: Path | None = None,
    reports_dir: Path | None = None,
    repeat_manifest_path: Path | None = None,
) -> dict[str, Any]:
    """Build a report without writing files (used by unit tests and tooling)."""
    fixture_root = fixtures_dir or ROOT / "training" / "fixtures"
    report_root = reports_dir or DATA
    report, _ = _build(
        Path(cards_path), Path(semantics_path), Path(fixture_root), Path(report_root),
        Path(repeat_manifest_path) if repeat_manifest_path else None)
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cards", type=Path, default=ROOT.parent / "STS2BestChoice/data/cards/generated/0.111.0/cards.json")
    parser.add_argument("--semantics", type=Path, default=ROOT.parent / "STS2BestChoice/data/cards/generated/0.111.0/semantics.json")
    parser.add_argument("--fixtures", type=Path, default=ROOT / "training/fixtures")
    parser.add_argument("--reports", type=Path, default=DATA)
    parser.add_argument("--repeat-manifest", type=Path, default=None)
    parser.add_argument("--output", type=Path, default=DATA / "card-semantic-signature-report.json")
    parser.add_argument("--evidence-output", type=Path, default=DATA / "card-semantic-evidence-manifest.json")
    args = parser.parse_args()
    report, manifest = _build(
        args.cards, args.semantics, args.fixtures, args.reports, args.repeat_manifest)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.evidence_output.parent.mkdir(parents=True, exist_ok=True)
    args.evidence_output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    summary = report["summary"]
    print(f"card semantic signature report written: {args.output.name}")
    print(f"  variants={summary['single_player_variants']} signatures={summary['signatures']} "
          f"reliable_eligible={summary['reliable_eligible_signatures']}")
    print(f"  machine checks: {json.dumps(summary['machine_checks'], ensure_ascii=False)}")
    print(f"  evidence: strict_reports={summary['behavior_reports_strict']} "
          f"strict_with_target={summary['behavior_reports_strict_with_target']} "
          f"degraded_reports={summary['behavior_reports_degraded']} "
          f"behavior_gaps={summary['signatures_with_behavior_gap']}")
    print(f"  evidence manifest written: {args.evidence_output.name}")
    return 0 if manifest["validation"]["verdict"] == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
