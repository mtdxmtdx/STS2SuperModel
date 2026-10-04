"""Synthetic schema/forward/loss checks only. No backward, optimizer or fit."""
from copy import deepcopy
import hashlib
import json
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
from test_public_evidence_v4 import declared_public, old_public, unavailable_targets
from nosl import schema_v4
from nosl.data_v5 import (RECORD_KIND, RECORD_SCHEMA, QUARANTINE_REASON, validate_record,
                          validate_native_source_record, validate_production_record, validate_targets)
from nosl.inference_v4 import implementation_fingerprint as v4_fingerprint
from nosl.inference_v5 import BUNDLE_FORMAT, InferenceV5, implementation_fingerprint
from nosl.loss_v5 import decision_loss
from nosl.model_v4 import StudentV4, public_evidence_features
from nosl.model_v5 import (StudentV5, MAP_SUMMARY_NAMES, GRAPH_SUMMARY_NAMES, NODE_NAMES, EDGE_NAMES, public_map_features)
from nosl.public_identity_v4 import public_input_digest as v4_digest
from nosl.public_identity_v5 import canonical_public_input, public_input_digest, legacy_alias_digests
from nosl.schema import HEADS, SchemaError
from nosl.schema_v2 import PLAN_HEADS
from nosl.schema_v5 import (EVIDENCE_SCHEMA, MODEL_VERSION, PUBLIC_SCHEMA, _v4_mechanics_projection,
                            load_config, loads, validate_config, validate_public)


def coordinate(col, row): return {"col": col, "row": row}


def graph_payload(status="complete"):
    start, a, b, c, d, boss = [coordinate(col, row) for col, row in ((3, 0), (2, 1), (4, 1), (2, 2), (4, 2), (3, 3))]
    def node(point, kind): return {"coordinate": point, "nodeType": kind}
    def edge(a, b): return {"from": a, "to": b}
    nodes = [node(start, "ancient"), node(a, "monster"), node(b, "monster")]
    capture = {"status": status, "nodes": deepcopy(nodes) + [node(c, "unknown"), node(d, "shop"), node(boss, "boss")],
               "edges": [edge(start, a), edge(start, b), edge(a, c), edge(a, d), edge(b, d), edge(c, boss), edge(d, boss)],
               "startingNode": start, "bossNodes": [boss]}
    if status == "missing": capture.update(nodes=[], edges=[], startingNode=None, bossNodes=[])
    return {"kind": "map", "current": start, "nodes": nodes, "edges": [edge(start, a), edge(start, b)],
            "options": [{"coordinate": a, "isOrdinaryConnection": True}, {"coordinate": b, "isOrdinaryConnection": True}],
            "currentMap": capture}


def public_v5(status="complete", *, source=None, include_map=True):
    public = declared_public(source=source)
    def extend(packet):
        packet["schema_version"] = PUBLIC_SCHEMA
        evidence = packet["public_evidence"]; evidence["schemaVersion"] = EVIDENCE_SCHEMA
        if include_map:
            events = evidence["events"]
            for event in events[1:]:
                event["eventOrdinal"] += 4
                if event["ownerOrdinal"] is not None: event["ownerOrdinal"] += 1
                for field in ("offerEventOrdinal", "decisionEventOrdinal", "historyThroughEventOrdinal", "replacesOfferEventOrdinal"):
                    if event["payload"].get(field) is not None: event["payload"][field] += 4
                if event["payload"].get("parentOwnerOrdinal") is not None: event["payload"]["parentOwnerOrdinal"] += 1
            payload = graph_payload(status)
            prefix = [{"kind": "owner_started", "ownerKind": "map", "actIndex": 0, "floor": 0,
                       "parentOwnerOrdinal": None, "completeFromOwnerStart": True}, payload,
                      {"kind": "map_chosen", "offerEventOrdinal": 2, "coordinate": payload["options"][0]["coordinate"]},
                      {"kind": "owner_ended", "outcome": "completed", "assets": None}]
            events[1:1] = [{"eventOrdinal": i + 1, "ownerOrdinal": 0, "payload": value} for i, value in enumerate(prefix)]
        anchor = packet["controller_context"].get("anchor")
        if anchor is not None: extend(anchor)
    extend(public)
    return public


class CompleteMapV5Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.config = load_config(ROOT / "configs/student.v5.engineering.json")

    def setUp(self): self.public = public_v5()

    def check_bad(self, mutate, pattern="."):
        changed = deepcopy(self.public); mutate(changed)
        with self.assertRaisesRegex(SchemaError, pattern): validate_public(changed, self.config)

    @staticmethod
    def capture(public): return public["public_evidence"]["events"][2]["payload"]["currentMap"]

    def test_explicit_versions_and_complete_missing_no_map(self):
        for public in (self.public, public_v5("missing"), public_v5(include_map=False)):
            self.assertIs(validate_public(public, self.config), public)
            self.assertEqual(InferenceV5(self.config).predict(public)["status"], "MODEL_UNTRAINED")
        self.assertEqual(self.config["base_config"], schema_v4.load_config(ROOT / "configs/student.v4.engineering.json"))
        with self.assertRaises(SchemaError): validate_config(self.config["base_config"])
        with self.assertRaises(SchemaError): validate_public(declared_public(), self.config)
        with self.assertRaises(SchemaError): schema_v4.validate_public(self.public, self.config["base_config"])
        self.check_bad(lambda p: p["public_evidence"].update(schemaVersion="nosl.public-run-evidence.v1"))
        self.check_bad(lambda p: p["public_evidence"]["events"][2]["payload"].pop("currentMap"), "require")
        self.check_bad(lambda p: p["public_evidence"]["events"][2]["payload"].update(currentMap=None))

    def test_missing_never_accepts_partial_or_unknown_status(self):
        for field, value in (("nodes", [self.capture(self.public)["nodes"][0]]), ("edges", self.capture(self.public)["edges"][:1]),
                             ("bossNodes", [coordinate(3, 3)]), ("startingNode", coordinate(3, 0))):
            public = public_v5("missing"); self.capture(public)[field] = value
            with self.assertRaisesRegex(SchemaError, "partial"): validate_public(public, self.config)
        for status in ("partial", "Complete", "unknown", 1):
            self.check_bad(lambda p: self.capture(p).update(status=status))

    def test_graph_structural_guards_and_bounds(self):
        mutations = [lambda c: c["nodes"].reverse(), lambda c: c["edges"].reverse(),
                     lambda c: c["nodes"].append(deepcopy(c["nodes"][-1])),
                     lambda c: c["edges"].append(deepcopy(c["edges"][-1])),
                     lambda c: c.update(startingNode=coordinate(8, 0)), lambda c: c.update(bossNodes=[]),
                     lambda c: c.update(bossNodes=[coordinate(2, 2)]),
                     lambda c: c["edges"][0].update(to=coordinate(9, 9)),
                     lambda c: c["edges"][0].update(to=c["edges"][0]["from"]),
                     lambda c: c["edges"].pop(4),  # disconnect the b path from any boss
                     lambda c: c["nodes"][-1].update(nodeType="unknown"),
                     lambda c: c["nodes"][3].update(nodeType="hidden_event")]
        for mutate in mutations:
            with self.subTest(mutate=mutate): self.check_bad(lambda p: mutate(self.capture(p)))
        config = deepcopy(self.config); config["map_max_nodes"] = 5
        with self.assertRaises(SchemaError): validate_public(self.public, config)
        config = deepcopy(self.config); config["map_max_edges"] = 6
        with self.assertRaises(SchemaError): validate_public(self.public, config)

    def test_slice_agreement_and_canonical_profile(self):
        self.check_bad(lambda p: self.capture(p)["nodes"][1].update(nodeType="elite"), "slice nodes")
        for field in ("nodes", "edges", "options"):
            self.check_bad(lambda p: p["public_evidence"]["events"][2]["payload"][field].reverse(), "canonical")
        # A valid old slice can still omit a full-graph edge: v5 rejects this.
        def remove_edge(public):
            payload = public["public_evidence"]["events"][2]["payload"]
            payload["edges"].pop(); payload["options"][-1]["isOrdinaryConnection"] = False
        self.check_bad(remove_edge, "slice edges")

    def test_hidden_fields_and_transport_reject(self):
        for key in ("eventId", "encounterId", "rngState", "mapSeed", "nativeInsertionOrder", "rawHash", "actIndex"):
            self.check_bad(lambda p: self.capture(p).update({key: "private"}), "fields")
            self.check_bad(lambda p: self.capture(p)["nodes"][3].update({key: "private"}), "fields")
        for text in ('{"status":"complete","status":"missing"}', '{"value":NaN}', '{"value":Infinity}'):
            with self.assertRaises(SchemaError): loads(text)
        self.check_bad(lambda p: self.capture(p)["nodes"][3]["coordinate"].update(row=10**1000))

    def test_identity_json_property_invariance_and_full_graph_sensitivity(self):
        original = public_input_digest(self.public)
        self.assertEqual(original, public_input_digest(json.loads(json.dumps(self.public, sort_keys=True))))
        changed = deepcopy(self.public); self.capture(changed)["nodes"][4]["nodeType"] = "rest"
        validate_public(changed, self.config)
        self.assertNotEqual(original, public_input_digest(changed))
        self.assertTrue(legacy_alias_digests(self.public) & legacy_alias_digests(changed))
        self.assertIn(v4_digest(_v4_mechanics_projection(self.public)), legacy_alias_digests(self.public))
        self.assertEqual(canonical_public_input(self.public)["public_evidence"]["events"][2]["payload"]["currentMap"]["nodes"][3]["nodeType"], "unknown")
        for actions in (changed["candidate_actions"], changed["public_evidence"]["events"][-1]["payload"]["actions"]):
            for action in actions: action["revision"] += 10
        self.capture(changed)["nodes"][4]["nodeType"] = "shop"
        self.assertEqual(original, public_input_digest(changed))

    def test_semantic_features_capture_every_entity_and_availability(self):
        (summary, graphs), anchor = public_map_features(self.public)
        self.assertIsNone(anchor); self.assertEqual(len(summary), len(MAP_SUMMARY_NAMES))
        self.assertEqual(len(graphs), 1)
        graph, nodes, edges = graphs[0]
        self.assertEqual(len(graph), len(GRAPH_SUMMARY_NAMES))
        self.assertEqual(len(nodes), 6); self.assertEqual(len(edges), 7)
        self.assertTrue(all(len(n) == len(NODE_NAMES) for n in nodes))
        self.assertTrue(all(len(e) == len(EDGE_NAMES) for e in edges))
        flat = summary + graph + [v for row in nodes + edges for v in row]
        self.assertTrue(all(math.isfinite(v) and abs(v) <= 1 for v in flat))
        missing, _ = public_map_features(public_v5("missing")); absent, _ = public_map_features(public_v5(include_map=False))
        self.assertNotEqual((summary, graphs), missing); self.assertNotEqual(missing, absent)
        self.assertEqual(missing[1][0][0][:2], [0., 1.]); self.assertEqual(missing[1][0][1:], ([], []))
        changed = deepcopy(self.public); self.capture(changed)["nodes"][4]["nodeType"] = "rest"
        self.assertNotEqual(public_map_features(self.public), public_map_features(changed))
        # Inherited v4 event hashing sees only its exact pre-existing map slice.
        old = _v4_mechanics_projection(self.public); changed_old = _v4_mechanics_projection(changed)
        self.assertEqual(public_evidence_features(old, 64), public_evidence_features(changed_old, 64))

    def test_forward_determinism_sensitivity_and_no_weight_or_gradient_change(self):
        torch.manual_seed(855); model = StudentV5(self.config).eval()
        state = {name: value.clone() for name, value in model.state_dict().items()}
        changed = deepcopy(self.public); self.capture(changed)["nodes"][4]["nodeType"] = "rest"
        with torch.no_grad():
            first, second, altered = model(self.public), model(self.public), model(changed)
            missing, absent = model(public_v5("missing")), model(public_v5(include_map=False))
        for head in HEADS:
            self.assertTrue(torch.isfinite(first[head]).all()); self.assertTrue(torch.equal(first[head], second[head]))
        self.assertFalse(torch.equal(first["value"], altered["value"]))
        self.assertFalse(torch.equal(missing["value"], absent["value"]))
        self.assertTrue(all(torch.equal(state[key], value) for key, value in model.state_dict().items()))
        self.assertTrue(all(p.grad is None for p in model.parameters()))

    def test_legacy_model_fingerprint_feature_and_schema_are_unchanged(self):
        public = _v4_mechanics_projection(self.public)
        torch.manual_seed(954); model = StudentV4(self.config["base_config"]).eval()
        identity, features, fingerprint = v4_digest(public), public_evidence_features(public, 64), v4_fingerprint()
        with torch.no_grad(): before = model(public); StudentV5(self.config).eval()(self.public); after = model(public)
        self.assertTrue(torch.equal(before["value"], after["value"]))
        self.assertEqual(identity, v4_digest(public)); self.assertEqual(features, public_evidence_features(public, 64))
        self.assertEqual(fingerprint, v4_fingerprint())
        with self.assertRaisesRegex(SchemaError, "legacy weights"): InferenceV5(self.config, model)
        with self.assertRaisesRegex(RuntimeError, "Missing key"): StudentV5(self.config).load_state_dict(model.state_dict(), strict=True)
        wrong = deepcopy(public); wrong["public_evidence"]["events"][2]["payload"]["currentMap"] = self.capture(self.public)
        with self.assertRaises(SchemaError): schema_v4.validate_public(wrong, self.config["base_config"])

    def test_loss_masks_and_quarantine_bind_full_graph(self):
        targets = unavailable_targets(self.public)
        record = {"schema_version": RECORD_SCHEMA, "record_kind": RECORD_KIND, "public_input": self.public,
                  "targets": targets, "audit_only": {"trainable": False, "conditioned_public_input_digest": public_input_digest(self.public)}}
        validate_record(record, self.config)
        with self.assertRaisesRegex(SchemaError, QUARANTINE_REASON): validate_production_record(record, self.config)
        altered = deepcopy(record); self.capture(altered["public_input"])["nodes"][4]["nodeType"] = "rest"
        with self.assertRaisesRegex(SchemaError, "full v5"): validate_record(altered, self.config)
        model = StudentV5(self.config).eval()
        with torch.no_grad():
            output = model(self.public)
            zero, terms = decision_loss(output, targets, self.public, self.config)
            self.assertEqual(float(zero), 0.); self.assertTrue(all(value == 0. for value in terms.values()))
            labeled = deepcopy(targets); row = labeled["actions"][0]
            row.update(quality="objective_value_unresolved", win_probability=0.75); row["masks"]["win_probability"] = True
            loss, terms = decision_loss(output, labeled, self.public, self.config)
            self.assertTrue(torch.isfinite(loss)); self.assertGreater(terms["win_probability"], 0.)
            self.assertTrue(all(value == 0. for name, value in terms.items() if name != "win_probability"))
        self.assertTrue(all(p.grad is None for p in model.parameters()))
        targets["actions"][0]["value"] = 0
        with self.assertRaisesRegex(SchemaError, "null"): validate_targets(targets, self.public, self.config)

    def test_finite_anchor_binds_graph_and_rejects_rewritten_prefix(self):
        public = public_v5(source=old_public("finite-hunt-record-v2.jsonl")["public_input"])
        validate_public(public, self.config)
        root, anchor = public_map_features(public); self.assertEqual(root, anchor)
        altered = deepcopy(public); self.capture(altered["controller_context"]["anchor"])["nodes"][4]["nodeType"] = "rest"
        with self.assertRaisesRegex(SchemaError, "unchanged prefix"): validate_public(altered, self.config)
        targets = unavailable_targets(public)
        targets["plan"] = {"label_scope": "unavailable", **{h: None for h in PLAN_HEADS}, "masks": {h: False for h in PLAN_HEADS},
                           "allocated_worlds": 0, "success_completed_worlds": 0, "paired_completed_worlds": 0}
        validate_targets(targets, public, self.config)
        runner = InferenceV5(self.config)
        self.assertEqual(runner.predict(public)["status"], "MODEL_UNTRAINED")
        changed = deepcopy(public)
        for packet in (changed, changed["controller_context"]["anchor"]): self.capture(packet)["nodes"][4]["nodeType"] = "rest"
        validate_public(changed, self.config)
        self.assertEqual(runner.predict(changed)["status"], "INVALID_INPUT")
        settled = runner.predict(old_public("finite-hunt-terminal-v2.json"))
        self.assertEqual(settled["status"], "PLAN_FINISHED")
        self.assertEqual(settled["controller_context"]["anchor"]["schema_version"], PUBLIC_SCHEMA)
        self.assertEqual(self.capture(settled["controller_context"]["anchor"]), self.capture(public))

    def test_label_free_native_source_keeps_zero_labels(self):
        record = {"schema_version": "nosl.natural-source.v5", "record_kind": "natural_raw_source_candidate", "public_input": self.public,
                  "targets": {"actions": [], "pairwise": [], "equivalent_action_set": []},
                  "audit_only": {"trainable": False, "teacher_label_count": 0, "actual_seed": "private audit"}}
        self.assertIs(validate_native_source_record(record, self.config), record)
        with self.assertRaisesRegex(SchemaError, QUARANTINE_REASON): validate_production_record(record, self.config)
        record["schema_version"] = "nosl.natural-source.v4"
        with self.assertRaises(SchemaError): validate_native_source_record(record, self.config)

    def test_bundle_isolation_and_untrained_inference_never_executes_model(self):
        model = StudentV5(self.config)
        with patch.object(model, "forward", side_effect=AssertionError("must abstain")):
            self.assertEqual(InferenceV5(self.config, model).predict(self.public)["status"], "MODEL_UNTRAINED")
            self.assertEqual(InferenceV5(self.config, model, {"trained": True, "optimizer_steps": 1}).predict(self.public)["status"], "MODEL_UNVALIDATED")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "config.json").write_text(json.dumps(self.config))
            torch.save(model.state_dict(), path / "weights.pt")
            manifest = {"format": BUNDLE_FORMAT, "public_schema": PUBLIC_SCHEMA, "model_version": MODEL_VERSION,
                        "config_sha256": hashlib.sha256(json.dumps(self.config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest(),
                        "weights_sha256": hashlib.sha256((path / "weights.pt").read_bytes()).hexdigest(), "implementation": implementation_fingerprint()}
            (path / "manifest.json").write_text(json.dumps(manifest))
            self.assertEqual(InferenceV5.from_bundle(path).predict(self.public)["status"], "MODEL_UNTRAINED")
            manifest["model_version"] = "nosl.student.model.v4"
            (path / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaisesRegex(SchemaError, "unsupported"): InferenceV5.from_bundle(path)

    def test_standalone_import_and_cli(self):
        environment = {**os.environ, "PYTHONPATH": str(ROOT / "python"), "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1", "OPENBLAS_NUM_THREADS": "1"}
        script = "import sys; import nosl.inference_v5; assert not any(x in sys.modules for x in ('nosl.train','nosl.train_v2','nosl.data','nosl.data_v5','clr')); print('standalone')"
        result = subprocess.run([sys.executable, "-c", script], env=environment, cwd=ROOT, capture_output=True, text=True, check=True)
        self.assertEqual(result.stdout.strip(), "standalone")
        result = subprocess.run([sys.executable, "-m", "nosl.inference_v5", "--config", "configs/student.v5.engineering.json"],
            input=json.dumps(self.public) + '\n{"schema_version":"x","schema_version":"y"}\n',
            env=environment, cwd=ROOT, capture_output=True, text=True, check=True)
        self.assertEqual([json.loads(line)["status"] for line in result.stdout.splitlines()], ["MODEL_UNTRAINED", "INVALID_INPUT"])


if __name__ == "__main__": unittest.main()
