# Reuse the public opening act in initial-map rejection

The public-map branch previously returned before reusing a separately available
`NativePublicOpeningEncounterCondition`. A source could publicly identify an
Overgrowth or Underdocks weak roster, yet initial-prefix proposals still generated
maps for both acts and could defer a wrong-act contradiction until native room
generation. The map-only certificate alone does not identify an act.

`NativeInitialPrefixCondition.TryCreate` now joins the existing public-map
certificate with that optional opening-roster certificate. The existing native
prefix sampler already rejects a certified wrong act after its three native
ActSelection words, before constructing any map. The whole seed and oracle are
redrawn at the next trial. Correct-act candidates still run every native map pass
and match the same declared public map observation channel. The original legacy
certificate path and no-roster fallback are unchanged. Prior identities, source
execution, room rules, and complete content support do not change.

The necessary event is now E = observed act AND observed map when both are
certified. Its probability is root dependent; no numerical value is assumed.
For fixed K, fresh whole-prefix rejection has successful subdensity
p(x)1[E(x)] times the root-constant sum of clean-miss probabilities. Wrong-act map
cells integrate out exactly. No seed-specific retry normalizer, held act, forced
native constructor, or extra likelihood factor is introduced. Native errors in
executed work remain errors. The subsequent opening-encounter proposal still
selects the public weak encounter within its actual native act.

Tests cover both acts, both map observation profiles, exact accepted native trace
replay, a three-word wrong-act miss followed by a whole fresh-seed success,
exhaustion accounting, and no-roster/gap/unreviewed-roster/unavailable-history
fallback. Finite alias-dependent enumeration compares early versus eager joint
predicates: accepted masses stay 26:13 out of 64, with 25 exhausted attempts and
a common p/q of 8/13; skipping irrelevant map reads changes no accepted trace.
Actual recorded roots also check that the replayed native act equals the act
certified from the detached public opening roster.

## Predeclared bounded work diagnostic

`configs/joint-public-act-map-work-v1.json` fixes the already used source draws
11002/11003, original hybrid prior, oracle seed 880014, and 64 common native whole
prefixes. Every candidate is generated once under the original law. For each
root, the same candidate seed and recorded oracle words are supplied to one trial
of the actual new prefix sampler. Its acceptance must equal act AND map, and its
native word count must be exactly three on wrong-act candidates or the original
full count on correct-act candidates. Selected traces must match exactly.

Report all fixed candidates, map-only versus joint-event matches, maps avoided,
wrong-act map accepts avoided, and native words consumed. A smaller joint match
count is expected when previously accepted maps had the wrong public act. This
is a component work diagnostic, not full-posterior throughput or an acceptance
rate improvement claim. No replacement root search, new fitting, production data,
or remote write is allowed.

```
dotnet run --project tools/Nosl.MapAcceptanceBenchmark -c Release --artifacts-path artifacts/canonical-map -- --joint-act configs/joint-public-act-map-work-v1.json
```

## Fixed work result and deterministic fixture maintenance

Implementation and declaration were committed as `4edfdbe` before the diagnostic.
Declaration SHA256:
`c07770c1a3dff80dd23b0d15ae3b2038b48c7db19defa1311a6a00dfc0bfcef1`.
Both roots certify Underdocks. In the 64 common candidates, the new sampler
avoided 36 wrong-act maps per root. Native words fell from 17,824 to 7,938 per
root. Root 11002 had four map-only matches and three joint matches; root 11003 had
one map-only match and zero joint matches. Each previously accepted wrong-act map
is now rejected before construction. All executed predicate/traces and word
counts matched the reference candidate. The entire diagnostic took 4.56 seconds;
that includes eager reference work, so it is not a sampler speedup measurement.

Raw output: `artifacts/canonical-map/joint-act-work-v1.jsonl`, SHA256
`14ad1422df7911102d85a6103ab424ecaaef38618fc62415bb881b4fee669b8e`.
No additional source draws or candidate seeds were searched after these results.

Skipping map words changes subsequent deterministic auxiliary-stream positions.
Two old fixtures were maintained without searching for new successful seeds:

- The opening/Neow/combat composition fixture retains its original recorded
  accepted native prefix, regenerated from its original public map boundary and
  fixed auxiliary seed, then passes that same RunSeed and native trace through
  the new joint condition. Its accepted trace and downstream public replay remain
  pinned. Fresh joint generation is tested separately for both native acts.
- The fixed root-11008/seed-501/K64 outer-retry fixture now accepts its first outer
  attempt. The historical v4 first-exhaustion/later-success result is historical
  only. Root-11001/seed-501/K1 still requires exactly two independently redrawn,
  clean exhausted outer attempts, preserving native retry/accounting regression
  coverage. Source seeds and budgets remain unchanged.

The final focused suite passed 44/44, including both legacy initial-prefix test
classes, public-map and opening certificates, native opening composition through
settlement, and outer-retry accounting. Independent read-only review found no
correctness blocker in normalization, trace ownership, fallbacks, fixture
maintenance, or the diagnostic. The updated native fixture no longer shows
exhaustion followed by success within a single call; actual exhaustion/retry and
successful replay are covered separately, together with exact finite outer-retry
normalization tests. Full combined regression is an integration-stage check.
