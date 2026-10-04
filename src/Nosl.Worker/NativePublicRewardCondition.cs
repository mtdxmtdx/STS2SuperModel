using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.CardPools;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativePublicRewardTarget(int CombatIndex, int ActIndex, int Floor,
    long OfferEventOrdinal, IReadOnlyList<string> BaseCardIds, NativeRewardPoolCertificate Certificate,
    NativePublicRewardHistoryTarget? PublicHistory = null);

/// <summary>
/// Detached public evidence only. Each first displayed primary offer identifies three
/// native base identities, including unpicked cards. Refresh/reroll/append observations
/// remain in the caller's complete public equality check; they add no proposal factor.
/// </summary>
internal sealed class NativePublicRewardCondition
{
    internal string Character { get; }
    internal IReadOnlyDictionary<int, NativePublicRewardTarget> Targets { get; }
    internal ShuffleRational Envelope { get; }

    private NativePublicRewardCondition(string character, Dictionary<int, NativePublicRewardTarget> targets)
    {
        Character = character;
        Targets = new System.Collections.ObjectModel.ReadOnlyDictionary<int, NativePublicRewardTarget>(targets);
        Envelope = targets.Values.Aggregate(new ShuffleRational(1, 1), (product, target) =>
            product.Multiply(target.Certificate.Envelope.Numerator, target.Certificate.Envelope.Denominator));
    }

    internal static NativePublicRewardCondition Create(PublicRunEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.CompleteFromRunStart || evidence.Events.FirstOrDefault()?.Payload is not PublicRunStarted start)
            throw new InvalidOperationException("Public reward proposal requires a complete typed run prefix");
        CharacterModel character = ModelDb.AllCharacters.SingleOrDefault(c => c.GetType().Name == start.Character)
            ?? throw new InvalidOperationException("Public character has no native catalog");
        var owners = new Dictionary<long, (PublicOwnerStarted Start, int Combat)>();
        var rewards = new Dictionary<long, (PublicOwnerStarted Start, int Combat)>();
        var observedRewards = new HashSet<long>();
        var targets = new Dictionary<int, NativePublicRewardTarget>();
        (PublicOwnerStarted Start, int Combat)? pending = null;
        int combatIndex = -1;
        foreach (var entry in evidence.Events)
        {
            if (entry.Payload is PublicOwnerStarted owner && entry.OwnerOrdinal is { } ownerId)
            {
                if (owner.OwnerKind == PublicEvidenceOwnerKind.Combat)
                {
                    combatIndex++;
                    pending = null;
                }
                owners.Add(ownerId, (owner, combatIndex));
                if (owner.OwnerKind == PublicEvidenceOwnerKind.Reward && pending is { } closed
                    && closed.Start.ActIndex == owner.ActIndex && closed.Start.Floor == owner.Floor
                    && closed.Start.ParentOwnerOrdinal == owner.ParentOwnerOrdinal)
                {
                    rewards.Add(ownerId, (owner, closed.Combat));
                    pending = null; // Only the first reward owner following this closed combat.
                }
            }
            else if (entry.Payload is PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Victory }
                && entry.OwnerOrdinal is { } endedId && owners[endedId].Start.OwnerKind == PublicEvidenceOwnerKind.Combat)
                pending = owners[endedId];
            else if (entry.Payload is PublicOffersObserved offer && entry.OwnerOrdinal is { } rewardId
                && rewards.TryGetValue(rewardId, out var reward) && observedRewards.Add(rewardId))
            {
                var groups = offer.Groups.Where(group => group.GroupKind == PublicOfferGroupKind.Primary
                    && group.Offers.Any(item => item.OfferKind == PublicOfferKind.Card)).ToArray();
                // A no-standard-reward forced fight can return to its event and
                // display a potion/relic-only custom owner at this same boundary.
                // Classify only its first snapshot; later refreshes and owners
                // cannot manufacture a certified primary combat card generation.
                if (groups.Length == 0) continue;
                if (groups.Length != 1) throw new InvalidOperationException("Primary native combat card offer is ambiguous");
                var cards = groups[0].Offers.Where(item => item.OfferKind == PublicOfferKind.Card).ToArray();
                if (cards.Length < 3 || !cards.Take(3).Select(item => item.Key)
                    .SequenceEqual(new[] { "card:card:0", "card:card:1", "card:card:2" }))
                    throw new InvalidOperationException("Primary native base-card positions are not certified");
                string[] ids = cards.Take(3).Select(item => item.Card!.Id).ToArray();
                var certificate = NativeRewardPoolCertificate.Create(character, ids);
                targets.Add(reward.Combat, new(reward.Combat, reward.Start.ActIndex, reward.Start.Floor,
                    entry.EventOrdinal, Array.AsReadOnly(ids), certificate));
            }
        }
        NativePublicRewardHistoryCertificate.Tighten(evidence, targets, character);
        return new(start.Character, targets);
    }
}

/// <summary>
/// Root-constant conservative envelope. All four pinned native creation-pool configurations
/// cover every public inventory, including active/melted Rug/Gem uncertainty. Enumerating
/// reference exclusions handles all matching duplicate entries without an observed-world bound.
/// </summary>
internal sealed class NativeRewardPoolCertificate
{
    private readonly IReadOnlyList<CardModel[]>[] _remainingBySlot;
    private IReadOnlyList<LabelCardRarityThresholds>? _publicThresholds;
    internal IReadOnlyList<ShuffleRational> SlotBounds { get; }
    internal ShuffleRational Envelope { get; }

    private NativeRewardPoolCertificate(IReadOnlyList<CardModel[]>[] remaining, ShuffleRational[] bounds)
    {
        _remainingBySlot = remaining; SlotBounds = Array.AsReadOnly(bounds);
        Envelope = bounds.Aggregate(new ShuffleRational(1, 1), (p, b) => p.Multiply(b.Numerator, b.Denominator));
    }

    internal static NativeRewardPoolCertificate Create(CharacterModel character, IReadOnlyList<string> targets, int bits = 53)
    {
        var all = ModelDb.AllCharacters.Select(c => c.CardPool).ToArray();
        CardPoolModel[][] pools = [[character.CardPool], [character.CardPool, ColorlessCardPool.Instance],
            all, [.. all, ColorlessCardPool.Instance]];
        var unlock = PlayerUnlockState.AllUnlocked();
        return FromPools(pools.Select(pool => pool.SelectMany(p => p.GetUnlockedCards(unlock, false)).ToArray()), targets, bits);
    }

    internal static NativeRewardPoolCertificate FromPools(IEnumerable<CardModel[]> pools,
        IReadOnlyList<string> targets, int bits = 53)
    {
        if (bits is < 1 or > 53) throw new ArgumentOutOfRangeException(nameof(bits));
        var remaining = pools.ToArray();
        var perSlot = new IReadOnlyList<CardModel[]>[targets.Count];
        var bounds = new ShuffleRational[targets.Count];
        for (int slot = 0; slot < targets.Count; slot++)
        {
            perSlot[slot] = Array.AsReadOnly(remaining);
            BigInteger maximum = BigInteger.Zero;
            foreach (var pool in remaining)
                foreach (var rarity in new[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common })
                {
                    var candidates = pool.Where(c => c.Rarity == rarity).ToArray();
                    BigInteger mass = BigInteger.Zero;
                    for (int i = 0; i < candidates.Length; i++)
                        if (candidates[i].GetType().Name == targets[slot])
                            mass += ConditionalShuffleProposal.Factor(candidates.Length, i, bits).BucketSize;
                    maximum = BigInteger.Max(maximum, mass);
                }
            bounds[slot] = new(maximum, BigInteger.One << bits);
            if (maximum.IsZero) throw new NativePublicConstraintMismatchException("Public primary card has no native catalog pool support");
            remaining = remaining.SelectMany(pool => pool.Where(card => card.GetType().Name == targets[slot])
                    .Distinct(ReferenceEqualityComparer.Instance)
                    .Select(selected => pool.Where(card => !ReferenceEquals(card, selected)).ToArray())).ToArray();
        }
        return new(perSlot, bounds);
    }

    internal static NativeRewardPoolCertificate FromPublicHistory(CardModel[] pool,
        IReadOnlyList<string> targets, IReadOnlyList<LabelCardRarityThresholds> thresholds, int bits = 53)
    {
        if (targets.Count != thresholds.Count || pool.Select(c => c.GetType().Name).Distinct().Count() != pool.Length)
            throw new ArgumentException("Exact public reward history requires one canonical model per identity");
        var certificate = FromPools([pool], targets, bits);
        var bounds = new ShuffleRational[targets.Count];
        for (int slot = 0; slot < targets.Count; slot++)
        {
            var remaining = certificate._remainingBySlot[slot].Single();
            var branches = new[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common }
                .Select(rarity => new LabelRewardCardBranch(rarity, remaining.Where(c => c.Rarity == rarity).ToArray())).ToArray();
            var mass = NativeRewardIdentityMath.Arms(branches, thresholds[slot], targets[slot], bits)
                .Aggregate(BigInteger.Zero, (total, arm) => total + arm.Mass);
            if (mass.IsZero) throw new NativePublicConstraintMismatchException("Public primary card has no certified history mass");
            bounds[slot] = new(mass, BigInteger.One << (2 * bits));
        }
        return new(certificate._remainingBySlot, bounds) { _publicThresholds = thresholds };
    }

    internal void Validate(LabelRewardCardSelectionContext context)
    {
        if (context.Kind != LabelRewardCardSelectionKind.LegacyCombat || context.Thresholds is null
            || !context.ChangesFutureOdds || context.Source != CardCreationSource.Encounter
            || !context.Flags.HasFlag(CardCreationFlags.IsCardReward | CardCreationFlags.IsFromCombat)
            || context.SelectionIndex >= _remainingBySlot.Length)
            throw new InvalidOperationException("Primary reward departed from the certified native overload");
        if (!_remainingBySlot[context.SelectionIndex].Any(pool => pool.SequenceEqual(context.RemainingCards)))
            throw new InvalidOperationException("Native reward pool is outside the root-wide catalog certificate");
        if (_publicThresholds is not null && (context.OddsType != CardRarityOddsType.RegularEncounter
            || context.Thresholds != _publicThresholds[context.SelectionIndex]))
            throw new NativePublicConstraintMismatchException("Native rarity thresholds differ from the complete public offer history");
        foreach (var branch in context.Branches)
            if (!context.RemainingCards.Where(card => card.Rarity == branch.RolledRarity).SequenceEqual(branch.Candidates))
                throw new InvalidOperationException("Legacy native reward branch or exclusion policy changed");
    }
}

/// <summary>One owned hypothetical replay. The caller supplies its Rewards oracle and independent proposal words.</summary>
internal sealed class NativePublicRewardProposal(NativePublicRewardCondition condition,
    Func<LabelRandomAddressV1, bool> wasVisited, Action<LabelRandomAddressV1, ulong> forceFresh,
    Func<ulong> nextProposalWord, Action<Exception>? captureFailure = null)
{
    private NativePublicRewardTarget? _active;
    private Rng? _rewards;
    private int _slot;
    private bool _failed;
    private readonly HashSet<int> _completed = [];
    internal ShuffleRational NativeToProposalRatio { get; private set; } = new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;
    internal int ConditionedCardCount { get; private set; }

    internal IDisposable? BeginCombatReward(LabelCombatRewardContext context, int combatIndex)
    {
        try
        {
            if (!condition.Targets.TryGetValue(combatIndex, out var target)) return null;
            if (_active is not null || _completed.Contains(combatIndex))
                throw new InvalidOperationException("Public primary reward boundary repeated or nested");
            if (context.Player.RunState is not RunState run || run.Players.Count != 1
                || !ReferenceEquals(context.Rng, context.Player.PlayerRng.Rewards))
                throw new InvalidOperationException("Primary reward boundary requires the owned solo native Rewards RNG");
            if (run.CurrentActIndex != target.ActIndex || run.TotalFloor != target.Floor
                || context.Player.Character.GetType().Name != condition.Character)
                throw new NativePublicConstraintMismatchException("Native reward boundary differs from its public combat owner");
            _active = target; _rewards = context.Rng; _slot = 0; _failed = false;
            return new Completion(error => { _failed = true; captureFailure?.Invoke(error); }, () =>
            {
                _active = null; _rewards = null;
                if (_failed) return;
                if (_slot != 3) throw new InvalidOperationException("Native primary reward did not execute all three certified base slots");
                _completed.Add(combatIndex);
            });

        }
        catch (Exception error) { _failed = true; captureFailure?.Invoke(error); throw; }
    }

    internal IDisposable? BeginSelection(LabelRewardCardSelectionContext context)
    {
        if (_active is null || _slot == 3) return null; // Extras/LastingCandy and all noncombat sources retain Q=P.
        try
        {
            if (!ReferenceEquals(context.Rng, _rewards) || context.SelectionIndex != _slot || context.OptionCount != 3)
                throw new InvalidOperationException("Unexpected native primary selection order or RNG");
            _active.Certificate.Validate(context);
            LabelRandomAddressV1 address = Address(context.Rng);
            var second = address with { RawCursor = checked(address.RawCursor + 1) };
            if (wasVisited(address) || wasVisited(second))
                throw new InvalidOperationException("Primary reward proposal requires fresh Rewards cells; alias invalidates its envelope");
            var plan = NativeRewardIdentityMath.Create(context, _active.BaseCardIds[_slot], nextProposalWord);
            var bound = _active.Certificate.SlotBounds[_slot];
            if (plan.NativeToProposalRatio.Numerator * bound.Denominator > plan.NativeToProposalRatio.Denominator * bound.Numerator)
                throw new InvalidOperationException("Native identity mass exceeded the root-wide pool envelope");
            if (plan.RawWords.Count != 2) throw new InvalidOperationException("Native primary rarity/index draw contract changed");
            forceFresh(address, plan.RawWords[0]); forceFresh(second, plan.RawWords[1]);
            NativeToProposalRatio = NativeToProposalRatio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
            _slot++; ConditionedCardCount++;
            return new Completion(error => { _failed = true; captureFailure?.Invoke(error); }, () =>
            {
                var after = Address(context.Rng);
                if (after != address with { RawCursor = checked(address.RawCursor + 3) }
                    || !wasVisited(address) || !wasVisited(second))
                    throw new InvalidOperationException("Native primary generation restored, skipped or added Rewards draws");
            });
        }
        catch (Exception error) { _failed = true; captureFailure?.Invoke(error); throw; }
    }

    internal void AbortActiveBoundary() => _failed = true;

    internal void ValidateCompletion()
    {
        if (_active is not null || _failed || _completed.Count != condition.Targets.Count)
            throw new InvalidOperationException("Public reward proposal did not complete every recorded primary offer");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        ValidateCompletion();
        return NativeNeowProposal.ExactRationalBernoulli(new(
            NativeToProposalRatio.Numerator * Envelope.Denominator,
            NativeToProposalRatio.Denominator * Envelope.Numerator), nextWord);
    }

    private static LabelRandomAddressV1 Address(Rng rng)
    {
        var p = rng.ToSerializable().LabelProvenance;
        if (p is not { Law: LabelRandomProvenance.LawId or LabelRandomProvenance.MapLawId,
            Partition: LabelRandomProvenance.RewardsPartition,
            OriginFamily: LabelRandomProvenance.RewardsOrigin, InitialSeed: { } seed, RawCursor: { } cursor })
            throw new InvalidOperationException("Primary reward identity conditioning requires tagged native Rewards provenance");
        return new(LabelRandomProvenance.RewardsOrigin, seed, cursor);
    }

    private sealed class Completion(Action<Exception> failure, Action action) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; try { action(); } catch (Exception error) { failure(error); throw; } }
    }
}
