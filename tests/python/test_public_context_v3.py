"""V3 engineering boundaries, forward-only regressions, and opt-in one backward.

Fixtures below add declared synthetic public context to public mechanics only.
They never reuse legacy targets, prove recorder provenance, or admit training
data. The sole gradient connectivity check requires an explicit environment flag.
"""
from copy import deepcopy
import hashlib
import json
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
from nosl.data_v3 import (RECORD_KIND, RECORD_SCHEMA, QUARANTINE_REASON, validate_record,
                          validate_production_record, validate_targets)
from nosl.inference_v2 import InferenceV2, implementation_fingerprint as v2_fingerprint
from nosl.inference_v3 import (BUNDLE_FORMAT, InferenceV3, POLICY_ACTION_KINDS, SUPERVISION_FORMAT,
                              SUPERVISION_PROGRESS_FORMAT, action_policy_training_verified,
                              implementation_fingerprint, valid_supervision_coverage)
from nosl.model_v2 import StudentV2
from nosl.model_v3 import RUN_CONTEXT_FEATURE_NAMES, StudentV3, public_run_context_features
from nosl.public_identity_v2 import public_input_digest as v2_digest
from nosl.public_identity_v3 import canonical_public_input, legacy_alias_digests, public_input_digest
from nosl.schema import HEADS, SchemaError
from nosl.schema_v2 import PLAN_HEADS, validate_public as validate_v2
from nosl.schema_v3 import (INT32_MAX, MODEL_VERSION, PUBLIC_SCHEMA, RUN_CONTEXT_SCHEMA,
                           PublicHuntControllerV3, PublicRegenControllerV3, load_config,
                           validate_config, validate_public, validate_run_context)


def fixture(name):
    return json.loads((ROOT / "tests/python/fixtures" / name).read_text())


def declared_public(value, *, complete=True, count=3):
    """Synthetic boundary fixture only; never a legacy-label upgrade API."""
    result = deepcopy(value)
    result["schema_version"] = PUBLIC_SCHEMA
    result["observation"]["schema"] = "nosl.public.v3"
    result["observation"]["runContext"] = {"schemaVersion": RUN_CONTEXT_SCHEMA, "actIndex": 1,
        "floor": 17, "combatEntryIndex": count if complete else None, "completeFromRunStart": complete}
    if "anchor" in result["controller_context"]:
        result["controller_context"]["anchor"] = declared_public(result["controller_context"]["anchor"], complete=complete, count=count)
    return result


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def unavailable_targets(public):
    result = {"actions": [{"action_index": i, "quality": "unresolved", "masks": {head: False for head in HEADS},
                           **{head: None for head in HEADS}} for i in range(len(public["candidate_actions"]))],
              "pairwise": [], "equivalent_action_set": []}
    if public["controller_context"]["status"] != "inactive":
        result["plan"] = {"label_scope": "unavailable", **{head: None for head in PLAN_HEADS},
            "masks": {head: False for head in PLAN_HEADS}, "allocated_worlds": 0,
            "success_completed_worlds": 0, "paired_completed_worlds": 0}
    return result


class PublicContextV3Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.config = load_config(ROOT / "configs/student.v3.engineering.json")

    def setUp(self):
        self.old = fixture("native-entry-public-v2.json")
        self.public = declared_public(self.old)
        self.hunt = declared_public(fixture("finite-hunt-record-v2.jsonl")["public_input"])

    def test_explicit_v3_and_complete_incomplete_zero_count(self):
        for complete, count in ((True, 0), (True, 3), (False, None)):
            public = declared_public(self.old, complete=complete, count=count)
            self.assertIs(validate_public(public, self.config), public)
            self.assertEqual(InferenceV3(self.config).predict(public)["status"], "MODEL_UNTRAINED")
        for value in (0, INT32_MAX):
            context = self.public["observation"]["runContext"]
            context.update(actIndex=value, floor=value, combatEntryIndex=value)
            validate_run_context(context)
        for version, observation in (("nosl.student.public.v2", "nosl.public.v3"), (PUBLIC_SCHEMA, "nosl.public.v2")):
            changed = deepcopy(self.public)
            changed["schema_version"], changed["observation"]["schema"] = version, observation
            with self.assertRaises(SchemaError): validate_public(changed, self.config)

    def test_invalid_and_private_context_rejects_fail_closed(self):
        origin = self.public["observation"]["runContext"]
        mutations = [{"completeFromRunStart": False}, {"combatEntryIndex": None}, {"completeFromRunStart": 1},
                     {"combatEntryIndex": True}, {"actIndex": -1}, {"floor": 1.0}, {"floor": INT32_MAX + 1},
                     {"schemaVersion": "private"}, {"seed": 1}, {"rngState": 1}, {"worldIndex": 1}]
        for change in mutations:
            with self.subTest(change=change), self.assertRaises(SchemaError):
                validate_run_context({**origin, **change})
        for key in origin:
            value = deepcopy(origin); value.pop(key)
            with self.subTest(missing=key), self.assertRaises(SchemaError): validate_run_context(value)
        for path in ((), ("observation",), ("controller_context",), ("observation", "runContext")):
            value = deepcopy(self.public); target = value
            for key in path: target = target[key]
            target["privateSeed"] = 991
            with self.subTest(path=path), self.assertRaises(SchemaError): validate_public(value, self.config)
        value = deepcopy(self.public)
        event = value["observation"]["history"][1]
        details = json.loads(event["detail"]); details["seed"] = 991; event["detail"] = json.dumps(details)
        with self.assertRaises(SchemaError): validate_public(value, self.config)

    def test_opt_in_config_and_frozen_v2_remain_separate(self):
        self.assertEqual(self.config["base_config"], load_config_v2())
        validate_v2(self.old, self.config["base_config"])
        self.assertEqual(InferenceV2(self.config["base_config"]).predict(self.old)["status"], "MODEL_UNTRAINED")
        with self.assertRaises(SchemaError): validate_v2(self.public, self.config["base_config"])
        with self.assertRaises(SchemaError): validate_public(self.old, self.config)
        with self.assertRaises(SchemaError): validate_config(self.config["base_config"])
        changed = deepcopy(self.config); changed["production_admission"] = "enabled"
        with self.assertRaises(SchemaError): validate_config(changed)

    def test_v3_execution_cannot_change_legacy_model_bits(self):
        torch.manual_seed(281); legacy = StudentV2(self.config["base_config"]).eval()
        before = {key: value.clone() for key, value in legacy.state_dict().items()}
        with torch.no_grad():
            output = legacy(self.old)
            StudentV3(self.config).eval()(self.public)
            repeated = legacy(self.old)
        for key in output:
            if isinstance(output[key], dict):
                for head in output[key]: self.assertTrue(torch.equal(output[key][head], repeated[key][head]))
            else: self.assertTrue(torch.equal(output[key], repeated[key]))
        self.assertTrue(all(torch.equal(before[key], value) for key, value in legacy.state_dict().items()))

    def test_explicit_features_and_knownness_consume_every_context_field(self):
        complete = declared_public(self.old, complete=True, count=0)
        incomplete = declared_public(self.old, complete=False)
        a, b = public_run_context_features(complete), public_run_context_features(incomplete)
        self.assertEqual(len(a), len(RUN_CONTEXT_FEATURE_NAMES))
        self.assertEqual(a[:5], b[:5])
        self.assertEqual((a[5:7], b[5:7]), ([1., 1.], [0., 0.]))
        self.assertEqual(a[7:], [0.] * 8)
        torch.manual_seed(911); model = StudentV3(self.config).eval()
        with torch.no_grad(): baseline = model(self.public)
        for key in ("actIndex", "floor", "combatEntryIndex"):
            changed = deepcopy(self.public); changed["observation"]["runContext"][key] += 1
            self.assertNotEqual(public_run_context_features(self.public), public_run_context_features(changed))
            with torch.no_grad(): output = model(changed)
            self.assertFalse(torch.equal(baseline["value"], output["value"]), key)
        with torch.no_grad():
            self.assertFalse(torch.equal(model(complete)["value"], model(incomplete)["value"]))

    def test_nested_anchor_features_and_context_isolation(self):
        validate_public(self.hunt, self.config)
        features = public_run_context_features(self.hunt)
        self.assertEqual(features[7], 1.)
        self.assertEqual(features[:7], features[8:])
        changed = deepcopy(self.hunt)
        changed["controller_context"]["anchor"]["observation"]["runContext"]["floor"] += 1
        self.assertNotEqual(features, public_run_context_features(changed))
        self.assertNotEqual(public_input_digest(self.hunt), public_input_digest(changed))
        self.assertTrue(legacy_alias_digests(self.hunt) & legacy_alias_digests(changed))
        with self.assertRaisesRegex(SchemaError, "runContext"): validate_public(changed, self.config)
        changed = deepcopy(self.hunt)
        changed["controller_context"]["anchor"] = fixture("finite-hunt-record-v2.jsonl")["public_input"]["controller_context"]["anchor"]
        with self.assertRaises(SchemaError): validate_public(changed, self.config)
        changed = deepcopy(self.hunt)
        changed["controller_context"]["anchor"]["observation"]["runContext"]["privateSeed"] = 12
        with self.assertRaises(SchemaError): validate_public(changed, self.config)

    def test_identity_keeps_full_context_aliases_only_overgroup(self):
        changed = deepcopy(self.public); changed["observation"]["runContext"]["combatEntryIndex"] += 1
        self.assertNotEqual(public_input_digest(self.public), public_input_digest(changed))
        self.assertTrue(legacy_alias_digests(self.public) & legacy_alias_digests(changed))
        self.assertIn(v2_digest(self.old), legacy_alias_digests(self.public))
        self.assertIn("runContext", canonical_public_input(self.public)["observation"])
        old_hunt = fixture("finite-hunt-record-v2.jsonl")["public_input"]
        self.assertIn(v2_digest(old_hunt), legacy_alias_digests(self.hunt))
        anchor = self.hunt["controller_context"]["anchor"]
        self.assertIn(public_input_digest(anchor), legacy_alias_digests(self.hunt))
        self.assertEqual(self.public, declared_public(self.old))

    def test_revision_and_masked_candidate_semantics_remain_unchanged(self):
        changed = deepcopy(self.hunt)
        for packet in (changed, changed["controller_context"]["anchor"]):
            for action in packet["candidate_actions"]: action["revision"] += 10
        self.assertEqual(public_input_digest(self.hunt), public_input_digest(changed))
        padded = deepcopy(self.hunt)
        for packet in (padded, padded["controller_context"]["anchor"]):
            action = deepcopy(next(a for a in packet["candidate_actions"] if a["kind"] == "play"))
            action["target"] = -2
            packet["candidate_actions"].append(action); packet["legal_mask"].append(False)
        torch.manual_seed(17); model = StudentV3(self.config).eval()
        with torch.no_grad(): original, extra = model(self.hunt), model(padded)
        self.assertTrue(torch.isneginf(extra["ranking_score"][-1]))
        for head in PLAN_HEADS: self.assertTrue(torch.allclose(original["plan"][head], extra["plan"][head], atol=1e-7))

    def test_hunt_no_reset_and_settlement_retain_full_context(self):
        controller = PublicHuntControllerV3(self.hunt, self.config)
        changed = deepcopy(self.hunt)
        for packet in (changed, changed["controller_context"]["anchor"]):
            packet["observation"]["runContext"]["combatEntryIndex"] += 1
        validate_public(changed, self.config)
        with self.assertRaisesRegex(SchemaError, "cannot replace"): controller.advance(changed)
        later = deepcopy(self.hunt)
        later["observation"]["turn"] = 3
        event = {"kind": "player_turn", "detail": "3"}
        later["observation"]["history"].append(event)
        later["controller_context"].update(lastObservedPlayerTurn=3, observedEvents=[event])
        expired = controller.advance(later)
        self.assertEqual(expired["controller_context"]["exitReason"], "fixed_deadline_expired")
        with self.assertRaisesRegex(SchemaError, "cannot reopen"): controller.advance(later)
        runner = InferenceV3(self.config)
        self.assertEqual(runner.predict(self.hunt)["status"], "MODEL_UNTRAINED")
        terminal = fixture("finite-hunt-terminal-v2.json")
        settled = runner.predict(terminal)
        self.assertEqual(settled["status"], "PLAN_FINISHED")
        self.assertEqual(settled["controller_context"]["anchor"], self.hunt["controller_context"]["anchor"])
        self.assertEqual(runner.predict(self.hunt)["status"], "INVALID_INPUT")
        self.assertEqual(runner.predict(self.public)["status"], "INVALID_INPUT")

    def test_regen_retains_original_guards_and_anchor_context(self):
        originals = [json.loads(line) for line in (ROOT / "tests/python/fixtures/finite-regen-public-v2.jsonl").read_text().splitlines()]
        packets = [declared_public(public, complete=False) for public in originals]
        runner = InferenceV3(self.config)
        for public in packets:
            validate_public(public, self.config)
            result = runner.predict(public)
            self.assertIn(result["status"], ("MODEL_UNTRAINED", "PLAN_FINISHED"))
            self.assertIsNone(result["selected_action"])
        self.assertEqual(result["status"], "PLAN_FINISHED")
        self.assertEqual(runner.controller.public["controller_context"]["anchor"], packets[0]["controller_context"]["anchor"])
        controller = PublicRegenControllerV3(packets[0], self.config)
        invalid = deepcopy(packets[0]); invalid["observation"]["hp"] += 5
        with self.assertRaisesRegex(SchemaError, "contradicts observed history"): controller.advance(invalid)

    def test_engineering_rows_keep_masks_context_and_production_quarantine(self):
        record = {"schema_version": RECORD_SCHEMA, "record_kind": RECORD_KIND,
                  "public_input": self.public, "targets": unavailable_targets(self.public),
                  "audit_only": {"trainable": False, "conditioned_public_input_digest": public_input_digest(self.public)}}
        validate_record(record, self.config)
        self.assertTrue(all(not any(row["masks"].values()) for row in record["targets"]["actions"]))
        with self.assertRaisesRegex(SchemaError, QUARANTINE_REASON): validate_production_record(record, self.config)
        changed = deepcopy(record); changed["audit_only"]["trainable"] = True
        with self.assertRaisesRegex(SchemaError, "trainable:false"): validate_record(changed, self.config)
        changed = deepcopy(record); changed["public_input"]["observation"]["runContext"]["floor"] += 1
        with self.assertRaisesRegex(SchemaError, "full v3 conditioning"): validate_record(changed, self.config)
        changed = deepcopy(record); changed["targets"]["actions"][0]["value"] = 0
        with self.assertRaisesRegex(SchemaError, "null"): validate_record(changed, self.config)
        regen_public = declared_public(json.loads((ROOT / "tests/python/fixtures/finite-regen-public-v2.jsonl").read_text().splitlines()[0]))
        targets = unavailable_targets(regen_public)
        validate_targets(targets, regen_public, self.config)
        targets["plan"].update(label_scope="whole_plan_from_anchor", specified_success_probability=1,
                               allocated_worlds=1, success_completed_worlds=1)
        targets["plan"]["masks"]["specified_success_probability"] = True
        with self.assertRaisesRegex(SchemaError, "no applicable learned target"): validate_targets(targets, regen_public, self.config)

    def test_supervision_guard_requires_actual_consumption_and_v3_evidence(self):
        # Synthetic metadata checks only; no training or learned manifest is produced.
        counts = {kind: 0 for kind in POLICY_ACTION_KINDS}
        for action, legal in zip(self.public["candidate_actions"], self.public["legal_mask"]):
            if legal: counts[action["kind"]] += 1
        rows = sum(counts.values())
        coverage = {"format": SUPERVISION_FORMAT, "roots": 1,
            "action_heads": {head: {"masked_rows": rows if head == "value" else 0,
                                    "loss_rows": rows if head == "value" else 0,
                                    "loss_roots": int(head == "value")} for head in HEADS},
            "plan_heads": {head: {"masked_roots": 0, "loss_roots": 0} for head in PLAN_HEADS},
            "action_policy": {"eligible_roots": 1, "value_roots": 1, "pairwise_roots": 0,
                              "equivalent_roots": 0, "pairwise_pairs": 0}, "policy_action_kinds": counts}
        self.assertTrue(valid_supervision_coverage(coverage, self.config))
        progress = {"format": SUPERVISION_PROGRESS_FORMAT, "optimizer_steps": 1,
                    "action_policy_optimizer_steps": 1, "consumed": deepcopy(coverage)}
        frozen = {"training_supervision": coverage, "config_sha256": digest(self.config),
                  "splits": {"train": {"records_sha256": "a" * 64}}}
        manifest = {"trained": True, "optimizer_steps": 1, "training_supervision": coverage,
                    "supervision_progress": progress, "frozen_inputs": frozen, "config_sha256": digest(self.config)}
        evidence = {"config_sha256": digest(self.config), "train_records_sha256": "a" * 64,
                    "corpus": coverage, "progress": progress}
        manifest["supervision_sha256"] = digest(evidence)
        self.assertTrue(action_policy_training_verified(manifest, self.config, self.public))
        for key in ("supervision_progress", "training_supervision", "supervision_sha256", "frozen_inputs"):
            changed = deepcopy(manifest); changed.pop(key)
            self.assertFalse(action_policy_training_verified(changed, self.config, self.public))
        for change in (0, False):
            changed = deepcopy(manifest); changed["supervision_progress"]["action_policy_optimizer_steps"] = change
            self.assertFalse(action_policy_training_verified(changed, self.config, self.public))
        changed = deepcopy(manifest); changed["supervision_progress"]["consumed"]["action_policy"]["value_roots"] = 0
        self.assertFalse(action_policy_training_verified(changed, self.config, self.public))
        changed = deepcopy(coverage); changed["format"] = "nosl.training.supervision.v2"
        self.assertFalse(valid_supervision_coverage(changed, self.config))
        changed = deepcopy(self.config); changed["base_config"]["base_config"]["loss_weights"]["value"] = 0
        self.assertFalse(valid_supervision_coverage(coverage, changed))

    def test_untrained_bundle_abstains_and_legacy_weights_fail_strictly(self):
        torch.manual_seed(45); model = StudentV3(self.config)
        self.assertNotEqual(implementation_fingerprint(), v2_fingerprint())
        self.assertIn("python/nosl/schema_v3.py", implementation_fingerprint()["source_sha256"])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "config.json").write_text(json.dumps(self.config))
            torch.save(model.state_dict(), path / "weights.pt")
            manifest = {"format": BUNDLE_FORMAT, "public_schema": PUBLIC_SCHEMA, "model_version": MODEL_VERSION,
                        "config_sha256": digest(self.config), "weights_sha256": hashlib.sha256((path / "weights.pt").read_bytes()).hexdigest(),
                        "trained": False, "optimizer_steps": 0, "implementation": implementation_fingerprint()}
            (path / "manifest.json").write_text(json.dumps(manifest))
            runner = InferenceV3.from_bundle(path, allow_experimental=True)
            result = runner.predict(self.public)
            self.assertEqual(result["status"], "MODEL_UNTRAINED")
            self.assertIsNone(result["selected_action"])
            altered = deepcopy(manifest); altered["implementation"]["runtime"]["torch_version"] = "wrong"
            (path / "manifest.json").write_text(json.dumps(altered))
            with self.assertRaisesRegex(SchemaError, "implementation/runtime"): InferenceV3.from_bundle(path)
            torch.save(StudentV2(self.config["base_config"]).state_dict(), path / "weights.pt")
            manifest["weights_sha256"] = hashlib.sha256((path / "weights.pt").read_bytes()).hexdigest()
            (path / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaisesRegex(RuntimeError, "Missing key"): InferenceV3.from_bundle(path)
        with self.assertRaisesRegex(SchemaError, "legacy weights"):
            InferenceV3(self.config, StudentV2(self.config["base_config"]))
        fake = {"trained": True, "optimizer_steps": 1, "status": "EXPERIMENTAL_UNPROMOTED"}
        self.assertEqual(InferenceV3(self.config, model, fake, allow_experimental=True).predict(self.public)["status"], "MODEL_UNVALIDATED")
        fake.update(format=BUNDLE_FORMAT, public_schema=PUBLIC_SCHEMA, model_version=MODEL_VERSION, implementation=implementation_fingerprint())
        with patch.object(model, "forward", side_effect=AssertionError("must abstain before forward")):
            self.assertEqual(InferenceV3(self.config, model, fake, allow_experimental=True).predict(self.public)["status"], "ACTION_POLICY_UNVALIDATED")

    def test_import_and_cli_have_no_trainer_or_simulator(self):
        script = "import sys; import nosl.inference_v3; assert not any(x in sys.modules for x in ('nosl.train','nosl.train_v2','nosl.data','nosl.data_v2','nosl.data_v3','nosl.smoke_v2','clr')); print('standalone')"
        environment = {**os.environ, "PYTHONPATH": str(ROOT / "python")}
        result = subprocess.run([sys.executable, "-c", script], env=environment, cwd=ROOT, capture_output=True, text=True, check=True)
        self.assertEqual(result.stdout.strip(), "standalone")
        result = subprocess.run([sys.executable, "-m", "nosl.inference_v3", "--config", "configs/student.v3.engineering.json"],
            input=json.dumps(self.public) + "\n", env=environment, cwd=ROOT, capture_output=True, text=True, check=True)
        self.assertEqual(json.loads(result.stdout)["status"], "MODEL_UNTRAINED")

    @unittest.skipUnless(os.environ.get("NOSL_V3_SINGLE_BACKWARD") == "1", "one explicitly authorized engineering backward only")
    def test_single_batch_gradient_connectivity_no_optimizer_no_labels(self):
        torch.manual_seed(7103); model = StudentV3(self.config)
        before = {key: value.clone() for key, value in model.state_dict().items()}
        # One single-packet engineering batch, no old targets and no objective or
        # posterior claim. Squared outputs merely exercise gradient connectivity.
        output = model(self.hunt)
        scalar = sum(output[head].square().mean() for head in HEADS)
        scalar = scalar + sum(value.square() for value in output["plan"].values())
        scalar.backward()
        self.assertTrue(torch.isfinite(scalar))
        for name, parameter in model.named_parameters():
            if parameter.grad is not None: self.assertTrue(torch.isfinite(parameter.grad).all(), name)
        self.assertGreater(model.run_context[0].weight.grad.abs().sum().item(), 0)
        self.assertGreater(model.context[0].weight.grad.abs().sum().item(), 0)
        for head in model.plan_heads.values(): self.assertGreater(head.weight.grad.abs().sum().item(), 0)
        self.assertTrue(all(torch.equal(before[key], value) for key, value in model.state_dict().items()))
        print(json.dumps({"status": "V3_SINGLE_BATCH_GRADIENT_CHECK_PASS", "backward_calls": 1,
            "optimizer_steps": 0, "weights_changed": False, "targets_used": False,
            "old_test_targets_read": False, "formal_training_run": False}))


def load_config_v2():
    from nosl.schema_v2 import load_config
    return load_config(ROOT / "configs/student.v2.engineering.json")


if __name__ == "__main__": unittest.main()
