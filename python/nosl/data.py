"""JSONL data loader: separate public inputs, nullable supervision, and audit data."""
from __future__ import annotations

import json
import hashlib
import math
import re
from pathlib import Path

from .public_identity import PUBLIC_IDENTITY_SCHEME, public_input_digest
from .schema import HEADS, SchemaError, boolean, integer, number, object_keys, reject, sequence, validate_public


def validate_targets(targets: dict, public: dict, config: dict) -> dict:
    object_keys(targets, ["actions", "pairwise", "equivalent_action_set"], "targets")
    actions = sequence(targets["actions"], "targets.actions", 4096)
    count = len(public["candidate_actions"])
    if len(actions) != count:
        reject("every candidate needs an explicit target/mask row")
    indices = set()
    for row in actions:
        accounting = ["allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds"]
        keys = ["action_index", "quality", "masks", *HEADS]
        if "sample_weight" in row:
            keys += ["sample_weight"]
            number(row["sample_weight"], "sample_weight", 0, 1)
        if any(key in row for key in accounting):
            keys += accounting
        object_keys(row, keys, "target action")
        if "allocated_worlds" in row:
            for key in accounting:
                integer(row[key], key)
            if row["allocated_worlds"] != sum(row[key] for key in accounting[1:]):
                reject("rollout accounting does not conserve assigned worlds")
            if row["quality"] != "unresolved" and (row["allocated_worlds"] == 0 or row["completed_worlds"] != row["allocated_worlds"]):
                reject("unresolved world mass cannot produce normalized targets")
        index = integer(row["action_index"], "action_index", 0, count - 1)
        if index in indices:
            reject("duplicate target action_index")
        indices.add(index)
        if row["quality"] not in ("complete", "unresolved", "objective_value_unresolved"):
            reject("unknown target quality")
        object_keys(row["masks"], HEADS, "target masks")
        for head in HEADS:
            mask = boolean(row["masks"][head], "mask." + head)
            value = row[head]
            if not mask:
                if value is not None:
                    reject(f"masked {head} must be null; unavailable is not zero")
                continue
            if not public["legal_mask"][index]:
                reject("illegal action cannot have a supervised target")
            if row["quality"] == "unresolved":
                reject("incomplete trajectories cannot be normalized into targets")
            if head == "value" and row["quality"] == "objective_value_unresolved":
                reject("unresolved objective cannot produce a value target")
            if head == "hp_distribution":
                atoms = sequence(value, head, 100000)
                if not atoms:
                    reject("empty hp distribution")
                for atom in atoms:
                    object_keys(atom, ["hp", "probability"], "hp distribution atom")
                    number(atom["hp"], "distribution.hp", 0, config["hp_max"])
                    number(atom["probability"], "distribution.probability", 0, 1)
                if abs(sum(a["probability"] for a in atoms) - 1) > 1e-6:
                    reject("hp distribution must conserve probability mass")
            elif head in ("win_probability", "death_probability"):
                number(value, head, 0, 1)
            elif head == "expected_final_hp":
                number(value, head, 0, config["hp_max"])
            else:
                number(value, head)
        if row["masks"]["hp_distribution"] and row["masks"]["expected_final_hp"]:
            mean = sum(x["hp"] * x["probability"] for x in row["hp_distribution"])
            if abs(mean - row["expected_final_hp"]) > 1e-4:
                reject("HP mean disagrees with distribution")
    by_index = {row["action_index"]: row for row in actions}
    for pair in sequence(targets["pairwise"], "pairwise", 100000):
        object_keys(pair, ["preferred", "other", "weight"], "pair")
        a = integer(pair["preferred"], "preferred", 0, count - 1)
        b = integer(pair["other"], "other", 0, count - 1)
        number(pair["weight"], "pair.weight", 0, 1)
        if a == b or not all(public["legal_mask"][i] and by_index[i]["masks"]["value"] for i in (a, b)):
            reject("pair ranking requires two legal, objective-resolved actions")
    equivalent = sequence(targets["equivalent_action_set"], "equivalent_action_set", count)
    for index in equivalent:
        integer(index, "equivalent index", 0, count - 1)
        if not public["legal_mask"][index] or not by_index[index]["masks"]["value"]:
            reject("equivalent set requires resolved objective labels")
    if len(set(equivalent)) != len(equivalent):
        reject("duplicate equivalent action")
    return targets


def validate_record(record: dict, config: dict) -> dict:
    object_keys(record, ["public_input", "targets", "audit_only"], "DecisionRecord")
    validate_public(record["public_input"], config)
    validate_targets(record["targets"], record["public_input"], config)
    if not isinstance(record["audit_only"], dict):
        reject("audit_only must be an object")
    return record


class DecisionDataset:
    """Small pilot loader; all decisions stay whole. Split externally by provenance."""
    def __init__(self, path: str | Path, config: dict):
        self.path = Path(path)
        self.sha256 = __import__("hashlib").sha256(self.path.read_bytes()).hexdigest()
        self.records = []
        with Path(path).open(encoding="utf-8") as handle:
            for line_no, line in enumerate(handle, 1):
                if not line.strip():
                    continue
                try:
                    self.records.append(validate_record(json.loads(line), config))
                except (ValueError, TypeError) as error:
                    raise SchemaError(f"{path}:{line_no}: {error}") from error
        if not self.records:
            reject("dataset is empty")

    def __len__(self) -> int:
        return len(self.records)

    def __getitem__(self, index: int) -> dict:
        # No merged feature dictionary: the model receives public_input explicitly.
        return self.records[index]


def _hash_file(path: Path) -> str:
    import hashlib
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()



REGISTRY_VERSION = "nosl.dataset.split-protection.v1"
SPLITS = {"train", "validation", "test"}
TOKEN_PREFIXES = {"source_run_group", "source_combat_id", "branch_family",
                  "public_state_digest", "prepared_public_input_digest"}


def require(condition, reason):
    if not condition:
        raise ValueError("split_protection:" + reason)


def is_sha256(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def registry_bytes(registry):
    return (json.dumps(registry, sort_keys=True, separators=(",", ":"),
                       ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


def registry_hash(registry):
    return hashlib.sha256(registry_bytes(registry)).hexdigest()


def validate_components(components):
    require(isinstance(components, dict) and bool(components), "components_missing")
    owners = {}
    for group, info in components.items():
        require(is_sha256(group) and isinstance(info, dict)
                and set(info) == {"split", "tokens"} and info["split"] in SPLITS,
                "component_invalid")
        tokens = info["tokens"]
        require(isinstance(tokens, list) and all(isinstance(t, str) for t in tokens)
                and tokens == sorted(set(tokens)), "component_tokens_invalid")
        for token in tokens:
            prefix, separator, value = token.partition(":")
            require(separator and prefix in TOKEN_PREFIXES and bool(value.strip()), "token_invalid")
            require(prefix != "prepared_public_input_digest" or is_sha256(value), "digest_invalid")
            require(token not in owners, "token_has_multiple_owners")
            owners[token] = group
        expected = hashlib.sha256(json.dumps(tokens, ensure_ascii=False,
                                              separators=(",", ":")).encode()).hexdigest()
        require(group == expected, "component_digest_mismatch")
    return owners


def validate_registry(registry):
    required = {"schema_version", "public_identity_scheme", "source", "components"}
    require(isinstance(registry, dict) and required <= set(registry)
            and set(registry) <= required | {"native_history"}, "fields_invalid")
    if "native_history" in registry:
        history = registry["native_history"]
        require(isinstance(history, dict) and set(history) == {"schema_version", "base_registry_sha256", "base_registry", "native_tokens", "metadata_sha256", "rows"}
                and history["schema_version"] == "nosl.native-history-protection.v1"
                and is_sha256(history["base_registry_sha256"]) and is_sha256(history["metadata_sha256"])
                and type(history["rows"]) is int and history["rows"] > 0, "native_history_invalid")
    require(registry["schema_version"] == REGISTRY_VERSION, "version_unknown")
    require(registry["public_identity_scheme"] in (PUBLIC_IDENTITY_SCHEME, "nosl.public-identity.student-v2.v1"), "identity_unknown")
    source = registry["source"]
    require(isinstance(source, dict) and set(source) == {
        "manifest_sha256", "split_state_sha256", "versions", "frozen_test_shards"}, "source_invalid")
    require(is_sha256(source["manifest_sha256"]) and is_sha256(source["split_state_sha256"]),
            "source_hash_invalid")
    require(isinstance(source["versions"], dict) and bool(source["versions"])
            and all(isinstance(v, str) and v for v in source["versions"].values()), "source_versions_invalid")
    require(isinstance(source["frozen_test_shards"], list), "source_frozen_test_invalid")
    for shard in source["frozen_test_shards"]:
        require(isinstance(shard, dict) and set(shard) == {"sha256", "bytes", "rows"}
                and is_sha256(shard["sha256"]) and type(shard["bytes"]) is int and shard["bytes"] >= 0
                and type(shard["rows"]) is int and shard["rows"] >= 0, "source_frozen_test_invalid")
    owners = validate_components(registry["components"])
    if "native_history" in registry:
        history = registry["native_history"]
        base = validate_registry(history["base_registry"])
        require(registry_hash(base) == history["base_registry_sha256"], "native_history_base_checksum_mismatch")
        require(registry["source"] == base["source"] and registry["public_identity_scheme"] == base["public_identity_scheme"],
                "native_history_base_identity_changed")
        for info in base["components"].values():
            groups = {owners.get(token) for token in info["tokens"]}
            require(None not in groups and len(groups) == 1, "native_history_base_alias_removed")
            require(all(registry["components"][group]["split"] == info["split"] for group in groups),
                    "native_history_base_split_changed")
        native_tokens = history["native_tokens"]
        require(isinstance(native_tokens, list) and bool(native_tokens)
                and all(isinstance(token, str) for token in native_tokens)
                and native_tokens == sorted(set(native_tokens))
                and any(token.startswith("source_run_group:native-source-v1:") for token in native_tokens)
                and all(token in owners for token in native_tokens), "native_history_alias_removed")
        require(any(t.startswith("source_run_group:native-source-v1:") for info in registry["components"].values()
                    for t in info["tokens"]), "native_history_source_aliases_missing")
    return registry


def validate_binding(lock, descriptors, read_registry, state):
    """Enforce the optional contract in both preparation and the student loader.

    Ordinary datasets have neither the lock key nor a registry descriptor. The
    registry is an immutable first-stage file; later states retain all aliases.
    """
    bound = lock.get("split_protection")
    files = [item for item in descriptors if item["kind"] == "split_protection"]
    if bound is None:
        require(not files, "unbound_registry")
        return None
    require(isinstance(bound, dict) and set(bound) == {"schema_version", "sha256"}
            and bound["schema_version"] == REGISTRY_VERSION and is_sha256(bound["sha256"]),
            "binding_version_or_hash_invalid")
    require(len(files) == 1 and files[0]["sha256"] == bound["sha256"], "registry_binding_mismatch")
    registry = validate_registry(read_registry(files[0]["path"]))
    require(registry_hash(registry) == bound["sha256"], "registry_checksum_mismatch")
    validate_state_protection(state, registry)
    return registry


def validate_state_protection(state, registry, *, enforce_exclusion=True):
    owners = validate_components(state["components"])
    protected_test_groups = set()
    for info in registry["components"].values():
        groups = {owners.get(token) for token in info["tokens"]}
        require(None not in groups and len(groups) <= 1, "protected_alias_closure_removed")
        require(all(state["components"][group]["split"] == info["split"] for group in groups),
                "protected_split_reassigned")
        if info["split"] == "test":
            protected_test_groups.update(groups)
    for digest, info in state["seen_public_digests"].items():
        group = owners.get("prepared_public_input_digest:" + digest)
        require(group is not None and state["components"][group]["split"] == info["split"],
                "seen_digest_split_inconsistent")
        # The exporting source itself retains its original test. Only a NEW
        # protected corpus has this lock and must exclude all old test targets.
        if enforce_exclusion and "split_protection" in state["lock"] and group in protected_test_groups:
            require(info["held_out_excluded"] is True and digest not in state["frozen_test_digests"],
                    "protected_test_target_retained")
    for digest in state["frozen_test_digests"]:
        group = owners.get("prepared_public_input_digest:" + digest)
        require(group is not None and state["components"][group]["split"] == "test", "frozen_test_alias_missing")

def prepared_paths(root: Path, split: str, expected_config_sha256: str | None = None, *,
                   expected_identity: str = PUBLIC_IDENTITY_SCHEME,
                   expected_pipeline: str = "nosl.dataset.prepare.v3") -> tuple[list[Path], str]:
    """Read the immutable M5 manifest contract without importing pipeline/teacher."""
    if split not in ("train", "validation", "test"):
        reject("unknown dataset split")
    root = root.resolve()
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text())
    if manifest.get("schema_version") != "nosl.dataset.manifest.v2" or manifest.get("isolation_passed") is not True:
        reject("dataset manifest unsupported or isolation blocked")
    if (manifest.get("pipeline_version") != expected_pipeline
            or manifest.get("public_identity_scheme") != expected_identity
            or manifest.get("lock", {}).get("pipeline_version") != expected_pipeline
            or manifest.get("lock", {}).get("public_identity_scheme") != expected_identity):
        reject("prepared public identity scheme unsupported; rebuild grouping from raw provenance")
    if expected_config_sha256 and manifest["lock"]["student_config_sha256"] != expected_config_sha256:
        reject("prepared dataset student config checksum mismatch")

    def safe(relative):
        path = (root / relative).resolve()
        if Path(relative).is_absolute() or not path.is_relative_to(root):
            reject("manifest path outside dataset")
        return path

    stages, descriptors, parent_hash = [], {}, None
    for reference in manifest["stages"]:
        path = safe(reference["manifest"])
        if _hash_file(path) != reference["sha256"]: reject("stage manifest checksum mismatch")
        stage = json.loads(path.read_text())
        if stage["schema_version"] != "nosl.dataset.stage.v2" or stage["lock"] != manifest["lock"] or stage["versions"] != manifest["versions"] or stage["parent_stage_sha256"] != parent_hash or stage.get("observation_schema") != manifest.get("observation_schema"):
            reject("stage version/config/chain mismatch")
        parent_hash = reference["sha256"]
        for item in stage["files"]:
            path = safe(item["path"])
            if item["path"] in descriptors: reject("duplicate shard path")
            if path.stat().st_size != item["bytes"] or _hash_file(path) != item["sha256"]:
                reject("dataset shard checksum mismatch")
            descriptors[item["path"]] = item
        stages.append(stage)
    if not stages: reject("empty dataset stage chain")
    first_test = [x for x in stages[0]["files"] if x["kind"] == "test"]
    if first_test != manifest["frozen_test_shards"]: reject("frozen test changed")
    latest = manifest["latest_state"]
    if latest["path"] not in descriptors or descriptors[latest["path"]] != latest:
        reject("split state is not bound to immutable stage")
    state = json.loads(safe(latest["path"]).read_text())
    if state.get("schema_version") != "nosl.dataset.split-state.v2":
        reject("split state schema unsupported")
    if manifest.get("observation_schema") not in ("nosl.public.v1", "nosl.public.v2"):
        reject("manifest observation schema unsupported")
    if (state["lock"] != manifest["lock"] or state["versions"] != manifest["versions"]
            or state["stage_count"] != len(stages)
            or state.get("observation_schema") != manifest["observation_schema"]):
        reject("split state lock/observation mismatch")
    conflicts = state.get("cross_split_conflicts")
    if not isinstance(conflicts, list):
        reject("split state conflict evidence missing")
    state_isolated = len(conflicts) == 0
    if manifest["isolation_passed"] is not state_isolated:
        reject("manifest isolation state mismatch")
    if not state_isolated:
        reject("dataset isolation blocked by historical split conflicts")
    # The optional portable registry is part of the immutable dataset contract,
    # not unchecked audit decoration. Legacy v3 datasets need no registry.
    validate_binding(manifest["lock"], list(descriptors.values()),
                     lambda path: json.loads(safe(path).read_text()), state)
    selected = first_test if split == "test" else [x for stage in stages for x in stage["files"] if x["kind"] == split]
    return [safe(x["path"]) for x in selected], _hash_file(manifest_path)


def prepared_dataset(root: str | Path, split: str, config: dict, expected_config_sha256: str | None = None) -> DecisionDataset:
    paths, manifest_hash = prepared_paths(Path(root), split, expected_config_sha256)
    data = DecisionDataset.__new__(DecisionDataset)
    data.path = Path(root) / "manifest.json"
    data.sha256 = manifest_hash + ":" + split
    data.records = []
    for path in paths:
        data.records.extend(DecisionDataset(path, config).records)
    if not data.records:
        reject(f"prepared {split} split is empty; independent groups required")
    return data


def canonical_object_digest(value) -> str:
    """M5 object_digest contract: key order/whitespace and 1 vs 1.0 do not matter."""
    def normalize(item):
        if isinstance(item, float) and math.isfinite(item) and item.is_integer():
            return int(item)
        if isinstance(item, list):
            return [normalize(x) for x in item]
        if isinstance(item, dict):
            return {key: normalize(val) for key, val in item.items()}
        return item
    canonical = json.dumps(normalize(value), sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()
