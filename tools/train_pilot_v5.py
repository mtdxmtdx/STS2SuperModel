#!/usr/bin/env python3
"""Default: fail-closed v5 pilot readiness only. See --help for explicit execution."""
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from nosl.pilot_v5 import main

if __name__ == "__main__":
    raise SystemExit(main())
