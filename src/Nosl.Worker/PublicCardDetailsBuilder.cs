using System.Globalization;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Enchantments;
using Sts2Sim.Core.ValueProps;

namespace Nosl.Worker;

/// <summary>
/// Explicit player-visible card fields only. Never use the simulator's private fingerprint,
/// reflection field dumps, physical identities, draw positions, or random-state metadata here.
/// </summary>
internal static class PublicCardDetailsBuilder
{
    internal static PublicCard Card(CardModel card)
    {
        var details = new PublicCardDetails(card.CostsXEnergy, card.CostsXStar,
            card.LocalEnergyCost, card.LocalStarCost, card.TemporaryRetainThisTurn,
            card.TemporarySlyThisTurn, card.BaseReplayCount, card.ExhaustOnNextPlay,
            card.TemporaryFreeThisTurn, card.TemporaryFreeUntilPlayed, card.TemporaryFreeThisCombat,
            card.TemporaryStarCostOverrideThisTurn,
            card.NoslPublicEnergyModifiers.Select(m => new PublicCostModifier(m.Kind, m.Amount)).ToArray());
        var state = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["targetType"] = card.TargetType.ToString(),
            ["tags"] = string.Join(",", card.Tags.Select(t => t.ToString()).Order(StringComparer.Ordinal)),
        };
        void Number(string key, decimal value) => state[key] = value.ToString(CultureInfo.InvariantCulture);

        // These providers were individually checked: each reads accumulated public damage,
        // not hidden pile contents or a random target. Do not expand to arbitrary providers.
        if (card is Rampage or Thrash or Claw or Whistle or Maul)
        {
            if (((ICardDamageVariableProvider)card).TryGetThrashDamageVariable(out decimal damage))
                Number("damage", damage);
        }
        switch (card)
        {
            case GeneticAlgorithm genetic:
                Number("block", genetic.CurrentBlock);
                Number("blockIncrease", genetic.IncreasedBlock);
                break;
            case TheScythe scythe: Number("damageIncrease", scythe.IncreasedDamage); break;
            case Maul maul: Number("damageFromMaulPlays", maul.ExtraDamageFromMaulPlays); break;
            case Wither wither:
                Number("witherLevel", wither.FakeUpgradeLevel);
                Number("turnEndDamage", wither.TurnEndDamage);
                break;
            case Disintegration disintegration: Number("powerAmount", disintegration.PowerAmount); break;
            case TheHunt hunt: Number("pendingCardRewards", hunt.PendingCardRewardsForCombatSearch); break;
            case MadScience science: state["rider"] = science.Rider.ToString(); break;
            case Bombardment bombardment: state["hasReplayed"] = bombardment.NoslHasReplayed ? "true" : "false"; break;
            case Fetch fetch: state["playedThisTurn"] = fetch.NoslPlayedThisTurn ? "true" : "false"; break;
            case ThrummingHatchet hatchet: state["playedOnTurn"] = hatchet.NoslPlayedOnTurnNumber?.ToString(CultureInfo.InvariantCulture) ?? "none"; break;
            case Bolas bolas: state["playedOnTurn"] = bolas.NoslPlayedOnTurnNumber?.ToString(CultureInfo.InvariantCulture) ?? "none"; break;
            case Guilty guilty: Number("combatsCompleted", guilty.NoslCombatsCompleted); break;
            case Dowsing dowsing: Number("unknownRoomsEntered", dowsing.NoslUnknownRoomsEntered); break;
            // Abundance's unchosen generated candidates are simulator diagnostics, never a
            // public selection request. Only its actually generated hand card is observable.
        }
        for (int i = 0; i < card.Enchantments.Count; i++)
        {
            var enchantment = card.Enchantments[i];
            state[$"enchantment.{i}.status"] = enchantment.Status.ToString();
            if (enchantment is Momentum momentum)
                Number($"enchantment.{i}.extraDamage", momentum.EnchantDamageAdditive(0m, ValueProp.Move));
        }
        return new(card.GetType().Name, card.CurrentUpgradeLevel, card.EnergyCost, card.StarCost,
            card.Type.ToString(), card.Keywords.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToArray(),
            details, card.Enchantments.Select(e => new PublicCardEffect(e.GetType().Name, e.Magnitude)).ToArray(),
            card.Affliction is { } affliction ? new PublicCardEffect(affliction.GetType().Name, affliction.Amount) : null,
            state);
    }
}
