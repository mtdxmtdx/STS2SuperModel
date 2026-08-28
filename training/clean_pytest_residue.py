#!/usr/bin/env python3
"""Remove only pytest-generated residue inside this checkout.

The command is intentionally opt-in: without --apply it only prints candidates.
"""

from __future__ import annotations

import argparse
import shutil
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PATTERNS = ("pytest-cache-files-*", ".pytest_cache", "test-output")


def candidates() -> list[Path]:
    found: list[Path] = []
    for base in (ROOT, ROOT / "training"):
        if not base.is_dir():
            continue
        for path in base.iterdir():
            if path.name == ".pytest_cache" or any(path.match(pattern) for pattern in PATTERNS):
                if path not in found:
                    found.append(path)
    return sorted(found, key=lambda path: str(path).lower())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="delete listed residue")
    args = parser.parse_args()
    paths = candidates()
    if not args.apply:
        for path in paths:
            print(path)
        print(f"{len(paths)} candidate(s); rerun with --apply to remove")
        return 0
    for path in paths:
        if path.is_dir():
            shutil.rmtree(path)
        elif path.exists():
            path.unlink()
    print(f"removed {len(paths)} pytest residue path(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
