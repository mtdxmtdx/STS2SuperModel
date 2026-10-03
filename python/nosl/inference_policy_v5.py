"""Standalone full-v5 scorer. Action selection is an explicit experimental opt-in.

Existing v5 auxiliary bundles cannot be loaded here. Finite-plan estimates never
authorize learned continuation, and no promotion/calibration path is provided.
"""
from __future__ import annotations

import argparse
from copy import deepcopy
import hashlib
import json
from pathlib import Path
import sys

import torch

from .model_v5 import StudentV5
from .policy_applicability_v5 import resource_screen
from .policy_v5 import (action_policy_training_verified, require, validate_manifest,
                        validate_output)
from .schema import HEADS, SchemaError, object_keys
from .schema_regen import CONTEXT_SCHEMA as REGEN_CONTEXT_SCHEMA
from .schema_v5 import (PublicHuntControllerV5, PublicRegenControllerV5,
                        load_config, loads, validate_config, validate_public)


def score_public(model, public, config):
    """Guarded no-grad engineering scores; never select or assert readiness.

The full map/evidence/anchor schema is checked before the model can perform any
mechanics projection. Callers must treat all outputs as uncalibrated estimates.
"""
    validate_public(public, config)
    require(type(model) is StudentV5 and model.config == config, "exact_v5_model_and_config_required")
    require(all(p.device.type == "cpu" and p.dtype == torch.float32
                and bool(torch.isfinite(p).all()) for p in model.parameters()), "invalid_model_parameters")
    was_training = model.training
    model.eval()
    try:
        with torch.no_grad():
            output = validate_output(model(public), public, config)
            rows = []
            for i, legal in enumerate(public["legal_mask"]):
                predictions = {}
                for head in HEADS:
                    value = output[head][i]
                    if head in ("win_probability", "death_probability"): value = value.sigmoid()
                    elif head == "hp_distribution": value = value.softmax(-1)
                    elif head in ("value", "expected_final_hp"): value = value.double() * 100
                    predictions[head] = value.tolist() if legal else None
                rows.append({"action_index": i, "legal": legal, **predictions})
            plan = {"specified_success_probability": float(output["plan"]["specified_success_probability"].sigmoid()),
                    "extra_net_hp_loss": float(output["plan"]["extra_net_hp_loss"]) * 100}
            # Include no selected index in the reusable diagnostic scorer.
            return {"status": "UNCALIBRATED_SCORES", "selected_action": None,
                    "predictions": rows, "plan_predictions": plan,
                    "probabilities_calibrated": False}
    finally:
        model.train(was_training)


class InferencePolicyV5:
    """One instance per combat; defaults abstain even for trained policy bundles."""
    def __init__(self, config, model=None, manifest=None, *, allow_experimental=False):
        require(type(allow_experimental) is bool, "experimental_opt_in_must_be_boolean")
        self.config = deepcopy(validate_config(config))
        self.model = model
        require(model is None or type(model) is StudentV5 and model.config == self.config,
                "exact_v5_model_and_config_required")
        require(manifest is None or isinstance(manifest, dict), "manifest_must_be_object")
        self.manifest = deepcopy(manifest or {})
        if self.manifest:
            require(model is not None, "manifest_requires_model")
            validate_manifest(self.manifest, self.config, model.state_dict())
        self.allow_experimental, self.controller = allow_experimental, None

    @classmethod
    def from_bundle(cls, path, *, allow_experimental=False):
        path = Path(path)
        config = load_config(path / "config.json")
        manifest = loads((path / "manifest.json").read_text(encoding="utf-8"))
        weights = path / "weights.pt"
        require(isinstance(manifest, dict) and manifest.get("weights_sha256")
                == hashlib.sha256(weights.read_bytes()).hexdigest(), "weights_checksum_mismatch")
        with torch.random.fork_rng():
            model = StudentV5(config)
        state = torch.load(weights, map_location="cpu", weights_only=True)
        validate_manifest(manifest, config, state)
        expected = model.state_dict()
        require(isinstance(state, dict) and set(state) == set(expected)
                and all(isinstance(state[k], torch.Tensor) and state[k].shape == v.shape
                        and state[k].dtype == v.dtype for k, v in expected.items()), "model_tensor_schema_mismatch")
        model.load_state_dict(state, strict=True)
        return cls(config, model, manifest, allow_experimental=allow_experimental)

    def predict(self, public):
        def result(status, reason, **extra):
            return {"status": status, "reason": reason, "selected_action": None, "predictions": [], **extra}
        try:
            if isinstance(public, dict) and "decision_status" in public:
                object_keys(public, ("decision_status", "terminal_public"), "terminal envelope")
                require(public["decision_status"] == "terminal" and self.controller is not None,
                        "terminal_settlement_requires_existing_finite_controller")
                context = self.controller.settle(public["terminal_public"])
                return result("PLAN_" + context["status"].upper(), context["exitReason"], controller_context=context)
            validate_public(public, self.config)
            finite = public["controller_context"]["status"] != "inactive"
            if finite:
                if self.controller is None:
                    cls = PublicRegenControllerV5 if public["controller_context"].get("schemaVersion") == REGEN_CONTEXT_SCHEMA else PublicHuntControllerV5
                    self.controller = cls(public, self.config)
                else:
                    self.controller.advance(public)
                public = self.controller.public
                context = public["controller_context"]
                if context["status"] != "active":
                    return result("PLAN_" + context["status"].upper(), context["exitReason"], controller_context=context)
            else:
                require(self.controller is None, "existing_finite_context_cannot_be_dropped")
        except (SchemaError, ValueError, TypeError, KeyError, RecursionError, OverflowError) as error:
            return result(getattr(error, "status", "INVALID_INPUT"), str(error))
        if self.model is None or not self.manifest or self.manifest.get("trained") is not True:
            return result("MODEL_UNTRAINED", "no committed full-v5 objective policy training")
        try:
            validate_manifest(self.manifest, self.config, self.model.state_dict())
            require(self.model.config == self.config, "model_config_changed")
        except (SchemaError, ValueError, TypeError, KeyError, RecursionError, OverflowError) as error:
            return result("MODEL_ERROR", str(error))
        if not self.allow_experimental:
            return result("MODEL_UNVALIDATED", "explicit experimental opt-in required; no calibrated or promoted v5 policy")
        if not finite:
            screen = resource_screen(public, self.config)
            if screen["blockers"]:
                return result("POLICY_INAPPLICABLE", "unpriced public resource mechanism; the complete decision abstains",
                              applicability_guard=screen)
        if not finite and not action_policy_training_verified(self.manifest, self.config, public):
            return result("ACTION_POLICY_UNVALIDATED", "committed objective supervision does not cover every legal action kind")
        try:
            scores = score_public(self.model, public, self.config)
        except (SchemaError, ValueError, TypeError, KeyError, RuntimeError, RecursionError, OverflowError) as error:
            return result("MODEL_ERROR", str(error))
        if finite:
            hunt = public["controller_context"].get("schemaVersion") != REGEN_CONTEXT_SCHEMA
            consumed = self.manifest["supervision_progress"]["consumed"]["plan_heads"]
            masks = {head: consumed[head]["loss_roots"] > 0 for head in scores["plan_predictions"]}
            return result("PLAN_POLICY_UNVALIDATED", "anchor estimates do not establish safe learned continuation",
                          plan_predictions={head: value if masks[head] else None
                                            for head, value in scores["plan_predictions"].items()} if hunt else None,
                          plan_prediction_masks=masks if hunt else None)
        legal = [row for row in scores["predictions"] if row["legal"]]
        selected = max(legal, key=lambda row: row["value"])["action_index"]
        consumed = self.manifest["supervision_progress"]["consumed"]["action_heads"]
        masks = {head: consumed[head]["loss_rows"] > 0 for head in HEADS}
        for row in scores["predictions"]:
            row["score"] = row["value"]  # ranking remains available without absolute value regression
            for head in HEADS:
                if not masks[head]: row[head] = None
        return {**scores, "status": "EXPERIMENTAL_UNCALIBRATED", "selected_index": selected,
                "selected_action": deepcopy(public["candidate_actions"][selected]), "plan_predictions": None,
                "prediction_head_masks": masks,
                "score_semantics": "uncalibrated_objective_value_estimate" if masks["value"] else "ranking_only_no_absolute_utility_interpretation",
                "applicability_guard": screen}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--config"); source.add_argument("--bundle")
    parser.add_argument("--allow-experimental", action="store_true")
    args = parser.parse_args(argv)
    runner = (InferencePolicyV5.from_bundle(args.bundle, allow_experimental=args.allow_experimental)
              if args.bundle else InferencePolicyV5(load_config(args.config)))
    for line in sys.stdin:
        if not line.strip(): continue
        try:
            output = runner.predict(loads(line))
        except (ValueError, TypeError, RecursionError, OverflowError) as error:
            output = {"status": "INVALID_INPUT", "reason": str(error), "selected_action": None, "predictions": []}
        print(json.dumps(output, allow_nan=False), flush=True)


if __name__ == "__main__": main()
