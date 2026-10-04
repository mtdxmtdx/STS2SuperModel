"""One declared engineering batch, exactly one backward, never an optimizer step."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / "python"), str(ROOT / "tools"), str(ROOT / "tests/data")]
import prepare_dataset as prep
from test_prepare_v2 import fixture
from nosl.data_v2 import PreparedDatasetV2
from nosl.inference_v2 import InferenceV2
from nosl.model_v2 import StudentV2
from nosl.public_identity_v2 import PUBLIC_IDENTITY_SCHEME
from nosl.schema_v2 import load_config
from nosl.smoke_v2 import decision_loss
from nosl.train import atomic_save, seed_everything
from nosl.train_v2 import checkpoint_state, restore_checkpoint, freeze_inputs, quality_gate, export_bundle


class V2PipelineAcceptance(unittest.TestCase):
    def test_protected_prepare_append_load_checkpoint_and_single_backward(self):
        config = load_config(ROOT / "configs/student.v2.engineering.json")
        pipeline = json.loads((ROOT / "configs/data_pipeline.v2.json").read_text())
        legacy_pipeline = json.loads((ROOT / "configs/data_pipeline.v1.json").read_text())
        roots = {}
        for i in range(100):
            row = fixture(i)
            group = prep.provenance_components([row], identity_scheme=PUBLIC_IDENTITY_SCHEME)[0][0]
            roots.setdefault(prep.choose_split(group, pipeline), row)
            if len(roots) == 3: break
        self.assertEqual({"train", "validation", "test"}, set(roots))
        old = None
        for i in range(150, 250):
            row = fixture(i)
            row["public_input"]["schema_version"] = "nosl.student.public.v1"
            row["audit_only"]["versions"]["public_schema"] = "nosl.student.public.v1"
            group = prep.provenance_components([row])[0][0]
            if prep.choose_split(group, legacy_pipeline) == "test": old = row; break
        self.assertIsNotNone(old)
        finite = json.loads((ROOT / "tests/python/fixtures/finite-hunt-record-v2.jsonl").read_text())
        with tempfile.TemporaryDirectory() as directory, patch.object(torch.optim.AdamW, "step", side_effect=AssertionError("no fitting authorized")):
            source, corpus, bundle = (Path(directory) / key for key in ("legacy", "v2", "bundle"))
            prep.persist_batch(source, [old], [{}], legacy_pipeline, "engineering-smoke", [{"fixture": "declared-old-contract"}], False)
            protection = prep.export_split_protection(source)
            prep.persist_batch(corpus, list(roots.values()), [{}, {}, {}], pipeline, "engineering-smoke",
                               [{"fixture": "declared-independent-contract-roots"}], False, protection=protection)
            frozen_test = deepcopy(prep.verify_manifest(corpus)["frozen_test_shards"])
            excluded = deepcopy(old)
            excluded["public_input"]["schema_version"] = "nosl.student.public.v2"
            excluded["audit_only"]["versions"] = deepcopy(roots["train"]["audit_only"]["versions"])
            # Different teacher versions stay in distinct immutable corpora.
            appended = deepcopy(roots["train"])
            appended["public_input"]["observation"]["enemies"][0]["hp"] += 100
            prep.persist_batch(corpus, [appended, excluded], [{}, {}], pipeline, "engineering-smoke",
                               [{"fixture": "protected-append"}], True)
            self.assertEqual(frozen_test, prep.verify_manifest(corpus)["frozen_test_shards"])
            data = {split: PreparedDatasetV2(corpus, split, config) for split in ("train", "validation")}
            self.assertEqual(2, len(data["train"]))
            seed_everything(config["base_config"]["seed"])
            frozen = freeze_inputs(data, config)
            plan_corpus = Path(directory) / "finite-plan"
            prep.persist_batch(plan_corpus, [finite], [{}], pipeline, "engineering-smoke",
                               [{"fixture": "actual-bounded-whole-plan"}], False, protection=protection)
            finite_data = PreparedDatasetV2(plan_corpus, "train", config)
            frozen["engineering_plan_manifest_sha256"] = finite_data.manifest_sha256
            frozen["engineering_plan_records_sha256"] = finite_data.records_sha256
            budget = {"max_steps": 1, "max_epochs": 1, "max_roots": 8}
            self.assertFalse(quality_gate(frozen, budget)["accepted"])
            model = StudentV2(config)
            optimizer = torch.optim.AdamW(model.parameters(), lr=config["base_config"]["learning_rate"])
            before = {key: value.clone() for key, value in model.state_dict().items()}
            path = Path(directory) / "checkpoint.pt"
            atomic_save(checkpoint_state(model, optimizer, frozen, budget, 0, 0, 0, [], []), path)
            restored = StudentV2(config)
            restored_optimizer = torch.optim.AdamW(restored.parameters(), lr=config["base_config"]["learning_rate"])
            with self.assertRaisesRegex(ValueError, "budget"):
                restore_checkpoint(path, restored, restored_optimizer, frozen, {**budget, "max_steps": 2})
            state = restore_checkpoint(path, restored, restored_optimizer, frozen, budget)
            self.assertEqual(0, state["optimizer_steps"])
            self.assertTrue(all(torch.equal(before[key], value) for key, value in restored.state_dict().items()))
            # One two-root batch: one prepared synthetic action root and one
            # separately prepared actual whole-plan root, never fitted together.
            batch = [data["train"][0], finite_data[0]]
            losses = [decision_loss(restored(row["public_input"]), row["targets"], row["public_input"], config)[0] for row in batch]
            loss = torch.stack(losses).mean()
            loss.backward()
            self.assertTrue(torch.isfinite(loss))
            gradients = [parameter.grad for parameter in restored.parameters() if parameter.grad is not None]
            self.assertTrue(gradients and all(torch.isfinite(value).all() for value in gradients))
            self.assertTrue(all(torch.equal(before[key], value) for key, value in restored.state_dict().items()))
            self.assertTrue(all(parameter.grad is not None for parameter in restored.plan_heads.parameters()))
            manifest = export_bundle(restored, bundle, frozen=frozen, budget=budget)
            self.assertFalse(manifest["trained"])
            runner = InferenceV2.from_bundle(bundle)
            self.assertEqual("MODEL_UNTRAINED", runner.predict(batch[0]["public_input"])["status"])
            print(json.dumps({"status": "V2_PIPELINE_NO_FIT_ACCEPTANCE_PASS", "prepared_train_roots": len(data["train"]),
                              "backward_calls": 1, "optimizer_steps": 0, "weights_changed": False,
                              "bundle_trained": False, "old_test_targets_read": False, "formal_training_run": False}))


if __name__ == "__main__": unittest.main()
