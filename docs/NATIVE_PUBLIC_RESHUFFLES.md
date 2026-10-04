# Publicly witnessed reshuffle proposals

`nosl.public-witnessed-reshuffle.v2` is optional acceleration under the existing declared ideal primitive-tape prior. It does not change the public observation channel, finite run-seed prior, native source collection, or corpus admission. Reshuffle draws remain separate from the original first-cycle prefix. Independently versioned transition-closure support additions are documented in `NATIVE_PUBLIC_FIRST_DRAW_CYCLE.md`; this condition currently shares its v9 closure.

## Joint public witness

The detached condition reuses the reviewed transition closure in `NativePublicDrawPrefixCondition`. Only the existing exact card, potion, monster, power, relic, and Sharp implementations are admitted. In particular, arbitrary `CardMoved` operations are not public facts, so a quiet log never substitutes for that source review.

An admitted `Shuffled` fact must occur in an ordinary draw-producing continuation after its preceding pile has emptied. A new cycle initially has no certified pool. At a subsequent stable public decision, the same closure must establish that only ordinary `Draw` removed cards from that shuffled pile. The decision must expose every remaining identity through `UnknownDraw`, with no known positions, unidentified cards, or inconsistent counts. The input `(Id, Upgrade)` multiset is then exactly the remaining public multiset plus the ordered draw facts since that shuffle. Its counts cannot exceed the entry inventory plus explicitly witnessed plain Slimed generation from the v4 closed slime-turn path. That inventory is only an upper bound; the joint snapshot fixes the exact pool even when held or exhausted membership differs.

This is a joint certificate for pool and prefix. It never infers an input pool from a sampled hidden inventory. Hand retention, ordinary discard, Ethereal exhaust, a playing Backflip, and duplicate physical variants can affect actual pool membership; the post-shuffle public witness fixes the required public signature multiset without reconstructing that membership.

After the witness, later certified draws can extend the same prefix. An unsupported transition, evidence gap, uncertified generated card, or missing witness stops extension. A second shuffle before the first stable witness leaves the pending cycle unconditioned. Previously certified cycles remain usable. Unsupported content remains in the prior and follows ordinary native replay rejection.

The native ordering matters. `CardPileCmd.Shuffle` combines discard and remaining draw, calls `StableShuffle`, applies enchantment shuffle-order callbacks, installs the resulting draw pile, publishes `Shuffled`, then dispatches `Hook.AfterShuffle`. `Draw` publishes each drawn card before both `AfterCardDrawn` passes. The source closure therefore covers callbacks on both sides of the public facts; Sharp does not reorder or draw.

## Exact physical law

For a witnessed pool of size `n` and public signature prefix `a[0..k)`, write

`z = product_i remaining_count(a[i]) / (n-i)`.

There are exactly `n! * z` compatible physical permutations. Equal IDs, upgrades, and enchantments remain distinct physical indices. The proposal samples those permutations uniformly, inverts each actual Fisher–Yates path, and samples raw words inside the exact native floating-point index buckets.

For each descending bound `b`, let `B_b` be the selected bucket size and `M_b` the largest bucket size, with `D = 2^53`. The exact native/proposal ratio is

`z * product_b (b * B_b / D)`.

A root-constant envelope is

`E = z * product_b (b * M_b / D)`.

The correction accepts with `product_b B_b/M_b`. The combinatorial `z` does not assert uniform native index conversions; those native biases are retained by the bucket factors.

The pool's physical variants and native sort ties can remain latent. They change the actual permutation and selected buckets, but not the publicly fixed size, ID/upgrade multiplicities, prefix, or envelope. Native sorting runs before the proposal sees the physical list. No public-ID sort replaces native `CardModel.CompareTo`, which compares ID then upgrade. Independent native transitions compose by multiplying their ratios and root-fixed envelopes, even when earlier draws affect later physical membership. Complete public replay equality still checks all remaining metadata and evidence.

If pool multiplicities are not fixed by a public witness, a sampled-pool envelope is invalid in general. Pools `[A,A,B]` and `[A,B,B]` have different likelihoods for drawing `A`. Dividing by each pool's own envelope removes those latent odds. This implementation leaves such cycles unconditioned; it does not estimate a normalizer or claim a root-wide envelope from observed samples.

## Native boundary and failure accounting

The optional `LabelCombatReshuffleScope` marker is active only when explicitly entered by label execution. Its lexical boundary surrounds exactly `CardPileCmd.Shuffle`'s existing `combined.StableShuffle` call. The generic shuffle callback observes that actual list after native sorting. Other card-list shuffles on the same RNG, including `Uproar`, `BeatDown`, and `Catastrophe`, cannot consume a reshuffle target.

The proposal verifies the hypothetical run, live combat, player, sequential shuffle RNG, combat sequence, reshuffle ordinal, complete physical discard/draw union, and detached public ID/upgrade pool. It forces only the native shuffle words through the caller's RNG-state-validated forcing scope. Already visited cells, conflicting replay overrides, and changed word consumption remain unresolved. Native counters advance before raw callbacks, so counter equality alone cannot establish success. A target completes only when the exact marker acknowledges that `StableShuffle`, including its forcing scope, returned normally. A final-word error or a forcing-disposal error receives no success acknowledgment. `ValidateCompletion` runs before any randomized correction, so missing targets cannot disappear into random rejection.

A witnessed pool mismatch or impossible prefix is a public-constraint rejection of the complete outer attempt: the detached closure proves that world cannot yield the observed root. Missing or malformed call boundaries, alias conflicts, skipped/extra words, native faults, and incomplete target execution are errors, never ordinary rejection, partial restart, or content exclusion. While unwinding an aborted forcing callback, incomplete plans remain unfinished rather than masking the original exception. Ordinary source execution does not activate the marker or force any words.

The owner reconstructs this proposal with independent proposal randomness and immutable detached conditions on `ReplayCopy`, preserving the accepted hypothetical tape overrides. No original native source graph, hidden order, private seed, or audit payload is a condition input.

## Integration contract

Create `NativePublicReshuffleCondition` from the detached public packet. When it has targets, create `NativePublicReshuffleProposal(condition, nextWord, forcePrefixWords)` using a separate proposal RNG namespace. Enter its scope with label execution, attach the owned hypothetical run, notify every combat entry, and dispatch its `BeginShuffle` before the existing startup proposal. Compose `ValidateCompletion`, `AcceptCorrection`, exact ratio/envelope, and target counts. The generic force delegate must retain native-state identity, fresh-cell checks, override consistency, and full prefix consumption. The proposal additionally validates exact shuffle counter advancement.

`CombatAudits` exposes the last certified event and stop reason for the shared transition walk. This is a draw-pile closure; another proposal consuming this boundary must separately establish its own mechanism-specific source proof.

## Focused evidence

Finite tests enumerate all 1,024 native raw-word worlds for two composed cycles where an earlier retained `A` makes later physical variant membership latent. They compare all 72 coarse and 24 ID/upgrade-refined compatible physical outcomes and every correction outcome with the native mass under a single root-constant envelope. Existing finite duplicate-ID/bucket tests remain unchanged.

Public fixtures cover absent witnesses, gaps, uncertified generation, another shuffle before the witness, excess inventory, unidentified cards, and known positions while retaining the original first-cycle outputs. Native boundary fixtures distinguish an unrelated shuffle of the same physical cards and RNG, verify the exact owned command, and exercise missing scopes, pool mismatches, forcing errors on the first/final word, two-card failure, disposal failures, extra draws, completion-before-correction, and scope cleanup. Successive native cycles also verify different witnessed pool sizes when cards drawn before the later reshuffle remain in hand.

Retained native development histories are detached through JSON before conditioning. Source11002 witnesses a 12-card pool at event80 after shuffle76, with prefix `[DefendSilent]` and `z=5/12`. Source11004 witnesses an 11-card pool at event82 after shuffle75; certification extends its first four later draws to nine before combat settlement, giving `z=1/6930`. These are mechanism checks, not fresh independent posterior samples or production admission.

V4 additionally executes a real conditioned native reshuffle containing generated Slimed with null DeckVersion. Only this source-certified plain status may lack a persistent deck origin; ownership, distinct physical references, exact pile union and marker identity still apply. Native generated copies remain distinct physical permutation indices, just like duplicate deck cards. Their origin adds no proposal factor, and the fixed public ID pool keeps the exact correction envelope independent of latent physical membership. All incompatible-pool and unsupported-generation mass remains in native replay rejection.
