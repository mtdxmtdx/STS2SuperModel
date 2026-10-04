"""Source binding only: no fit, optimizer, forward or backward execution."""
import hashlib
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "python"))
from nosl.train_v2 import implementation_fingerprint


class NativeAdmissionFingerprintTests(unittest.TestCase):
    def test_native_validator_and_protection_builder_are_bound_without_learning(self):
        with patch("torch.Tensor.backward", side_effect=AssertionError("backward forbidden")), \
             patch("torch.optim.Optimizer.step", side_effect=AssertionError("optimizer forbidden")):
            fingerprint = implementation_fingerprint()
        for key, path in (("python/nosl/native_pilot.py", ROOT / "python/nosl/native_pilot.py"),
                          ("tools/native_protection.py", ROOT / "tools/native_protection.py")):
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), fingerprint["source_sha256"][key])


if __name__ == "__main__": unittest.main()
