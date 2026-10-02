# Reviewed conditional-choice belief sampler

Profile: `reviewed-stable-origin-conditional-choice-v1`.

This extends the existing 24-card conditional-permutation / independent-future-RNG model to
pending choices. It does not clone a suspended coroutine, infer the finite source seed, widen
reviewed content, or permit formal labels/training. Native and arbitrary general/setup choices
retain their previous conservative behavior.

## Stable origin and ownership

Immediately before a legal play of Survivor, Prepared, Acrobatics, ThinkingAhead or DaggerThrow,
`CombatSession` checks the existing exchangeable-family predicate and excludes native sessions.
Only then does it retain an owned detached exact snapshot of that stable player boundary. No
other cards/potions/relics/enemies are admitted. The shared predicate also now pins the declared
initial Enemy/Enemies prior to one of the four reviewed single enemies; killing all but one
enemy in an originally broader encounter cannot turn it into a reviewed family. Checking the original Scenario's declared entry
inventory prevents unsupported consumed content from becoming eligible later.

The snapshot is an execution primitive, not a belief sample. It carries the full public history,
known physical draw-position bindings, public current state, and exact execution context. If the
play produces no pending choice, it is immediately discarded. While a choice is pending, the
session records every intervening action (with detached selection arrays) and every complete
public packet. This is the nearest earlier stable boundary; subsequent stable decisions release
the previous origin and capture a new one only when another reviewed choice-producing play starts.

The origin, suffix and coroutine are private worker state and never enter policy DTOs. One
session owns one origin; there is no shared/ref-counted coroutine or chain of ancestor snapshots.
Origins are disposed after reaching a stable/terminal boundary, on action failure, or when the
session is disposed. Rejecting a proposal cancels only its own pending selection/operation.

## Conditional sampling argument

Let O be the complete public observation/history at the retained stable boundary, and H the
hidden draw order and future random streams. The existing sampler supplies its documented
idealized distribution P(H | O): canonical conditional permutations respecting every known
position, plus independently replaced future run/player/monster streams. All other relevant
state in this reviewed family is public-determined.

For each proposal:

1. Fork the stable origin and resample all supported hidden order/future streams there
2. Execute the recorded action suffix on this sampled world
3. Compare every complete public packet, including intermediate choices, public history,
   hand order, all candidate actions and revision tokens
4. Accept only an exact match; preserve the accepted generator states unchanged

Rejection sampling therefore conditions the stable-origin model on the observed suffix. Source
private order/future RNG is never used to decide the suffix, and no reseeding occurs after the
observed draw/choice has been conditioned upon. The actual Scenario seed is not consulted by
this path. The proposal stream is separately named `nosl-independent-choice-prior-v1`.

A Survivor play reveals no new hidden draw, so each valid proposal matches. Prepared/Acrobatics/
ThinkingAhead/DaggerThrow can reveal cards and genuinely reject proposals. A finite attempt
budget can still exhaust; it remains computationally inconclusive, never a fabricated loss,
empty action set, easy-root substitution or zero-valued outcome.

The retained origin's and current choice's packets must still match their saved suffix. Missing
or changed public history fails closed. Existing known-position constraints are retained before
sampling, rather than reconstructed from the already-revealed choice candidates alone.

## Candidate branches retain accepted sampled provenance

Every accepted pending world owns its sampled stable origin and the matching suffix. A candidate
branch clones that origin and re-executes the suffix exactly, thereby creating a fresh coroutine
and the same accepted pre-choice RNG state. It never calls CreateAsync with the original Scenario.
Each branch has distinct run/player/monster RNG objects; sibling execution and parent disposal
cannot mutate or cancel it. Across actions, one evaluation world deliberately uses common random
numbers; different evaluation seeds produce separate proposal streams/worlds. The teacher's
existing disjoint exploration/evaluation seed validation remains unchanged.

`ForkExact` still rejects suspended coroutines. `ForkForContinuationAsync` and `ReplayToChoiceAsync`
use the owned origin only when this reviewed provenance exists. Generic whole-setup sampling
still rejects in-place sampled worlds; unsupported in-place choices cannot fall back to their
original Scenario seed. Native choices are not admitted by this change.

## Tests and bounded diagnostic evidence

New tests check:

- Hidden source order/RNG replacement, identical same-seed public strategy/worlds, and independent
  RNG objects across candidate branches
- Re-sampling an accepted choice agrees with sampling the original public choice at the same seed
- Child choices survive parent disposal and release their origins after resolution
- Prepared with three distinct unknown draw cards: exact prior has six permutations; its observed
  draw leaves exactly two equally weighted orders. 240 sampled worlds match this conditional
  support/frequency, while one-attempt failures retain the expected rejection mass
- A Prepared choice crossing an empty-draw/discard reshuffle: exact conditional support/frequency
  and unchanged accepted RNG states when re-executed for independent candidate forks
- Known-top physical card constraints, complete public suffix matching and explicit history rejection
- Every legal teacher candidate retained; broader initial inventory and native choices remain blocked
- The old blanket sampled-choice prohibition is replaced with owned-provenance checks while retaining
  explicit failure for unreviewed in-place content and direct whole-Scenario fallback

At a fixed snapshot of the running phased-v3 logs, all nine recorded starter/first-choice deadline
failures were replayed only in this isolated checkout, with their exact Scenario, source decision
index, public source policy and four original evaluation seeds. All were genuine Survivor choices.
With one posterior proposal per world, all 84 allocated action-worlds settled and all value targets
resolved. Teacher RPC time was 0.124–3.464 seconds (median 0.306), versus the recorded 45–47-second
request deadlines. All source packets stayed unchanged. These trials added zero corpus rows and
performed no training. The attempt log snapshot and full diagnostic outputs remain in ignored
`artifacts/`; `CONDITIONAL_CHOICE_PROOF_SUMMARY.json` preserves the concrete denominator and results.

## Integration limits

This is not just the earlier rule-equivalent map optimization: pending reviewed choices now use
an explicitly identified extension of the ideal exchangeable model, instead of the finite
whole-setup replay prior. Outputs need not equal that different prior's outputs. Keep existing
corpora/workers frozen and record the new profile/binary provenance in a separately audited stage.
No strong-pair utility-support certificate is expanded; choice-root strong rankings stay masked.
Some draw-conditioned choices can still have low acceptance and must remain inconclusive when
bounded sampling fails. No claim is made for arbitrary native, generated-content, or setup choices.

Final verification: 879/879 NOSL tests passed, including all six new conditional-choice tests,
updated bridge safety guards and existing native exclusions. The separate-process protocol smoke
also passed. No CombatTeacher, source generator, active dataset, vendor or default-main binary
was changed by this implementation.

An independently built pre-change `6bfe268` worker was compared against the changed worker for
all nine timeout scenarios: every public packet/action trace, physical source draw order and
run/player/monster RNG state matched exactly at the pending choice. The comparison used local
audit-only hashes; no private values were supplied to a policy.

`BeliefSampler.PosteriorProfileFor` supplies audit-only identities for stable exchangeable,
conditional choice, whole-setup rejection, native certified, and unsupported-provenance cases.
Persisting that identifier plus teacher scope/warnings in the external audit record is an
integration prerequisite owned by the parent task; this patch does not modify teacher/record
schemas or student inputs. Independent review identified and prompted the declared-enemy
prior guard; a two-enemy-to-one Survivor regression prevents that scope loophole.
