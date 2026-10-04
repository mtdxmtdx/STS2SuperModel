using Nosl.Contracts;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Ordered startup roster from the complete public combat owner of the declared
/// constructed Ruby encounter. The existing startup closure rules out roster and
/// intent-changing startup hooks. No current snapshot or source recipe is used.
/// </summary>
internal sealed class NativePublicRubyFormationCondition
{
    internal long CombatOwnerOrdinal { get; }
    internal string EntryJson { get; }
    internal IReadOnlyList<string> Roster { get; }
    internal ShuffleRational Envelope { get; }

    private NativePublicRubyFormationCondition(long owner, string entry, string[] roster)
    {
        CombatOwnerOrdinal = owner; EntryJson = entry; Roster = Array.AsReadOnly(roster);
        Envelope = NativeRubyFormationPlan.Mass(roster);
    }

    internal static bool TryCreateConstructed(DecisionPacket root, NativeConstructedTapePrior prior,
        out NativePublicRubyFormationCondition? condition, out string? reason)
    {
        condition = null;
        reason = "declared_constructed_ruby_encounter_required";
        if (prior.Setup.Encounter != "RubyRaiders") return false;
        var prefixes = NativePublicCombatPrefixCondition.CreateConstructed(root, prior);
        reason = "certified_constructed_ruby_startup_required";
        if (prefixes.Combats.Count != 1 || !prefixes.Combats.TryGetValue(0, out var prefix)
            || prefix.Shuffle is null || prefix.EntryJson is null) return false;
        var events = root.PublicEvidence!.Events.Where(e => e.OwnerOrdinal == prefix.OwnerOrdinal).ToArray();
        int turn = Array.FindIndex(events, e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 });
        var intents = events.Skip(turn + 1).TakeWhile(e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.IntentPublished }).Select(e => (PublicCombatFact)e.Payload).ToArray();
        reason = "ordered_full_ruby_startup_roster_required";
        if (turn < 0 || intents.Length != 3
            || !intents.Select(intent => intent.TargetSlot).SequenceEqual(new int?[] { 0, 1, 2 })) return false;
        string[] roster = intents.Select(intent => intent.Model!).ToArray();
        if (!NativeRubyFormationCatalog.Tickets.Any(ticket => ticket.Roster.SequenceEqual(roster, StringComparer.Ordinal)))
            return false;
        condition = new(prefix.OwnerOrdinal, prefix.EntryJson, roster); reason = null; return true;
    }

    internal NativeRubyFormationPlan CreateProposal(LabelRubyRaidersFormationContext context, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(nextWord);
        if (context.Run is not { CurrentActIndex: 0, Act: Overgrowth } run || run.Rng.UsesSemanticKeys
            || run.Players.Count != 1 || run.Players[0].Character is not Silent
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || !ReferenceEquals(context.FactoryRng, context.Rng) || context.Rng.Counter != 0
            || context.Encounter is not { Slots.Count: 0 }
            || !ReferenceEquals(context.Encounter, run.Act.MonsterEncounterCandidates.Single(
                encounter => encounter.IdEntry == "RUBY_RAIDERS_NORMAL")))
            throw new InvalidOperationException("Native Ruby formation differs from its unslotted factory certificate");
        return NativeRubyFormationPlan.Create(Roster, nextWord);
    }
}

internal sealed record NativeRubyFormationTicket(IReadOnlyList<int> Choices, IReadOnlyList<string> Roster);

/// <summary>
/// Exhausts the native 5*4*3 without-replacement branches, retaining output slot
/// order. Initial moves in all five Ruby models are deterministic; the observed
/// ordered formation therefore also fixes their initial public intent identities.
/// </summary>
internal static class NativeRubyFormationCatalog
{
    private static readonly Lazy<IReadOnlyList<NativeRubyFormationTicket>> Catalog = new(Build);
    internal static IReadOnlyList<NativeRubyFormationTicket> Tickets => Catalog.Value;
    private static IReadOnlyList<NativeRubyFormationTicket> Build()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = new Overgrowth().MonsterEncounterCandidates.Single(item => item.IdEntry == "RUBY_RAIDERS_NORMAL");
        var tickets = new List<NativeRubyFormationTicket>();
        using var disabled = LabelRubyRaidersScope.Enter(_ => null);
        for (int first = 0; first < 5; first++)
        for (int second = 0; second < 4; second++)
        for (int third = 0; third < 3; third++)
        {
            int[] choices = [first, second, third];
            ulong[] words = choices.Select((choice, i) => ConditionalShuffleProposal.Factor(5 - i, choice).BucketStart << 11).ToArray();
            int cursor = 0;
            using var scope = LabelRandomScope.Enter(_ => cursor < words.Length ? words[cursor++]
                : throw new InvalidOperationException("Native Ruby factory consumed an unreviewed draw"));
            var draws = new List<(int Ordinal, string Operation, string Result)>();
            var rng = Rng.CreateObserved(0, (ordinal, operation, result) => draws.Add((ordinal, operation, result)));
            var roster = encounter.CreateMonsters(rng);
            if (cursor != 3 || rng.Counter != 3 || roster.Count != 3 || roster.Any(item => item.SlotName is not null)
                || !draws.SequenceEqual(choices.Select((choice, i) => (i + 1,
                    "NextInt(minInclusive=0,maxExclusive=" + (5 - i) + ")", choice.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
                throw new InvalidOperationException("Native Ruby factory draw count or slot order changed");
            tickets.Add(new(Array.AsReadOnly(choices), Array.AsReadOnly(roster.Select(item => item.Monster.GetType().Name).ToArray())));
        }
        if (tickets.Select(ticket => string.Join(",", ticket.Roster)).Distinct(StringComparer.Ordinal).Count() != 60)
            throw new InvalidOperationException("Native Ruby factory no longer has one ticket per ordered roster");
        return tickets.AsReadOnly();
    }
}

/// <summary>
/// Uniform full 64-bit preimages of the single ticket fixed by public slot order.
/// The exact mass is the product of the native IEEE-aware 5,4,3 bucket widths,
/// not the rounded value 1/60. It is root-constant, so p/q equals its envelope.
/// </summary>
internal sealed class NativeRubyFormationPlan
{
    internal IReadOnlyList<ulong> RawWords { get; }
    internal IReadOnlyList<ConditionalShuffleFactor> Factors { get; }
    internal NativeRubyFormationTicket Ticket { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope => NativeToProposalRatio;
    private NativeRubyFormationPlan(ulong[] words, ConditionalShuffleFactor[] factors,
        NativeRubyFormationTicket ticket, ShuffleRational ratio)
    { RawWords = Array.AsReadOnly(words); Factors = Array.AsReadOnly(factors); Ticket = ticket; NativeToProposalRatio = ratio; }

    private static NativeRubyFormationTicket FindTicket(IReadOnlyList<string> roster) =>
        NativeRubyFormationCatalog.Tickets.SingleOrDefault(ticket => ticket.Roster.SequenceEqual(roster, StringComparer.Ordinal))
        ?? throw new ArgumentException("Public Ruby roster has no native factory ticket", nameof(roster));

    internal static ShuffleRational Mass(IReadOnlyList<string> roster, int precisionBits = 53)
    {
        var ticket = FindTicket(roster);
        var mass = new ShuffleRational(1, 1);
        for (int i = 0; i < 3; i++)
            mass = mass.Multiply(ConditionalShuffleProposal.Factor(5 - i, ticket.Choices[i], precisionBits).BucketSize, 1UL << precisionBits);
        return mass;
    }

    internal static NativeRubyFormationPlan Create(IReadOnlyList<string> roster, Func<ulong> nextWord, int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(roster); ArgumentNullException.ThrowIfNull(nextWord);
        if (precisionBits is < 1 or > 53) throw new ArgumentOutOfRangeException(nameof(precisionBits));
        var ticket = FindTicket(roster);
        var words = new ulong[3]; var factors = new ConditionalShuffleFactor[3];
        int lowBits = 64 - precisionBits; ulong lowMask = (1UL << lowBits) - 1;
        for (int i = 0; i < 3; i++)
        {
            var factor = factors[i] = ConditionalShuffleProposal.Factor(5 - i, ticket.Choices[i], precisionBits);
            if (factor.BucketSize == 0) throw new ArgumentException("Ruby ticket has no native support at this precision");
            words[i] = ((factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord)) << lowBits)
                | (nextWord() & lowMask);
        }
        return new(words, factors, ticket, Mass(roster, precisionBits));
    }
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }
}
