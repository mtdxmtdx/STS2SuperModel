using System.Collections.ObjectModel;
using Nosl.Contracts;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Detached startup shapes, taken from first published intents rather than a later
/// live move or private move ID. The public-combat-v2 closure proves creation order,
/// self-Ravenous publication, and absence of startup roster/intent-changing hooks.
/// </summary>
internal sealed class NativePublicCorpseSlugIntentCondition
{
    internal IReadOnlyDictionary<int, NativeCorpseSlugIntentInput> Combats { get; }
    internal int EligibleCombatCount => Combats.Count;
    private NativePublicCorpseSlugIntentCondition(Dictionary<int, NativeCorpseSlugIntentInput> combats)
        => Combats = new ReadOnlyDictionary<int, NativeCorpseSlugIntentInput>(combats);

    internal static NativePublicCorpseSlugIntentCondition Create(DecisionPacket root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var targets = new Dictionary<int, NativeCorpseSlugIntentInput>();
        if (root.PublicEvidence is not { } evidence) return new(targets);
        var prefixes = NativePublicCombatPrefixCondition.Create(root);
        foreach (var prefix in prefixes.Combats.Values.Where(input => input.Shuffle is not null))
        {
            var events = evidence.Events.Where(entry => entry.OwnerOrdinal == prefix.OwnerOrdinal).ToArray();
            // Only ordinary map combat lifecycle is certified. Combat-layout event
            // preparation can happen before CombatEntering; never guess an offset.
            if (events[0].Payload is not PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Combat, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true }) continue;
            int turn = Array.FindIndex(events, entry => entry.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 });
            var intents = events.Skip(turn + 1).TakeWhile(entry => entry.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.IntentPublished })
                .Select(entry => (PublicCombatFact)entry.Payload).ToArray();
            if (turn < 0 || intents.Length is not (2 or 3)
                || intents.Any(intent => intent.Model != nameof(CorpseSlug))
                || !intents.Select(intent => intent.TargetSlot).SequenceEqual(
                    Enumerable.Range(0, intents.Length).Select(slot => (int?)slot))) continue;
            int[] indices = intents.Select(intent => PublicShapeIndex(intent.Intents)).ToArray();
            if (indices[0] < 0 || !indices.Select((index, slot) => index == (indices[0] + slot) % 3).All(match => match)) continue;
            targets.Add(prefix.CombatIndex, new(prefix.CombatIndex, prefix.OwnerOrdinal,
                prefix.EntryJson!, intents.Length, indices[0]));
        }
        return new(targets);
    }

    private static int PublicShapeIndex(IReadOnlyList<PublicIntent> intents) => intents.Count == 1
        ? intents[0] switch
        {
            { Kind: "Attack", Damage: 3, Repeats: 2 } => 0,
            { Kind: "Attack", Damage: 9, Repeats: 1 } => 1,
            { Kind: "Debuff", Damage: null, Repeats: null } => 2,
            _ => -1,
        } : -1;
}

internal sealed record NativeCorpseSlugIntentInput(int CombatIndex, long OwnerOrdinal,
    string EntryJson, int EnemyCount, int StarterIndex)
{
    internal NativeCorpseSlugIntentPlan CreateProposal(LabelCorpseSlugInitialIntentsContext context,
        Func<ulong> nextWord)
    {
        if (context.Monsters.Count != EnemyCount || context.Monsters.Any(monster => monster is not CorpseSlug))
            throw new NativePublicConstraintMismatchException("Native slug factory differs from its public initial roster");
        string expectedId = EnemyCount == 2 ? "CORPSE_SLUGS_WEAK" : "CORPSE_SLUGS_NORMAL";
        var encounter = UnderdocksEncounters.BatchA.Concat(UnderdocksEncounters.BatchB)
            .Single(definition => definition.IdEntry == expectedId);
        if (!ReferenceEquals(context.Encounter, encounter) || encounter.Slots.Count != 0
            || context.Run is null || context.Run.Rng.UsesSemanticKeys
            || !context.Run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || !ReferenceEquals(context.FactoryRng, context.Rng) || context.Rng.Counter != 0
            || context.Monsters.Any(monster => !monster.IsMutable || monster.Creature is not null)
            || context.Monsters.Distinct(ReferenceEqualityComparer.Instance).Count() != EnemyCount)
            throw new InvalidOperationException("Native slug initializer differs from its unslotted factory certificate");
        return NativeCorpseSlugIntentPlan.Create(StarterIndex, nextWord);
    }
}

/// <summary>
/// Uniform full raw-word preimage of native NextInt(3), including all discarded
/// low bits and IEEE multiplication rounding. The public cyclic shapes uniquely
/// fix the bucket, so the exact native/proposal ratio is already root-constant.
/// </summary>
internal sealed record NativeCorpseSlugIntentPlan(ulong RawWord, ConditionalShuffleFactor Factor, int PrecisionBits)
{
    internal ShuffleRational NativeToProposalRatio => new(Factor.BucketSize, 1UL << PrecisionBits);
    internal ShuffleRational Envelope => NativeToProposalRatio;
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }

    internal static NativeCorpseSlugIntentPlan Create(int starterIndex, Func<ulong> nextWord, int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        var factor = ConditionalShuffleProposal.Factor(3, starterIndex, precisionBits);
        if (factor.BucketSize == 0) throw new ArgumentException("Startup bucket has no native support");
        int lowBits = 64 - precisionBits;
        ulong word = ((factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord)) << lowBits)
            | (nextWord() & ((1UL << lowBits) - 1));
        return new(word, factor, precisionBits);
    }
}
