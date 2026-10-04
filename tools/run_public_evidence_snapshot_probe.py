"""One fixed eight-root implementation comparison, never source collection/admission."""
import argparse
import datetime
import hashlib
import json
import pathlib
import resource
import shutil
import subprocess
import time


EXCLUDED = [
    "/elapsedSeconds", "/peakWorkerMemoryBytes", "/attempts/*/seconds",
    "/records/*/audit_only/costs/elapsed_seconds",
    "/records/*/audit_only/costs/clone_seconds",
    "/records/*/audit_only/costs/settlement_seconds",
    "/records/*/audit_only/costs/peak_worker_memory_bytes",
    "/records/*/audit_only/posterior_proposals/*/elapsedSeconds",
]
sha = lambda value: hashlib.sha256(value).hexdigest()


def write(path, value):
    with path.open("x") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def files(directory):
    return {p.name: sha(p.read_bytes()) for p in sorted(directory.iterdir()) if p.is_file()}


def normalize(value, path=""):
    if isinstance(value, dict):
        return {key: normalize(item, path + "/" + key) for key, item in value.items()
                if path + "/" + key not in EXCLUDED}
    if isinstance(value, list):
        return [normalize(item, path + "/*") for item in value]
    return value


def differences(left, right, path=""):
    if type(left) is not type(right):
        return [path]
    if isinstance(left, dict):
        result = [path + "/" + key for key in left.keys() ^ right.keys()]
        for key in left.keys() & right.keys():
            result.extend(differences(left[key], right[key], path + "/" + key))
        return sorted(result)
    if isinstance(left, list):
        if len(left) != len(right):
            return [path + "/length"]
        return [p for index, pair in enumerate(zip(left, right))
                for p in differences(*pair, path + "/" + str(index))]
    return [] if left == right else [path]


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("mode", choices=["prepare", "run", "compare"])
parser.add_argument("--baseline-root", type=pathlib.Path, required=True)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
out = root / "artifacts/reports/map-public-v6-snapshot"
frozen = root / "artifacts/snapshot-frozen"
baseline = args.baseline_root.resolve()
baseline_reports = baseline / "artifacts/reports/map-public-v6"
worker = frozen / "Nosl.Worker.dll"

if args.mode == "prepare":
    out.mkdir(parents=True, exist_ok=True)
    assert not (out / "predeclaration.json").exists(), "Never replace a declared invocation"
    source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    assert source_commit == "8aab8d9e478f28dd301a346f3280243136d9fded"
    freeze = json.loads((baseline_reports / "runtime-freeze.json").read_text())
    original_sources = freeze["source_files"]
    sources = {path: sha((root / path).read_bytes()) for path in original_sources}
    changed = sorted(path for path in sources if sources[path] != original_sources[path])
    assert changed == ["src/Nosl.Worker/NativeRunWorld.cs", "src/Nosl.Worker/NaturalSourceCollector.cs"], changed
    helper = "src/Nosl.Worker/PublicDecisionSnapshot.cs"
    sources[helper] = sha((root / helper).read_bytes())
    shutil.copytree(root / "artifacts/snapshot-build/bin/Nosl.Worker/release", frozen)
    assert files(frozen)["Nosl.Worker.dll"] == "b05e856f087358a12718829c6fb3eb2e80cb66f69c8278bfcdf4a0c9d335095c"
    spec = json.loads((baseline / "configs/native_map_inspected_probe_v6.json").read_text())
    request = (json.dumps(spec["request"]) + "\n").encode()
    assert request == (baseline_reports / "inspected-eight-request.jsonl").read_bytes()
    (out / "request.jsonl").write_bytes(request)
    shutil.copyfile(baseline_reports / "inspected-eight-response.jsonl", out / "baseline-response.jsonl")
    write(out / "predeclaration.json", dict(
        schema="nosl.public-evidence-snapshot-comparison.v1", created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        source_commit=source_commit, source_tree=subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=root, text=True).strip(),
        build_checkout_commit="1e05ac844004f67e1a404f90b5225b9274199d14",
        build_note="The final tested working-tree runtime sources became commit 8aab8d9; no production source changes followed the build.",
        baseline_runtime_commit=freeze["source_commit"], baseline_binary_hashes=freeze["binary_hashes"],
        runtime_changes=changed + [helper], source_files=sources, binary_hashes=files(frozen),
        request_sha256=sha(request), baseline_response_sha256=sha((out / "baseline-response.jsonl").read_bytes()),
        request=spec["request"], external_watchdog_seconds=spec["external_watchdog_seconds"],
        excluded_json_paths=EXCLUDED, excluded_build_fields=[],
        comparison="Compare the entire response after only the eight enumerated timing/memory removals; keep all other fields and ordered arrays.",
        invocation_limit=1, source_replacements=0, formal_training=False, production_admission=False))
    print(json.dumps({"ready": True, "source_commit": source_commit, "worker_sha256": files(frozen)["Nosl.Worker.dll"], "request_sha256": sha(request)}))

elif args.mode == "run":
    declared = json.loads((out / "predeclaration.json").read_text())
    assert files(frozen) == declared["binary_hashes"]
    assert sha((out / "request.jsonl").read_bytes()) == declared["request_sha256"]
    assert sha((out / "baseline-response.jsonl").read_bytes()) == declared["baseline_response_sha256"]
    write(out / "launch.json", dict(launched_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        runner_sha256=sha(pathlib.Path(__file__).read_bytes()), predeclaration_sha256=sha((out / "predeclaration.json").read_bytes())))
    start = time.monotonic()
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    killed = False
    with (out / "request.jsonl").open("rb") as inp, (out / "response.jsonl").open("xb") as response, (out / "stderr.log").open("xb") as err:
        process = subprocess.Popen(["dotnet", str(worker)], cwd=root, stdin=inp, stdout=response, stderr=err)
        try:
            exit_code = process.wait(timeout=declared["external_watchdog_seconds"])
        except subprocess.TimeoutExpired:
            killed = True
            process.kill()
            exit_code = process.wait()
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    result = dict(exit_code=exit_code, external_watchdog_killed=killed, wall_seconds=time.monotonic() - start,
        user_seconds=after.ru_utime-before.ru_utime, system_seconds=after.ru_stime-before.ru_stime,
        peak_child_rss_kib=after.ru_maxrss, binaries_unchanged=files(frozen) == declared["binary_hashes"])
    write(out / "resources.json", result)
    print(json.dumps(result))
    assert exit_code == 0 and not killed and result["binaries_unchanged"]

else:
    declared = json.loads((out / "predeclaration.json").read_text())
    assert declared["excluded_json_paths"] == EXCLUDED
    left_raw = json.loads((out / "baseline-response.jsonl").read_text())
    right_raw = json.loads((out / "response.jsonl").read_text())
    left, right = normalize(left_raw), normalize(right_raw)
    changed = differences(left, right)
    encoded = lambda x: json.dumps(x, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    result = dict(semantic_equal=not changed, changed_paths=changed, excluded_json_paths=EXCLUDED,
        baseline_normalized_sha256=sha(encoded(left)), optimized_normalized_sha256=sha(encoded(right)),
        baseline_response_sha256=sha((out / "baseline-response.jsonl").read_bytes()),
        optimized_response_sha256=sha((out / "response.jsonl").read_bytes()),
        baseline_worker_elapsed_seconds=left_raw["elapsedSeconds"], optimized_worker_elapsed_seconds=right_raw["elapsedSeconds"],
        source_draws_requested=right_raw["sourceDrawsRequested"], accepted_posterior_draws=right_raw["acceptedPosteriorDraws"],
        allocated_candidate_worlds=right_raw["allocatedCandidateWorlds"], settled_candidate_worlds=right_raw["settledCandidateWorlds"],
        budget_expired=right_raw["budgetExpired"], formal_training=False, production_admission=False)
    write(out / "comparison.json", result)
    print(json.dumps(result))
    assert not changed, "Semantic difference: never expand the exclusion list after observing a mismatch"
