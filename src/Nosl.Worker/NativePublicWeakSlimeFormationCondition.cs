using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Detached ordered startup roster for the already certified first normal weak
/// encounter. Missing proof disables this proposal; later rosters, source seeds,
/// private move IDs and native source objects are never inputs.
/// </summary>
internal sealed class NativePublicWeakSlimeFormationCondition
{
    internal long CombatOwnerOrdinal { get; }
    internal string EntryJson { get; }
    internal IReadOnlyList<string> Roster { get; }
    internal ShuffleRational Envelope { get; }

    private NativePublicWeakSlimeFormationCondition(long owner, string entry, string[] roster)
    {
        CombatOwnerOrdinal = owner; EntryJson = entry; Roster = Array.AsReadOnly(roster);
        Envelope = NativeWeakSlimeFormationPlan.Mass(Roster);
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicWeakSlimeFormationCondition? condition, out string? reason)
    {
        condition = null;
        if (!NativePublicOpeningEncounterCondition.TryCreate(root, prior, out var opening, out reason)) return false;
        reason = "certified_slimes_weak_opening_required";
        if (opening!.TargetActType != typeof(Overgrowth) || opening.TargetEncounterId != "SLIMES_WEAK") return false;
        var events = root.PublicEvidence!.Events.Where(entry => entry.OwnerOrdinal == opening.CombatOwnerOrdinal).ToArray();
        int turn = Array.FindIndex(events, entry => entry.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 });
        var intents = events.Skip(turn + 1).TakeWhile(entry => entry.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.IntentPublished }).Select(entry => (PublicCombatFact)entry.Payload).ToArray();
        reason = "ordered_full_slimes_weak_startup_roster_required";
        if (intents.Length != 3 || !intents.Select(intent => intent.TargetSlot).SequenceEqual(new int?[] { 0, 1, 2 }))
            return false;
        string[] roster = intents.Select(intent => intent.Model!).ToArray();
        if (!NativeWeakSlimeFormationCatalog.Tickets.Any(ticket => ticket.Roster.SequenceEqual(roster, StringComparer.Ordinal)))
            return false;
        reason = "typed_opening_entry_assets_required";
        if (events.Length < 3 || events[1].Payload is not PublicCombatFact { FactKind: PublicCombatFactKind.Started }
            || events[2].Payload is not PublicCombatFact { FactKind: PublicCombatFactKind.EntryAssets, Assets: { } assets }
            || events.Take(turn).Count(entry => entry.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.EntryAssets }) != 1)
            return false;
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", assets.Hp, assets.MaxHp, assets.Gold,
            assets.Deck, assets.Relics, assets.Potions.ToArray(), assets.MaxEnergy, assets.PotionSlots,
            assets.OrbSlots, assets.CardRemovalsUsed);
        condition = new(opening.CombatOwnerOrdinal, PublicJson.Serialize(entry), roster); reason = null; return true;
    }

    internal NativeWeakSlimeFormationPlan CreateProposal(LabelSlimesWeakFormationContext context, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(nextWord);
        if (context.Run is not { CurrentActIndex: 0, Act: Overgrowth } run || run.Rng.UsesSemanticKeys
            || run.Players.Count != 1 || run.Players[0].Character is not Silent
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || !ReferenceEquals(context.FactoryRng, context.Rng) || context.Rng.Counter != 0
            || context.Encounter is not { Slots.Count: 0 }
            || !ReferenceEquals(context.Encounter, run.Act.MonsterEncounterCandidates.Single(
                encounter => encounter.IdEntry == "SLIMES_WEAK")))
            throw new InvalidOperationException("Native weak-slime formation differs from its unslotted factory certificate");
        return NativeWeakSlimeFormationPlan.Create(Roster, nextWord);
    }
}

internal sealed record NativeWeakSlimeFormationTicket(IReadOnlyList<int> Choices, IReadOnlyList<string> Roster);

/// <summary>
/// Exhaust all four native paths [NextItem(2), NextItem(1), NextItem(2)]. Every
/// physical output slot is retained, including repeated model types if present;
/// tickets are never deduplicated by roster. No empirical seed search is used.
/// </summary>
internal static class NativeWeakSlimeFormationCatalog
{
    private static readonly Lazy<IReadOnlyList<NativeWeakSlimeFormationTicket>> Catalog = new(Build);
    internal static IReadOnlyList<NativeWeakSlimeFormationTicket> Tickets => Catalog.Value;
    private static IReadOnlyList<NativeWeakSlimeFormationTicket> Build()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = new Overgrowth().MonsterEncounterCandidates.Single(item => item.IdEntry == "SLIMES_WEAK");
        var tickets = new List<NativeWeakSlimeFormationTicket>();
        using var disabled = LabelSlimesWeakScope.Enter(_ => null);
        for (int first = 0; first < 2; first++)
        for (int medium = 0; medium < 2; medium++)
        {
            int[] choices = [first, 0, medium];
            ulong[] words = [ConditionalShuffleProposal.Factor(2, first).BucketStart << 11, 0,
                ConditionalShuffleProposal.Factor(2, medium).BucketStart << 11];
            int cursor = 0;
            using var scope = LabelRandomScope.Enter(_ => cursor < words.Length ? words[cursor++]
                : throw new InvalidOperationException("Native weak-slime factory consumed an unreviewed draw"));
            var rng = new Rng(0, "nosl-weak-slime-catalog-only");
            var roster = encounter.CreateMonsters(rng);
            if (cursor != 3 || rng.Counter != 3 || roster.Count != 3 || roster.Any(item => item.SlotName is not null))
                throw new InvalidOperationException("Native weak-slime factory draw count or slot order changed");
            tickets.Add(new(Array.AsReadOnly(choices), Array.AsReadOnly(roster.Select(item => item.Monster.GetType().Name).ToArray())));
        }
        return tickets.AsReadOnly();
    }
}

/// <summary>
/// Uniform complete raw-word preimages for a public ordered roster. Each native
/// ticket has mass 1/4. Every word retains all low bits; the bound-one call has its
/// whole 64-bit support. Thus p/q = compatibleTickets/4 is fixed by the root.
/// </summary>
internal sealed class NativeWeakSlimeFormationPlan
{
    internal IReadOnlyList<ulong> RawWords { get; }
    internal IReadOnlyList<ConditionalShuffleFactor> Factors { get; }
    internal NativeWeakSlimeFormationTicket Ticket { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope => NativeToProposalRatio;
    private NativeWeakSlimeFormationPlan(ulong[] words, ConditionalShuffleFactor[] factors,
        NativeWeakSlimeFormationTicket ticket, ShuffleRational ratio)
    { RawWords = Array.AsReadOnly(words); Factors = Array.AsReadOnly(factors); Ticket = ticket; NativeToProposalRatio = ratio; }

    internal static ShuffleRational Mass(IReadOnlyList<string> roster) => new(
        NativeWeakSlimeFormationCatalog.Tickets.Count(ticket => ticket.Roster.SequenceEqual(roster, StringComparer.Ordinal)), 4);

    internal static NativeWeakSlimeFormationPlan Create(IReadOnlyList<string> roster, Func<ulong> nextWord, int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(roster); ArgumentNullException.ThrowIfNull(nextWord);
        if (precisionBits is < 1 or > 53) throw new ArgumentOutOfRangeException(nameof(precisionBits));
        var compatible = NativeWeakSlimeFormationCatalog.Tickets.Where(ticket => ticket.Roster.SequenceEqual(roster, StringComparer.Ordinal)).ToArray();
        if (compatible.Length == 0) throw new ArgumentException("Public weak-slime roster has no native factory ticket", nameof(roster));
        var selected = compatible[(int)ConditionalShuffleProposal.UniformBelow((ulong)compatible.Length, nextWord)];
        int[] bounds = [2, 1, 2];
        var words = new ulong[3]; var factors = new ConditionalShuffleFactor[3];
        int lowBits = 64 - precisionBits; ulong lowMask = (1UL << lowBits) - 1;
        for (int i = 0; i < words.Length; i++)
        {
            var factor = factors[i] = ConditionalShuffleProposal.Factor(bounds[i], selected.Choices[i], precisionBits);
            words[i] = ((factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord)) << lowBits)
                | (nextWord() & lowMask);
        }
        return new(words, factors, selected, new(compatible.Length, 4));
    }
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }
}
