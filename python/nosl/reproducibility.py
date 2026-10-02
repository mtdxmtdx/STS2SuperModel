"""Source/build identity only; no trainer, dataset, or simulator imports."""
from __future__ import annotations

import hashlib
import platform
from pathlib import Path

import torch

from .schema import SchemaError


IMPLEMENTATION_FORMAT = "nosl.implementation.v1"
SOURCE_FILES = ("__init__.py", "train.py", "model.py", "schema.py", "data.py",
                "public_identity.py", "vocabulary.py", "inference.py", "reproducibility.py")
INFERENCE_SOURCE_FILES = ("__init__.py", "model.py", "schema.py", "public_identity.py",
                          "inference.py", "reproducibility.py")


def source_hashes(filenames=SOURCE_FILES) -> dict:
    # Only this package's named public sources are read. Persist relative names,
    # never checkout paths, home directories, environment variables, or secrets.
    package = Path(__file__).resolve().parent
    try:
        return {"python/nosl/" + name: hashlib.sha256((package / name).read_bytes()).hexdigest()
                for name in filenames}
    except OSError as error:
        raise SchemaError("cannot verify implementation: required Python source is unavailable") from error


def runtime_identity() -> dict:
    return {"python_implementation": platform.python_implementation(),
            "python_version": platform.python_version(),
            # TorchVersion is a str subclass that weights_only loading does not
            # allow. Store an ordinary str, including its CPU/build suffix.
            "torch_version": str(torch.__version__),
            "torch_git_version": str(torch.version.git_version),
            # Build text can contain compiler paths; bind it without storing them.
            "torch_build_sha256": hashlib.sha256(str(torch.__config__.show()).encode()).hexdigest(),
            "machine": platform.machine(),
            "cpu_capability": str(torch.backends.cpu.get_cpu_capability())}


def implementation_fingerprint() -> dict:
    return {"format": IMPLEMENTATION_FORMAT, "source_sha256": source_hashes(),
            "runtime": runtime_identity(),
            "cpu_settings": {"deterministic_algorithms": torch.are_deterministic_algorithms_enabled(),
                             "deterministic_warn_only": torch.is_deterministic_algorithms_warn_only_enabled(),
                             "intraop_threads": torch.get_num_threads(),
                             "interop_threads": torch.get_num_interop_threads(),
                             "mkldnn_enabled": torch.backends.mkldnn.enabled,
                             "mkldnn_deterministic": torch.backends.mkldnn.deterministic,
                             "float32_matmul_precision": torch.get_float32_matmul_precision(),
                             "default_dtype": str(torch.get_default_dtype()),
                             "default_device": str(torch.get_default_device())}}


def verify_inference_implementation(fingerprint: dict | None) -> None:
    """Learned weights require matching inference sources/build, not fit settings."""
    if (not isinstance(fingerprint, dict) or fingerprint.get("format") != IMPLEMENTATION_FORMAT
            or not isinstance(fingerprint.get("source_sha256"), dict)
            or not isinstance(fingerprint.get("cpu_settings"), dict)):
        raise SchemaError("learned bundle lacks implementation provenance; cannot safely evaluate")
    # No read/import of training-only modules is needed for standalone inference.
    if any(fingerprint["source_sha256"].get(name) != digest
           for name, digest in source_hashes(INFERENCE_SOURCE_FILES).items()):
        raise SchemaError("learned bundle rejected: inference implementation changed")
    if fingerprint.get("runtime") != runtime_identity():
        raise SchemaError("learned bundle rejected: Python/PyTorch runtime or CPU build changed")
