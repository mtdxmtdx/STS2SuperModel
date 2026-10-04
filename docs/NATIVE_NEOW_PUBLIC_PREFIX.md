# Observed Neow opening proposal

This development-only acceleration belongs exclusively to the separately declared
`nosl.native-rewards-state-tape-prior.v1` law. It does not migrate the legacy v7
prior, implementation identity, or corpus. It does not certify the game's finite
common-seed posterior and does not enable production collection or formal training.

## Public certificate

`NativeNeowCondition` can use both ordered positive options when the typed public
evidence begins with these four uninterrupted records:

1. Observed `run_started`, Silent A10
2. The first owner, a complete event at act index 0 / floor 1, without a parent
3. Three displayed, unlocked, unpriced options: two distinct native positive keys
   followed by one native curse key
4. The same owner's choice linked to that offer, selecting its first positive

The fixed fresh-run prior establishes that this first Ancient is Neow:
`ActDefinition.GetRandomList` starts with Overgrowth or Underdocks, whose initial
Ancient pools contain only Neow, and `RunState.GenerateAllActRooms` supplies no
shared Ancient at index zero. `RunDriver.RunAsync` invokes
`DriveAncientStartingRoomAsync` before map travel. Both declared outside-combat scripts select the first unlocked
initial option. The event recorder emits the owner, options and linked choice at
that public boundary. No private event ID, source trace, actual seed, or hidden
state is read to establish this certificate.

The three option keys and their locks are checked against the source-pinned native
vocabulary. `PublicRunEvidence` already validates unique keys, contiguous observer
ordinals and same-owner offer/choice links. An explicit gap before the initial
options or their linked choice cannot qualify. A gap later in the run does not
invalidate observations already recorded. The selected relic need not remain in
the combat-entry inventory: its public offered/selected origin is already known,
including after removal or melting. This extension changes only the Neow proposal;
other structural certificates retain their own asset checks. The hybrid source
does not enable the legacy first-reward proposal, and its tape rejects that proposal;
broader Neow origin evidence does not certify hybrid reward inversion.

Missing or uncertified evidence falls back to the existing single retained-positive
necessary predicate where available, otherwise ordinary native rejection. No
content or root is dropped. A legacy prior always uses the original path even if
typed evidence is attached.

## Proposal and correction

A label-only `LabelRandomScope.BeginNeowInitialOptions` boundary exposes Neow's
actual allowed curse pool and its own RNG before the curse and extra-pair rolls.
It surrounds only those native rolls; normal generation, filtering, draw order,
counters and shuffle remain unchanged. The worker verifies the source-pinned
ordered ten-curse pool, then samples the observed third option's exact native
53-bit index bucket. Only an extra-pair coin needed to include an observed
positive is conditioned. Other coin words come from the ordinary full-state
oracle, including any prior aliases. Native `Rng.NextBool` means `Next(2) == 0`:
the first member of each pair takes bucket zero. LargeCapsule skips the entire
LavaRock/SmallCapsule pair, exactly as in native generation.

`NativeLabelTape.BeginShuffle` still identifies Neow's own native positive
shuffle. `ConditionalShuffleProposal.Create` receives the actual native pool
order and both observed positives. It samples a uniform physical permutation
conditional on that ordered prefix and inverts native Fisher–Yates using exact
53-bit floating-point bucket boundaries. Complete public-packet equality still
checks all three displayed options and the remaining public history.

Let D = 2^53, C be the observed curse's exact bucket length, r the number of
required extra-pair coins, n the native positive-pool size, k the prefix length,
and B_b the actual shuffle bucket lengths. The opening density factor is
`L = (C / D) * (1 / 2^r)`. It depends only on the certified public root, never on
an unrequired latent coin. The joint ratio is:

`native/proposal = L * (1 / (n falling k)) * product(b * B_b / D, b = 2..n)`

The joint global envelope is L times the maximum shuffle expression with each
bucket replaced by its maximum length over **all** certified native pool sizes
14, 15 and 16. For k = 2 the prefix factor is `1 / (n * (n - 1))`. Correction
accepts with exactly `(joint native/proposal) / joint globalEnvelope`. The common
L cancels in that Bernoulli probability, but remains explicit in both the joint
ratio and envelope. No conditional normalizer Z is estimated, and no envelope is
selected from the sampled pool. Each native unrequired coin path retains its
prior mass, including multiple paths yielding the same pool.

Impossible public extra-pair combinations, a required suppressed pair, or a
valid native pool omitting an observed positive remain public nonmatches. Invalid
native pool shapes or changed allowed curse order are engine errors/unresolved
outcomes. Opening replay validates every native pre-draw state and complete roll
count. Every forced word must be a fresh full-state oracle cell; a previously
visited forced cell is an unresolved alias error, never rejection or resampling.
Replay copies must reuse exactly the same conditioned words. Untouched coin
words retain ordinary oracle lookup and are not registered as conditioned cells.
The observed-opening path is available only under the hybrid prior; uncertified
or legacy roots retain their earlier single-positive path unchanged.

`RootEnvelope` keeps its original single-positive value. `MaximumEnvelope` keeps
the existing call signature behavior through `prefixLength = 1`; its optional
third parameter enables two-prefix correction. The original one-positive plan's
raw words, physical permutation and ratio remain identical.

## Verification

`NativeNeowPublicPrefixTests` checks:

- Typed public boundary, removed/melted inventory, later gaps, and conservative
  fallback for incomplete, locked, unknown or incorrectly selected initial offers
- Unchanged legacy identity and one-positive proposal bytes, including absent
  evidence and typed evidence attached to the old prior
- All ten native curse branches and all three-coin assignments, covering all 52
  native pool shapes and sizes 14–16, followed by every curse with zero, one or
  two required coins, both pair polarities, LargeCapsule suppression, untouched
  native coin choices, and unchanged native RNG advancement
- Exhaustive finite raw-word laws with variable pool sizes, unequal latent masses,
  ordered two-option prefixes in both orders, and exact rational correction;
  using a sampled-pool envelope demonstrably changes latent posterior odds
- Exhaustive finite opening raw words composed with shuffle permutations and
  exact correction, preserving each compatible latent coin path's joint mass
- Forced opening aliases fail closed, untouched coin aliases remain native,
  impossible public pair combinations reject, and allowed-pool drift is an error
- A real native hybrid opening, full public-packet equality after conditioning
  both positives, and independent continuation replay with the conditioned words

The native opening is a fixed lifecycle fixture using its original state recipe
and a changed proposal seed. It is evidence of public-prefix preservation and
replay correctness, not a posterior acceptance-rate estimate or a coverage gate.
