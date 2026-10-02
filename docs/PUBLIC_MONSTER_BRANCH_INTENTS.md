# Public monster weighted-branch proposal

`NativePublicMonsterBranchIntentCondition` and `NativePublicMonsterBranchIntentProposal`
add optional acceleration under the declared independent native-state-word tape law.
They do not define a conditional sampler for the finite native seed prior, filter the
source-root population, replace native transitions, or relax final public equality.
Source and tape composition remain caller-owned.

## Eligibility and detached inputs

The condition contains only public combat-owner ordinals, initial roster model names,
public lifetime slots, successive published turn ordinals, public shape categories,
intent event ordinals, and the existing detached entry-assets JSON. It never receives
the actual source seed, source graph, native move IDs, state log, or weight delegates.

The existing public-combat startup certificate supplies a complete ordinary map-combat
entry and startup closure. Event-parented/incomplete owners are not certified. The
first player-turn publication has contiguous slots `0..n-1`. Later targets prefer
`NativePublicReshuffleCondition.CombatAudits[index].ThroughEventOrdinal`, constructed
inside Create from that exact same detached root. The audit advances only after each
source-certified transition succeeds and excludes the first unproved event. A missing
audit retains `NativePublicDrawPrefixAudit.ThroughEventOrdinal`; independently proved
startup is preserved even if either later scan stops early. Turn ordinals must be
successive, with contiguous publication blocks, unique increasing lifetime slots and
unchanged model identity. A malformed block stops acceleration before that block.
Every original public event and numeric intent remains a final native replay constraint.

Supported public categories uniquely distinguish the pinned sealed graphs:

- SludgeSpinner: Attack+Debuff, Attack, Attack+Buff
- LeafSlimeS: Attack, StatusCard

Attack damage is deliberately not an internal branch identifier: Weak, Strength and
native arithmetic determine it. Repeat count and kind sequence still identify the
branch; complete public equality subsequently checks actual damage too.

Current extension scope is SludgeSpinner later rolls through the source-certified
transition scan, including across native reshuffles, plus LeafSlimeS initial rolls. The implementation/kernel can condition later
Leaf rolls once a separately reviewed transition certificate includes their turns.
The current draw closure rejects Leaf enemy turns and generation, so those later
publications remain ordinary native replay. In particular this implementation does
not silently claim a certificate for generated Slimed cards. The bridge reuses the
reviewed reshuffle scanner's transition proof; no caller-supplied endpoint or arbitrary
shuffle marker grants eligibility. The measured SludgeSpinner event 54 and later
post-reshuffle publications in retained source root 11004 are covered; initial Leaf
targets in retained roots 11006/11007 are also covered. These source IDs appear only in tests, never eligibility or features.

## Why the transition boundary also fixes roll ownership

The source-pinned startup closure reviews constructors, initial branching, state
entry/exit callbacks, AfterAddedToRoom, all active AbstractModel hooks, draw hooks,
card modifiers, and the initial roster. The additionally consumed transition closure
reviews every action and enemy continuation before its certified endpoint, and its
reshuffle continuation preserves the same action and listener restrictions. Its
Strike/Defend/Neutralize/Survivor/Backflip/Deflect/Mirage plays, Fire/Block/Energy/
Strength/Swift potions, and Weak/Frail/Strength/Ravenous powers cannot grant an extra
player turn or reroll/replace a SludgeSpinner or LeafSlimeS intent. Survivor's Sly path
is explicitly excluded. CorpseSlug's private Ravenous transition affects its own
state machine; it cannot alter either supported model. The reviewed entry relic
hooks change draw count, energy, counters, or map use; post-combat hooks and LavaRock
reward changes cannot execute within this pre-settlement prefix.

The ordinary engine calls RollMove once for each surviving enemy at the start of a
normal player turn, before drawing and before PublicKnowledge publishes intents.
The closure prevents hidden intervening replacement. Consequently the nth certified
publication for a surviving slot belongs to its nth actual RollMove, not its nth
random branch call. Sludge's deterministic first roll consumes no branch word; Leaf's
first roll does. Explicit native roll boundaries and per-creature ordinals preserve
that distinction. No offset is guessed from repeated IntentPublished events.

## Native boundary and purity

The optional `LabelMonsterMoveScope` surrounds `MonsterModel.RollMove` and marks
completion only after the original NextMove assignment. Inside RandomBranchState it
records each original Sum getter result, then calls the proposal before the original
NextFloat. The original weighted traversal still invokes GetStateWeight, subtracts
in native order and uses `<= 0`. Each visited traversal result is checked against the
already captured result. No weight getter is evaluated on behalf of the proposal.
Callbacks execute off the random tape using the existing resource-callback guard.
With no scope, the added helpers simply return the original values; native getter
order/count, RNG conversion, advancement and state changes are unchanged.

Runtime ownership checks include the exact hypothetical RunState, current CombatRoom
and CombatState, single player, MonsterAi stream, ordinary sequential mode, complete
initial roster and stable creature references. Native graph checks pin the sealed
SludgeSpinner and LeafSlimeS ordered moves, ordinary MoveStates, constant pure
CannotRepeat branches, zero cooldown, graph size, log length and performed-first-move
phase. Effective captured weights must equal the native last-log restriction. These
are hypothetical replay facts used for proposal likelihood, never source features.

The tape's existing ForcePrefixWords must enforce exact stream/state, preexisting-cell
consistency, full word consumption and unresolved aliases. The proposal independently
checks one branch and one primitive draw per weighted roll, zero for deterministic
startup, one completion per public target, and total target counts. Missing, partial,
repeated, foreign or unowned callbacks cannot masquerade as successful conditioning.
After all targets in a combat complete, later native rolls remain unrestricted.

## Exact word law and fixed envelope

For one draw, let H range uniformly over `0..2^53-1`, with the discarded 11 bits
independent and uniform. The kernel computes precisely the native expression
`float(double(H) * 2^-53 * double(totalWeight))`, followed by each ordered float
subtraction and first `residual <= 0`. The supplied total is the original float Sum
result, not a separately rounded cumulative sum. Nonnegative finite weights make
the selected index monotone, so two integer binary searches find each exact preimage.
A possible no-selection tail remains outside every bucket.

The kernel samples the selected high-bit bucket uniformly using unbiased rejection
and adds independent discarded low bits. Its exact native/proposal ratio is `b/2^53`,
where b is the bucket size. It preserves both float-rounding bias and the nonzero
zero-weight endpoint: a first branch with effective weight zero still wins at H=0.
It never substitutes equal uniform branch probabilities.

For initial Leaf rolls the public root fixes weights `[1,1]`; the envelope is that
selected branch's exact mass. For later supported rolls, the public root fixes that
there is a last move and all branches have constant base weight one with CannotRepeat.
Exactly one effective weight is zero. The envelope size M is the maximum selected
bucket size over *every* possible last-move index, independent of the sampled log.
The correction accepts with exact probability `b/M`. Thus the accepted proposal
contribution for each compatible raw word is proportional to its original native
mass by one root-constant factor `2^53/M`; no per-world normalizer is silently used.
Products of these fixed factors compose over all certified public targets. The code
supports latent last moves even when public history could provide a tighter proof.

## Verification

- 16 exact kernel tests: exhaustive reduced-precision words; real NextFloat production
  boundaries; all discarded-bit patterns for tiny 53-bit buckets; zero-weight endpoint,
  float bias, extreme finite weights, no-selection tails, frozen inputs and correction
- 26 condition/native tests: retained measured targets including post-reshuffle
  publications; same-root audit bridge with gap and unsupported-transition stops;
  detached history; initial and
  later native rolls; stable owner/stream/counter/alias checks; missing/partial scope;
  native zero-word CannotRepeat repeat and its nontrivial global correction; ambiguous
  public history; unchanged weight evaluation count and callback isolation
- 54 affected Core monster/state-machine, forced-transition, LeafSlimeS, Underdocks
  weak-monster and combat-clone regression checks pass

Full source/tape composition, outer retry accounting, aggregate suites and bounded
combined throughput are performed by the integrating caller. No acceptance or
throughput claim follows from eligibility or focused tests alone.
