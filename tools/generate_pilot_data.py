#!/usr/bin/env python3
"""Resumable bounded real-simulator data generation; never trains a model.

Private scenario/sampler seeds stay in commands and audit records. Player choices
are made only by the worker's frozen public continuation. Failed attempts and
posterior/timeout budgets are journaled, not converted to game losses.
"""
from __future__ import annotations

import argparse
import fcntl
from collections import Counter
from copy import deepcopy
import hashlib
import gzip
import json
import math
import os
from pathlib import Path
import random
import re
import selectors
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
VERSION = "nosl-real-pilot-generation-v3"
INFLIGHT_SCHEMA = "nosl.generator.inflight.v1"
sys.path.insert(0, str(ROOT / "python"))
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME, normalize_numeric_leaves, public_input_digest


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def sha(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def public_sha(value):
    return public_input_digest(value)


def fsync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def atomic_json(path, value):
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8") as stream:
        stream.write(canonical(value) + "\n")
        stream.flush(); os.fsync(stream.fileno())
    os.replace(tmp, path)
    fsync_directory(path.parent)


def append_json(path, value):
    with path.open("a", encoding="utf-8") as stream:
        stream.write(canonical(value) + "\n")
        stream.flush(); os.fsync(stream.fileno())
    fsync_directory(path.parent)


def archive_outcomes(output: Path, source_index: int, record: dict):
    """Lossless independent gzip members; raw facts never inflate training-row RAM."""
    audit = record["audit_only"]
    outcomes = audit.pop("outcome_samples", None)
    if outcomes is None: raise ValueError("teacher_missing_raw_outcome_samples")
    raw = canonical(outcomes).encode("utf-8")
    compressed = gzip.compress(raw, compresslevel=6, mtime=0)
    relative = f"outcomes/{source_index // 128:06d}.jsonl.gz"
    path = output / relative; path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("ab") as stream:
        offset = stream.tell(); stream.write(compressed); stream.flush(); os.fsync(stream.fileno())
    fsync_directory(path.parent)
    fsync_directory(output)
    audit["outcome_samples_ref"] = {"path": relative, "offset": offset, "compressed_bytes": len(compressed),
        "uncompressed_bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(),
        "format": "independent-gzip-member-json", "action_count": len(outcomes)}


def read_outcomes(output: Path, reference: dict):
    path = (output / reference["path"]).resolve()
    if not path.is_relative_to(output.resolve()): raise ValueError("outcome_reference_outside_corpus")
    with path.open("rb") as stream:
        stream.seek(reference["offset"]); compressed = stream.read(reference["compressed_bytes"])
    raw = gzip.decompress(compressed)
    if len(raw) != reference["uncompressed_bytes"] or hashlib.sha256(raw).hexdigest() != reference["sha256"]:
        raise ValueError("outcome_sample_integrity_failure")
    outcomes = json.loads(raw)
    if len(outcomes) != reference["action_count"]: raise ValueError("outcome_action_count_mismatch")
    return outcomes


def repair_interrupted_tail(path):
    """Preserve any incomplete tail, including a tiny/oversized first line."""
    if not path.exists() or not path.stat().st_size:
        return None
    with path.open("rb") as stream:
        stream.seek(-1, os.SEEK_END)
        if stream.read(1) == b"\n":
            return None
        size = stream.tell()
        position, cut = size, 0
        while position:
            start = max(0, position - 1024 * 1024)
            stream.seek(start)
            block = stream.read(position - start)
            last = block.rfind(b"\n")
            if last >= 0:
                cut = start + last + 1
                break
            position = start
        backup = path.with_suffix(path.suffix + f".partial.{time.time_ns()}")
        stream.seek(cut)
        with backup.open("xb") as saved:
            while chunk := stream.read(1024 * 1024):
                saved.write(chunk)
            saved.flush(); os.fsync(saved.fileno())
    fsync_directory(path.parent)
    with path.open("r+b") as stream:
        stream.truncate(cut); stream.flush(); os.fsync(stream.fileno())
    fsync_directory(path.parent)
    return backup


def existing_rows(path, repairs=None):
    if not path.exists(): return
    backup = repair_interrupted_tail(path)
    if backup is not None and repairs is not None:
        repairs.append(str(backup))
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            if line.strip(): yield json.loads(line)


class Worker:
    def __init__(self, repo, output, timeout, max_worker_mib=768):
        self.timeout = timeout
        self.max_worker_mib = max_worker_mib
        self.stderr = (output / "worker-stderr.log").open("a", encoding="utf-8")
        self.process = subprocess.Popen(["dotnet", str(repo / "src/Nosl.Worker/bin/Release/net9.0/Nosl.Worker.dll")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr, text=True, bufsize=1, cwd=repo)

    def request(self, command):
        self.process.stdin.write(canonical(command) + "\n"); self.process.stdin.flush()
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            deadline = time.monotonic() + self.timeout
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0: raise TimeoutError("worker_response_deadline")
                if selector.select(min(1.0, remaining)): break
                try:
                    status = Path(f"/proc/{self.process.pid}/status").read_text()
                    match = re.search(r"^VmRSS:\s+(\d+) kB", status, re.MULTILINE)
                    if match and int(match.group(1)) > self.max_worker_mib * 1024:
                        raise RuntimeError("worker_memory_budget_exceeded")
                except FileNotFoundError:
                    raise RuntimeError("worker_exited:" + str(self.process.poll()))
            line = self.process.stdout.readline()
        if not line: raise RuntimeError("worker_exited:" + str(self.process.poll()))
        return json.loads(line)

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try: self.process.wait(timeout=5)
            except subprocess.TimeoutExpired: self.process.kill(); self.process.wait()
        self.stderr.close()


def source_catalog(repo):
    manifest = json.loads((repo / "configs/coverage_manifest.json").read_text())
    if manifest.get("schema") != "nosl.coverage.v2": raise ValueError("unsupported_source_catalog_schema")
    eligible = [c for c in manifest["cards"] if c["silentPool"] and not c["multiplayerOnly"]]
    cards = sorted(c["id"] for c in eligible)
    potions = sorted(x["id"] for x in manifest["items"] if x["category"] == "Potion")
    relics = sorted(x["id"] for x in manifest["items"] if x["category"] == "Relic")
    if not cards or not potions or not relics: raise ValueError("source_catalog_empty")
    return {"cards": cards, "potions": potions, "relics": relics,
            "max_upgrade": {c["id"]: c["maxUpgradeLevel"] for c in eligible},
            "source_upstream": manifest["upstream"],
            "distribution": "constructed_curriculum_not_natural_reachability"}


def scenario(index, catalog, seed_prefix, root_policy="opening-prefix-v1"):
    """Declared constructed sources; card rules are never duplicated here."""
    rng = random.Random(seed_prefix + ":" + str(index))
    group = index % 10
    enemy = ("TwigSlimeS", "LeafSlimeS", "Nibbit", "TwigSlimeM")[index % 4]
    result = {"seed": seed_prefix + ":source:" + str(index), "enemy": enemy,
              "hp": rng.randint(12, 70), "enemyHp": rng.randint(12, 65)}
    if root_policy == "public-phase-v1" and index % 7 == 0:
        result["hp"] = rng.randint(1, 12)
    category = "starter_constructed"
    if group >= 4:
        focus = catalog["cards"][(index // 10 * 4 + group - 4) % len(catalog["cards"])]
        # All source recipes are explicit constructed stress/curriculum states, not natural run states.
        if catalog.get("max_upgrade", {}).get(focus, 0) > 0 and rng.random() < 0.5: focus += "+"
        result["deck"] = [focus, "StrikeSilent", "StrikeSilent", "DefendSilent"]
        category = "single_card_constructed"
        if group == 8:
            result["potions"] = [catalog["potions"][(index // 10) % len(catalog["potions"])]]
            category = "potion_constructed"
        elif group == 9:
            result["relics"] = [catalog["relics"][(index // 10) % len(catalog["relics"])]]
            category = "relic_constructed"
    return result, category



ACTIVE_STATUSES = ("player_decision", "card_choice")


def requested_phase(root_policy, source_slot):
    if root_policy == "opening-prefix-v1": return "decision_prefix_" + str(source_slot)
    if root_policy != "public-phase-v1" or source_slot not in range(4):
        raise ValueError("unsupported_source_snapshot_policy")
    return ("opening", "first_player_turn_2", "first_player_turn_3", "first_pending_choice")[source_slot]


def collect_source_snapshot(worker, setup, root_policy, source_slot, max_decisions):
    """A predeclared PUBLIC stopping time, never selected using future outcomes.

    No teacher call, hidden fact, terminal result or later trajectory length chooses
    a favorable root. An unavailable phase remains an explicit missing source.
    """
    phase = requested_phase(root_policy, source_slot)
    packet = worker.request({"op": "reset", "scenario": setup})
    executed = 0
    while True:
        status = packet.get("status")
        if status not in ACTIVE_STATUSES:
            if status == "terminal_settled":
                raise ValueError("source_snapshot_unavailable:" + canonical({"reason": "terminal_before_requested_phase",
                    "phase": phase, "decision_index": executed, "status": status}))
            raise ValueError("reset:" + canonical(packet))
        observation = packet["observation"]
        selected = (executed == source_slot if root_policy == "opening-prefix-v1" else
                    source_slot == 0 or source_slot == 1 and observation["turn"] >= 2 or
                    source_slot == 2 and observation["turn"] >= 3 or source_slot == 3 and status == "card_choice")
        if selected: return packet, executed, phase
        if executed >= max_decisions:
            raise ValueError("source_snapshot_unavailable:" + canonical({"reason": "source_decision_budget_exhausted",
                "phase": phase, "decision_index": executed, "status": status}))
        packet = worker.request({"op": "continue"})
        executed += 1

def usable_counts(record):
    actions = record.get("targets", {}).get("actions", [])
    complete = bool(actions) and all(a.get("allocated_worlds", 0) > 0
        and a.get("completed_worlds") == a["allocated_worlds"]
        and a.get("masks", {}).get("win_probability") is True
        and a.get("masks", {}).get("expected_final_hp") is True for a in actions)
    values = complete and all(a.get("masks", {}).get("value") is True for a in actions)
    return complete, values


def saved_config(output):
    config = json.loads((output / "generation_config.json").read_text())
    recipe = output / "generation_recipe.py"
    if not recipe.is_file() or hashlib.sha256(recipe.read_bytes()).hexdigest() != config.get("generator_sha256"):
        raise ValueError("persisted_generation_recipe_hash_mismatch")
    return config


def checked_source_index(index, config):
    partition = config.get("execution_partition", {"shard_id": 0, "shard_count": 1})
    if type(index) is not int or index < 0 or index % partition["shard_count"] != partition["shard_id"]:
        raise ValueError("source_shard_identity_mismatch")
    return index


def validate_attempt_intent(attempt, config):
    """Reject corrupt durable intent before synthesizing or deleting anything."""
    index = checked_source_index(attempt["source_index"], config)
    battle = attempt.get("source_battle_index")
    if type(battle) is not int or battle != index // config["roots_per_battle"]:
        raise ValueError("attempt_battle_identity_mismatch")
    if type(attempt.get("teacher_request_may_have_started")) is not bool:
        raise ValueError("attempt_teacher_request_state_invalid")
    for field in ("requested_action_worlds", "uncommitted_requested_action_worlds", "planned_evaluation_worlds", "planned_exploration_worlds"):
        value = attempt.get(field)
        if value is not None and (type(value) is not int or value < 0):
            raise ValueError("invalid_requested_world_budget")
    if attempt["teacher_request_may_have_started"] and attempt.get("requested_action_worlds") is None:
        raise ValueError("teacher_request_budget_missing")
    for field in ("elapsed_seconds", "elapsed_seconds_observed"):
        value = attempt.get(field)
        if value is not None and (type(value) not in (int, float) or not math.isfinite(value) or value < 0):
            raise ValueError("invalid_attempt_elapsed")
    label_hash = sha({key: value for key, value in config.items() if key != "execution_partition"})
    if attempt.get("generation_version") not in (None, config["version"]) or attempt.get("generation_config_sha256") not in (None, label_hash):
        raise ValueError("attempt_generation_config_mismatch")
    return index


def begin_attempt(output, config, attempt):
    path = output / "inflight_attempt.json"
    if path.exists(): raise ValueError("unreconciled_inflight_attempt")
    validate_attempt_intent(attempt, config)
    atomic_json(path, {"schema": INFLIGHT_SCHEMA, "config_sha256": sha(config),
                       "stage": "source_started", "attempt": deepcopy(attempt)})


def checkpoint_attempt(output, config, attempt, stage, *, started=None, record=None):
    path = output / "inflight_attempt.json"
    old = json.loads(path.read_text())
    if old.get("config_sha256") != sha(config) or old["attempt"]["source_index"] != attempt["source_index"]:
        raise ValueError("inflight_identity_mismatch")
    if started is not None:
        attempt["elapsed_seconds_observed"] = time.monotonic() - started
        attempt["timing_status"] = "partial"
    validate_attempt_intent(attempt, config)
    value = {"schema": INFLIGHT_SCHEMA, "config_sha256": sha(config), "stage": stage,
             "attempt": deepcopy(attempt)}
    if record is not None:
        value.update(record=record, record_sha256=sha(record))
    elif "record" in old:
        value.update(record=old["record"], record_sha256=old["record_sha256"])
    atomic_json(path, value)


def clear_inflight(output):
    path = output / "inflight_attempt.json"
    if path.exists():
        path.unlink(); fsync_directory(output)


def finish_attempt(output, config, attempt, started, record=None):
    if record is not None and attempt.get("status") == "accepted":
        digest = public_sha(record["public_input"])
        if attempt.get("public_input_digest") not in (None, digest):
            raise ValueError("accepted_attempt_public_digest_mismatch")
        attempt.update(public_input_digest=digest, public_digest_scheme=PUBLIC_IDENTITY_SCHEME)
    attempt["elapsed_seconds"] = time.monotonic() - started
    attempt["elapsed_seconds_observed"] = attempt["elapsed_seconds"]
    attempt["timing_status"] = "measured"
    attempt["timing_window"] = "source_attempt_start_through_result_commit_or_failure"
    validate_journal(attempt, config)
    checkpoint_attempt(output, config, attempt, "journal_ready", record=record)
    append_json(output / "attempts.jsonl", attempt)
    clear_inflight(output)


def validate_durable_record(output, row, config, validate, verify_raw=False):
    validate(row)
    audit = row["audit_only"]
    index = checked_source_index(audit["generation_source_index"], config)
    label_hash = sha({key: value for key, value in config.items() if key != "execution_partition"})
    if audit.get("generation_config_sha256") != label_hash or audit.get("versions", {}).get("generation_config") != label_hash:
        raise ValueError("durable_record_generation_config_mismatch")
    for field in ("source_run_group", "source_combat_id", "branch_family"):
        if not isinstance(audit.get(field), str) or not audit[field]: raise ValueError("durable_record_provenance_missing")
    if config.get("public_identity_scheme") == PUBLIC_IDENTITY_SCHEME:
        if audit.get("public_identity_scheme") != PUBLIC_IDENTITY_SCHEME or audit.get("normalized_public_input_digest") != public_sha(row["public_input"]):
            raise ValueError("durable_record_public_identity_mismatch")
    if verify_raw:
        raw = audit.get("outcome_samples")
        if "outcome_samples_ref" in audit:
            if raw is not None: raise ValueError("ambiguous_raw_outcome_sources")
            raw = read_outcomes(output, audit["outcome_samples_ref"])
        actions = row["targets"]["actions"]
        if not isinstance(raw, list) or len(raw) != len(actions): raise ValueError("durable_record_raw_outcomes_missing")
        for i, (group, action) in enumerate(zip(raw, actions)):
            if group.get("action_index") != i or len(group.get("outcomes", [])) != action["allocated_worlds"]:
                raise ValueError("durable_record_raw_outcome_count_mismatch")
            counts = Counter()
            for outcome in group["outcomes"]:
                kind = outcome.get("terminalKind")
                if kind in ("Win", "Loss") and outcome.get("isTrueTerminal") is True and outcome.get("settlementComplete") is True:
                    counts["completed_worlds"] += 1
                elif kind in ("ComputeTruncated", "EngineError", "PolicyNonterminating"):
                    counts[{"ComputeTruncated": "truncated_worlds", "EngineError": "error_worlds", "PolicyNonterminating": "other_worlds"}[kind]] += 1
                else:
                    raise ValueError("durable_record_raw_terminal_unresolved")
            if any(counts[field] != action[field] for field in ("completed_worlds", "truncated_worlds", "error_worlds", "other_worlds")):
                raise ValueError("durable_record_raw_accounting_mismatch")
    return index


def recovered_timing(attempt):
    value = deepcopy(attempt)
    # Only a persisted final timing measurement can be called complete.
    observed = value.get("elapsed_seconds_observed")
    value["elapsed_seconds"] = None
    value["timing_status"] = "recovered_partial" if type(observed) in (int, float) and math.isfinite(observed) and observed >= 0 else "recovered_unknown"
    value["elapsed_seconds_observed"] = observed if value["timing_status"] == "recovered_partial" else None
    return value


def recovered_accepted(row, config, duplicate, first_index, inflight=None):
    audit = row["audit_only"]
    index = audit["generation_source_index"]
    if inflight and inflight["stage"] == "journal_ready":
        attempt = deepcopy(inflight["attempt"])
        if attempt.get("status") != "accepted": raise ValueError("durable_decision_conflicts_with_failed_inflight")
        reason = "saved_final_journal_replayed"
    elif inflight:
        attempt = recovered_timing(inflight["attempt"])
        reason = "durable_decision_missing_journal"
    else:
        # Legacy recovery reads durable facts; never executes an old recipe or
        # substitutes teacher time for the unavailable complete source-attempt time.
        worlds = config.get("teacher_options", {}).get("evaluationSeeds")
        budget = len(row["targets"]["actions"]) * len(worlds) if isinstance(worlds, list) else None
        attempt = {"source_index": index, "source_battle_index": index // config["roots_per_battle"],
                   "source_step": audit.get("generation_source_step"), "source_category": audit.get("source_category", "undeclared"),
                   "scenario": audit.get("scenario_recipe"), "generation_version": audit.get("generation_version", config["version"]),
                   "generation_decision_index": audit.get("generation_decision_index"),
                   "requested_source_phase": audit.get("generation_source_phase"), "requested_action_worlds": budget,
                   "requested_world_budget_source": "frozen_config_and_durable_candidate_count" if budget is not None else "unavailable",
                   "elapsed_seconds": None, "elapsed_seconds_observed": None, "timing_status": "recovered_unknown"}
        reason = "legacy_durable_row_missing_journal"
    complete, value = usable_counts(row)
    attempt.update(status="accepted", recovered=True, recovery_reason=reason,
        recovery_tool_version=VERSION, recovery_public_identity_scheme=PUBLIC_IDENTITY_SCHEME,
        public_input_digest=public_sha(row["public_input"]), public_digest_scheme=PUBLIC_IDENTITY_SCHEME,
        duplicate_public_input=duplicate, effective_unique_root=not duplicate, first_public_input_source_index=first_index,
        complete_candidate_root=complete, complete_value_root=value,
        generation_config_sha256=audit["generation_config_sha256"])
    return attempt


def validate_journal(attempt, config):
    index = checked_source_index(attempt["source_index"], config)
    if attempt.get("source_battle_index") != index // config["roots_per_battle"]:
        raise ValueError("attempt_battle_identity_mismatch")
    if attempt.get("status") not in ("accepted", "failed_attempt", "duplicate_public_input", "interrupted_attempt"):
        raise ValueError("incomplete_or_unknown_attempt_journal")
    label_hash = sha({key: value for key, value in config.items() if key != "execution_partition"})
    if attempt.get("generation_version") not in (None, config["version"]) or attempt.get("generation_config_sha256") not in (None, label_hash):
        raise ValueError("attempt_generation_config_mismatch")
    elapsed, observed = attempt.get("elapsed_seconds"), attempt.get("elapsed_seconds_observed")
    number = lambda value: type(value) in (int, float) and math.isfinite(value) and value >= 0
    if elapsed is None:
        if attempt.get("recovered") is not True or attempt.get("timing_status") not in ("recovered_unknown", "recovered_partial"):
            raise ValueError("unknown_attempt_time_without_explicit_recovery")
        if attempt["timing_status"] == "recovered_partial" and not number(observed):
            raise ValueError("invalid_recovered_partial_time")
    elif not number(elapsed):
        raise ValueError("invalid_attempt_elapsed")
    if observed is not None and (not number(observed) or elapsed is not None and observed > elapsed):
        raise ValueError("invalid_observed_elapsed_bound")
    for field in ("requested_action_worlds", "uncommitted_requested_action_worlds"):
        value = attempt.get(field)
        if value is not None and (type(value) is not int or value < 0):
            raise ValueError("invalid_requested_world_budget")
    return index


def reconcile_corpus(output, config, validate):
    """Caller holds generator.lock. Repair commit gaps using only durable facts."""
    if saved_config(output) != config: raise ValueError("recovery_config_mismatch")
    path = output / "inflight_attempt.json"
    inflight = json.loads(path.read_text()) if path.exists() else None
    if inflight:
        if inflight.get("schema") != INFLIGHT_SCHEMA or inflight.get("config_sha256") != sha(config):
            raise ValueError("inflight_config_or_schema_mismatch")
        validate_attempt_intent(inflight["attempt"], config)
        if inflight.get("stage") not in ("source_started", "teacher_requested", "record_ready", "journal_ready"):
            raise ValueError("inflight_stage_unknown")
        if inflight["stage"] == "journal_ready":
            validate_journal(inflight["attempt"], config)
        if "record" in inflight and sha(inflight["record"]) != inflight.get("record_sha256"):
            raise ValueError("inflight_record_checksum_mismatch")
    journals, repairs = {}, []
    for attempt in existing_rows(output / "attempts.jsonl", repairs):
        index = validate_journal(attempt, config)
        if index in journals: raise ValueError("duplicate_attempt_journal_source")
        journals[index] = attempt
    state = {"seen": {}, "battles": set(), "max_source": -1, "unique_roots": 0, "complete_roots": 0,
             "value_roots": 0, "decision_records": 0, "duplicate_records": 0, "errors": Counter(), "recovered_journals": 0, "preserved_partial_tails": repairs}
    rows = {}
    def inspect_row(row):
        index = validate_durable_record(output, row, config, validate)
        if index in rows: raise ValueError("duplicate_durable_decision_source")
        digest = public_sha(row["public_input"])
        duplicate = digest in state["seen"]
        first = state["seen"].get(digest, index)
        matching = inflight if inflight and inflight["attempt"]["source_index"] == index else None
        if matching and "record" in matching and sha(row) != matching["record_sha256"]:
            raise ValueError("inflight_and_durable_row_disagree")
        journal = journals.get(index)
        if journal is None:
            validate_durable_record(output, row, config, validate, verify_raw=True)
            journal = recovered_accepted(row, config, duplicate, first, matching)
            validate_journal(journal, config)
            append_json(output / "attempts.jsonl", journal)
            journals[index] = journal
            state["recovered_journals"] += 1
        elif journal["status"] != "accepted":
            raise ValueError("durable_decision_conflicts_with_nonaccepted_journal")
        else:
            scheme = journal.get("public_digest_scheme")
            expected = digest if scheme == PUBLIC_IDENTITY_SCHEME else sha(normalize_numeric_leaves(row["public_input"])) if scheme == "m5_numeric_canonical_v1" else sha(row["public_input"])
            if scheme not in (None, PUBLIC_IDENTITY_SCHEME, "m5_numeric_canonical_v1") or journal.get("public_input_digest") not in (None, expected):
                raise ValueError("accepted_journal_public_digest_mismatch")
        rows[index] = sha(row)
        state["decision_records"] += 1
        if duplicate:
            state["duplicate_records"] += 1
        else:
            state["seen"][digest] = index
            complete, value = usable_counts(row)
            state["unique_roots"] += 1; state["complete_roots"] += complete; state["value_roots"] += value
    for row in existing_rows(output / "decisions.jsonl", repairs):
        inspect_row(row)
    if inflight:
        index = inflight["attempt"]["source_index"]
        if index not in rows and "record" in inflight:
            record = inflight["record"]
            if validate_durable_record(output, record, config, validate, verify_raw=True) != index:
                raise ValueError("inflight_record_source_mismatch")
            append_json(output / "decisions.jsonl", record)
            inspect_row(record)
        if index not in journals:
            if inflight["stage"] == "journal_ready":
                attempt = deepcopy(inflight["attempt"])
                if attempt.get("status") == "accepted": raise ValueError("accepted_inflight_without_saved_record")
                attempt.update(recovered=True, recovery_reason="saved_final_journal_replayed",
                               recovery_tool_version=VERSION, recovery_public_identity_scheme=PUBLIC_IDENTITY_SCHEME)
            else:
                attempt = recovered_timing(inflight["attempt"])
                attempt.update(status="interrupted_attempt", reason="process_interrupted_without_durable_decision",
                    recovered=True, recovery_reason="inflight_without_durable_result",
                    recovery_tool_version=VERSION, recovery_public_identity_scheme=PUBLIC_IDENTITY_SCHEME,
                    uncommitted_requested_action_worlds=(attempt.get("requested_action_worlds")
                        if attempt.get("teacher_request_may_have_started") else 0))
            validate_journal(attempt, config)
            append_json(output / "attempts.jsonl", attempt)
            journals[index] = attempt
            state["recovered_journals"] += 1
        clear_inflight(output)
    for index, attempt in journals.items():
        if attempt["status"] == "accepted" and index not in rows:
            raise ValueError("accepted_journal_missing_durable_decision")
        state["battles"].add(index // config["roots_per_battle"])
        state["max_source"] = max(state["max_source"], index)
        if attempt["status"] != "accepted":
            state["errors"][attempt.get("reason", attempt["status"]).split(":", 1)[0]] += 1
    state["attempts"] = len(journals)
    state["public_identity_scheme"] = PUBLIC_IDENTITY_SCHEME
    state["stored_generation_identity_scheme"] = config.get("public_identity_scheme")
    return state


def recover_only(args):
    output = args.output.resolve()
    with (output / "generator.lock").open("a") as lock:
        try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc: raise RuntimeError("another_generator_owns_this_corpus") from exc
        config = saved_config(output)
        sys.path.insert(0, str(args.repo.resolve() / "python"))
        from nosl.data import validate_record
        from nosl.schema import load_config
        student = load_config(args.repo.resolve() / "configs/student.pilot.json")
        state = reconcile_corpus(output, config, lambda row: validate_record(row, student))
        result = {key: state[key] for key in ("unique_roots", "complete_roots", "decision_records", "duplicate_records", "recovered_journals", "preserved_partial_tails", "public_identity_scheme", "stored_generation_identity_scheme")}
        print(canonical({"status": "recovery_only_complete", "simulation_started": False, **result}))
        return result


def run(args):
    args.output.mkdir(parents=True, exist_ok=True)
    with (args.output / "generator.lock").open("a") as lock:
        try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as exc: raise RuntimeError("another_generator_owns_this_corpus") from exc
        return run_locked(args)


def run_locked(args):
    repo, output = args.repo.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    catalog = source_catalog(repo)
    sys.path.insert(0, str(repo / "python"))
    from nosl.data import validate_record
    from nosl.schema import load_config
    student_config = load_config(repo / "configs/student.pilot.json")
    options = {"mode": args.teacher, "evaluationSeeds": list(range(100001, 100001 + args.worlds)),
               "explorationSeeds": list(range(200001, 200001 + args.exploration_worlds)) if args.teacher == "T1" else [],
               "maxDecisions": args.max_decisions, "treeDepth": args.tree_depth, "formalLabels": False}
    config = {"version": VERSION, "catalog_hash": sha(catalog), "teacher_options": options,
              "seed_prefix": args.seed_prefix, "source_kind": "constructed", "data_mode": args.mode, "roots_per_battle": args.roots_per_battle,
              "root_policy": args.root_policy, "max_source_decisions": args.max_source_decisions,
              "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
              "formal_labels": False, "training_started": False,
              "execution_partition": {"shard_id": args.shard_id, "shard_count": args.shard_count},
              "runtime_files": {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                  for p in sorted((repo / "src/Nosl.Worker/bin/Release/net9.0").iterdir())
                  if p.suffix in (".dll", ".json")},
              "generator_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "student_config_sha256": sha(student_config),
              "validator_files": {name: hashlib.sha256((repo / "python/nosl" / name).read_bytes()).hexdigest() for name in ("schema.py", "data.py", "public_identity.py")}}

    config_file = output / "generation_config.json"
    if config_file.exists() and json.loads(config_file.read_text()) != config:
        raise ValueError("Generation version/config changed: use a new output corpus and relabel intentionally")
    atomic_json(config_file, config)
    recipe_file = output / "generation_recipe.py"
    if recipe_file.exists() and hashlib.sha256(recipe_file.read_bytes()).hexdigest() != config["generator_sha256"]:
        raise ValueError("persisted_generation_recipe_hash_mismatch")
    if not recipe_file.exists():
        with recipe_file.open("xb") as stream:
            stream.write(Path(__file__).read_bytes()); stream.flush(); os.fsync(stream.fileno())
        fsync_directory(output)
    label_config = {k: v for k, v in config.items() if k != "execution_partition"}
    state = reconcile_corpus(output, config, lambda row: validate_record(row, student_config))
    seen, battles_seen = state["seen"], state["battles"]
    unique_count, complete, scored = state["unique_roots"], state["complete_roots"], state["value_roots"]
    max_source, errors = state["max_source"], Counter(state["errors"])
    duplicate_count, decision_count = state["duplicate_records"], state["decision_records"]
    next_index = args.shard_id if max_source < 0 else max_source + args.shard_count
    progress = {"version": VERSION, "status": "data_stage_already_complete", "target_effective_roots": args.target_roots,
                "attempts": state["attempts"], "effective_full_candidate_roots": complete, "unique_roots": unique_count,
                "all_candidate_value_roots": scored, "decision_records": decision_count, "duplicate_records_preserved": duplicate_count,
                "source_battles_attempted": len(battles_seen), "shard_id": args.shard_id, "shard_count": args.shard_count,
                "recovered_journals": state["recovered_journals"], "preserved_partial_tails": state["preserved_partial_tails"],
                "public_identity_scheme": PUBLIC_IDENTITY_SCHEME, "errors": dict(errors), "training_started": False, "formal_labels": False}
    started = time.monotonic(); worker = None
    try:
        while complete < args.target_roots:
            if (output / "PAUSE_REQUESTED").exists():
                progress.update(status="paused_at_safe_boundary", effective_full_candidate_roots=complete, unique_roots=unique_count)
                atomic_json(output / "progress.json", progress)
                break
            if (next_index - args.shard_id) // args.shard_count >= args.max_attempts:
                raise RuntimeError("attempt_budget_exhausted_without_target_effective_roots")
            source_index = next_index; next_index += args.shard_count
            battle_index, source_step = divmod(source_index, args.roots_per_battle)
            battles_seen.add(battle_index)
            sc, category = scenario(battle_index, catalog, args.seed_prefix, args.root_policy)
            t = time.monotonic()
            attempt = {"source_index": source_index, "source_category": category, "scenario": sc,
                       "status": "started", "generation_version": VERSION, "source_battle_index": battle_index, "source_step": source_step,
                       "requested_source_phase": requested_phase(args.root_policy, source_step)}
            attempt.update(generation_config_sha256=sha(label_config), planned_evaluation_worlds=args.worlds,
                           planned_exploration_worlds=len(options["explorationSeeds"]), teacher_request_may_have_started=False,
                           requested_action_worlds=None, elapsed_seconds=None, timing_status="unknown")
            begin_attempt(output, config, attempt)
            if worker is None:
                worker = Worker(repo, output, args.timeout, args.max_worker_mib)
            failure = None
            try:
                packet, source_decisions, phase = collect_source_snapshot(worker, sc, args.root_policy,
                    source_step, args.max_source_decisions)
                attempt["generation_decision_index"] = source_decisions
                root_options = dict(options)
                def seeds(label, count):
                    return [int.from_bytes(hashlib.sha256(f"{args.seed_prefix}:{source_index}:{label}:{i}".encode()).digest()[:8], "big") for i in range(count)]
                root_options["evaluationSeeds"] = seeds("independent-evaluation", args.worlds)
                root_options["explorationSeeds"] = seeds("exploration", args.exploration_worlds) if args.teacher == "T1" else []
                command = {"op": "teacher_record", "options": root_options,
                           "sourceRun": args.seed_prefix + ":run:" + str(battle_index),
                           "sourceCombat": args.seed_prefix + ":combat:" + str(battle_index),
                           "branchFamily": args.seed_prefix + ":family:" + str(battle_index)}
                attempt["requested_action_worlds"] = len(packet["actions"]) * args.worlds
                attempt["teacher_request_may_have_started"] = True
                checkpoint_attempt(output, config, attempt, "teacher_requested", started=t)
                record = worker.request(command)
                if "public_input" not in record: raise ValueError("teacher:" + canonical(record))
                validate_record(record, student_config)
            except (TimeoutError, BrokenPipeError, RuntimeError, ValueError, OSError) as exc:
                failure = str(exc)
            if failure is not None:
                attempt.update(status="failed_attempt", reason=failure,
                    uncommitted_requested_action_worlds=(attempt["requested_action_worlds"]
                        if attempt["teacher_request_may_have_started"] else 0))
                finish_attempt(output, config, attempt, t)
                errors[failure.split(":", 1)[0]] += 1
                worker.close(); worker = None
            else:
                # Storage failures deliberately propagate with inflight intent intact;
                # they must never relabel a durable successful row as an engine failure.
                digest = public_sha(record["public_input"])
                duplicate = digest in seen
                record["audit_only"].update(generation_source_index=source_index, source_category=category,
                    generation_version=VERSION, scenario_recipe=sc, generation_source_step=source_step,
                    generation_decision_index=source_decisions, generation_source_phase=phase,
                    source_policy_version="nosl-public-rules-v1", generation_config_sha256=sha(label_config), generation_shard_id=args.shard_id,
                    duplicate_public_input=duplicate, normalized_public_input_digest=digest,
                    public_identity_scheme=PUBLIC_IDENTITY_SCHEME,
                    first_public_input_source_index=seen.get(digest, source_index))
                record["audit_only"]["versions"]["generation_config"] = sha(label_config)
                record["audit_only"]["engineering_smoke"] = args.mode == "engineering-smoke"
                record["audit_only"]["experimental_pilot_data"] = args.mode == "pilot"
                is_complete, has_value = usable_counts(record)
                record["audit_only"]["effective_full_candidate_root"] = is_complete and not duplicate
                archive_outcomes(output, source_index, record)
                checkpoint_attempt(output, config, attempt, "record_ready", started=t, record=record)
                append_json(output / "decisions.jsonl", record)
                attempt.update(status="accepted", public_input_digest=digest, public_digest_scheme=PUBLIC_IDENTITY_SCHEME,
                               complete_candidate_root=is_complete, complete_value_root=has_value,
                               duplicate_public_input=duplicate, effective_unique_root=not duplicate,
                               first_public_input_source_index=seen.get(digest, source_index))
                finish_attempt(output, config, attempt, t, record=record)
                decision_count += 1
                if duplicate:
                    duplicate_count += 1
                else:
                    seen[digest] = source_index; unique_count += 1; complete += is_complete; scored += has_value
            progress = {"version": VERSION, "target_effective_roots": args.target_roots,
                "attempts": (next_index - args.shard_id) // args.shard_count, "source_battles_attempted": len(battles_seen), "shard_id": args.shard_id, "shard_count": args.shard_count, "unique_roots": unique_count, "effective_full_candidate_roots": complete,
                "all_candidate_value_roots": scored, "decision_records": decision_count, "duplicate_records_preserved": duplicate_count,
                "errors": dict(errors),
                "current_process_seconds": time.monotonic() - started, "training_started": False,
                "formal_labels": False, "status": "running" if complete < args.target_roots else "data_stage_complete"}
            atomic_json(output / "progress.json", progress)
            if progress["attempts"] % 10 == 0: print(canonical(progress), flush=True)
    finally:
        if worker is not None: worker.close()
    atomic_json(output / "progress.json", progress)
    print(canonical(progress), flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--repo", type=Path, default=ROOT); p.add_argument("--output", type=Path, required=True)
    p.add_argument("--recover-only", action="store_true", help="Reconcile saved facts/journals without starting a worker or changing corpus versions")
    p.add_argument("--mode", choices=["engineering-smoke", "pilot"])
    p.add_argument("--target-roots", type=int); p.add_argument("--max-attempts", type=int)
    p.add_argument("--worlds", type=int, default=4); p.add_argument("--teacher", choices=["T0", "T1"], default="T0")
    p.add_argument("--exploration-worlds", type=int, default=8); p.add_argument("--max-decisions", type=int, default=200)
    p.add_argument("--max-worker-mib", type=int, default=768)
    p.add_argument("--shard-id", type=int, default=0); p.add_argument("--shard-count", type=int, default=1)
    p.add_argument("--root-policy", choices=["opening-prefix-v1", "public-phase-v1"], default="public-phase-v1")
    p.add_argument("--roots-per-battle", type=int, default=4)
    p.add_argument("--max-source-decisions", type=int, default=160)
    p.add_argument("--tree-depth", type=int, default=4); p.add_argument("--timeout", type=float, default=90)
    p.add_argument("--seed-prefix", default="nosl-m5-engineering-v1")
    args = p.parse_args()
    if args.recover_only:
        recover_only(args)
        return
    if args.mode is None or args.target_roots is None or args.max_attempts is None:
        p.error("generation requires --mode, --target-roots and --max-attempts")
    if args.root_policy == "public-phase-v1" and args.roots_per_battle != 4:
        p.error("public-phase-v1 predeclares exactly four source phases per battle")
    if args.shard_count < 1 or not 0 <= args.shard_id < args.shard_count: p.error("invalid source shard")
    if min(args.target_roots, args.max_attempts, args.worlds, args.max_decisions, args.roots_per_battle, args.max_worker_mib, args.max_source_decisions) <= 0:
        p.error("positive bounded budgets required")
    run(args)


if __name__ == "__main__": main()
