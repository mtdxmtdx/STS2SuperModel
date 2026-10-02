# Public combat startup composition

`NativePublicCombatPrefixCondition` applies the existing
`NativeInitialShuffleCondition` and `NativeInitialHpCondition` certificates to
earlier combats recorded in `PublicRunEvidence`, including the current combat.
This is an optional accelerator for the declared state-addressed native tape
partition. It does not change the public evidence contract, native engine,
prior, or content support.

## Public mapping

Every typed combat owner start advances the observer combat index, even a
forced fight that never offers a stable decision. Noncombat owners and nested
parent ordinals do not advance it. Each combat uses its first stable decision;
its complete local history, observed run start, and any published combat
coordinate must agree. Missing run start or an earlier global observation gap
leaves absolute indices uncertified. A local gap disables that owner's startup
certificate. Every owner is retained with explicit eligibility reasons.

The mapper reconstructs only the startup segment: `Started`, `EntryAssets`, an
uninterrupted initial `CardDrawn` sequence, turn one, and the contiguous startup
intents. Typed assets are serialized through the canonical `NativeEntryAssets`
schema without loss of public card/relic details or potion slots. Character
and ascension come from the observed public run start. The certificates remain
authoritative for reviewed startup hooks, deck multiplicities, native ranges,
and initial lifetime monster slots. No private trace, graph, seed, identifier,
or outcome ledger supplies missing observations.

Later actions, draws, and reshuffles are not added to the initial permutation
constraint. They remain part of the full evidence that the owning replay source
must compare exactly. A malformed, incomplete, or uncertified startup falls
back to ordinary native replay, with a reason; it is not negative evidence and
does not remove that content from the prior.

The existing real `artifacts/reports/evidence/native-v4-sample.json` was
inspected: combat owner 3 is observer index 0 and has seven uninterrupted
startup draws. Combat owner 6 is index 1 and has two power-change observations
before its draws, so that later startup does not receive the existing shuffle
certificate. This demonstrates why earlier eligibility and current eligibility
must be evaluated separately.

## Proposal and integration contract

`NativePublicCombatPrefixProposal` accepts a dedicated independent reproducible
proposal-word source and an injected `ForceStateWords(words, purpose)` callback.
Attach its owned hypothetical run, then forward every native combat entry,
shuffle boundary, and initial monster-HP boundary. The helper checks the exact
canonical public entry before requesting any proposal words, the owned shuffle
and HP streams, the actual native draw pile/player references, and per-combat
HP identity. Completed HP and shuffle plans are retained when the next combat
begins; only the active combat's IDs reset.

Duplicate IDs retain separate, source-pinned helpers rather than weakening the
legacy unique-model certificate. Complete two/three-CorpseSlug startups use their
reviewed self-power slot order. The complete two-Toadpole startup uses the exact
registered unslotted front/rear factory and ordinary CombatStarted slot order;
see `NATIVE_TOADPOLE_STARTUP.md`. All other duplicate rosters retain fallback.

The helper multiplies each existing exact `NativeToProposalRatio` and public
root-constant `Envelope`, and applies every retained plan's actual native
bucket correction. Product composition is valid only when the tape callback
certifies distinct as-yet-unvisited state cells. Existing equal-state aliases
are still one native tape cell. A visited alias, preexisting conflicting word,
extra/missing forced word, wrong stream, or skipped native hook is an unresolved
computation error, never a retryable public mismatch.

The tape integration must:

- Disable the old current-only shuffle/HP conditioner when this helper is used
- Reconstruct a fresh helper and fresh identically seeded proposal RNG on replay,
  retaining the accepted hypothetical tape overrides
- Preserve the hybrid Rewards callback when entering a nested state force scope
- Enforce full-word consumption and visited/preexisting-cell consistency in the
  injected callback
- Call `ValidateCompletion` before any correction can reject randomly, and require
  full public evidence equality before accepting a hypothetical root

There is no new trial loop, optional stopping rule, posterior sample claim,
production generation, fitting, or training in this helper.

## Focused verification

Mapping tests cover forced/no-decision indexing, first-snapshot freezing,
detachment, canonical entry reconstruction, local/global gaps, coordinate
conflicts, unsafe hooks, incomplete startup, and exclusion of later draws.
Native unit tests execute two real HP/draw-pile startup sequences with the same
monster type, verify both retained corrections and the product envelope, and
exercise foreign streams/owners plus unresolved state-cell conflicts. The
injected test tape checks complete consumption of every forced word sequence.
These are controlled kernel fixtures, not independent posterior evidence.

The owning `NativeLabelTape` integration now exercises a native multi-combat
run with earlier Neow/HP/shuffle conditioning, primary reward conditioning, and
full public equality between the resulting hypothesis and its continuation
forks through settlement. This validates composition and reproducibility; the
separate bounded source-conditioned probe still fails its throughput gate.
