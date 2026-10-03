"""New full-v5 objective records; no migration of auxiliary/native/raw artifacts.

The supplied objective/source attestations require external review. These hashes
bind what was reviewed; a JSON flag or hash does not prove source authenticity.
Historic protection is metadata only, and no held-out labels are opened here.
"""
from __future__ import annotations

from copy import deepcopy
from pathlib import Path

from .data_v5 import validate_targets
from .native_pilot import source_run_identity
from .policy_v5 import (PURPOSES, OBJECTIVE_IDENTITY_FIELDS, empty_coverage, mechanics_config, require, sha,
                        digest, validate_objective_identity)
from .protection_v5 import group_records, validate_protection
from .public_identity_v5 import public_input_digest
from .schema import HEADS, integer, object_keys
from .schema_v2 import PLAN_HEADS
from .schema_v5 import loads, validate_config

RECORD_FORMAT = "nosl.dataset.full-policy.record.v5.1"
OBJECTIVE_FORMAT = "nosl.dataset.full-policy.objective-attestation.v5.1"
ENDPOINT = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
SOURCE_FIELDS = ("source_run_group", "source_combat_id", "branch_family", "public_state_digest")


def supervision_coverage(record, config):
    """Exact positive-weight enabled-loss coverage, after complete validation."""
    public, targets = record["public_input"], record["targets"]
    result = empty_coverage(); result["roots"] = 1
    cfg = mechanics_config(config)
    weights = cfg["base_config"]["loss_weights"]
    legal = {i for i, allowed in enumerate(public["legal_mask"]) if allowed}
    by_index = {row["action_index"]: row for row in targets["actions"]}
    for head in HEADS:
        count = sum(row["action_index"] in legal and row.get("sample_weight", 1) > 0 and row["masks"][head]
                    for row in targets["actions"])
        result["action_heads"][head] = {"masked_rows": count, "loss_rows": count if weights[head] > 0 else 0,
                                        "loss_roots": int(count > 0 and weights[head] > 0)}
    for head in PLAN_HEADS:
        available = bool(targets.get("plan", {}).get("masks", {}).get(head, False))
        result["plan_heads"][head] = {"masked_roots": int(available),
            "loss_roots": int(available and cfg["plan_loss_weights"][head] > 0)}
    if public["controller_context"]["status"] != "inactive" or len(legal) < 2: return result
    valued = {i for i in legal if by_index[i]["masks"]["value"] and by_index[i].get("sample_weight", 1) > 0}
    value = weights["value"] > 0 and valued == legal
    pairs = [p for p in targets["pairwise"] if weights["pairwise"] > 0 and p["weight"] > 0
             and p["preferred"] in valued and p["other"] in valued]
    equivalent = set(targets["equivalent_action_set"])
    equiv = weights["equivalent"] > 0 and bool(equivalent) and equivalent < legal and valued == legal
    result["action_policy"] = {"eligible_roots": int(value or bool(pairs) or equiv),
        "value_roots": int(value), "pairwise_roots": int(bool(pairs)), "equivalent_roots": int(equiv),
        "pairwise_pairs": len(pairs)}
    supervised = set(legal) if value or equiv else set()
    for pair in pairs: supervised.update((pair["preferred"], pair["other"]))
    for index in supervised: result["policy_action_kinds"][public["candidate_actions"][index]["kind"]] += 1
    return result


def validate_record(record, config):
    object_keys(record, ("format", "public_input", "targets", "audit_only", "objective"), "full v5 policy record")
    require(record["format"] == RECORD_FORMAT, "new_objective_record_envelope_required")
    public, targets, audit, objective = (record[k] for k in ("public_input", "targets", "audit_only", "objective"))
    # This validates all graph, history and exact anchor data before any target
    # arithmetic or inherited mechanics encoder can strip an extension.
    validate_targets(targets, public, config)
    if isinstance(audit, dict) and audit.get("source_kind") == "constructed_native_tape_action_fixture_v1":
        from .constructed_native_policy_v5 import validate_record as validate_constructed_tape_record
        return validate_constructed_tape_record(record, config)
    if isinstance(audit, dict) and audit.get("source_kind") == "constructed_empirical_plan_fixture":
        # Only this source-backed engineering subtype omits native seed claims.
        # Independently revalidate exact raw evidence and regenerate every field;
        # the caller cannot supply a trusted constructed assertion or receipt.
        from .finite_hunt_policy_v5 import validate_record as validate_constructed_record
        return validate_constructed_record(record, config)
    object_keys(audit, ("purpose", "trainable", "source_kind", *SOURCE_FIELDS,
        "source_artifact_sha256", "source_record_sha256", "conditioned_public_input_digest", "targets_sha256",
        "native_run", "actual_seed", "source_draw_seed", "native_source_run_identity", "producer_receipt_sha256"),
        "full v5 policy audit")
    require(audit["purpose"] in PURPOSES and audit["trainable"] is (audit["purpose"] == PURPOSES[1]),
            "record_purpose_or_trainable_flag_invalid")
    source_kind = {"engineering-fixture": "synthetic_contract_fixture",
                   "bounded-objective-pilot": "externally_reviewed_objective_outcomes",
                   "native-objective-candidate": "native_empirical_objective_candidate"}
    require(audit["source_kind"] == source_kind[audit["purpose"]], "record_source_kind_invalid")
    for key in SOURCE_FIELDS:
        require(isinstance(audit[key], str) and bool(audit[key].strip()), "source_provenance_missing:" + key)
    require(audit["native_run"] is True and isinstance(audit["actual_seed"], str)
            and bool(audit["actual_seed"].strip())
            and audit["native_source_run_identity"] == source_run_identity(audit["actual_seed"]),
            "native_source_identity_required_even_in_synthetic_contracts")
    integer(audit["source_draw_seed"], "source_draw_seed", 0, 2**64 - 1)
    for key in ("source_artifact_sha256", "source_record_sha256", "targets_sha256", "conditioned_public_input_digest"):
        sha(audit[key], key)
    if audit["purpose"] == "engineering-fixture":
        require(audit["producer_receipt_sha256"] is None, "synthetic_fixture_has_no_native_receipt")
    else:
        sha(audit["producer_receipt_sha256"], "producer_receipt")
    require(audit["conditioned_public_input_digest"] == public_input_digest(public)
            and audit["targets_sha256"] == digest(targets), "full_public_or_targets_binding_mismatch")
    object_keys(objective, (*OBJECTIVE_IDENTITY_FIELDS, "evaluation_design_sha256"), "full v5 objective attestation")
    validate_objective_identity({k: objective[k] for k in OBJECTIVE_IDENTITY_FIELDS})
    sha(objective["evaluation_design_sha256"], "evaluation_design")
    by_index = {row["action_index"]: row for row in targets["actions"]}
    for row in targets["actions"]:
        # Optional accounting in the generic loss becomes mandatory here. An
        # unresolved action keeps null masks and its allocated mass explicitly.
        for key in ("allocated_worlds", "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds"):
            integer(row.get(key), key)
        if any(row["masks"].values()):
            require(row["allocated_worlds"] > 0 and row["completed_worlds"] == row["allocated_worlds"],
                    "unresolved_mass_cannot_be_supervised")
    for pair in targets["pairwise"]:
        require(pair["weight"] <= 0 or all(by_index[i].get("sample_weight", 1) > 0
                for i in (pair["preferred"], pair["other"])), "ranking_cannot_use_zero_weight_targets")
    if targets["equivalent_action_set"]:
        legal = {i for i, allowed in enumerate(public["legal_mask"]) if allowed}
        require(all(by_index[i]["masks"]["value"] and by_index[i].get("sample_weight", 1) > 0 for i in legal),
                "equivalent_loss_requires_complete_positive_weight_objectives")
    return record


class PolicyDatasetV5:
    """Detached immutable inputs, verified before every loss/commit/export."""
    def __init__(self, records, config, *, split, purpose, producer_receipts=None):
        require(split in ("train", "validation") and purpose in PURPOSES, "train_or_validation_only")
        validate_config(config)
        require(isinstance(records, list) and bool(records) and len(records) <= 5000, "bounded_nonempty_records_required")
        self.config, self.split, self.purpose = deepcopy(config), split, purpose
        self._scope = (split, purpose)
        self._records = deepcopy(records)
        for record in self._records:
            validate_record(record, self.config)
            require(record["audit_only"]["purpose"] == purpose, "mixed_record_purposes")
        self._identity = digest(self._records)
        self._config_identity = digest(self.config)
        supplied_receipts = {} if producer_receipts is None else deepcopy(producer_receipts)
        require(isinstance(supplied_receipts, dict), "producer_receipts_mapping_required")
        expected = {r["audit_only"]["producer_receipt_sha256"] for r in self._records} - {None}
        require(expected <= set(supplied_receipts), "exact_native_producer_receipts_required")
        self._receipts = {key: supplied_receipts[key] for key in expected}
        for key, receipt in self._receipts.items():
            sha(key, "producer_receipt_key")
            require(digest(receipt) == key, "producer_receipt_checksum_mismatch")
        self._receipt_identity = digest(self._receipts)
        self._path = None
        self._bytes_sha256 = None
        self._receipt_path, self._receipt_bytes_sha256 = None, None

    @classmethod
    def from_jsonl(cls, path, config, *, split, purpose, producer_receipts=None, producer_receipts_path=None):
        path = Path(path)
        payload = path.read_bytes()
        receipt_bytes = None
        if producer_receipts_path is not None:
            require(producer_receipts is None, "one_producer_receipt_input_required")
            receipt_bytes = Path(producer_receipts_path).read_bytes()
            producer_receipts = loads(receipt_bytes.decode("utf-8"))
        value = cls([loads(line) for line in payload.decode("utf-8").splitlines() if line.strip()],
                    config, split=split, purpose=purpose, producer_receipts=producer_receipts)
        value._path, value._bytes_sha256 = path, __import__("hashlib").sha256(payload).hexdigest()
        if receipt_bytes is not None:
            value._receipt_path = Path(producer_receipts_path)
            value._receipt_bytes_sha256 = __import__("hashlib").sha256(receipt_bytes).hexdigest()
        return value

    def verify_integrity(self):
        require(digest(self._records) == self._identity and digest(self.config) == self._config_identity
                and digest(self._receipts) == self._receipt_identity and (self.split, self.purpose) == self._scope,
                "dataset_or_config_changed")
        if self._path is not None:
            require(__import__("hashlib").sha256(self._path.read_bytes()).hexdigest() == self._bytes_sha256,
                    "dataset_file_changed")
        if self._receipt_path is not None:
            require(__import__("hashlib").sha256(self._receipt_path.read_bytes()).hexdigest() == self._receipt_bytes_sha256,
                    "producer_receipt_file_changed")
        return True

    @property
    def records(self):
        self.verify_integrity()
        return deepcopy(self._records)

    def __len__(self): return len(self._records)

    def producer_metadata(self, protection):
        self.verify_integrity()
        if not self._receipts: return {}
        from .native_policy_v5 import (validate_policy_producer_receipt, policy_producer_metadata,
                                      validate_policy_record_producer)
        result = {}
        for key, receipt in self._receipts.items():
            validate_policy_producer_receipt(receipt, self.config, protection)
            require(receipt["protection_sha256"] == digest(protection), "producer_protection_binding_mismatch")
            for record in self._records:
                if record["audit_only"]["producer_receipt_sha256"] == key:
                    validate_policy_record_producer(record, receipt, self.config, protection)
            result[key] = policy_producer_metadata(receipt, self.config)
        return result

    def engineering_source_metadata(self):
        """All-attempt engineering aliases, without native receipt authority."""
        self.verify_integrity()
        result = {}
        for record in self._records:
            if record["audit_only"]["source_kind"] == "constructed_native_tape_action_fixture_v1":
                from .constructed_native_policy_v5 import all_attempt_metadata
                key = record["audit_only"]["source_artifact_sha256"]
                metadata = all_attempt_metadata(record, self.config)
                require(key not in result or result[key] == metadata, "constructed_source_metadata_changed")
                result[key] = metadata
        return [row for rows in result.values() for row in rows]


def validate_isolation(datasets, protection):
    """Union all source/public aliases before eligibility; protect all old splits."""
    validate_protection(protection)
    rows, splits, producer_metadata, engineering_metadata = [], [], {}, []
    for split in ("train", "validation"):
        dataset = datasets[split]
        dataset.verify_integrity()
        rows.extend(dataset.records); splits.extend([split] * len(dataset))
        engineering_metadata.extend(dataset.engineering_source_metadata())
        for key, metadata in dataset.producer_metadata(protection).items():
            require(key not in producer_metadata or producer_metadata[key] == metadata, "inconsistent_producer_metadata")
            producer_metadata[key] = metadata
    # Metadata from failed, excluded and unexecuted attempts has no new split
    # owner, but participates in every transitive source/public alias union.
    metadata = [*engineering_metadata, *(row for items in producer_metadata.values() for row in items)]
    groups, tokens, historical = group_records([*rows, *metadata], protection["components"])
    require(all(len(owners) == 1 for owners in historical.values()), "historical_cross_split_bridge")
    protected = {token for component in protection["components"].values() for token in component["tokens"]}
    require(all(not tokens[group] & protected for group in groups), "previously_observed_source_or_public_alias")
    owners = {}
    for group, split in zip(groups, splits): owners.setdefault(group, set()).add(split)
    require(all(len(values) == 1 for values in owners.values()), "train_validation_source_or_public_alias_overlap")
    return {key: digest(metadata) for key, metadata in producer_metadata.items()}
