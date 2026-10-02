using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.CardPools;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Optional public-origin certificate for one first normal-combat reward. Unsupported
/// roots retain the same complete native tape prior. No source recipe or inventory
/// graph is an input; the first combat and its HP outcome remain native.
/// </summary>
internal sealed class NativeFirstRewardCondition
{
    private static readonly string[] StarterIds =
    [
        "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent",
        "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent",
        "Neutralize", "Survivor", "AscendersBane",
    ];
    private static readonly HashSet<string> FirstEncounterNames = new(StringComparer.Ordinal)
    {
        "FuzzyWurmCrawler", "LoneNibbit", "ShrinkerBeetle", "SlimesWeak",
        "CorpseSlugsWeak", "SeapunkWeak", "SludgeSpinnerWeak", "ToadpolesWeak",
    };
    private readonly string[] _candidateIds;
    private readonly NativeRewardWordBucket[] _buckets;
    internal string TargetCardId { get; }
    internal string TargetNeowId { get; }
    internal int TargetEntryHp { get; }
    internal int TargetGoldPayout { get; }
    internal CardRarity TargetRarity { get; }
    internal ShuffleRational Envelope { get; }

    private NativeFirstRewardCondition(string cardId, string neowId, int entryHp, int goldPayout)
    {
        TargetCardId = cardId; TargetNeowId = neowId; TargetEntryHp = entryHp; TargetGoldPayout = goldPayout;
        TargetRarity = cardId == "Accelerant" ? CardRarity.Uncommon : CardRarity.Common;
        NaturalSourceCollector.InitializeNativeModels();
        _candidateIds = new SilentCardPool().GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false)
            .Where(card => card.Rarity == TargetRarity).Select(card => card.GetType().Name).ToArray();
        int targetIndex = Array.IndexOf(_candidateIds, cardId);
        if (targetIndex < 0 || _candidateIds.Distinct().Count() != _candidateIds.Length)
            throw new InvalidOperationException("Certified first reward card pool has changed");
        _buckets = NativeFirstRewardProposal.BuildBuckets(goldPayout, TargetRarity, _candidateIds.Length, targetIndex);
        Envelope = NativeFirstRewardProposal.Probability(_buckets);
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeFirstRewardCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        try { condition = Create(root, prior); return true; }
        catch (Ineligible exception) { reason = exception.Message; return false; }
        catch (JsonException) { reason = "invalid_public_entry_json"; return false; }
        catch (ArgumentException) { reason = "invalid_public_first_reward_evidence"; return false; }
    }

    private static NativeFirstRewardCondition Create(DecisionPacket root, NativeTapePrior prior)
    {
        Require(prior is { SchemaVersion: NativeTapePrior.Version, Execution: not null }
            && prior.Execution.EmitsPublicRunContext
            && !PublicRunContext.IsHistoryUnavailable(prior.Execution.PublicCombatHistoryMode),
            "complete_public_run_history_required");
        Require(root.Observation is { Schema: PublicRunContext.ObservationSchema,
            RunContext: { SchemaVersion: PublicRunContext.Version, CompleteFromRunStart: true,
                ActIndex: 0, Floor: 3, CombatEntryIndex: 1 } }, "second_combat_floor_three_required");
        Require(NativeNeowCondition.TryCreate(root, prior, out var neow, out _)
            && neow!.TargetRelicId is nameof(WingedBoots) or nameof(LeadPaperweight),
            "first_reward_neow_origin_not_certified");
        Require(NativeInitialShuffleCondition.TryCreate(root, out _, out _), "first_reward_startup_not_certified");
        var observation = root.Observation!;
        var entry = PublicJson.Read<NativeEntryAssets>(observation.History[1].Detail);
        Require(entry.Relics.Length == 2 && entry.Relics.Count(relic => relic.Id == nameof(RingOfTheSnake)) == 1
            && entry.Relics.All(relic => relic.Details is not null
                && relic.Details.TryGetValue("isWax", out int wax) && wax == 0
                && relic.Details.TryGetValue("isMelted", out int melted) && melted == 0
                && relic.Details.TryGetValue("stackCount", out int count) && count == 1),
            "first_reward_inventory_not_certified");
        Require(entry.MaxHp == 70 && entry.Hp is > 0 and <= 56 && entry.MaxEnergy == 3
            && entry.PotionSlots == 2 && entry.Potions is { Length: 2 } && entry.Potions.All(potion => potion is null)
            && entry.OrbSlots == 0 && entry.CardRemovalsUsed == 0,
            "first_reward_inventory_not_certified");
        Require(entry.Deck.Length == StarterIds.Length + 1 && entry.Deck.All(card => card is not null
            && card.Upgrade == 0 && (card.Enchantments?.Length ?? 0) == 0 && card.Affliction is null),
            "plain_first_reward_deck_required");
        var remaining = Counts(entry.Deck.Select(card => card.Id));
        foreach (string id in StarterIds)
        {
            Require(remaining.TryGetValue(id, out int count) && count > 0, "plain_first_reward_deck_required");
            remaining[id]--;
        }
        string[] extra = remaining.SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)).ToArray();
        Require(extra is { Length: 1 } && extra[0] is "Accelerant" or "DeadlyPoison", "first_reward_card_not_certified");
        Require(entry.Gold is >= 106 and <= 114, "first_reward_gold_outside_native_range");
        int firstTurn = Array.FindIndex(observation.History, item => item.Kind == "player_turn" && item.Detail == "1");
        var startup = observation.History.Skip(firstTurn + 1).TakeWhile(item => item.Kind == "intent_published")
            .Select(item => PublicJson.Read<StartupIdentity>(item.Detail)).ToArray();
        Require(startup is [{ Slot: 0, Id: "FuzzyWurmCrawler" or "SludgeSpinner" }],
            "first_reward_route_not_certified");

        // Source-pinned origin closure:
        // NativeRunWorld starts a fresh Silent A10. Neow is first; these retained
        // positives identify its selected option. WingedBoots adds no asset, and
        // LeadPaperweight offers two colorless cards but SourceBridge.ChooseCardsAsync
        // takes MinCount=0. Those native offers still consume rarity/pick/upgrade
        // words; Source.Other calls CardRarityOdds.RollWithBaseOdds and leaves the
        // encounter offset at -0.05f. Neither relic affects gold, HP or rewards.
        // StandardActMap row one is Monster; WingedBoots cannot skip rows. Public
        // floor3/index1 therefore allows only one prior normal combat. The current
        // FuzzyWurmCrawler/SludgeSpinner startup excludes every event-forced roster:
        // DenseVegetation=Wrigglers, FakeMerchant=FakeMerchantMonster, TheLanternKey=
        // MysteriousKnight, PunchOff=PunchConstructs, BattlewornDummy=BattleFriendV1-3.
        // The startup certificate rules out identity-changing entry effects. Thus
        // there was no intervening event, shop, rest, treasure, or other acquisition.
        //
        // ActDefinition chooses one of the eight reviewed weak encounters above.
        // Their eleven monster types have no escape/gold theft/permanent-deck or
        // extra-reward effects; gold bounds/percentage are the ordinary A10 7..15.
        // Their powers only affect combat state. The starter cards and allowed
        // relics cannot generate permanent assets; both target cards have no pickup
        // or after-added side effect. The plain deck delta is therefore card option0:
        // RewardDecisionClassifier takes first eligible and Silent has neither
        // excluded Splash nor Kaleidoscope. No card-reward/gold/HP modifier is owned.
        // Empty carry-in potions force no first reward potion, since the native
        // default takes any potion into the two empty slots. Initial gold is 99.
        // HP after the first native settlement equals the next entry HP; checking
        // that value rejects impossible proposals but never changes a combat roll.
        return new(extra[0], neow!.TargetRelicId, entry.Hp, entry.Gold - 99);
    }

    /// <summary>
    /// Runtime drift in the reviewed native reward context is an engine error.
    /// Different retained Neow or first combat terminal HP is a public nonmatch.
    /// The tape caller must identify combat index zero, enforce one interception,
    /// validate each forced RNG pre-state, and reject earlier-cell aliases as errors.
    /// </summary>
    internal NativeFirstRewardProposal CreateProposal(LabelCombatRewardContext context, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(nextWord);
        var player = context.Player;
        if (player.RunState is not RunState run || player.Character is not Silent
            || run.CurrentActIndex != 0 || run.TotalFloor != 2 || run.CurrentRoomCount != 1
            || run.Players.Count != 1 || !ReferenceEquals(run.Players[0], player)
            || run.CurrentRoom is not CombatRoom { Won: true, RoomType: RoomType.Monster } room
            || context.RoomType != RoomType.Monster || context.Encounter is not { IsWeak: true } encounter
            || !ReferenceEquals(room.Encounter, encounter) || !FirstEncounterNames.Contains(encounter.Name)
            || encounter.MinGoldReward is not null || encounter.MaxGoldReward is not null
            || encounter.GoldProportionCalculator is not null || context.FixedGoldAmount is not null
            || context.GoldProportion != 1f || player.PlayerRng.UsesSemanticKeys
            || !ReferenceEquals(context.Rng, player.PlayerRng.Rewards))
            throw new InvalidOperationException("First reward interception is outside its certified native generation context");
        if (player.Relics.Count != 2 || !player.Relics.Any(relic => relic is RingOfTheSnake)
            || !player.Relics.Any(relic => relic.GetType().Name == TargetNeowId))
            throw new NativePublicConstraintMismatchException("First reward carries a different retained Neow relic");
        if (player.Relics.Any(relic => relic.IsWax || relic.IsMelted || relic.StackCount != 1)
            || player.Gold != 99 || player.Creature.MaxHp != 70
            || !SameCounts(player.Deck.Cards.Select(card => card.GetType().Name), StarterIds)
            || player.Deck.Cards.Any(card => card.CurrentUpgradeLevel != 0 || card.Enchantments.Count != 0 || card.Affliction is not null)
            || player.PotionSlots.Count != 2 || player.PotionSlots.Any(potion => potion is not null)
            || player.Odds.CardRarity.CurrentValue != -0.05f || player.Odds.CardRarity.RegularRareOdds != 0.0149f
            || player.Odds.PotionReward.CurrentValue != 0.4f)
            throw new InvalidOperationException("Reviewed first reward asset or odds invariant changed");
        var candidates = player.Character.CardPool.GetUnlockedCards(player.UnlockState, false)
            .Where(card => card.Rarity == TargetRarity).Select(card => card.GetType().Name).ToArray();
        if (!candidates.SequenceEqual(_candidateIds, StringComparer.Ordinal))
            throw new InvalidOperationException("Reviewed first reward candidate ordering changed");
        if (player.Creature.CurrentHp != TargetEntryHp)
            throw new NativePublicConstraintMismatchException($"First native combat HP differs from the public next entry: proposed={player.Creature.CurrentHp}, required={TargetEntryHp}");
        return NativeFirstRewardProposal.Create(_buckets, nextWord);
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> ids) => ids.GroupBy(id => id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    private static bool SameCounts(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = Counts(left); var b = Counts(right);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out int count) && count == pair.Value);
    }
    private static void Require([DoesNotReturnIf(false)] bool value, string reason)
    { if (!value) throw new Ineligible(reason); }
    private sealed record StartupIdentity(int Slot, string Id);
    private sealed class Ineligible(string message) : Exception(message);
}

internal sealed record NativeRewardWordBucket(string Role, int PrecisionBits, ulong Start, ulong Size);

/// <summary>
/// Exact five-word native conditional prefix. Pool, odds and gold bounds are fixed by
/// the public root's source proof, so its joint native/proposal ratio is root-constant.
/// Unselected reward options and every subsequent draw retain ordinary native laws.
/// </summary>
internal sealed class NativeFirstRewardProposal
{
    internal IReadOnlyList<ulong> RawWords { get; }
    internal IReadOnlyList<NativeRewardWordBucket> Buckets { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope => NativeToProposalRatio;

    private NativeFirstRewardProposal(NativeRewardWordBucket[] buckets, ulong[] words)
    {
        Buckets = Array.AsReadOnly(buckets); RawWords = Array.AsReadOnly(words);
        NativeToProposalRatio = Probability(buckets);
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }

    internal static NativeFirstRewardProposal Create(IReadOnlyList<NativeRewardWordBucket> buckets, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(buckets); ArgumentNullException.ThrowIfNull(nextWord);
        NativeRewardWordBucket[] copy = buckets.ToArray(); var words = new ulong[copy.Length];
        if (copy.Length != 5) throw new ArgumentException("The first reward requires exactly five primitive buckets", nameof(buckets));
        for (int i = 0; i < copy.Length; i++)
        {
            var bucket = copy[i]; ValidateBits(bucket.PrecisionBits);
            ulong domain = 1UL << bucket.PrecisionBits;
            if (bucket.Size == 0 || bucket.Start >= domain || bucket.Size > domain - bucket.Start)
                throw new ArgumentException("Reward primitive bucket has no native support", nameof(buckets));
            int lowBits = 64 - bucket.PrecisionBits;
            ulong high = bucket.Start + ConditionalShuffleProposal.UniformBelow(bucket.Size, nextWord);
            words[i] = (high << lowBits) | (nextWord() & ((1UL << lowBits) - 1));
        }
        return new(copy, words);
    }

    internal static ShuffleRational Probability(IEnumerable<NativeRewardWordBucket> buckets)
    {
        var probability = new ShuffleRational(1, 1);
        foreach (var bucket in buckets) probability = probability.Multiply(bucket.Size, 1UL << bucket.PrecisionBits);
        return probability;
    }

    internal static NativeRewardWordBucket[] BuildBuckets(int goldPayout, CardRarity rarity,
        int candidateCount, int targetIndex, int precisionBits = 53)
    {
        ValidateBits(precisionBits);
        if (goldPayout is < 7 or > 15) throw new ArgumentOutOfRangeException(nameof(goldPayout));
        if (rarity is not (CardRarity.Common or CardRarity.Uncommon)) throw new ArgumentOutOfRangeException(nameof(rarity));
        ulong domain = 1UL << precisionBits;
        ulong noPotionStart = FloatLowerBound(0.4f, precisionBits);
        // These are precisely the native float operations at A10 offset -0.05f.
        float rareThreshold = 0.0149f + -0.05f;
        float uncommonThreshold = 0.37f + rareThreshold;
        ulong rarityStart = FloatLowerBound(rarity == CardRarity.Uncommon ? rareThreshold : uncommonThreshold, precisionBits);
        ulong rarityEnd = rarity == CardRarity.Uncommon ? FloatLowerBound(uncommonThreshold, precisionBits) : domain;
        var gold = ConditionalShuffleProposal.Factor(9, goldPayout - 7, precisionBits);
        var card = ConditionalShuffleProposal.Factor(candidateCount, targetIndex, precisionBits);
        return
        [
            new("no_potion", precisionBits, noPotionStart, domain - noPotionStart),
            new("gold", precisionBits, gold.BucketStart, gold.BucketSize),
            new("first_card_rarity", precisionBits, rarityStart, rarityEnd - rarityStart),
            new("first_card_index", precisionBits, card.BucketStart, card.BucketSize),
            // Rng.NextFloat casts its 53-bit NextDouble, NOT MegaRandom's unused
            // 24-bit NextFloat. Native (decimal)roll <= 0 upgrades only high word0.
            new("first_card_not_upgraded", precisionBits, 1, domain - 1),
        ];
    }

    internal static ulong FloatLowerBound(float threshold, int precisionBits = 53)
    {
        ValidateBits(precisionBits);
        if (float.IsNaN(threshold)) throw new ArgumentOutOfRangeException(nameof(threshold));
        ulong low = 0, high = 1UL << precisionBits;
        double increment = Math.ScaleB(1d, -precisionBits);
        while (low < high)
        {
            ulong middle = low + (high - low) / 2;
            float native = (float)((double)middle * increment);
            if (native < threshold) low = middle + 1; else high = middle;
        }
        return low;
    }
    private static void ValidateBits(int bits)
    { if (bits is < 1 or > 53) throw new ArgumentOutOfRangeException(nameof(bits)); }
}
