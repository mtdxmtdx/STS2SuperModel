# Public Neow PhialHolster potion proposal

This engineering-only proposal addresses the first recorded initial-event settlement
for PhialHolster under either explicit hybrid tape prior. It does not alter potion
prices, objective values, source eligibility, finite source seeds, or training status.

## Public certificate and native boundary

The certificate reads only the detached complete public evidence: the typed initial
Silent A10 Neow options and selected PhialHolster, the native starter relic with two
empty potion slots, and the immediately completed owner-zero settlement at event 4.
That settlement must contain starter + PhialHolster and exactly two ordered, distinct
potion identities followed by one empty slot. Later inventory alone, missing settlement,
partial captures, other relics, or unsupported potion identities cannot enable it.
An uncertified root retains ordinary replay; it is not removed from the prior.

The optional `LabelPhialHolsterScope` surrounds the pinned native pickup batch after
slot growth. The actual `PhialHolster.AfterObtained`, `PotionFactory.RollRarity`, filtered
`CreateRandomOutOfCombat`, `Rng.NextItem`, and `PotionCmd.TryToProcure` execute normally.
Ordinary execution creates no pool snapshot or random draw for this seam. The native
implementation's existing batch-uniqueness constraint is preserved: the second draw
excludes the first generated ModelId. This documents the pinned simulator law, including
its already documented batch-uniqueness supplement; it does not claim new game fidelity.

The proposal verifies the owned solo initial Neow room, floor, character, exact relics,
empty grown slots, exact character-plus-shared all-unlocked pool, sequential run/player
RNG mode, the actual `CombatPotionGeneration` stream, and the explicit hybrid full-state provenance marker.
That stream remains in the existing full-state oracle partition. No Rewards cell or
Map cell is reassigned and no new prior version is introduced.

## Exact joint mass and correction

Let D = 2^53. Native rarity uses float-rounded `NextDouble` with `<= 0.1f` rare,
`<= 0.35f` uncommon, otherwise common. An identity's exact mass is the sum, over
compatible rarity arms, of the arm's integer preimage size times the native
`NextInt` position bucket size, divided by D^2. The shared resource arithmetic
retains the float comparison, double multiplication, rounding, and low eleven raw bits.

For observed ordered identities (a,b), let P be the fixed native pool and
P' = P excluding ModelId(a). The joint proposal samples each native raw-word
preimage uniformly with exact rejection sampling. Its native/proposal ratio is
Z = mass(P,a) * mass(P',b). The public-root envelope is exactly B = Z, so correction
Z/B = 1. The explicit ratio and correction method are retained for composition and
validation. Treating the grants as independent draws from P would give the wrong mass.
The two observed non-null grants require two native words each; empty-rarity branches
are outside this particular evidence, rather than silently re-rolled or normalized away.

The owning tape's `ForcePrefixWords` enforces fresh addresses, exact native stream/state,
no already-read aliases, replay override consistency, and full consumption. The proposal
also compares the final native primitive state, counter advance of four, and actual
procured slot identities. Missing callbacks, restored/skipped/added draws, scope errors,
and native exceptions leave completion invalid. A native exception aborts batch
validation while preserving cleanup and the original exception; it is never credited
as a successful conditional grant or converted into a public contradiction.

Incomplete disposal of either generic forced-word API now records the first fatal
failure on the owning tape before throwing. Catching that exception cannot permit
later correction, public-prefix checks, or replay. Disposal is idempotent. Phial's
explicit native abort also aborts its internal word scope, suppressing the missing-word
cleanup check while preserving the original native exception, cancellation, or public
mismatch. This does not clear an earlier fatal callback failure or mark the partial
grant complete; proposal validation and correction still reject it.

## Verification

`NativeNeowPotionTests` reproduces fresh actual-native source draws 24001
(SkillPotion, StrengthPotion) and 24003 (WeakPotion, FyshOil). Only detached public
packets are passed into certificate construction; an independent seed is used for
the conditioned opening. Tests cover native grant settlement and continuation replay,
missing public settlement, missing callback, foreign stream, pool drift, and native
failure propagation. Finite 2- and 3-bit exhaustive worlds independently enumerate
rarity/index quadruples, verify the without-replacement joint mass, and retain an
unobserved fifth cell's native probability.

These checks establish local primitive and replay behavior, not production posterior
acceptance, broad root coverage, or quality admission. Owning tape integration and
fresh end-to-end probe results must be reported separately.

`NativeConditionedWordFailureTests` checks partial disposal and explicit abort for
both forced-word APIs, including preservation of an earlier callback failure.
`NativeNeowPotionStickyFailureTests` exercises the actual owned grant boundary
with partial consumption, native failure, cancellation, public mismatch, and a
prior callback failure; abort cleanup preserves the original exception and never
credits an incomplete grant.
