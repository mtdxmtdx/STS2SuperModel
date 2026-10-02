using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using Nosl.Contracts;
using Sts2Sim.Core.Models.Relics;

namespace Nosl.Worker;

/// <summary>
/// Public-only certificate for observed Neow positives under the Rewards hybrid
/// prior, or a retained positive under either declared fresh Silent A10 tape prior.
/// Other priors/entries retain plain replay.
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
    private static readonly Type[] CurseTypes =
    [
        typeof(CursedPearl), typeof(DowsingRod), typeof(HeftyTablet), typeof(LargeCapsule),
        typeof(LeafyPoultice), typeof(NeowsBones), typeof(NeowsSacrifice), typeof(PrecariousShears),
        typeof(SilkenTress), typeof(SilverCrucible),
    ];
    private static readonly HashSet<string> CurseIds = new(CurseTypes.Select(type => type.Name), StringComparer.Ordinal);
    private static readonly HashSet<string> NativePoolShapes = BuildNativePoolShapes();
    internal static ShuffleRational RootEnvelope { get; } = MaximumEnvelope(
        Enumerable.Range(MinimumNativePoolSize, MaximumNativePoolSize - MinimumNativePoolSize + 1));
    private static readonly ShuffleRational ObservedPrefixRootEnvelope = MaximumEnvelope(
        Enumerable.Range(MinimumNativePoolSize, MaximumNativePoolSize - MinimumNativePoolSize + 1), prefixLength: 2);

    internal IReadOnlyList<string> PositivePrefixIds { get; }
    internal string TargetRelicId => PositivePrefixIds[0];
    internal string? ObservedCurseId { get; }
    private NativeNeowCondition(string[] positivePrefixIds, string? observedCurseId = null)
    {
        PositivePrefixIds = Array.AsReadOnly(positivePrefixIds.ToArray());
        ObservedCurseId = observedCurseId;
    }

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
        Require(prior is { SchemaVersion: NativeTapePrior.Version or NativeTapePrior.RewardsVersion,
            Execution: { SourcePolicyId: PublicContinuationPolicies.ReviewedId } },
            "fresh_native_run_prior_required");
        Require(prior.Execution.ResolvedOutsideCombatScript is NaturalSourceCollector.ScriptVersion
            or NaturalSourceCollector.BoundedEventScriptVersion, "neow_source_script_not_certified");
        Require(root is { Status: "player_decision" or "card_choice", Observation: not null, Actions.Length: > 0 },
            "active_decision_required");
        var observation = root.Observation!;
        if (prior.UsesRewardsProvenance && observation.Ascension == 10
            && TryObservedInitialPrefix(root.PublicEvidence, out var observedPrefix, out var observedCurse))
            return new(observedPrefix, observedCurse);
        Require(observation.Ascension == 10 && observation.History is { Length: >= 2 }
            && observation.History[0] is { Kind: "combat_started", Detail: "Silent:A10" }
            && observation.History[1].Kind == NativeEntryAssets.EventKind
            && observation.History.Count(item => item.Kind == NativeEntryAssets.EventKind) == 1,
            "fresh_silent_entry_history_required");
        var entry = PublicJson.Read<NativeEntryAssets>(observation.History[1].Detail);
        Require(entry is { SchemaVersion: "nosl.native-entry-assets.v1", Deck: not null,
                Relics: not null, Potions: not null }
            && entry.Hp == observation.StartHp && entry.Hp > 0 && entry.Hp <= entry.MaxHp,
            "invalid_public_entry");
        Require(entry.Relics.All(relic => relic is not null && !string.IsNullOrWhiteSpace(relic.Id)),
            "invalid_public_entry");
        PublicRelic[] retained = entry.Relics.Where(relic => PositiveIds.Contains(relic.Id)).ToArray();
        Require(retained.Length == 1, "unique_retained_neow_positive_required");
        var target = retained[0];
        Require(target.Details is not null
            && target.Details.TryGetValue("isWax", out int wax) && wax == 0
            && target.Details.TryGetValue("isMelted", out int melted) && melted == 0
            && target.Details.TryGetValue("stackCount", out int count) && count == 1,
            "ordinary_retained_neow_positive_required");

        // Origin closure (not an inference from a sampled hidden trace): NativeRunWorld
        // constructs one fresh Silent A10 with RingOfTheSnake. ActDefinition.GetRandomList
        // chooses Overgrowth/Underdocks at index zero, both exclusively Neow, and
        // RunState.GenerateAllActRooms gives index zero no shared Ancients. RunDriver
        // resolves Neow before the first counted floor. Every positive RelicOption is
        // unlocked. Both reviewed source scripts take the first unlocked initial
        // option: v3 resets its public choice history on every event boundary. Thus
        // the initial curse (including NeowsBones/LargeCapsule) is never selected.
        //
        // Whole-run acquisition closure, pinned to the native source files:
        //  - Player inventory insertion occurs in fresh starter setup, RelicCmd.Obtain,
        //    RelicCmd.Replace, type-preserving combat clones, or transplant restore.
        //    NativeRunWorld uses fresh setup/replay, never transplant restore.
        //  - No official character/shared relic pool contains a PositiveTypes member.
        //    Player grab bags restrict rarity to Common/Uncommon/Rare/Shop. The shared
        //    bag includes all rarities (including OTHER Ancients), but treasure draws
        //    request only RelicFactory.RollRarity's Common/Uncommon/Rare. The same
        //    ordinary factories cover combat rewards, shops, dig, CrystalSphere,
        //    random event grants, SmallCapsule, CallingBell, LavaRock, BlackStar,
        //    WongosMysteryTicket, PaelsWing, and ToyBox's wax copies.
        //  - Every explicit ordinary-event grant (including TrashHeap's Type pool,
        //    TeaMaster's generic grants and FakeMerchant) is disjoint from these 19
        //    types. Cards and potions have no relic-insertion paths. The sole native
        //    replacement is SwordOfStone replacing itself with SwordOfJade.
        //  - The seven other Ancient option tables are disjoint. Neow cannot recur:
        //    Hive/Glory exclude it, and SharedAncientPool contains only Darv. The sole
        //    generic Ancient-options copier is NeowsBones, whose only acquisition
        //    source is Neow's unchosen curse. No ordinary positive can arise there.
        //  - ToyBox is the sole natural IsWax writer and cannot draw these types.
        //    RelicCmd.Melt preserves type; no code turns a wax copy into an ordinary
        //    relic. Ranwid/RelicTrader remove only IsTradable relics, excluding Ancient.
        // Hence the retained ordinary public positive identifies the initial choice
        // even after other acquisitions or Ancient visits. No current Ring, deck,
        // other-relic state, floor/combat index, or hidden source trace is required.
        // Missing/duplicate/wax/melted targets fall back without excluding that root.
        return new([target.Id]);
    }

    private static bool TryObservedInitialPrefix(PublicRunEvidence? evidence, out string[] prefix, out string? curse)
    {
        prefix = []; curse = null;
        if (evidence is null || evidence.Events.Length < 4) return false;
        var events = evidence.Events;
        // The fixed fresh-run execution emits these four observations before any
        // subchoice, map travel or combat. Requiring this uninterrupted boundary
        // excludes missing starts, earlier gaps, other owners and later screens.
        // A gap AFTER the linked choice does not erase the already observed prefix.
        // Native initial act zero has only Neow; no private event ID is consulted.
        if (events[0].Payload is not PublicRunStarted { Character: "Silent", Ascension: 10 }
            || events[1] is not { OwnerOrdinal: 0, Payload: PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Event, ActIndex: 0, Floor: 1,
                    ParentOwnerOrdinal: null, CompleteFromOwnerStart: true } }
            || events[2] is not { OwnerOrdinal: 0, Payload: PublicOptionsObserved observed }
            || observed.Options.Length != 3
            || observed.Options.Any(option => option.IsLocked || option.Price is not null)
            || !PositiveIds.Contains(observed.Options[0].Key)
            || !PositiveIds.Contains(observed.Options[1].Key)
            || !CurseIds.Contains(observed.Options[2].Key)
            || events[3] is not { OwnerOrdinal: 0, Payload: PublicOptionChosen chosen }
            || chosen.OfferEventOrdinal != events[2].EventOrdinal
            || chosen.Key != observed.Options[0].Key)
            return false;
        // Both declared scripts select the first unlocked initial option. These
        // typed offers directly prove its origin even if later removed or melted.
        // The observed curse and only the extra-pair coins required by the two
        // public positives can also be proposed with exact native raw-word mass.
        prefix = [observed.Options[0].Key, observed.Options[1].Key];
        curse = observed.Options[2].Key;
        return true;
    }

    /// <summary>
    /// The public opening fixes one curse bucket and zero, one or two independent
    /// extra-pair coins. Null words preserve the native oracle, including aliases.
    /// The ratio is fixed by the public root, never by an unrequired latent coin.
    /// </summary>
    internal NativeNeowOpeningProposal CreateOpeningProposal(IReadOnlyList<Type> allowedCurses,
        Func<ulong> nextWord, int precisionBits = ConditionalShuffleProposal.NativePrecisionBits)
    {
        ArgumentNullException.ThrowIfNull(allowedCurses);
        ArgumentNullException.ThrowIfNull(nextWord);
        if (ObservedCurseId is null) throw new InvalidOperationException("No observed Neow opening is certified");
        if (!allowedCurses.SequenceEqual(CurseTypes))
            throw new InvalidOperationException("Neow allowed curse pool does not match its certified native order");
        var words = new List<ulong?>();
        var ratio = new ShuffleRational(1, 1);
        int curseIndex = Array.FindIndex(CurseTypes, type => type.Name == ObservedCurseId);
        AddForced(allowedCurses.Count, curseIndex);
        for (int pairIndex = 0; pairIndex < ExtraPairs.Length; pairIndex++)
        {
            Type[] pair = ExtraPairs[pairIndex];
            string[] required = pair.Select(type => type.Name).Where(PositivePrefixIds.Contains).ToArray();
            if (required.Length > 1 || (pairIndex == 0 && ObservedCurseId == nameof(LargeCapsule) && required.Length > 0))
                throw new NativePublicConstraintMismatchException("Published Neow positives cannot coexist with the observed curse and extra pairs");
            if (pairIndex == 0 && ObservedCurseId == nameof(LargeCapsule)) continue;
            if (required.Length == 0) words.Add(null);
            // Rng.NextBool is Next(2) == 0: the first pair member uses bucket zero.
            else AddForced(2, pair[0].Name == required[0] ? 0 : 1);
        }
        return new(Array.AsReadOnly(words.ToArray()), ratio);

        void AddForced(int bound, int index)
        {
            var factor = ConditionalShuffleProposal.Factor(bound, index, precisionBits);
            if (factor.BucketSize == 0)
                throw new NativePublicConstraintMismatchException("Published Neow opening has zero native raw-word mass");
            int lowBits = 64 - precisionBits;
            ulong high = factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord);
            words.Add((high << lowBits) | (nextWord() & ((1UL << lowBits) - 1)));
            ratio = ratio.Multiply(factor.BucketSize, BigInteger.One << precisionBits);
        }
    }

    /// <summary>
    /// Call only after identifying the current hypothetical Neow's own Rng and its
    /// initial Type shuffle. Invalid native shapes are engine errors, not rejection.
    /// A valid pool omitting the public target is a proved public nonmatch.
    /// </summary>
    internal NativeNeowProposal CreateProposal(IReadOnlyList<Type> nativePool, Func<ulong> nextWord,
        NativeNeowOpeningProposal? opening = null)
    {
        ArgumentNullException.ThrowIfNull(nativePool);
        ArgumentNullException.ThrowIfNull(nextWord);
        Type[] pool = nativePool.ToArray();
        if (pool.Length is < MinimumNativePoolSize or > MaximumNativePoolSize
            || pool.Any(type => type is null || !PositiveTypes.Contains(type))
            || pool.Distinct().Count() != pool.Length || !NativePoolShapes.Contains(PoolKey(pool)))
            throw new InvalidOperationException("Neow positive shuffle does not match its certified native pool shapes");
        string[] ids = pool.Select(type => type.Name).ToArray();
        if (PositivePrefixIds.Any(id => !ids.Contains(id, StringComparer.Ordinal)))
            throw new NativePublicConstraintMismatchException("Published Neow positive is absent from this native pool");
        var plan = ConditionalShuffleProposal.Create(ids, PositivePrefixIds, nextWord)
            ?? throw new InvalidOperationException("Certified native Neow positive has no shuffle proposal support");
        var envelope = PositivePrefixIds.Count == 1 ? RootEnvelope : ObservedPrefixRootEnvelope;
        var openingRatio = opening?.NativeToProposalRatio ?? new ShuffleRational(1, 1);
        return new(plan, envelope.Multiply(openingRatio.Numerator, openingRatio.Denominator), openingRatio);
    }

    // Test precision permits exhaustive finite analogs of this same root correction.
    internal static ShuffleRational MaximumEnvelope(IEnumerable<int> poolSizes,
        int precisionBits = ConditionalShuffleProposal.NativePrecisionBits, int prefixLength = 1)
    {
        ArgumentNullException.ThrowIfNull(poolSizes);
        if (prefixLength < 1) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        var maximum = new ShuffleRational(0, 1);
        foreach (int count in poolSizes)
        {
            if (count < prefixLength) throw new ArgumentOutOfRangeException(nameof(poolSizes));
            var envelope = new ShuffleRational(1, count);
            for (int index = 1; index < prefixLength; index++)
                envelope = envelope.Multiply(1, count - index);
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

internal sealed record NativeNeowOpeningProposal(IReadOnlyList<ulong?> RawWords, ShuffleRational NativeToProposalRatio);

/// <summary>Exact joint native/proposal correction with one envelope across all latent pools.</summary>
internal sealed class NativeNeowProposal
{
    internal ConditionalShufflePlan Plan { get; }
    internal ShuffleRational RootEnvelope { get; }
    internal ShuffleRational AcceptanceProbability { get; }
    internal ShuffleRational NativeToProposalRatio { get; }

    internal NativeNeowProposal(ConditionalShufflePlan plan, ShuffleRational rootEnvelope, ShuffleRational? openingRatio = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (rootEnvelope.Numerator <= 0) throw new ArgumentOutOfRangeException(nameof(rootEnvelope));
        Plan = plan; RootEnvelope = rootEnvelope;
        var factor = openingRatio ?? new ShuffleRational(1, 1);
        NativeToProposalRatio = plan.NativeToProposalRatio.Multiply(factor.Numerator, factor.Denominator);
        AcceptanceProbability = new(NativeToProposalRatio.Numerator * rootEnvelope.Denominator,
            NativeToProposalRatio.Denominator * rootEnvelope.Numerator);
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
