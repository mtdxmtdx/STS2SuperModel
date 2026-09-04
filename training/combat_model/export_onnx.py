"""Export the NOSL combat model and verify decision-equivalent ONNX output."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import numpy as np
import onnx
import onnxruntime as ort
import torch

from .dataset import CombatDataset, collate_samples, load_rows
from .decision_parity import (
    LEGACY_ABSOLUTE_TOLERANCE,
    NEAR_TIE_THRESHOLD,
    NUMERIC_DIAGNOSTIC_THRESHOLD,
    TIE_TOLERANCE,
    canonical_ranking,
    compare_decisions,
    inject_near_tie_perturbation,
)
from .encoder import CombatFeatureEncoder, TokenVocabulary
from .model import CombatPolicyValueModel


INPUT_NAMES = [
    "state_numeric", "state_token_ids", "state_token_weights", "enemy_token_ids",
    "enemy_numeric", "enemy_mask", "candidate_token_ids", "candidate_numeric", "legal_mask",
]
OUTPUT_NAMES = ["policy_logits", "objective_values", "risk_logits"]
MINIMUM_PARITY_SAMPLES = 512


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _load_model(checkpoint: dict[str, Any]) -> CombatPolicyValueModel:
    model = CombatPolicyValueModel(checkpoint["vocab_size"], checkpoint["embedding_dim"], checkpoint["hidden_dim"])
    model.load_state_dict(checkpoint["state_dict"])
    model.eval()
    return model


@torch.no_grad()
def _policy_logits_by_row(
    rows: list[dict[str, Any]],
    encoder: CombatFeatureEncoder,
    model: CombatPolicyValueModel,
    batch_size: int,
) -> list[np.ndarray]:
    dataset = CombatDataset(rows, encoder)
    result: list[np.ndarray] = []
    for start in range(0, len(dataset), batch_size):
        samples = [dataset[index] for index in range(start, min(start + batch_size, len(dataset)))]
        batch = collate_samples(samples)
        logits = model(*(batch[name] for name in INPUT_NAMES))[0].cpu().numpy()
        for index, action_ids in enumerate(batch["action_ids"]):
            result.append(logits[index, :len(action_ids)].copy())
    return result


def _select_rows(
    rows: list[dict[str, Any]],
    logits: list[np.ndarray],
    sample_count: int,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    if len(rows) < sample_count:
        raise ValueError(f"test split has {len(rows)} Reliable rows, needs {sample_count}")
    action_counts = [len(row.get("legal_actions") or []) for row in rows]
    enemy_counts = [len((row.get("public_state") or {}).get("enemies") or []) for row in rows]
    margins: list[float] = []
    for values in logits:
        ordered = np.sort(values)[::-1]
        margins.append(float(ordered[0] - ordered[1]) if len(ordered) >= 2 else float("inf"))
    max_actions = max(action_counts)
    max_enemies = max(enemy_counts)
    by_hash = lambda index: str(rows[index].get("state_hash_public"))
    required = {
        min((index for index, count in enumerate(action_counts) if count == max_actions), key=by_hash),
        min((index for index, count in enumerate(enemy_counts) if count == max_enemies), key=by_hash),
    }
    near_tie_indices = sorted(
        (index for index, margin in enumerate(margins) if margin < NEAR_TIE_THRESHOLD),
        key=lambda index: (margins[index], by_hash(index)),
    )
    if not near_tie_indices:
        raise ValueError("test split contains no near-tie sample with top-2 margin < 1e-3")
    required.update(near_tie_indices[:min(64, len(near_tie_indices))])
    remaining = sorted(
        (index for index in range(len(rows)) if index not in required),
        key=lambda index: hashlib.sha256(by_hash(index).encode("utf-8")).hexdigest(),
    )
    selected_indices = sorted((*required, *remaining[:max(0, sample_count - len(required))]), key=by_hash)[:sample_count]
    selected_near_ties = sum(margins[index] < NEAR_TIE_THRESHOLD for index in selected_indices)
    provenance = {
        "source_rule": "Reliable rows from test split only",
        "selection_rule": "include lexicographically-first max-actions and max-enemies rows; include up to 64 smallest near ties; fill by sha256(state_hash_public); final order by state_hash_public",
        "requested_samples": sample_count,
        "selected_samples": len(selected_indices),
        "source_reliable_rows": len(rows),
        "maximum_legal_actions_in_source": max_actions,
        "maximum_legal_actions_in_fixture": max(action_counts[index] for index in selected_indices),
        "maximum_enemies_in_source": max_enemies,
        "maximum_enemies_in_fixture": max(enemy_counts[index] for index in selected_indices),
        "near_tie_threshold": NEAR_TIE_THRESHOLD,
        "near_tie_samples_in_source": len(near_tie_indices),
        "near_tie_samples_in_fixture": selected_near_ties,
    }
    return [rows[index] for index in selected_indices], provenance


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--samples", type=int, default=MINIMUM_PARITY_SAMPLES)
    parser.add_argument("--inference-batch-size", type=int, default=256)
    parser.add_argument("--inject-logit-perturbation", type=float, default=0.0)
    parser.add_argument("--prior-maximum-absolute-error", type=float)
    args = parser.parse_args()
    if args.samples < MINIMUM_PARITY_SAMPLES:
        raise SystemExit(f"--samples must be >= {MINIMUM_PARITY_SAMPLES}")
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    vocabulary = TokenVocabulary.from_dict(json.loads(args.vocabulary.read_text(encoding="utf-8")))
    encoder = CombatFeatureEncoder(vocabulary)
    all_rows = load_rows(args.data)
    model = _load_model(checkpoint)
    all_logits = _policy_logits_by_row(all_rows, encoder, model, args.inference_batch_size)
    rows, selection = _select_rows(all_rows, all_logits, args.samples)
    batch = collate_samples([CombatDataset(rows, encoder)[index] for index in range(len(rows))])
    inputs = tuple(batch[name] for name in INPUT_NAMES)
    with torch.no_grad():
        reference = model(*inputs)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    dynamic_axes = {
        "state_numeric": {0: "batch"}, "state_token_ids": {0: "batch", 1: "state_tokens"},
        "state_token_weights": {0: "batch", 1: "state_tokens"},
        "enemy_token_ids": {0: "batch", 1: "enemies"}, "enemy_numeric": {0: "batch", 1: "enemies"},
        "enemy_mask": {0: "batch", 1: "enemies"}, "candidate_token_ids": {0: "batch", 1: "actions"},
        "candidate_numeric": {0: "batch", 1: "actions"}, "legal_mask": {0: "batch", 1: "actions"},
        "policy_logits": {0: "batch", 1: "actions"}, "objective_values": {0: "batch", 1: "actions"},
        "risk_logits": {0: "batch", 1: "actions"},
    }
    torch.onnx.export(model, inputs, args.output, input_names=INPUT_NAMES, output_names=OUTPUT_NAMES,
                      dynamic_axes=dynamic_axes, opset_version=18, dynamo=False)
    onnx.checker.check_model(onnx.load(args.output))
    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = 1
    session_options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(args.output), sess_options=session_options, providers=["CPUExecutionProvider"])
    ort_inputs = {name: tensor.detach().cpu().numpy() for name, tensor in zip(INPUT_NAMES, inputs)}
    actual = session.run(OUTPUT_NAMES, ort_inputs)
    reference_arrays = [value.detach().cpu().numpy() for value in reference]
    injected_sample = injected_action = None
    if args.inject_logit_perturbation:
        actual[0], injected_sample, injected_action = inject_near_tie_perturbation(
            actual[0], reference_arrays[0], batch["action_ids"], ort_inputs["legal_mask"], args.inject_logit_perturbation)
    maximum_absolute_error = max(float(np.max(np.abs(want - got))) for want, got in zip(reference_arrays, actual))
    decision = compare_decisions(reference_arrays[0], actual[0], batch["action_ids"], ort_inputs["legal_mask"])
    decision_dict = decision.to_dict()
    diagnostic_status = "warning" if maximum_absolute_error > NUMERIC_DIAGNOSTIC_THRESHOLD else "normal"
    verdict = "pass" if decision.passed else "fail"
    model_hash = sha256(args.output)
    fixture: dict[str, Any] = {
        "schema_version": 2,
        "model_sha256": model_hash,
        "decision_gate": {
            "tie_tolerance": TIE_TOLERANCE,
            "tie_break": "ActionId Ordinal ascending within anchor-relative logit groups whose anchor delta is < 1e-4",
            "required_top1_agreement_rate": 1.0,
            "required_top3_set_agreement_rate": 1.0,
            "required_ranking_agreement": 1.0,
        },
        "legacy_absolute_tolerance": LEGACY_ABSOLUTE_TOLERANCE,
        "numeric_diagnostic_threshold": NUMERIC_DIAGNOSTIC_THRESHOLD,
        "prior_reported_maximum_absolute_error": args.prior_maximum_absolute_error,
        "measurement_difference_note": (
            f"The stratified {len(rows)}-sample fixture measured {maximum_absolute_error}; "
            f"this supersedes the prior conversational measurement {args.prior_maximum_absolute_error}."
            if args.prior_maximum_absolute_error is not None else None
        ),
        "selection": {**selection, "source": str(args.data), "source_sha256": sha256(args.data)},
        "inputs": {
            name: {"shape": list(tensor.shape), "dtype": str(tensor.numpy().dtype), "values": tensor.reshape(-1).tolist()}
            for name, tensor in zip(INPUT_NAMES, inputs)
        },
        "reference_outputs": {
            name: {"shape": list(value.shape), "values": value.reshape(-1).tolist()}
            for name, value in zip(OUTPUT_NAMES, reference_arrays)
        },
        "outputs": {
            name: {"shape": list(value.shape), "values": value.reshape(-1).tolist()}
            for name, value in zip(OUTPUT_NAMES, actual)
        },
        "action_ids": batch["action_ids"],
        "state_hashes": batch["state_hash_public"],
        "synthetic_tie_case": {
            "reference_logits": [1.0, 1.00005, 0.0],
            "candidate_logits": [1.00001, 1.00004, 0.0],
            "action_ids": ["action:B", "action:A", "action:C"],
            "legal_mask": [True, True, True],
            "expected_order": ["action:A", "action:B", "action:C"],
        },
    }
    args.fixture.write_text(json.dumps(fixture, separators=(",", ":")) + "\n", encoding="utf-8")
    report = {
        "verdict": verdict,
        **decision_dict,
        "onnx_path": str(args.output), "model_sha256": model_hash, "onnx_sha256": model_hash,
        "opset": 18, "samples": len(rows), "selection": selection,
        "python_runtime": "onnxruntime", "python_runtime_version": ort.__version__,
        "maximum_absolute_error": maximum_absolute_error,
        "tolerance": LEGACY_ABSOLUTE_TOLERANCE,
        "legacy_absolute_tolerance": LEGACY_ABSOLUTE_TOLERANCE,
        "legacy_absolute_verdict": "pass" if maximum_absolute_error <= LEGACY_ABSOLUTE_TOLERANCE else "fail",
        "numeric_diagnostic_threshold": NUMERIC_DIAGNOSTIC_THRESHOLD,
        "numeric_diagnostic_status": diagnostic_status,
        "prior_reported_maximum_absolute_error": args.prior_maximum_absolute_error,
        "measurement_difference_note": (
            f"The stratified {len(rows)}-sample fixture measured {maximum_absolute_error}; "
            f"this supersedes the prior conversational measurement {args.prior_maximum_absolute_error}."
            if args.prior_maximum_absolute_error is not None else None
        ),
        "tie_tolerance": TIE_TOLERANCE,
        "tie_break_rule": "ActionId Ordinal ascending within anchor-relative logit groups whose anchor delta is < 1e-4",
        "injected_logit_perturbation": args.inject_logit_perturbation,
        "injected_sample_index": injected_sample,
        "injected_action_index": injected_action,
        "dynamic_axes": ["batch", "state_tokens", "enemies", "actions"],
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    model_manifest_path = args.checkpoint.parent / "combat-nosl-model-manifest.json"
    if decision.passed and not args.inject_logit_perturbation and model_manifest_path.exists():
        model_manifest = json.loads(model_manifest_path.read_text(encoding="utf-8"))
        model_manifest.update({
            "onnx_path": str(args.output), "onnx_sha256": model_hash, "onnx_opset": 18,
            "onnx_decision_parity_verdict": verdict,
            "onnx_top1_agreement_rate": decision.top1_agreement_rate,
            "onnx_top3_set_agreement_rate": decision.top3_set_agreement_rate,
            "onnx_ranking_agreement": decision.ranking_agreement,
            "onnx_tie_break_deterministic": decision.tie_break_deterministic,
            "python_onnx_maximum_absolute_error": maximum_absolute_error,
            "onnx_legacy_absolute_tolerance": LEGACY_ABSOLUTE_TOLERANCE,
            "onnx_numeric_diagnostic_threshold": NUMERIC_DIAGNOSTIC_THRESHOLD,
            "onnx_parity_samples": len(rows),
        })
        model_manifest_path.write_text(json.dumps(model_manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0 if decision.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
