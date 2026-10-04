"""Full-v5 contracts under hard no-backward/no-optimizer guards.

Hypothetical committed receipts below are explicitly synthetic structural tests;
they do not claim any optimization, authentic labels or trained artifacts.
"""
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from full_policy_v5_fixtures import (config_fixture, manifest_fixture, protection_fixture, public_fixture,
    rebind, record_fixture, reviewed_fixture, session_fixture, training_fixture)
from nosl.data_policy_v5 import PolicyDatasetV5, supervision_coverage, validate_isolation, validate_record
from nosl.inference_policy_v5 import InferencePolicyV5, score_public
from nosl.inference_v5 import InferenceV5
from nosl.policy_applicability_v5 import resource_screen
from nosl.policy_v5 import (INFERENCE_SOURCES, action_policy_training_verified, digest, empty_progress,
    implementation_fingerprint as inference_fingerprint, merge_coverage, state_digest, validate_manifest,
    validate_output, validate_training_config, validate_training_evidence)
from nosl.protection_v5 import component_id, record_tokens
from nosl.schema import HEADS, SchemaError
from nosl.train_policy_v5 import (PolicyTrainingSessionV5, TRAINING_SOURCES, atomic_save, batch_loss,
    evaluate, execution_gate, freeze_inputs, implementation_fingerprint, train_bounded)


class FullPolicyV5NoLearningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): torch.set_num_threads(1)

    def setUp(self):
        # Optimizer construction is forbidden too, including validation paths.
        for target in ("torch.Tensor.backward", "torch.autograd.backward", "torch.autograd.grad",
                       "torch.optim.AdamW", "torch.nn.utils.clip_grad_norm_"):
            guard = patch(target, side_effect=AssertionError("no-learning contract test attempted learning"))
            guard.start(); self.addCleanup(guard.stop)
        context = torch.no_grad()
        context.__enter__(); self.addCleanup(lambda: context.__exit__(None, None, None))
        self.config = config_fixture()

    def test_full_record_strict_versions_and_source_receipts(self):
        record = record_fixture()
        self.assertIs(validate_record(record, self.config), record)
        for mutate in (
            lambda r: r.update(format="nosl.dataset.native-complete-map.candidate.v1"),
            lambda r: r["audit_only"].update(trainable=True),
            lambda r: r["objective"].update(public_conditioning_scope="v2_projection"),
            lambda r: r["objective"].update(independent_final_evaluation=False),
            lambda r: r["objective"].update(endpoint="BEFORE_SETTLEMENT"),
            lambda r: r["objective"].update(evaluator_source_sha256="bad"),
            lambda r: r["audit_only"].update(native_source_run_identity="f" * 64),
            lambda r: r["audit_only"].update(hidden_seed="forbidden"),
        ):
            changed = deepcopy(record); mutate(changed)
            with self.subTest(mutate=mutate), self.assertRaises(SchemaError): validate_record(changed, self.config)

    def test_native_candidate_preserves_uncalibrated_semantics_without_fit_eligibility(self):
        record = record_fixture(purpose="native-objective-candidate")
        record["objective"].update(objective_profile_status="candidate", objective_calibrated=False,
                                   calibration_evidence_sha256=None)
        self.assertIs(validate_record(record, self.config), record)
        self.assertFalse(record["audit_only"]["trainable"])
        self.assertTrue(record["targets"]["actions"][0]["masks"]["value"])
        with self.assertRaisesRegex(SchemaError, "producer_receipts_required"):
            PolicyDatasetV5([record], self.config, split="train", purpose="native-objective-candidate")
        record["objective"]["calibration_evidence_sha256"] = "f" * 64
        with self.assertRaisesRegex(SchemaError, "cannot_claim_calibration"): validate_record(record, self.config)

    def test_full_conditioning_and_target_digests_cannot_be_reused(self):
        record = record_fixture()
        record["public_input"]["public_evidence"]["events"][2]["payload"]["currentMap"]["nodes"][4]["nodeType"] = "rest"
        with self.assertRaisesRegex(SchemaError, "binding"): validate_record(record, self.config)
        record = record_fixture(); record["targets"]["actions"][0]["value"] -= 1
        with self.assertRaisesRegex(SchemaError, "binding"): validate_record(record, self.config)

    def test_accounting_masks_unresolved_and_weight_guards(self):
        record = record_fixture()
        for mutate in (
            lambda r: r["targets"]["actions"][0].pop("allocated_worlds"),
            lambda r: r["targets"]["actions"][0].update(completed_worlds=3, truncated_worlds=1),
            lambda r: r["targets"]["actions"][0].update(quality="objective_value_unresolved"),
            lambda r: r["targets"]["actions"][0].update(sample_weight=0.),
            lambda r: r["targets"]["actions"][0]["masks"].update(value=False),
        ):
            changed = deepcopy(record); mutate(changed); rebind(changed)
            with self.subTest(mutate=mutate), self.assertRaises(SchemaError): validate_record(changed, self.config)

    def test_zero_weight_and_disabled_loss_do_not_count_as_consumed_objective(self):
        record = record_fixture()
        record["targets"].update(pairwise=[], equivalent_action_set=[])
        for row in record["targets"]["actions"]: row["sample_weight"] = 0.
        validate_record(rebind(record), self.config)
        coverage = supervision_coverage(record, self.config)
        self.assertEqual(coverage["action_policy"]["eligible_roots"], 0)
        self.assertEqual(coverage["action_heads"]["value"]["loss_rows"], 0)
        cfg = deepcopy(self.config)
        weights = cfg["base_config"]["base_config"]["base_config"]["base_config"]["loss_weights"]
        weights.update(value=0., pairwise=0., equivalent=0.)
        self.assertEqual(supervision_coverage(record_fixture(), cfg)["action_policy"]["eligible_roots"], 0)

    def test_data_copies_are_detached_and_internal_or_file_drift_is_rejected(self):
        records = [record_fixture()]
        dataset = PolicyDatasetV5(records, self.config, split="train", purpose="engineering-fixture")
        records[0]["targets"]["actions"][0]["value"] -= 9
        retrieved = dataset.records; retrieved[0]["targets"]["actions"][0]["value"] -= 8
        self.assertEqual(dataset.records[0]["targets"]["actions"][0]["value"], -5.)
        dataset._records[0]["targets"]["actions"][0]["value"] -= 1
        with self.assertRaisesRegex(SchemaError, "changed"): dataset.verify_integrity()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fresh.jsonl"
            path.write_text(json.dumps(record_fixture()) + "\n")
            dataset = PolicyDatasetV5.from_jsonl(path, self.config, split="train", purpose="engineering-fixture")
            path.write_text(path.read_text() + "\n")
            with self.assertRaisesRegex(SchemaError, "file_changed"): dataset.verify_integrity()
        with self.assertRaisesRegex(SchemaError, "train_or_validation_only"):
            PolicyDatasetV5([record_fixture()], self.config, split="test", purpose="engineering-fixture")

    def test_source_and_public_alias_isolation_before_filtering(self):
        session = session_fixture()
        train = session.datasets["train"].records[0]
        tokens = sorted(record_tokens(train))
        protection = protection_fixture()
        protection["components"][component_id(tokens)] = {"split": "test", "tokens": tokens}
        with self.assertRaisesRegex(SchemaError, "previously_observed"):
            validate_isolation(session.datasets, protection)
        # Renaming the visible group cannot hide the actual native source seed.
        other = record_fixture(8)
        for key in ("actual_seed", "native_source_run_identity"):
            other["audit_only"][key] = train["audit_only"][key]
        datasets = {**session.datasets, "validation": PolicyDatasetV5([other], self.config,
                    split="validation", purpose="engineering-fixture")}
        with self.assertRaisesRegex(SchemaError, "source_or_public_alias_overlap"):
            validate_isolation(datasets, protection_fixture())
        # A graph-only difference retains old public aliases; it cannot cross splits.
        other = record_fixture(8); other["public_input"] = deepcopy(train["public_input"])
        other["public_input"]["public_evidence"]["events"][2]["payload"]["currentMap"]["nodes"][4]["nodeType"] = "rest"
        datasets["validation"] = PolicyDatasetV5([rebind(other)], self.config, split="validation", purpose="engineering-fixture")
        with self.assertRaisesRegex(SchemaError, "source_or_public_alias_overlap"):
            validate_isolation(datasets, protection_fixture())

    def test_full_losses_forward_and_parameters_unchanged(self):
        session = session_fixture()
        before = state_digest(session.model.state_dict())
        loss, terms = batch_loss(session.model, session.datasets["train"].records, session.config)
        self.assertTrue(torch.isfinite(loss)); self.assertGreater(float(loss), 0.)
        for key in ("value", "pairwise", "equivalent", "win_probability", "hp_distribution"):
            self.assertGreater(terms[0][key], 0.)
        self.assertEqual(state_digest(session.model.state_dict()), before)
        self.assertEqual(session.progress, empty_progress())
        self.assertIsNone(session._optimizer)
        self.assertTrue(all(parameter.grad is None for parameter in session.model.parameters()))

    def test_mixed_objective_profiles_are_rejected_but_per_root_evaluation_may_vary(self):
        session = session_fixture()
        original = session.datasets["validation"].records
        for key in ("objective_spec_sha256", "evaluator_source_sha256", "calibration_evidence_sha256", "continuation_policy_id"):
            records = deepcopy(original); records[0]["objective"][key] = "f" * 64
            datasets = {**session.datasets, "validation": PolicyDatasetV5(records, self.config,
                        split="validation", purpose="engineering-fixture")}
            with self.subTest(key=key), self.assertRaisesRegex(SchemaError, "mixed_objective"):
                freeze_inputs(datasets, self.config, session.training, session.protection)
        records = deepcopy(original); records[0]["objective"]["evaluation_design_sha256"] = "f" * 64
        datasets["validation"] = PolicyDatasetV5(records, self.config, split="validation", purpose="engineering-fixture")
        freeze_inputs(datasets, self.config, session.training, session.protection)

    def test_all_v5_validation_happens_before_model_projection(self):
        session = session_fixture()
        public = public_fixture()
        public["public_evidence"]["events"][2]["payload"]["currentMap"]["rngState"] = "hidden"
        with patch.object(session.model.mechanics, "forward", side_effect=AssertionError("must validate before projection")):
            with self.assertRaises(SchemaError): score_public(session.model, public, session.config)
            record = session.datasets["train"].records[0]; record["public_input"] = public
            with self.assertRaises(SchemaError): batch_loss(session.model, [record], session.config)

    def test_loss_rejects_nonfinite_mask_and_ranking_outputs(self):
        session = session_fixture(); public = session.datasets["train"].records[0]["public_input"]
        output = session.model(public)
        for mutate in (
            lambda o: o["value"].fill_(float("nan")),
            lambda o: o.update(legal_mask=~o["legal_mask"]),
            lambda o: o["ranking_score"].fill_(float("inf")),
            lambda o: o.update(hp_distribution=o["hp_distribution"][:, :2]),
            lambda o: o["plan"].update(extra_net_hp_loss=torch.tensor(float("nan"))),
        ):
            changed = deepcopy(output); mutate(changed)
            with self.subTest(mutate=mutate), self.assertRaises(SchemaError): validate_output(changed, public, self.config)

    def test_score_public_never_selects_and_keeps_illegal_heads_null(self):
        session = session_fixture(); public = public_fixture(illegal=True)
        session.model.train()
        result = score_public(session.model, public, self.config)
        self.assertIsNone(result["selected_action"])
        self.assertEqual(result["status"], "UNCALIBRATED_SCORES")
        self.assertTrue(session.model.training)
        for head in HEADS: self.assertIsNone(result["predictions"][-1][head])
        self.assertAlmostEqual(sum(result["predictions"][0]["hp_distribution"]), 1., places=5)
        self.assertFalse(result["probabilities_calibrated"])

    def test_default_untrained_and_auxiliary_inference_are_unchanged(self):
        session = session_fixture(); public = public_fixture()
        with patch.object(session.model, "forward", side_effect=AssertionError("default must abstain")):
            self.assertEqual(InferencePolicyV5(self.config, session.model).predict(public)["status"], "MODEL_UNTRAINED")
            self.assertEqual(InferenceV5(self.config, session.model,
                {"trained": True, "optimizer_steps": 3}).predict(public)["status"], "MODEL_UNVALIDATED")
        with self.assertRaises(SchemaError):
            InferencePolicyV5(self.config, session.model, {"format": "nosl.student.bundle.v5", "trained": True})

    def test_synthetic_receipt_exercises_experimental_selection_and_default_abstention(self):
        session = session_fixture(purpose="bounded-objective-pilot")
        manifest = manifest_fixture(session)
        public = session.datasets["train"].records[0]["public_input"]
        with patch.object(session.model, "forward", side_effect=AssertionError("default must abstain")):
            self.assertEqual(InferencePolicyV5(self.config, session.model, manifest).predict(public)["status"], "MODEL_UNVALIDATED")
        runner = InferencePolicyV5(self.config, session.model, manifest, allow_experimental=True)
        result = runner.predict(public)
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertTrue(public["legal_mask"][result["selected_index"]])
        self.assertEqual(result["selected_action"], public["candidate_actions"][result["selected_index"]])
        self.assertIsNone(result["plan_predictions"])
        self.assertEqual(session.progress["optimizer_steps"], 0)  # fixture did not train

    def test_resource_screen_checks_v5_gold_max_hp_cards_and_after_combat_relic(self):
        from test_public_map_v5 import public_v5
        session = session_fixture(purpose="bounded-objective-pilot")
        manifest = manifest_fixture(session)
        for field, value, code in (("gold", 100, "UNPRICED_OBSERVED_GOLD_CHANGE"),
                                   ("maxHp", 71, "UNPRICED_OBSERVED_MAX_HP_CHANGE")):
            public = public_fixture(); public["observation"][field] = value
            public = public_v5(source=public)
            result = InferencePolicyV5(self.config, session.model, manifest, allow_experimental=True).predict(public)
            self.assertEqual(result["status"], "POLICY_INAPPLICABLE")
            self.assertIn(code, [b["code"] for b in result["applicability_guard"]["blockers"]])
            self.assertIsNone(result["selected_action"])
        for card in ("Feed", "TheHunt", "HandOfGreed", "Alchemize"):
            public = public_fixture(); public["observation"]["hand"][0]["id"] = card
            if card == "TheHunt": public["observation"]["hand"][0]["publicState"]["pendingCardRewards"] = "0"
            public = public_v5(source=public)
            with self.subTest(card=card):
                blockers = resource_screen(public, self.config)["blockers"]
                self.assertIn(card, next(b["card_ids"] for b in blockers if b["code"] == "UNPRICED_KNOWN_RESOURCE_CARD"))
        public = public_fixture()
        public["observation"]["relics"][0] = "ChosenCheese"
        public["observation"]["relicStates"][0]["id"] = "ChosenCheese"
        public = public_v5(source=public)
        self.assertIn("UNPRICED_KNOWN_RESOURCE_RELIC", [b["code"] for b in resource_screen(public, self.config)["blockers"]])
        self.assertFalse(resource_screen(public_fixture(), self.config)["certifies_full_applicability"])

    def test_net_cancelled_objective_training_does_not_authorize_fresh_potion_action(self):
        from test_public_map_v5 import public_v5
        record = record_fixture(1)
        public = record["public_input"]
        public["observation"]["potions"][0] = "FirePotion"
        public["candidate_actions"].append({"revision": 0, "kind": "potion", "slot": 0, "target": 0, "selection": None})
        public["legal_mask"].append(True)
        record["public_input"] = public_v5(source=public)
        row = deepcopy(record["targets"]["actions"][0]); row.update(action_index=len(public["candidate_actions"]) - 1,
            value=0., potion_net_change=0.)
        record["targets"]["actions"].append(row); rebind(record)
        datasets = {"train": PolicyDatasetV5([record], self.config, split="train", purpose="engineering-fixture"),
            "validation": PolicyDatasetV5([record_fixture(7)], self.config, split="validation", purpose="engineering-fixture")}
        session = PolicyTrainingSessionV5(datasets, self.config, training_fixture(self.config), protection_fixture())
        session._synthetic_receipt_purpose = "bounded-objective-pilot"
        manifest = manifest_fixture(session)
        self.assertTrue(action_policy_training_verified(manifest, self.config, record["public_input"]))
        runner = InferencePolicyV5(self.config, session.model, manifest, allow_experimental=True)
        with patch.object(session.model, "forward", side_effect=AssertionError("screen must abstain before forward")):
            result = runner.predict(record["public_input"])
        self.assertEqual(result["status"], "POLICY_INAPPLICABLE")
        self.assertEqual(len(record["public_input"]["candidate_actions"]), len(public["candidate_actions"]))
        self.assertIsNone(result["selected_action"])

    def test_past_potion_or_discard_remains_blocked_after_inventory_is_empty(self):
        from test_public_map_v5 import public_v5
        for event in ({"kind": "potion_used", "detail": "FirePotion"},
                      {"kind": "action", "detail": json.dumps({"revision": 0, "kind": "discard_potion", "slot": 0,
                                                               "target": -1, "selection": None})}):
            public = public_fixture()
            if event["kind"] == "action":
                action = json.loads(event["detail"])
                events = public["public_evidence"]["events"]
                prior = events[-1]
                prior["payload"]["observation"]["potions"][0] = "FirePotion"
                prior["payload"]["actions"].append(deepcopy(action))
                public["observation"]["history"].append(event)
                for candidate in public["candidate_actions"]: candidate["revision"] += 1
                events.append({"eventOrdinal": len(events), "ownerOrdinal": prior["ownerOrdinal"],
                    "payload": {"kind": "combat_action", "decisionEventOrdinal": prior["eventOrdinal"], "action": action}})
                observed = deepcopy(public["observation"]); observed["history"] = []
                events.append({"eventOrdinal": len(events), "ownerOrdinal": prior["ownerOrdinal"],
                    "payload": {"kind": "combat_decision", "status": "player_decision", "historyThroughEventOrdinal": len(events) - 1,
                        "historyCompleteFromCombatStart": True, "observation": observed, "actions": deepcopy(public["candidate_actions"])}})
            else:
                public["observation"]["history"].append(event)
                public = public_v5(source=public)
            self.assertIn("UNPRICED_PUBLIC_POTION_HISTORY", [b["code"] for b in resource_screen(public, self.config)["blockers"]])

    def test_auxiliary_or_finite_only_consumed_receipt_never_selects_actions(self):
        for kwargs in ({"auxiliary": True}, {"finite": True}):
            session = session_fixture(purpose="bounded-objective-pilot", **kwargs)
            manifest = manifest_fixture(session)
            self.assertFalse(action_policy_training_verified(manifest, session.config, public_fixture()))
            runner = InferencePolicyV5(session.config, session.model, manifest, allow_experimental=True)
            self.assertEqual(runner.predict(public_fixture())["status"], "ACTION_POLICY_UNVALIDATED")

    def test_finite_anchor_diagnostics_and_controller_never_select(self):
        session = session_fixture(purpose="bounded-objective-pilot", finite=True)
        manifest = manifest_fixture(session)
        runner = InferencePolicyV5(session.config, session.model, manifest, allow_experimental=True)
        public = session.datasets["train"].records[0]["public_input"]
        result = runner.predict(public)
        self.assertEqual(result["status"], "PLAN_POLICY_UNVALIDATED")
        self.assertIsNone(result["selected_action"])
        self.assertIsNotNone(result["plan_predictions"])
        self.assertEqual(runner.predict(public_fixture())["status"], "INVALID_INPUT")
        changed = deepcopy(public)
        changed["controller_context"]["anchor"]["public_evidence"]["events"][2]["payload"]["currentMap"]["nodes"][4]["nodeType"] = "rest"
        self.assertEqual(runner.predict(changed)["status"], "INVALID_INPUT")

    def test_unconsumed_plan_and_auxiliary_heads_are_null_and_pairwise_score_has_no_absolute_units(self):
        action_session = session_fixture(purpose="bounded-objective-pilot")
        runner = InferencePolicyV5(self.config, action_session.model, manifest_fixture(action_session), allow_experimental=True)
        plan_result = runner.predict(record_fixture(2, finite=True)["public_input"])
        self.assertEqual(plan_result["status"], "PLAN_POLICY_UNVALIDATED")
        self.assertTrue(all(value is None for value in plan_result["plan_predictions"].values()))
        self.assertTrue(all(value is False for value in plan_result["plan_prediction_masks"].values()))
        config = deepcopy(self.config)
        weights = config["base_config"]["base_config"]["base_config"]["base_config"]["loss_weights"]
        for head in [*HEADS, "equivalent"]: weights[head] = 0.
        datasets = {split: PolicyDatasetV5([record_fixture(tag)], config, split=split, purpose="engineering-fixture")
                    for split, tag in (("train", 1), ("validation", 7))}
        session = PolicyTrainingSessionV5(datasets, config, training_fixture(config), protection_fixture())
        session._synthetic_receipt_purpose = "bounded-objective-pilot"
        runner = InferencePolicyV5(config, session.model, manifest_fixture(session), allow_experimental=True)
        result = runner.predict(public_fixture())
        self.assertEqual(result["status"], "EXPERIMENTAL_UNCALIBRATED")
        self.assertEqual(result["score_semantics"], "ranking_only_no_absolute_utility_interpretation")
        self.assertTrue(all(row["score"] is not None for row in result["predictions"]))
        self.assertTrue(all(row[head] is None for row in result["predictions"] for head in HEADS))

    def test_committed_ledger_recomputes_coverage_rejects_replay_and_wrong_model(self):
        session = session_fixture(purpose="bounded-objective-pilot")
        manifest = manifest_fixture(session)
        frozen, progress = manifest["frozen_inputs"], manifest["supervision_progress"]
        for mutate in (
            lambda p: p.update(optimizer_steps=True),
            lambda p: p["consumed"]["action_heads"]["value"].update(loss_rows=1000),
            lambda p: p["committed_batches"][0]["records"].append(p["committed_batches"][0]["records"][0]),
            lambda p: p["committed_batches"][0].update(records=["f" * 64]),
            lambda p: p["committed_batches"][0].update(after_state_sha256="f" * 64),
            lambda p: p.update(action_policy_optimizer_steps=0),
        ):
            changed = deepcopy(progress); mutate(changed)
            with self.subTest(mutate=mutate), self.assertRaises(SchemaError):
                validate_training_evidence(frozen, changed, session.config, final_state_sha256=manifest["state_sha256"])
        public = public_fixture()
        public["candidate_actions"][0]["kind"] = "choose"
        self.assertFalse(action_policy_training_verified(manifest, session.config, public))

    def test_manifest_rejects_fake_promotion_and_unbound_review(self):
        session = session_fixture(purpose="bounded-objective-pilot")
        manifest = manifest_fixture(session)
        for mutate in (
            lambda m: m.update(status="PROMOTED", promoted=True, calibrated=True),
            lambda m: m.update(authorization=None),
            lambda m: m.update(config_sha256="f" * 64),
            lambda m: m["implementation"]["source_sha256"].update({"python/nosl/model_v5.py": "f" * 64}),
            lambda m: m["frozen_inputs"]["review"].update(inputs_sha256="f" * 64),
        ):
            changed = deepcopy(manifest); mutate(changed)
            with self.subTest(mutate=mutate), self.assertRaises(SchemaError):
                validate_manifest(changed, session.config, session.model.state_dict())

    def test_inference_detects_model_manifest_and_output_drift(self):
        session = session_fixture(purpose="bounded-objective-pilot")
        runner = InferencePolicyV5(session.config, session.model, manifest_fixture(session), allow_experimental=True)
        public = public_fixture()
        output = session.model(public); output["ranking_score"][0] = float("inf")
        with patch.object(session.model, "forward", return_value=output):
            self.assertEqual(runner.predict(public)["status"], "MODEL_ERROR")
        next(session.model.parameters()).fill_(float("nan"))
        self.assertEqual(runner.predict(public)["status"], "MODEL_ERROR")

    def test_external_review_and_authorization_are_separate_exact_gates(self):
        engineering = session_fixture()
        frozen, authorization = reviewed_fixture(engineering)
        self.assertFalse(execution_gate(frozen, authorization)["accepted"])
        session = session_fixture(purpose="bounded-objective-pilot")
        frozen, authorization = reviewed_fixture(session)
        self.assertFalse(execution_gate(session.frozen, authorization)["accepted"])
        self.assertFalse(execution_gate(frozen)["accepted"])
        self.assertTrue(execution_gate(frozen, authorization)["accepted"])
        changed = deepcopy(frozen); changed["training_config"]["max_optimizer_steps"] += 1
        self.assertFalse(execution_gate(changed, authorization)["accepted"])
        for kwargs in ({}, {"execute": True}):
            with self.assertRaises(SchemaError): session.train_next(**kwargs)
        self.assertIsNone(session._optimizer)
        self.assertEqual(session.progress, empty_progress())

    def test_loop_rejects_before_model_backward_optimizer_or_directory_creation(self):
        session = session_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "run"
            with self.assertRaises(SchemaError): train_bounded(session, path, execute=True)
            self.assertFalse(path.exists())

    def test_zero_step_checkpoint_roundtrip_and_failed_restore_are_atomic(self):
        session = session_fixture()
        state = session.checkpoint()
        self.assertIsNone(state["optimizer"]); self.assertEqual(state["progress"], empty_progress())
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "checkpoint.pt"
            atomic_save(state, path)
            restored = session_fixture()
            self.assertEqual(restored.restore(path), empty_progress())
            self.assertEqual(state_digest(restored.model.state_dict()), state_digest(session.model.state_dict()))
            before = state_digest(restored.model.state_dict())
            for mutate in (
                lambda s: s.update(cursor=1),
                lambda s: s.update(optimizer={"state": {}, "param_groups": []}),
                lambda s: s["frozen_inputs"]["training_config"].update(max_optimizer_steps=4),
                lambda s: s["model"][next(iter(s["model"]))].fill_(float("nan")),
                lambda s: s.update(torch_rng=torch.zeros(1)),
            ):
                changed = deepcopy(state); mutate(changed); atomic_save(changed, path)
                with self.subTest(mutate=mutate), self.assertRaises(SchemaError): restored.restore(path)
                self.assertEqual(state_digest(restored.model.state_dict()), before)

    def test_zero_step_bundle_roundtrip_checks_checksum_tensor_schema_and_version(self):
        session = session_fixture()
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "bundle"
            manifest = session.export_bundle(path)
            self.assertFalse(manifest["trained"]); self.assertEqual(manifest["optimizer_steps"], 0)
            runner = InferencePolicyV5.from_bundle(path, allow_experimental=True)
            self.assertEqual(runner.predict(public_fixture())["status"], "MODEL_UNTRAINED")
            self.assertEqual(state_digest(runner.model.state_dict()), state_digest(session.model.state_dict()))
            with self.assertRaisesRegex(SchemaError, "new_bundle_directory"): session.export_bundle(path)
            self.assertEqual(session.export_bundle(path, reuse_existing=True), manifest)
            with self.assertRaises(SchemaError): InferenceV5.from_bundle(path)
            (path / "weights.pt").write_bytes((path / "weights.pt").read_bytes() + b"changed")
            with self.assertRaisesRegex(SchemaError, "checksum"): InferencePolicyV5.from_bundle(path)
            with self.assertRaisesRegex(SchemaError, "checksum"): session.export_bundle(path, reuse_existing=True)

    def test_source_fingerprints_include_complete_read_and_loss_closure(self):
        for name in ("data_v5.py", "loss_v5.py", "train.py", "native_pilot.py", "protection_v5.py",
                     "native_policy_v5.py", "data_policy_v5.py", "train_policy_v5.py", "schema_v5.py", "public_identity_v5.py"):
            self.assertIn(name, TRAINING_SOURCES)
        inference = inference_fingerprint()
        self.assertEqual(set(inference["source_sha256"]), {"python/nosl/" + p for p in INFERENCE_SOURCES})
        self.assertNotIn("python/nosl/train_policy_v5.py", inference["source_sha256"])
        identity = implementation_fingerprint()
        self.assertEqual(set(identity["source_sha256"]), {"python/nosl/" + p for p in TRAINING_SOURCES})
        self.assertNotIn(str(ROOT), json.dumps(identity))
        session = session_fixture()
        with patch("nosl.train_policy_v5.implementation_fingerprint", return_value={"changed": True}):
            with self.assertRaises(SchemaError): session.verify_integrity()

    def test_validation_is_no_grad_and_rejects_train_or_test_metrics(self):
        session = session_fixture()
        before = state_digest(session.model.state_dict())
        result = evaluate(session.model, session.datasets["validation"])
        self.assertGreater(result["mean_loss"], 0.)
        self.assertFalse(result["test_evaluated"]); self.assertFalse(result["student_rollout_performance_measured"])
        self.assertEqual(before, state_digest(session.model.state_dict()))
        with self.assertRaises(SchemaError): evaluate(session.model, session.datasets["train"])

    def test_training_config_and_cli_default_no_fit(self):
        training = training_fixture(self.config)
        for mutate in (lambda c: c.update(max_epochs=2), lambda c: c.update(formal_training=True),
                       lambda c: c.update(student_config_sha256="f" * 64), lambda c: c.update(batch_size=True)):
            changed = deepcopy(training); mutate(changed)
            with self.assertRaises(SchemaError): validate_training_config(changed, self.config)
        # Run the CLI in-process so all no-learning guards cover it.
        from contextlib import redirect_stdout
        from io import StringIO
        from nosl.train_policy_v5 import main
        with tempfile.TemporaryDirectory() as temporary:
            config_path, training_path = Path(temporary) / "config.json", Path(temporary) / "training.json"
            config_path.write_text(json.dumps(self.config)); training_path.write_text(json.dumps(training))
            output = StringIO()
            with redirect_stdout(output): code = main(["--student-config", str(config_path), "--training-config", str(training_path)])
            self.assertEqual(code, 0)
            result = json.loads(output.getvalue())
            self.assertEqual(result["mode"], "readiness"); self.assertEqual(result["backward_calls"], 0)
            self.assertFalse(result["weights_written"]); self.assertEqual(result["status"], "FIT_BLOCKED")

    def test_standalone_inference_imports_no_training_data_or_simulator(self):
        # This subprocess only imports the inference module; it runs no model.
        code = "import sys; import nosl.inference_policy_v5; assert not any(k in sys.modules for k in ('nosl.train_policy_v5','nosl.data_policy_v5','nosl.train','nosl.data_v5','nosl.native_v5'))"
        result = subprocess.run([sys.executable, "-c", code], cwd=ROOT,
            env={**__import__("os").environ, "PYTHONPATH": str(ROOT / "python")}, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__": unittest.main()
