# Coordinate-ordered public map observation profile

`publicMapObservationProfile: "nosl.public-map-options.coordinate-order.v1"`
selects an explicit conservative observation channel in both
`NaturalSourceOptions` and `NativeRunExecutionOptions`. It requires the existing
run-evidence and run-context channels. The serialized execution option changes
both run and tape prior identities. Raw source records carry the declaration in
`audit_only.public_map_observation_profile`; tape records already carry the full
declared prior. Omission retains the old producer and exact serialized identities.
Old public inputs, corpora, weights, and test seals are not migrated or rewritten.

## Contract audit and retained information

The pinned simulator's `MapPoint.Children` is a `HashSet<MapPoint>`.
`MapTravel.GetTravelablePointsFrom` may enumerate those children or a row for free travel.
The original recorder keeps that native option enumeration. Its DTO does not
assign semantic indices to map choices: `PublicMapChosen` identifies an offered
coordinate, and validation checks membership by coordinate. The source script
`NativeSourceMapChoice.Choose` orders by node priority, column, then row; it does
not use the enumeration as a tie breaker for distinct coordinates.

The new producer sorts only its projected option array by public row, then
column. It preserves the current node, every offered coordinate, node icon,
ordinary edge, connection flag, and chosen coordinate. Unknown remains Unknown.
It neither sorts the native collections nor changes generation, actions,
encounters, rooms, or random draws. All native content remains eligible.
`NativePublicMapSlice.Observe` is shared by the recorder and the initial-prefix
predicate. Every later observed map is still part of full-packet replay.

This is an intentionally chosen coarsening of the observation channel. No live
client/UI observation was performed, and this change does not prove that native
enumeration is or is not visible in a live client. We may conservatively choose
to forget that incidental order. We must not silently apply the coarsening to an
input that declares the original channel, or rename old data as new data.

The existing DTO is sufficient: its shape and coordinate-based meaning do not
change. The external profile versions the observation function rather than the
wire grammar. A canonical-profile consumer rejects any map option array not in
strict coordinate order, including later maps; it does not repair inputs during
validation. Original-profile packets whose options happen to be sorted are
compatible values in the new channel, but choosing its prior remains explicit.

## Conditional law

If the old observation is Y and C sorts only its map options, the new channel
observes Z=C(Y). For each z, the likelihood is the sum of the original likelihoods
across all fine observations y with C(y)=z. This generally changes the posterior:
old data cannot be reused as though its prior/observation declaration were the
same. No reciprocal permutation count, estimated acceptance adjustment, or
preservation of the old posterior is claimed.

The initial-map accelerator conditions the same coarsened necessary event as
the producer. Each trial still redraws the entire joint native seed/oracle
prefix. For fixed root and cap K, successful proposal density is the native
prefix density on that event times the root-constant geometric factor from
complete clean misses. The existing root-constant correction argument is
unchanged. Exhaustion remains computationally inconclusive; native exceptions
remain exceptions. Finite tests explicitly distinguish fine masses 2:1 from
coarsened masses 2:3 and exhaustively enumerate two-trial outcomes 14:21 with
one exhaustion, whose common p/q is 6/7.

## Predeclared standalone diagnostic

Before running the diagnostic, `configs/canonical-map-acceptance-v1.json` fixes
existing development source draws 11002 and 11003, their original full hybrid
prior, candidate oracle seed 880013, 512 whole native constructor trials, and
blocks of 64. No replacement roots or success search is allowed. Each source is
executed under both channels and must have identical native random state and
public output after the declared coarsening. The same independent candidate maps
are checked against both necessary events. Report matches and first acceptance
per fixed block, including every exhausted block. This measures only map-event
acceptance, not full posterior acceptance, speedup, production eligibility, or
training readiness.

Run with .NET 9:

```
dotnet run --project tools/Nosl.MapAcceptanceBenchmark -c Release --artifacts-path artifacts/canonical-map -- configs/canonical-map-acceptance-v1.json
```

All generated output stays under isolated `artifacts/canonical-map`. No fitting,
formal training, production admission, or remote write is part of this change.

## Fixed diagnostic result (2026-10-02)

The declaration and implementation were committed as `4ba9bcd` before execution.
Declaration SHA256: `43c80d18b8e9ce326e1720bfcc5ab2eef80ee4f30c5251ec50efe8b38514f290`.
The 512 shared complete native prefixes consumed 142,602 native words in 15.75
seconds. Both paired source executions retained identical public packet hashes
and native random state: these roots already exposed coordinate-ordered options.

| Source draw | Original matches | Coordinate-order matches | Original / canonical exhausted 64-trial blocks |
|---|---:|---:|---:|
| 11002 | 7 / 512 | 7 / 512 | 3 / 8 for both |
| 11003 | 3 / 512 | 3 / 512 | 5 / 8 for both |

Every block's first accepted trial was identical between channels. This sample
shows **no map acceptance benefit** on the two measured roots; it does not solve
the observed initial-map bottleneck. The source-11003 Neow choice is WingedBoots,
whose first offered row is already coordinate ordered. Source 11002 chose
ScrollBoxes and its observed options were also ordered. No additional roots were
searched after the negative result.

Raw local diagnostic: `artifacts/canonical-map/acceptance-v1.jsonl`, SHA256
`d38a280f7421f7683bd5fe5ce473159b05963771a8bbe3413abe0da79de24e34`.
The original prior identity remains
`d61db05514950173d55dd38593253f23c9b93c910767f8d12c742e830b767ff7`;
the explicitly chosen observation profile has identity
`7333be0aa34902b4e6e596727055d998b6eeb3259b23eaef59033da5307f7b51`.

## Verification and review

The focused map/profile/context/evidence checks passed 36/36, and the full
`Nosl.Tests` regression passed 1,491/1,491. Independent read-only review found no
correctness blockers; its later-map validation regression and channel-specific
benchmark failure diagnostics were added. The final profile tests passed 4/4,
including a structurally valid hostile input whose initial map remains canonical
but a later map's options are reversed. Actual native paired-source tests verify
full retained public evidence, policy actions, map geometry, and native run/player
random states through an additional action. No simulator files were changed.
