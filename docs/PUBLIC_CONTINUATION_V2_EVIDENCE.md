# Opt-in public continuation v2: bounded defensive-cycle exit

Date: 2026-10-02. Base: `931944c`. Pinned simulator:
`5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0`.

This change addresses the specific v1 behavior recorded in
[FINITE_LOOP_EVIDENCE.md](FINITE_LOOP_EVIDENCE.md), with a separate, opt-in
`nosl-public-rules-v2` continuation. The original `PublicRulePolicy` file,
default selection, v1 T1 identities, v1 dataset record shape, simulator, objective,
existing data, and model bundle are unchanged. There is no training or promotion.

## Public certificate and behavior

[ReviewedPublicRulePolicy.cs](../src/Nosl.Contracts/ReviewedPublicRulePolicy.cs)
receives only a `DecisionPacket`. It is stateless and cannot access a simulator
world, private draw order, source seed, future RNG, or teacher metadata.

The certificate admits only `nosl.public.v2`, A10, one living TwigSlimeS with
one unmodified 5-damage, one-hit attack, no player/enemy powers, no potions, no
stars/orbs/pets, and an ordinary RingOfTheSnake. All current cards, including
exhaust, must be plain base/upgraded Finesse, Impatience, FlashOfSteel,
StrikeSilent, BladeDance, or Shiv with reviewed metadata and no enchantment,
affliction, temporary cost change, replay effect, or unknown public-state field.
Public known-position and unordered draw counts must account for every draw
card, with no unidentified cards. Missing details fail closed.

This narrow attack preview is sufficient because the pinned native TwigSlimeS
has a fixed attack and the admitted state excludes damage/block modifiers.
Outside the certificate, v2 uses v1. It does not claim that an arbitrary intent
preview is actual incoming damage, or that an unfamiliar trigger is harmless.

Within the certificate:

- When every circulating card is Finesse/Impatience, select the existing legal
  end-turn action. Additional defense/draw has no reviewed damage, persistent
  asset, or reward-producing mechanism in this family
- In mixed inventories, preserve drawing whenever an off-hand card could provide
  another useful action. Only if all possible off-hand draws are reviewed loop
  cards can an unnecessary loop action be demoted below useful plays/end turn
- Finesse remains eligible while block is below the certified incoming attack
- Impatience remains eligible when it can draw Finesse needed for defense. Its
  native no-attacks-in-hand condition is respected
- Choice handling, legal action objects, root candidate enumeration, and
  action-order tie breaking remain intact. Attacks, including profitable
  FlashOfSteel cycles, retain their existing priority

Choosing end turn in the pure family can cause an actual eventual defeat. This
is a declared continuation behavior, not proof that every policy must lose and
not semantic nontermination detection. No outcome is inferred from a repeated
hand, action count, or excessive block. Only the native settled endpoint yields
win/loss. A budget reached first remains `ComputeTruncated` with retained mass,
null utility, and masked targets. No action-count utility penalty is added.

The inspected native authorities are `Finesse`, `Impatience`, `BladeDance`,
`Shiv`, `FlashOfSteel`, `StrikeSilent`, `TwigSlimeS`, and `RingOfTheSnake` under
`vendor/sts2-sim/src/Sts2Sim.Core/Models`; all execution remains in that engine.

## Opt-in identity and data separation

The JSONL worker accepts `continuationPolicyId` in `continue` requests,
`TeacherOptions`, and `NaturalSourceOptions`. Omission selects v1. Invalid IDs
are rejected. Before reset, `{"op":"continuation_policies"}` returns:

```json
{"status":"available","version":"nosl.continuation-policies.v1","supportedPolicyIds":["nosl-public-rules-v1","nosl-public-rules-v2"]}
```

T1 exploration uses the selected fallback. The v2 frozen tree family is
`nosl-public-uct-frozen-v2-rules-v2:<digest>`; v1 remains
`nosl-public-uct-frozen-v1:<digest>`. Thus identical tree choices cannot hide a
changed fallback policy behind an old identity.

Only v2 teacher records add `audit_only.dataset_version` and
`audit_only.versions.dataset`, both `nosl.teacher-data.public-rules-v2.v1`.
The Python preparation version lock rejects appending these records to a v1
corpus. Both real diagnostic records pass the engineering-smoke validator
separately. Mixed preparation reports `append_record_versions_mismatch`.
An independent reviewer also verified that a real v1 corpus rejects a v2 append
with `record_versions_mismatch_no_stage_committed`, leaving its manifest hash
unchanged. No existing corpus was used for these checks.

## Native regression evidence

[ReviewedPublicRuleTests.cs](../tests/Nosl.Tests/ReviewedPublicRuleTests.cs)
executes actual native transitions:

- Two Finesse plays produce block 8 against incoming 5. V1 selects more Finesse;
  v2 selects BladeDance, then three generated Shivs produce a settled 70-HP win
- A distinct mixed fixture at block 4 keeps Impatience useful: it draws discarded
  Finesse, which produces block 8 before BladeDance. Independent review reproduced
  this sequence at player HP 1 / enemy HP 13; it is useful-block evidence, not a
  claim that this separate fixture becomes a win
- Pure Finesse ×2 and Impatience ×2 each reach an actual native loss after 14
  legal end turns. All six T0 candidate/world copies settle; budget 2 leaves
  every copy truncated instead
- FlashOfSteel and FlashOfSteel+ still win through 13 and 9 atomic plays,
  respectively, with full HP and objective cost 0
- Two native setups with identical public roots but different heterogeneous
  hidden draw order and actual future RNG produce identical T0/T1 candidate
  outcomes and frozen identities. Both source packets, RNG states, and assets
  remain unchanged
- Unidentified draws, missing card details, powers, modified intents, relics,
  potions, enchantments, and afflictions preserve v1 fallback. Useful hidden
  draws, native choices, and all legal candidates remain available
- Standalone protocol and native source collection verify explicit v2 selection,
  unchanged defaults, and unknown-ID rejection

## Paired T0 completion comparison

[public_rules_v2_fixture_report.json](../configs/public_rules_v2_fixture_report.json)
records one constructed mixed root after two native Finesse plays. It has all
three legal candidates: BladeDance, Finesse, and end turn. Each policy uses the
same 16 independent final root-world seeds (201–216), producing 48 candidate
copies per policy, with a 32-decision continuation budget. Candidate copies
sharing a seed are paired; they are not 48 independent root worlds.

| Root action | V1 outcomes | V2 outcomes | V2 settled HP / cost |
|---|---|---|---|
| BladeDance | 16 wins | 16 wins | 70 / 0 |
| Finesse | 16 compute truncations | 16 wins | 70 / 0 |
| End turn | 16 compute truncations | 16 wins | 70 / 0 |

V1 completes 16/48 copies and retains all 32 unresolved copies with null values
and false masks. V2 completes 48/48. The source public input and every candidate
are identical across the comparison and the source remains unchanged. Every v2
candidate value ties at 0: this is completion evidence, not an informative
relative-preference label, global policy improvement, natural source coverage,
or full C02/M3 acceptance. Neither policy obtains formal-label permission.

Raw JSONL records are generated into a new ignored artifact directory and their
SHA-256 hashes are in the small checked-in report. Recorded settled action
counts include the two historical preparation plays; continuation budgets count
new branch decisions. No raw experiment payload is committed.

## Reproduction and verification

Use the configured .NET 9 SDK and cached restore sources. Build/test products
must use an isolated artifacts path. The full Nosl project suite first reported
916/917; its only failure was an existing standalone policy test hardcoding the
default `bin` path. That test now resolves its already-built referenced assembly,
which works with isolated artifacts and still verifies the process boundary.
The repaired full suite passed **917/917** before the final draw-preservation and
protocol hardening additions. Subsequently, affected native/teacher/objective
coverage passed **61/61**, and final policy/source/protocol coverage passed
**17/17**. The final combined full suite belongs to integration; no full-suite
claim is inferred from these later focused checks.

```sh
dotnet test tests/Nosl.Tests/Nosl.Tests.csproj -c Release \
  --artifacts-path /tmp/nosl-public-v2-build --no-restore \
  -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter 'FullyQualifiedName~ReviewedPublicRuleTests|FullyQualifiedName~NaturalSourceTests|FullyQualifiedName~M2_StandalonePolicyProcessReceivesOnlyWhitelistedJson'
python3 tools/verify_public_continuation_v2.py \
  --worker /tmp/nosl-public-v2-build/bin/Nosl.Worker/release/Nosl.Worker.dll \
  --output artifacts/public-rules-v2-paired-fixture-new
```

The comparison tool positively checks worker capability before collection and
requires a new output directory. It performs no optimization, production cohort
generation, formal-label generation, training, remote write, or catalog status
promotion. Unsupported interaction families and v1 fallback can still exhaust
compute; a general loop solver remains out of scope.
