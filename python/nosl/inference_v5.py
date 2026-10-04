"""Standalone v5 public-only engineering inference; learned execution abstains."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

import torch

from .inference_v4 import V4_INFERENCE_SOURCES
from .model_v5 import StudentV5
from .reproducibility import runtime_identity, source_hashes
from .schema import SchemaError, object_keys
from .schema_regen import CONTEXT_SCHEMA as REGEN_CONTEXT_SCHEMA
from .schema_v5 import (MODEL_VERSION, PUBLIC_SCHEMA, PublicHuntControllerV5, PublicRegenControllerV5,
                        load_config, loads, validate_config, validate_public)

BUNDLE_FORMAT = "nosl.student.bundle.v5"
V5_INFERENCE_SOURCES = (*V4_INFERENCE_SOURCES, "schema_v5.py", "public_identity_v5.py", "model_v5.py", "inference_v5.py")


def implementation_fingerprint():
    return {"model_version": MODEL_VERSION, "source_sha256": source_hashes(V5_INFERENCE_SOURCES), "runtime": runtime_identity()}


class InferenceV5:
    """No admission, promotion, calibrated policy, or training path exists in v5."""
    def __init__(self, config, model=None, manifest=None):
        self.config = validate_config(config)
        if manifest is not None and not isinstance(manifest, dict): raise SchemaError("v5 manifest must be an object")
        if model is not None and (not isinstance(model, StudentV5) or model.config != config):
            raise SchemaError("v5 inference requires exact v5 model/config; legacy weights cannot be promoted")
        self.model, self.manifest, self.controller = model, manifest or {}, None
        if model is not None: model.eval()

    @classmethod
    def from_bundle(cls, path):
        path = Path(path)
        config = load_config(path / "config.json")
        manifest = loads((path / "manifest.json").read_text())
        if (not isinstance(manifest, dict) or manifest.get("format") != BUNDLE_FORMAT
                or manifest.get("public_schema") != PUBLIC_SCHEMA or manifest.get("model_version") != MODEL_VERSION):
            raise SchemaError("unsupported v5 bundle")
        digest = hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
        weights = path / "weights.pt"
        if manifest.get("config_sha256") != digest or manifest.get("weights_sha256") != hashlib.sha256(weights.read_bytes()).hexdigest():
            raise SchemaError("v5 bundle checksum mismatch")
        if manifest.get("implementation") != implementation_fingerprint(): raise SchemaError("v5 bundle implementation/runtime mismatch")
        model = StudentV5(config)
        model.load_state_dict(torch.load(weights, map_location="cpu", weights_only=True), strict=True)
        return cls(config, model, manifest)

    def predict(self, public):
        def result(status, reason, **extra):
            return {"status": status, "reason": reason, "selected_action": None, "predictions": [], **extra}
        try:
            if isinstance(public, dict) and "decision_status" in public:
                object_keys(public, ("decision_status", "terminal_public"), "terminal envelope")
                if public["decision_status"] != "terminal" or self.controller is None:
                    raise SchemaError("terminal settlement requires an existing finite controller")
                context = self.controller.settle(public["terminal_public"])
                return result("PLAN_" + context["status"].upper(), context["exitReason"], controller_context=context)
            validate_public(public, self.config)
            if public["controller_context"]["status"] != "inactive":
                if self.controller is None:
                    controller_type = PublicRegenControllerV5 if public["controller_context"].get("schemaVersion") == REGEN_CONTEXT_SCHEMA else PublicHuntControllerV5
                    self.controller = controller_type(public, self.config)
                else: self.controller.advance(public)
                context = self.controller.public["controller_context"]
                if context["status"] != "active": return result("PLAN_" + context["status"].upper(), context["exitReason"], controller_context=context)
            elif self.controller is not None: raise SchemaError("existing finite plan context cannot be dropped or reset")
        except (SchemaError, ValueError, TypeError, KeyError, RecursionError, OverflowError) as error:
            return result(getattr(error, "status", "INVALID_INPUT"), str(error))
        steps = self.manifest.get("optimizer_steps")
        if self.model is None or self.manifest.get("trained") is not True or type(steps) is not int or steps <= 0:
            return result("MODEL_UNTRAINED", "v5 has no trained weights or approved training admission")
        return result("MODEL_UNVALIDATED", "v5 is engineering-only; training admission and policy promotion are not implemented")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--config"); group.add_argument("--bundle")
    args = parser.parse_args()
    runner = InferenceV5.from_bundle(args.bundle) if args.bundle else InferenceV5(load_config(args.config))
    for line in sys.stdin:
        if not line.strip(): continue
        try: result = runner.predict(loads(line))
        except (ValueError, TypeError) as error:
            result = {"status": "INVALID_INPUT", "reason": str(error), "selected_action": None, "predictions": []}
        print(json.dumps(result, allow_nan=False), flush=True)


if __name__ == "__main__": main()
