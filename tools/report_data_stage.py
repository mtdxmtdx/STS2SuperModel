#!/usr/bin/env python3
"""Read-only streaming M5 stage diagnostics; never generates, prepares or trains.

Pass one corpus or compatible shard directories. Exact M5 public-input digests
and global source identities are indexed in a temporary SQLite database, not a
list of corpus records. Incompatible engineering/pilot corpora must be reported
separately. --verify-outcomes also checks bounded gzip members and raw accounting.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import sqlite3
import sys
import tempfile
import zlib

from prepare_dataset import (COUNT_FIELDS, HEADS, canonical_json, has_usable_targets, load_student_config,
                             public_digest, validate_record)
from nosl.public_identity import PUBLIC_IDENTITY_SCHEME

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "nosl.data-stage-report.v1"


class ReportError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ReportError(message)


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def digest_bytes(raw):
    return hashlib.sha256(raw).hexdigest()


def generator_canonical(value):
    """Historical config hashes use generator serialization, not M5 normalization."""
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def load_json(raw):
    return json.loads(raw, parse_constant=lambda value: (_ for _ in ()).throw(ReportError("nonfinite_json_number")))


def stable_config(config):
    return {key: value for key, value in config.items() if key != "execution_partition"}


def discover_shards(paths):
    shards = []
    for supplied in paths:
        path = Path(supplied).resolve()
        if (path / "generation_config.json").is_file():
            shards.append(path)
        else:
            children = sorted(p for p in path.glob("shard-*") if (p / "generation_config.json").is_file())
            require(bool(children), "no_generation_shards:" + str(path))
            shards.extend(children)
    require(len(shards) == len(set(shards)), "duplicate_shard_path")
    return sorted(shards)


class Problems:
    def __init__(self):
        self.counts = Counter()
        self.examples = []

    def add(self, kind, path, line, detail):
        key = kind + ":" + str(detail).split(":", 1)[0][:100]
        if key not in self.counts and len(self.counts) >= 100:
            key = "other_errors"
        self.counts[key] += 1
        if len(self.examples) < 30:
            self.examples.append({"kind": kind, "path": str(path), "line": line, "detail": str(detail)[:500]})


class StreamingReader:
    def __init__(self, problems, max_line_bytes):
        self.problems = problems
        self.max_line_bytes = max_line_bytes
        self.files = []
        self.snapshots = []

    def finish(self):
        # A file can change after its own read while another shard is scanned.
        for descriptor, expected in self.snapshots:
            path = Path(descriptor["path"])
            current = path.stat()
            if (current.st_size, current.st_mtime_ns, current.st_ino) != expected:
                descriptor["stable_during_read"] = False
                self.problems.add("snapshot", path, None, "source_changed_before_scan_completed")

    def rows(self, path):
        """Do not use generator.existing_rows: it repairs/truncates input files."""
        before = path.stat()
        sha = hashlib.sha256()
        with path.open("rb") as stream:
            line_number = 0
            while True:
                raw = stream.readline(self.max_line_bytes + 1)
                if not raw:
                    break
                line_number += 1
                sha.update(raw)
                if len(raw) > self.max_line_bytes:
                    while raw and not raw.endswith(b"\n"):
                        raw = stream.readline(self.max_line_bytes + 1)
                        sha.update(raw)
                    self.problems.add("jsonl", path, line_number, "line_size_budget_exceeded")
                    yield line_number, None
                    continue
                if not raw.strip():
                    continue
                if not raw.endswith(b"\n"):
                    self.problems.add("jsonl", path, line_number, "unterminated_record_not_repaired")
                    yield line_number, None
                    continue
                try:
                    value = load_json(raw)
                    require(isinstance(value, dict), "row_not_object")
                except (ValueError, UnicodeError) as exc:
                    self.problems.add("jsonl", path, line_number, exc)
                    yield line_number, None
                    continue
                yield line_number, value
        after = path.stat()
        stable = (before.st_size, before.st_mtime_ns, before.st_ino) == (after.st_size, after.st_mtime_ns, after.st_ino)
        descriptor = {"path": str(path), "bytes": after.st_size, "sha256": sha.hexdigest(), "stable_during_read": stable}
        self.files.append(descriptor)
        self.snapshots.append((descriptor, (after.st_size, after.st_mtime_ns, after.st_ino)))
        if not stable:
            self.problems.add("snapshot", path, None, "source_changed_during_read")


def read_outcome_member(shard, reference, max_bytes):
    require(isinstance(reference, dict) and reference.get("format") == "independent-gzip-member-json", "outcome_reference_format")
    relative = reference.get("path")
    require(isinstance(relative, str) and not Path(relative).is_absolute(), "outcome_path_invalid")
    path = (shard / relative).resolve()
    require(path.is_relative_to(shard.resolve()), "outcome_path_outside_corpus")
    for key in ("offset", "compressed_bytes", "uncompressed_bytes", "action_count"):
        require(type(reference.get(key)) is int and reference[key] >= 0, "outcome_reference_integer_invalid")
    require(0 < reference["compressed_bytes"] <= max_bytes and 0 < reference["uncompressed_bytes"] <= max_bytes,
            "outcome_size_budget_exceeded")
    with path.open("rb") as stream:
        require(reference["offset"] + reference["compressed_bytes"] <= path.stat().st_size, "outcome_reference_range_invalid")
        stream.seek(reference["offset"])
        compressed = stream.read(reference["compressed_bytes"])
    decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
    raw = decoder.decompress(compressed, max_bytes + 1)
    require(len(raw) <= max_bytes and decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail,
            "outcome_gzip_member_invalid_or_oversized")
    require(len(raw) == reference["uncompressed_bytes"] and digest_bytes(raw) == reference.get("sha256"),
            "outcome_checksum_mismatch")
    outcomes = load_json(raw)
    require(isinstance(outcomes, list) and len(outcomes) == reference["action_count"], "outcome_action_count_mismatch")
    return outcomes


def verify_raw_outcomes(record, shard, max_bytes):
    audit = record["audit_only"]
    require(not ("outcome_samples" in audit and "outcome_samples_ref" in audit), "ambiguous_raw_outcome_sources")
    if "outcome_samples_ref" in audit:
        raw = read_outcome_member(shard, audit["outcome_samples_ref"], max_bytes)
        kind = "gzip_reference"
    else:
        raw = audit.get("outcome_samples")
        kind = "inline"
    actions = record["targets"]["actions"]
    require(isinstance(raw, list) and len(raw) == len(actions), "raw_outcomes_missing_or_action_count_mismatch")
    for index, (group, action) in enumerate(zip(raw, actions)):
        require(isinstance(group, dict) and group.get("action_index") == index and isinstance(group.get("outcomes"), list),
                "raw_outcome_action_alignment")
        samples = group["outcomes"]
        counts = Counter(allocated_worlds=len(samples))
        for outcome in samples:
            require(isinstance(outcome, dict), "raw_outcome_not_object")
            terminal = outcome.get("terminalKind")
            if terminal in ("Win", "Loss"):
                require(outcome.get("isTrueTerminal") is True and outcome.get("settlementComplete") is True, "raw_terminal_not_settled")
                counts["completed_worlds"] += 1
            elif terminal == "ComputeTruncated":
                counts["truncated_worlds"] += 1
            elif terminal == "EngineError":
                counts["error_worlds"] += 1
            elif terminal == "PolicyNonterminating":
                counts["other_worlds"] += 1
            else:
                raise ReportError("raw_terminal_kind_unknown")
        require(all(counts[name] == action[name] for name in COUNT_FIELDS), "raw_outcome_accounting_mismatch")
    return kind


def observed_content(public):
    """Public root entities only; requested recipes are not observed coverage."""
    obs = public["observation"]
    cards = []
    for pile in ("hand", "discard", "exhaust"):
        cards.extend(obs[pile])
    cards.extend(item["card"] for item in obs["unknownDraw"] + obs["knownDraw"])
    if obs.get("choice"):
        cards.extend(obs["choice"]["candidates"])
        for bundle in obs["choice"].get("bundles") or []:
            cards.extend(bundle)
    return {
        "cards": {card["id"] for card in cards},
        "card_upgrades": {card["id"] + ":" + str(card["upgrade"]) for card in cards},
        "potions": {item for item in obs["potions"] if item is not None},
        "relics": set(obs["relics"]),
        "enemies": {enemy["id"] for enemy in obs["enemies"]},
    }


HP_RATIO_BUCKETS = ("[0,0.10]", "(0.10,0.25]", "(0.25,0.50]", "(0.50,0.75]", "(0.75,1.00]")


def hp_fraction_bucket(hp, max_hp):
    # Integer cross-products preserve inclusive boundaries without float rounding.
    for percent, label in zip((10, 25, 50, 75, 100), HP_RATIO_BUCKETS):
        if 100 * hp <= percent * max_hp:
            return label
    return "above_1.00"


def index_bucket(value):
    if value is None:
        return "missing"
    if type(value) is not int or value < 0:
        return "invalid"
    # Display bound only, not a gameplay cap or quality threshold.
    return str(value) if value <= 1000 else "1001_plus"


def record_root_distribution(db, record, counts):
    obs, audit = record["public_input"]["observation"], record["audit_only"]
    population = "root_state_distribution"
    tally(db, population, "turn", index_bucket(obs["turn"]))
    tally(db, population, "source_step", index_bucket(audit.get("generation_source_step")))
    tally(db, population, "decision_index", index_bucket(audit.get("generation_decision_index")))
    revisions = {action["revision"] for action in record["public_input"]["candidate_actions"]}
    tally(db, population, "action_revision", index_bucket(next(iter(revisions))) if len(revisions) == 1 else "mixed")
    changed, lost, gained = obs["hp"] != obs["startHp"], obs["hp"] < obs["startHp"], obs["hp"] > obs["startHp"]
    counts["after_hp_change_roots"] += changed
    counts["after_hp_loss_roots"] += lost
    counts["after_hp_gain_roots"] += gained
    counts["hp_unchanged_from_start_roots"] += not changed
    counts["pending_choice_roots"] += obs.get("choice") is not None
    counts["turn_one_roots"] += obs["turn"] == 1
    counts["later_turn_roots"] += obs["turn"] > 1
    counts["later_turn_after_hp_loss_roots"] += obs["turn"] > 1 and lost
    bucket = hp_fraction_bucket(obs["hp"], obs["maxHp"])
    tally(db, population, "current_hp_fraction", bucket)
    if lost:
        tally(db, population, "current_hp_fraction_after_net_loss", bucket)
    for pile, size in (("hand", len(obs["hand"])), ("discard", len(obs["discard"])),
                       ("exhaust", len(obs["exhaust"])), ("draw", obs["drawCount"])):
        tally(db, population, "pile_" + pile, size)


def distribution_summary(tallies, counts):
    hist = tallies.get("root_state_distribution", {})
    roots = counts["unique_effective_public_roots"]
    fraction = lambda count: count / roots if roots else None
    sizes = {}
    for pile in ("hand", "discard", "exhaust", "draw"):
        values = hist.get("pile_" + pile, {})
        numeric = sorted((int(size), count) for size, count in values.items())
        size_count = sum(count for _, count in numeric)
        sizes[pile] = {"roots": size_count, "min": numeric[0][0] if numeric else None,
                       "max": numeric[-1][0] if numeric else None,
                       "mean": sum(size * count for size, count in numeric) / size_count if size_count else None,
                       "histogram": {str(size): count for size, count in numeric}}
    comparison = {key: {"roots": counts[key], "fraction": fraction(counts[key])} for key in
                  ("after_hp_change_roots", "after_hp_loss_roots", "after_hp_gain_roots", "hp_unchanged_from_start_roots")}
    ratio = {bucket: {"roots": hist.get("current_hp_fraction", {}).get(bucket, 0),
                      "fraction": fraction(hist.get("current_hp_fraction", {}).get(bucket, 0)),
                      "after_net_hp_loss_roots": hist.get("current_hp_fraction_after_net_loss", {}).get(bucket, 0)}
             for bucket in HP_RATIO_BUCKETS}
    absent = []
    if not roots:
        absent.append("no_usable_unique_roots")
    else:
        for count, label in (("later_turn_roots", "no_after_turn_one_roots"),
                             ("after_hp_change_roots", "no_net_hp_changed_roots"),
                             ("after_hp_loss_roots", "no_net_hp_loss_roots"),
                             ("pending_choice_roots", "no_pending_choice_roots")):
            if counts[count] == 0:
                absent.append(label)
    return {
        "population": "globally_deduplicated_usable_public_roots", "roots": roots,
        "turn_histogram": hist.get("turn", {}), "source_step_histogram": hist.get("source_step", {}),
        "decision_index_histogram": hist.get("decision_index", {}),
        "action_revision_histogram": hist.get("action_revision", {}),
        "hp_comparison_to_combat_start": comparison,
        "current_hp_over_current_max_hp_buckets": ratio,
        "pending_choice": {"roots": counts["pending_choice_roots"], "fraction": fraction(counts["pending_choice_roots"])},
        "pile_sizes": sizes,
        "adequacy_indicators": {
            "verdict": "TASK_SPECIFIC_DISTRIBUTION_REVIEW_REQUIRED", "universal_pass_threshold_applied": False,
            "turn_one_fraction": fraction(counts["turn_one_roots"]),
            "later_turn_roots": counts["later_turn_roots"], "later_turn_fraction": fraction(counts["later_turn_roots"]),
            "later_turn_after_net_hp_loss_roots": counts["later_turn_after_hp_loss_roots"],
            "observed_absences": absent,
            "actual_decision_index_missing_roots": hist.get("decision_index", {}).get("missing", 0),
            "source_step_missing_roots": hist.get("source_step", {}).get("missing", 0),
            "actual_decision_index_invalid_roots": hist.get("decision_index", {}).get("invalid", 0),
            "source_step_invalid_roots": hist.get("source_step", {}).get("invalid", 0),
        },
        "definitions": [
            "HP-change/loss are net comparisons of current hp with combat-start hp, not evidence of every historical damage/healing event. Low initial HP alone does not count as combat HP loss.",
            "HP ratio buckets use current hp/current maxHp. Their boundaries are descriptive display bins, not universal adequacy or safety cutoffs.",
            "generation_source_step is a declared requested source step; generation_decision_index is a separately declared actual within-combat decision count. Missing actual counts are not inferred from a recipe index.",
            "Action revision is reported as the public anti-stale token separately, without claiming it equals a curriculum step or actual decision count.",
            "Integer turn/index histograms are exact through 1000, with an explicit 1001_plus display bin; this is not a game horizon cap. Pile summaries use public array lengths and drawCount, including unidentified draw cards.",
            "A clean structural/label audit does not establish phase, health, history, or choice-distribution adequacy. Zero-support observations and concentration ratios require task-specific review, not an invented universal pass threshold."
        ],
    }


def setup_index(path):
    db = sqlite3.connect(path)
    db.execute("PRAGMA journal_mode=OFF")
    db.execute("PRAGMA temp_store=FILE")
    db.execute("PRAGMA cache_size=-4096")
    db.executescript("""
        CREATE TABLE roots(digest TEXT PRIMARY KEY);
        CREATE TABLE usable_roots(digest TEXT PRIMARY KEY);
        CREATE TABLE records(source INTEGER PRIMARY KEY, valid INTEGER NOT NULL);
        CREATE TABLE attempts(source INTEGER PRIMARY KEY, status TEXT NOT NULL);
        CREATE TABLE identities(population TEXT,kind TEXT,id TEXT,PRIMARY KEY(population,kind,id));
        CREATE TABLE tallies(population TEXT,kind TEXT,id TEXT,n INTEGER NOT NULL,PRIMARY KEY(population,kind,id));
    """)
    return db


def add_outcome_accounting(counter, record):
    """Saved accounting only; unknown/truncated/error mass never becomes losses."""
    counter["decision_records"] += 1
    counter["root_independent_eval_worlds"] += record["audit_only"]["n_independent_eval"]
    counter["root_exploration_worlds"] += record["audit_only"]["n_exploration"]
    for action in record["targets"]["actions"]:
        for field in COUNT_FIELDS:
            counter[field] += action[field]


def tally(db, population, kind, key):
    db.execute("INSERT INTO tallies VALUES(?,?,?,1) ON CONFLICT(population,kind,id) DO UPDATE SET n=n+1", (population, kind, str(key)))


def identity(db, population, kind, key):
    db.execute("INSERT OR IGNORE INTO identities VALUES(?,?,?)", (population, kind, str(key)))


def source_index(audit, config):
    index = audit.get("generation_source_index")
    require(type(index) is int and index >= 0, "generation_source_index_invalid")
    partition = config.get("execution_partition", {"shard_id": 0, "shard_count": 1})
    require(index % partition["shard_count"] == partition["shard_id"], "record_wrong_execution_partition")
    return index


def classify_failure(reason):
    """Classify saved facts only; a terminal source boundary is not a defeat."""
    if reason.startswith("source_snapshot_unavailable:"):
        try:
            detail = load_json(reason.split(":", 1)[1])
            require(isinstance(detail, dict), "source_snapshot_detail_invalid")
            why = detail.get("reason")
        except (ValueError, TypeError):
            return "source_snapshot_unclassified", "source_snapshot_unavailable:malformed"
        if why == "terminal_before_requested_phase":
            return "source_phase_absent", "source_snapshot_unavailable:" + why
        if why == "source_decision_budget_exhausted":
            return "source_budget_exhausted", "source_snapshot_unavailable:" + why
        return "source_snapshot_unclassified", "source_snapshot_unavailable:unknown_reason"
    if reason.startswith("reset:"):
        try:
            packet = load_json(reason[len("reset:"):])
            require(isinstance(packet, dict) and isinstance(packet.get("status"), str), "reset_packet_missing_status")
        except (ValueError, TypeError):
            return "reset_unclassified", "reset:malformed_or_missing_status"
        status = packet["status"]
        if status in ("terminal_settled", "terminal_pending_settlement"):
            return "reset_terminal_boundary", "reset:" + status
        if status in ("unsupported_capability", "unsupported_content"):
            return "reset_unsupported", "reset:" + status
        if status in ("engine_error", "error"):
            return "reset_engine_error", "reset:" + status
        return "reset_unclassified", "reset:other_status"
    if "worker_response_deadline" in reason or "Timeout" in reason:
        return "timeout", "worker_response_deadline"
    if "worker_memory_budget_exceeded" in reason:
        return "memory_budget", "worker_memory_budget_exceeded"
    return "other_failure", reason.split(":", 1)[0][:160]


def report_stage(paths, *, verify_outcomes=False, max_line_bytes=64 * 1024 * 1024,
                 max_outcome_bytes=64 * 1024 * 1024, student_config=None, temporary_dir=None):
    require(type(max_line_bytes) is int and max_line_bytes > 0 and type(max_outcome_bytes) is int and max_outcome_bytes > 0,
            "positive_streaming_budgets_required")
    shards = discover_shards(paths)
    configs, fingerprints, common, common_versions = {}, [], None, None
    partition_ids = set()
    for shard in shards:
        path = shard / "generation_config.json"
        raw = path.read_bytes()
        config = load_json(raw)
        require(isinstance(config, dict) and type(config.get("roots_per_battle")) is int and config["roots_per_battle"] > 0,
                "generation_config_invalid")
        stable = stable_config(config)
        if common is None:
            common = stable
        require(canonical_json(stable) == canonical_json(common), "incompatible_generation_configs_report_corpora_separately")
        partition = config.get("execution_partition", {"shard_id": 0, "shard_count": 1})
        require(isinstance(partition, dict) and type(partition.get("shard_id")) is int and type(partition.get("shard_count")) is int
                and 0 <= partition["shard_id"] < partition["shard_count"], "invalid_execution_partition")
        pair = (partition["shard_count"], partition["shard_id"])
        require(pair not in partition_ids, "duplicate_execution_partition")
        require(not partition_ids or next(iter(partition_ids))[0] == pair[0], "partition_count_mismatch")
        partition_ids.add(pair)
        recipe = shard / "generation_recipe.py"
        require(recipe.is_file() and digest_bytes(recipe.read_bytes()) == config.get("generator_sha256"), "generation_recipe_checksum_mismatch")
        configs[shard] = config
        fingerprints.append({"path": str(path), "sha256": digest_bytes(raw)})
    pipeline_config = load_json((ROOT / "configs/data_pipeline.v1.json").read_bytes())
    student_config = student_config or load_student_config()
    problems = Problems()
    reader = StreamingReader(problems, max_line_bytes)
    counts, worlds, timings, measured, failures, raw_checks = Counter(), Counter(), Counter(), Counter(), Counter(), Counter()
    outcome_accounting = {name: Counter() for name in ("valid_records", "diagnostic_only_records", "unique_valid_public_roots")}
    peak_rss = None
    with tempfile.TemporaryDirectory(prefix="nosl-stage-report-", dir=temporary_dir) as temp:
        db = setup_index(Path(temp) / "index.sqlite3")
        try:
            for shard in shards:
                config = configs[shard]
                config_hash = digest_bytes(generator_canonical(stable_config(config)).encode())
                for line, record in reader.rows(shard / "decisions.jsonl"):
                    counts["decision_rows"] += 1
                    if record is None:
                        counts["invalid_decision_rows"] += 1
                        continue
                    try:
                        validate_record(record, pipeline_config, config["data_mode"], student_config, require_usable=False)
                        audit = record["audit_only"]
                        index = source_index(audit, config)
                        require(audit.get("generation_config_sha256") == config_hash
                                and audit["versions"].get("generation_config") == config_hash, "record_generation_config_mismatch")
                        require(audit["versions"].get("observation_schema") == record["public_input"]["observation"]["schema"], "record_observation_schema_mismatch")
                        require(common_versions is None or audit["versions"] == common_versions, "record_versions_mismatch")
                        if verify_outcomes:
                            raw_kind = verify_raw_outcomes(record, shard, max_outcome_bytes)
                            raw_checks[raw_kind] += 1
                        if db.execute("INSERT OR IGNORE INTO records VALUES(?,1)", (index,)).rowcount == 0:
                            raise ReportError("duplicate_generation_source_index")
                        if common_versions is None:
                            common_versions = dict(audit["versions"])
                    except (ValueError, TypeError, KeyError, OSError, zlib.error, OverflowError) as exc:
                        counts["invalid_decision_rows"] += 1
                        problems.add("decision", shard / "decisions.jsonl", line, exc)
                        continue
                    counts["valid_decision_rows"] += 1
                    usable = has_usable_targets(record)
                    counts["usable_decision_rows" if usable else "diagnostic_only_decision_rows"] += 1
                    add_outcome_accounting(outcome_accounting["valid_records"], record)
                    if not usable:
                        add_outcome_accounting(outcome_accounting["diagnostic_only_records"], record)
                    costs = audit.get("costs", {})
                    for name in ("elapsed_seconds", "clone_seconds", "settlement_seconds", "rollout_decisions"):
                        if finite(costs.get(name)) and costs[name] >= 0:
                            timings["valid_record_" + name] += costs[name]
                            measured[name] += 1
                    if finite(costs.get("peak_worker_memory_bytes")):
                        measured["peak_worker_memory_bytes"] += 1
                        peak_rss = max(peak_rss or 0, costs["peak_worker_memory_bytes"])
                    for key, field in (("runs", "source_run_group"), ("battles", "source_combat_id"), ("branch_families", "branch_family")):
                        identity(db, "valid_records", key, audit[field])
                    digest = public_digest(record["public_input"])
                    if db.execute("INSERT OR IGNORE INTO roots VALUES(?)", (digest,)).rowcount == 0:
                        counts["duplicate_public_roots"] += 1
                    else:
                        counts["unique_valid_public_roots"] += 1
                        add_outcome_accounting(outcome_accounting["unique_valid_public_roots"], record)
                        tally(db, "unique_valid_roots", "posterior_profile", audit.get("posterior_profile") or "undeclared")
                    if not usable:
                        continue
                    if db.execute("INSERT OR IGNORE INTO usable_roots VALUES(?)", (digest,)).rowcount == 0:
                        counts["duplicate_usable_public_roots"] += 1
                        continue
                    counts["unique_effective_public_roots"] += 1
                    record_root_distribution(db, record, counts)
                    counts["objective_calibrated_roots"] += audit.get("objective_calibrated") is True
                    counts["objective_uncalibrated_or_undeclared_roots"] += audit.get("objective_calibrated") is not True
                    actions = record["targets"]["actions"]
                    complete = all(a["allocated_worlds"] > 0 and a["completed_worlds"] == a["allocated_worlds"] for a in actions)
                    full_utility = complete and all(a["masks"]["value"] for a in actions)
                    all_heads = complete and all(all(a["masks"].values()) for a in actions)
                    any_value = any(a["masks"]["value"] for a in actions)
                    pairs = record["targets"]["pairwise"]
                    counts["completed_whole_candidate_roots"] += complete
                    counts["full_utility_roots"] += full_utility
                    counts["all_head_complete_roots"] += all_heads
                    counts["not_full_utility_usable_roots"] += not full_utility
                    counts["auxiliary_only_no_value_roots"] += not any_value
                    counts["partial_value_roots"] += any_value and not full_utility
                    counts["strong_pair_roots"] += bool(pairs)
                    counts["strong_pair_labels"] += len(pairs)
                    counts["empirical_value_only_roots"] += any_value and not pairs
                    counts["equivalent_set_roots"] += bool(record["targets"]["equivalent_action_set"])
                    counts["root_candidates"] += len(actions)
                    worlds["unique_root_independent_eval_worlds"] += audit["n_independent_eval"]
                    worlds["unique_root_exploration_worlds"] += audit["n_exploration"]
                    for action in actions:
                        for field in COUNT_FIELDS:
                            worlds[field] += action[field]
                    for key, field in (("runs", "source_run_group"), ("battles", "source_combat_id"), ("branch_families", "branch_family")):
                        identity(db, "unique_effective_roots", key, audit[field])
                    tally(db, "unique_effective_roots", "source_kind", audit["source_kind"])
                    tally(db, "unique_effective_roots", "source_category", audit.get("source_category", "undeclared"))
                    tally(db, "unique_effective_roots", "ranking_evidence", audit.get("ranking_evidence", "undeclared"))
                    tally(db, "unique_effective_roots", "posterior_profile", audit.get("posterior_profile") or "undeclared")
                    for category, ids in observed_content(record["public_input"]).items():
                        for model_id in ids:
                            tally(db, "observed_public_content", category, model_id)
                db.commit()
            for shard in shards:
                config = configs[shard]
                for line, attempt in reader.rows(shard / "attempts.jsonl"):
                    counts["attempt_journal_rows"] += 1
                    if attempt is None:
                        counts["invalid_attempt_rows"] += 1
                        continue
                    try:
                        index = source_index({"generation_source_index": attempt.get("source_index")}, config)
                        status = attempt.get("status")
                        expected_label_hash = digest_bytes(generator_canonical(stable_config(config)).encode())
                        require(attempt.get("generation_version") in (None, config["version"])
                                and attempt.get("generation_config_sha256") in (None, expected_label_hash), "attempt_generation_config_mismatch")
                        require(status in ("accepted", "failed_attempt", "duplicate_public_input", "interrupted_attempt"), "attempt_status_unknown_or_incomplete")
                        elapsed, observed = attempt.get("elapsed_seconds"), attempt.get("elapsed_seconds_observed")
                        if elapsed is None:
                            require(attempt.get("recovered") is True and attempt.get("timing_status") in ("recovered_unknown", "recovered_partial"), "attempt_elapsed_missing_without_explicit_recovery")
                            if attempt["timing_status"] == "recovered_partial":
                                require(finite(observed) and observed >= 0, "attempt_partial_elapsed_invalid")
                        else:
                            require(finite(elapsed) and elapsed >= 0, "attempt_elapsed_invalid")
                        require(observed is None or finite(observed) and observed >= 0 and (elapsed is None or observed <= elapsed), "attempt_observed_elapsed_invalid")
                        battle = attempt.get("source_battle_index")
                        require(type(battle) is int and battle == index // config["roots_per_battle"], "attempt_battle_identity_invalid")
                        require(db.execute("INSERT OR IGNORE INTO attempts VALUES(?,?)", (index, status)).rowcount == 1, "duplicate_attempt_source_index")
                    except (ValueError, TypeError, KeyError, OverflowError) as exc:
                        counts["invalid_attempt_rows"] += 1
                        problems.add("attempt", shard / "attempts.jsonl", line, exc)
                        continue
                    counts["journal_" + status] += 1
                    counts["journal_recovered_attempts"] += attempt.get("recovered") is True
                    counts["accepted_duplicate_record_journals"] += status == "accepted" and attempt.get("duplicate_public_input") is True
                    if elapsed is None:
                        measured["missing_" + status + "_elapsed"] += 1
                        counts["journal_missing_elapsed_rows"] += 1
                        counts["journal_" + attempt["timing_status"] + "_rows"] += 1
                        if observed is not None:
                            timings["journal_partial_observed_seconds"] += observed
                    else:
                        timings["journal_" + status + "_seconds"] += elapsed
                        counts["journal_measured_elapsed_rows"] += 1
                    identity(db, "attempts", "battles", str(battle))
                    tally(db, "attempts", "source_category", attempt.get("source_category", "undeclared"))
                    if status in ("failed_attempt", "interrupted_attempt"):
                        reason = str(attempt.get("reason", "undeclared"))
                        kind, failure_key = ("process_interruption", "process_interrupted_without_durable_decision") if status == "interrupted_attempt" else classify_failure(reason)
                        counts["failed_" + kind] += 1
                        if failure_key not in failures and len(failures) >= 100:
                            failure_key = "other_failure_reasons"
                        failures[failure_key] += 1
                        requested = attempt.get("uncommitted_requested_action_worlds")
                        if type(requested) is int and requested >= 0:
                            population = "interrupted" if status == "interrupted_attempt" else "failed"
                            worlds[population + "_uncommitted_requested_action_worlds"] += requested
                            measured[population + "_requested_worlds"] += 1
                        # Failed attempts do not become completed worlds, losses or effective roots.
                db.commit()
            counts["valid_records_without_accepted_journal"] = db.execute("SELECT count(*) FROM records r LEFT JOIN attempts a ON r.source=a.source WHERE a.source IS NULL OR a.status!='accepted'").fetchone()[0]
            counts["accepted_journal_without_valid_record"] = db.execute("SELECT count(*) FROM attempts a LEFT JOIN records r ON a.source=r.source WHERE a.status='accepted' AND r.source IS NULL").fetchone()[0]
            counts["diagnostic_only_public_roots"] = db.execute("SELECT count(*) FROM roots r LEFT JOIN usable_roots u ON r.digest=u.digest WHERE u.digest IS NULL").fetchone()[0]
            identities = {}
            for population, kind, number in db.execute("SELECT population,kind,count(*) FROM identities GROUP BY population,kind"):
                identities.setdefault(population, {})[kind] = number
            tallies = {}
            for population, kind, key, number in db.execute("SELECT population,kind,id,n FROM tallies ORDER BY population,kind,id"):
                tallies.setdefault(population, {}).setdefault(kind, {})[key] = number
            temporary_index_bytes = (Path(temp) / "index.sqlite3").stat().st_size
        finally:
            db.close()
    reader.finish()
    for fingerprint in fingerprints:
        path = Path(fingerprint["path"])
        if digest_bytes(path.read_bytes()) != fingerprint["sha256"]:
            problems.add("snapshot", path, None, "generation_config_changed_during_read")
    validation_fingerprint = {
        "student_config_sha256": digest_bytes(generator_canonical(student_config).encode()),
        "validator_files": {name: digest_bytes((ROOT / "python/nosl" / name).read_bytes()) for name in ("schema.py", "data.py", "public_identity.py")},
        "m5_preparer_sha256": digest_bytes((ROOT / "tools/prepare_dataset.py").read_bytes()),
        "note": "Current reporting validators; stored generation fingerprints remain separately preserved. Older corpus configs may omit these fields."
    }
    valid_count = counts["valid_decision_rows"]
    complete_costs = lambda key: measured[key] == valid_count and valid_count > 0
    statuses = ("accepted", "failed_attempt", "duplicate_public_input", "interrupted_attempt")
    measured_attempt_seconds = sum(timings["journal_" + status + "_seconds"] for status in statuses)
    timing_complete = counts["journal_missing_elapsed_rows"] == 0 and counts["invalid_attempt_rows"] == 0 and counts["valid_records_without_accepted_journal"] == 0
    total_attempt_seconds = measured_attempt_seconds if timing_complete else None
    journal_timing = {}
    for status in statuses:
        journal_timing["journal_" + status + "_measured_seconds"] = timings["journal_" + status + "_seconds"]
        journal_timing["journal_" + status + "_seconds"] = timings["journal_" + status + "_seconds"] if not measured["missing_" + status + "_elapsed"] else None
    record_elapsed = timings["valid_record_elapsed_seconds"]
    all_snapshots_stable = all(item["stable_during_read"] for item in reader.files)
    for key in ("decision_rows", "invalid_decision_rows", "valid_decision_rows", "duplicate_public_roots",
                "usable_decision_rows", "diagnostic_only_decision_rows", "diagnostic_only_public_roots",
                "unique_effective_public_roots", "duplicate_usable_public_roots",
                "unique_valid_public_roots", "completed_whole_candidate_roots", "full_utility_roots",
                "objective_calibrated_roots", "objective_uncalibrated_or_undeclared_roots",
                "after_hp_change_roots", "after_hp_loss_roots", "after_hp_gain_roots", "hp_unchanged_from_start_roots",
                "pending_choice_roots", "turn_one_roots", "later_turn_roots", "later_turn_after_hp_loss_roots",
                "all_head_complete_roots", "not_full_utility_usable_roots", "auxiliary_only_no_value_roots",
                "partial_value_roots", "strong_pair_roots", "strong_pair_labels", "empirical_value_only_roots",
                "equivalent_set_roots", "root_candidates", "attempt_journal_rows", "invalid_attempt_rows",
                "journal_accepted", "journal_failed_attempt", "journal_duplicate_public_input", "journal_interrupted_attempt", "failed_timeout",
                "journal_recovered_attempts", "journal_missing_elapsed_rows", "journal_recovered_unknown_rows", "journal_recovered_partial_rows",
                "journal_measured_elapsed_rows", "accepted_duplicate_record_journals", "failed_process_interruption",
                "failed_memory_budget", "failed_other_failure", "failed_reset_terminal_boundary",
                "failed_reset_unsupported", "failed_reset_engine_error", "failed_reset_unclassified",
                "failed_source_phase_absent", "failed_source_budget_exhausted", "failed_source_snapshot_unclassified"):
        counts.setdefault(key, 0)
    for field in (*COUNT_FIELDS, "unique_root_independent_eval_worlds", "unique_root_exploration_worlds"):
        worlds.setdefault(field, 0)
    for population in outcome_accounting.values():
        for field in ("decision_records", "root_independent_eval_worlds", "root_exploration_worlds", *COUNT_FIELDS):
            population.setdefault(field, 0)
    report = {
        "schema_version": SCHEMA, "diagnostic_only": True,
        "public_identity_scheme": PUBLIC_IDENTITY_SCHEME,
        "stored_generation_identity_scheme": common.get("public_identity_scheme"),
        "identity_recomputed_from_public_data": True, "full_content_verified": False,
        "trained_strength_claimed": False, "generation_or_training_started": False,
        "data_mode": common.get("data_mode"), "shards": [str(path) for path in shards],
        "configuration": {"compatible": True, "stable_config_sha256": digest_bytes(canonical_json(common).encode()),
                          "record_generation_config_sha256": digest_bytes(generator_canonical(common).encode()),
                          "execution_partitions": [dict(shard_count=count, shard_id=part) for count, part in sorted(partition_ids)],
                          "all_partition_files_present": len(partition_ids) == next(iter(partition_ids))[0],
                          "versions": common_versions, "files": fingerprints},
        "counts": dict(sorted(counts.items())), "source_identities": identities,
        "validation_environment": validation_fingerprint,
        "source_distributions": {key: value for key, value in tallies.items() if key not in ("observed_public_content", "root_state_distribution")},
        "root_state_distribution": distribution_summary(tallies, counts),
        "observed_public_content": {kind: {"distinct_ids": len(tallies.get("observed_public_content", {}).get(kind, {})),
                                           "roots_by_id": tallies.get("observed_public_content", {}).get(kind, {})}
                                    for kind in ("cards", "card_upgrades", "potions", "relics", "enemies")},
        "worlds": dict(sorted(worlds.items())) | {
            "committed_population": "globally_deduplicated_usable_public_roots",
            "committed_unique_action_world_completion_fraction": worlds["completed_worlds"] / worlds["allocated_worlds"] if worlds["allocated_worlds"] else None,
            "failed_requested_action_worlds_total_if_complete": worlds["failed_uncommitted_requested_action_worlds"] if measured["failed_requested_worlds"] == counts["journal_failed_attempt"] else None,
            "interrupted_requested_action_worlds_total_if_complete": worlds["interrupted_uncommitted_requested_action_worlds"] if measured["interrupted_requested_worlds"] == counts["journal_interrupted_attempt"] else None,
        }, "saved_outcome_accounting": {key: dict(sorted(value.items())) for key, value in outcome_accounting.items()},
        "failures_by_reason": dict(sorted(failures.items())),
        "timing": dict(sorted(timings.items())) | journal_timing | {
            "journal_timing_complete": timing_complete,
            "journal_total_service_seconds": total_attempt_seconds,
            "journal_measured_service_seconds": measured_attempt_seconds,
            "journal_total_service_lower_bound_seconds": measured_attempt_seconds + timings["journal_partial_observed_seconds"],
            "unique_effective_roots_per_service_second": counts["unique_effective_public_roots"] / total_attempt_seconds if total_attempt_seconds else None,
            "completed_whole_candidate_roots_per_service_second": counts["completed_whole_candidate_roots"] / total_attempt_seconds if total_attempt_seconds else None,
            "clone_fraction_of_teacher_elapsed": timings["valid_record_clone_seconds"] / record_elapsed if record_elapsed and complete_costs("clone_seconds") and complete_costs("elapsed_seconds") else None,
            "settlement_fraction_of_teacher_elapsed": timings["valid_record_settlement_seconds"] / record_elapsed if record_elapsed and complete_costs("settlement_seconds") and complete_costs("elapsed_seconds") else None,
            "wall_clock_seconds": None,
        },
        "resources": {"peak_reported_worker_rss_bytes": peak_rss,
                      "missing_valid_record_costs": {key: valid_count - measured[key] for key in
                         ("elapsed_seconds", "clone_seconds", "settlement_seconds", "rollout_decisions", "peak_worker_memory_bytes")},
                      "failed_attempt_peak_rss": None,
                      "failed_attempts_missing_requested_world_counts": counts["journal_failed_attempt"] - measured["failed_requested_worlds"],
                      "interrupted_attempts_missing_requested_world_counts": counts["journal_interrupted_attempt"] - measured["interrupted_requested_worlds"],
                      "attempts_missing_elapsed_by_status": {status: measured["missing_" + status + "_elapsed"] for status in statuses},
                      "temporary_sqlite_index_bytes": temporary_index_bytes,
                      "max_jsonl_line_bytes": max_line_bytes, "max_raw_outcome_bytes": max_outcome_bytes,
                      "record_storage": "One JSONL record and optionally one bounded raw-outcome member; global indices use temporary SQLite with 4 MiB page cache"},
        "raw_outcomes": {"verification_requested": verify_outcomes, "verified_records_by_storage": dict(raw_checks),
                         "status": "CHECKED_WITH_FAILURES" if verify_outcomes and counts["invalid_decision_rows"] else "VERIFIED_FOR_VALID_ROWS" if verify_outcomes else "NOT_CHECKED"},
        "integrity": {"source_files_stable": all_snapshots_stable, "error_counts": dict(problems.counts), "examples": problems.examples,
                      "passed": not problems.counts and counts["valid_records_without_accepted_journal"] == 0 and counts["accepted_journal_without_valid_record"] == 0},
        "source_files": reader.files,
        "limitations": [
            "Compatible shards are globally deduplicated using the declared current public-identity scheme, recomputed from public data. Legacy stored hashes are not asserted valid under the new scheme. Invalid records never add effective roots or worlds.",
            "Structurally valid records without positive-weight usable targets remain diagnostic-only evidence: their journals, costs and raw accounting are checked, but they never add effective roots, phase distributions or observed-content coverage. Preparation still rejects them for training. Unique valid roots include them; unique effective roots select the first usable whole record independently, without pooling later labels.",
            "Saved outcome accounting includes conserved completed/truncated/error/other mass from all valid records and separately from diagnostic-only records. These record-level workload totals include duplicate attempts; unique-valid accounting uses the first structurally valid record. Neither truncation nor errors are converted into losses or usable points.",
            "Completed whole-candidate roots require conserved complete terminal accounting for every candidate; full utility requires every value mask, not objective calibration. Empirical values are not strong pairwise ordering evidence. Emitted strong pairs are counted, not statistically recertified by this report.",
            "Root world draws are distinct from correlated candidate action-world copies and from independent source battles. Seed independence remains a sampler contract, not a claim established by counting rows.",
            "Source counts are global distinct identities, never sums of per-shard counts. Constructed sources are not natural gameplay distribution.",
            "Observed content is public root card piles/multisets/choices, inventory and enemies. Presence does not prove mechanism, interaction, natural reachability or full-content coverage.",
            "Journal elapsed costs include reset/replay, failed attempts and successful calls; teacher clone/settlement fractions use teacher elapsed only. clone_seconds includes belief sampling and branch preparation, not just object cloning. Summed concurrent worker service time is not wall-clock time.",
            "Explicit recovered-unknown/partial attempt timings are valid missing data. Measured sums and checkpoint lower bounds are separate; total service time and throughput are null if any attempt time is missing. Accepted duplicate records preserve provenance while effective totals remain globally deduplicated.",
            "Failed requested worlds are uncommitted work, never terminal outcomes or game losses. A reset terminal boundary means the source combat already ended before the requested later decision, not an inferred defeat or engine bug. Explicit unsupported/error statuses and malformed reset reasons are separate. An unavailable predeclared public phase and a source decision-budget exhaustion are separate from engine errors and labeled game losses. Missing cost fields remain explicitly missing; failure-only RSS is unavailable.",
            "Validation uses the current strict public/target contract and is reported separately from generation-time config hashes. Raw verification checks member integrity and terminal accounting, not objective recalibration or client fidelity.",
        ],
    }
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=Path, help="One corpus or compatible shard directories; incompatible corpora must be separate calls")
    parser.add_argument("--verify-outcomes", action="store_true")
    parser.add_argument("--max-line-mib", type=int, default=64)
    parser.add_argument("--max-outcome-mib", type=int, default=64)
    args = parser.parse_args(argv)
    try:
        report = report_stage(args.paths, verify_outcomes=args.verify_outcomes,
                              max_line_bytes=args.max_line_mib * 1024 * 1024,
                              max_outcome_bytes=args.max_outcome_mib * 1024 * 1024)
        print(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False))
        return 0 if report["integrity"]["passed"] else 2
    except (ValueError, OSError, KeyError, TypeError) as exc:
        print(json.dumps({"schema_version": SCHEMA, "status": "REPORT_BLOCKED", "reason": str(exc)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
