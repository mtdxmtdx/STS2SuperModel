# Sampling / cloning cost investigation

Engineering-only measurements in the isolated native-belief checkout based on `df13220`.
No training, new corpus rows, changed source distribution, posterior relaxation, or remote writes.
The authoritative running workers and their default binaries were not touched.

## Finding

The teacher's `clone_seconds` combines independent posterior sampling and continuation forks.
It is not a measurement of the raw graph-copy primitive. Reading the finalized engineering-200-v2
records and reconstructing the existing public capability predicate gives:

| Path | Roots | Combined sampling/fork seconds | Total teacher seconds |
|---|---:|---:|---:|
| Fast conditional sampler | 118 | 1.419 | 28.688 |
| Native rejection replay | 82 | 1,095.517 | 1,103.077 |

Thus 99.87% of the recorded combined clone time comes from replay roots. The historical aggregate
is 1,096.936 / 1,131.764 seconds (96.9%). This classification uses the recorded public state and
declared setup against the unchanged admission predicate, not newly generated training examples.

A deterministic six-root benchmark uses original engineering source indices 0, 8, 42, 89, 145,
and 196: two starter fast roots plus costly single-card, potion and relic replay roots. Source
recipes and steps are preserved in `tools/Nosl.Benchmarks/fixtures.json`; they are diagnostic
fixtures, not an estimate of corpus-wide average speedup.

Initial phase measurements found fast samples around 2–4 ms and exact graph copies around
0.02–0.13 ms. Replay samples took roughly 0.2–5 seconds with 1–22 independent proposals; exact
native continuation replay added roughly 0.06–1 second per fork. Reconstructing RunState alone
took roughly 0.03–0.8 seconds, versus 2–5 ms for player setup. Timing varies under shared load.
The later benchmark excludes public-root verification from the continuation-fork timer.

The dominant replay work is repeated standard-map generation, especially duplicate segment
processing in `MapPathPruning.FindMatchingSegments`. Model-ID metadata was measured separately
but did not explain the dominant time; no speculative reflection/model registry change was made.
Skipping maps, changing native initialization, caching sampled worlds, widening the fast sampler,
or bypassing exact native replay was deliberately avoided.

## Small production patch and equivalence argument

Only `vendor/sts2-sim/src/Sts2Sim.Core/Map/MapPathPruning.cs` changes the production implementation:

1. Keep the original complete-path enumeration and traversal order
2. During one FindMatchingSegments call, remember exact node-reference slices already visited
3. An identical slice has length at least three. Its second occurrence overlaps the first
   occurrence, or overlaps the earlier same-key segment which rejected that first occurrence
4. Segment groups only grow within the call. Therefore repeating that exact slice can never
   add a segment or alter a group. Eliding that duplicate work preserves first-visit order
5. Cached polynomial prefix hashes make slice lookup O(1) before equality checks. Hash collisions
   are resolved using full node-reference equality; neither hash values nor set iteration order
   enter game logic
6. The cache is local to one call and discarded before pruning mutates topology. Sorted key
   grouping, group/segment order, all pruning operations, and every RNG call remain unchanged

This is an implementation optimization of the pinned simulator algorithm, not a rule change or
an approximation. No source seed, observation, action, proposal acceptance, sampling budget,
random distribution or termination condition changes. It applies equally to source initialization,
replay proposals and continuation replays. Existing corpora must retain their recorded runtime version and frozen binaries. Adopting this
patch requires a distinct audited binary profile for a later stage; equivalence evidence is not
a reason to swap binaries under an active or resumable corpus.

## Verification

- Complete generated map topology, node/parent/child/start enumeration order, mutability flags,
  and final map RNG state match the pre-patch hashes for 64 maps: all four acts, A0/A10, eight seeds
- Sixteen of these baseline hashes are frozen in unit tests
- Synthetic branching DAGs compare the complete ordered result to the original reference
  algorithm, then mutate types/edges and compare again to detect invalid cross-call caching
- Constructed-root public hashes and rejection proposal counts are unchanged
- Same-seed teacher comparisons retain exact serialized action/outcome/evaluation results;
  additional checks compare source/sample/settled RNG and full public/terminal continuation traces
- Source observations and RNG remain unchanged by the benchmark

Initial final-patch map comparison was 9.55→4.61 seconds; a serial repeat was 9.16→3.64 seconds.
Allocation fell from 4.37 GB to about 1.36 GB across the 64 maps, approximately 69% less. The first
paired six-root full teacher comparison was 16.09→8.83 seconds; all outcome hashes matched.
Fast-root wall timings are noisy and are not claimed to improve. Final checked measurements and
verification counts are in `CLONE_PROFILING_SUMMARY.json`. Final NOSL tests passed 871/871;
17 targeted optimization tests, 37 existing upstream map tests, and the separate-process protocol
smoke passed. The supplemental patch was applied to the pre-patch file and reproduced the optimized file exactly. The native proof
was also rerun: all 200 public/source traces, all targets and all 174 native branch outcomes
matched the pre-optimization artifact exactly.

## Reproduction

Build `tools/Nosl.Benchmarks/Nosl.Benchmarks.csproj` in Release with an isolated `--artifacts-path`,
`-m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false`. Run the built benchmark DLL:

- `tools/Nosl.Benchmarks/fixtures.json 2`: split sample / continuation-fork / exact-fork measurements
- `--map-profile`: map topology/RNG hashes, elapsed time and allocation
- `--root-check tools/Nosl.Benchmarks/fixtures.json`: match the original recorded public-input digests
- `--teacher-check tools/Nosl.Benchmarks/fixtures.json`: full teacher outputs and private/public trace checks

Keep pre-patch and patched Core DLLs in separate execution directories. The same benchmark driver
must run against both. JSONL measurements stay in ignored `artifacts/clone-profile/`, never the
training corpus. Repeated executions here are correctness/performance trials and count as zero
new data samples.
