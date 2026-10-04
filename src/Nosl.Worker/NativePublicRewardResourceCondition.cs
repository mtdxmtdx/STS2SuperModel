using System.Collections.ObjectModel;
using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.PotionPools;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativePublicRewardResourceTarget(NativePublicRewardTarget Owner, int? Gold, string? Potion,
    NativeGoldEnvelopeCertificate GoldCertificate, ShuffleRational PotionIdentityMass)
{
    internal ShuffleRational PotionEnvelope => Owner.PublicHistory is { } history
        ? NativeRewardResourceMath.DisplayMass(false, history.PotionThreshold, Potion is not null, PotionIdentityMass)
        : Potion is null ? new(1, 1) : PotionIdentityMass;
}

/// <summary>Detached first displayed primary resource offers, keyed only by certified card owners.</summary>
internal sealed class NativePublicRewardResourceCondition
{
    internal string Character { get; }
    internal IReadOnlyList<PotionModel> PotionPool { get; }
    internal IReadOnlyDictionary<int, NativePublicRewardResourceTarget> Targets { get; }
    internal ShuffleRational Envelope { get; }

    private NativePublicRewardResourceCondition(string character, PotionModel[] pool,
        Dictionary<int, NativePublicRewardResourceTarget> targets)
    {
        Character = character; PotionPool = Array.AsReadOnly(pool);
        Targets = new ReadOnlyDictionary<int, NativePublicRewardResourceTarget>(targets);
        Envelope = targets.Values.Aggregate(new ShuffleRational(1, 1), (mass, target) =>
        {
            var potionBound = target.PotionEnvelope;
            return mass.Multiply(potionBound.Numerator, potionBound.Denominator)
                .Multiply(target.GoldCertificate.Envelope.Numerator, target.GoldCertificate.Envelope.Denominator);
        });
    }

    internal static NativePublicRewardResourceCondition Create(PublicRunEvidence evidence, NativePublicRewardCondition cards)
    {
        ArgumentNullException.ThrowIfNull(evidence); ArgumentNullException.ThrowIfNull(cards);
        if (!evidence.CompleteFromRunStart || evidence.Events[0].Payload is not PublicRunStarted start
            || start.Character != cards.Character) throw new InvalidOperationException("Resource proposals require the same complete public card-owner prefix");
        // Revalidate the detached owner mapping, so an unrelated condition cannot borrow an offer ordinal.
        var verified = NativePublicRewardCondition.Create(evidence);
        if (!verified.Targets.Keys.SequenceEqual(cards.Targets.Keys)
            || verified.Targets.Any(pair => pair.Value.OfferEventOrdinal != cards.Targets[pair.Key].OfferEventOrdinal
                || pair.Value.ActIndex != cards.Targets[pair.Key].ActIndex || pair.Value.Floor != cards.Targets[pair.Key].Floor
                || !pair.Value.BaseCardIds.SequenceEqual(cards.Targets[pair.Key].BaseCardIds)))
            throw new InvalidOperationException("Resource condition received different certified reward owners");
        var character = ModelDb.AllCharacters.Single(c => c.GetType().Name == start.Character);
        var unlock = PlayerUnlockState.AllUnlocked();
        var pool = character.PotionPool.GetUnlockedPotions(unlock)
            .Concat(SharedPotionPool.Instance.GetUnlockedPotions(unlock)).DistinctBy(p => p.Id).ToArray();
        var combats = evidence.Events.Where(e => e.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat }).ToArray();
        var targets = new Dictionary<int, NativePublicRewardResourceTarget>();
        foreach (var target in cards.Targets.Values)
        {
            var offers = (PublicOffersObserved)evidence.Events[(int)target.OfferEventOrdinal].Payload;
            var primary = offers.Groups.Where(g => g.GroupKind == PublicOfferGroupKind.Primary).SelectMany(g => g.Offers).ToArray();
            var gold = primary.Where(o => o.OfferKind == PublicOfferKind.Gold).ToArray();
            var potion = primary.Where(o => o.OfferKind == PublicOfferKind.Potion).ToArray();
            if (gold.Length > 1 || potion.Length > 1 || gold.Any(o => o.Key != "gold") || potion.Any(o => o.Key != "potion"))
                throw new InvalidOperationException("Primary native resource positions are not certified");
            int? amount = gold.SingleOrDefault()?.Gold;
            string? id = potion.SingleOrDefault()?.Potion;
            var identityMass = NativeRewardResourceMath.PotionMass(pool, id);
            if (id is not null && identityMass.Numerator.IsZero)
                throw new NativePublicConstraintMismatchException("Public potion has no character-pool support");
            targets.Add(target.CombatIndex, new(target, amount, id,
                target.PublicHistory?.GoldCertificate ?? NativeGoldEnvelopeCertificate.Create(evidence, combats[target.CombatIndex], amount), identityMass));
        }
        return new(start.Character, pool, targets);
    }
}

/// <summary>
/// A public direct-map combat certifies the pinned ordinary room range, not the latent
/// escape fraction. All float proportions in [0,1] are covered, including rounded bounds.
/// Other contexts (event fixed rewards, unknown map nodes) retain the safe global bound one.
/// </summary>
internal sealed class NativeGoldEnvelopeCertificate
{
    internal ShuffleRational Envelope { get; }
    internal IReadOnlyList<(int Min, int Max)> Ranges { get; }
    internal RoomType? RoomType { get; }
    private readonly int _actIndex;
    private bool _exactWeak;
    private readonly HashSet<string> _encounterNames;

    private NativeGoldEnvelopeCertificate(ShuffleRational envelope, IReadOnlyList<(int Min, int Max)> ranges,
        RoomType? roomType = null, int actIndex = -1, IEnumerable<string>? names = null)
    { Envelope = envelope; Ranges = ranges; RoomType = roomType; _actIndex = actIndex; _encounterNames = names?.ToHashSet() ?? []; }

    internal static NativeGoldEnvelopeCertificate Create(PublicRunEvidence evidence, PublicRunEvidenceEvent combat, int? gold)
    {
        var fallback = new NativeGoldEnvelopeCertificate(new(1, 1), []);
        var start = (PublicOwnerStarted)combat.Payload;
        if (start.ParentOwnerOrdinal is not null || gold is null) return fallback;
        int cursor = checked((int)combat.EventOrdinal - 1);
        while (cursor >= 0 && evidence.Events[cursor].Payload is PublicOwnerEnded) cursor--;
        if (cursor < 0 || evidence.Events[cursor] is not { Payload: PublicMapChosen chosen, OwnerOrdinal: { } mapOwner }) return fallback;
        var mapStart = evidence.Events.First(e => e.OwnerOrdinal == mapOwner).Payload as PublicOwnerStarted;
        if (mapStart is null || mapStart.ActIndex != start.ActIndex || mapStart.Floor + 1 != start.Floor) return fallback;
        var map = (PublicMapObserved)evidence.Events[(int)chosen.OfferEventOrdinal].Payload;
        RoomType? roomType = map.Nodes.Single(n => n.Coordinate == chosen.Coordinate).NodeType switch
        { PublicMapNodeType.Monster => Sts2Sim.Core.Rooms.RoomType.Monster,
            PublicMapNodeType.Elite => Sts2Sim.Core.Rooms.RoomType.Elite,
            PublicMapNodeType.Boss => Sts2Sim.Core.Rooms.RoomType.Boss, _ => null };
        if (roomType is null) return fallback;
        ActDefinition[] acts = start.ActIndex switch
        { 0 => [new Overgrowth(), new Underdocks()], 1 => [new Hive()], 2 => [new Glory()], _ => [] };
        if (acts.Length == 0) return fallback;
        var encounters = acts.SelectMany(act => roomType switch
        { Sts2Sim.Core.Rooms.RoomType.Monster => act.MonsterEncounterCandidates,
            Sts2Sim.Core.Rooms.RoomType.Elite => act.EliteEncounterCandidates, _ => act.BossEncounterCandidates }).ToArray();
        // GremlinMerc's pinned custom proportion is 0, 1/2, or 1; default is 1 - escaped/spawned.
        if (encounters.Any(e => e.MinGoldReward is not null || e.MaxGoldReward is not null
            || e.GoldProportionCalculator is not null && e.Name != "GremlinMercNormal")) return fallback;
        (int min, int max) = roomType switch
        { Sts2Sim.Core.Rooms.RoomType.Monster => (10, 20), Sts2Sim.Core.Rooms.RoomType.Elite => (35, 45), _ => (100, 100) };
        if (((PublicRunStarted)evidence.Events[0].Payload).Ascension >= 3) { min = (int)(min * 0.75); max = (int)(max * 0.75); }
        var ranges = AllProportionalRanges(min, max);
        ulong maximum = ranges.Max(range => NativeRewardResourceMath.GoldBucket(range.Min, range.Max, gold).Size);
        if (maximum == 0) throw new NativePublicConstraintMismatchException("Public gold is outside every certified map reward range");
        return new(new(maximum, BigInteger.One << 53), ranges, roomType, start.ActIndex, encounters.Select(e => e.Name));
    }

    internal static NativeGoldEnvelopeCertificate ExactWeak(string[] encounterNames, int ascension, int actIndex, int gold)
    {
        int min = ascension >= 3 ? 7 : 10, max = ascension >= 3 ? 15 : 20;
        ulong mass = NativeRewardResourceMath.GoldBucket(min, max, gold).Size;
        if (mass == 0) throw new NativePublicConstraintMismatchException("Public weak-combat gold has no standard native range support");
        return new(new(mass, BigInteger.One << 53), [(min, max)], Sts2Sim.Core.Rooms.RoomType.Monster,
            actIndex, encounterNames) { _exactWeak = true };
    }

    internal static IReadOnlyList<(int Min, int Max)> AllProportionalRanges(int min, int max)
    {
        if (min < 0 || max < min || max > 100) throw new ArgumentOutOfRangeException(nameof(max));
        // Positive IEEE float bit patterns are ordered. Enumerate exactly every transition
        // of the native int*float, then banker's-round conversion, without sampling p.
        const uint end = 0x3f800001; // One past 1f.
        var boundaries = new SortedSet<uint> { 0, end };
        foreach (int bound in new[] { min, max })
            for (int value = 0; value <= bound; value++)
            {
                uint low = 0, high = end;
                while (low < high)
                {
                    uint middle = low + (high - low) / 2;
                    if (Math.Round(bound * BitConverter.UInt32BitsToSingle(middle)) <= value) low = middle + 1;
                    else high = middle;
                }
                boundaries.Add(low);
            }
        return boundaries.Where(bits => bits < end).Select(bits =>
        {
            float p = BitConverter.UInt32BitsToSingle(bits);
            return ((int)Math.Round(min * p), (int)Math.Round(max * p));
        }).Distinct().ToArray();
    }

    internal void ValidateBoundary(LabelCombatRewardContext context)
    {
        if (RoomType is null) return;
        if (context.Player.RunState is not RunState run || run.CurrentActIndex != _actIndex
            || context.RoomType != RoomType || run.CurrentRoomCount != 1
            || run.CurrentRoom is not CombatRoom { Won: true } room || !ReferenceEquals(room.Encounter, context.Encounter)
            || context.Encounter is not { } encounter || !_encounterNames.Contains(_exactWeak ? encounter.IdEntry : encounter.Name)
            || encounter.MinGoldReward is not null || encounter.MaxGoldReward is not null || context.FixedGoldAmount is not null
            || !float.IsFinite(context.GoldProportion) || context.GoldProportion is < 0f or > 1f)
            throw new NativePublicConstraintMismatchException("Native gold boundary differs from its certified public direct-map combat");
        if (_exactWeak && (context.GoldProportion != 1f || context.Encounter is not { IsWeak: true, GoldProportionCalculator: null }))
            throw new NativePublicConstraintMismatchException("Native weak-combat gold differs from the complete public history certificate");
    }

    internal void ValidateRange(LabelGoldRewardContext context)
    {
        if (RoomType is not null && !Ranges.Contains((context.Min, context.Max)))
            throw new InvalidOperationException("Native gold range departed from the public catalog envelope");
    }
}
