"""Standalone opt-in v2 inference: public data only, no simulator/search/trainer."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys
import torch

from .model_v2 import StudentV2
from .reproducibility import INFERENCE_SOURCE_FILES, runtime_identity, source_hashes
from .schema import HEADS, SchemaError, object_keys
from .schema_v2 import MODEL_VERSION, PUBLIC_SCHEMA, PublicHuntController, load_config, validate_public

V2_INFERENCE_SOURCES = (*INFERENCE_SOURCE_FILES, "schema_v2.py", "model_v2.py", "inference_v2.py")
SUPERVISION_FORMAT = "nosl.training.supervision.v2"
SUPERVISION_PROGRESS_FORMAT = "nosl.training.supervision-progress.v2"
POLICY_COVERAGE_KEYS = ("eligible_roots", "value_roots", "pairwise_roots", "equivalent_roots", "pairwise_pairs")
POLICY_ACTION_KINDS = ("play", "potion", "discard_potion", "choose", "end_turn")


def implementation_fingerprint():
    return {"model_version": MODEL_VERSION, "source_sha256": source_hashes(V2_INFERENCE_SOURCES), "runtime": runtime_identity()}


def valid_supervision_coverage(value, config):
    """Validate exported counts without importing training code or reading labels."""
    if not isinstance(value, dict) or value.get("format") != SUPERVISION_FORMAT: return False
    positive_int = lambda number: type(number) is int and number >= 0
    if not positive_int(value.get("roots")): return False
    for section, heads, weights, available, used in (
            ("action_heads", HEADS, config["base_config"]["loss_weights"], "masked_rows", "loss_rows"),
            ("plan_heads", ("specified_success_probability", "extra_net_hp_loss"), config["plan_loss_weights"], "masked_roots", "loss_roots")):
        counts = value.get(section)
        if not isinstance(counts, dict) or set(counts) != set(heads): return False
        for head in heads:
            row = counts[head]
            if (not isinstance(row, dict) or not all(positive_int(row.get(key)) for key in (available, used, "loss_roots"))
                    or row[used] > row[available] or row["loss_roots"] > value["roots"]
                    or row["loss_roots"] > row[used] or weights[head] <= 0 and row[used] != 0): return False
    policy = value.get("action_policy")
    if (not isinstance(policy, dict) or set(policy) != set(POLICY_COVERAGE_KEYS)
            or not all(positive_int(policy[key]) for key in POLICY_COVERAGE_KEYS)): return False
    routes = [policy[key] for key in ("value_roots", "pairwise_roots", "equivalent_roots")]
    if (max(routes) > policy["eligible_roots"] or policy["eligible_roots"] > min(value["roots"], sum(routes))
            or policy["value_roots"] > value["action_heads"]["value"]["loss_roots"]
            or 2 * policy["value_roots"] > value["action_heads"]["value"]["loss_rows"]
            or policy["pairwise_roots"] > policy["pairwise_pairs"]): return False
    kinds = value.get("policy_action_kinds")
    if (not isinstance(kinds, dict) or set(kinds) != set(POLICY_ACTION_KINDS)
            or not all(positive_int(count) for count in kinds.values())
            or not 2 * policy["eligible_roots"] <= sum(kinds.values()) <= value["action_heads"]["value"]["masked_rows"]
            or policy["eligible_roots"] == 0 and any(kinds.values())): return False
    weights = config["base_config"]["loss_weights"]
    return all(weights[name] > 0 or policy[key] == 0 for name, key in
               (("value", "value_roots"), ("pairwise", "pairwise_roots"), ("equivalent", "equivalent_roots")))


def action_policy_training_verified(manifest, config, public=None):
    """A plan/auxiliary fit or merely available action labels cannot enable actions."""
    steps = manifest.get("optimizer_steps")
    if manifest.get("trained") is not True or type(steps) is not int or steps <= 0: return False
    frozen, corpus, progress = (manifest.get("frozen_inputs"), manifest.get("training_supervision"),
                                manifest.get("supervision_progress"))
    if (not isinstance(frozen, dict) or frozen.get("training_supervision") != corpus
            or not valid_supervision_coverage(corpus, config)
            or not isinstance(progress, dict) or progress.get("format") != SUPERVISION_PROGRESS_FORMAT
            or type(progress.get("optimizer_steps")) is not int or progress["optimizer_steps"] != steps): return False
    policy_steps, consumed = progress.get("action_policy_optimizer_steps"), progress.get("consumed")
    if type(policy_steps) is not int or not 1 <= policy_steps <= steps or not valid_supervision_coverage(consumed, config): return False
    batch_size = config["base_config"]["batch_size"]
    if (not policy_steps <= consumed["action_policy"]["eligible_roots"] <= policy_steps * batch_size
            or not steps <= consumed["roots"] <= steps * batch_size): return False
    if not any(corpus["action_policy"][key] > 0 and consumed["action_policy"][key] > 0
               for key in ("value_roots", "pairwise_roots", "equivalent_roots")): return False
    if public is not None:
        kinds = {action["kind"] for action, legal in zip(public["candidate_actions"], public["legal_mask"]) if legal}
        if any(corpus["policy_action_kinds"].get(kind, 0) <= 0 or consumed["policy_action_kinds"].get(kind, 0) <= 0
               for kind in kinds): return False
    digest = hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
    splits = frozen.get("splits")
    if not isinstance(splits, dict) or not isinstance(splits.get("train"), dict): return False
    train = splits["train"]
    records_hash = train.get("records_sha256")
    if (frozen.get("config_sha256") != digest or manifest.get("config_sha256") != digest
            or not isinstance(records_hash, str) or len(records_hash) != 64
            or any(char not in "0123456789abcdef" for char in records_hash)): return False
    evidence = {"config_sha256": digest, "train_records_sha256": records_hash, "corpus": corpus, "progress": progress}
    evidence_hash = hashlib.sha256(json.dumps(evidence, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
    return manifest.get("supervision_sha256") == evidence_hash


class InferenceV2:
    """One instance per combat: a finite anchor cannot silently reset/reopen."""
    def __init__(self, config, model=None, manifest=None, *, allow_experimental=False):
        self.config, self.model, self.manifest = config, model, manifest or {}
        self.allow_experimental = allow_experimental
        self.controller = None
        if model is not None: model.eval()

    @classmethod
    def from_bundle(cls, path, *, allow_experimental=False):
        path = Path(path); config = load_config(path / "config.json")
        manifest = json.loads((path / "manifest.json").read_text())
        if (manifest.get("format") != "nosl.student.bundle.v2" or manifest.get("public_schema") != PUBLIC_SCHEMA
                or manifest.get("model_version") != MODEL_VERSION): raise SchemaError("unsupported v2 bundle")
        digest = hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
        weights = path / "weights.pt"
        if manifest.get("config_sha256") != digest or manifest.get("weights_sha256") != hashlib.sha256(weights.read_bytes()).hexdigest():
            raise SchemaError("v2 bundle checksum mismatch")
        if manifest.get("trained") is True and manifest.get("implementation") != implementation_fingerprint():
            raise SchemaError("v2 learned bundle implementation/runtime changed")
        model = StudentV2(config)
        model.load_state_dict(torch.load(weights, map_location="cpu", weights_only=True), strict=True)
        return cls(config, model, manifest, allow_experimental=allow_experimental)

    def predict(self, public):
        def result(status, reason=None, **extra):
            return {"status": status, "reason": reason, "selected_action": None, "predictions": [], **extra}
        try:
            if isinstance(public, dict) and "decision_status" in public:
                object_keys(public, ("decision_status", "terminal_public"), "terminal envelope")
                if public["decision_status"] != "terminal" or self.controller is None:
                    raise SchemaError("terminal settlement requires an existing finite controller")
                context = self.controller.settle(public["terminal_public"])
                return result("PLAN_" + context["status"].upper(), context["exitReason"], controller_context=context)
            validate_public(public, self.config)
            finite = public["controller_context"]["status"] != "inactive"
            if finite:
                if self.controller is None: self.controller = PublicHuntController(public, self.config)
                else: self.controller.advance(public)
                public = self.controller.public
                context = public["controller_context"]
                if context["status"] != "active":
                    return result("PLAN_" + context["status"].upper(), context["exitReason"], controller_context=context)
            elif self.controller is not None:
                raise SchemaError("existing finite plan context cannot be dropped or reset")
        except (SchemaError, ValueError, TypeError, KeyError) as error:
            return result(getattr(error, "status", "INVALID_INPUT"), str(error))
        steps = self.manifest.get("optimizer_steps")
        if (self.model is None or self.manifest.get("trained") is not True
                or type(steps) is not int or steps <= 0):
            return result("MODEL_UNTRAINED", "v2 has no trained weights; no safe learned plan execution is claimed")
        experimental = self.allow_experimental and self.manifest.get("status") == "EXPERIMENTAL_UNPROMOTED"
        if not experimental and (self.manifest.get("status") != "PROMOTED" or self.manifest.get("calibrated") is not True):
            return result("MODEL_UNVALIDATED", "independent evaluation/calibration and explicit promotion required")
        if not finite and not action_policy_training_verified(self.manifest, self.config, public):
            return result("ACTION_POLICY_UNVALIDATED", "missing verified committed objective value/ranking supervision or legal action-kind coverage; the complete decision abstains")
        with torch.no_grad(): output = self.model(public)
        tensors = [v for k, v in output.items() if k not in ("ranking_score", "plan")] + list(output["plan"].values())
        if any(not torch.isfinite(t).all() for t in tensors): return result("MODEL_ERROR", "non-finite output")
        plan = {"specified_success_probability": float(torch.sigmoid(output["plan"]["specified_success_probability"])),
                "extra_net_hp_loss": float(output["plan"]["extra_net_hp_loss"]) * 100} if finite else None
        # Anchor labels do not verify a learned continuation or conditional budget.
        # Until a separate closed-loop acceptance exists, finite-plan action selection abstains.
        if finite: return result("PLAN_POLICY_UNVALIDATED", "whole-plan head estimates do not establish safe learned continuation or eligibility", plan_predictions=plan)
        selected = int(output["ranking_score"].argmax())
        rows = [{"action_index": i, "legal": legal, "score": float(output["value"][i]) * 100 if legal else None}
                for i, legal in enumerate(public["legal_mask"])]
        return {"status": "EXPERIMENTAL_UNCALIBRATED" if experimental else "APPLICABLE_SUPPORTED_PILOT",
                "selected_index": selected, "selected_action": public["candidate_actions"][selected], "predictions": rows,
                "plan_predictions": None}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--config"); group.add_argument("--bundle")
    parser.add_argument("--allow-experimental", action="store_true")
    args = parser.parse_args()
    runner = InferenceV2.from_bundle(args.bundle, allow_experimental=args.allow_experimental) if args.bundle else InferenceV2(load_config(args.config))
    for line in sys.stdin:
        if not line.strip(): continue
        try: value = runner.predict(json.loads(line))
        except (ValueError, TypeError) as error:
            value = {"status": "INVALID_INPUT", "reason": str(error), "selected_action": None, "predictions": []}
        print(json.dumps(value, allow_nan=False), flush=True)


if __name__ == "__main__": main()
