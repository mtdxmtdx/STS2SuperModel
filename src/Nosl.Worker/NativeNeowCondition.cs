using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Models.Relics;

namespace Nosl.Worker;

/// <summary>
/// Public-only certificate for the first Neow positive under the declared fresh
/// Silent A10, one-floor ideal-tape prior. Other priors/entries retain plain replay.
/// This certificate says nothing about the finite common-run-seed posterior.
/// </summary>
internal sealed class NativeNeowCondition
{
    internal const int MinimumNativePoolSize = 14;
    internal const int MaximumNativePoolSize = 16;

    // Source-pinned to Neow.GenerateInitialOptions. MassiveScroll is deliberately
    // absent: IsAllowedAtNeow delegates to its IsAllowed=false override. WingedBoots
    // is allowed for the single player. ScrollBoxes is allowed: SilentCardPool has
    // Backflip/CloakAndDagger/DaggerSpray/DaggerThrow (Common) and Accuracy/Blur
    // (Uncommon), none multiplayer-only; its inherited epoch filter is identity.
    private static readonly Type[] BasePositives =
    [
        typeof(ArcaneScroll), typeof(BoomingConch), typeof(FishingRod), typeof(GoldenPearl),
        typeof(Kaleidoscope), typeof(LeadPaperweight), typeof(LostCoffer), typeof(NeowsTorment),
        typeof(NewLeaf), typeof(PhialHolster), typeof(PreciseScissors), typeof(ScrollBoxes), typeof(WingedBoots),
    ];
    private static readonly Type[][] ExtraPairs =
    [
        [typeof(LavaRock), typeof(SmallCapsule)],
        [typeof(NutritiousOyster), typeof(StoneHumidifier)],
        [typeof(NeowsTalisman), typeof(Pomander)],
    ];
    private static readonly HashSet<Type> PositiveTypes = [.. BasePositives, .. ExtraPairs.SelectMany(pair => pair)];
    private static readonly HashSet<string> PositiveIds = new(PositiveTypes.Select(type => type.Name), StringComparer.Ordinal);
    private static readonly HashSet<string> NativePoolShapes = BuildNativePoolShapes();
    internal static ShuffleRational RootEnvelope { get; } = MaximumEnvelope(
        Enumerable.Range(MinimumNativePoolSize, MaximumNativePoolSize - MinimumNativePoolSize + 1));

    internal string TargetRelicId { get; }
    private NativeNeowCondition(string targetRelicId) => TargetRelicId = targetRelicId;

    internal static bool TryCreate(DecisionPacket publicRoot, NativeTapePrior prior,
        out NativeNeowCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        try { condition = Create(publicRoot, prior); return true; }
        catch (Ineligible exception) { reason = exception.Message; return false; }
        catch (JsonException) { reason = "invalid_public_entry_json"; return false; }
        catch (ArgumentException) { reason = "invalid_public_entry"; return false; }
    }

    private static NativeNeowCondition Create(DecisionPacket root, NativeTapePrior prior)
    {
        Require(prior is { SchemaVersion: NativeTapePrior.Version, EligibleCombats: 1,
            Execution: { MaxFloors: 1, SourcePolicyId: PublicContinuationPolicies.ReviewedId } },
            "first_native_combat_prior_required");
        Require(root is { Status: "player_decision" or "card_choice", Observation: not null, Actions.Length: > 0 },
            "active_decision_required");
        var observation = root.Observation!;
        Require(observation.Ascension == 10 && observation.History is { Length: >= 2 }
            && observation.History[0] is { Kind: "combat_started", Detail: "Silent:A10" }
            && observation.History[1].Kind == NativeEntryAssets.EventKind
            && observation.History.Count(item => item.Kind == NativeEntryAssets.EventKind) == 1,
            "fresh_silent_entry_history_required");
        var entry = PublicJson.Read<NativeEntryAssets>(observation.History[1].Detail);
        Require(entry is { SchemaVersion: "nosl.native-entry-assets.v1", Deck.Length: > 0,
                Relics.Length: 2, Potions: not null, OrbSlots: 0 }
            && entry.Hp == observation.StartHp && entry.Hp > 0 && entry.Hp <= entry.MaxHp,
            "exact_neow_entry_inventory_required");
        Require(entry.Relics.All(relic => relic is not null && relic.Details is not null
                && relic.Details.TryGetValue("isWax", out int wax) && wax == 0
                && relic.Details.TryGetValue("isMelted", out int melted) && melted == 0
                && relic.Details.TryGetValue("stackCount", out int count) && count == 1)
            && entry.Relics.Count(relic => relic.Id == nameof(RingOfTheSnake)) == 1,
            "ordinary_starter_and_positive_required");
        string target = entry.Relics.Single(relic => relic.Id != nameof(RingOfTheSnake)).Id;
        Require(PositiveIds.Contains(target), "neow_positive_origin_not_certified");

        // Origin closure (not an inference from a sampled hidden trace): NativeRunWorld
        // constructs one fresh Silent A10 with RingOfTheSnake. ActDefinition.GetRandomList
        // chooses Overgrowth/Underdocks at index zero, both exclusively Neow, and
        // RunState.GenerateAllActRooms gives index zero no shared Ancients. RunDriver
        // resolves Neow before the first counted floor. Every positive RelicOption is
        // unlocked, and SourceBridge.ChooseEventOptionAsync takes the first unlocked.
        // Thus the curse (including NeowsBones/LargeCapsule) can never be chosen.
        // RelicCmd.Obtain retains the selected positive. Its reviewed pickup effects
        // neither remove/replace it nor start combat. Only SmallCapsule offers another
        // relic; RelicFactory.RollRarity is Common/Uncommon/Rare, never Ancient, and
        // no such pickup removes SmallCapsule (the sole relic Replace caller replaces
        // SwordOfStone itself after elite wins). Card/potion pickups do not execute
        // their combat effects. None of these positives modifies the generated map.
        // StandardActMap fixes row one as Monster; MapTravel's WingedBoots branch also
        // selects row one. There is no intervening event/shop/treasure acquisition.
        // The exact two-relic public entry therefore forces this selected positive;
        // wider horizons/inventories do not have this origin proof and are ineligible.
        return new(target);
    }

    /// <summary>
    /// Call only after identifying the current hypothetical Neow's own Rng and its
    /// initial Type shuffle. Invalid native shapes are engine errors, not rejection.
    /// A valid pool omitting the public target is a proved public nonmatch.
    /// </summary>
    internal NativeNeowProposal CreateProposal(IReadOnlyList<Type> nativePool, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(nativePool);
        ArgumentNullException.ThrowIfNull(nextWord);
        Type[] pool = nativePool.ToArray();
        if (pool.Length is < MinimumNativePoolSize or > MaximumNativePoolSize
            || pool.Any(type => type is null || !PositiveTypes.Contains(type))
            || pool.Distinct().Count() != pool.Length || !NativePoolShapes.Contains(PoolKey(pool)))
            throw new InvalidOperationException("Neow positive shuffle does not match its certified native pool shapes");
        string[] ids = pool.Select(type => type.Name).ToArray();
        if (!ids.Contains(TargetRelicId, StringComparer.Ordinal))
            throw new NativePublicConstraintMismatchException("Published Neow positive is absent from this native pool");
        var plan = ConditionalShuffleProposal.Create(ids, [TargetRelicId], nextWord)
            ?? throw new InvalidOperationException("Certified native Neow positive has no shuffle proposal support");
        return new(plan, RootEnvelope);
    }

    // Test precision permits exhaustive finite analogs of this same root correction.
    internal static ShuffleRational MaximumEnvelope(IEnumerable<int> poolSizes,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(poolSizes);
        var maximum = new ShuffleRational(0, 1);
        foreach (int count in poolSizes)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(poolSizes));
            var envelope = new ShuffleRational(1, count);
            for (int bound = 2; bound <= count; bound++)
            {
                var factor = ConditionalShuffleProposal.Factor(bound, 0, precisionBits);
                envelope = envelope.Multiply((BigInteger)bound * factor.MaxBucketSize, BigInteger.One << precisionBits);
            }
            if (envelope.Numerator * maximum.Denominator > maximum.Numerator * envelope.Denominator)
                maximum = envelope;
        }
        if (maximum.Numerator.IsZero) throw new ArgumentException("At least one pool size is required", nameof(poolSizes));
        return maximum;
    }

    private static HashSet<string> BuildNativePoolShapes()
    {
        Type[][] exclusions = [[], [typeof(GoldenPearl)], [typeof(ArcaneScroll)], [typeof(NewLeaf)],
            [typeof(PreciseScissors)], [typeof(PhialHolster), typeof(LostCoffer)]];
        var shapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var excluded in exclusions)
            foreach (Type first in ExtraPairs[0])
                foreach (Type second in ExtraPairs[1])
                    foreach (Type third in ExtraPairs[2])
                        shapes.Add(PoolKey(BasePositives.Except(excluded).Concat([first, second, third])));
        // LargeCapsule suppresses the entire LavaRock/SmallCapsule pair.
        foreach (Type second in ExtraPairs[1])
            foreach (Type third in ExtraPairs[2])
                shapes.Add(PoolKey(BasePositives.Concat([second, third])));
        return shapes;
    }

    private static string PoolKey(IEnumerable<Type> pool) =>
        string.Join("|", pool.Select(type => type.FullName).Order(StringComparer.Ordinal));
    private static void Require([DoesNotReturnIf(false)] bool value, string reason)
    { if (!value) throw new Ineligible(reason); }
    private sealed class Ineligible(string message) : Exception(message);
}

/// <summary>Exact native/proposal correction with one envelope across all latent pools.</summary>
internal sealed class NativeNeowProposal
{
    internal ConditionalShufflePlan Plan { get; }
    internal ShuffleRational RootEnvelope { get; }
    internal ShuffleRational AcceptanceProbability { get; }

    internal NativeNeowProposal(ConditionalShufflePlan plan, ShuffleRational rootEnvelope)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (rootEnvelope.Numerator <= 0) throw new ArgumentOutOfRangeException(nameof(rootEnvelope));
        Plan = plan; RootEnvelope = rootEnvelope;
        AcceptanceProbability = new(plan.NativeToProposalRatio.Numerator * rootEnvelope.Denominator,
            plan.NativeToProposalRatio.Denominator * rootEnvelope.Numerator);
        if (AcceptanceProbability.Numerator > AcceptanceProbability.Denominator)
            throw new InvalidOperationException("Neow root envelope does not bound the native/proposal ratio");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord) => ExactRationalBernoulli(AcceptanceProbability, nextWord);

    internal static bool ExactRationalBernoulli(ShuffleRational probability, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        if (probability.Numerator > probability.Denominator) throw new ArgumentOutOfRangeException(nameof(probability));
        if (probability.Numerator.IsZero) return false;
        if (probability.Numerator == probability.Denominator) return true;
        // Independent uint64 words give a uniform power-of-two proposal. Mask only
        // surplus high bits, reject integers beyond the denominator, then compare.
        int bits = checked((int)(probability.Denominator - 1).GetBitLength());
        int wordCount = checked((bits + 63) / 64);
        BigInteger mask = (BigInteger.One << bits) - 1;
        BigInteger sample;
        do
        {
            sample = BigInteger.Zero;
            for (int i = 0; i < wordCount; i++) sample = (sample << 64) | nextWord();
            sample &= mask;
        } while (sample >= probability.Denominator);
        return sample < probability.Numerator;
    }
}
