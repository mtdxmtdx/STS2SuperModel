# Bridge verification checkpoint

Code checkpoint: `fcbec1b68aab96c579351352d8b56ce6fdcfdc1d` (2026-10-01). Pinned upstream: `iRyougi/sts2-sim@5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`.

- Release solution build: passed,0 warnings,0 errors
- NOSL integration:803 passed,0 failed,0 skipped,3m26s
  -219 card/upgrade and focused mechanism cases
  -169 natural-encounter/factory/branch/formation/posterior cases
  -374 potion/relic-public-state cases
  -31 original M0–M2 regressions
  -10 bridge safety regressions
- Complete vendored Core suite:4479 passed,0 failed,3 intentional opt-in skips (4482 total),22m14s
- Two-process JSONL smoke: passed; public-invariant sample, stale token rejection, terminal settlement and zero reward selections
- Independent source-only review: no remaining confirmed high-severity finding in the reviewed changes; no claim of independently rerunning the integration suite

The exact upstream's [CI](https://github.com/iRyougi/sts2-sim/actions/runs/36843230604) also reported4479 passes and3 skips (10m57s). The local test run is actual execution of the vendored source plus the frozen NOSL modifications, not merely citing upstream CI.

Opt-in skips: neutral A0/A10 baseline measurement, unkillable-run invariant probe, and twenty-run-per-act-one A10 acceptance probe. No failed case was recategorized as skipped.

The803 cases are fixture evidence, not exhaustive combination coverage: all86 single-player Silent cards have base/legal-upgrade execution cases; all64 potions have execution/clone cases; all293 relics have explicit public-field projection cases (not all relic effects/inherited states); all80 natural encounters have factory and adapter opening tests. See [COVERAGE_BRIDGE.md](COVERAGE_BRIDGE.md) for remaining native forced-event, carry-in-state, action-space and sampler limitations.

Later M3–M6 or natural-collector source changes are outside this exact checkpoint and need their own regression evidence. No training-readiness flag is inferred from these counts.

## Commands

```sh
dotnet build Nosl.M012.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release --no-build -m:1 -nr:false
dotnet test vendor/sts2-sim/tests/Sts2Sim.Core.Tests/Sts2Sim.Core.Tests.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
python3 -B tools/protocol_smoke.py
```

Machine-readable summary: [bridge_verification.json](../configs/bridge_verification.json). Raw TRX/log output stays outside source control in ignored artifacts, per upstream contribution rules.
