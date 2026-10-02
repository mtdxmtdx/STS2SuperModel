using System.Reflection;
using Nosl.Contracts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;

namespace Nosl.Worker;

/// <summary>
/// Explicit public relic state. Private field access is limited to reviewed counters determined
/// by public acquisition state/actions; never serialize model internals, RNG, hashes or object IDs.
/// A renamed/missing approved field fails loudly rather than fabricating a zero.
/// </summary>
internal static class PublicRelicDetails
{
    private static readonly IReadOnlyDictionary<string, string[]> CounterFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["ArtOfWar"] = ["_attackPlayedThisTurn", "_previousTurnHadNoAttack"],
        ["BeatingRemnant"] = ["_hpLostThisTurn"],
        ["BeltBuckle"] = ["_dexterityApplied"],
        ["BookOfFiveRings"] = ["_cardsAdded"],
        ["BrilliantScarf"] = ["_cardsPlayed"],
        ["BurningSticks"] = ["_wasUsedThisCombat"],
        ["CentennialPuzzle"] = ["_triggeredThisCombat"],
        ["DemonTongue"] = ["_triggeredThisTurn"],
        ["FakeOrichalcum"] = ["_shouldTrigger"],
        ["FakeVenerableTeaSet"] = ["_isArmed"],
        ["GalacticDust"] = ["_starsSpent"],
        ["HappyFlower"] = ["_turnCounter"],
        ["HistoryCourse"] = ["_lastAttackTurn"],
        ["IronClub"] = ["_cardsPlayed"],
        ["JossPaper"] = ["_cardsExhausted", "_etherealCount"],
        ["Kunai"] = ["_attacksThisTurn"],
        ["Kusarigama"] = ["_attacksThisTurn"],
        ["LavaLamp"] = ["_tookDamageThisCombat"],
        ["LetterOpener"] = ["_skillsThisTurn"],
        ["LizardTail"] = ["_wasUsed"],
        ["Metronome"] = ["_orbsChanneled"],
        ["MiniRegent"] = ["_triggeredThisTurn"],
        ["MusicBox"] = ["_wasUsedThisTurn"],
        ["Nunchaku"] = ["_attacksPlayed"],
        ["Orichalcum"] = ["_shouldTrigger"],
        ["OrnamentalFan"] = ["_attacksThisTurn"],
        ["PaelsEye"] = ["_usedThisCombat", "_wasOwnerPartOfLastPlayerTurn", "_eligibleForExtraTurn"],
        ["PaelsLegion"] = ["_cooldown"],
        ["PaelsTears"] = ["_gainNextTurn"],
        ["PenNib"] = ["_attacksPlayed"],
        ["Pendulum"] = ["_turnCounter"],
        ["Permafrost"] = ["_triggeredThisCombat"],
        ["PollinousCore"] = ["_handsSeen"],
        ["RainbowRing"] = ["_playedAttack", "_playedSkill", "_playedPower", "_triggeredThisTurn"],
        ["RedSkull"] = ["_strengthApplied"],
        ["Regalite"] = ["_usedThisTurn"],
        ["RippleBasin"] = ["_attackPlayedThisTurn"],
        ["RuinedHelmet"] = ["_usedThisCombat"],
        ["Shuriken"] = ["_attacksThisTurn"],
        ["SilkenTress"] = ["_isUsed"],
        ["StoneCalendar"] = ["_shouldTriggerThisTurn"],
        ["SwordOfStone"] = ["_eliteVictories"],
        ["Toolbox"] = ["_wasUsedThisCombat"],
        ["TuningFork"] = ["_skillsPlayed"],
        ["UnsettlingLamp"] = ["_finished"],
        ["Vambrace"] = ["_triggeredThisCombat"],
        ["VelvetChoker"] = ["_cardsPlayedThisTurn"],
        ["VenerableTeaSet"] = ["_isArmed"],
        ["WongosMysteryTicket"] = ["_gaveRelics"],
    };

    internal static IReadOnlyDictionary<string, int> Details(RelicModel relic)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["isWax"] = relic.IsWax ? 1 : 0,
            ["isMelted"] = relic.IsMelted ? 1 : 0,
            ["isUsedUp"] = relic.IsUsedUp ? 1 : 0,
            ["stackCount"] = relic.StackCount,
        };
        if (CounterFields.TryGetValue(relic.GetType().Name, out var fields))
            foreach (string field in fields)
                result[field.TrimStart('_')] = ReadApprovedField(relic, field) switch
                {
                    int number => number,
                    bool flag => flag ? 1 : 0,
                    _ => throw new InvalidOperationException($"Approved public counter is not int/bool: {relic.GetType().Name}.{field}"),
                };
        // Prefer upstream public snapshots when supplied. These are explicit, not property discovery.
        switch (relic)
        {
            case BoneTea value: result["combatsLeft"] = value.CombatsLeft; break;
            case EmberTea value: result["combatsLeft"] = value.CombatsLeft; break;
            case TeaOfDiscourtesy value: result["combatsLeft"] = value.CombatsLeft; break;
            case FakeHappyFlower value: result["turnsSeen"] = value.TurnsSeen; break;
            case FishingRod value: result["combatsSeen"] = value.CombatsSeen; break;
            case Girya value: result["timesLifted"] = value.TimesLifted; break;
            case GoldenCompass value: result["goldenPathAct"] = value.GoldenPathAct; break;
            case LastingCandy value: result["combatRewardsSeen"] = value.CombatRewardsSeen; break;
            case LavaRock value: result["hasTriggered"] = value.HasTriggered ? 1 : 0; break;
            case MawBank value: result["hasItemBeenBought"] = value.HasItemBeenBought ? 1 : 0; break;
            case PaelsWing value: result["rewardsSacrificed"] = value.RewardsSacrificed; break;
            case Pocketwatch value:
                result["cardsPlayedThisTurn"] = value.CardsPlayedThisTurnSnapshot;
                result["cardsPlayedPreviousTurn"] = value.CardsPlayedPreviousTurnSnapshot;
                result["shouldDrawExtra"] = value.ShouldDrawExtraSnapshot ? 1 : 0;
                result["canStillTriggerThisTurn"] = value.CanStillTriggerThisTurnSnapshot ? 1 : 0;
                break;
            case PumpkinCandle value: result["kindleCount"] = value.KindleCount; break;
            case SilverCrucible value:
                result["timesUsed"] = value.TimesUsed;
                result["treasureRoomsEntered"] = value.TreasureRoomsEntered;
                break;
            case ThrowingAxe value: result["usedThisCombat"] = value.UsedThisCombat ? 1 : 0; break;
            case ToyBox value: result["combatsSeen"] = value.CombatsSeen; break;
            case WingedBoots value: result["timesUsed"] = value.TimesUsed; break;
            case WongosMysteryTicket value: result["combatsFinished"] = value.CombatsFinished; break;
        }
        return result;
    }

    internal static PublicCard[] Cards(RelicModel relic) => relic switch
    {
        HistoryCourse => ReadApprovedField(relic, "_lastAttack") is CardModel card ? [PublicViews.Card(card)] : [],
        PaelsTooth => ((IEnumerable<CardModel>)ReadApprovedField(relic, "_removedCards")!).Select(PublicViews.Card).ToArray(),
        _ => [],
    };

    internal static string? SelectedModel(RelicModel relic) => relic switch
    {
        DustyTome value => value.AncientCard?.ToString(),
        SeaGlass value => value.CharacterId?.ToString(),
        _ => null,
    };

    internal static void AssertStable(RelicModel relic)
    {
        string? transient = relic switch
        {
            PenNib => "_doubleDamagePlay",
            PaelsLegion => "_affectedCardPlay",
            MusicBox => "_cardBeingPlayed",
            _ => null,
        };
        if (transient is not null && ReadApprovedField(relic, transient) is not null)
            throw new InvalidOperationException($"Cannot clone an unfinished relic effect: {relic.GetType().Name}");
    }

    private static object? ReadApprovedField(RelicModel relic, string name) =>
        (relic.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(relic.GetType().FullName, name)).GetValue(relic);
}
