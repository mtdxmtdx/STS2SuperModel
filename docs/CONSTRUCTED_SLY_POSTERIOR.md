# Constructed Reflex/Tactician posterior extension

This change admits only `Reflex` and `Tactician` in addition to the existing
24-card constructed exchangeable family. It uses the ideal conditional
permutation / independent future RNG model already used by that family. It
**does not preserve or certify the former finite whole-setup-seed posterior**
for these scenarios. This is a separately versioned prior, not a speed-only
implementation of whole-setup rejection.

## Scope and provenance

- Dispatcher: `nosl-belief-dispatch-v3`
- New stable profile: `reviewed-constructed-reflex-tactician-exchangeable-v1`
- New choice profile: `reviewed-constructed-reflex-tactician-conditional-choice-v1`
- Original 24-card stable and conditional profile strings are unchanged
- Family identity is determined by the declared initial deck, including upgrades;
  removing or exhausting its Sly cards cannot change that identity
- The initial inventory and enemy restrictions remain in force, as do current
  enchantment, affliction, and unidentified-generation exclusions. Temporary Sly
  is explicitly excluded. Calculated Gamble, Hand Trick, other Sly cards, new
  powers, relics, potions and enemies are outside the extension
- `NativeBeliefCertificate`, native choice handling, the choice-origin card list,
  objective, continuation, ranking formulas/bounds, and public/student DTOs are unchanged.
  A conservative initial-prior guard keeps the new family outside the old ranking certificate

Teacher records carry the new profile only in audit metadata and carry dispatcher
v3 in `audit_only.versions.posterior_implementation`. Dataset preparation compares
the complete `versions` object and rejects mixed-version append batches; frozen
student inputs likewise require identical stable versions. Therefore v2 records
cannot silently mix with this v3 cohort. The new profile remains visible for
within-version stratification. No training, optimizer work, broad generation,
or remote writes are part of this change.

## Source-derived sufficiency

The pinned simulator's Reflex draws two cards (three upgraded), and Tactician
gains one energy (two upgraded). Both cost three manually and have permanent
Sly. Their card-specific private numeric fields are fixed by the public upgrade
level. Existing public card details, piles, energy, counters and complete history
already expose the effect-relevant state for reachable constructed states in
this closed family under the pinned simulator source. Mechanics changes or
broader content require a new sufficiency audit.

Native `CardCmd.Discard` performs ordinary discard movement and hooks, then
`AutoPlayCmd.FromCards` moves every selected Sly card into Play before resolving
them freely in selection order. A Tactician that
has resolved can re-enter a shuffle during subsequent Reflex; a Tactician still
waiting in Play cannot. End-turn hand flushing uses pile movement directly and
does not invoke Sly. No simulator behavior is modified here.

## Conditioning and ownership

The existing stable-origin algorithm is unchanged. It canonicalizes the public
unknown draw multiset, preserves known physical positions, samples a permutation,
and replaces future RNG streams at the stable boundary. It then replays the exact
observed action suffix and accepts only complete matching public packets. It
never samples in place at a suspended coroutine and never includes a later
candidate selection or rollout outcome as acceptance evidence. Accepted future
streams remain intact; independent continuation forks replay the accepted origin
and each own their coroutine and origin after parent disposal.

At the actual `discard-draw:0` diagnostic revision 8, the unknown draw multiset is
Acrobatics, Dagger Throw and Defend, each once. Prepared+ exposes Defend then
Acrobatics. Exactly one of six permutations reproduces the complete revision-9
packet, including all 30 ordered actions; Dagger Throw remains in Draw. This
small-world enumeration establishes that draw-order conditioning example, not
all future PRNG distributions or equivalence to whole-setup-seed conditioning.

At that choice, Tactician then Reflex draws Dagger Throw and then reshuffles and
draws Tactician. Reflex then Tactician draws only Dagger Throw, since Tactician
is still in Play. Native tests inspect the resulting energy, piles, history and
counters, including free versus manually paid plays and both upgrade levels.

## Verification boundaries

`SlyBeliefTests` covers exact six-order conditioning, source-order/RNG erasure,
indistinguishable-instance replacement, known physical positions and duplicate
signatures, draw-pile exhaustion and reshuffle, accepted-RNG fork ownership and
parent disposal, end-turn flushing, profile identity, and fail-closed initial
content/temporary-Sly/enchantment/affliction/native/history exclusions. Existing
conditional-choice, native-belief and dataset provenance tests remain applicable.
TeacherRanking retains its existing formulas and bounds, with one narrowing
profile guard: the new Sly prior never inherits the original family's certificate,
even after its distinguishing cards leave current piles. A deliberate internal
removal fixture tests this invariant; it does not claim natural reachability.

A separately predeclared bounded diagnostic reuses the original scenario, exact
nine-action prefix, all 30 candidates and the original 16 evaluation-world seeds.
Its record and timing must identify the new binary/profile; the old 120-second
timeout with 480 unknown requested copies remains baseline evidence. Any new
incomplete mass remains incomplete, never a loss or zero-valued observation.

## Bounded diagnostic result (2026-10-02)

The independently reviewed probe reproduced every original public packet through
revision 9, including root hash
`4515bbccc3d72d1d5a7ab6a023f577a8fd114732659e159202004a1ba195eea1`.
All 30 candidates × the original 16 evaluation seeds completed: 480 terminal
wins, zero incomplete copies. Teacher request time was 12.30 seconds; whole-job
time was 13.17 seconds; sampled combined driver/worker RSS peaked at 120.31 MiB
on CPU 6. The original 120-second whole-setup timeout and its 480 unknown requested
copies remain separate baseline evidence. The active generator runtime and
baseline artifact hashes were unchanged.

The student's original action, Defend then Slimed (index 23), had empirical value
0 and final HP 18, tied for the top observed value with four other actions. The
30 empirical values ranged from -7.5444 to 0. Strong ranking remained masked,
with no certified pairs or equivalence set. This probe alone neither establishes
general performance nor preserves the former finite whole-setup posterior.

See `CONSTRUCTED_SLY_POSTERIOR_SUMMARY.json` for validation counts, explicit proof
limits and hashes of the retained local diagnostic artifacts.
