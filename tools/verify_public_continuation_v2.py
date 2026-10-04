#!/usr/bin/env python3
"""One bounded, constructed v1/v2 teacher comparison. Never modifies a corpus or trains."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

V1 = "nosl-public-rules-v1"
V2 = "nosl-public-rules-v2"
DATASET = "nosl.teacher-data.public-rules-v2.v1"


def run(worker, destination):
    destination.mkdir(parents=True, exist_ok=False)
    with (destination / "worker.stderr.log").open("w") as error:
        process = subprocess.Popen(["dotnet", str(worker)], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=error, text=True)
        def call(request):
            process.stdin.write(json.dumps(request) + "\n")
            process.stdin.flush()
            line = process.stdout.readline()
            if not line:
                raise RuntimeError("Native worker ended before returning a result")
            result = json.loads(line)
            if result.get("status") in {"invalid_operation", "engine_error", "unsupported_capability", "posterior_budget_exhausted"}:
                raise RuntimeError(result)
            return result
        try:
            capabilities = call({"op": "continuation_policies"})
            assert capabilities["version"] == "nosl.continuation-policies.v1"
            assert {V1, V2} <= set(capabilities["supportedPolicyIds"])
            packet = call({"op": "reset", "scenario": {"seed": "public-rules-v2-paired-fixture",
                          "deck": ["Finesse", "Finesse", "BladeDance"], "enemyHp": 12}})
            for _ in range(2):
                packet = call({"op": "continue", "continuationPolicyId": V2})
            assert packet["observation"]["block"] == 8
            seeds = list(range(201, 217))
            reports = []
            records = []
            for policy in (V1, V2):
                result = call({"op": "teacher_record", "sourceRun": "constructed-public-rules-v2-fixture",
                               "sourceCombat": "mixed-finesse-bladedance", "branchFamily": "enough-block",
                               "options": {"mode": "T0", "continuationPolicyId": policy,
                                           "evaluationSeeds": seeds, "maxDecisions": 32}})
                assert call({"op": "observe"}) == packet, "Teacher changed its source"
                assert result["public_input"]["candidate_actions"] == packet["actions"]
                assert len(result["targets"]["actions"]) == len(packet["actions"])
                audit = result["audit_only"]
                assert audit["continuation_version"] == policy
                assert audit["sampler_seeds"] == seeds
                if policy == V2:
                    assert audit["dataset_version"] == audit["versions"]["dataset"] == DATASET
                else:
                    assert "dataset_version" not in audit and "dataset" not in audit["versions"]
                name = policy + ".jsonl"
                encoded = (json.dumps(result, separators=(",", ":"), ensure_ascii=False) + "\n").encode()
                (destination / name).write_bytes(encoded)
                candidates = []
                for action, target, samples in zip(packet["actions"], result["targets"]["actions"], audit["outcome_samples"], strict=True):
                    outcomes = samples["outcomes"]
                    assert len(outcomes) == len(seeds)
                    assert target["allocated_worlds"] == len(seeds)
                    kinds = {kind: sum(o["terminalKind"] == kind for o in outcomes)
                             for kind in sorted({o["terminalKind"] for o in outcomes})}
                    candidates.append({"action": action, "card": packet["observation"]["hand"][action["slot"]]["id"]
                                       if action["kind"] == "play" else None,
                                       "outcomes": kinds, "completed_worlds": target["completed_worlds"],
                                       "value": target["value"], "win_probability": target["win_probability"],
                                       "value_mask": target["masks"]["value"],
                                       "atomic_actions": sorted({o["atomicActionsExecuted"] for o in outcomes}),
                                       "settled_hp": sorted({o["hpAfterSettlement"] for o in outcomes
                                                             if o["hpAfterSettlement"] is not None})})
                reports.append({"continuation_version": policy, "dataset_version": audit.get("dataset_version"),
                                "file": name, "sha256": hashlib.sha256(encoded).hexdigest(),
                                "worlds_allocated": audit["costs"]["worlds_allocated"],
                                "worlds_completed": audit["costs"]["worlds_completed"], "candidates": candidates})
                records.append(result)
            assert records[0]["public_input"] == records[1]["public_input"]
            assert reports[0]["worlds_completed"] < reports[0]["worlds_allocated"]
            assert reports[1]["worlds_completed"] == reports[1]["worlds_allocated"]
            assert all(c["outcomes"] == {"Win": 16} for c in reports[1]["candidates"])
            from prepare_dataset import prepare, validate_record
            config = json.loads((Path(__file__).resolve().parents[1] / "configs/data_pipeline.v1.json").read_text())
            for record in records:
                validate_record(record, config, "engineering-smoke")
            _, mixed_report, _ = prepare(records, config, "engineering-smoke")
            assert mixed_report["filter_reasons"] == {"append_record_versions_mismatch": 1}
            report = {"schema_version": "nosl.public-rules-v2-fixture-report.v1",
                      "scope": "one constructed enough-block mixed root; no natural-frequency or general C02 claim",
                      "formal_labels": False, "trainable": False, "teacher": "T0", "max_decisions": 32,
                      "paired_final_world_seeds": seeds, "independent_root_worlds_per_policy": len(seeds),
                      "candidate_copies_per_policy": len(seeds) * len(packet["actions"]),
                      "block": 8, "published_incoming": 5,
                      "source_unchanged": True, "public_input_equal": True, "all_legal_candidates_retained": True,
                      "dataset_record_validation": "both_records_pass_engineering_smoke_validator",
                      "mixed_version_preparation": "append_record_versions_mismatch",
                      "preference_signal": "all_v2_candidate_values_tied_at_0; completion_evidence_only",
                      "policies": reports}
            (destination / "report.json").write_text(json.dumps(report, indent=2) + "\n")
            return report
        finally:
            process.stdin.close()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="New isolated directory; existing paths are rejected")
    args = parser.parse_args()
    report = run(args.worker.resolve(strict=True), args.output)
    print(json.dumps({"report": str(args.output / "report.json"),
                      "worlds": [{"policy": p["continuation_version"], "allocated": p["worlds_allocated"],
                                  "completed": p["worlds_completed"]} for p in report["policies"]]}))


if __name__ == "__main__":
    main()
