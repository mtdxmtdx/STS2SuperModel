#!/usr/bin/env python3
"""Extend immutable history using public/source metadata only, never target rows."""
import argparse
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from nosl.prepare_v5 import canonical_bytes, export_protection
from nosl.protection_v5 import extend_protection, validate_protection
from nosl.schema_v5 import loads


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--base-registry", type=Path, help="Existing opaque-target metadata registry")
    source.add_argument("--prepared-v5", type=Path, help="Protect all observed rows of a prior v5 snapshot")
    parser.add_argument("--metadata", nargs="+", type=Path, help="Public/provenance-only JSONL, including all development probes")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    registry = export_protection(args.prepared_v5) if args.prepared_v5 else validate_protection(loads(args.base_registry.read_text()))
    if args.metadata:
        values = [loads(line) for path in args.metadata for line in path.read_text().splitlines() if line.strip()]
        registry = extend_protection(registry, values)
    with args.output.open("xb") as handle: handle.write(canonical_bytes(registry))
    print("Saved immutable metadata-only protection; no target values decoded")


if __name__ == "__main__": main()
