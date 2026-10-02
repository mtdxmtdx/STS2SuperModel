using System.Numerics;
using Sts2Sim.Core.Factories;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>Owns only fresh native Rewards cells for already displayed past-combat resources.</summary>
internal sealed class NativePublicRewardResourceProposal(NativePublicRewardResourceCondition condition,
    Func<LabelRandomAddressV1, bool> wasVisited, Action<LabelRandomAddressV1, ulong> forceFresh,
    Func<ulong> nextProposalWord)
{
    private NativePublicRewardResourceTarget? _active;
    private Rng? _rng;
    private bool _presenceSeen, _goldSeen, _potionSeen, _expectsPotion, _failed;
    private readonly HashSet<int> _completed = [];
    private ShuffleRational _boundaryRatio = new(1, 1);
    internal ShuffleRational NativeToProposalRatio { get; private set; } = new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;
    internal int ConditionedPresenceCount { get; private set; }
    internal int ConditionedGoldCount { get; private set; }
    internal int ConditionedPotionCount { get; private set; }
    // A dependent card boundary must be aborted before disposal when a resource
    // hook prevented native card generation, so its slot-count check cannot mask
    // the original resource exception. The flag survives boundary cleanup.
    internal bool HasFailed => _failed;
    internal void AbortActiveBoundary() => _failed = true;

    internal IDisposable? BeginCombatReward(LabelCombatRewardContext context, int combatIndex)
    {
        if (!condition.Targets.TryGetValue(combatIndex, out var target)) return null;
        if (_active is not null || _completed.Contains(combatIndex)) throw new InvalidOperationException("Public resource boundary repeated or nested");
        if (context.Player.RunState is not RunState run || run.Players.Count != 1
            || !ReferenceEquals(context.Rng, context.Player.PlayerRng.Rewards) || context.Player.PlayerRng.UsesSemanticKeys)
            throw new InvalidOperationException("Resource proposals require the owned solo native Rewards RNG");
        if (run.CurrentActIndex != target.Owner.ActIndex || run.TotalFloor != target.Owner.Floor
            || context.Player.Character.GetType().Name != condition.Character)
            throw new NativePublicConstraintMismatchException("Native resource boundary differs from its public owner");
        target.GoldCertificate.ValidateBoundary(context);
        _active = target; _rng = context.Rng;
        _presenceSeen = _goldSeen = _potionSeen = _expectsPotion = _failed = false;
        _boundaryRatio = new(1, 1);
        var scope = LabelRewardResourceScope.Enter(BeginPresence, BeginGold, BeginPotion);
        return new Completion(() =>
        {
            try
            {
                scope.Dispose();
                if (_failed) return;
                if (!_presenceSeen || !_goldSeen || _potionSeen != _expectsPotion)
                    throw new InvalidOperationException("Native reward did not complete its primary resource draw contract");
                var bound = target.GoldCertificate.Envelope;
                if (target.Potion is not null) bound = bound.Multiply(target.PotionIdentityMass.Numerator, target.PotionIdentityMass.Denominator);
                if (_boundaryRatio.Numerator * bound.Denominator > _boundaryRatio.Denominator * bound.Numerator)
                    throw new InvalidOperationException("Native resource mass exceeded its root-wide envelope");
                _completed.Add(combatIndex);
            }
            catch { _failed = true; throw; }
            finally { _active = null; _rng = null; }
        });
    }

    private bool PrimaryDone => _goldSeen && (!_expectsPotion || _potionSeen);

    private IDisposable? BeginPresence(LabelPotionPresenceContext context)
    {
        if (_active is null || PrimaryDone) return null;
        try
        {
            if (_presenceSeen || _goldSeen || !ReferenceEquals(context.Rng, _rng))
                throw new InvalidOperationException("Native primary potion presence order changed");
            // An absent display also includes any native empty-rarity branch.
            var plan = NativeRewardResourceMath.Presence(context.Forced, context.Threshold,
                _active.Potion is not null, _active.PotionIdentityMass, nextProposalWord);
            _expectsPotion = plan.Present;
            _presenceSeen = true; ConditionedPresenceCount++;
            return Force(context.Rng, plan.Plan);
        }
        catch { _failed = true; throw; }
    }

    private IDisposable? BeginGold(LabelGoldRewardContext context)
    {
        if (_active is null || PrimaryDone) return null;
        try
        {
            if (!_presenceSeen || _goldSeen || !ReferenceEquals(context.Rng, _rng))
                throw new InvalidOperationException("Native primary gold order changed");
            _active.GoldCertificate.ValidateRange(context);
            var plan = NativeRewardResourceMath.Gold(context.Min, context.Max, context.Omitted, _active.Gold, nextProposalWord);
            var bound = _active.GoldCertificate.Envelope;
            if (plan.NativeToProposalRatio.Numerator * bound.Denominator > plan.NativeToProposalRatio.Denominator * bound.Numerator)
                throw new InvalidOperationException("Native gold mass exceeded its root-wide range envelope");
            _goldSeen = true; ConditionedGoldCount++;
            return Force(context.Rng, plan);
        }
        catch { _failed = true; throw; }
    }

    private IDisposable? BeginPotion(LabelPotionSelectionContext context)
    {
        if (_active is null || PrimaryDone) return null;
        try
        {
            if (!_presenceSeen || !_goldSeen || !_expectsPotion || _potionSeen || !ReferenceEquals(context.Rng, _rng))
                throw new InvalidOperationException("Native primary potion identity order changed");
            if (!condition.PotionPool.SequenceEqual(context.Pool))
                throw new InvalidOperationException("Native potion pool departed from its fixed character catalog");
            var plan = NativeRewardResourceMath.Potion(context.Pool, _active.Potion, nextProposalWord);
            if (plan.NativeToProposalRatio != _active.PotionIdentityMass)
                throw new InvalidOperationException("Native potion identity mass changed");
            _potionSeen = true; ConditionedPotionCount++;
            return Force(context.Rng, plan);
        }
        catch { _failed = true; throw; }
    }

    private IDisposable Force(Rng rng, NativeResourcePlan plan)
    {
        var address = Address(rng);
        for (int i = 0; i < plan.RawWords.Count; i++)
            if (wasVisited(address with { RawCursor = checked(address.RawCursor + (ulong)i) }))
                throw new InvalidOperationException("Resource proposal requires fresh Rewards cells; alias invalidates its envelope");
        for (int i = 0; i < plan.RawWords.Count; i++)
            forceFresh(address with { RawCursor = checked(address.RawCursor + (ulong)i) }, plan.RawWords[i]);
        NativeToProposalRatio = NativeToProposalRatio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
        _boundaryRatio = _boundaryRatio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
        return new Completion(() =>
        {
            try
            {
                if (Address(rng) != address with { RawCursor = checked(address.RawCursor + (ulong)plan.RawWords.Count) }
                    || Enumerable.Range(0, plan.RawWords.Count).Any(i => !wasVisited(address with { RawCursor = checked(address.RawCursor + (ulong)i) })))
                    throw new InvalidOperationException("Native resource skipped, restored, or added Rewards draws");
            }
            catch { _failed = true; throw; }
        });
    }

    internal void ValidateCompletion()
    {
        if (_active is not null || _failed || _completed.Count != condition.Targets.Count)
            throw new InvalidOperationException("Public resource proposal did not complete every certified owner");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        ValidateCompletion();
        return NativeNeowProposal.ExactRationalBernoulli(new(NativeToProposalRatio.Numerator * Envelope.Denominator,
            NativeToProposalRatio.Denominator * Envelope.Numerator), nextWord);
    }

    private static LabelRandomAddressV1 Address(Rng rng)
    {
        if (rng.ToSerializable().LabelProvenance is not { Law: LabelRandomProvenance.LawId,
            Partition: LabelRandomProvenance.RewardsPartition, OriginFamily: LabelRandomProvenance.RewardsOrigin,
            InitialSeed: { } seed, RawCursor: { } cursor })
            throw new InvalidOperationException("Resource conditioning requires tagged native Rewards provenance");
        return new(LabelRandomProvenance.RewardsOrigin, seed, cursor);
    }

    private sealed class Completion(Action action) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; action(); }
    }
}
