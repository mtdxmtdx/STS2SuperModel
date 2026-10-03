# Constructed Ruby enemy-turn draw closure

This wrapper-only extension closes the measured turn-2 draw guard for the
already inspected Ruby source. The independent source prior, source setup,
source-generation identity, stopping rule, native mechanics, and full public
packet matching are unchanged. Only the conditional proposal and its closure
certificate advance. The preceding formation-v2 reports remain historical
evidence, including their 32 retained later-draw rejections.

## Source audit

All five pinned `Models/Monsters/*RubyRaider.cs` types are sealed. Their complete
`GenerateMoveStateMachine` graphs use deterministic `MoveState` follow-up edges
and no state-entry/exit effects. Their moves have the following complete effects
at Silent A10:

| Monster | Native cycle/effect |
| --- | --- |
| Axe | Attack 6 and block 6 twice, attack 13, repeat |
| Assassin | Attack 11, repeat |
| Brute | Attack 8, self Strength +3, repeat with powered damage scaling |
| Crossbow | Block 3, attack 16, repeat |
| Tracker | Apply Frail 2 to player targets once, then attack 1 nine times repeatedly |

None overrides an `AbstractModel` combat hook or `AfterAddedToRoom`. The inherited
`MonsterModel.PerformMove` dispatches the chosen move and normal death removal;
it does not draw or modify a card pile. No Ruby move generates, moves, draws,
reorders, transforms, or upgrades cards; summons monsters; or creates another
power type.

`StrengthPower` only adds its amount to its owner's powered attack damage.
`FrailPower` only scales the owner's powered block and ticks at enemy-side end.
Native `PowerCmd.Apply` sets the first duration-tick skip for player debuffs;
later ticks decrement and remove Frail. Application, modification, and removal
use the already reviewed power/hook paths and do not modify card piles. Both
power types were already in the existing draw-closure listener set.

## Narrow version and guard boundary

`NativePublicCombatPrefixCondition.CreateConstructed` opts in only when the
declared encounter is `RubyRaiders`. The generic `Extend` default and all
`FindReshuffles` calls keep the original v11 behavior. The opt-in certificate is
`nosl.public-first-draw-cycle.v12-constructed-ruby`.

Only ordinary Ruby constructions use the new pair:

- `nosl-constructed-native-tape-ruby-draw-closure-v3-public-evidence-v2`
- `owned-constructed-native-tape-ruby-draw-closure-v3-public-evidence-v2`

The v1 and formation-v2 IDs stay interpretable for their retained reports.
Event-owner sampling retains its separate dispatch and plain-rejection behavior.
No prior/source identity changes and no old records are rewritten.

All existing unknown-card, unknown-power, card-modifier, listener-set, changed
roster, hand-end effect, generation, selection, action/effect linkage, and
reshuffle boundaries remain in force. This extension only proves that a
certified Ruby enemy turn leaves the undrawn initial pile unchanged. It does not
grant arbitrary card effects, later reshuffles, or new listener types.

The existing exact initial-shuffle proposal conditions the longer public draw
prefix, retains physical duplicate-card support and full native-word preimages,
and still applies its density correction before returning a sample. Full
unmodified public history is checked independently. Missing proof continues to
mean ordinary rejection, never removal of source support.

## Native verification

The new 12-case suite executes every Ruby model's complete move cycle with the
full mixed deck. It checks effect values, physical card identity, pile order,
public card metadata, and unchanged shuffle RNG state after every move and each
Frail skip/decrement/removal boundary. Negative cases retain the stops for
unknown power, hidden/visible generation, hand-end effects, uncertified card
plays, and changed relic listeners.

The exact inspected turn-2 prior remains
`709783dcf45ceb01218ffecc02a740048460005c99676a28fdedfbe3f92b5712`.
For source draw 44101, the certificate extends the public initial-draw prefix
from 8 to 13 cards. An explicit ordinary v11 scan still stops at 8 with
`draw_cycle_enemy_turn_not_certified`. Independent evaluation worlds 74101 and
74102 both accept on their first proposal under the original limit of 16;
their full public packets and owned continuation forks match exactly.

The focused run passes 204/204 tests, including previous v11 draw closures,
constructed source/dataset checks, the formation proposal, and all 12 new cases.
No new native Core change, source cohort, raw generation, value-price change,
training, or speed claim is part of this code checkpoint.
