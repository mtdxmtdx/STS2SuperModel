"""Source-backed constructed Hunt plans at the full-policy engineering boundary.

This bridge admits only engineering fixtures, not native candidates or training.
Every validation reparses the exact source bytes, runs the independent raw
validator, and regenerates the entire normalized record. Hashes bind the source
and current evaluator/specification bytes; they do not authenticate a producer,
establish natural-run provenance, or supply calibration/admission evidence.
"""
from __future__ import annotations

from copy import deepcopy
import hashlib
from pathlib import Path

from .data_policy_v5 import OBJECTIVE_FORMAT, RECORD_FORMAT, SOURCE_FIELDS
from .evidence_v4 import numeric_guard
from .finite_hunt_v5 import ENDPOINT, validate_raw_record
from .policy_v5 import digest, require, validate_objective_identity, OBJECTIVE_IDENTITY_FIELDS
from .public_identity_v5 import public_input_digest
from .schema import object_keys
from .schema_v5 import loads

SOURCE_KIND = "constructed_empirical_plan_fixture"
EVIDENCE_FORMAT = "nosl.full-policy.constructed-plan.evidence.v1"
ROOT = Path(__file__).resolve().parents[2]
# Hash the actual checked-in artifacts, never a fabricated profile/receipt. The
# C# default Candidate profile lives in RolloutOutcome.cs. The spec and runtime
# profile are both retained in the specification identity.
OBJECTIVE_SPEC_FILES = ("docs/spec/v4/config/objective_profile.candidate.json",
                        "configs/objective_profile.candidate.json")
EVALUATOR_SOURCE_FILES = ("src/Nosl.Objectives/RolloutOutcome.cs",
                          "src/Nosl.Objectives/ObjectiveEvaluator.cs",
                          "src/Nosl.Objectives/PreferenceGates.cs",
                          "src/Nosl.Worker/AnchoredHuntEvaluator.cs",
                          "src/Nosl.Worker/ConstructedHuntV5View.cs",
                          "src/Nosl.Worker/FiniteHuntDatasetV5.cs",
                          "python/nosl/finite_hunt_v5.py",
                          "python/nosl/finite_hunt_policy_v5.py")
AUDIT_FIELDS = ("purpose", "trainable", "source_kind", *SOURCE_FIELDS,
                "source_artifact_sha256", "source_record_sha256",
                "conditioned_public_input_digest", "targets_sha256", "native_run",
                "producer_receipt_sha256", "constructed_evidence")


def _sha(payload):
    return hashlib.sha256(payload).hexdigest()


def _source_hashes(paths):
    # Fail closed if the checked-out source/specification is unavailable. This
    # engineering adapter is intentionally not an inference dependency.
    return {path: _sha((ROOT / path).read_bytes()) for path in paths}


def _evaluation_design(raw):
    """Bind the exact setup, independent draw plan, paired policies and ledger."""
    audit = raw["audit_only"]
    return {key: deepcopy(audit[key]) for key in (
        "producer_version", "scenario", "evaluation_options", "public_input_json_sha256",
        "public_conditioning", "posterior_profile", "paired_evidence",
        "sampled_public_roots", "terminal_public_evidence")}


def _normalize(raw, payload, config):
    validate_raw_record(raw, config)
    public, targets, source = raw["public_input"], raw["targets"], raw["audit_only"]
    public_digest = public_input_digest(public)
    audit = {key: deepcopy(source[key]) for key in SOURCE_FIELDS if key != "public_state_digest"}
    audit.update(purpose="engineering-fixture", trainable=False, source_kind=SOURCE_KIND,
        # The raw constructed source has no native state digest. Its full public
        # identity is the sole public-state alias; no native seed is invented.
        public_state_digest=public_digest, source_artifact_sha256=_sha(payload),
        source_record_sha256=_sha(payload), conditioned_public_input_digest=public_digest,
        targets_sha256=digest(targets), native_run=False, producer_receipt_sha256=None,
        constructed_evidence={"format": EVIDENCE_FORMAT, "raw_utf8": payload.decode("utf-8")})
    objective = {"format": OBJECTIVE_FORMAT,
        "objective_spec_sha256": digest(_source_hashes(OBJECTIVE_SPEC_FILES)),
        "evaluator_source_sha256": digest(_source_hashes(EVALUATOR_SOURCE_FILES)),
        "calibration_evidence_sha256": None,
        "evaluation_design_sha256": digest(_evaluation_design(raw)),
        "endpoint": ENDPOINT,
        "continuation_policy_id": source["paired_evidence"]["anchor"]["templateId"],
        "independent_final_evaluation": True, "public_conditioning_scope": "full_v5",
        "objective_profile_status": "candidate", "objective_calibrated": False}
    validate_objective_identity({key: objective[key] for key in OBJECTIVE_IDENTITY_FIELDS})
    return {"format": RECORD_FORMAT, "public_input": deepcopy(public), "targets": deepcopy(targets),
            "audit_only": audit, "objective": objective}


@numeric_guard
def adapt_record(raw, config, *, source_raw_bytes):
    """Adapt one exact fresh raw record without producing any native receipt."""
    require(isinstance(source_raw_bytes, bytes), "constructed_exact_raw_bytes_required")
    require(digest(loads(source_raw_bytes.decode("utf-8"))) == digest(raw),
            "constructed_raw_bytes_record_mismatch")
    return _normalize(raw, source_raw_bytes, config)


@numeric_guard
def validate_record(record, config):
    """Independently regenerate claims; caller-provided hashes grant no trust."""
    object_keys(record, ("format", "public_input", "targets", "audit_only", "objective"),
                "constructed full-policy record")
    audit = object_keys(record["audit_only"], AUDIT_FIELDS, "constructed full-policy audit")
    evidence = object_keys(audit["constructed_evidence"], ("format", "raw_utf8"), "constructed evidence")
    require(evidence["format"] == EVIDENCE_FORMAT and isinstance(evidence["raw_utf8"], str),
            "constructed_exact_raw_evidence_required")
    payload = evidence["raw_utf8"].encode("utf-8")
    expected = _normalize(loads(evidence["raw_utf8"]), payload, config)
    # Canonical JSON identity distinguishes booleans from integers and rejects
    # extra/missing fields throughout, including otherwise plausible objectives.
    require(digest(record) == digest(expected), "constructed_record_regeneration_mismatch")
    return record


def adapt_jsonl(payload, config):
    """Retain each nonblank record's exact UTF-8 bytes, including its newline."""
    require(isinstance(payload, bytes), "constructed_exact_jsonl_bytes_required")
    records = [adapt_record(loads(line.decode("utf-8")), config, source_raw_bytes=line)
               for line in payload.splitlines(keepends=True) if line.strip()]
    require(bool(records), "constructed_nonempty_raw_artifact_required")
    return records
