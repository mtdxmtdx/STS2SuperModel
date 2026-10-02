"""Narrow fail-closed resource screen around unchanged experimental inference.

This is an offline pilot policy wrapper, not an applicability certificate or a
replacement for the frozen inference implementation. It never prices resources,
filters candidates, selects a fallback, or promotes weights.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from .inference import Inference
from .schema import load_config


GUARD_VERSION = "nosl.experimental-resource-guard.v1"
_EXPERIMENTAL = "EXPERIMENTAL_UNCALIBRATED"


def _resource_blockers(public: dict) -> list[dict]:
    """Inspect only validated public facts, never predicted resource outcomes."""
    blockers = []
    potion_indices = [i for i, (action, legal) in enumerate(
        zip(public["candidate_actions"], public["legal_mask"]))
        if legal and action["kind"] in ("potion", "discard_potion")]
    if potion_indices:
        blockers.append({"code": "UNPRICED_LEGAL_POTION_ACTION",
                         "action_indices": potion_indices,
                         "reason": "legal potion use/discard has no supported utility ranking under the unpriced pilot objective"})
    observation = public["observation"]
    # History survives an empty bottle and can precede a pending card choice.
    # An accepted action and its completion event may describe the same use:
    # retain evidence indices, never count them as net inventory movement.
    potion_history = [i for i, event in enumerate(observation["history"])
                      if event["kind"] == "potion_used" or
                      event["kind"] == "action" and json.loads(event["detail"])["kind"] in ("potion", "discard_potion")]
    if potion_history:
        blockers.append({"code": "UNPRICED_PUBLIC_POTION_HISTORY",
                         "history_event_indices": potion_history,
                         "reason": "public history records potion use/discard; absolute utility applicability against combat-start inventory is unverified"})
    if observation["schema"] == "nosl.public.v2" and observation["gold"] != observation["startGold"]:
        blockers.append({"code": "UNPRICED_OBSERVED_GOLD_CHANGE",
                         "reason": "public gold differs from combat-start gold; absolute utility target applicability is unverified without its permanent future value"})
    return blockers


class ExperimentalPolicyGuard:
    """Compose with Inference without changing its sources or explicit opt-in.

    Base validation, control envelopes and model rejection remain authoritative.
    A passed screen means only that these specific blockers were not observed;
    it does not establish calibrated probabilities or supported utility for all
    remaining actions. Full candidate enumeration remains the caller's duty.
    """

    def __init__(self, inference: Inference, *, include_diagnostics: bool = False):
        self.inference = inference
        self.include_diagnostics = include_diagnostics

    @classmethod
    def from_bundle(cls, path: str | Path, *, allow_experimental: bool = False,
                    include_diagnostics: bool = False):
        return cls(Inference.from_bundle(path, allow_experimental=allow_experimental),
                   include_diagnostics=include_diagnostics)

    def predict(self, public: dict) -> dict:
        # Delegate the exact original input first. Rejected/control inputs must
        # retain base behavior; no new parser or relaxation of its strict schema.
        raw = self.inference.predict(public)
        if raw["status"] not in (_EXPERIMENTAL, "APPLICABLE_SUPPORTED_PILOT"):
            return raw

        if raw["status"] != _EXPERIMENTAL:
            # This wrapper cannot establish any promoted scope, even if a future
            # caller supplies a promoted bundle to the unchanged base runner.
            return {"status": "POLICY_INAPPLICABLE",
                    "reason": "this guard is restricted to explicitly experimental unpromoted inference",
                    "selected_index": None, "selected_action": None, "predictions": []}

        nested = public["public_input"] if "public_input" in public else public
        blockers = _resource_blockers(nested)
        result = {
            "status": "POLICY_INAPPLICABLE" if blockers else _EXPERIMENTAL,
            "reason": ("unresolved resource utility; abstain from the entire decision" if blockers else
                       "no listed resource blocker observed; all other applicability remains unverified"),
            "selected_index": None if blockers else raw["selected_index"],
            "selected_action": None if blockers else raw["selected_action"],
            "predictions": [],
            "applicability_guard": {"version": GUARD_VERSION, "blockers": blockers,
                                    "certifies_applicability": False},
        }
        if self.include_diagnostics:
            # Preserve estimates and original candidate indices, but never expose
            # the raw argmax as a second policy choice behind an abstention.
            result["diagnostics"] = {
                "status": _EXPERIMENTAL,
                "usable_as_policy": False,
                "reason": "uncalibrated teacher-continuation estimates; resource utility may be unsupported",
                **{key: raw[key] for key in ("predictions", "hp_bin_values", "prediction_semantics", "continuation_policy_id")},
            }
        return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--bundle")
    group.add_argument("--config", help="schema-only mode: valid active inputs remain MODEL_UNTRAINED")
    parser.add_argument("--allow-experimental", action="store_true",
                        help="explicit offline use of unpromoted pilot weights; never a promotion")
    parser.add_argument("--include-diagnostics", action="store_true",
                        help="include raw estimates under diagnostics, without a diagnostic action selection")
    args = parser.parse_args()
    if args.bundle:
        runner = ExperimentalPolicyGuard.from_bundle(args.bundle, allow_experimental=args.allow_experimental,
                                                     include_diagnostics=args.include_diagnostics)
    else:
        runner = ExperimentalPolicyGuard(Inference(load_config(args.config)),
                                         include_diagnostics=args.include_diagnostics)
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
