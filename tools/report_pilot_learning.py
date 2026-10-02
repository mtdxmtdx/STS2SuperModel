#!/usr/bin/env python3
"""Read-only, bounded train/validation diagnostics for an unpromoted pilot bundle."""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import sys
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))

import torch

from nosl.data import canonical_object_digest, prepared_paths, validate_record
from nosl.inference import Inference
from nosl.public_identity import public_input_digest
from nosl.reproducibility import runtime_identity, source_hashes
from nosl.schema import SchemaError, integer
from nosl.train import check_split_isolation, config_hash, evaluate

SCHEMA = "nosl.pilot-learning-report.v1"
AVAILABILITY = ("full_utility", "partial_utility", "auxiliary_only", "missing_utility_and_auxiliary")
RANKING_TOLERANCE = 1e-9


def require(condition, message):
    if not condition:
        raise SchemaError(message)


def file_hash(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_inputs(prepared, config, *, max_records=10000, max_loaded_bytes=512 * 1024**2,
                max_line_bytes=8 * 1024**2):
    """Reuse M5 integrity checks; parse ONLY train and validation, never test."""
    integer(max_records, "report max_records", 1, 10000)
    integer(max_loaded_bytes, "report max_loaded_bytes", 1, 1024**3)
    integer(max_line_bytes, "report max_line_bytes", 1, 32 * 1024**2)
    prepared = Path(prepared)
    datasets, hashes, files = {}, set(), []
    count, byte_count = 0, 0
    for split in ("train", "validation"):
        paths, manifest_hash = prepared_paths(prepared, split, canonical_object_digest(config))
        hashes.add(manifest_hash)
        records = []
        for path in paths:
            before = file_hash(path)
            files.append({"split": split, "path": str(path.relative_to(prepared.resolve())), "sha256": before})
            with path.open("rb") as stream:
                while raw := stream.readline(max_line_bytes + 1):
                    require(len(raw) <= max_line_bytes, "record line exceeds report byte cap; never truncated")
                    byte_count += len(raw)
                    require(byte_count <= max_loaded_bytes, "train/validation exceeds report byte cap; never truncated")
                    if not raw.strip():
                        continue
                    count += 1
                    require(count <= max_records, "train/validation exceeds report root cap; never truncated")
                    records.append(validate_record(json.loads(raw), config))
            require(file_hash(path) == before, "prepared shard changed during report read")
        require(bool(records), f"prepared {split} split is empty")
        datasets[split] = SimpleNamespace(records=records, sha256=manifest_hash + ":" + split)
    require(len(hashes) == 1, "prepared manifest changed during report read")
    manifest_hash = hashes.pop()
    require(file_hash(prepared / "manifest.json") == manifest_hash, "prepared manifest changed during report read")
    check_split_isolation(datasets["train"], datasets["validation"])
    return datasets, {"prepared_manifest_sha256": manifest_hash, "parsed_shards": files,
                      "parsed_records": count, "parsed_bytes": byte_count}


def value_rows(record):
    return [row for row in record["targets"]["actions"] if row["masks"]["value"]]


def train_constant(records):
    """Argmin of the training head's root-mean, weighted-candidate-mean MSE."""
    numerator = denominator = 0.0
    candidates = positive = roots = 0
    for record in records:
        rows = value_rows(record)
        candidates += len(rows)
        roots += bool(rows)
        for row in rows:
            coefficient = row.get("sample_weight", 1.0) / len(rows)
            positive += coefficient > 0
            numerator += coefficient * row["value"]
            denominator += coefficient
    require(denominator > 0, "train has no positive-weight utility support for a constant baseline")
    return {"value": numerator / denominator, "fit_split": "train", "roots": len(records),
            "roots_with_value": roots, "masked_value_candidates": candidates,
            "positive_weight_value_candidates": positive, "effective_coefficient_sum": denominator,
            "definition": "sum_r sum_i(w_ri*y_ri/n_r) / sum_r sum_i(w_ri/n_r); n_r counts value-masked candidates including zero weights",
            "semantics": "descriptive constant regression baseline, not a policy or battle-strength estimate"}


def support_row(record):
    public, audit = record["public_input"], record["audit_only"]
    legal = [index for index, allowed in enumerate(public["legal_mask"]) if allowed]
    rows = value_rows(record)
    if len(rows) == len(legal):
        availability = "full_utility"
    elif rows:
        availability = "partial_utility"
    elif any(any(row["masks"].values()) for row in record["targets"]["actions"]):
        availability = "auxiliary_only"
    else:
        availability = "missing_utility_and_auxiliary"
    return {"public_input_sha256": public_input_digest(public),
            "source_run_group": audit["source_run_group"], "source_battle": audit["source_combat_id"],
            "branch_family": audit["branch_family"],
            "source_category": audit.get("source_category") or "undeclared",
            "declared_phase": audit.get("generation_source_phase") or "undeclared",
            "actual_turn": str(public["observation"]["turn"]),
            "posterior_profile": audit.get("posterior_profile") or "undeclared",
            "utility_availability": availability, "legal_candidate_indices": legal,
            "value_candidate_weights": [[row["action_index"], row.get("sample_weight", 1.0)] for row in rows],
            "targets_sha256": canonical_object_digest(record["targets"]),
            "empirical_ranking_eligible": len(legal) >= 2 and len(rows) == len(legal)}


def summary(records, supports, predictions=None, constant=None):
    counts = Counter({key + "_roots": 0 for key in AVAILABILITY})
    learned, baseline, weighted_learned, weighted_baseline = [], [], [], []
    regrets, agreements, ranks = [], [], []
    spreads, nonzero_spread_regrets, nonzero_spread_agreements = [], [], []
    candidate_counts, best_mean_counts = Counter(), Counter()
    for index, (record, support) in enumerate(zip(records, supports)):
        rows = value_rows(record)
        counts[support["utility_availability"] + "_roots"] += 1
        counts["legal_candidates"] += len(support["legal_candidate_indices"])
        counts["value_candidates"] += len(rows)
        counts["zero_weight_value_candidates"] += sum(row.get("sample_weight", 1.0) == 0 for row in rows)
        counts["missing_value_candidates"] += len(support["legal_candidate_indices"]) - len(rows)
        counts["no_value_roots"] += not rows
        counts["positive_weight_pairwise_labels"] += sum(pair["weight"] > 0 for pair in record["targets"]["pairwise"])
        counts["roots_with_positive_weight_pairs"] += any(pair["weight"] > 0 for pair in record["targets"]["pairwise"])
        counts["single_action_roots_skipped"] += len(support["legal_candidate_indices"]) < 2
        # Count incomplete roots independently of single-action exclusions.
        counts["incomplete_value_roots"] += support["utility_availability"] != "full_utility"
        counts["incomplete_value_roots_skipped"] += len(support["legal_candidate_indices"]) >= 2 and not support["empirical_ranking_eligible"]
        if support["empirical_ranking_eligible"]:
            means = {row["action_index"]: row["value"] for row in rows}
            best = max(means.values())
            spread = best - min(means.values())
            spreads.append(spread)
            candidate_counts[len(means)] += 1
            best_mean_counts[sum(abs(best - value) <= RANKING_TOLERANCE for value in means.values())] += 1
        if predictions is None:
            continue
        prediction = predictions[index]
        scores = {row["action_index"]: row["score"] for row in prediction["predictions"] if row["legal"]}
        for row in rows:
            error = scores[row["action_index"]] - row["value"]
            coefficient = row.get("sample_weight", 1.0) / len(rows)
            learned.append(error)
            baseline.append(constant - row["value"])
            weighted_learned.append((error, coefficient))
            weighted_baseline.append((constant - row["value"], coefficient))
        if support["empirical_ranking_eligible"]:
            selected = means[prediction["selected_index"]]
            regret = max(0.0, best - selected)
            agreement = float(abs(best - selected) <= RANKING_TOLERANCE)
            regrets.append(regret)
            agreements.append(agreement)
            ranks.append(1 + sum(value > selected + RANKING_TOLERANCE for value in means.values()))
            if spread > RANKING_TOLERANCE:
                nonzero_spread_regrets.append(regret)
                nonzero_spread_agreements.append(agreement)
    result = {"roots": len(records), "distinct_source_battles": len({s["source_battle"] for s in supports}),
              "distinct_source_run_groups": len({s["source_run_group"] for s in supports}),
              "distinct_branch_families": len({s["branch_family"] for s in supports}), **dict(counts)}
    result["empirical_ranking_label_support"] = {
        "eligible_roots": len(spreads),
        "all_equal_mean_roots": sum(spread <= RANKING_TOLERANCE for spread in spreads),
        "nonzero_spread_roots": sum(spread > RANKING_TOLERANCE for spread in spreads),
        "roots_with_best_mean_ties": sum(count for best_count, count in best_mean_counts.items() if best_count > 1),
        "utility_spread": {"min": min(spreads) if spreads else None,
                           "mean": metric(spreads)["value"], "max": max(spreads) if spreads else None,
                           "count": len(spreads)},
        "legal_candidate_count_distribution": {str(size): count for size, count in sorted(candidate_counts.items())},
        "best_mean_candidate_count_distribution": {str(size): count for size, count in sorted(best_mean_counts.items())}}
    if predictions is not None:
        def regression(errors, weighted):
            weight = sum(w for _, w in weighted)
            return {"value_mae": metric([abs(x) for x in errors]), "value_mse": metric([x*x for x in errors]),
                    "normalized_root_weighted_value_mae": sum(abs(x)*w for x, w in weighted)/weight if weight else None,
                    "normalized_root_weighted_value_mse": sum(x*x*w for x, w in weighted)/weight if weight else None,
                    "root_mean_weighted_value_mse": sum(x*x*w for x, w in weighted)/len(records) if records else None,
                    "effective_coefficient_sum": weight}
        result.update(learned=regression(learned, weighted_learned),
                      train_constant=regression(baseline, weighted_baseline),
                      empirical_teacher_mean_regret=metric(regrets),
                      empirical_teacher_best_action_agreement=metric(agreements),
                      selected_action_empirical_mean_rank=metric(ranks),
                      nonzero_spread_empirical_teacher_mean_regret=metric(nonzero_spread_regrets),
                      nonzero_spread_empirical_teacher_best_action_agreement=metric(nonzero_spread_agreements))
    return result


def metric(values):
    return {"value": sum(values) / len(values) if values else None, "count": len(values)}


def stratified(records, supports, predictions=None, constant=None):
    result = {"overall": summary(records, supports, predictions, constant), "strata": {}}
    for key in ("source_category", "declared_phase", "actual_turn", "posterior_profile", "source_battle",
                "source_category_and_phase", "source_category_and_observed_enemy_ids"):
        groups = defaultdict(list)
        for index, support in enumerate(supports):
            # Joint support prevents common starter phases from hiding sparse
            # later-turn nonstarter failures in either marginal histogram.
            if key == "source_category_and_observed_enemy_ids":
                # Current public composition, preserving multiplicity; not the initial encounter.
                enemy_ids = sorted(enemy["id"] for enemy in records[index]["public_input"]["observation"]["enemies"])
                name = json.dumps([support["source_category"], enemy_ids], separators=(",", ":"))
            elif key == "source_category_and_phase":
                name = json.dumps([support["source_category"], support["declared_phase"]], separators=(",", ":"))
            else:
                name = support[key]
            groups[name].append(index)
        result["strata"][key] = {name: summary([records[i] for i in indices], [supports[i] for i in indices],
                                               [predictions[i] for i in indices] if predictions is not None else None, constant)
                                 for name, indices in sorted(groups.items())}
    return result


def compare_metrics(before, stored_final, final):
    """Fail closed rather than comparing different populations or evaluator code."""
    require(isinstance(before, dict) and isinstance(stored_final, dict), "bundle lacks before-fit/final validation metrics")
    result = {}
    for key, current in final.items():
        if not isinstance(current, dict) or set(current) != {"value", "count"}:
            continue
        initial, stored = before.get(key), stored_final.get(key)
        require(isinstance(initial, dict) and isinstance(stored, dict), f"missing same-support metric {key}")
        require(initial.get("count") == stored.get("count") == current["count"], f"validation support mismatch for {key}")
        a, b = stored.get("value"), current["value"]
        require(a is None and b is None or (type(a) in (int, float) and type(b) in (int, float)
                and math.isclose(a, b, rel_tol=1e-5, abs_tol=1e-6)), f"recomputed final metric disagrees with bundle: {key}")
        initial_value = initial.get("value")
        require((initial_value is None) == (current["count"] == 0), f"invalid before-fit support for {key}")
        require(initial_value is None or type(initial_value) in (int, float) and math.isfinite(initial_value), f"invalid before-fit value for {key}")
        result[key] = {"before_fit": initial_value, "final": b, "count": current["count"],
                       "final_minus_before_fit": b - initial_value if b is not None else None}
    for key in ("evaluation_roots", "unresolved_actions"):
        require(before.get(key) == stored_final.get(key) == final.get(key), f"validation population mismatch: {key}")
    require(before.get("empirical_teacher_ranking_semantics") == stored_final.get("empirical_teacher_ranking_semantics")
            == final.get("empirical_teacher_ranking_semantics"), "validation ranking population/semantics mismatch")
    return result


def verify_frozen(datasets, runner):
    manifest, config = runner.manifest, runner.config
    require(manifest.get("status") == "EXPERIMENTAL_UNPROMOTED" and manifest.get("trained") is True
            and manifest.get("pilot_trial") is True and manifest.get("formal_training_run") is False
            and manifest.get("test_evaluated") is False, "expected a completed experimental pilot with untouched test outcomes")
    frozen = manifest.get("frozen_inputs", {})
    require(frozen.get("config_sha256") == config_hash(config), "frozen config checksum mismatch")
    require(frozen.get("test_used_for_model_selection") is False, "test selection prohibition missing")
    require(frozen.get("implementation", {}).get("source_sha256") == source_hashes(), "training/evaluation implementation changed since fit")
    for split, data in datasets.items():
        require(frozen.get("splits", {}).get(split) == {"sha256": data.sha256, "roots": len(data.records)}, f"bundle is not bound to this prepared {split} support")
        for record in data.records:
            audit = record["audit_only"]
            require(audit.get("engineering_smoke") is not True, "engineering smoke records cannot stand in for a pilot")
            require(audit.get("versions") == frozen.get("stable_versions") and isinstance(audit.get("versions"), dict), "record stable provenance differs from bundle")
            require(record["public_input"]["observation"]["schema"] == frozen.get("observation_schema"), "record observation schema differs from bundle")
            for key in ("simulator_commit", "rules_version", "label_endpoint", "teacher_version", "objective_version"):
                require(isinstance(audit.get(key), str) and audit[key] == frozen.get("versions", {}).get(key), "record label provenance mismatch: " + key)
            require(isinstance(audit.get("continuation_version"), str) and
                    audit["continuation_version"].split(":", 1)[0] == frozen.get("versions", {}).get("continuation_version"), "record continuation provenance mismatch")


def build_report(prepared, bundle, *, max_records=10000, max_loaded_bytes=512 * 1024**2,
                 max_line_bytes=8 * 1024**2):
    torch.set_num_threads(1)
    if torch.get_num_interop_threads() != 1:
        torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True)
    bundle = Path(bundle)
    bundle_manifest_hash = file_hash(bundle / "manifest.json")
    config_file_hash = file_hash(bundle / "config.json")
    runner = Inference.from_bundle(bundle, allow_experimental=True)
    datasets, audit = load_inputs(prepared, runner.config, max_records=max_records,
                                 max_loaded_bytes=max_loaded_bytes, max_line_bytes=max_line_bytes)
    verify_frozen(datasets, runner)
    constant = train_constant(datasets["train"].records)
    supports = {split: [support_row(record) for record in data.records] for split, data in datasets.items()}
    predictions = []
    for record in datasets["validation"].records:
        prediction = runner.predict(record["public_input"])
        require(prediction.get("status") == "EXPERIMENTAL_UNCALIBRATED", "pilot inference rejected validation input: " + str(prediction.get("status")))
        predictions.append(prediction)
    final = evaluate(runner.model, datasets["validation"])
    comparison = compare_metrics(runner.manifest.get("validation_before_fit"), runner.manifest.get("validation"), final)
    # Rerun shared integrity checks after inference: reject concurrent mutation.
    require(prepared_paths(Path(prepared), "train", canonical_object_digest(runner.config))[1]
            == audit["prepared_manifest_sha256"], "prepared inputs changed while reporting")
    require(file_hash(bundle / "manifest.json") == bundle_manifest_hash and
            file_hash(bundle / "config.json") == config_file_hash and
            file_hash(bundle / "weights.pt") == runner.manifest["weights_sha256"], "bundle changed while reporting")
    validation = stratified(datasets["validation"].records, supports["validation"], predictions, constant["value"])
    for key in ("value_mae", "value_mse"):
        require(math.isclose(validation["overall"]["learned"][key]["value"], final[key]["value"], rel_tol=1e-5, abs_tol=1e-6)
                if final[key]["value"] is not None else validation["overall"]["learned"][key]["value"] is None, "inference/evaluation utility mismatch")
    return {"schema": SCHEMA, "status": "DESCRIPTIVE_SMALL_PILOT_ONLY",
            "audit": {**audit, "bundle_manifest_sha256": bundle_manifest_hash,
                      "config_sha256": runner.manifest["config_sha256"], "config_file_sha256": config_file_hash,
                      "weights_sha256": runner.manifest["weights_sha256"], "reporter_sha256": file_hash(__file__),
                      "implementation": runner.manifest["frozen_inputs"]["implementation"], "report_runtime": runtime_identity(),
                      "pilot_budget": runner.manifest.get("budget"), "optimizer_steps_in_bundle": runner.manifest.get("optimizer_steps"),
                      "support_sha256": {split: canonical_object_digest(rows) for split, rows in supports.items()}},
            "limits": {"torch_intraop_threads": 1, "torch_interop_threads": 1, "device": "cpu", "max_records_train_plus_validation": max_records,
                       "max_loaded_bytes": max_loaded_bytes, "max_line_bytes": max_line_bytes, "truncation": False,
                       "optimizer_steps_performed": 0, "test_target_records_parsed": 0,
                       "test_bytes_integrity_hashed_by_shared_loader": True},
            "train_only_constant": constant, "before_fit_vs_final_same_validation_support": comparison,
            "train_support": stratified(datasets["train"].records, supports["train"]), "validation": validation,
            "exact_support": supports,
            "ranking_semantics": {**final["empirical_teacher_ranking_semantics"],
                                  "selection": "actual inference selected_index from highest predicted legal utility",
                                  "rank": "1 + number of legal empirical teacher means exceeding the selected mean by >1e-9; ties share rank",
                                  "label_support": "complete legal sets with at least two candidates; spread = maximum minus minimum empirical teacher mean; all-equal means spread <=1e-9, nonzero spread >1e-9; best-mean candidates are within 1e-9 of maximum"},
            "warnings": ["Teacher continuation targets are finite small-N estimates; mean regret/agreement/rank are empirical and not certified labels.",
                         "All-equal empirical means yield agreement for every legal choice; nonzero-spread metrics expose action-dependent empirical support but do not establish learning or true action equivalence.",
                         "Missing utility excludes the entire root from ranking metrics and stays visible in every applicable stratum.",
                         "Source-battle IDs count distinct provenance groups, not independent roots; groups can share source runs. No confidence intervals are claimed.",
                         "Regression reports raw utility units (MSE squared); unweighted candidate metrics match stored validation, weighted metrics expose the training head support.",
                         "No rollout win rate, battle strength, calibration, formal readiness, or model promotion is established."]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", required=True)
    parser.add_argument("--bundle", required=True)
    parser.add_argument("--max-records", type=int, default=10000)
    parser.add_argument("--max-loaded-mib", type=int, default=512)
    parser.add_argument("--max-line-mib", type=int, default=8)
    args = parser.parse_args()
    try:
        report = build_report(args.prepared, args.bundle, max_records=args.max_records,
                              max_loaded_bytes=args.max_loaded_mib * 1024**2, max_line_bytes=args.max_line_mib * 1024**2)
    except (ValueError, OSError, TypeError, KeyError, RuntimeError) as error:
        parser.exit(2, f"pilot learning report rejected: {error}\n")
    print(json.dumps(report, indent=2, allow_nan=False))


if __name__ == "__main__":
    main()
