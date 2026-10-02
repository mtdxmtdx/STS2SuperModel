"""Source/runtime guards and mean-loss contracts; no optimizer steps or fitting."""
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.data import DecisionDataset
from nosl.inference import Inference
from nosl.model import Student
from nosl.reproducibility import (INFERENCE_SOURCE_FILES, SOURCE_FILES,
                                 implementation_fingerprint, verify_inference_implementation)
from nosl.schema import HEADS, SchemaError, load_config
from nosl.train import (atomic_save, checkpoint_state, config_hash, decision_loss,
                        evaluate, freeze_inputs, restore_checkpoint, seed_everything)
from test_student import fixture


def changed_source(name):
    """Simulate byte drift without touching any live generator-bound source."""
    original = Path.read_bytes
    target = ROOT / "python/nosl" / name

    def read_bytes(path):
        data = original(path)
        return data + b"\n# simulated source drift\n" if path == target else data

    return patch.object(Path, "read_bytes", read_bytes)


class PilotReproducibilityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        cls.config["hidden_dim"] = 16  # Small untrained serialization fixtures.

    def setUp(self):
        seed_everything(1729, 1)
        self.model = Student(self.config)
        self.optimizer = torch.optim.AdamW(self.model.parameters(), lr=.001)
        self.frozen = {"identity": "untrained-engineering-fixture"}
        self.budget = {"max_steps": 1, "max_epochs": 1, "max_roots": 1}
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "checkpoint.pt"
        self.state = checkpoint_state(self.model, self.optimizer, self.frozen, self.budget,
                                      0, 0, 0, [], [])
        atomic_save(self.state, self.path)

    def restore(self, frozen=None):
        return restore_checkpoint(self.path, self.model, self.optimizer,
                                  self.frozen if frozen is None else frozen, self.budget)

    def test_fingerprint_is_stable_relative_and_plain_serializable(self):
        first = implementation_fingerprint()
        self.assertEqual(first, implementation_fingerprint())
        self.assertEqual(first, json.loads(json.dumps(first, allow_nan=False)))
        self.assertEqual(set(first["source_sha256"]), {"python/nosl/" + name for name in SOURCE_FILES})
        self.assertTrue(all(not Path(name).is_absolute() for name in first["source_sha256"]))
        self.assertNotIn(str(ROOT), json.dumps(first))
        self.assertEqual(type(first["runtime"]["torch_version"]), str)
        self.assertEqual(len(first["runtime"]["torch_build_sha256"]), 64)
        self.assertTrue(first["cpu_settings"]["deterministic_algorithms"])
        self.assertEqual(first["cpu_settings"]["intraop_threads"], 1)

    def test_ordinary_checkpoint_roundtrip_restores_model_optimizer_and_rng(self):
        expected_random, expected_torch = random.random(), torch.rand(4)
        expected_parameter = next(self.model.parameters()).detach().clone()
        with torch.no_grad():
            next(self.model.parameters()).add_(1)
        self.optimizer.param_groups[0]["lr"] = .5
        seed_everything(999, 1)
        loaded = self.restore()
        self.assertEqual(random.random(), expected_random)
        self.assertTrue(torch.equal(torch.rand(4), expected_torch))
        self.assertTrue(torch.equal(next(self.model.parameters()), expected_parameter))
        self.assertEqual(self.optimizer.param_groups[0]["lr"], .001)
        self.assertEqual(loaded["frozen"]["implementation"], implementation_fingerprint())
        self.assertEqual(loaded["optimizer_steps"], 0)
        self.assertFalse(loaded["formal_training_run"])
        self.assertNotIn("implementation", self.frozen)  # No caller mutation.

    def test_all_bound_source_changes_reject_resume(self):
        for name in SOURCE_FILES:
            with self.subTest(source=name), changed_source(name):
                with self.assertRaisesRegex(SchemaError, "implementation.*changed"):
                    self.restore()

    def test_python_torch_version_and_build_changes_reject_resume(self):
        patches = (patch("nosl.reproducibility.platform.python_version", return_value="different-python"),
                   patch("nosl.reproducibility.torch.__version__", "different-torch"),
                   patch("nosl.reproducibility.torch.version.git_version", "different-build"),
                   patch("nosl.reproducibility.torch.__config__.show", return_value="different-build-settings"),
                   patch("nosl.reproducibility.torch.backends.cpu.get_cpu_capability", return_value="different-ISA"))
        for index, replacement in enumerate(patches):
            with self.subTest(runtime_field=index), replacement:
                with self.assertRaisesRegex(SchemaError, "runtime.*changed"):
                    self.restore()

    def test_effective_cpu_settings_changes_reject_resume(self):
        patches = (patch("nosl.reproducibility.torch.are_deterministic_algorithms_enabled", return_value=False),
                   patch("nosl.reproducibility.torch.is_deterministic_algorithms_warn_only_enabled", return_value=True),
                   patch("nosl.reproducibility.torch.get_num_threads", return_value=2),
                   patch("nosl.reproducibility.torch.get_num_interop_threads", return_value=999),
                   patch("nosl.reproducibility.torch.get_default_dtype", return_value=torch.float64))
        for index, replacement in enumerate(patches):
            with self.subTest(cpu_setting=index), replacement:
                with self.assertRaisesRegex(SchemaError, "CPU settings changed"):
                    self.restore()

    def test_caller_cannot_bypass_live_check_with_the_old_frozen_metadata(self):
        with changed_source("model.py"):
            with self.assertRaisesRegex(SchemaError, "implementation.*changed"):
                self.restore(self.state["frozen"])

    def test_changed_stored_fingerprint_is_rejected(self):
        self.state["frozen"]["implementation"]["source_sha256"]["python/nosl/model.py"] = "0" * 64
        atomic_save(self.state, self.path)
        with self.assertRaisesRegex(SchemaError, "implementation.*changed"):
            self.restore()

    def test_old_checkpoint_missing_provenance_explicitly_cannot_resume(self):
        del self.state["frozen"]["implementation"]
        atomic_save(self.state, self.path)
        with self.assertRaisesRegex(SchemaError, "lacks implementation provenance; cannot safely resume"):
            self.restore()

    def test_checkpoint_save_rejects_source_drift_during_pilot(self):
        with changed_source("train.py"):
            with self.assertRaisesRegex(SchemaError, "changed during pilot"):
                checkpoint_state(self.model, self.optimizer, self.state["frozen"], self.budget,
                                 0, 0, 0, [], [])

    def test_frozen_dataset_identity_includes_implementation(self):
        datasets = {}
        for index, split in enumerate(("train", "validation", "test")):
            record = fixture()
            record["public_input"]["observation"]["block"] = index
            audit = record["audit_only"]
            audit.update(source_run_group=split, source_combat_id=split, branch_family=split,
                         public_state_digest=split, simulator_commit="pinned", rules_version="v1", label_endpoint="settled",
                         versions={"teacher": "T0-test", "continuation": "test-policy", "objective": "synthetic-contract",
                                   "public_schema": "nosl.student.public.v1", "simulator": "pinned"})
            path = Path(self.temp.name) / (split + ".jsonl")
            path.write_text(json.dumps(record) + "\n")
            datasets[split] = DecisionDataset(path, self.config)
        self.assertEqual(freeze_inputs(datasets, self.config)["implementation"], implementation_fingerprint())

    def bundle(self, *, trained=True, provenance=True):
        root = Path(self.temp.name)
        (root / "config.json").write_text(json.dumps(self.config))
        torch.save(self.model.state_dict(), root / "weights.pt")
        # These boundary-test weights are random initialization, never learned.
        manifest = {"format": "nosl.student.bundle.v1", "public_schema": self.config["schema_version"],
                    "trained": trained, "status": "EXPERIMENTAL_UNPROMOTED", "calibrated": False,
                    "config_sha256": config_hash(self.config),
                    "weights_sha256": hashlib.sha256((root / "weights.pt").read_bytes()).hexdigest()}
        if provenance:
            manifest["frozen_inputs"] = {"implementation": implementation_fingerprint()}
        (root / "manifest.json").write_text(json.dumps(manifest))
        return root

    def test_learned_bundle_same_implementation_is_loadable(self):
        runner = Inference.from_bundle(self.bundle(), allow_experimental=True)
        self.assertEqual(runner.predict(fixture()["public_input"])["status"], "EXPERIMENTAL_UNCALIBRATED")

    def test_learned_bundle_rejects_each_inference_source_change(self):
        root = self.bundle()
        for name in INFERENCE_SOURCE_FILES:
            with self.subTest(source=name), changed_source(name):
                with self.assertRaisesRegex(SchemaError, "inference implementation changed"):
                    Inference.from_bundle(root, allow_experimental=True)

    def test_learned_bundle_rejects_runtime_build_change(self):
        root = self.bundle()
        with patch("nosl.reproducibility.torch.__config__.show", return_value="different-build-settings"):
            with self.assertRaisesRegex(SchemaError, "runtime.*changed"):
                Inference.from_bundle(root, allow_experimental=True)

    def test_learned_bundle_missing_provenance_is_rejected(self):
        with self.assertRaisesRegex(SchemaError, "cannot safely evaluate"):
            Inference.from_bundle(self.bundle(provenance=False), allow_experimental=True)

    def test_untrained_engineering_bundle_remains_compatible(self):
        runner = Inference.from_bundle(self.bundle(trained=False, provenance=False))
        self.assertEqual(runner.predict(fixture()["public_input"])["status"], "MODEL_UNTRAINED")

    def test_inference_allows_trainer_only_change_and_different_fit_flags(self):
        root = self.bundle()
        with changed_source("train.py"), patch("nosl.reproducibility.torch.get_num_threads", return_value=2):
            Inference.from_bundle(root, allow_experimental=True)

    def test_inference_does_not_read_training_only_sources(self):
        fingerprint = implementation_fingerprint()
        original = Path.read_bytes

        def read_bytes(path):
            if path.name in {"train.py", "data.py", "vocabulary.py"}:
                raise AssertionError("inference must not depend on training source files")
            return original(path)

        with patch.object(Path, "read_bytes", read_bytes):
            verify_inference_implementation(fingerprint)

    def test_standalone_inference_imports_no_trainer_dataset_or_teacher(self):
        script = ("import sys; import nosl.inference; "
                  "assert not {'nosl.train', 'nosl.data', 'nosl.vocabulary'} & set(sys.modules); "
                  "assert not [m for m in sys.modules if any(s in m.lower() for s in ('teacher', 'simulator', 'searcher'))]")
        process = subprocess.run([sys.executable, "-c", script], text=True, capture_output=True,
                                 env={**os.environ, "PYTHONPATH": str(ROOT / "python")})
        self.assertEqual(process.returncode, 0, process.stderr)


class MeanLossContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        seed_everything(1729, 1)

    def loss(self, head, prediction, targets, *, masked=False):
        record, config = fixture(), deepcopy(self.config)
        record["targets"].update(pairwise=[], equivalent_action_set=[])
        config["loss_weights"] = {name: float(name == head) for name in config["loss_weights"]}
        scalar = torch.tensor(prediction, dtype=torch.float64, requires_grad=True)
        output = {name: scalar.expand(3) * 0 for name in HEADS}
        output[head] = scalar.expand(3)
        output["ranking_score"] = output["value"]
        for index, row in enumerate(record["targets"]["actions"]):
            row["masks"] = {name: not masked and name == head and index < 2 for name in HEADS}
            row.update({name: targets[index] if row["masks"][name] else None for name in HEADS})
            row["sample_weight"] = (.95, .05, 1.)[index]
        loss, terms = decision_loss(output, record["targets"], record["public_input"], config)
        loss.backward()
        return float(loss), float(scalar.grad), terms

    def test_rare_large_utility_loss_has_zero_gradient_at_weighted_mean(self):
        # 95% utility 0, 5% utility -1000 => expected utility -50 => model -0.5.
        loss, gradient, _ = self.loss("value", -.5, [0., -1000.])
        self.assertAlmostEqual(gradient, 0., places=12)
        self.assertAlmostEqual(loss, 2.375, places=12)
        _, robust_location_gradient, _ = self.loss("value", -.05 / .95, [0., -1000.])
        self.assertGreater(robust_location_gradient, .4)

    def test_other_mean_heads_also_target_weighted_arithmetic_mean(self):
        for head, prediction, targets in (("expected_final_hp", .05, [0., 100.]),
                                          ("potion_net_change", -.05, [0., -1.])):
            with self.subTest(head=head):
                _, gradient, _ = self.loss(head, prediction, targets)
                self.assertAlmostEqual(gradient, 0., places=12)

    def test_nullable_masked_targets_contribute_zero_loss_and_gradient(self):
        for head in ("value", "expected_final_hp", "potion_net_change"):
            with self.subTest(head=head):
                loss, gradient, terms = self.loss(head, .3, [None, None], masked=True)
                self.assertEqual(loss, 0.)
                self.assertEqual(gradient, 0.)
                self.assertTrue(all(term == 0. for term in terms.values()))

    def test_reported_value_mse_uses_value_mae_masks_and_raw_utility_units(self):
        from test_pilot_metrics import FixedPredictions, dataset, means_record
        record = means_record([0., -5., -10.])
        row = record["targets"]["actions"][2]
        row["masks"]["value"], row["value"], row["quality"] = False, None, "objective_value_unresolved"
        result = evaluate(FixedPredictions(self.config, [-2., 0., 999.]), dataset(record))
        self.assertEqual(result["value_mse"]["count"], result["value_mae"]["count"])
        self.assertEqual(result["value_mse"]["count"], 2)
        self.assertAlmostEqual(result["value_mse"]["value"], 14.5, places=5)


if __name__ == "__main__":
    unittest.main()
