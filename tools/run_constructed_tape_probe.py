"""Predeclare and retain one bounded constructed native-tape request, without admission."""
from __future__ import annotations

import argparse
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import resource
import shutil
import signal
import subprocess
import time


ROOT = Path(__file__).resolve().parents[1]
sha = lambda value: hashlib.sha256(value).hexdigest()
canonical = lambda value: json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def write(path, value):
    with path.open("x") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")


def binary_hashes(path):
    return {str(item.relative_to(path)): sha(item.read_bytes()) for item in sorted(path.rglob("*")) if item.is_file()}


def source_hashes():
    untracked = subprocess.check_output(["git", "ls-files", "--others", "--exclude-standard", "-z"], cwd=ROOT).decode().split("\0")
    if any(path.endswith((".cs", ".csproj", ".props", ".targets", ".sln")) for path in untracked if path):
        raise ValueError("Untracked native source/build inputs must be committed before preparing a runtime receipt")
    paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
    return {path: sha((ROOT / path).read_bytes()) for path in sorted(paths) if path and
            (path.endswith((".cs", ".csproj", ".props", ".targets", ".sln")) or path == "global.json")}


def watchdog_seconds(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not 1 <= value <= 960:
        raise ValueError("External watchdog must be a finite number in [1,960] seconds")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("prepare", "run"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--spec", type=Path)
    parser.add_argument("--worker", type=Path)
    args = parser.parse_args()
    out = args.output.resolve()
    worker = out / "runtime/Nosl.Worker.dll"
    if args.mode == "prepare":
        if args.spec is None or args.worker is None:
            parser.error("prepare requires --spec and --worker")
        spec_bytes = args.spec.read_bytes()
        spec = json.loads(spec_bytes)
        watchdog_seconds(spec["external_watchdog_seconds"])
        request = deepcopy(spec["request"])
        if request.get("op") != "native_constructed_tape_candidates" or "build_receipt_json" in request:
            raise ValueError("A constructed request without a fabricated build receipt is required")
        out.mkdir(parents=True, exist_ok=False)
        (out / "spec.json").write_bytes(spec_bytes)
        shutil.copytree(args.worker.resolve().parent, worker.parent)
        identity_input = b'{"op":"native_constructed_tape_runtime_identity"}\n'
        try:
            identity = subprocess.run(["dotnet", str(worker)], input=identity_input, cwd=ROOT,
                                      capture_output=True, timeout=30, check=False)
        except subprocess.TimeoutExpired as exception:
            (out / "runtime-identity.jsonl").write_bytes(exception.stdout or b"")
            (out / "runtime-identity-stderr.log").write_bytes(exception.stderr or b"")
            write(out / "preparation-failure.json", {"stage": "runtime_identity", "reason": "timeout", "source_draws_started": 0})
            raise
        (out / "runtime-identity.jsonl").write_bytes(identity.stdout)
        (out / "runtime-identity-stderr.log").write_bytes(identity.stderr)
        if identity.returncode != 0:
            write(out / "preparation-failure.json", {"stage": "runtime_identity", "reason": "nonzero_exit", "exit_code": identity.returncode, "source_draws_started": 0})
            raise ValueError("Runtime identity failed; all returned bytes retained")
        receipt = json.loads(identity.stdout)
        if set(receipt) != {"upstream_commit", "worker_assembly_sha256", "core_assembly_sha256", "runtime_version"}:
            write(out / "preparation-failure.json", {"stage": "runtime_identity", "reason": "invalid_identity", "source_draws_started": 0})
            raise ValueError("Runtime identity response is not an identity")
        sources = source_hashes()
        vendor = {p: h for p, h in sources.items() if p.startswith("vendor/sts2-sim/")}
        wrapper = {p: h for p, h in sources.items() if not p.startswith("vendor/")}
        receipt["wrapper_source_sha256"] = sha(canonical(wrapper))
        receipt["vendor_source_sha256"] = sha(canonical(vendor))
        request["build_receipt_json"] = json.dumps(receipt, indent=2) + "\n"
        request_bytes = (json.dumps(request, separators=(",", ":"), allow_nan=False) + "\n").encode()
        (out / "request.jsonl").write_bytes(request_bytes)
        write(out / "predeclaration.json", {
            "schema": "nosl.constructed-native-tape.probe-predeclaration.v1",
            "prepared_utc": datetime.now(timezone.utc).isoformat(),
            "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
            "source_tree": subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=ROOT, text=True).strip(),
            "source_status": subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True),
            "source_files": sources, "binary_hashes": binary_hashes(worker.parent),
            "source_hash_encoding": "SHA256 of sorted compact JSON mapping tracked native source/build paths to SHA256; build assertion, not attestation",
            "runner_sha256": sha(Path(__file__).read_bytes()), "spec": spec,
            "spec_sha256": sha(spec_bytes), "request_sha256": sha(request_bytes),
            "external_watchdog_seconds": spec["external_watchdog_seconds"],
            "invocation_limit": 1, "source_replacements": 0, "production_admission": False,
            "formal_training": False, "quarantined": True})
        print(json.dumps({"stage": "prepared", "output": str(out), "request_sha256": sha(request_bytes)}))
        return

    declared = json.loads((out / "predeclaration.json").read_text())
    watchdog = watchdog_seconds(declared["external_watchdog_seconds"])
    if binary_hashes(worker.parent) != declared["binary_hashes"]:
        raise ValueError("Frozen runtime changed")
    if source_hashes() != declared["source_files"]:
        raise ValueError("Native source closure changed since preparation")
    request = (out / "request.jsonl").read_bytes()
    if sha(request) != declared["request_sha256"] or sha(Path(__file__).read_bytes()) != declared["runner_sha256"]:
        raise ValueError("Declared request or runner changed")
    write(out / "launch.json", {"launched_utc": datetime.now(timezone.utc).isoformat(),
                               "predeclaration_sha256": sha((out / "predeclaration.json").read_bytes())})
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    start = time.monotonic()
    killed = False
    interrupted = None
    code = None
    process = None
    def interrupt(signum, _frame):
        raise InterruptedError("Probe interrupted by signal " + str(signum))
    previous_term_handler = signal.signal(signal.SIGTERM, interrupt)
    with (out / "request.jsonl").open("rb") as inp, (out / "response.jsonl").open("xb") as response, (out / "stderr.log").open("xb") as error:
        try:
            process = subprocess.Popen(["dotnet", str(worker)], cwd=ROOT, stdin=inp, stdout=response,
                                       stderr=error, start_new_session=True)
            code = process.wait(timeout=watchdog)
        except subprocess.TimeoutExpired:
            killed = True
        except BaseException as exception:
            interrupted = type(exception).__name__ + ": " + str(exception)
        finally:
            if process is not None and process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                code = process.wait()
            signal.signal(signal.SIGTERM, previous_term_handler)
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    report_valid = False
    report_error = None
    try:
        report = json.loads((out / "response.jsonl").read_bytes())
        requested = len(json.loads(request)["options"]["sourceDrawSeeds"])
        report_valid = (report.get("schema_version") == "nosl.native-constructed-tape.raw-report.v1"
                        and report.get("source_draws_requested") == requested
                        and isinstance(report.get("attempts"), list) and len(report["attempts"]) == requested
                        and isinstance(report.get("records"), list))
        if not report_valid:
            report_error = "Response is not the expected all-attempt raw report"
    except (ValueError, TypeError, AttributeError) as exception:
        report_error = type(exception).__name__ + ": " + str(exception)
    measured = {"exit_code": code, "external_watchdog_killed": killed,
                "interruption": interrupted,
                "raw_report_valid": report_valid, "raw_report_error": report_error,
                "wall_seconds": time.monotonic() - start,
                "user_seconds": after.ru_utime - before.ru_utime,
                "system_seconds": after.ru_stime - before.ru_stime, "peak_child_rss_kib": after.ru_maxrss,
                "binaries_unchanged": binary_hashes(worker.parent) == declared["binary_hashes"],
                "native_sources_unchanged": source_hashes() == declared["source_files"],
                "response_sha256": sha((out / "response.jsonl").read_bytes())}
    write(out / "resources.json", measured)
    print(json.dumps(measured))
    if code != 0 or killed or interrupted or not report_valid or not measured["binaries_unchanged"] or not measured["native_sources_unchanged"]:
        raise SystemExit("Bounded probe did not complete cleanly; retained output must not be retried as a replacement")


if __name__ == "__main__":
    main()
