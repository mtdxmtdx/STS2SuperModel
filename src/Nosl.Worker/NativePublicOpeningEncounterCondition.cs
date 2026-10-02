using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// The first observed native combat, certified as normal slot zero by its typed
/// run/map/owner history. Missing certificates disable this proposal only. No
/// source encounter, seed, private inventory, or later combat roster is an input.
/// </summary>
internal sealed class NativePublicOpeningEncounterCondition
{
    internal Type TargetActType { get; }
    internal string TargetEncounterId { get; }
    internal int TargetEncounterIndex { get; }
    internal long CombatOwnerOrdinal { get; }
    internal ShuffleRational Envelope => new(1, 4);

    private NativePublicOpeningEncounterCondition(NativeOpeningEncounterEntry entry, long owner)
    {
        TargetActType = entry.ActType; TargetEncounterId = entry.EncounterId;
        TargetEncounterIndex = entry.Index; CombatOwnerOrdinal = owner;
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicOpeningEncounterCondition? condition, out string? reason)
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

    private static bool TryCreate(PublicRunEvidence? evidence,
        out NativePublicOpeningEncounterCondition? condition, out string? reason)
    {
        condition = null; reason = "observed_fresh_silent_run_required";
        if (evidence is null || evidence.Events.FirstOrDefault()?.Payload is not PublicRunStarted
            { Character: "Silent", Ascension: 10 }) return false;
        var events = evidence.Events;
        var combat = events.FirstOrDefault(entry => entry.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Combat });
        reason = "first_normal_combat_owner_required";
        if (combat?.Payload is not PublicOwnerStarted { ActIndex: 0, Floor: 2,
            CompleteFromOwnerStart: true, ParentOwnerOrdinal: null }) return false;
        long owner = combat.OwnerOrdinal!.Value;
        var ownerEvents = events.Where(entry => entry.OwnerOrdinal == owner).ToArray();
        int turn = Array.FindIndex(ownerEvents, entry => entry.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.PlayerTurnStarted, Turn: 1 });
        reason = "observed_initial_roster_required";
        if (turn < 0 || ownerEvents.Take(turn).Count(entry => entry.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.Started }) != 1) return false;
        var intents = ownerEvents.Skip(turn + 1).TakeWhile(entry => entry.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.IntentPublished }).ToArray();
        if (intents.Length == 0 || intents.Select(entry => ((PublicCombatFact)entry.Payload).TargetSlot)
            .Distinct().Count() != intents.Length) return false;
        long through = intents[^1].EventOrdinal;
        // Later gaps cannot erase an already observed opening. Earlier gaps could
        // hide a normal pull or an original monster, so they cannot certify it.
        if (events.TakeWhile(entry => entry.EventOrdinal <= through).Any(entry => entry.Payload is PublicEvidenceGap))
        { reason = "complete_public_opening_prefix_required"; return false; }
        var before = events.TakeWhile(entry => entry.EventOrdinal < combat.EventOrdinal).ToArray();
        var maps = before.Where(entry => entry.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Map }).ToArray();
        reason = "first_monster_map_move_required";
        if (maps is not [var mapOwner] || mapOwner.Payload is not PublicOwnerStarted
            { ActIndex: 0, Floor: 1, ParentOwnerOrdinal: null, CompleteFromOwnerStart: true }) return false;
        var mapEvents = before.Where(entry => entry.OwnerOrdinal == mapOwner.OwnerOrdinal).ToArray();
        if (mapEvents is not [_, var observed, var chosen, var ended]
            || observed.Payload is not PublicMapObserved map || map.Current is not { Row: 0 }
            || chosen.Payload is not PublicMapChosen choice || choice.OfferEventOrdinal != observed.EventOrdinal
            || choice.Coordinate.Row != 1
            || ended.Payload is not PublicOwnerEnded { Outcome: PublicEvidenceOwnerOutcome.Completed }
            || !map.Nodes.Any(node => node.Coordinate == map.Current && node.NodeType == PublicMapNodeType.Ancient)
            || !map.Nodes.Any(node => node.Coordinate == choice.Coordinate && node.NodeType == PublicMapNodeType.Monster))
            return false;
        // StandardActMap pins row one as unmodifiable Monster. RunDriver visits
        // Neow, then this observed move. RoomFactory's Monster branch pulls the
        // next stored normal encounter. Forced event fights have a parent owner;
        // any earlier combat would instead be the first owner selected above.
        // Weak batches do not change monster types or summon another weak family
        // before turn one. Startup damage/removal cannot turn any other reviewed
        // weak batch into a complete roster belonging to this one.
        string[] roster = intents.Select(entry => ((PublicCombatFact)entry.Payload).Model!).ToArray();
        var matches = NativeOpeningEncounterCatalog.Entries.Where(entry => entry.Rosters.Any(
            candidate => candidate.SequenceEqual(roster, StringComparer.Ordinal))).ToArray();
        reason = "initial_roster_has_no_unique_native_weak_origin";
        if (matches is not [var target]) return false;
        condition = new(target, owner); reason = null; return true;
    }

    internal NativeOpeningEncounterPlan CreateProposal(LabelNormalEncounterContext context, Func<ulong> nextWord)
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
            throw new InvalidOperationException("Opening encounter escaped fresh native room generation");
        NativeFirstEncounterCondition.ValidatePool(act);
        if (act.GetType() != TargetActType)
            throw new NativePublicConstraintMismatchException("Native act differs from the observed opening roster");
        return NativeOpeningEncounterPlan.Create(TargetEncounterIndex, nextWord);
    }
}

/// <summary>
/// A single complete raw-word preimage of native GrabBag's first four-entry draw.
/// Its NextDouble/strict cumulative comparisons equal NextInt's floor buckets
/// here. All subsequent bag, formation, startup and combat randomness stays native.
/// </summary>
internal sealed class NativeOpeningEncounterPlan
{
    internal IReadOnlyList<ulong> RawWords { get; }
    internal int FirstEncounterIndex { get; }
    internal ShuffleRational NativeToProposalRatio { get; }
    internal ShuffleRational Envelope => NativeToProposalRatio;
    private NativeOpeningEncounterPlan(ulong word, int index, ShuffleRational ratio)
    { RawWords = Array.AsReadOnly(new[] { word }); FirstEncounterIndex = index; NativeToProposalRatio = ratio; }

    internal static NativeOpeningEncounterPlan Create(int targetIndex, Func<ulong> nextWord, int precisionBits = 53)
    {
        ArgumentNullException.ThrowIfNull(nextWord);
        if (precisionBits is < 2 or > 53) throw new ArgumentOutOfRangeException(nameof(precisionBits));
        var factor = ConditionalShuffleProposal.Factor(4, targetIndex, precisionBits);
        int lowBits = 64 - precisionBits;
        ulong word = ((factor.BucketStart + ConditionalShuffleProposal.UniformBelow(factor.BucketSize, nextWord)) << lowBits)
            | (nextWord() & ((1UL << lowBits) - 1));
        return new(word, targetIndex, new(factor.BucketSize, 1UL << precisionBits));
    }
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }
}

/// <summary>One owned replay; the tape's ForcePrefixWords retains full-state and alias guards.</summary>
internal sealed class NativePublicOpeningEncounterProposal(NativePublicOpeningEncounterCondition condition,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private NativeOpeningEncounterPlan? _plan;
    private bool _complete;
    internal bool Applied => _plan is not null;
    internal int? FirstEncounterIndex => _plan?.FirstEncounterIndex;
    internal ShuffleRational Envelope => condition.Envelope;
    internal void AttachHypotheticalRun(RunState run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_run is not null) throw new InvalidOperationException("Opening encounter already owns a native run");
        _run = run;
    }
    internal IDisposable BeginGeneration(LabelNormalEncounterContext context)
    {
        if (_run is null || !ReferenceEquals(context.Run, _run) || _plan is not null)
            throw new InvalidOperationException("Opening encounter boundary is unowned or repeated");
        _plan = condition.CreateProposal(context, nextWord);
        var inner = forcePrefixWords(_plan.RawWords, context.Rng, "public opening encounter");
        return new Completion(() => { inner.Dispose(); _complete = true; });
    }
    internal void ValidateCompletion()
    {
        if (_plan is null || !_complete) throw new InvalidOperationException("Opening encounter proposal did not complete");
    }
    internal bool AcceptCorrection(Func<ulong> correctionWord)
    { ValidateCompletion(); return _plan!.AcceptCorrection(correctionWord); }
    private sealed class Completion(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}

internal sealed record NativeOpeningEncounterEntry(Type ActType, int Index, string EncounterId,
    IReadOnlyList<IReadOnlyList<string>> Rosters);

/// <summary>
/// Exhaustive public roster support produced by native factories, not a guessed
/// roster table or empirical seed sample. Source-pinned finite factory kernels:
/// Overgrowth.CreateSlimesWeak uses NextItem(2), NextItem(1), NextItem(2);
/// CorpseSlug.EnsureCorpseSlugsStartWithDifferentMoves uses NextInt(3), affecting
/// initial moves only. The six other weak factories consume no random words.
/// </summary>
internal static class NativeOpeningEncounterCatalog
{
    private static readonly Lazy<IReadOnlyList<NativeOpeningEncounterEntry>> Catalog = new(Build);
    internal static IReadOnlyList<NativeOpeningEncounterEntry> Entries => Catalog.Value;
    private static IReadOnlyList<NativeOpeningEncounterEntry> Build()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var entries = new List<NativeOpeningEncounterEntry>();
        foreach (ActDefinition act in new ActDefinition[] { new Overgrowth(), new Underdocks() })
        {
            NativeFirstEncounterCondition.ValidatePool(act);
            var pool = act.MonsterEncounterCandidates.Where(encounter => encounter.IsWeak).ToArray();
            for (int index = 0; index < pool.Length; index++)
            {
                int[] bounds = pool[index].IdEntry switch
                { "SLIMES_WEAK" => [2, 1, 2], "CORPSE_SLUGS_WEAK" => [3], _ => [] };
                List<ulong[]> paths = [[]];
                foreach (int bound in bounds)
                    paths = paths.SelectMany(path => Enumerable.Range(0, bound).Select(choice => path.Append(
                        ConditionalShuffleProposal.Factor(bound, choice).BucketStart << 11).ToArray())).ToList();
                var rosters = new List<IReadOnlyList<string>>();
                foreach (var path in paths)
                {
                    int cursor = 0;
                    using var scope = LabelRandomScope.Enter(_ => cursor < path.Length ? path[cursor++]
                        : throw new InvalidOperationException("Native opening factory consumed an unreviewed draw"));
                    var batch = pool[index].CreateMonsters(new Rng(0, "nosl-opening-catalog-only"));
                    if (cursor != path.Length || batch.Any(item => item.SlotName is not null))
                        throw new InvalidOperationException("Native opening factory draw count or slot order changed");
                    string[] roster = batch.Select(item => item.Monster.GetType().Name).ToArray();
                    if (!rosters.Any(existing => existing.SequenceEqual(roster))) rosters.Add(Array.AsReadOnly(roster));
                }
                entries.Add(new(act.GetType(), index, pool[index].IdEntry, rosters.AsReadOnly()));
            }
        }
        return entries.AsReadOnly();
    }
}
