import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.data import DecisionDataset, validate_record
from nosl.inference import Inference
from nosl.model import Student
from nosl.schema import HEADS, SchemaError, load_config, validate_public
from nosl.train import batch_loss, check_split_isolation, distribution_target, seed_everything, smoke


def card(name="StrikeSilent", kind="Attack"):
    return {"id": name, "upgrade": 0, "cost": 1, "starCost": 0, "type": kind, "keywords": []}


def fixture():
    c = card()
    d = card("DefendSilent", "Skill")
    observation = {"schema": "nosl.public.v1", "startHp": 60, "ascension": 10, "turn": 1, "hp": 60, "maxHp": 70,
                   "block": 0, "energy": 3, "stars": 0, "hand": [c, d], "discard": [], "exhaust": [],
                   "unknownDraw": [{"card": c, "count": 2}, {"card": d, "count": 3}], "knownDraw": [], "drawCount": 5,
                   "potions": ["FirePotion", None], "relics": ["RingOfTheSnake"], "powers": [],
                   "enemies": [{"slot": 0, "id": "TwigSlimeS", "hp": 20, "maxHp": 20, "block": 0,
                                "powers": [], "intents": [{"kind": "Attack", "damage": 5, "repeats": 1}]}],
                   "history": [{"kind": "combat_started", "detail": "Silent:A10"}, {"kind": "player_turn", "detail": "1"}], "choice": None}
    actions = [{"revision": 0, "kind": "play", "slot": 0, "target": 0, "selection": None},
               {"revision": 0, "kind": "play", "slot": 1, "target": -2, "selection": None},
               {"revision": 0, "kind": "end_turn", "slot": -1, "target": -1, "selection": None}]
    public = {"schema_version": "nosl.student.public.v1", "observation": observation, "history_complete": True,
              "controller_context": {"status": "inactive"}, "candidate_actions": actions, "legal_mask": [True] * 3}
    targets = []
    for index in range(3):
        hp = 60 - index * 5
        targets.append({"action_index": index, "quality": "complete", "masks": {head: True for head in HEADS},
                        "value": hp - 60, "win_probability": 1., "death_probability": 0., "expected_final_hp": hp,
                        "hp_distribution": [{"hp": hp, "probability": 1.}], "potion_net_change": 0.,
                        "allocated_worlds": 8, "completed_worlds": 8, "truncated_worlds": 0, "error_worlds": 0, "other_worlds": 0})
    return {"public_input": public, "targets": {"actions": targets, "pairwise": [{"preferred": 0, "other": 2, "weight": .5}], "equivalent_action_set": [0, 1]},
            "audit_only": {"source_run_group": "run-a", "source_combat_id": "combat-a", "branch_family": "branch-a", "public_state_digest": "digest-a", "teacher_version": "T0-test", "objective_version": "synthetic-contract", "continuation_version": "test-policy"}}


class StudentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        seed_everything(cls.config["seed"])
        cls.model = Student(cls.config).eval()

    def setUp(self):
        self.record = fixture()

    def test_validate_fixture(self):
        validate_record(self.record, self.config)

    def test_forward_backward_without_optimizer(self):
        loss, terms = batch_loss(self.model, [self.record, fixture()])
        self.model.zero_grad(set_to_none=True)
        loss.backward()
        self.assertTrue(torch.isfinite(loss))
        self.assertTrue(all(torch.isfinite(p.grad).all() for p in self.model.parameters() if p.grad is not None))
        self.assertEqual(len(terms), 2)

    def test_unknown_draw_order_invariant(self):
        public = self.record["public_input"]
        other = copy.deepcopy(public)
        other["observation"]["unknownDraw"].reverse()
        with torch.no_grad():
            a, b = self.model(public), self.model(other)
        for head in a:
            self.assertTrue(torch.equal(a[head], b[head]), head)

    def test_hand_order_is_observable(self):
        public = self.record["public_input"]
        other = copy.deepcopy(public)
        other["observation"]["hand"].reverse()
        with torch.no_grad():
            self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_known_positions_are_bound_to_cards(self):
        public = self.record["public_input"]
        public["observation"]["unknownDraw"] = []
        public["observation"]["drawCount"] = 2
        public["observation"]["knownDraw"] = [{"position": 0, "card": card()}, {"position": 1, "card": card("DefendSilent", "Skill")}]
        other = copy.deepcopy(public)
        other["observation"]["knownDraw"][0]["position"] = 1
        other["observation"]["knownDraw"][1]["position"] = 0
        with torch.no_grad():
            self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_history_order_is_observable(self):
        public = self.record["public_input"]
        public["observation"]["history"] += [{"kind": "player_turn", "detail": "2"}, {"kind": "player_turn_ended", "detail": ""}]
        other = copy.deepcopy(public)
        other["observation"]["history"][-2:] = reversed(other["observation"]["history"][-2:])
        with torch.no_grad():
            self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_metadata_never_enters_features(self):
        other = copy.deepcopy(self.record)
        other["audit_only"] = {"seed": 88771, "teacher_score": 1e99, "secret_digest": "private"}
        with torch.no_grad():
            self.assertTrue(torch.equal(self.model(self.record["public_input"])["value"], self.model(other["public_input"])["value"]))
        with self.assertRaises(SchemaError):
            self.model(self.record)

    def test_private_unknown_top_field_rejected(self):
        self.record["public_input"]["seed"] = 99
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_private_nested_field_rejected(self):
        self.record["public_input"]["observation"]["hand"][0]["privateState"] = 2
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_private_nested_event_field_rejected(self):
        self.record["public_input"]["observation"]["history"].append({"kind": "draw", "detail": json.dumps({**card(), "seed": 9})})
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_unknown_content_rejected(self):
        self.record["public_input"]["observation"]["hand"][0]["id"] = "UNSUPPORTED_CARD"
        result = Inference(self.config).predict(self.record["public_input"])
        self.assertEqual(result["status"], "UNSUPPORTED")
        self.assertIsNone(result["selected_action"])

    def test_incomplete_history_rejected(self):
        self.record["public_input"]["history_complete"] = False
        self.assertEqual(Inference(self.config).predict(self.record["public_input"])["status"], "INCOMPLETE_HISTORY")

    def test_empty_actions_explicit_rejection(self):
        self.record["public_input"]["candidate_actions"] = []
        self.record["public_input"]["legal_mask"] = []
        self.assertEqual(Inference(self.config).predict(self.record["public_input"])["status"], "NO_DECISION")

    def test_no_random_weight_action(self):
        self.assertEqual(Inference(self.config, self.model).predict(self.record["public_input"])["status"], "MODEL_UNTRAINED")

    def test_unpromoted_weight_action_rejected(self):
        self.assertEqual(Inference(self.config, self.model, {"trained": True}).predict(self.record["public_input"])["status"], "MODEL_UNVALIDATED")

    def test_missing_target_rejected(self):
        del self.record["targets"]["actions"][0]["expected_final_hp"]
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_masked_zero_rejected(self):
        self.record["targets"]["actions"][0]["masks"]["value"] = False
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_unresolved_null_targets_valid_zero_loss(self):
        self.record["targets"]["pairwise"] = []
        self.record["targets"]["equivalent_action_set"] = []
        for row in self.record["targets"]["actions"]:
            row["quality"] = "unresolved"
            row["completed_worlds"], row["truncated_worlds"] = 7, 1
            row.update({head: None for head in HEADS})
            row["masks"] = {head: False for head in HEADS}
        validate_record(self.record, self.config)
        loss, _ = batch_loss(self.model, [self.record])
        self.assertEqual(float(loss.detach()), 0)

    def test_unresolved_mass_cannot_label_completed_subset(self):
        row = self.record["targets"]["actions"][0]
        row["completed_worlds"], row["truncated_worlds"] = 7, 1
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_distribution_probability_mass_checked(self):
        self.record["targets"]["actions"][0]["hp_distribution"][0]["probability"] = .9
        with self.assertRaises(SchemaError): validate_record(self.record, self.config)

    def test_hp_histogram_mean_preserved(self):
        target = distribution_target([{"hp": 0, "probability": .1}, {"hp": 61, "probability": .4}, {"hp": 512, "probability": .5}], self.config, "cpu")
        self.assertAlmostEqual(float(target.sum()), 1.)
        self.assertAlmostEqual(float(target @ torch.linspace(0, 512, self.config["hp_bins"])), 280.4, places=4)

    def test_illegal_candidate_mask(self):
        public = self.record["public_input"]
        public["legal_mask"][0] = False
        with torch.no_grad(): self.assertEqual(float(self.model(public)["ranking_score"][0]), float("-inf"))

    def test_dataset_and_smoke(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "data.jsonl"
            path.write_text(json.dumps(self.record) + "\n")
            dataset = DecisionDataset(path, self.config)
            report = smoke(dataset, self.config)
            self.assertEqual(report["optimizer_steps"], 0)
            self.assertFalse(report["weights_written"])
            self.assertEqual(len(list(Path(temp).iterdir())), 1)

    def test_independent_process_schema_only(self):
        process = subprocess.run([sys.executable, "-m", "nosl.inference", "--config", str(ROOT / "configs/student.pilot.json")],
                                 input=json.dumps(self.record["public_input"]) + "\n", text=True, capture_output=True,
                                 env={"PYTHONPATH": str(ROOT / "python")})
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertEqual(json.loads(process.stdout)["status"], "MODEL_UNTRAINED")



def fixture_v2():
    from nosl.schema import COUNTER_KEYS, DETAIL_BOOLS, DETAIL_INTS
    record = fixture()
    def upgrade(item):
        if isinstance(item, dict):
            if set(item) == {"id", "upgrade", "cost", "starCost", "type", "keywords"}:
                item["details"] = {**{k: False for k in DETAIL_BOOLS}, **{k: 0 for k in DETAIL_INTS}, "starCostThisTurn": None, "energyModifiers": []}
                item["enchantments"], item["affliction"] = [], None
                item["publicState"] = {"targetType": "AnyEnemy" if item["type"] == "Attack" else "Self", "tags": ""}
            else:
                for value in list(item.values()): upgrade(value)
        elif isinstance(item, list):
            for value in item: upgrade(value)
    upgrade(record)
    obs = record["public_input"]["observation"]
    obs.update(schema="nosl.public.v2", counters={key: 0 for key in COUNTER_KEYS},
               relicStates=[{"id": "RingOfTheSnake", "details": {"isWax": 0, "isMelted": 0, "isUsedUp": 0, "stackCount": 1}, "cards": [], "selectedModel": None}],
               gold=99, startGold=99, orbCapacity=0, orbs=[], pets=[], unidentifiedDrawCount=0)
    return record


class ExtendedStudentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        seed_everything(1729)
        cls.model = Student(cls.config).eval()

    def test_v2_validates_and_encodes(self):
        record = fixture_v2()
        validate_record(record, self.config)
        with torch.no_grad(): self.assertEqual(len(self.model(record["public_input"])["value"]), 3)

    def test_v2_missing_required_counters_rejected(self):
        record = fixture_v2()
        record["public_input"]["observation"]["counters"] = None
        with self.assertRaises(SchemaError): validate_record(record, self.config)

    def test_v2_unidentified_draw_mass(self):
        record = fixture_v2()
        obs = record["public_input"]["observation"]
        obs["unidentifiedDrawCount"], obs["drawCount"] = 2, 7
        obs["history"].append({"kind": "hidden_card_generated", "detail": "draw"})
        validate_record(record, self.config)
        obs["drawCount"] = 5
        with self.assertRaises(SchemaError): validate_record(record, self.config)

    def test_v2_private_card_state_rejected(self):
        record = fixture_v2()
        record["public_input"]["observation"]["hand"][0]["publicState"]["rng"] = "1001"
        with self.assertRaises(SchemaError): validate_record(record, self.config)

    def test_v2_private_relic_state_rejected(self):
        record = fixture_v2()
        record["public_input"]["observation"]["relicStates"][0]["details"]["seed"] = 3
        with self.assertRaises(SchemaError): validate_record(record, self.config)

    def test_v2_temp_cost_changes_encoding(self):
        public = fixture_v2()["public_input"]
        other = copy.deepcopy(public)
        other["observation"]["hand"][0]["details"]["freeThisTurn"] = True
        with torch.no_grad(): self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_v2_counters_change_encoding(self):
        public = fixture_v2()["public_input"]
        other = copy.deepcopy(public)
        other["observation"]["counters"]["attacksPlayed"] = 2
        with torch.no_grad(): self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_v2_power_timing_changes_encoding(self):
        public = fixture_v2()["public_input"]
        public["observation"]["powers"] = [{"id": "WeakPower", "amount": 2, "amountOnTurnStart": 2, "skipNextDurationTick": False,
                                               "selectedCard": None, "selectedUpgrade": None, "applierSlot": 0}]
        other = copy.deepcopy(public)
        other["observation"]["powers"][0]["skipNextDurationTick"] = True
        with torch.no_grad(): self.assertFalse(torch.equal(self.model(public)["value"], self.model(other)["value"]))

    def test_discard_potion_action(self):
        public = fixture_v2()["public_input"]
        public["candidate_actions"] = [{"revision": 0, "kind": "discard_potion", "slot": 0, "target": -1, "selection": None}]
        public["legal_mask"] = [True]
        with torch.no_grad(): self.assertEqual(len(self.model(public)["value"]), 1)

    def test_v2_public_damage_event_slots(self):
        record = fixture_v2()
        record["public_input"]["observation"]["history"].append({"kind": "damage", "detail": json.dumps({"target": "player", "targetSlot": -2, "sourceSlot": 0, "blocked": 0, "unblocked": 5, "overkill": 0, "hpAfter": 55, "killed": False})})
        validate_record(record, self.config)

    def test_automatic_selection_result_only(self):
        record = fixture_v2()
        record["public_input"]["observation"]["history"].append({"kind": "automatic_selection", "detail": json.dumps({"source": "Prepared", "cards": [], "unidentifiedCount": 1})})
        validate_record(record, self.config)

    def test_zero_supervision_weight_masks_regression(self):
        record = fixture()
        record["targets"]["pairwise"] = []
        record["targets"]["equivalent_action_set"] = []
        for row in record["targets"]["actions"]: row["sample_weight"] = 0
        loss, _ = batch_loss(self.model, [record])
        self.assertEqual(float(loss.detach()), 0)

    def test_metrics_have_counts_and_no_calibration_claim(self):
        from nosl.train import evaluate
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "data.jsonl"
            path.write_text(json.dumps(fixture()) + "\n")
            result = evaluate(self.model, DecisionDataset(path, self.config))
            self.assertEqual(result["hp_mae"]["count"], 3)
            self.assertEqual(result["pairwise_agreement"]["count"], 1)
            self.assertFalse(result["probabilities_calibrated"])

    def test_checkpoint_roundtrip_untrained_optimizer_rng(self):
        from nosl.train import atomic_save, checkpoint_state, restore_checkpoint
        optimizer = torch.optim.AdamW(self.model.parameters(), lr=.001)
        frozen, budget = {"hash": "example"}, {"max_steps": 1, "max_epochs": 1, "max_roots": 1}
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "engineering-only.pt"
            state = checkpoint_state(self.model, optimizer, frozen, budget, 0, 0, 0, [], [])
            atomic_save(state, path)
            restored = restore_checkpoint(path, self.model, optimizer, frozen, budget)
            self.assertEqual(restored["optimizer_steps"], 0)
            self.assertFalse(restored["promoted"])
            with self.assertRaises(SchemaError): restore_checkpoint(path, self.model, optimizer, {"hash": "changed"}, budget)
            with self.assertRaises(SchemaError): restore_checkpoint(path, self.model, optimizer, frozen, {**budget, "max_steps": 2})
        # The temporary untrained serialization is deleted. No optimizer step occurred.

    def test_pilot_authorization_and_bounds(self):
        from nosl.train import pilot_train, validate_pilot_budget
        with self.assertRaises(SchemaError): pilot_train({}, self.config, Path("must-not-exist"), budget={})
        with self.assertRaises(SchemaError): validate_pilot_budget(10, 1, 5, 6)
        with self.assertRaises(SchemaError): validate_pilot_budget(10, 6, 5, 5)
        self.assertEqual(validate_pilot_budget(10, 1, 5, 5)["max_steps"], 10)

    def test_formal_training_cli_disabled(self):
        process = subprocess.run([sys.executable, "-m", "nosl.train", "--config", str(ROOT / "configs/student.pilot.json"), "--data", "unused", "--mode", "formal"], text=True, capture_output=True,
                                 env={"PYTHONPATH": str(ROOT / "python")})
        self.assertNotEqual(process.returncode, 0)
        self.assertIn("formal training is disabled", process.stderr)

    def test_vocabulary_registry_not_curated_ceiling(self):
        from nosl.vocabulary import generate
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "registry.json"
            categories = {"CARD": "NewPublicCard", "MONSTER": "NewPublicMonster", "POTION": "NewPublicPotion", "RELIC": "NewPublicRelic", "POWER": "NewPublicPower"}
            path.write_text(json.dumps({"schema": "nosl.coverage.v2", "upstream": {"commit": "test-only"}, "entries": [{"id": value, "category": key, "status": "ImplementedUnverified"} for key, value in categories.items()]}))
            config = generate(path, self.config)
            self.assertIn("NewPublicCard", config["supported_cards"])
            self.assertFalse(config["full_content_ready"])
            self.assertFalse(config["formal_training_authorized"])


class CrossLanguageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        seed_everything(1729)
        cls.model = Student(cls.config).eval()

    def test_real_csharp_teacher_loader_backward(self):
        dataset = DecisionDataset(ROOT / "tests/python/fixtures/real-teacher-v1.jsonl", self.config)
        report = smoke(dataset, self.config)
        self.assertEqual(report["candidates"], 7)
        self.assertEqual(report["optimizer_steps"], 0)
        result = Inference(self.config).predict(dataset[0]["public_input"])
        self.assertEqual(result["status"], "MODEL_UNTRAINED")

    def test_real_csharp_v2_observation_forward(self):
        packet = json.loads((ROOT / "tests/python/fixtures/public-v2-reset.jsonl").read_text().splitlines()[0])
        public = {"schema_version": "nosl.student.public.v1", "observation": packet["observation"],
                  "candidate_actions": packet["actions"], "legal_mask": [True] * len(packet["actions"]),
                  "history_complete": True, "controller_context": {"status": "inactive"}}
        validate_public(public, self.config)
        with torch.no_grad():
            self.assertTrue(torch.isfinite(self.model(public)["value"]).all())

    def test_ordered_multi_selection_is_not_pooled_away(self):
        public = fixture_v2()["public_input"]
        public["observation"]["choice"] = {"source": "Prepared", "min": 2, "max": 2, "cancelable": False,
                                              "candidates": public["observation"]["hand"], "candidateOrder": "public", "bundles": None}
        public["candidate_actions"] = [{"revision": 1, "kind": "choose", "slot": -1, "target": -1, "selection": [0, 1]},
                                        {"revision": 1, "kind": "choose", "slot": -1, "target": -1, "selection": [1, 0]}]
        public["legal_mask"] = [True, True]
        with torch.no_grad():
            output = self.model(public)
            self.assertNotEqual(float(output["value"][0]), float(output["value"][1]))

    def test_frozen_group_overlap_and_version_checks(self):
        from nosl.train import freeze_inputs
        config = self.config
        with tempfile.TemporaryDirectory() as temp:
            datasets = {}
            for split in ("train", "validation", "test"):
                record = fixture()
                record["public_input"]["observation"]["block"] = ("train", "validation", "test").index(split)
                record["audit_only"].update(source_run_group=split, source_combat_id=split, branch_family=split, public_state_digest=split,
                                             simulator_commit="pinned", rules_version="v1", label_endpoint="settled",
                                             versions={"teacher": "T0-test", "continuation": "test-policy", "objective": "synthetic-contract", "public_schema": "nosl.student.public.v1", "simulator": "pinned", "observation_schema": "nosl.public.v1"})
                path = Path(temp) / (split + ".jsonl")
                path.write_text(json.dumps(record) + "\n")
                datasets[split] = DecisionDataset(path, config)
            frozen = freeze_inputs(datasets, config)
            self.assertFalse(frozen["test_used_for_model_selection"])
            datasets["test"].records[0]["audit_only"]["source_run_group"] = "train"
            with self.assertRaises(SchemaError): freeze_inputs(datasets, config)
            datasets["test"].records[0]["audit_only"]["source_run_group"] = "test"
            datasets["test"].records[0]["audit_only"]["objective_version"] = "changed"
            with self.assertRaises(SchemaError): freeze_inputs(datasets, config)

    def test_standalone_dependency_import_graph(self):
        script = "import sys; import nosl.inference; forbidden=[name for name in sys.modules if any(word in name.lower() for word in ('simulator','teacher','searcher'))]; assert not forbidden, forbidden"
        process = subprocess.run([sys.executable, "-c", script], text=True, capture_output=True, cwd=tempfile.gettempdir(),
                                 env={"PYTHONPATH": str(ROOT / "python")})
        self.assertEqual(process.returncode, 0, process.stderr)



class ArtifactGuardsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")

    def test_prepared_manifest_shard_and_frozen_test_guards(self):
        from nosl.data import prepared_dataset, prepared_paths
        import hashlib
        def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            from nosl.public_identity import PUBLIC_IDENTITY_SCHEME
            lock, versions = {"pipeline_version": "nosl.dataset.prepare.v3", "student_config_sha256": "abc", "public_identity_scheme": PUBLIC_IDENTITY_SCHEME}, {"teacher": "test-only"}
            files = []
            for split in ("train", "validation", "test"):
                path = root / (split + ".jsonl")
                path.write_text(json.dumps(fixture()) + "\n")
                files.append({"path": path.name, "kind": split, "bytes": path.stat().st_size, "sha256": sha(path), "rows": 1})
            path = root / "state.json"
            path.write_text(json.dumps({"schema_version": "nosl.dataset.split-state.v2", "lock": lock, "versions": versions, "stage_count": 1, "observation_schema": "nosl.public.v1", "cross_split_conflicts": []}))
            state = {"path": path.name, "kind": "state", "bytes": path.stat().st_size, "sha256": sha(path), "rows": 0}
            files.append(state)
            stage = {"schema_version": "nosl.dataset.stage.v2", "lock": lock, "versions": versions, "parent_stage_sha256": None, "files": files, "observation_schema": "nosl.public.v1"}
            stage_path = root / "stage.json"
            stage_path.write_text(json.dumps(stage))
            manifest = {"schema_version": "nosl.dataset.manifest.v2", "pipeline_version": "nosl.dataset.prepare.v3", "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
                        "isolation_passed": True, "lock": lock, "versions": versions, "observation_schema": "nosl.public.v1",
                        "stages": [{"id": "test-stage", "manifest": "stage.json", "sha256": sha(stage_path)}], "latest_state": state,
                        "frozen_test_shards": [files[2]]}
            manifest_path = root / "manifest.json"
            manifest_path.write_text(json.dumps(manifest))
            dataset = prepared_dataset(root, "test", self.config, "abc")
            self.assertEqual(len(dataset), 1)
            manifest["lock"].pop("pipeline_version")
            manifest_path.write_text(json.dumps(manifest))
            with self.assertRaisesRegex(SchemaError, "identity scheme unsupported"):
                prepared_paths(root, "train")
            manifest["lock"]["pipeline_version"] = "nosl.dataset.prepare.v3"
            manifest_path.write_text(json.dumps(manifest))
            with self.assertRaises(SchemaError): prepared_paths(root, "train", "wrong")
            manifest["frozen_test_shards"] = []
            manifest_path.write_text(json.dumps(manifest))
            with self.assertRaises(SchemaError): prepared_paths(root, "test")
            manifest["frozen_test_shards"] = [files[2]]
            manifest_path.write_text(json.dumps(manifest))
            (root / "train.jsonl").write_text("tampered\n")
            with self.assertRaises(SchemaError): prepared_paths(root, "test")

    def test_bundle_weight_and_config_hash_guards_no_training(self):
        import hashlib
        from nosl.train import config_hash
        model = Student(self.config)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "config.json").write_text(json.dumps(self.config))
            torch.save(model.state_dict(), root / "weights.pt")
            manifest = {"format": "nosl.student.bundle.v1", "public_schema": "nosl.student.public.v1", "trained": False,
                        "config_sha256": config_hash(self.config), "weights_sha256": hashlib.sha256((root / "weights.pt").read_bytes()).hexdigest()}
            (root / "manifest.json").write_text(json.dumps(manifest))
            runner = Inference.from_bundle(root)
            self.assertEqual(runner.predict(fixture()["public_input"])["status"], "MODEL_UNTRAINED")
            manifest["weights_sha256"] = "wrong"
            (root / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(SchemaError): Inference.from_bundle(root)
            manifest["config_sha256"] = "wrong"
            (root / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(SchemaError): Inference.from_bundle(root)

    def test_v1_cannot_silently_admit_v2_mechanic(self):
        record = fixture()
        record["public_input"]["observation"]["hand"][0]["id"] = "Footwork"
        with self.assertRaises(SchemaError): validate_record(record, self.config)

    def test_multichoice_bundle_content_is_encoded(self):
        seed_everything(1729)
        model = Student(self.config)
        public = fixture_v2()["public_input"]
        cards = public["observation"]["hand"]
        public["observation"]["choice"] = {"source": "Prepared", "min": 1, "max": 1, "cancelable": False,
                                              "candidates": cards, "candidateOrder": "public", "bundles": [[cards[0]], [cards[1]]]}
        public["candidate_actions"] = [{"revision": 1, "kind": "choose", "slot": -1, "target": -1, "selection": [0]}]
        public["legal_mask"] = [True]
        other = copy.deepcopy(public)
        other["observation"]["choice"]["bundles"][0] = [copy.deepcopy(cards[1])]
        with torch.no_grad(): self.assertFalse(torch.equal(model(public)["value"], model(other)["value"]))



class PreparedIntegrationRegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")
        sys.path.insert(0, str(ROOT / "tools"))
        import prepare_dataset
        cls.pipeline = prepare_dataset

    def test_prepared_config_digest_matches_pipeline_canonical_contract(self):
        from nosl.data import canonical_object_digest
        for value in (self.config, {"count": 1.0, "nested": [2., "文字"], "flag": True}):
            self.assertEqual(canonical_object_digest(value), self.pipeline.object_digest(value))
        self.assertEqual(canonical_object_digest({"a": 1.0}), canonical_object_digest({"a": 1}))

    def test_real_v2_teacher_record_prepared_to_cli_backward_no_optimizer(self):
        record_path = ROOT / "tests/python/fixtures/real-teacher-v2.jsonl"
        record = json.loads(record_path.read_text().splitlines()[0])
        pipeline_config = json.loads((ROOT / "configs/data_pipeline.v1.json").read_text())
        # Fixture-only split seed: put the sole real root in train so --smoke
        # can exercise the actual prepared path. This is not a fitted corpus.
        for seed in range(100):
            pipeline_config["split_seed"] = f"m6-cli-fixture-{seed}"
            splits, _, _ = self.pipeline.prepare([record], pipeline_config, "engineering-smoke", student_config=self.config)
            if splits["train"]:
                break
        else:
            self.fail("could not assign engineering fixture to train")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            config_path = root / "pipeline.json"
            config_path.write_text(json.dumps(pipeline_config))
            prepared = root / "prepared"
            setup = subprocess.run([sys.executable, str(ROOT / "tools/prepare_dataset.py"), str(record_path), "--config", str(config_path),
                                    "--output-dir", str(prepared), "--mode", "engineering-smoke"], capture_output=True, text=True, cwd=ROOT)
            self.assertEqual(setup.returncode, 0, setup.stderr)
            # Different whitespace/numeric notation in a semantically identical
            # student config must not break the canonical M5 config lock.
            student_config = root / "student-compact.json"
            student_config.write_text(json.dumps(self.config, separators=(",", ":")))
            result = subprocess.run([sys.executable, "-m", "nosl.train", "--config", str(student_config), "--prepared", str(prepared), "--mode", "smoke"],
                                    capture_output=True, text=True, cwd=ROOT, env={"PYTHONPATH": str(ROOT / "python")})
            self.assertEqual(result.returncode, 0, result.stderr)
            outcome = json.loads(result.stdout)
            self.assertEqual(outcome["status"], "SINGLE_BATCH_FORWARD_BACKWARD_PASS")
            self.assertEqual(outcome["optimizer_steps"], 0)
            self.assertFalse(outcome["weights_written"])
            self.assertEqual(outcome["candidates"], len(record["public_input"]["candidate_actions"]))

    def test_t1_root_specific_frozen_hashes_preserve_shared_family(self):
        from nosl.train import freeze_inputs
        original = json.loads((ROOT / "tests/python/fixtures/real-teacher-v2.jsonl").read_text().splitlines()[0])
        with tempfile.TemporaryDirectory() as temp:
            datasets, original_hashes = {}, {}
            for split in ("train", "validation", "test"):
                record = copy.deepcopy(original)
                # Synthetic distinct public roots for the continuation-family guard.
                record["public_input"]["observation"]["gold"] += ("train", "validation", "test").index(split)
                audit = record["audit_only"]
                for key in ("source_run_group", "source_combat_id", "branch_family", "public_state_digest"): audit[key] = split
                audit["teacher_version"] = audit["versions"]["teacher"] = "teacher:T1"
                audit["versions"]["continuation"] = "nosl-public-uct-frozen-v1"
                audit["continuation_version"] = "nosl-public-uct-frozen-v1:" + split + "-hash"
                original_hashes[split] = audit["continuation_version"]
                path = Path(temp) / (split + ".jsonl")
                path.write_text(json.dumps(record) + "\n")
                datasets[split] = DecisionDataset(path, self.config)
            frozen = freeze_inputs(datasets, self.config)
            self.assertEqual(frozen["versions"]["continuation_version"], "nosl-public-uct-frozen-v1")
            for split, dataset in datasets.items():
                self.assertEqual(dataset[0]["audit_only"]["continuation_version"], original_hashes[split])
            datasets["test"][0]["audit_only"]["continuation_version"] += "-changed"
            changed = freeze_inputs(datasets, self.config)
            self.assertNotEqual(frozen["per_root_continuation_audit_sha256"], changed["per_root_continuation_audit_sha256"])
            datasets["test"][0]["audit_only"]["versions"]["continuation"] = "different-family"
            with self.assertRaises(SchemaError): freeze_inputs(datasets, self.config)



class RawSplitBypassTests(unittest.TestCase):
    def test_same_real_public_root_with_forged_distinct_group_ids_rejected(self):
        from nosl.train import freeze_inputs
        config = load_config(ROOT / "configs/student.pilot.json")
        original = json.loads((ROOT / "tests/python/fixtures/real-teacher-v2.jsonl").read_text().splitlines()[0])
        with tempfile.TemporaryDirectory() as temp:
            datasets = {}
            for split in ("train", "validation", "test"):
                record = copy.deepcopy(original)
                for key in ("source_run_group", "source_combat_id", "branch_family", "public_state_digest"):
                    record["audit_only"][key] = "forged-" + split
                if split == "validation":
                    # Numeric formatting is not a distinct observation.
                    record["public_input"]["observation"]["block"] = float(record["public_input"]["observation"]["block"])
                path = Path(temp) / (split + ".jsonl")
                path.write_text(json.dumps(record) + "\n")
                datasets[split] = DecisionDataset(path, config)
            with self.assertRaisesRegex(SchemaError, "recomputed public_input"):
                freeze_inputs(datasets, config)



class ImmutableIsolationRegressionTests(unittest.TestCase):
    def test_top_manifest_flag_cannot_erase_immutable_cross_split_conflict(self):
        from nosl.data import prepared_paths, canonical_object_digest
        sys.path.insert(0, str(ROOT / "tools"))
        import prepare_dataset as pipeline
        config = json.loads((ROOT / "configs/data_pipeline.v1.json").read_text())
        student = load_config(ROOT / "configs/student.pilot.json")
        base = json.loads((ROOT / "tests/python/fixtures/real-teacher-v2.jsonl").read_text().splitlines()[0])
        def synthetic_root(number):
            record = copy.deepcopy(base)
            record["public_input"]["observation"]["gold"] = number
            for key in ("source_run_group", "source_combat_id", "branch_family"):
                record["audit_only"][key] = key + ":" + str(number)
            record["audit_only"]["public_state_digest"] = canonical_object_digest(record["public_input"])
            return record
        roots = {}
        for number in range(200):
            record = synthetic_root(number)
            groups, _, _ = pipeline.provenance_components([record])
            roots.setdefault(pipeline.choose_split(groups[0], config), record)
            if len(roots) == 3: break
        self.assertEqual(len(roots), 3)
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            def persist(records, resume, tag):
                return pipeline.persist_batch(output, records, [{"path": tag, "line": i + 1} for i in range(len(records))], config,
                                              "engineering-smoke", [{"path": tag, "sha256": pipeline.object_digest(records), "bytes": 0}], resume, 1000)
            persist(list(roots.values()), False, "initial")
            self.assertTrue(prepared_paths(output, "train")[0])
            bridge = synthetic_root(999)
            bridge["audit_only"]["source_run_group"] = roots["train"]["audit_only"]["source_run_group"]
            bridge["audit_only"]["branch_family"] = roots["test"]["audit_only"]["branch_family"]
            persist([bridge], True, "historical-bridge")
            path = output / "manifest.json"
            manifest = json.loads(path.read_text())
            self.assertFalse(manifest["isolation_passed"])
            with self.assertRaises(SchemaError): prepared_paths(output, "train")
            # Only the mutable root flag is changed. Immutable state, stage and
            # shard hashes remain exactly as produced by the real pipeline.
            manifest["isolation_passed"] = True
            path.write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError, "manifest_isolation_state_mismatch"):
                pipeline.verify_manifest(output)
            with self.assertRaisesRegex(SchemaError, "manifest isolation state mismatch"):
                prepared_paths(output, "train")



class DecisionEnvelopeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = load_config(ROOT / "configs/student.pilot.json")

    def runner_that_must_not_call_model(self):
        class NeverCalled:
            def eval(self): return self
            def __call__(self, _): raise AssertionError("control/rejected envelope called model")
        return Inference(self.config, NeverCalled(), {"trained": True, "status": "PROMOTED", "calibrated": True})

    def test_terminal_explicit_without_model_call(self):
        result = self.runner_that_must_not_call_model().predict({"decision_status": "terminal", "public_input": None})
        self.assertEqual(result["status"], "TERMINAL")
        self.assertIsNone(result["selected_action"])
        self.assertEqual(result["predictions"], [])

    def test_waiting_explicit_without_model_call(self):
        result = self.runner_that_must_not_call_model().predict({"decision_status": "waiting", "public_input": None})
        self.assertEqual(result["status"], "WAITING")
        self.assertIsNone(result["selected_action"])
        self.assertEqual(result["predictions"], [])

    def test_terminal_waiting_require_null_payload(self):
        runner = self.runner_that_must_not_call_model()
        for status in ("terminal", "waiting"):
            for payload in ({}, fixture()["public_input"], [], False):
                with self.subTest(status=status, payload=type(payload).__name__):
                    self.assertEqual(runner.predict({"decision_status": status, "public_input": payload})["status"], "INVALID_INPUT")

    def test_active_requires_non_null_public_input(self):
        runner = self.runner_that_must_not_call_model()
        for status in ("player_decision", "card_choice"):
            self.assertEqual(runner.predict({"decision_status": status, "public_input": None})["status"], "INVALID_INPUT")

    def test_unknown_extra_missing_or_nested_envelope_rejected(self):
        runner = self.runner_that_must_not_call_model()
        bad = [{"decision_status": "finished", "public_input": None},
               {"decision_status": ["terminal"], "public_input": None},
               {"decision_status": "terminal", "public_input": None, "seed": 123},
               {"decision_status": "terminal"}, {"public_input": None},
               {"decision_status": "player_decision", "public_input": {"decision_status": "waiting", "public_input": None}}]
        for value in bad:
            with self.subTest(value=value):
                result = runner.predict(value)
                self.assertEqual(result["status"], "INVALID_INPUT")
                self.assertIsNone(result["selected_action"])

    def test_player_boundary_matches_unwrapped_public_input(self):
        runner = Inference(self.config)
        public = fixture()["public_input"]
        self.assertEqual(runner.predict(public), runner.predict({"decision_status": "player_decision", "public_input": public}))
        self.assertEqual(runner.predict({"decision_status": "card_choice", "public_input": public})["status"], "INVALID_INPUT")

    def test_choice_boundary_matches_unwrapped_public_input(self):
        runner = Inference(self.config)
        public = fixture()["public_input"]
        public["observation"]["choice"] = {"source": "Prepared", "min": 1, "max": 1, "cancelable": False, "candidates": public["observation"]["hand"]}
        public["candidate_actions"] = [{"revision": 0, "kind": "choose", "slot": -1, "target": -1, "selection": [0]}]
        public["legal_mask"] = [True]
        self.assertEqual(runner.predict(public), runner.predict({"decision_status": "card_choice", "public_input": public}))
        self.assertEqual(runner.predict({"decision_status": "player_decision", "public_input": public})["status"], "INVALID_INPUT")

    def test_empty_candidates_do_not_imply_terminal_or_waiting(self):
        runner = self.runner_that_must_not_call_model()
        public = fixture()["public_input"]
        public["candidate_actions"], public["legal_mask"] = [], []
        plain = runner.predict(public)
        self.assertEqual(plain["status"], "NO_DECISION")  # legacy behavior unchanged
        for status in ("player_decision", "card_choice"):
            result = runner.predict({"decision_status": status, "public_input": public})
            self.assertEqual(result["status"], "INVALID_INPUT")
            self.assertIsNone(result["selected_action"])

    def test_jsonl_cli_control_and_plain_inputs(self):
        public = fixture()["public_input"]
        inputs = [{"decision_status": "terminal", "public_input": None},
                  {"decision_status": "waiting", "public_input": None},
                  {"decision_status": "player_decision", "public_input": public}, public,
                  {"decision_status": "terminal", "public_input": public}]
        result = subprocess.run([sys.executable, "-m", "nosl.inference", "--config", str(ROOT / "configs/student.pilot.json")],
                                input="".join(json.dumps(value) + "\n" for value in inputs), text=True, capture_output=True,
                                cwd=ROOT, env={"PYTHONPATH": str(ROOT / "python")})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([json.loads(line)["status"] for line in result.stdout.splitlines()],
                         ["TERMINAL", "WAITING", "MODEL_UNTRAINED", "MODEL_UNTRAINED", "INVALID_INPUT"])


if __name__ == "__main__":
    unittest.main()
