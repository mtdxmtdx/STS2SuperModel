# Exact public weak-slime opening formation

`NativePublicWeakSlimeFormationCondition` is a detached optional proposal for the
declared independent primitive-word tape. It composes with the existing first
normal encounter identity proposal; it does not alter encounter selection or
expand the reviewed opening catalog. The measured v5 roots 11006 and 11007 both
start with `SLIMES_WEAK`, whose native random formation was still a source of
initial-roster rejection after the encounter identity was fixed.

## Public certificate and native scope

The condition reuses `NativePublicOpeningEncounterCondition`'s complete first
normal map-combat origin certificate. It requires Silent A10, the complete
observed row-zero Ancient to row-one Monster move, the first unparented combat
owner at act zero/floor two, and a complete gap-free prefix through the first
published intents. The existing source-pinned startup closure establishes that
these weak monster types cannot change identity, disappear, or summon another
weak family before their initial publications. This uniquely identifies the
native Overgrowth `SLIMES_WEAK` encounter among the reviewed opening origins.

The new condition additionally requires exactly three physical public slots in
order `0, 1, 2`, the full ordered model roster, and the typed pre-setup entry assets.
It reads retained public history even when the root is in a later combat or the
current enemy array has changed. Missing or unsupported proof disables only this
proposal. Source seeds, source graphs, private move IDs, and current/live moves
are not condition inputs. The implementation certifies only combat index zero;
later encounters remain outside this additional proposal.

`LabelSlimesWeakScope` carries the actual hypothetical run/room from
`CombatRoom.Prepare` and encounter/RNG from `EncounterDefinition.CreateMonsters`.
One scope surrounds the unchanged `Overgrowth.CreateSlimesWeak` body. Ordinary
execution with no scope consumes the same calls and allocates no label context.
Callback sampling uses `LabelRandomScope`'s interception guard, so auxiliary
draws cannot leak into the native tape. The separate slug scope is unchanged.

Runtime guards require the attached run, sequential A10 Silent Overgrowth,
current not-yet-started Monster room, original encounter definition from that
run's act, unslotted encounter, fresh original factory RNG, correct observer
combat index and entry assets, and one complete factory invocation. Aliases,
foreign streams/owners, missing hooks, and incomplete/extra consumption remain
unresolved errors; they never become public constraint rejections. A mismatched
public entry is still an ordinary public rejection before conditioning.

## Exact native tickets and full-word law

The pinned native body calls `NextItem(2)`, removes the chosen small slime, calls
`NextItem(1)` for the other small slime, and calls `NextItem(2)` for the medium.
It returns `(smallA, medium, smallB)`. The bound-one call consumes a full word and
is retained. `NativeWeakSlimeFormationCatalog` exhausts all four native paths,
using the actual factory to obtain every ordered output roster. It never samples
empirical seeds, collapses physical slots, or deduplicates tickets by model name.
The output rosters are:

- LeafSlimeS, LeafSlimeM, TwigSlimeS
- LeafSlimeS, TwigSlimeM, TwigSlimeS
- TwigSlimeS, LeafSlimeM, LeafSlimeS
- TwigSlimeS, TwigSlimeM, LeafSlimeS

Native upper-53-bit buckets for bounds two and one have exact masses `2^52` and
`2^53`. Thus each ticket has probability `1/4`; all four sum to one. The proposal
selects a compatible ticket uniformly and independently samples every bucket's
full high-bit preimage and all discarded low 11 bits. Its middle word therefore
retains all `2^64` possibilities. For the current factory each ordered public
roster fixes one ticket, with `2^190` compatible full three-word sequences. Each
sequence has native probability `2^-192` and proposal probability `2^-190`.

The exact native/proposal ratio and root-fixed envelope are both `1/4`, so this
component's correction is identically true. The implementation retains all
compatible physical tickets if more than one has an equal ordered roster, using
`compatibleTickets/4`; no per-model deduplication is involved. Existing map,
encounter, HP, shuffle, intent, and other proposal corrections remain necessary,
as do final full-public-history and packet equality. This is a claim about the
declared independent-word law, not the game's finite common-seed PRNG prior.

## Integration contract

1. Call `NativePublicWeakSlimeFormationCondition.TryCreate(root, prior, ...)` once
   from detached public evidence and retain the condition in owned replay copies
2. Create `NativePublicWeakSlimeFormationProposal` with an independent auxiliary
   RNG and the owning tape's `ForcePrefixWords` callback
3. Attach the hypothetical run and forward every `CombatEntering` notification
4. Enter `LabelSlimesWeakScope.Enter(proposal.BeginFormation)` with nested disposal
5. Validate completion before correction, compose the exact ratio/envelope and
   call `AcceptCorrection`; keep full public replay equality

Only three fresh state-addressed primitive cells are forced. The original native
factory consumes them in its original order. All following words remain native.
No shared sampler route or profile version is changed by this isolated helper.

## Focused verification

Tests exhaust every reduced two-bit triple and all four native formation tickets,
then enumerate every compatible conditional high-bit preimage and the eight
combinations of low-bit endpoints. They also verify native 53-bit endpoints,
exact quarter mass/correction, all three draws including bound one, four native
factory rosters composed with existing HP/shuffle proposals, actual physical
slot identity, and no-scope/disabled parity.

Negative fixtures cover incomplete public origins and slots, repeated model
types that lack factory support, gaps, forced owners, different acts/encounter
instances, ownership/stream/index drift, missing or repeated hooks, aliases,
missing middle consumption, and extra draws. Existing measured roots 11006 and
11007 are reproduced as bounded lifecycle fixtures: changed auxiliary formation
words preserve the same public root, owning-tape replay copies agree at every
continuation decision, and both settle identically. Reusing these coordinates is
only an integration fixture, never posterior evidence or a throughput claim.

No new source roots, large benchmark, production data, training, or rule changes
are part of this implementation.
