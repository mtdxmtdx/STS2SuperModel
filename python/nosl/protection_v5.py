"""Versioned metadata-only isolation for complete-context v5 cohorts.

Legacy registries and split ownership remain intact. Even malformed provenance
contributes conservative aliases before rejection. These aliases never label,
validate, or deduplicate semantic public inputs.
"""
from __future__ import annotations
from copy import deepcopy
import hashlib
import json

from .data import canonical_object_digest, validate_components, validate_registry
from .native_pilot import native_source_tokens
from .native_v5 import require
from .public_identity_v5 import public_input_digest, legacy_alias_digests
from .public_identity_v4 import legacy_alias_digests as v4_aliases

PROTECTION_SCHEMA = "nosl.dataset.complete-map-protection.v1"
PROVENANCE_FIELDS = ("source_run_group", "source_combat_id", "branch_family", "public_state_digest")
AUDIT_PROVENANCE_FIELDS = {*PROVENANCE_FIELDS, "actual_seed", "act", "floor", "encounter", "source_kind", "native_run", "source_draw_seed", "native_source_run_identity"}


def validate_metadata(value):
    require(isinstance(value, dict) and set(value) <= {"public_input", "audit_only"}, "protection_metadata_only")
    if "public_input" in value: require(isinstance(value["public_input"], dict), "protection_public_metadata_invalid")
    audit = value.get("audit_only", {})
    require(isinstance(audit, dict) and set(audit) <= AUDIT_PROVENANCE_FIELDS, "protection_audit_metadata_only")
    for key, item in audit.items():
        if key == "native_run": require(type(item) is bool, "protection_native_flag_invalid")
        elif key in ("act", "floor", "source_draw_seed"):
            require(type(item) is int and 0 <= item <= 2**64 - 1, "protection_coordinate_invalid")
        else: require(isinstance(item, str) and bool(item.strip()), "protection_scalar_metadata_invalid")
    return value


def component_id(tokens):
    return hashlib.sha256(json.dumps(sorted(tokens), ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()


def record_tokens(record):
    if not isinstance(record, dict): return set()
    tokens, audit = set(), record.get("audit_only")
    if isinstance(audit, dict):
        tokens.update(native_source_tokens(audit))
        if type(audit.get("source_draw_seed")) is int and audit["source_draw_seed"] >= 0:
            tokens.add("source_run_group:native-tape-source-draw-v1:" + str(audit["source_draw_seed"]))
        for field in PROVENANCE_FIELDS:
            value = audit.get(field)
            if isinstance(value, str) and value.strip(): tokens.add(field + ":" + value)
    public = record.get("public_input")
    if isinstance(public, dict):
        aliases = set()
        for identity in (public_input_digest,):
            try: aliases.add(identity(public))
            except (ValueError, TypeError, KeyError, RecursionError, OverflowError): pass
        for recover in (legacy_alias_digests, v4_aliases):
            try: aliases.update(recover(public))
            except (ValueError, TypeError, KeyError, AttributeError, RecursionError, OverflowError): pass
        tokens.update("prepared_public_input_digest:" + value for value in aliases)
    return tokens


def group_records(records, prior_components):
    """Union complete history and every raw input before any eligibility filter."""
    token_groups = [set(info["tokens"]) for info in prior_components.values()] + [record_tokens(r) for r in records]
    parent, first = list(range(len(token_groups))), {}
    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]; i = parent[i]
        return i
    for i, tokens in enumerate(token_groups):
        for token in tokens:
            if token in first:
                a, b = find(i), find(first[token]); parent[max(a, b)] = min(a, b)
            else: first[token] = i
    merged, owners = {}, {}
    for i, tokens in enumerate(token_groups): merged.setdefault(find(i), set()).update(tokens)
    prior = list(prior_components.values())
    for i, info in enumerate(prior): owners.setdefault(find(i), set()).add(info["split"])
    identifiers = {i: component_id(tokens) for i, tokens in merged.items()}
    return ([identifiers[find(i)] for i in range(len(prior), len(token_groups))],
            {identifiers[i]: tokens for i, tokens in merged.items()},
            {identifiers[i]: splits for i, splits in owners.items()})


def validate_protection(value):
    if not isinstance(value, dict) or value.get("schema_version") != PROTECTION_SCHEMA:
        return validate_registry(value)
    require(set(value) == {"schema_version", "base", "base_sha256", "metadata", "metadata_sha256", "components", "new_component_owners"}, "protection_fields")
    base = validate_protection(value["base"])
    require(value["base_sha256"] == canonical_object_digest(base), "protection_base_hash")
    metadata = value["metadata"]
    require(isinstance(metadata, list) and bool(metadata), "protection_metadata_missing")
    require(all(validate_metadata(r) and record_tokens(r) for r in metadata), "protection_metadata_only")
    require(value["metadata_sha256"] == canonical_object_digest(metadata), "protection_metadata_hash")
    _, tokens, owners = group_records(metadata, base["components"])
    require(all(len(splits) == 1 for splits in owners.values()), "protection_cross_split_bridge")
    new_owners = value["new_component_owners"]
    require(isinstance(new_owners, dict) and set(new_owners) == set(tokens) - set(owners)
            and all(split in ("train", "validation", "test") for split in new_owners.values()), "protection_new_owners_invalid")
    expected = {g: {"split": next(iter(owners[g])) if g in owners else new_owners[g], "tokens": sorted(t)} for g, t in tokens.items()}
    require(value["components"] == expected, "protection_alias_or_owner_changed")
    validate_components(value["components"])
    return value


def extend_protection(base, metadata, *, new_component_owners=None):
    """Return a new registry; never mutate the imported historic registry."""
    validate_protection(base)
    for item in metadata: validate_metadata(item)
    _, tokens, owners = group_records(metadata, base["components"])
    require(all(len(splits) == 1 for splits in owners.values()), "protection_cross_split_bridge")
    new_owners = {g: "train" for g in set(tokens) - set(owners)} if new_component_owners is None else new_component_owners
    result = {"schema_version": PROTECTION_SCHEMA, "new_component_owners": deepcopy(new_owners), "base": deepcopy(base), "base_sha256": canonical_object_digest(base),
              "metadata": deepcopy(metadata), "metadata_sha256": canonical_object_digest(metadata),
              "components": {g: {"split": next(iter(owners[g])) if g in owners else new_owners[g], "tokens": sorted(t)} for g, t in tokens.items()}}
    return validate_protection(result)
