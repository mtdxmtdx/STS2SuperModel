#!/usr/bin/env python3
"""Bounded offline constructed-source policy evaluation; never trains/promotes.

Freeze first. A learned run requires an experimental bundle tied to the frozen
prepared corpus. Source recipes/seeds remain orchestration audit only. The
student boundary receives solely a fresh, whitelisted public_input.
"""
from __future__ import annotations

import argparse
from collections import Counter
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
VERSION = "nosl-offline-pilot-policy-evaluation-v1"
BASELINE = "nosl-public-rules-v1"
ENDPOINT = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION"
ACTIVE = ("player_decision", "card_choice")


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def file_hash(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""): h.update(chunk)
    return h.hexdigest()


def save(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8") as handle:
        handle.write(canonical(value) + "\n"); handle.flush(); os.fsync(handle.fileno())
    os.replace(temporary, path)


def runtime_identity(repo):
    directory = repo / "src/Nosl.Worker/bin/Release/net9.0"
    if not (directory / "Nosl.Worker.dll").is_file(): raise ValueError("built worker required; this tool never rebuilds")
    return {p.name: file_hash(p) for p in sorted(directory.iterdir()) if p.suffix in (".dll", ".json")}


def declared_cases(smoke_only=False):
    # Fixed before any outcome is observed. No seed/outcome screening or retries.
    cases = [
        ("starter", "starter", {"enemy": "TwigSlimeS", "hp": 70, "enemyHp": 30}),
        ("low-hp-basic", "low_hp", {"enemy": "LeafSlimeS", "hp": 8, "enemyHp": 24,
            "deck": ["StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent", "Neutralize", "Survivor"]}),
        ("poison-guard", "combination", {"enemy": "Nibbit", "hp": 24, "enemyHp": 45,
            "deck": ["DeadlyPoison", "Backflip", "LegSweep", "DefendSilent", "StrikeSilent", "Survivor"], "potions": ["FirePotion"]}),
        ("discard-draw", "combination", {"enemy": "TwigSlimeM", "hp": 18, "enemyHp": 42,
            "deck": ["Prepared+", "DaggerThrow", "Acrobatics", "Reflex", "Tactician", "DefendSilent", "StrikeSilent"]}),
        ("shivs-relic", "combination", {"enemy": "Nibbit", "hp": 20, "enemyHp": 55,
            "deck": ["BladeDance", "CloakAndDagger", "Accuracy", "DefendSilent", "StrikeSilent"], "relics": ["Kunai"]}),
        ("low-hp-poison", "low_hp_hard", {"enemy": "TwigSlimeM", "hp": 12, "enemyHp": 60,
            "deck": ["NoxiousFumes", "DeadlyPoison", "Backflip", "Footwork", "DefendSilent", "DefendSilent"], "potions": ["SwiftPotion"]}),
        ("multi-enemy-draw", "combination_hard", {"enemies": ["TwigSlimeS", "LeafSlimeS"], "hp": 35,
            "deck": ["DaggerSpray+", "Dash", "Backflip", "DefendSilent", "Neutralize", "StrikeSilent"]}),
        ("block-carry", "combination_hard", {"enemy": "TwigSlimeM", "hp": 28, "enemyHp": 65,
            "deck": ["Footwork", "Blur", "DodgeAndRoll", "Backflip", "StrikeSilent", "DefendSilent"], "relics": ["MeatOnTheBone"]}),
    ]
    return cases[:1] if smoke_only else cases


def setup_signature(scenario):
    # Initial composition only; do not pretend this proves an interaction never
    # appeared through generation/draw later in a training rollout.
    return digest({key: sorted(scenario.get(key) or []) for key in ("deck", "potions", "relics")})


def validate_limits(limits):
    bounds = {"max_decisions": (1, 1024), "max_battle_seconds": (1, 120), "max_job_seconds": (1, 3600),
              "max_rss_mib": (128, 2048), "max_trace_mib": (1, 1024)}
    if set(limits) != set(bounds) or any(type(limits[k]) is not int or not lo <= limits[k] <= hi for k, (lo, hi) in bounds.items()):
        raise ValueError("invalid finite evaluation limits")


def freeze(repo, output, *, prepared=None, seeds_per_case=2, seed_prefix="NOSL-OFFLINE-HOLDOUT-v1", smoke_only=False,
           limits=None):
    if not 1 <= seeds_per_case <= 4: raise ValueError("seeds_per_case must be in [1,4]")
    limits = limits or {"max_decisions": 256, "max_battle_seconds": 30, "max_job_seconds": 600, "max_rss_mib": 1024, "max_trace_mib": 256}
    validate_limits(limits)
    if prepared is None and not smoke_only: raise ValueError("full held-out plan requires a frozen prepared corpus")
    corpus_seeds, corpus_setups, prepared_hash = set(), set(), None
    if prepared is not None:
        sys.path.insert(0, str(repo / "python"))
        from nosl.data import prepared_paths
        for split in ("train", "validation", "test"):
            paths, observed_hash = prepared_paths(Path(prepared), split)
            if prepared_hash is not None and prepared_hash != observed_hash: raise ValueError("prepared manifest changed during freeze")
            prepared_hash = observed_hash
            for path in paths:
                with path.open(encoding="utf-8") as stream:
                    for line in stream:
                        if not line.strip(): continue
                        audit = json.loads(line)["audit_only"]
                        source = audit.get("scenario_recipe")
                        if not isinstance(source, dict) or not isinstance(source.get("seed"), str):
                            raise ValueError("source scenario audit missing; cannot verify held-out seeds/setups")
                        corpus_seeds.add(source["seed"]); corpus_setups.add(setup_signature(source))
    rows = []
    for name, category, recipe in declared_cases(smoke_only):
        for index in range(seeds_per_case):
            seed = f"{seed_prefix}:{name}:{index}"
            if seed in corpus_seeds: raise ValueError("declared evaluation seed occurs in the frozen corpus")
            rows.append({"source_battle_id": f"{name}:{index}", "category": category, "scenario": {**recipe, "seed": seed},
                         "initial_composition_unseen_in_frozen_corpus": bool(prepared_hash and setup_signature(recipe) not in corpus_setups)})
    if prepared_hash and not smoke_only and not any(r["initial_composition_unseen_in_frozen_corpus"] and "combination" in r["category"] for r in rows):
        raise ValueError("declared combination cases are not novel initial setups; revise plan before observing outcomes")
    if prepared is not None and prepared_paths(Path(prepared), "train")[1] != prepared_hash:
        raise ValueError("prepared corpus changed during holdout freeze")
    plan = {"schema": VERSION, "frozen_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(), "source_kind": "constructed",
            "natural_distribution_claimed": False, "smoke_only": smoke_only, "baseline_policy_id": BASELINE,
            "prepared_manifest_sha256": prepared_hash, "seed_holdout_checked": prepared_hash is not None,
            "corpus_source_seeds_checked": len(corpus_seeds), "initial_composition_novelty_scope": "initial deck/potion/relic multisets only; not all encountered interactions",
            "seed_policy": "one fixed declared seed per source battle; no favorable-outcome search/replacement", "scenarios": rows,
            "source_battle_count": len(rows), "limits": limits, "runtime_files": runtime_identity(repo),
            "evaluator_sha256": file_hash(Path(__file__)), "formal_training": False, "promotion": False}
    output.mkdir(parents=True, exist_ok=False)
    save(output / "plan.json", plan)
    (output / "plan.sha256").write_text(digest(plan) + "\n")
    (output / "evaluator-source.py").write_bytes(Path(__file__).read_bytes())
    return plan


def load_plan(path, repo):
    plan = json.loads((path / "plan.json").read_text())
    if plan.get("schema") != VERSION or digest(plan) != (path / "plan.sha256").read_text().strip(): raise ValueError("frozen plan checksum mismatch")
    if plan["runtime_files"] != runtime_identity(repo): raise ValueError("worker runtime changed since freeze")
    if plan["evaluator_sha256"] != file_hash(Path(__file__)): raise ValueError("evaluator changed since freeze")
    if plan["evaluator_sha256"] != file_hash(path / "evaluator-source.py"): raise ValueError("frozen evaluator source changed")
    validate_limits(plan["limits"])
    if plan.get("source_kind") != "constructed" or plan.get("natural_distribution_claimed") is not False:
        raise ValueError("only declared constructed evaluation is supported")
    if plan["source_battle_count"] != len(plan["scenarios"]) or not 1 <= len(plan["scenarios"]) <= 32: raise ValueError("source count mismatch")
    ids = [row["source_battle_id"] for row in plan["scenarios"]]
    if len(set(ids)) != len(ids) or not all(isinstance(value, str) and re.fullmatch(r"[a-z0-9-]+:[0-9]+", value) for value in ids):
        raise ValueError("unsafe or repeated source battle id")
    if len({row["scenario"]["seed"] for row in plan["scenarios"]}) != len(plan["scenarios"]): raise ValueError("repeated source seed")
    return plan


class BudgetExceeded(RuntimeError): pass
class PolicyFailure(RuntimeError): pass


def rss_mib(pid):
    try:
        text = Path(f"/proc/{pid}/status").read_text()
        match = re.search(r"^VmRSS:\s+(\d+) kB", text, re.MULTILINE)
        return int(match.group(1)) / 1024 if match else 0.
    except FileNotFoundError:
        return 0.


class Guard:
    def __init__(self, limits):
        self.limits, self.started, self.battle_started, self.worker_pid = limits, time.monotonic(), time.monotonic(), None
        self.peak_rss_mib, self.trace_bytes = 0., 0

    def check(self):
        now = time.monotonic()
        if now - self.started >= self.limits["max_job_seconds"]: raise BudgetExceeded("job_wall_time_limit")
        if now - self.battle_started >= self.limits["max_battle_seconds"]: raise BudgetExceeded("battle_wall_time_limit")
        current = rss_mib(os.getpid()) + (rss_mib(self.worker_pid) if self.worker_pid else 0)
        self.peak_rss_mib = max(self.peak_rss_mib, current)
        if current > self.limits["max_rss_mib"]: raise BudgetExceeded("combined_driver_worker_rss_limit")
        if self.trace_bytes > self.limits["max_trace_mib"] * 1024 ** 2: raise BudgetExceeded("trace_size_limit")

    def remaining(self):
        self.check()
        return max(.001, min(self.limits["max_job_seconds"] - (time.monotonic() - self.started),
                             self.limits["max_battle_seconds"] - (time.monotonic() - self.battle_started)))


class Worker:
    """One sequential native worker; bounded nonblocking JSONL read, no build."""
    def __init__(self, repo, log, dotnet, guard):
        self.guard, self.buffer = guard, bytearray()
        self.log = log.open("ab")
        env = {**os.environ, "DOTNET_PROCESSOR_COUNT": "1", "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1"}
        self.process = subprocess.Popen([dotnet, str(repo / "src/Nosl.Worker/bin/Release/net9.0/Nosl.Worker.dll")],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log, cwd=repo, env=env, start_new_session=True)
        self.guard.worker_pid = self.process.pid
        os.set_blocking(self.process.stdout.fileno(), False)

    def request(self, command):
        self.guard.check()
        self.process.stdin.write((canonical(command) + "\n").encode()); self.process.stdin.flush()
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            while b"\n" not in self.buffer:
                timeout = min(.1, self.guard.remaining())
                if selector.select(timeout):
                    chunk = os.read(self.process.stdout.fileno(), 65536)
                    if not chunk: raise RuntimeError("worker_exited_without_response")
                    self.buffer.extend(chunk)
                    if len(self.buffer) > 64 * 1024 ** 2: raise BudgetExceeded("worker_response_size_limit")
        line, _, remaining = self.buffer.partition(b"\n"); self.buffer = bytearray(remaining)
        self.guard.check()
        return json.loads(line)

    def close(self):
        if self.process.poll() is None:
            self.process.terminate()
            try: self.process.wait(timeout=2)
            except subprocess.TimeoutExpired: self.process.kill(); self.process.wait(timeout=2)
        self.guard.worker_pid = None
        self.log.close()


def public_input(packet):
    if set(packet) != {"status", "observation", "actions"} or packet["status"] not in ACTIVE:
        raise ValueError("worker did not return an exact active DecisionPacket")
    return {"schema_version": "nosl.student.public.v1", "observation": packet["observation"],
            "history_complete": True, "controller_context": {"status": "inactive"},
            "candidate_actions": packet["actions"], "legal_mask": [True] * len(packet["actions"])}


def recover_action(before, after, terminal=None):
    history = before["observation"]["history"]
    events = terminal["events"] if terminal else (after.get("observation") or {}).get("history", [])
    new_actions = [json.loads(e["detail"]) for e in events[len(history):] if e["kind"] == "action"]
    if len(new_actions) != 1: raise RuntimeError("baseline_public_action_trace_missing_or_ambiguous")
    if new_actions[0] not in before["actions"]: raise RuntimeError("baseline_selected_non_candidate")
    return new_actions[0]


def rollout(worker, source, policy, guard, trace_path, student=None):
    guard.battle_started = time.monotonic()
    decisions, initial, terminal = 0, None, None
    result = {"source_battle_id": source["source_battle_id"], "category": source["category"], "policy": policy,
              "status": "ENGINE_ERROR", "terminal": None, "decisions": 0, "selected_action_trace_complete": True}
    def trace(value):
        encoded = (canonical(value) + "\n").encode()
        guard.trace_bytes += len(encoded); guard.check()
        with trace_path.open("ab") as stream: stream.write(encoded); stream.flush(); os.fsync(stream.fileno())
    try:
        packet = worker.request({"op": "reset", "scenario": source["scenario"]})
        trace({"kind": "initial_public_packet", "packet": packet})
        if packet.get("status") not in ACTIVE: raise RuntimeError("reset_not_active:" + str(packet.get("status")) + ":" + str(packet.get("message", "")))
        initial = packet["observation"]
        while packet.get("status") in ACTIVE:
            guard.check()
            if decisions >= guard.limits["max_decisions"]: raise BudgetExceeded("decision_limit")
            before = packet
            selected = None
            if policy == "student":
                # Absolutely no scenario/seed/audit/worker handle crosses this call.
                prediction = student.predict(public_input(before))
                if prediction.get("status") != "EXPERIMENTAL_UNCALIBRATED":
                    result["status"] = "POLICY_REJECTED"; result["reason"] = prediction.get("status", "missing_status")
                    trace({"kind": "policy_rejection", "packet": before, "status": prediction.get("status"), "reason": prediction.get("reason")})
                    break
                selected = prediction.get("selected_action")
                if selected not in before["actions"]: raise PolicyFailure("student_selected_non_candidate")
                trace({"kind": "public_decision", "packet": before, "selected_action": selected})
                packet = worker.request({"op": "step", "action": selected})
            else:
                trace({"kind": "public_decision_pending", "packet": before, "selection_source": BASELINE})
                packet = worker.request({"op": "continue"})
            trace({"kind": "public_response", "packet": packet})
            decisions += 1
            if packet.get("status") in ("terminal_settled", "terminal_pending_settlement"):
                terminal = worker.request({"op": "settle"})
            if policy == "baseline":
                try: selected = recover_action(before, packet, terminal)
                except RuntimeError:
                    result["selected_action_trace_complete"] = False
                    raise
                trace({"kind": "public_action_selected", "selected_action": selected})
            if terminal is not None: break
            if packet.get("status") not in ACTIVE: raise RuntimeError("worker_failure:" + str(packet.get("status")))
        if terminal is not None:
            if (terminal.get("result") not in ("win", "loss") or terminal.get("boundary") != ENDPOINT
                    or terminal.get("rewardSelectionsMade") != 0): raise RuntimeError("invalid_terminal_settlement_boundary")
            for field in ("startHp", "finalHp", "startMaxHp", "finalMaxHp"):
                if type(terminal.get(field)) is not int or terminal[field] < 0: raise RuntimeError("invalid_terminal_hp")
            if terminal["startHp"] != initial["startHp"]: raise RuntimeError("terminal_start_hp_changed")
            trace({"kind": "actual_settled_terminal", "facts": terminal})
            result.update(status=terminal["result"].upper(), terminal=terminal, actual_death=terminal["finalHp"] == 0,
                          net_hp_loss=terminal["startHp"] - terminal["finalHp"],
                          resources={"start_potions": initial["potions"], "end_potions": terminal["potions"],
                                     "potion_net_count": sum(p is not None for p in terminal["potions"]) - sum(p is not None for p in initial["potions"]),
                                     "start_max_hp": terminal["startMaxHp"], "end_max_hp": terminal["finalMaxHp"],
                                     "final_gold": None, "other_permanent_resources": None,
                                     "limitation": "current TerminalFacts exposes HP/maxHP/potions only; missing resources are not zero-valued"})
    except BudgetExceeded as error:
        result.update(status="COMPUTE_TRUNCATED", reason=str(error))
    except PolicyFailure as error:
        result.update(status="POLICY_ERROR", reason=str(error))
    except Exception as error:
        result.update(status="ENGINE_ERROR", reason=type(error).__name__ + ":" + str(error))
    result.update(decisions=decisions, wall_seconds=time.monotonic() - guard.battle_started,
                  combined_peak_rss_mib=guard.peak_rss_mib, trace_file=trace_path.name)
    return result


def summarize(plan, results, policies):
    report = {"schema": VERSION, "source_kind": "constructed", "natural_distribution_claimed": False,
              "declared_source_battles": plan["source_battle_count"], "actual_rollout_count": len(results),
              "policies": {}, "paired_comparisons": [], "training_run": False, "promotion": False,
              "claims": "actual closed-loop settled simulator outcomes, not predicted teacher heads; small finite constructed sample, no performance significance claim"}
    for policy in policies:
        rows = [r for r in results if r["policy"] == policy]
        terminal = [r for r in rows if r["status"] in ("WIN", "LOSS")]
        counts = Counter(r["status"] for r in rows)
        mean = lambda values: sum(values) / len(values) if values else None
        report["policies"][policy] = {"declared_source_battles": plan["source_battle_count"], "attempted": len(rows), "status_counts": dict(counts),
            "wins_over_all_declared_sources": counts["WIN"] / plan["source_battle_count"],
            "wins_over_completed_game_outcomes": counts["WIN"] / len(terminal) if terminal else None,
            "wins_over_all_declared_is_lower_bound_when_unresolved": len(terminal) < plan["source_battle_count"],
            "unresolved_or_failed_sources": plan["source_battle_count"] - len(terminal),
            "actual_deaths": sum(r.get("actual_death", False) for r in terminal),
            "mean_net_hp_loss_completed": mean([r["net_hp_loss"] for r in terminal]),
            "mean_final_hp_completed": mean([r["terminal"]["finalHp"] for r in terminal]),
            "mean_potion_net_count_completed": mean([r["resources"]["potion_net_count"] for r in terminal])}
    if "student" in policies:
        by_source = {(r["source_battle_id"], r["policy"]): r for r in results}
        for source in plan["scenarios"]:
            a, b = (by_source.get((source["source_battle_id"], policy)) for policy in ("student", "baseline"))
            resolved = a is not None and b is not None and a["status"] in ("WIN", "LOSS") and b["status"] in ("WIN", "LOSS")
            report["paired_comparisons"].append({"source_battle_id": source["source_battle_id"], "both_completed": resolved,
                "student_status": a["status"] if a else "NOT_RUN", "baseline_status": b["status"] if b else "NOT_RUN",
                "student_minus_baseline_final_hp": a["terminal"]["finalHp"] - b["terminal"]["finalHp"] if resolved else None,
                "student_minus_baseline_net_hp_loss": a["net_hp_loss"] - b["net_hp_loss"] if resolved else None})
        completed = [row for row in report["paired_comparisons"] if row["both_completed"]]
        report["paired_completed_count"] = len(completed)
        report["paired_unresolved_count"] = plan["source_battle_count"] - len(completed)
        report["mean_student_minus_baseline_final_hp_both_completed"] = (sum(row["student_minus_baseline_final_hp"] for row in completed) / len(completed)) if completed else None
        report["student_wins_baseline_loses"] = sum(row["student_status"] == "WIN" and row["baseline_status"] == "LOSS" for row in completed)
        report["baseline_wins_student_loses"] = sum(row["student_status"] == "LOSS" and row["baseline_status"] == "WIN" for row in completed)
    return report


def run(repo, plan_dir, output, *, dotnet, baseline_only=False, bundle=None, confirm_experimental=False):
    job_started = time.monotonic()
    plan = load_plan(plan_dir, repo)
    guard = Guard(plan["limits"])
    guard.started = job_started
    # Set affinity before importing or initializing a torch model as well.
    if not hasattr(os, "sched_getaffinity"): raise ValueError("Linux CPU/RSS guard required")
    os.sched_setaffinity(0, {min(os.sched_getaffinity(0))})
    student = None
    if not baseline_only:
        if not confirm_experimental or bundle is None: raise ValueError("learned evaluation requires an explicit experimental bundle confirmation")
        if plan["smoke_only"] or not plan["seed_holdout_checked"]: raise ValueError("smoke/unverified plan cannot evaluate learned weights")
        for name, max_bytes in (("weights.pt", 64 * 1024 ** 2), ("config.json", 2 * 1024 ** 2), ("manifest.json", 4 * 1024 ** 2)):
            if (Path(bundle) / name).stat().st_size > max_bytes: raise ValueError("bundle artifact exceeds evaluation size cap:" + name)
        sys.path.insert(0, str(repo / "python"))
        from nosl.inference import Inference
        import torch
        torch.set_num_threads(1)
        student = Inference.from_bundle(bundle, allow_experimental=True)
        if student.manifest.get("status") != "EXPERIMENTAL_UNPROMOTED" or student.manifest.get("trained") is not True:
            raise ValueError("only a trained experimental unpromoted bundle is admissible")
        expected = plan["prepared_manifest_sha256"] + ":train"
        if student.manifest.get("frozen_inputs", {}).get("splits", {}).get("train", {}).get("sha256") != expected:
            raise ValueError("bundle training corpus differs from frozen holdout audit")
    if not dotnet: raise ValueError("dotnet executable required")
    output.mkdir(parents=True, exist_ok=False)
    save(output / "frozen-plan.json", plan)
    policies, results = (["baseline"] if baseline_only else ["baseline", "student"]), []
    setup_wall_seconds = time.monotonic() - job_started
    guard.battle_started = time.monotonic()
    guard.check()
    # SIGALRM interrupts Python orchestration waits and public inference. RSS is
    # sampled at boundaries and every100ms while waiting, not a kernel RSS quota.
    previous = signal.signal(signal.SIGALRM, lambda *_: (_ for _ in ()).throw(BudgetExceeded("battle_or_job_wall_time_signal")))
    signal.setitimer(signal.ITIMER_REAL, plan["limits"]["max_job_seconds"])
    try:
        for source in plan["scenarios"]:
            for policy in policies:
                name = source["source_battle_id"].replace(":", "-") + "-" + policy
                guard.battle_started = time.monotonic()
                worker = None
                try:
                    guard.check()
                    signal.setitimer(signal.ITIMER_REAL, guard.remaining())
                    worker = Worker(repo, output / (name + "-stderr.log"), dotnet, guard)
                    result = rollout(worker, source, policy, guard, output / (name + ".trace.jsonl"), student)
                except BudgetExceeded as error:
                    result = {"source_battle_id": source["source_battle_id"], "category": source["category"], "policy": policy,
                              "status": "COMPUTE_TRUNCATED", "terminal": None, "reason": str(error)}
                except Exception as error:
                    result = {"source_battle_id": source["source_battle_id"], "category": source["category"], "policy": policy,
                              "status": "ENGINE_ERROR", "terminal": None, "reason": type(error).__name__ + ":" + str(error)}
                finally:
                    signal.setitimer(signal.ITIMER_REAL, 0)
                    if worker: worker.close()
                results.append(result)
                save(output / "outcomes.json", results)
                if time.monotonic() - guard.started >= plan["limits"]["max_job_seconds"]: break
            if time.monotonic() - guard.started >= plan["limits"]["max_job_seconds"]: break
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0); signal.signal(signal.SIGALRM, previous)
    report = summarize(plan, results, policies)
    report.update(plan_sha256=digest(plan), evaluator_sha256=file_hash(Path(__file__)), limits=plan["limits"],
                  cpu_affinity=list(os.sched_getaffinity(0)), combined_peak_rss_mib=guard.peak_rss_mib,
                  setup_wall_seconds=setup_wall_seconds, total_wall_seconds=time.monotonic() - job_started,
                  bundle_manifest_sha256=file_hash(Path(bundle) / "manifest.json") if bundle else None,
                  resource_guard_limit="RSS sampled, not kernel-hard RSS cap; native calls may delay Python signal handling")
    save(output / "report.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=ROOT)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("freeze")
    p.add_argument("--output", type=Path, required=True); p.add_argument("--prepared", type=Path)
    p.add_argument("--seeds-per-case", type=int, default=2); p.add_argument("--seed-prefix", default="NOSL-OFFLINE-HOLDOUT-v1")
    p.add_argument("--smoke-only", action="store_true")
    p = sub.add_parser("run")
    p.add_argument("--plan", type=Path, required=True); p.add_argument("--output", type=Path, required=True)
    p.add_argument("--dotnet", default=shutil.which("dotnet")); p.add_argument("--baseline-only", action="store_true")
    p.add_argument("--bundle", type=Path); p.add_argument("--confirm-experimental", action="store_true")
    args = parser.parse_args()
    if args.command == "freeze":
        plan = freeze(args.repo, args.output, prepared=args.prepared, seeds_per_case=args.seeds_per_case, seed_prefix=args.seed_prefix, smoke_only=args.smoke_only)
        result = {"status": "SCENARIOS_FROZEN", "source_battles": plan["source_battle_count"], "plan_sha256": digest(plan)}
    else:
        result = run(args.repo, args.plan, args.output, dotnet=args.dotnet, baseline_only=args.baseline_only,
                     bundle=args.bundle, confirm_experimental=args.confirm_experimental)
    print(canonical(result))


if __name__ == "__main__": main()
