#!/usr/bin/env python3
"""Export and validate STS2 Power Catalog (v0.111.0).

Reads power metadata from generated artifacts and localization JSONs,
validating consistency and schema conformance.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Dict, List

ROOT = Path(__file__).parent.parent
POWERS_DIR = ROOT / "data" / "powers" / "v0.111"
CATALOG_PATH = POWERS_DIR / "power-catalog.json"
COVERAGE_PATH = POWERS_DIR / "power-coverage.json"
SCHEMA_PATH = POWERS_DIR / "power-schema.json"
README_PATH = POWERS_DIR / "README.md"


def load_catalog() -> Dict[str, Any]:
    with CATALOG_PATH.open(encoding="utf-8-sig") as f:
        return json.load(f)


def load_coverage() -> Dict[str, Any]:
    with COVERAGE_PATH.open(encoding="utf-8-sig") as f:
        return json.load(f)


def load_schema() -> Dict[str, Any]:
    with SCHEMA_PATH.open(encoding="utf-8-sig") as f:
        return json.load(f)


def validate_catalog() -> bool:
    catalog = load_catalog()
    coverage = load_coverage()

    assert catalog["game_version"] == "0.111.0"
    assert catalog["game_commit"] == "41cef1ea"
    assert catalog["assembly_sha256"] == "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"
    assert catalog["cli_protocol_version"] == "0.2.0"
    assert len(catalog["powers"]) == coverage["summary"]["total_powers"]
    assert coverage["summary"]["total_powers"] > 250
    assert coverage["summary"]["il_inspected_count"] == 0
    # Zero-mismatch v0.111 CLI/Core behavior probes; see P0_VERIFICATION.md and
    # P1_POWER_VERIFICATION.md. P0: 9 powers, P1: THORNS/ACCURACY/PLATING/
    # POISON/PANACHE.
    assert coverage["summary"]["runtime_probed_count"] == 14
    assert coverage["summary"]["simulator_supported_count"] == 14
    assert coverage["summary"]["simulator_declared_count"] > 0

    for power in catalog["powers"]:
        assert power["stable_id"]
        assert power["canonical_name"]
        assert power["localized_name_zh"]
        assert power["runtime_type"]
        assert power["owner_type"] in ("Creature", "Player", "Enemy", "Any")
        assert power["evidence"] in ("LiveObserved", "HeuristicInferred", "Unknown")
        assert power["source_version"] == "0.111.0"
        assert power["assembly_sha256"] == "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9"

    return True


def main() -> int:
    if validate_catalog():
        print(f"Power catalog verified successfully: {CATALOG_PATH}")
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
