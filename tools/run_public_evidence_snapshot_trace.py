"""Paired private-state diagnostic, deliberately separate from timed throughput."""
import argparse
import datetime
import hashlib
import json
import pathlib
import shutil
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--baseline-root", type=pathlib.Path, required=True)
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
out = root / "artifacts/reports/map-public-v6-snapshot"
harness = root / "artifacts/snapshot-trace-build/bin/Nosl.MapAcceptanceBenchmark/release"
baseline = args.baseline_root.resolve() / "artifacts/map-public-v6-frozen/bin/Nosl.Worker/release"
optimized = root / "artifacts/snapshot-frozen"
spec = root / "configs/public_evidence_snapshot_trace.json"
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
frozen = json.loads((out / "predeclaration.json").read_text())
runtimes = {}
for label, dependencies, key in [("baseline", baseline, "baseline_binary_hashes"), ("optimized", optimized, "binary_hashes")]:
    assert {p.name: sha(p) for p in dependencies.iterdir() if p.is_file()} == frozen[key]
    destination = root / "artifacts" / ("snapshot-trace-" + label)
    shutil.copytree(harness, destination)  # Existing runs are never overwritten.
    for source in dependencies.iterdir():
        if source.is_file():
            shutil.copyfile(source, destination / source.name)
    runtimes[label] = {"path": str(destination), "binary_hashes": {
        p.name: sha(p) for p in sorted(destination.iterdir()) if p.is_file()}}
assert runtimes["baseline"]["binary_hashes"]["Nosl.MapAcceptanceBenchmark.dll"] == runtimes["optimized"]["binary_hashes"]["Nosl.MapAcceptanceBenchmark.dll"]

with (out / "trace-predeclaration.json").open("x") as stream:
    json.dump(dict(created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
        declaration=json.loads(spec.read_text()), declaration_sha256=sha(spec), runtimes=runtimes,
        scope="4 fixed inspected source recipes, each direct continuation plus its independent fork; 8 paths per binary, not all 98 posterior branches; no new source data",
        private_audit="Native combat state digest, full run/player/monster RNG snapshot hashes, tape recipe and exact visited/override cell sets, public/action/terminal/source traces; no private state reaches policy",
        instrumentation_outside_timed_probe=True), stream, indent=2)

for label, runtime in runtimes.items():
    with (out / (label + "-trace.jsonl")).open("xb") as stdout, (out / (label + "-trace.stderr.log")).open("xb") as stderr:
        process = subprocess.run(["dotnet", str(pathlib.Path(runtime["path"]) / "Nosl.MapAcceptanceBenchmark.dll"),
            "--snapshot-trace", str(spec)], cwd=root, stdout=stdout, stderr=stderr, timeout=300)
    assert process.returncode == 0, (label, process.returncode)
    assert {p.name: sha(p) for p in pathlib.Path(runtime["path"]).iterdir() if p.is_file()} == runtime["binary_hashes"]
left = (out / "baseline-trace.jsonl").read_bytes()
right = (out / "optimized-trace.jsonl").read_bytes()
rows = [json.loads(line) for line in left.splitlines()]
result = dict(exact_trace_bytes_equal=left == right,
    baseline_sha256=hashlib.sha256(left).hexdigest(), optimized_sha256=hashlib.sha256(right).hexdigest(),
    decision_frames=sum(row["stage"] == "decision" for row in rows),
    settled_fixtures=sum(row["stage"] == "settled" for row in rows),
    trajectories_per_binary=2 * sum(row["stage"] == "settled" for row in rows),
    scope="Fixed direct source continuations and native forks; not the full posterior-branch population", timed_probe_separate=True)
with (out / "trace-comparison.json").open("x") as stream:
    json.dump(result, stream, indent=2)
print(json.dumps(result))
assert left == right
