using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Joint native rarity/index proposal for two public ordered grants. CombatPotionGeneration
/// remains on the existing full-state oracle. The owning tape supplies exact fresh-cell,
/// stream, alias and replay checks; this component never reads a source run or seed.
/// </summary>
internal sealed class NativeNeowPotionProposal(NativeNeowPotionCondition condition, Func<ulong> nextWord,
    Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords, Action<Exception>? captureFailure = null)
{
    private RunState? _run;
    private bool _begun, _complete, _failed;
    internal int ConditionedPotionCount { get; private set; }
    internal ShuffleRational NativeToProposalRatio { get; private set; } = new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;
    internal IDisposable EnterScope() => LabelPhialHolsterScope.Enter(BeginGrant);

    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Neow potion proposal already owns a native run");
        _run = run;
    }

    internal IDisposable? BeginGrant(LabelPhialHolsterContext context)
    {
        if (_run?.CurrentRoom is not EventRoom { Event: Neow }) return null;
        try
        {
            var player = context.Player;
            if (_begun || _run.Players.Count != 1 || !ReferenceEquals(player.RunState, _run)
                || !ReferenceEquals(player, _run.Players[0]) || _run.CurrentActIndex != 0 || _run.TotalFloor != 1
                || player.Character is not Silent || _run.Rng.UsesSemanticKeys || player.PlayerRng.UsesSemanticKeys
                || !ReferenceEquals(context.Rng, _run.Rng.CombatPotionGeneration)
                || context.Rng.ToSerializable().LabelProvenance is not
                    { Law: LabelRandomProvenance.LawId or LabelRandomProvenance.MapLawId,
                        Partition: LabelRandomProvenance.FullStatePartition, OriginFamily: null, InitialSeed: null, RawCursor: null, ActIndex: null }
                || !player.Relics.Select(r => r.GetType().Name).SequenceEqual(new[] { nameof(RingOfTheSnake), nameof(PhialHolster) })
                || player.PotionSlots.Count != 3 || player.PotionSlots.Any(p => p is not null)
                || !context.Pool.SequenceEqual(condition.Pool))
                throw new InvalidOperationException("Neow potion grant escaped its owned native startup boundary or full-state provenance");
            _begun = true;
            var words = new List<ulong>();
            var ratio = new ShuffleRational(1, 1);
            for (int slot = 0; slot < 2; slot++)
            {
                var plan = NativeRewardResourceMath.Potion(condition.SlotPools[slot], condition.PotionIds[slot], nextWord);
                if (plan.RawWords.Count != 2 || plan.NativeToProposalRatio != condition.SlotMasses[slot])
                    throw new InvalidOperationException("Neow potion rarity/index law departed from its fixed public certificate");
                words.AddRange(plan.RawWords); ratio = ratio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
            }
            var expected = context.Rng.ToSerializable();
            int counter = context.Rng.Counter;
            var primitive = new MegaRandom(expected);
            for (int i = 0; i < words.Count; i++) primitive.NextULong();
            primitive.FillSerializableState(expected);
            var force = forcePrefixWords(words, context.Rng, "public Neow PhialHolster potions");
            NativeToProposalRatio = ratio;
            return new Boundary(force, () => _failed = true, captureFailure, () =>
            {
                try
                {
                    if (_failed) return;
                    var actual = context.Rng.ToSerializable();
                    if (context.Rng.Counter != counter + 4 || actual.state0 != expected.state0 || actual.state1 != expected.state1
                        || actual.state2 != expected.state2 || actual.state3 != expected.state3
                        || !player.PotionSlots.Select(p => p?.GetType().Name).SequenceEqual(condition.PotionIds.Cast<string?>().Append(null)))
                        throw new InvalidOperationException("Native Neow potions skipped, added, restored, or failed their certified grants");
                    ConditionedPotionCount = 2; _complete = true;
                }
                catch (Exception error) { _failed = true; captureFailure?.Invoke(error); throw; }
            });
        }
        catch (Exception error) { _failed = true; captureFailure?.Invoke(error); throw; }
    }

    internal void ValidateCompletion()
    {
        if (!_complete || _failed || ConditionedPotionCount != 2 || NativeToProposalRatio != Envelope)
            throw new InvalidOperationException("Neow potion proposal did not complete its two public grants");
    }

    internal bool AcceptCorrection(Func<ulong> correctionWord)
    {
        ValidateCompletion();
        return NativeNeowProposal.ExactRationalBernoulli(new(NativeToProposalRatio.Numerator * Envelope.Denominator,
            NativeToProposalRatio.Denominator * Envelope.Numerator), correctionWord);
    }

    private sealed class Boundary(IDisposable force, Action abort, Action<Exception>? captureFailure, Action complete) : IAbortableLabelPhialHolsterBoundary
    {
        private bool _disposed, _aborted;
        public void Abort()
        {
            _aborted = true; abort();
            (force as IAbortableConditionedWordScope)?.Abort();
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_aborted)
            {
                // Cleanup must not replace the original native failure with a missing-prefix error.
                try { force.Dispose(); } catch { }
                return;
            }
            try { force.Dispose(); complete(); }
            catch (Exception error) { abort(); captureFailure?.Invoke(error); throw; }
        }
    }
}
