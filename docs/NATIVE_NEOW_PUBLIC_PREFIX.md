# Observed Neow positive-prefix proposal

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

The existing `NativeLabelTape.BeginShuffle` hook identifies Neow's own native
positive shuffle. `ConditionalShuffleProposal.Create` receives the actual native
pool order and the two observed positive keys. It samples a uniform physical
permutation conditional on that ordered prefix and inverts native Fisher–Yates
using the existing exact 53-bit floating-point bucket boundaries. The third,
curse option is never forced: the native curse draw and extra-positive coin draws
still produce the latent pool, and complete public-packet equality still checks
the displayed curse and all remaining public history.

For pool size n, prefix length k, domain D = 2^53, and native bucket lengths B_b,
the density ratio is:

`native/proposal = (1 / (n falling k)) * product(b * B_b / D, b = 2..n)`

The global envelope is the maximum of the corresponding expression with each
bucket replaced by its maximum bucket length, over **all** certified native pool
sizes 14, 15 and 16. For k = 2 its prefix factor is `1 / (n * (n - 1))`. Correction
accepts with exactly `(native/proposal) / globalEnvelope`. It preserves every
native curse/coin path's prior mass, including multiple paths yielding the same
pool. No conditional normalizer Z is estimated, and the envelope is not selected
from the sampled pool. A valid native pool missing either observed positive is a
public nonmatch; invalid pool shapes remain engine errors/unresolved outcomes.

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
  native pool shapes and sizes 14–16, while preserving the actual third option
- Exhaustive finite raw-word laws with variable pool sizes, unequal latent masses,
  ordered two-option prefixes in both orders, and exact rational correction;
  using a sampled-pool envelope demonstrably changes latent posterior odds
- A real native hybrid opening, full public-packet equality after conditioning
  both positives, and independent continuation replay with the conditioned words

The native opening is a fixed lifecycle fixture using its original state recipe
and a changed proposal seed. It is evidence of public-prefix preservation and
replay correctness, not a posterior acceptance-rate estimate or a coverage gate.
