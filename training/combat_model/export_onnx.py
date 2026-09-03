"""Export the NOSL combat model to ONNX and verify Python runtime parity."""

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
from .encoder import CombatFeatureEncoder, TokenVocabulary
from .model import CombatPolicyValueModel


INPUT_NAMES = [
    "state_numeric", "state_token_ids", "state_token_weights", "enemy_token_ids",
    "enemy_numeric", "enemy_mask", "candidate_token_ids", "candidate_numeric", "legal_mask",
]
OUTPUT_NAMES = ["policy_logits", "objective_values", "risk_logits"]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--samples", type=int, default=16)
    args = parser.parse_args()
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    vocabulary = TokenVocabulary.from_dict(json.loads(args.vocabulary.read_text(encoding="utf-8")))
    encoder = CombatFeatureEncoder(vocabulary)
    rows = load_rows(args.data)[:args.samples]
    batch = collate_samples([CombatDataset(rows, encoder)[index] for index in range(len(rows))])
    model = CombatPolicyValueModel(checkpoint["vocab_size"], checkpoint["embedding_dim"], checkpoint["hidden_dim"])
    model.load_state_dict(checkpoint["state_dict"])
    model.eval()
    inputs = tuple(batch[name] for name in INPUT_NAMES)
    with torch.no_grad():
        expected = model(*inputs)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    dynamic_axes = {
        "state_numeric": {0: "batch"},
        "state_token_ids": {0: "batch", 1: "state_tokens"},
        "state_token_weights": {0: "batch", 1: "state_tokens"},
        "enemy_token_ids": {0: "batch", 1: "enemies"},
        "enemy_numeric": {0: "batch", 1: "enemies"},
        "enemy_mask": {0: "batch", 1: "enemies"},
        "candidate_token_ids": {0: "batch", 1: "actions"},
        "candidate_numeric": {0: "batch", 1: "actions"},
        "legal_mask": {0: "batch", 1: "actions"},
        "policy_logits": {0: "batch", 1: "actions"},
        "objective_values": {0: "batch", 1: "actions"},
        "risk_logits": {0: "batch", 1: "actions"},
    }
    torch.onnx.export(
        model, inputs, args.output, input_names=INPUT_NAMES, output_names=OUTPUT_NAMES,
        dynamic_axes=dynamic_axes, opset_version=18, dynamo=False,
    )
    onnx.checker.check_model(onnx.load(args.output))
    # Pin parity to one reduction lane. This avoids platform-dependent
    # floating-point reduction order while leaving production threading to the
    # serving host.
    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = 1
    session_options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(args.output), sess_options=session_options, providers=["CPUExecutionProvider"])
    ort_inputs = {name: tensor.detach().cpu().numpy() for name, tensor in zip(INPUT_NAMES, inputs)}
    actual = session.run(OUTPUT_NAMES, ort_inputs)
    max_error = max(float(np.max(np.abs(want.detach().cpu().numpy() - got))) for want, got in zip(expected, actual))
    if max_error > 1e-5:
        raise RuntimeError(f"ONNX parity error {max_error} exceeds 1e-5")
    fixture: dict[str, Any] = {
        "schema_version": 1,
        "model_sha256": sha256(args.output),
        "tolerance": 1e-5,
        "inputs": {
            name: {"shape": list(tensor.shape), "dtype": str(tensor.numpy().dtype), "values": tensor.reshape(-1).tolist()}
            for name, tensor in zip(INPUT_NAMES, inputs)
        },
        "outputs": {
            name: {"shape": list(value.shape), "values": value.reshape(-1).tolist()}
            for name, value in zip(OUTPUT_NAMES, actual)
        },
        "state_hashes": batch["state_hash_public"],
    }
    args.fixture.write_text(json.dumps(fixture, separators=(",", ":")) + "\n", encoding="utf-8")
    report = {
        "verdict": "pass",
        "onnx_path": str(args.output),
        "onnx_sha256": sha256(args.output),
        "opset": 18,
        "samples": len(rows),
        "python_runtime": "onnxruntime",
        "python_runtime_version": ort.__version__,
        "parity_intra_op_num_threads": 1,
        "parity_inter_op_num_threads": 1,
        "maximum_absolute_error": max_error,
        "tolerance": 1e-5,
        "dynamic_axes": ["batch", "state_tokens", "enemies", "actions"],
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    model_manifest_path = args.checkpoint.parent / "combat-nosl-model-manifest.json"
    if model_manifest_path.exists():
        model_manifest = json.loads(model_manifest_path.read_text(encoding="utf-8"))
        model_manifest.update({
            "onnx_path": str(args.output),
            "onnx_sha256": report["onnx_sha256"],
            "onnx_opset": report["opset"],
            "python_onnx_maximum_absolute_error": max_error,
            "onnx_parity_tolerance": report["tolerance"],
        })
        model_manifest_path.write_text(json.dumps(model_manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
