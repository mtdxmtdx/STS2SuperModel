"""Standalone JSONL public-input inference; no simulator/teacher/search imports."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

import torch

from .model import Student
from .reproducibility import verify_inference_implementation
from .schema import PUBLIC_SCHEMA, SchemaError, load_config, validate_public


class Inference:
    def __init__(self, config: dict, model: Student | None = None, manifest: dict | None = None, *, allow_experimental: bool = False):
        self.config = config
        self.model = model
        self.manifest = manifest or {}
        self.allow_experimental = allow_experimental
        if model is not None:
            model.eval()

    @classmethod
    def from_bundle(cls, path: str | Path, *, allow_experimental: bool = False):
        path = Path(path)
        config = load_config(path / "config.json")
        manifest = json.loads((path / "manifest.json").read_text())
        if manifest.get("format") != "nosl.student.bundle.v1" or manifest.get("public_schema") != PUBLIC_SCHEMA:
            raise SchemaError("unsupported model bundle")
        config_digest = hashlib.sha256(json.dumps(config, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()
        if manifest.get("config_sha256") != config_digest:
            raise SchemaError("model configuration checksum mismatch")
        weight_path = path / "weights.pt"
        if hashlib.sha256(weight_path.read_bytes()).hexdigest() != manifest.get("weights_sha256"):
            raise SchemaError("model weight checksum mismatch")
        if manifest.get("trained") is True:
            frozen = manifest.get("frozen_inputs")
            verify_inference_implementation(frozen.get("implementation") if isinstance(frozen, dict) else None)
        model = Student(config)
        model.load_state_dict(torch.load(weight_path, map_location="cpu", weights_only=True), strict=True)
        return cls(config, model, manifest, allow_experimental=allow_experimental)

    def predict(self, public: dict) -> dict:
        """Accept legacy public_input or an explicit decision-control envelope.

        Control status is caller-supplied. Empty actions alone never establish
        terminal/waiting, and control fields never enter model features.
        """
        decision_status = None
        try:
            if isinstance(public, dict) and ("decision_status" in public or "public_input" in public):
                if set(public) != {"decision_status", "public_input"}:
                    raise SchemaError("decision envelope must contain exactly decision_status and public_input")
                decision_status = public["decision_status"]
                if not isinstance(decision_status, str) or decision_status not in ("terminal", "waiting", "player_decision", "card_choice"):
                    raise SchemaError("unknown decision_status")
                nested = public["public_input"]
                if decision_status in ("terminal", "waiting"):
                    if nested is not None:
                        raise SchemaError("terminal/waiting envelope requires null public_input")
                    return {"status": "TERMINAL" if decision_status == "terminal" else "WAITING",
                            "reason": "caller explicitly reported " + decision_status,
                            "selected_action": None, "predictions": []}
                if not isinstance(nested, dict):
                    raise SchemaError("active decision envelope requires public_input")
                public = nested
            validate_public(public, self.config)
            if decision_status is not None:
                has_choice = public["observation"]["choice"] is not None
                if (decision_status == "card_choice") != has_choice:
                    raise SchemaError("decision_status contradicts the public choice boundary")
        except (SchemaError, TypeError, KeyError) as error:
            status = getattr(error, "status", "INVALID_INPUT")
            if decision_status in ("player_decision", "card_choice") and status == "NO_DECISION":
                status = "INVALID_INPUT"
            return {"status": status, "reason": str(error), "selected_action": None, "predictions": []}
        if self.model is None or self.manifest.get("trained") is not True:
            return {"status": "MODEL_UNTRAINED", "reason": "M6 has no trained student weights", "selected_action": None, "predictions": []}
        experimental = self.allow_experimental and self.manifest.get("status") == "EXPERIMENTAL_UNPROMOTED"
        if not experimental and (self.manifest.get("status") != "PROMOTED" or self.manifest.get("calibrated") is not True):
            return {"status": "MODEL_UNVALIDATED", "reason": "independent evaluation/calibration and explicit promotion required", "selected_action": None, "predictions": []}
        with torch.no_grad():
            output = self.model(public)
        if any(not torch.isfinite(tensor).all() for name, tensor in output.items() if name != "ranking_score"):
            return {"status": "MODEL_ERROR", "reason": "non-finite model output", "selected_action": None, "predictions": []}
        rows = []
        for i, legal in enumerate(public["legal_mask"]):
            if not legal:
                rows.append({"action_index": i, "legal": False, "score": None})
                continue
            rows.append({"action_index": i, "legal": True,
                         "score": float(output["value"][i]) * 100,
                         "win_probability": float(torch.sigmoid(output["win_probability"][i])),
                         "death_probability": float(torch.sigmoid(output["death_probability"][i])),
                         "expected_final_hp": float(output["expected_final_hp"][i]) * 100,
                         "hp_bin_probabilities": torch.softmax(output["hp_distribution"][i], -1).tolist(),
                         "potion_net_change": float(output["potion_net_change"][i])})
        selected = int(output["ranking_score"].argmax())
        return {"status": "EXPERIMENTAL_UNCALIBRATED" if experimental else "APPLICABLE_SUPPORTED_PILOT", "selected_index": selected,
                "selected_action": public["candidate_actions"][selected], "predictions": rows,
                "hp_bin_values": torch.linspace(0, self.config["hp_max"], self.config["hp_bins"]).tolist(),
                "prediction_semantics": "teacher continuation outcome estimates; not measured student rollout win rate",
                "continuation_policy_id": self.manifest.get("continuation_policy_id")}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--bundle")
    group.add_argument("--config", help="schema-only M6 mode: valid inputs return MODEL_UNTRAINED")
    parser.add_argument("--allow-experimental", action="store_true", help="explicit offline pilot evaluation of unpromoted trained weights; probabilities are uncalibrated")
    args = parser.parse_args()
    runner = Inference.from_bundle(args.bundle, allow_experimental=args.allow_experimental) if args.bundle else Inference(load_config(args.config))
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            result = runner.predict(json.loads(line))
        except (ValueError, TypeError) as error:
            result = {"status": "INVALID_INPUT", "reason": str(error), "selected_action": None, "predictions": []}
        print(json.dumps(result, allow_nan=False), flush=True)


if __name__ == "__main__":
    main()
