"""Generate versioned controlled vocab from the pinned adapter audit registry.

Registry admission is NOT evidence of complete NOSL/fidelity validation. The
model accepts only this explicit snapshot and strict public schema. Re-generate
on a new audited registry/version; never silently map unknown IDs to an UNK.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path

from .schema import SchemaError, load_config


CATEGORIES = {"CARD": "cards", "MONSTER": "enemies", "POTION": "potions", "RELIC": "relics",
              "POWER": "powers", "ENCHANTMENT": "enchantments", "AFFLICTION": "afflictions", "ORB": "orbs"}


def generate(registry_path: Path, base_config: dict) -> dict:
    raw = registry_path.read_bytes()
    registry = json.loads(raw)
    if registry.get("schema") != "nosl.coverage.v2" or not registry.get("upstream", {}).get("commit"):
        raise SchemaError("vocabulary requires versioned nosl.coverage.v2 registry, not historical hardcoded scope")
    entries = registry.get("entries")
    if not isinstance(entries, list) or not entries:
        raise SchemaError("empty content registry")
    config = dict(base_config)
    sources, selected = set(), set()
    for category, name in CATEGORIES.items():
        values = sorted({x["id"] for x in entries if x.get("category") == category and x.get("status") != "OutOfScope"})
        if not values and name in ("cards", "enemies", "potions", "relics", "powers"):
            raise SchemaError(f"registry lacks required category {category}")
        config["supported_" + name] = values
    for entry in entries:
        if entry.get("status") != "OutOfScope":
            sources.add(entry["id"])
            # ModelId.ToString is CATEGORY.ID with snake uppercase IDs in the
            # pinned registry. Exact emitted values can be extended only here.
            import re
            snake = re.sub(r"(?<!^)(?=[A-Z])", "_", entry["id"]).upper()
            selected.update((entry["id"], entry["category"] + "." + snake))
    config["supported_sources"] = sorted(sources)
    config["supported_selected_models"] = sorted(selected)
    config["supported_keywords"] = "None Exhaust Ethereal Innate Retain Sly Unplayable Eternal".split()
    config["supported_intents"] = "Attack Buff Debuff DebuffStrong Defend Escape Heal Hidden Summon Sleep Stun StatusCard CardDebuff DeathBlow Unknown".split()
    config["observation_schemas"] = ["nosl.public.v1", "nosl.public.v2"]
    config["vocabulary_source"] = {"registry_sha256": hashlib.sha256(raw).hexdigest(), "upstream": registry["upstream"],
                                   "admission_only_not_validation_claim": True}
    config["formal_training_authorized"] = False
    config["full_content_ready"] = False
    config["objective_calibrated"] = False
    config["torch_threads"] = 1
    config["max_candidates"] = 4096
    return config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", required=True, type=Path)
    parser.add_argument("--base-config", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    config = generate(args.registry, load_config(args.base_config))
    args.output.write_text(json.dumps(config, indent=2, ensure_ascii=False) + "\n")
    print(json.dumps({"status": "CONTROLLED_VOCABULARY_GENERATED", "counts": {name: len(config["supported_" + name]) for name in CATEGORIES.values()},
                      "registry_sha256": config["vocabulary_source"]["registry_sha256"], "training_run": False}))


if __name__ == "__main__":
    main()
