# Constructed Ruby startup proposal

This optional proposal accelerates the existing declared constructed native-tape
prior for `RubyRaiders`. It changes proposal generation only. The prior schema,
source setup, independent Map/Rewards/full-native-state partitions, source draw
domain, source stopping rule, all public evidence, and failure accounting stay
unchanged. It does not assert natural-run reachability or training admission.

The original mixed-deck breadth diagnostics remain frozen. At source draw 44101,
both the opening and turn-2 roots exhausted two evaluation worlds (74101, 74102),
each with 16 attempts. Per root, 24 proposals first failed initial roster
matching, seven failed the first published intent (event 12), and one failed the
second (event 13). The opening public roster is Brute, Tracker, Crossbow in that
physical order. This is viewed development evidence, not a new unseen cohort.

## Native source and exact law

The pinned simulator's `Overgrowth.CreateRubyRaiders` constructs five candidates
and executes three `Rng.NextItem` calls, removing the chosen candidate after each
call. `NextItem` calls `NextInt(0, count)`, then `MegaRandom.Next` and
`NextDouble`. Thus the three native bounds are 5, 4, 3. No extra draws, reseeding,
reordering, monster mutations, or recorder changes are introduced.

The native `GenerateMoveStateMachine` methods for `AxeRubyRaider`,
`AssassinRubyRaider`, `BruteRubyRaider`, `CrossbowRubyRaider`, and
`TrackerRubyRaider` have deterministic initial moves. The reported intent
mismatches were therefore formation-order mismatches, not an independent random
intent choice. Only the factory needs conditioning.

The finite catalog executes all 60 native tickets, preserving every output slot.
An instance-local native draw observer checks the exact overload, bounds,
ordinal, and result of each call. Catalog construction fails if the reviewed
three-call shape changes. Every ordered roster has exactly one ticket.

For each fixed public roster, let the corresponding exact native 53-bit bucket
widths be b5, b4, b3. Its native mass is

    M = (b5 / 2^53) * (b4 / 2^53) * (b3 / 2^53)

The plan samples uniformly within each complete native bucket and independently
retains all 11 discarded low bits in each 64-bit word. Bucket boundaries use the
existing IEEE-multiplication-aware helper. The mass is not replaced with 1/60.
The proposal's native/proposal density ratio is M, and its envelope is the same
root-constant M. Consequently this factor requires no extra Bernoulli rejection;
the existing HP and shuffle corrections still apply unchanged.

The exact-state tape force scope binds the three words to the hypothetical
native factory's own stream and successive states. A previously visited cell,
foreign stream, changed replay value, omitted callback, incomplete consumption,
or extra factory draw remains an unresolved execution error. It cannot be
converted into an accepted sample or a game loss. All other prior variables and
future random cells remain on their original native tape law.

## Public certificate and version boundary

The input is only the frozen declared prior and detached public packet. The
existing constructed startup certificate establishes the one complete combat
owner, typed entry assets, first published intents, and absence of uncertified
startup hooks. The ordered roster comes from those first three intent facts;
later snapshots, source recipes, source seeds, source tapes, and private monster
move IDs are not conditioning inputs. Missing proof disables acceleration.

At native execution the proposal also checks the exact run, current room,
encounter definition reference, unslotted Overgrowth factory, Silent A10,
factory RNG identity, unused counter, entry assets, and one-time completion.
The full original public packet must still match exactly before acceptance.

Old implementation/profile IDs are retained for old reports. Newly executed
ordinary Ruby constructions use:

- `nosl-constructed-native-tape-ruby-formation-v2-public-evidence-v2`
- `owned-constructed-native-tape-ruby-formation-v2-public-evidence-v2`

Other ordinary constructions retain v1. When composed with the separately
versioned event-owner extension, event-owner dispatch takes precedence; that
path retains its plain-rejection sampler. Neither a Ruby version string nor
successful bounded examples authorize production or fitting.

## Verification

`NativePublicRubyFormationTests` enumerates all 512 three-bit native word
triples and their 60 unequal formation masses. For each roster it enumerates
every compatible finite conditional word triple and checks exact mass and
support. It separately checks both true 53-bit bucket endpoints and all
combinations of discarded-low-bit endpoints, including native factory replay.

Native tests verify ordinary disabled-hook parity, off-tape callback sampling,
strict owner/room/stream/phase guards, alias and consumption failure handling,
detached later-root startup evidence, full packet equality, and owned replay
forks. The original mixed deck, source draw, evaluation seeds, and 16-attempt
limits are retained for both opening and turn-2 checks. Turn-2 rejection after
startup remains possible and is not reclassified as impossible game content.

The affected Core suites cover RNGs, encounters, combat rooms, driver lifecycles,
and all five Ruby models. A full Core rerun is not claimed by this change.

At code checkpoint `b8d36e2`, the full Worker suite passes 2,307/2,307; the
affected Core slice passes 217/217 and the five Ruby model suites pass 20/20.
The retained original-spec raw rerun accepts both opening worlds on attempt one
and settles all 44 candidate branches. Turn 2 still exhausts both worlds: 21
proposals reject at CardDrawn event 42 and 11 at event 43. Every original public
input byte, prior identity, source-generation JSON, and source-family alias is
unchanged. Both raw runs overlap the full Worker suite; their elapsed times are
nonexclusive audit records, not a speed comparison. There are no replacement
draws or additional raw runs. See
[the exact verification record](../configs/native_constructed_ruby_formation_verification.json).

The cumulative local vendor patch reconstructs all 2,244 tracked Core/Core.Tests
files byte-for-byte. The four Ruby hook files are its only source changes from
the preceding package; 60 prior patch sections and the map patch are unchanged.
Reproduction details and focused-test limits are in
[the vendor record](../configs/vendor_ruby_formation_verification.json).
