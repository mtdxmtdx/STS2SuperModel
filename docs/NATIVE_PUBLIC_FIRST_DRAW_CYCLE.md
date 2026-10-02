# Public first draw cycle certificate

`nosl.public-first-draw-cycle.v1` extends the explicit public-combat startup certificate to the observed first draw cycle. It changes only proposal acceleration under the declared independent primitive tape law. Legacy `NativeInitialShuffleCondition.TryCreate`, the v2 public startup entry point, the run-seed prior, public packet schemas, and final equality checks keep their existing behavior.

## Why draw facts alone are insufficient

`PublicKnowledge.CardMoved` intentionally omits arbitrary pile moves. For example, `ThinkingAhead.OnPlay` draws two cards and then returns a selected card to the top without publishing a move fact. `PotionUseFinished` publishes only after potion effects. Consequently neither a run of `CardDrawn` facts nor absence of `Shuffled` proves that later draws are positions of the original shuffle.

The certificate starts at the first stable public combat decision after certified startup. It walks the original typed owner history, linking every action to its immediately preceding public decision. Before crossing an action, it proves that the action's entire continuation can only remove cards from the remaining initial pile through ordinary `CardPileCmd.Draw`. Unknown transitions stop the extension permanently; they do not erase the already certified startup prefix or exclude content from the underlying prior.

## Source closure

The proof is pinned to the vendored simulator sources, particularly `CardModel`, `CardCmd`, `CardPileCmd`, `CombatEngine`, `Hook`, and the complete concrete implementations named below. It does not inspect an original native object graph, original hidden order, seed, source trace, or audit payload.

- Startup retains the existing reflection check of all entry cards, relics, and potions against `AbstractModel` hooks and the reviewed startup monster closure. Reviewed relic hooks only alter counts, energy, or counters across subsequent turns as well
- Played cards are the exact sealed `StrikeSilent`, `DefendSilent`, `Neutralize`, `Survivor`, `Backflip`, `Deflect`, and `Mirage` types. Their effects and upgrade branches only change damage, block, Weak, current-hand discard, or ordinary draw. The generic `GeneratedCardModel` base is not admitted; only the Strike/Defend specs and their absent generated-power mapping are reviewed
- Survivor is rejected before execution if any current hand card has permanent or temporary Sly, including automatic single-candidate selection. `CardCmd.Discard` can otherwise autoplay it without another public action. Pending choices must be the same admitted Survivor continuation
- Potions are checked before their action, using the selected public potion slot. Only exact Fire, Block, Energy, Strength, and Swift potion implementations are admitted
- A turn-ending action requires every current hand card to inherit `CardModel`'s false `HasTurnEndInHandEffect` and no-op `OnTurnEndInHand`. Ordinary flush and Ethereal exhaust affect the current hand only
- Enemy turns admit all move callbacks and transitions of exact `CorpseSlug` and `SludgeSpinner`; these only attack, apply the reviewed powers, or change AI state. Public numeric intents alone are never treated as evidence of a move implementation
- Active powers admit exact Weak, Frail, Strength, and Ravenous implementations, including application/removal behavior. Ravenous changes CorpseSlug AI and applies Strength on death; it never changes card piles
- Orbs, pets, afflictions, changed relic membership, and changed monster identities disable further extension. Only the exact Sharp enchantment is admitted

The explicit v3 startup entry points additionally admit `LavaRock.ModifyRewards` and Sharp. `CombatRoom` dispatches reward modification only during post-victory settlement, never during startup or an unfinished draw cycle. Sharp's only custom effect is additive attack damage; inherited draw/play/shuffle-order callbacks are no-ops. These exceptions do not alter the legacy startup or HP entry points.

At every stable snapshot, the unordered public draw multiset and count must equal entry cards minus the certified draws; known draw positions and unidentified cards are absent under this closure. These checks detect contradictions but do not replace the transition proof. Gaps, unreviewed powers/effects, generation, pre-settlement, or the first shuffle stop the certificate. The last ordinary draw before a shuffle remains valid, including a prefix equal to the complete entry deck. Later reshuffle draws are never folded into that permutation.

## Sampling and correction

The constraint uses card type IDs deliberately. Different upgrades and enchantments with the same ID remain distinct physical indices in `ConditionalShuffleProposal`; the complete entry anchor and final native public equality retain their full metadata. No uniform choice over coarse card identities substitutes for a physical permutation.

For public prefix IDs `p[0..k)`, the uniform physical-permutation event probability is the product of the remaining multiplicity of `p[i]` divided by `n-i`. Extending the prefix changes this factor and nothing else in the proposal math. The existing proposal samples each compatible physical permutation uniformly, inverts its native Fisher–Yates path, and samples full raw words inside the exact native floating-point buckets. Native-to-proposal ratios and root-constant rejection envelopes retain all bucket factors. Every completed combat's correction remains in the product. State alias checks, already visited cells, raw-word consumption, and full evidence equality remain mandatory.

The per-combat audit exposes certificate version, initial and extended draw counts, last certified event ordinal, and stop reason. This is a bounded computational improvement, not production admission, finite-seed posterior certification, or evidence of an independent posterior sample.

## Focused validation

Tests enumerate all finite native word worlds for partial and complete duplicate-ID prefixes, checking each distinct physical permutation and exact correction mass. Controlled native card/potion fixtures and public-event fixtures exercise card/potion draw, crossing turns, the first reshuffle, gaps, hidden generation, unreviewed moves/cards/potions/powers, protected turn-end effects, and Sly autoplay rejection. Native retained recipes 11002 and 11003 prove 16 and 12 observed initial draws respectively; 11004 proves its first combat's 13-card cycle and gains startup eligibility for its LavaRock/Sharp entries. Same-recipe native replay with independent proposal randomness checks complete public equality for 11002 and 11003; it is a kernel integration test, not an independent posterior experiment.
