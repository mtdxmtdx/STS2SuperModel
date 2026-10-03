# Public unknown-room conditional proposal

This optional accelerator conditions an initial act-zero prefix of publicly
resolved `?` nodes. Unsupported histories retain the ordinary proposal and final
public replay; this component does not exclude them from the declared prior.

## Public certificate

`NativePublicUnknownRoomCondition.TryCreate` first verifies the existing native
initial map/Neow certificate. It then follows complete, consecutive map-owner
scopes from the starting Ancient, matching each observed current coordinate to
the preceding public choice. A gap, missing owner start, act transition, or
uncertified unknown resolution ends the accelerated prefix.

For each unknown node, the immediate parentless room owner certifies Event,
Shop, or normal Monster. Elite odds begin negative and identity update hooks keep
them negative. Treasure/reward-only observations are left unsupported because
they do not provide the same unambiguous room-owner certificate.

The shop blacklist is fixed by public evidence: any Shop owner at the preceding
map point, including a nested Shop, or a complete current graph whose nonempty
outgoing set consists entirely of Shop nodes. Without either a witnessed previous
shop or a complete outgoing graph, the unknown suffix remains unconditioned.

The public map-end inventory supplies every listener in native
`RunState.IterateHookListeners(null)`: relics, potions, and deck cards. Each model
must be known, sealed, and inherit both unknown-room hook methods unchanged.
JuzuBracelet, GoldenCompass, LanternKey, other overrides, and unknown identities
therefore stop this certificate. Melted relics are conservatively included.

Native `UnknownMapPointOdds` has no ordinary production setter call between
these boundaries; the native act transition is its reset point. The supported
fresh act-zero path has neither a transplant nor an act reset. A separate scratch
instance starts from native defaults and invokes the original Roll with each
public result to reconstruct every exact float update. No source runtime odds,
run seed, random state, or hidden outcome is read by extraction.

## Exact raw-word law

The pinned native Roll converts a uniform 53-bit high word to double, then float,
and compares with `<=` against float cumulative sums in the order Monster,
Elite, Treasure, Shop. Negative odds and blacklisted Shop are skipped. Event is
the fallback. Binary search computes every boundary as the number of high words
whose native float conversion is at most that cumulative sum. This retains the
rounded float boundaries, zero-odds endpoint, and saturation above one.

Each result has one contiguous high-word interval of size n. Sampling is uniform
on all n high words and all 2^11 low suffixes, preserving the complete 64-bit
preimage. The native-to-proposal density ratio is exactly n / 2^53. The product
across certified public outcomes is fixed at extraction and is also the rejection
envelope, so correction consumes no random word.

## Native execution and guards

`LabelUnknownRoomScope` wraps only RoomFactory's Unknown case. The original
blacklist, original Roll, one raw RNG advancement, and every native odds update
remain in place. A completion marker is set only after Roll returns successfully.
The proposal checks its attached hypothetical run, raw stream, counter, native
map object, floor, coordinate, public map-owner prefix, hook closure, and exact
expected before/after odds. The tape's force scope guards full-state freshness
and aliases. Failures remain sticky even when native code catches an exception.
Unforced future cells remain the owning tape's ordinary cells.

## Focused evidence

`NativePublicUnknownRoomTests` exhausts reduced-bit native grids, checks all
53-bit interval endpoints and their neighboring words, verifies the native odds
updates and blacklist exclusions (including mixed Shop/Monster children outside
the immediate choice slice), and exercises modifier/gap fallback, native
ownership, missing words, and aliases. The retained source24002 fixture resolves
floor 3 as Event and floor 5 as Monster, reproduces its entire detached public
packet, and matches a replay copy through terminal settlement after independently
resampling the two compatible raw words. This is an owning lifecycle and exact-law
fixture, not a posterior admission or acceptance-throughput claim.
