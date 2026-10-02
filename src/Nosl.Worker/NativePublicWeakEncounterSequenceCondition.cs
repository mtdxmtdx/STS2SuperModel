using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

internal sealed record NativeWeakEncounterTarget(int NormalSlot, long CombatOwnerOrdinal,
    IReadOnlyList<int> EncounterIndices);

/// <summary>
/// Optional detached public certificate for the first three normal weak pulls.
/// Owner ancestry distinguishes event fights from normal pulls; complete observed
/// map moves establish the normal-slot count. An unproved suffix is left native.
/// Source recipes, private encounter order and source runtime objects are absent.
/// </summary>
internal sealed class NativePublicWeakEncounterSequenceCondition
{
    internal Type TargetActType { get; }
    internal IReadOnlyList<NativeWeakEncounterTarget> Targets { get; }
    internal int PrefixLength { get; }
    internal ShuffleRational Envelope { get; }
    private readonly IReadOnlyList<IReadOnlyList<int>> _allowed;

    private NativePublicWeakEncounterSequenceCondition(Type actType, NativeWeakEncounterTarget[] targets)
    {
        TargetActType = actType; Targets = Array.AsReadOnly(targets);
        PrefixLength = targets.Max(target => target.NormalSlot) + 1;
        _allowed = Array.AsReadOnly(Enumerable.Range(0, PrefixLength).Select(slot =>
            targets.SingleOrDefault(target => target.NormalSlot == slot)?.EncounterIndices
            ?? Array.AsReadOnly(new[] { 0, 1, 2, 3 })).ToArray());
        Envelope = NativeWeakEncounterSequencePlan.Probability(_allowed);
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicWeakEncounterSequenceCondition? condition, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(prior);
        condition = null; reason = "fresh_native_public_evidence_prior_required";
        if (prior.SchemaVersion is not (NativeTapePrior.Version or NativeTapePrior.RewardsVersion)
            || prior.Execution is not { SourcePolicyId: PublicContinuationPolicies.ReviewedId }
            || !prior.Execution.EmitsPublicEvidence
            || PublicRunContext.IsHistoryUnavailable(prior.Execution.PublicCombatHistoryMode)
            || prior.Execution.ResolvedOutsideCombatScript is not (NaturalSourceCollector.ScriptVersion
                or NaturalSourceCollector.BoundedEventScriptVersion)) return false;
        return TryCreate(root.PublicEvidence, out condition, out reason);
    }

    internal static bool TryCreate(PublicRunEvidence? evidence,
        out NativePublicWeakEncounterSequenceCondition? condition, out string? reason)
    {
        condition = null; reason = "observed_fresh_silent_run_required";
        if (evidence?.Events.FirstOrDefault()?.Payload is not PublicRunStarted { Character: "Silent", Ascension: 10 })
            return false;
        var events = evidence.Events;
        var starts = events.Where(entry => entry.Payload is PublicOwnerStarted)
            .ToDictionary(entry => entry.OwnerOrdinal!.Value);
        var candidates = new List<(int Slot, long Owner, NativeOpeningEncounterEntry[] Matches)>();
        int normalSlot = 0, expectedMapFloor = 1;
        PublicMapCoordinate? previousChoice = null;
        PublicMapNodeType? pendingType = null;
        long pendingMapEnd = -1;
        int pendingFloor = -1;
        bool directCombatSeen = false;
        reason = "no_certified_normal_weak_roster";
        foreach (var entry in events)
        {
            // A later gap cannot undo an earlier certified target. No target
            // after any missing run/owner history is eligible for this proposal.
            if (entry.Payload is PublicEvidenceGap) break;
            if (entry.Payload is not PublicOwnerStarted owner) continue;
            if (!owner.CompleteFromOwnerStart || owner.ActIndex != 0) break;
            if (owner.OwnerKind == PublicEvidenceOwnerKind.Map)
            {
                if (pendingType == PublicMapNodeType.Monster && !directCombatSeen) break;
                var mapEvents = events.Where(item => item.OwnerOrdinal == entry.OwnerOrdinal).ToArray();
                if (owner.ParentOwnerOrdinal is not null || owner.Floor != expectedMapFloor
                    || mapEvents is not [_, var observed, var chosen, var ended]
                    || observed.EventOrdinal != entry.EventOrdinal + 1 || chosen.EventOrdinal != entry.EventOrdinal + 2
                    || ended.EventOrdinal != entry.EventOrdinal + 3
                    || observed.Payload is not PublicMapObserved map || map.Current is null
                    || chosen.Payload is not PublicMapChosen choice || choice.OfferEventOrdinal != observed.EventOrdinal
                    || ended.Payload is not PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Completed }
                    || choice.Coordinate.Row != map.Current.Row + 1
                    || previousChoice is not null && map.Current != previousChoice
                    || previousChoice is null && (map.Current.Row != 0 || !map.Nodes.Any(node =>
                        node.Coordinate == map.Current && node.NodeType == PublicMapNodeType.Ancient))) break;
                pendingType = map.Nodes.Single(node => node.Coordinate == choice.Coordinate).NodeType;
                pendingMapEnd = ended.EventOrdinal; pendingFloor = owner.Floor + 1;
                previousChoice = choice.Coordinate; expectedMapFloor++; directCombatSeen = false;
                continue;
            }
            if (owner.OwnerKind != PublicEvidenceOwnerKind.Combat) continue;
            if (owner.ParentOwnerOrdinal is long parent)
            {
                // Native EventRoom creates its own explicit encounter batches;
                // it never pulls RunState's stored normal encounter queue.
                if (starts[parent].Payload is not PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.Event } eventOwner || eventOwner.Floor != owner.Floor)
                    break;
                continue;
            }
            if (pendingMapEnd != entry.EventOrdinal - 1 || owner.Floor != pendingFloor || directCombatSeen) break;
            directCombatSeen = true;
            if (pendingType is PublicMapNodeType.Elite or PublicMapNodeType.Boss) continue;
            // Fresh native Unknown rolls can resolve Monster/Treasure/Shop/Event.
            // UnknownMapPointOdds starts Elite at -1 and every current odds-increase
            // hook preserves that negative increment. The only room-type overrides
            // remove Monster (JuzuBracelet) or restrict to Event (GoldenCompass,
            // act-three LanternKey); none enable Elite. Thus direct parentless
            // combat immediately after this map move proves a normal Monster pull.
            if (pendingType is not (PublicMapNodeType.Monster or PublicMapNodeType.Unknown)) break;
            if (normalSlot >= 3) break;
            var ownerEvents = events.Where(item => item.OwnerOrdinal == entry.OwnerOrdinal).ToArray();
            int turn = Array.FindIndex(ownerEvents, item => item.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 });
            if (turn >= 0 && ownerEvents.Take(turn).Count(item => item.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.Started }) == 1)
            {
                var intents = ownerEvents.Skip(turn + 1).TakeWhile(item => item.Payload is PublicCombatFact
                    { FactKind: PublicCombatFactKind.IntentPublished }).ToArray();
                if (intents.Length > 0 && intents.Select(item => ((PublicCombatFact)item.Payload).TargetSlot)
                    .SequenceEqual(Enumerable.Range(0, intents.Length).Select(slot => (int?)slot))
                    && !events.Take(checked((int)intents[^1].EventOrdinal + 1)).Any(item => item.Payload is PublicEvidenceGap
                        or PublicOwnerStarted { CompleteFromOwnerStart: false }))
                {
                    string[] roster = intents.Select(item => ((PublicCombatFact)item.Payload).Model!).ToArray();
                    var matches = NativeOpeningEncounterCatalog.Entries.Where(candidate => candidate.Rosters.Any(
                        signature => signature.SequenceEqual(roster, StringComparer.Ordinal))).ToArray();
                    if (matches.Length > 0) candidates.Add((normalSlot, entry.OwnerOrdinal!.Value, matches));
                }
            }
            normalSlot++;
            if (normalSlot == 3) break;
        }
        if (candidates.Count == 0) return false;
        Type[] acts = candidates.Select(candidate => candidate.Matches.Select(match => match.ActType).Distinct())
            .Aggregate((left, right) => left.Intersect(right)).ToArray();
        // Cross-act ambiguous evidence is conservatively left to full replay.
        // Within the certified act every matching identity remains supported.
        reason = "public_weak_rosters_have_no_single_certified_act";
        if (acts is not [var actType]) return false;
        var targets = candidates.Select(candidate => new NativeWeakEncounterTarget(candidate.Slot, candidate.Owner,
            Array.AsReadOnly(candidate.Matches.Where(match => match.ActType == actType)
                .Select(match => match.Index).Distinct().Order().ToArray()))).ToArray();
        var proposed = new NativePublicWeakEncounterSequenceCondition(actType, targets);
        reason = "public_weak_sequence_has_no_native_support";
        if (proposed.Envelope.Numerator.IsZero) return false;
        condition = proposed; reason = null; return true;
    }

    internal NativeWeakEncounterSequencePlan CreateProposal(LabelNormalEncounterContext context, Func<ulong> nextWord)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(nextWord);
        var run = context.Run; var act = context.Act;
        if (run.Rng.UsesSemanticKeys || !ReferenceEquals(context.Rng, run.Rng.UpFront)
            || run.CurrentActIndex != 0 || run.TotalFloor != 1 || run.CurrentRoomCount != 0
            || run.VisitedMapCoords.Count != 1 || run.CurrentMapCoord != run.Map.StartingMapPoint.coord
            || run.Players.Count != 1 || run.Players[0].Character is not Silent
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || run.Acts.Count != 3 || !ReferenceEquals(act, run.Acts[0])
            || run.Acts[1] is not Hive || run.Acts[2] is not Glory
            || act.GetType() != typeof(Overgrowth) && act.GetType() != typeof(Underdocks))
            throw new InvalidOperationException("Weak encounter sequence escaped fresh native room generation");
        NativeFirstEncounterCondition.ValidatePool(act);
        if (act.GetType() != TargetActType)
            throw new NativePublicConstraintMismatchException("Native act differs from the certified weak encounter sequence");
        return NativeWeakEncounterSequencePlan.Create(_allowed, nextWord);
    }
}

internal sealed record NativeWeakEncounterSequenceBranch(IReadOnlyList<int> EncounterIndices,
    IReadOnlyList<ConditionalShuffleFactor> Factors, BigInteger BucketProduct);

/// <summary>
/// Enumerate at most 4*3*2 native ordered outcomes. Weight each by its exact
/// high-bit bucket product, then draw every complete raw-word preimage uniformly.
/// Thus q(word prefix)=1/sum(products), p/q=sum(products)/Q^length. The fixed
/// public-root mass is also the envelope: no post-replay correction draw is needed.
/// This is exact for the declared independent primitive tape, not a seed prior.
/// </summary>
internal sealed class NativeWeakEncounterSequencePlan
{
    internal IReadOnlyList<ulong> RawWords { get; }
    internal IReadOnlyList<int> EncounterIndices { get; }
    internal int FirstEncounterIndex => EncounterIndices[0];
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope => NativeToProposalRatio;
    private NativeWeakEncounterSequencePlan(ulong[] words, IReadOnlyList<int> indices, ShuffleRational ratio)
    { RawWords = Array.AsReadOnly(words); EncounterIndices = indices; NativeToProposalRatio = ratio; }

    internal static NativeWeakEncounterSequenceBranch[] Branches(IReadOnlyList<IReadOnlyList<int>> allowed,
        int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (precisionBits is < 2 or > 53) throw new ArgumentOutOfRangeException(nameof(precisionBits));
        if (allowed.Count is < 1 or > 3 || allowed.Any(slot => slot is null || slot.Any(index => index is < 0 or > 3)))
            throw new ArgumentException("A weak sequence requires one to three valid identity sets", nameof(allowed));
        // Freeze inputs before enumeration; duplicate public signatures must not
        // multiply the mass of any physical ordered encounter outcome.
        int[][] choices = allowed.Select(slot => slot.Distinct().ToArray()).ToArray();
        var branches = new List<NativeWeakEncounterSequenceBranch>();
        void Visit(int[] remaining, int[] selected, ConditionalShuffleFactor[] factors, BigInteger product)
        {
            int slot = selected.Length;
            if (slot == choices.Length)
            { branches.Add(new(Array.AsReadOnly(selected), Array.AsReadOnly(factors), product)); return; }
            for (int index = 0; index < remaining.Length; index++)
            {
                int identity = remaining[index];
                if (!choices[slot].Contains(identity)) continue;
                var factor = ConditionalShuffleProposal.Factor(remaining.Length, index, precisionBits);
                Visit(remaining.Where((_, i) => i != index).ToArray(), selected.Append(identity).ToArray(),
                    factors.Append(factor).ToArray(), product * factor.BucketSize);
            }
        }
        Visit([0, 1, 2, 3], [], [], BigInteger.One);
        return branches.ToArray();
    }

    internal static ShuffleRational Probability(IReadOnlyList<IReadOnlyList<int>> allowed, int precisionBits = 53)
        => new(Branches(allowed, precisionBits).Aggregate(BigInteger.Zero, (sum, branch) => sum + branch.BucketProduct),
            BigInteger.One << (precisionBits * allowed.Count));

    internal static NativeWeakEncounterSequencePlan Create(IReadOnlyList<IReadOnlyList<int>> allowed,
        Func<ulong> nextWord, int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        var branches = Branches(allowed, precisionBits);
        var total = branches.Aggregate(BigInteger.Zero, (sum, branch) => sum + branch.BucketProduct);
        if (total.IsZero) throw new ArgumentException("Weak encounter constraints have no native support", nameof(allowed));
        BigInteger ticket = NativeRewardIdentityMath.UniformBelow(total, nextWord);
        var selected = branches[^1];
        foreach (var branch in branches)
        {
            if (ticket < branch.BucketProduct) { selected = branch; break; }
            ticket -= branch.BucketProduct;
        }
        int lowBits = 64 - precisionBits; ulong lowMask = (1UL << lowBits) - 1;
        var words = selected.Factors.Select(factor =>
            ((factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord)) << lowBits)
            | (nextWord() & lowMask)).ToArray();
        return new(words, selected.EncounterIndices, new(total, BigInteger.One << (precisionBits * words.Length)));
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }
}

/// <summary>One owned generation boundary; ForcePrefixWords enforces primitive state and alias guards.</summary>
internal sealed class NativePublicWeakEncounterSequenceProposal(NativePublicWeakEncounterSequenceCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private NativeWeakEncounterSequencePlan? _plan;
    private bool _complete;
    internal bool Applied => _plan is not null;
    internal int? FirstEncounterIndex => _plan?.FirstEncounterIndex;
    internal int ConditionedEncounterCount => _plan is null ? 0 : condition.Targets.Count;
    internal ShuffleRational Envelope => condition.Envelope;
    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Weak encounter sequence already owns a native run");
        _run = run;
    }
    internal IDisposable BeginGeneration(LabelNormalEncounterContext context)
    {
        if (_run is null || !ReferenceEquals(context.Run, _run) || _plan is not null)
            throw new InvalidOperationException("Weak encounter sequence boundary is unowned or repeated");
        _plan = condition.CreateProposal(context, nextWord);
        var inner = forcePrefixWords(_plan.RawWords, context.Rng, "public weak encounter sequence");
        return new Completion(() => { inner.Dispose(); _complete = true; });
    }
    internal void ValidateCompletion()
    {
        if (_plan is null || !_complete) throw new InvalidOperationException("Weak encounter sequence proposal did not complete");
    }
    internal bool AcceptCorrection(Func<ulong> correctionWord)
    { ValidateCompletion(); return _plan!.AcceptCorrection(correctionWord); }
    private sealed class Completion(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}
