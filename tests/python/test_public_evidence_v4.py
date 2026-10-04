"""Synthetic public-only v4 checks. Forward only; no backward or optimizer."""
from copy import deepcopy
import json
import hashlib
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))

from nosl.data_v4 import (RECORD_KIND, RECORD_SCHEMA, QUARANTINE_REASON, validate_record, validate_production_record,
                          validate_targets, validate_native_source_record)
from nosl.evidence_v4 import EVIDENCE_SCHEMA, FACT_FIELDS, _project_legacy_event, validate_evidence
from nosl.inference_v4 import BUNDLE_FORMAT, InferenceV4, implementation_fingerprint
from nosl.model_v3 import StudentV3
from nosl.model_v4 import StudentV4, SUMMARY_NAMES, evidence_features, public_evidence_features
from nosl.public_identity_v3 import public_input_digest as v3_digest
from nosl.public_identity_v4 import canonical_public_input, legacy_alias_digests, public_input_digest
from nosl.schema import HEADS, SchemaError
from nosl.schema_v2 import PLAN_HEADS
from nosl.schema_v3 import validate_public as validate_v3
from nosl.schema_v4 import MODEL_VERSION, PUBLIC_SCHEMA, _v3_mechanics_projection, load_config, loads, validate_config, validate_public


def old_public(name="native-entry-public-v2.json"):
    return json.loads((ROOT / "tests/python/fixtures" / name).read_text())


def fact(kind, **values):
    result = {"kind": "combat_fact", "factKind": kind, **{key: None for key in FACT_FIELDS}, "cards": [], "intents": []}
    result.update(values)
    return result


def offer(key, kind="skip", **values):
    result = {"key": key, "offerKind": kind, "isLocked": False, "price": None,
              "card": None, "relic": None, "potion": None, "gold": None, "serviceKey": None}
    result.update(values)
    return result


def declared_public(*, complete=True, prior=True, source=None):
    """Detached synthetic observations only. This is not a label migration API."""
    public = deepcopy(source) if source is not None else old_public()
    public["schema_version"] = PUBLIC_SCHEMA
    public["observation"].update(schema="nosl.public.v3", runContext={"schemaVersion": "nosl.public-run-context.v1",
        "actIndex": 0, "floor": 4, "combatEntryIndex": 1 if prior else 0, "completeFromRunStart": True})
    assets = json.loads(old_public()["observation"]["history"][1]["detail"])
    assets.pop("schemaVersion")
    events = []
    def append(owner, payload):
        index = len(events)
        events.append({"eventOrdinal": index, "ownerOrdinal": owner, "payload": deepcopy(payload)})
        return index
    def begin(owner, kind, floor):
        append(owner, {"kind": "owner_started", "ownerKind": kind, "actIndex": 0, "floor": floor,
                       "parentOwnerOrdinal": None, "completeFromOwnerStart": True})
    if complete: append(None, {"kind": "run_started", "character": "Silent", "ascension": 10, "assets": assets})
    else: append(None, {"kind": "gap", "reason": "run_start_not_observed"})
    owner = 0
    if prior:
        begin(owner, "combat", 1)
        append(owner, fact("started"))
        append(owner, fact("entry_assets", assets=assets))
        append(owner, fact("damage", targetSlot=-2, targetModel="player",
                           damage={"blocked": 2, "unblocked": 3, "overkill": 0, "hpAfter": 56, "killed": False}))
        append(owner, {"kind": "owner_ended", "outcome": "victory", "assets": assets})
        owner += 1
        begin(owner, "reward", 1)
        shown = append(owner, {"kind": "offers", "groups": [{"groupKind": "primary", "selectionMode": "choose_one",
            "alternativeToGroupIndex": None, "offers": [offer("card-0", "card", card=public["observation"]["hand"][0]), offer("skip")]}],
            "replacesOfferEventOrdinal": None})
        append(owner, {"kind": "option_chosen", "offerEventOrdinal": shown, "key": "skip"})
        append(owner, {"kind": "owner_ended", "outcome": "completed", "assets": assets})
        owner += 1
    begin(owner, "combat", 4)
    for event in public["observation"]["history"]: append(owner, _project_legacy_event(event))
    observation = deepcopy(public["observation"]); observation["history"] = []
    append(owner, {"kind": "combat_decision", "status": "player_decision", "historyThroughEventOrdinal": len(events) - 1,
                   "historyCompleteFromCombatStart": True, "observation": observation, "actions": public["candidate_actions"]})
    public["public_evidence"] = {"schemaVersion": EVIDENCE_SCHEMA, "completeFromRunStart": complete, "events": events}
    if public["controller_context"]["status"] != "inactive":
        public["controller_context"]["anchor"] = declared_public(complete=complete, prior=prior, source=public["controller_context"]["anchor"])
    return public


def unavailable_targets(public):
    return {"actions": [{"action_index": i, "quality": "unresolved", "masks": {head: False for head in HEADS},
                         **{head: None for head in HEADS}} for i in range(len(public["candidate_actions"]))],
            "pairwise": [], "equivalent_action_set": []}


class PublicEvidenceV4Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.config = load_config(ROOT / "configs/student.v4.engineering.json")

    def setUp(self): self.public = declared_public()

    def check_bad(self, mutate, pattern=None):
        value = deepcopy(self.public); mutate(value)
        with self.assertRaisesRegex(SchemaError, pattern or "."):
            validate_public(value, self.config)

    def review_public(self):
        path = os.environ.get("NOSL_V4_NATIVE_SAMPLE")
        return loads(Path(path).read_text())["public_input"] if path else deepcopy(self.public)

    @staticmethod
    def mirror_current(public, mutate):
        mutate(public["observation"])
        mutate(public["public_evidence"]["events"][-1]["payload"]["observation"])

    def test_explicit_envelope_and_unchanged_legacy_config(self):
        self.assertIs(validate_public(self.public, self.config), self.public)
        self.assertEqual(self.config["base_config"], json.loads((ROOT / "configs/student.v3.engineering.json").read_text()))
        base = _v3_mechanics_projection(self.public)
        validate_v3(base, self.config["base_config"])
        with self.assertRaises(SchemaError): validate_v3(self.public, self.config["base_config"])
        with self.assertRaises(SchemaError): validate_public(base, self.config)
        with self.assertRaises(SchemaError): validate_config(self.config["base_config"])
        self.check_bad(lambda p: p.update(public_evidence=None))
        self.check_bad(lambda p: p.update(schema_version="nosl.student.public.v3"))

    def test_explicit_past_gaps_preserve_current_history(self):
        public = declared_public(complete=False)
        validate_public(public, self.config)
        self.assertEqual(InferenceV4(self.config).predict(public)["status"], "MODEL_UNTRAINED")
        self.check_bad(lambda p: p["public_evidence"].update(completeFromRunStart=False), "completeness")
        public["public_evidence"]["completeFromRunStart"] = True
        with self.assertRaisesRegex(SchemaError, "completeness"): validate_public(public, self.config)

    def test_current_snapshot_actions_location_and_completeness_bind(self):
        self.check_bad(lambda p: p["public_evidence"]["events"][-1]["payload"]["observation"].update(hp=55), "root observation")
        self.check_bad(lambda p: p["public_evidence"]["events"][-1]["payload"]["actions"].reverse(), "legal candidates")
        self.check_bad(lambda p: p["public_evidence"]["events"][-1]["payload"].update(historyThroughEventOrdinal=1), "preceding owner history")
        self.check_bad(lambda p: p["public_evidence"]["events"][-1]["payload"].update(historyCompleteFromCombatStart=False), "completeness")
        self.check_bad(lambda p: p["public_evidence"]["events"][10]["payload"].update(floor=5), "location")

    def test_evidence_creature_hp_integer_and_maximum_invariants(self):
        original = self.review_public()
        for kind in ("enemies", "pets"):
            for field, value in (("hp", -1), ("hp", 1.5), ("maxHp", -1), ("maxHp", 2.5), ("hp", 11)):
                public = deepcopy(original)
                def mutate(observation):
                    if kind == "pets":
                        observation["pets"] = [{"slot": 255, "id": observation["enemies"][0]["id"],
                                                "hp": 10, "maxHp": 10, "block": 0, "powers": []}]
                    creature = observation[kind][0]
                    creature.update(hp=10, maxHp=10)
                    creature[field] = value
                self.mirror_current(public, mutate)
                with self.subTest(kind=kind, field=field, value=value):
                    with self.assertRaises(SchemaError): validate_public(public, self.config)
                    self.assertEqual(InferenceV4(self.config).predict(public)["status"], "INVALID_INPUT")
        # A past evidence decision is checked even when it cannot affect root
        # equality. Use the native sample's first snapshot when available.
        public = deepcopy(original)
        snapshot = next(e["payload"]["observation"] for e in public["public_evidence"]["events"] if e["payload"]["kind"] == "combat_decision")
        snapshot["enemies"][0].update(hp=11, maxHp=10)
        with self.assertRaisesRegex(SchemaError, "HP exceeds maximum"): validate_evidence(public["public_evidence"], self.config)

    def test_evidence_power_applier_slot_excludes_minus_one_on_every_creature(self):
        original = self.review_public()
        power = {"id": "StrengthPower", "amount": 1, "amountOnTurnStart": 0, "skipNextDurationTick": False,
                 "selectedCard": None, "selectedUpgrade": None, "applierSlot": -1}
        for kind in ("player", "enemies", "pets"):
            public = deepcopy(original)
            def mutate(observation):
                if kind == "player": observation["powers"] = [deepcopy(power)]
                elif kind == "enemies": observation["enemies"][0]["powers"] = [deepcopy(power)]
                else:
                    observation["pets"] = [{"slot": 255, "id": observation["enemies"][0]["id"], "hp": 10, "maxHp": 10,
                                            "block": 0, "powers": [deepcopy(power)]}]
            self.mirror_current(public, mutate)
            with self.subTest(kind=kind), self.assertRaisesRegex(SchemaError, "applierSlot"):
                validate_public(public, self.config)
            self.assertEqual(InferenceV4(self.config).predict(public)["status"], "INVALID_INPUT")

    def test_numeric_overflow_is_schema_error_and_inference_abstention(self):
        public = self.review_public()
        damage = next(e["payload"]["damage"] for e in public["public_evidence"]["events"]
                      if e["payload"]["kind"] == "combat_fact" and e["payload"]["factKind"] == "damage")
        damage["blocked"] = 10**1000
        with self.assertRaisesRegex(SchemaError, "numeric value exceeds"):
            validate_evidence(public["public_evidence"], self.config)
        with self.assertRaisesRegex(SchemaError, "numeric value exceeds"): validate_public(public, self.config)
        result = InferenceV4(self.config).predict(public)
        self.assertEqual(result["status"], "INVALID_INPUT"); self.assertIsNone(result["selected_action"])
        model = StudentV4(self.config)
        with patch.object(model.mechanics, "forward", side_effect=AssertionError("must reject before mechanics")):
            with self.assertRaises(SchemaError): model(public)
        public = self.review_public()
        self.mirror_current(public, lambda observation: observation.update(block=10**1000))
        with self.assertRaisesRegex(SchemaError, "numeric value exceeds"): validate_public(public, self.config)
        self.assertEqual(InferenceV4(self.config).predict(public)["status"], "INVALID_INPUT")
        config = deepcopy(self.config)
        config["base_config"]["base_config"]["base_config"]["learning_rate"] = 10**1000
        with self.assertRaisesRegex(SchemaError, "numeric value exceeds"): validate_config(config)
        targets = unavailable_targets(self.public)
        targets["actions"][0].update(quality="complete", value=10**1000)
        targets["actions"][0]["masks"]["value"] = True
        with self.assertRaisesRegex(SchemaError, "numeric value exceeds"): validate_targets(targets, self.public, self.config)

    def test_complete_combat_count_matches_only_when_both_prefixes_are_complete(self):
        original = self.review_public()
        validate_public(original, self.config)
        public = deepcopy(original)
        self.mirror_current(public, lambda observation: observation["runContext"].update(combatEntryIndex=999))
        with self.assertRaisesRegex(SchemaError, "combatEntryIndex contradicts"): validate_public(public, self.config)
        self.assertEqual(InferenceV4(self.config).predict(public)["status"], "INVALID_INPUT")
        # An explicitly missing run prefix cannot establish how many prior
        # combats were recorded elsewhere by the independent context channel.
        public["public_evidence"]["completeFromRunStart"] = False
        public["public_evidence"]["events"][0]["payload"] = {"kind": "gap", "reason": "run_start_not_observed"}
        validate_public(public, self.config)
        self.assertEqual(public["observation"]["runContext"]["combatEntryIndex"], 999)
        public = deepcopy(original)
        self.mirror_current(public, lambda observation: observation["runContext"].update(completeFromRunStart=False, combatEntryIndex=None))
        validate_public(public, self.config)
        self.assertIsNone(public["observation"]["runContext"]["combatEntryIndex"])

    def test_private_fields_and_unknown_types_rejected_recursively(self):
        paths = [(), ("observation",), ("public_evidence",), ("public_evidence", "events", 0),
                 ("public_evidence", "events", 0, "payload"), ("public_evidence", "events", 0, "payload", "assets"),
                 ("public_evidence", "events", 7, "payload", "groups", 0, "offers", 0)]
        for path in paths:
            def mutate(p):
                for key in path: p = p[key]
                p["privateSeed"] = 321
            with self.subTest(path=path): self.check_bad(mutate)
        self.check_bad(lambda p: p["public_evidence"]["events"][0]["payload"].update(kind="native_snapshot"))
        self.check_bad(lambda p: p["public_evidence"]["events"][4]["payload"].update(factKind="native_rng"))
        self.check_bad(lambda p: p["public_evidence"]["events"][4]["payload"].update(cards=[p["observation"]["hand"][0]]), "does not match")

    def test_duplicate_properties_nonfinite_and_encoded_history_rejected(self):
        for text in ('{"a":1,"a":2}', '{"nested":{"seed":1,"seed":2}}', '{"n":NaN}', '{"n":Infinity}', '{"n":1e400}'):
            with self.subTest(text=text), self.assertRaises(SchemaError): loads(text)
        event = deepcopy(self.public)
        detail = event["observation"]["history"][1]["detail"]
        event["observation"]["history"][1]["detail"] = detail.replace('"hp":56', '"hp":56,"hp":56')
        with self.assertRaisesRegex(SchemaError, "duplicate"): loads(json.dumps(event))
        with self.assertRaisesRegex(SchemaError, "duplicate"): validate_public(event, self.config)

    def test_ordinals_lifecycle_references_and_locked_choices(self):
        self.check_bad(lambda p: p["public_evidence"]["events"][2].update(eventOrdinal=3), "contiguous")
        self.check_bad(lambda p: p["public_evidence"]["events"][1].update(ownerOrdinal=2), "contiguous")
        self.check_bad(lambda p: p["public_evidence"]["events"][4].update(ownerOrdinal=99), "unknown or ended")
        self.check_bad(lambda p: p["public_evidence"]["events"][6]["payload"].update(parentOwnerOrdinal=0), "remain open")
        self.check_bad(lambda p: p["public_evidence"]["events"][8]["payload"].update(offerEventOrdinal=2), "same owner")
        self.check_bad(lambda p: p["public_evidence"]["events"][8]["payload"].update(key="not-displayed"), "visibly enabled")
        self.check_bad(lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][1].update(isLocked=True), "visibly enabled")
        self.check_bad(lambda p: p["public_evidence"]["events"][5]["payload"].update(outcome="interrupted"), "explicit gap")

    def test_missing_or_reordered_typed_history_cannot_claim_complete(self):
        def remove_draw(p):
            events = p["public_evidence"]["events"]
            del events[13]
            for i, event in enumerate(events): event["eventOrdinal"] = i
            events[-1]["payload"]["historyThroughEventOrdinal"] -= 1
        self.check_bad(remove_draw, "does not match")
        def reorder(p):
            events = p["public_evidence"]["events"]
            events[13]["payload"], events[15]["payload"] = events[15]["payload"], events[13]["payload"]
        self.check_bad(reorder, "does not match")

    def test_action_tokens_one_use_stale_replay_and_effect_order(self):
        evidence = deepcopy(self.public["public_evidence"])
        events = evidence["events"]; decision = events[-1]
        action = {"kind": "combat_action", "decisionEventOrdinal": decision["eventOrdinal"], "action": deepcopy(decision["payload"]["actions"][0])}
        events.append({"eventOrdinal": len(events), "ownerOrdinal": decision["ownerOrdinal"], "payload": action})
        validate_evidence(evidence, self.config)
        repeat = deepcopy(events[-1]); repeat["eventOrdinal"] += 1; events.append(repeat)
        with self.assertRaises(SchemaError): validate_evidence(evidence, self.config)
        events.pop(); events[-1]["payload"]["action"]["revision"] += 1
        with self.assertRaisesRegex(SchemaError, "not offered"): validate_evidence(evidence, self.config)
        events.pop()
        events.append({"eventOrdinal": len(events), "ownerOrdinal": decision["ownerOrdinal"], "payload": fact("player_turn_ended")})
        events.append({"eventOrdinal": len(events), "ownerOrdinal": decision["ownerOrdinal"], "payload": action})
        action["action"]["revision"] -= 1
        with self.assertRaisesRegex(SchemaError, "precede"): validate_evidence(evidence, self.config)

    def test_owner_missing_start_and_global_gaps(self):
        evidence = {"schemaVersion": EVIDENCE_SCHEMA, "completeFromRunStart": False, "events": [
            {"eventOrdinal": 0, "ownerOrdinal": None, "payload": {"kind": "gap", "reason": "run_start_not_observed"}},
            {"eventOrdinal": 1, "ownerOrdinal": 0, "payload": {"kind": "owner_started", "ownerKind": "combat", "actIndex": 0,
                "floor": 4, "parentOwnerOrdinal": None, "completeFromOwnerStart": False}}]}
        decision = deepcopy(self.public["public_evidence"]["events"][-1]["payload"])
        decision.update(historyThroughEventOrdinal=1, historyCompleteFromCombatStart=False)
        evidence["events"].append({"eventOrdinal": 2, "ownerOrdinal": 0, "payload": decision})
        with self.assertRaisesRegex(SchemaError, "requires explicit gap"): validate_evidence(evidence, self.config)
        evidence["events"].insert(2, {"eventOrdinal": 2, "ownerOrdinal": 0, "payload": {"kind": "gap", "reason": "owner_start_not_observed"}})
        evidence["events"][-1]["eventOrdinal"] = 3; decision["historyThroughEventOrdinal"] = 2
        validate_evidence(evidence, self.config)

    def test_offer_group_price_alternative_reroll_and_payload_guards(self):
        self.check_bad(lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0].update(groupKind="alternative"), "reference")
        self.check_bad(lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0].update(groupKind="reroll"), "previous offer")
        self.check_bad(lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][0].update(price=-1))
        self.check_bad(lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][0].update(gold=100), "does not match")

    def test_map_options_edges_and_unknown_icons(self):
        current, a, b = {"col": 0, "row": 0}, {"col": 0, "row": 1}, {"col": 1, "row": 1}
        payload = {"kind": "map", "current": current,
                   "nodes": [{"coordinate": current, "nodeType": "start"}, {"coordinate": a, "nodeType": "unknown"}, {"coordinate": b, "nodeType": "shop"}],
                   "edges": [{"from": current, "to": a}], "options": [{"coordinate": a, "isOrdinaryConnection": True}, {"coordinate": b, "isOrdinaryConnection": False}]}
        evidence = {"schemaVersion": EVIDENCE_SCHEMA, "completeFromRunStart": False, "events": [
            {"eventOrdinal": 0, "ownerOrdinal": None, "payload": {"kind": "gap", "reason": "run_start_not_observed"}},
            {"eventOrdinal": 1, "ownerOrdinal": 0, "payload": {"kind": "owner_started", "ownerKind": "map", "actIndex": 0, "floor": 0, "parentOwnerOrdinal": None, "completeFromOwnerStart": True}},
            {"eventOrdinal": 2, "ownerOrdinal": 0, "payload": payload},
            {"eventOrdinal": 3, "ownerOrdinal": 0, "payload": {"kind": "map_chosen", "offerEventOrdinal": 2, "coordinate": b}}]}
        validate_evidence(evidence, self.config)
        payload["options"][1]["isOrdinaryConnection"] = True
        with self.assertRaisesRegex(SchemaError, "connection"): validate_evidence(evidence, self.config)
        payload["options"][1]["isOrdinaryConnection"] = False
        payload["nodes"].append(deepcopy(payload["nodes"][0]))
        with self.assertRaisesRegex(SchemaError, "duplicate"): validate_evidence(evidence, self.config)

    def test_outside_options_cards_bundles_cancel_and_selection_order(self):
        choice = {"source": "ThinkingAhead", "min": 0, "max": 2, "cancelable": True,
                  "candidates": deepcopy(self.public["observation"]["hand"][:2]), "candidateOrder": "canonical_unordered_reveal",
                  "bundles": [deepcopy(self.public["observation"]["hand"][:1]), deepcopy(self.public["observation"]["hand"][1:2])]}
        evidence = {"schemaVersion": EVIDENCE_SCHEMA, "completeFromRunStart": False, "events": [
            {"eventOrdinal": 0, "ownerOrdinal": None, "payload": {"kind": "gap", "reason": "run_start_not_observed"}},
            {"eventOrdinal": 1, "ownerOrdinal": 0, "payload": {"kind": "owner_started", "ownerKind": "outside_choice", "actIndex": 0, "floor": 0, "parentOwnerOrdinal": None, "completeFromOwnerStart": True}},
            {"eventOrdinal": 2, "ownerOrdinal": 0, "payload": {"kind": "card_choice", "choice": choice}},
            {"eventOrdinal": 3, "ownerOrdinal": 0, "payload": {"kind": "cards_chosen", "offerEventOrdinal": 2, "selection": [1, 0], "cancelled": False}}]}
        validate_evidence(evidence, self.config)
        selected = evidence["events"][-1]["payload"]
        selected.update(selection=[], cancelled=True)
        validate_evidence(evidence, self.config)
        selected["selection"] = [0]
        with self.assertRaisesRegex(SchemaError, "cancelled"): validate_evidence(evidence, self.config)
        selected.update(selection=[0, 0], cancelled=False)
        with self.assertRaisesRegex(SchemaError, "duplicate"): validate_evidence(evidence, self.config)
        selected.update(selection=[2], cancelled=False)
        with self.assertRaisesRegex(SchemaError, "outside"): validate_evidence(evidence, self.config)
        evidence["events"][1]["payload"]["ownerKind"] = "rest"
        evidence["events"][2]["payload"] = {"kind": "options", "options": [{"key": "REST", "isLocked": False, "price": None}, {"key": "SMITH", "isLocked": True, "price": 4}]}
        evidence["events"][3]["payload"] = {"kind": "option_chosen", "offerEventOrdinal": 2, "key": "REST"}
        validate_evidence(evidence, self.config)
        evidence["events"][3]["payload"]["key"] = "SMITH"
        with self.assertRaisesRegex(SchemaError, "visibly enabled"): validate_evidence(evidence, self.config)

    def test_full_identity_semantics_and_split_aliases(self):
        original = public_input_digest(self.public)
        for mutate in (lambda p: p["public_evidence"]["events"][4]["payload"]["damage"].update(blocked=3),
                       lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][0].update(price=9),
                       lambda p: p["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"].reverse()):
            changed = deepcopy(self.public); mutate(changed); validate_public(changed, self.config)
            self.assertNotEqual(original, public_input_digest(changed))
            self.assertTrue(legacy_alias_digests(self.public) & legacy_alias_digests(changed))
        self.assertEqual(canonical_public_input(self.public)["public_evidence"]["events"][4]["payload"]["damage"]["blocked"], 2)
        self.assertIn(v3_digest(_v3_mechanics_projection(self.public)), legacy_alias_digests(self.public))
        changed = deepcopy(self.public)
        for actions in (changed["candidate_actions"], changed["public_evidence"]["events"][-1]["payload"]["actions"]):
            for action in actions: action["revision"] += 10
        self.assertEqual(original, public_input_digest(changed))

    def test_bounded_features_include_every_event_and_early_unselected_offer(self):
        root, anchor = public_evidence_features(self.public, self.config["evidence_hash_dim"])
        self.assertIsNone(anchor)
        summary, rows = root
        self.assertEqual(len(summary), len(SUMMARY_NAMES))
        self.assertEqual(len(rows), len(self.public["public_evidence"]["events"]))
        self.assertTrue(all(math.isfinite(v) and abs(v) <= 1 for v in summary + [x for row in rows for x in row]))
        changed = deepcopy(self.public)
        changed["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][0]["price"] = 17
        other, _ = public_evidence_features(changed, self.config["evidence_hash_dim"])
        self.assertNotEqual(rows[7], other[1][7])
        self.assertEqual(rows[8:], other[1][8:])
        config = deepcopy(self.config); config["evidence_max_events"] = len(rows) - 1
        with self.assertRaises(SchemaError): validate_public(self.public, config)

    def test_forward_finite_changed_evidence_and_legacy_weights_unchanged(self):
        torch.manual_seed(704); model = StudentV4(self.config).eval()
        state = {key: value.clone() for key, value in model.state_dict().items()}
        changed = deepcopy(self.public)
        changed["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][0]["price"] = 17
        with torch.no_grad(): baseline, output = model(self.public), model(changed)
        for key, value in baseline.items():
            if isinstance(value, dict): self.assertTrue(all(torch.isfinite(x).all() for x in value.values()))
            else: self.assertTrue(torch.isfinite(value).all(), key)
        self.assertFalse(torch.equal(baseline["value"], output["value"]))
        self.assertTrue(all(torch.equal(state[key], value) for key, value in model.state_dict().items()))
        self.assertTrue(all(p.grad is None for p in model.parameters()))
        legacy = StudentV3(self.config["base_config"]).eval()
        public = _v3_mechanics_projection(self.public)
        with torch.no_grad(): before = legacy(public); model(self.public); after = legacy(public)
        self.assertTrue(torch.equal(before["value"], after["value"]))
        with self.assertRaisesRegex(SchemaError, "legacy weights"): InferenceV4(self.config, legacy)

    def test_inference_never_promotes_forged_training_or_executes_forward(self):
        model = StudentV4(self.config)
        with patch.object(model, "forward", side_effect=AssertionError("must abstain")):
            self.assertEqual(InferenceV4(self.config, model).predict(self.public)["status"], "MODEL_UNTRAINED")
            fake = {"trained": True, "optimizer_steps": 1, "status": "PROMOTED", "calibrated": True}
            result = InferenceV4(self.config, model, fake).predict(self.public)
            self.assertEqual(result["status"], "MODEL_UNVALIDATED"); self.assertIsNone(result["selected_action"])

    def test_untrained_bundle_checksums_fingerprint_and_version_isolation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "config.json").write_text(json.dumps(self.config))
            torch.save(StudentV4(self.config).state_dict(), path / "weights.pt")
            digest = hashlib.sha256(json.dumps(self.config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
            manifest = {"format": BUNDLE_FORMAT, "public_schema": PUBLIC_SCHEMA, "model_version": MODEL_VERSION,
                        "trained": False, "optimizer_steps": 0, "config_sha256": digest,
                        "weights_sha256": hashlib.sha256((path / "weights.pt").read_bytes()).hexdigest(),
                        "implementation": implementation_fingerprint()}
            (path / "manifest.json").write_text(json.dumps(manifest))
            runner = InferenceV4.from_bundle(path)
            self.assertEqual(runner.predict(self.public)["status"], "MODEL_UNTRAINED")
            wrong = deepcopy(manifest); wrong["implementation"]["runtime"]["torch_version"] = "wrong"
            (path / "manifest.json").write_text(json.dumps(wrong))
            with self.assertRaisesRegex(SchemaError, "implementation/runtime"): InferenceV4.from_bundle(path)
            wrong = deepcopy(manifest); wrong["weights_sha256"] = "0" * 64
            (path / "manifest.json").write_text(json.dumps(wrong))
            with self.assertRaisesRegex(SchemaError, "checksum"): InferenceV4.from_bundle(path)
            torch.save(StudentV3(self.config["base_config"]).state_dict(), path / "weights.pt")
            manifest["weights_sha256"] = hashlib.sha256((path / "weights.pt").read_bytes()).hexdigest()
            (path / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaisesRegex(RuntimeError, "Missing key"): InferenceV4.from_bundle(path)

    def test_finite_anchors_keep_evidence_and_cannot_reset(self):
        # Only the public side of an existing mechanical fixture is used.
        public = declared_public(source=old_public("finite-hunt-record-v2.jsonl")["public_input"])
        validate_public(public, self.config)
        root, anchor = public_evidence_features(public, self.config["evidence_hash_dim"])
        self.assertEqual(root, anchor)
        self.assertIn(public_input_digest(public["controller_context"]["anchor"]), legacy_alias_digests(public))
        runner = InferenceV4(self.config)
        self.assertEqual(runner.predict(public)["status"], "MODEL_UNTRAINED")
        changed = deepcopy(public)
        for packet in (changed, changed["controller_context"]["anchor"]):
            packet["public_evidence"]["events"][7]["payload"]["groups"][0]["offers"][0]["price"] = 9
        validate_public(changed, self.config)
        self.assertEqual(runner.predict(changed)["status"], "INVALID_INPUT")
        self.assertNotEqual(public_input_digest(public), public_input_digest(changed))
        altered = deepcopy(public); altered["controller_context"]["anchor"]["public_evidence"]["events"][4]["payload"]["damage"]["blocked"] += 1
        with self.assertRaisesRegex(SchemaError, "prefix"): validate_public(altered, self.config)
        targets = unavailable_targets(public)
        targets["plan"] = {"label_scope": "unavailable", **{head: None for head in PLAN_HEADS},
                           "masks": {head: False for head in PLAN_HEADS}, "allocated_worlds": 0,
                           "success_completed_worlds": 0, "paired_completed_worlds": 0}
        validate_targets(targets, public, self.config)
        terminal = old_public("finite-hunt-terminal-v2.json")
        settled = runner.predict(terminal)
        self.assertEqual(settled["status"], "PLAN_FINISHED")
        self.assertEqual(settled["controller_context"]["anchor"]["public_evidence"], public["public_evidence"])
        self.assertEqual(runner.predict(public)["status"], "INVALID_INPUT")
        torch.manual_seed(77)
        with torch.no_grad(): output = StudentV4(self.config).eval()(public)
        self.assertTrue(all(torch.isfinite(v) for v in output["plan"].values()))

    def test_label_free_native_source_is_distinct_and_still_quarantined(self):
        record = {"schema_version": "nosl.natural-source.v4", "record_kind": "natural_raw_source_candidate",
                  "public_input": self.public, "targets": {"actions": [], "pairwise": [], "equivalent_action_set": []},
                  "audit_only": {"trainable": False, "teacher_label_count": 0, "actual_seed": 123}}
        self.assertIs(validate_native_source_record(record, self.config), record)
        with self.assertRaises(SchemaError): validate_record(record, self.config)
        with self.assertRaisesRegex(SchemaError, QUARANTINE_REASON): validate_production_record(record, self.config)
        self.assertEqual(record["targets"]["actions"], [])
        changed = deepcopy(record); changed["targets"] = unavailable_targets(self.public)
        with self.assertRaisesRegex(SchemaError, "label-free"): validate_native_source_record(changed, self.config)

    @unittest.skipUnless(os.environ.get("NOSL_V4_NATIVE_SAMPLE"), "optional independently generated native producer sample")
    def test_native_sample_boundary_forward_identity_and_quarantine(self):
        record = loads(Path(os.environ["NOSL_V4_NATIVE_SAMPLE"]).read_text())
        validate_native_source_record(record, self.config)
        public = record["public_input"]
        self.assertEqual(InferenceV4(self.config).predict(public)["status"], "MODEL_UNTRAINED")
        self.assertEqual(len(public_input_digest(public)), 64)
        with self.assertRaisesRegex(SchemaError, QUARANTINE_REASON): validate_production_record(record, self.config)
        torch.manual_seed(744)
        model = StudentV4(self.config).eval()
        with torch.no_grad(): output = model(public)
        self.assertTrue(all(torch.isfinite(output[head]).all() for head in HEADS))
        self.assertTrue(all(parameter.grad is None for parameter in model.parameters()))

    def test_targets_bind_full_input_and_all_production_rows_reject(self):
        targets = unavailable_targets(self.public)
        record = {"schema_version": RECORD_SCHEMA, "record_kind": RECORD_KIND, "public_input": self.public,
                  "targets": targets, "audit_only": {"trainable": False, "conditioned_public_input_digest": public_input_digest(self.public)}}
        validate_record(record, self.config)
        with self.assertRaisesRegex(SchemaError, QUARANTINE_REASON): validate_production_record(record, self.config)
        changed = deepcopy(record); changed["public_input"]["public_evidence"]["events"][4]["payload"]["damage"]["blocked"] += 1
        with self.assertRaisesRegex(SchemaError, "full v4 conditioning"): validate_record(changed, self.config)
        changed = deepcopy(record); changed["targets"]["actions"][0]["value"] = 0
        with self.assertRaisesRegex(SchemaError, "null"): validate_record(changed, self.config)
        changed = deepcopy(record); changed["audit_only"]["trainable"] = True
        with self.assertRaisesRegex(SchemaError, "trainable:false"): validate_record(changed, self.config)
        changed = deepcopy(record); changed.update(record_kind="native_tape_engineering", schema_version="nosl.native-tape.engineering.v1")
        with self.assertRaises(SchemaError): validate_production_record(changed, self.config)

    def test_cli_strict_json_and_inference_import_no_training_or_native_engine(self):
        environment = {**os.environ, "PYTHONPATH": str(ROOT / "python"), "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1", "OPENBLAS_NUM_THREADS": "1"}
        script = "import sys; import nosl.inference_v4; assert not any(x in sys.modules for x in ('nosl.train','nosl.train_v2','nosl.data','nosl.data_v4','clr')); print('standalone')"
        result = subprocess.run([sys.executable, "-c", script], env=environment, cwd=ROOT, capture_output=True, text=True, check=True)
        self.assertEqual(result.stdout.strip(), "standalone")
        overflow = deepcopy(self.public)
        overflow["public_evidence"]["events"][4]["payload"]["damage"]["blocked"] = 10**1000
        result = subprocess.run([sys.executable, "-m", "nosl.inference_v4", "--config", "configs/student.v4.engineering.json"],
            input=json.dumps(overflow) + '\n' + json.dumps(self.public) + '\n{"schema_version":"x","schema_version":"y"}\n',
            env=environment, cwd=ROOT, capture_output=True, text=True, check=True)
        rows = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual([r["status"] for r in rows], ["INVALID_INPUT", "MODEL_UNTRAINED", "INVALID_INPUT"])


if __name__ == "__main__": unittest.main()
