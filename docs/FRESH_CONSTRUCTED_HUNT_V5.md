# Fresh constructed full-v5 finite Hunt producer

`FiniteHuntDatasetV5.GenerateAsync` creates a new declared-setup combat, evaluates
the existing finite Hunt plan on independent paired worlds, and emits real
simulator measurements under the complete v5 public schema. It does not read or
relabel an old v2 fixture. The records explicitly say
`source_kind:constructed_declared_setup`, `native_run:false`, `trainable:false`.

The source's run start and map were not observed. Its actual act/floor are retained,
its combat-entry index is null, and typed evidence starts with
`RunStartNotObserved`. No map event or graph is invented. This is a complete v5
envelope representing honestly unavailable history, not evidence that a native
run's complete map was captured.

## Execution and conditioning

TheHunt already uses the existing `whole-setup-rejection-v1` sampler: each proposal
creates a fresh Scenario with an independent proposal seed, replays the source's
recorded public decisions, and compares those decisions exactly. This producer
does not change that prior or inspect a live source's hidden future.

`ConstructedHuntV5View` deterministically records typed evidence from each newly
executed session's actual public decision transcript. The full source packet,
including v3 run context and v2 typed evidence, is frozen in the plan anchor.
Every accepted sampled world and every independently replayed continuation must
match that full packet before execution. Both policies start from the same
sampled world. The original next-player-turn deadline never changes.

The narrow observation seam in `AnchoredHuntEvaluator` preserves its ordinary
legacy API and successful record bytes. Sampling failures, branch failures,
truncation and cleanup failures each retain allocated mass. A failed baseline
does not erase a successful plan; success can remain available while paired HP
cost is masked. Cleanup failure invalidates labels even if settlement was already
observed. A formerly finished pursuit becomes unresolved; a genuine prior abort
remains an abort.

The original reviewed support remains unchanged: one TheHunt, the existing small
card family, one TwigSlimeS/LeafSlimeS/Nibbit, no potions, and RingOfTheSnake only.
The fresh producer caps requests at 16 evaluation worlds, 200 decisions per policy,
and 4,096 posterior attempts per world. It performs no seed search.

## Targets and evidence

The raw format is `nosl.dataset.finite-hunt.full-v5.raw.v1`. Its private audit
retains the declared Scenario and options, all paired outcomes and traces,
accepted full public roots, and actual terminal public evidence. Public model
input contains none of the source seed, evaluation seeds, outcomes or targets.

The Python `finite_hunt_v5` boundary validates the full v5 input first, checks the
fresh construction against its declared setup, verifies the immutable source and
terminal prefixes, and recomputes the two plan targets:

- Specified success requires a true settled win, actual public TheHunt fatal,
  actual offered extra CardReward, and the original deadline
- Extra net HP loss is baseline final HP minus plan final HP over complete pairs
- A complete actual failure is zero success; an unresolved execution is null
- Every action head stays null and masked, with zero action-world allocations

The extra CardReward retains its actual permanent-change ledger entry and its
unpriced objective utility. No future reward identity is inspected or selected,
and no reward price is supplied. Whole-plan measurements are not action values,
calibrated probabilities, safe learned execution, or proof of a population mean.

`adapt_record` preserves the exact raw bytes and regenerates an existing
`data_v5` engineering record with the semantic full-public digest. The strict
`validate_adapted_record` rechecks all evidence, instead of trusting that digest.
Hashes establish consistency, not source authenticity. Production admission
rejects the resulting engineering envelope. The separate full-policy record
normalization boundary is not modified by this core producer commit.

A separate [constructed full-policy adapter](FRESH_CONSTRUCTED_HUNT_FULL_POLICY_V5.md)
now connects this exact raw evidence to the real full-policy batch loss while
retaining engineering-only purpose and all admission restrictions.

## Running the bounded proof

The JSONL worker command creates its own source and does not require `reset`:

```json
{"op":"fresh_finite_hunt_v5","scenario":{"seed":"fresh-full-v5-hunt-2026-10-03","deck":["TheHunt"],"enemyHp":5},"options":{"evaluationSeeds":[7101,7102],"maxPosteriorAttempts":16},"sourceRun":"fresh-constructed-hunt-v5","sourceCombat":"fresh-constructed-hunt-v5/combat","branchFamily":"fresh-constructed-hunt-v5/paired"}
```

Focused C# tests export three freshly generated artifacts when requested. Python
integration checks use these artifacts; if absent, they explicitly skip with the
regeneration command and never substitute historical labels.

```sh
NOSL_FRESH_HUNT_V5_EXPORT=artifacts/fresh-finite-v5 \
  dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --filter FullyQualifiedName~FiniteHuntV5Tests
python -B -m unittest discover -s tests/data -p test_finite_hunt_v5.py -v
python -B tools/validate_finite_hunt_v5.py \
  artifacts/fresh-finite-v5/fresh-finite-hunt-v5-success.raw.json \
  --output artifacts/fresh-finite-v5/success.engineering.jsonl
python -B tools/check_finite_hunt_v5_forward.py \
  artifacts/fresh-finite-v5/fresh-finite-hunt-v5-success.raw.json
```

The deterministic fixtures cover immediate success, actual settled loss, and
decision-budget truncation. Ownership/mutation tests additionally cover sampling
failure, complete-public mismatches, fork failure, cleanup failure, asymmetric
policy failure and immutable later boundaries. Repeated verification runs are
not counted as additional independent samples.

The retained verification report records 60 affected C# checks, 13 strict Python checks, and 14 existing v5 boundary checks. The
current 1,573,534-parameter StudentV5 forward/loss check uses hard guards against
backward, autograd and optimizer construction. Both plan loss terms are positive,
all action/ranking terms are zero, and parameter bytes are unchanged. This adds
zero backward calls, zero optimizer steps and no saved weights; the earlier
one-backward authorization remains consumed.

See [the verification report](FRESH_CONSTRUCTED_HUNT_V5_VERIFICATION.json) for
artifact hashes, exact checks and the legacy byte comparison. Raw execution
artifacts remain under `artifacts/fresh-finite-v5`, outside the source commit.
