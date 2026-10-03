using Nosl.Contracts;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// A root-fixed necessary event on one WHOLE native event permutation. Unknown
/// eligibility is existentially overapproximated, never read from a hypothetical
/// hidden bag. Final full-public-history equality is mandatory after this proposal.
/// </summary>
internal sealed class NativePublicEventPermutationCondition
{
    private readonly Type[] _pool;
    private readonly NativeEventScanObservation[] _observations;
    internal Type TargetActType { get; }
    internal int TargetCount => _observations.Length;
    internal IReadOnlyList<long> OwnerOrdinals { get; }
    internal IReadOnlyList<string> TargetEventIds { get; }
    internal IReadOnlyList<Type> Pool => Array.AsReadOnly(_pool);
    internal ulong NaturalMask { get; }

    private NativePublicEventPermutationCondition(Type actType, Type[] pool,
        NativeEventScanObservation[] observations, long[] owners, string[] ids)
    {
        TargetActType = actType; _pool = pool; _observations = observations;
        OwnerOrdinals = Array.AsReadOnly(owners); TargetEventIds = Array.AsReadOnly(ids);
        NaturalMask = NativeEventSelectionCertificate.NaturalMask(pool);
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativePublicEventPermutationCondition? condition, out string? reason)
    {
        condition = null;
        // These certificates pin native Silent/A10 setup, exactly one initial
        // Neow advancement, and one public act identity. A held latent act would
        // otherwise give different rejection normalizers for its different pool.
        if (!NativePublicInitialMapCondition.TryCreate(root.PublicEvidence, prior, out _, out reason)
            || !NativePublicOpeningEncounterCondition.TryCreate(root, prior, out var opening, out reason)) return false;
        var evidence = root.PublicEvidence!;
        ActDefinition act = opening!.TargetActType == typeof(Overgrowth) ? new Overgrowth() : new Underdocks();
        Type[] pool = NativeEventSelectionCertificate.Pool(act);
        var observations = new List<NativeEventScanObservation>();
        var owners = new List<long>(); var ids = new List<string>();
        var events = evidence.Events;
        foreach (var entry in events.Where(e => e.Payload is PublicOwnerStarted
                     { OwnerKind: PublicEvidenceOwnerKind.Event } && e.OwnerOrdinal != 0))
        {
            // Stop at a missing certificate, retaining only the already certified
            // prefix. No unknown earlier event pull can be silently omitted.
            int index = checked((int)entry.EventOrdinal);
            if (entry.Payload is not PublicOwnerStarted { ActIndex: 0, ParentOwnerOrdinal: null,
                    CompleteFromOwnerStart: true } start || start.Floor < 2 || index < 4
                || events.Take(index + 1).Any(e => e.Payload is PublicEvidenceGap)
                || events[index - 4] is not { OwnerOrdinal: { } mapOwner, Payload: PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.Map, ActIndex: 0, ParentOwnerOrdinal: null,
                        CompleteFromOwnerStart: true } mapStart }
                || mapStart.Floor != start.Floor - 1
                || events[index - 3] is not { Payload: PublicMapObserved map } mapEntry
                || mapEntry.OwnerOrdinal != mapOwner
                || events[index - 2] is not { Payload: PublicMapChosen chosen } choiceEntry
                || choiceEntry.OwnerOrdinal != mapOwner || chosen.OfferEventOrdinal != mapEntry.EventOrdinal
                || map.Nodes.Single(n => n.Coordinate == chosen.Coordinate).NodeType != PublicMapNodeType.Unknown
                || events[index - 1] is not { Payload: PublicOwnerEnded
                    { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: { } assets } } end
                || end.OwnerOrdinal != mapOwner
                || !NativeEventSelectionCertificate.UnmodifiedSelection(assets)) break;
            var first = events.Skip(index + 1).FirstOrDefault(e => e.OwnerOrdinal == entry.OwnerOrdinal);
            if (first?.Payload is not PublicOptionsObserved options
                || NativeEventSelectionCertificate.Identify(options) is not { } id) break;
            int target = Array.FindIndex(pool, type => type.Name == id);
            if (target < 0) break;
            var eligibility = NativeEventSelectionCertificate.Eligibility(pool, assets, start.Floor - 1);
            observations.Add(new(1UL << target, eligibility.Must, eligibility.May));
            owners.Add(entry.OwnerOrdinal!.Value); ids.Add(id);
        }
        if (observations.Count == 0) { reason = "certified_public_event_prefix_required"; return false; }
        condition = new(act.GetType(), pool, observations.ToArray(), owners.ToArray(), ids.ToArray());
        reason = null; return true;
    }

    internal bool Matches(IReadOnlyList<Type> permutation)
    {
        if (permutation.Count != _pool.Length || permutation.Distinct().Count() != _pool.Length)
            throw new InvalidOperationException("Event permutation changed its complete native pool");
        int[] indices = permutation.Select(type => Array.IndexOf(_pool, type)).ToArray();
        if (indices.Any(index => index < 0)) throw new InvalidOperationException("Event permutation contains an unreviewed type");
        return NativeEventScanSuperset.Matches(indices, NaturalMask, _observations, initialCursor: 1);
    }

    /// <summary>Prepare before entering any label callback; only independent scratch RNGs are used.</summary>
    internal NativeEventPermutationPlan Prepare(NativeTapeRecipe recipe, int maxTrials,
        CancellationToken cancellationToken = default)
    {
        var auxiliary = new Rng(recipe.ProposalSeed, "nosl-public-event-permutation-v1");
        return Prepare(auxiliary.NextUnsignedLong, maxTrials, cancellationToken);
    }

    internal NativeEventPermutationPlan Prepare(Func<ulong> nextWord, int maxTrials,
        CancellationToken cancellationToken = default)
    {
        var result = NativeComponentRejection.Sample("public_event_permutation", maxTrials, () =>
            NativeComponentRejection.Evaluate(() =>
            {
                var permutation = _pool.ToList();
                permutation.UnstableShuffle(new Rng(0, "nosl-event-permutation-scratch-v1"));
                return permutation;
            }, nextWord, cancellationToken), Matches, cancellationToken);
        if (result.Trace.Count != _pool.Length - 1 || result.Trace.Select(w => w.State).Distinct().Count() != result.Trace.Count)
            throw new InvalidOperationException("Native event shuffle draw count or distinct-cell certificate changed");
        return new(result.Trace.Select(w => w.Word).ToArray(), result.Stats, maxTrials);
    }

    internal void ValidateBoundary(LabelEventGenerationContext context)
    {
        var run = context.Run;
        if (context.ActIndex != 0 || run.Rng.UsesSemanticKeys || !ReferenceEquals(context.Rng, run.Rng.UpFront)
            || run.CurrentActIndex != 0 || run.TotalFloor != 1 || run.CurrentRoomCount != 0
            || run.VisitedMapCoords.Count != 1 || run.CurrentMapCoord != run.Map.StartingMapPoint.coord
            || run.Players.Count != 1 || run.Players[0].Character is not Silent
            || !run.Ascension.HasLevel(AscensionLevel.DoubleBoss)
            || run.Acts.Count != 3 || !ReferenceEquals(context.Act, run.Acts[0])
            || run.Acts[1] is not Hive || run.Acts[2] is not Glory)
            throw new InvalidOperationException("Event shuffle escaped fresh native act-zero generation");
        if (context.Act.GetType() != TargetActType)
            throw new NativePublicConstraintMismatchException("Native act differs from the public event-pool certificate");
        if (!NativeEventSelectionCertificate.Pool(context.Act).SequenceEqual(_pool) || !context.Pool.SequenceEqual(_pool))
            throw new InvalidOperationException("Native event shuffle pool changed");
    }
}

internal sealed record NativeEventScanObservation(ulong CandidateMask, ulong MustBeAllowed, ulong MayBeAllowed);

/// <summary>
/// Existential upper approximation of RunState.PullNextEvent/AcceptEvent with an
/// identity modifier. Cursor and visited set are carried jointly across every
/// selection. Uncertainty may add impossible paths, never remove a native path.
/// </summary>
internal static class NativeEventScanSuperset
{
    internal static bool Matches(IReadOnlyList<int> permutation, ulong naturalMask,
        IReadOnlyList<NativeEventScanObservation> observations, int initialCursor = 1, ulong initialVisited = 0)
    {
        int n = permutation.Count;
        if (n is < 1 or > 63 || permutation.Distinct().Count() != n
            || permutation.Any(i => i < 0 || i >= n) || initialCursor < 0)
            throw new ArgumentException("A complete unique event pool with at most 63 entries is required");
        ulong all = (1UL << n) - 1;
        if ((naturalMask & ~all) != 0 || (initialVisited & ~all) != 0
            || observations.Any(o => (o.CandidateMask & ~all) != 0 || (o.MayBeAllowed & ~all) != 0
                || (o.MustBeAllowed & ~o.MayBeAllowed) != 0)) throw new ArgumentException("Invalid event masks");
        var states = new HashSet<(int Cursor, ulong Visited)> { (initialCursor % n, initialVisited) };
        foreach (var observed in observations)
        {
            var next = new HashSet<(int Cursor, ulong Visited)>();
            foreach (var (cursor, visited) in states)
            {
                bool canReachFallback = true;
                for (int skip = 0; skip < n; skip++)
                {
                    int position = (cursor + skip) % n; ulong bit = 1UL << permutation[position];
                    if ((naturalMask & bit) == 0 || (visited & bit) != 0) continue;
                    if ((observed.MayBeAllowed & observed.CandidateMask & bit) != 0)
                        next.Add(((position + 1) % n, visited | bit));
                    if ((observed.MustBeAllowed & bit) != 0) { canReachFallback = false; break; }
                }
                if (!canReachFallback) continue;
                // Native fallback starts after exactly n failed candidates,
                // hence at the same cursor, and ignores eligibility and visits.
                for (int skip = 0; skip < n; skip++)
                {
                    int position = (cursor + skip) % n; ulong bit = 1UL << permutation[position];
                    if ((naturalMask & bit) == 0) continue;
                    if ((observed.CandidateMask & bit) != 0) next.Add(((position + 1) % n, visited | bit));
                    break;
                }
            }
            if (next.Count == 0) return false;
            states = next;
        }
        return true;
    }
}

internal sealed class NativeEventPermutationPlan(IReadOnlyList<ulong> words, NativeComponentStats stats, int maxTrials)
{
    internal const string CorrectionClaim = "whole-event-permutation-public-superset-root-constant-bounded-rejection-v1";
    internal IReadOnlyList<ulong> RawWords { get; } = Array.AsReadOnly(words.ToArray());
    internal NativeComponentStats Stats => stats;
    internal int MaxTrials => maxTrials;
    // If Z is the mass of the fixed necessary event, successful subdensity is
    // p(w) 1[E] sum((1-Z)^j,j=0..K-1). The unknown p/q equals its fixed envelope
    // Z/(1-(1-Z)^K), and therefore cancels. It is NOT a numerical ratio of one.
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ArgumentNullException.ThrowIfNull(nextWord); return true; }
}

/// <summary>One owned replay; ForcePrefixWords must enforce full-state freshness and alias guards.</summary>
internal sealed class NativePublicEventPermutationProposal(NativePublicEventPermutationCondition condition,
    NativeEventPermutationPlan plan, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords)
{
    private RunState? _run;
    private bool _begun, _complete;
    internal int ConditionedEventCount => _complete ? condition.TargetCount : 0;
    internal int ConditionedShuffleCount => _complete ? 1 : 0;
    internal NativeComponentStats Stats => plan.Stats;
    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null) throw new InvalidOperationException("Event permutation already owns a native run");
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }
    internal IDisposable? BeginGeneration(LabelEventGenerationContext context)
    {
        if (context.ActIndex != 0) return null;
        if (_run is null || !ReferenceEquals(_run, context.Run) || _begun || context.CompletedPermutation is not null)
            throw new InvalidOperationException("Event permutation boundary is unowned or repeated");
        condition.ValidateBoundary(context); _begun = true;
        int before = context.Rng.Counter;
        var inner = forcePrefixWords(plan.RawWords, context.Rng, "public whole event permutation");
        return new Completion(() =>
        {
            inner.Dispose();
            // Native exceptions must retain their identity on unwind. Absence of
            // its completion marker leaves this proposal unresolved.
            if (context.CompletedPermutation is not { } permutation) return;
            if (context.Rng.Counter - before != plan.RawWords.Count || !condition.Matches(permutation))
                throw new InvalidOperationException("Event permutation did not complete its certified native shuffle");
            _complete = true;
        });
    }
    internal void ValidateCompletion()
    { if (!_complete) throw new InvalidOperationException("Event permutation proposal did not complete"); }
    internal bool AcceptCorrection(Func<ulong> nextWord)
    { ValidateCompletion(); return plan.AcceptCorrection(nextWord); }
    private sealed class Completion(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}

internal static class NativeEventSelectionCertificate
{
    private static readonly string[] Shared = ["BrainLeech", "CrystalSphere", "DollRoom", "FakeMerchant",
        "PotionCourier", "RanwidTheElder", "RelicTrader", "RoomFullOfCheese", "SelfHelpBook", "SlipperyBridge",
        "StoneOfAllTime", "Symbiote", "TeaMaster", "TheFutureOfPotions", "TheLegendsWereTrue", "ThisOrThat",
        "WarHistorianRepy", "WelcomeToWongos"];
    private static readonly string[] Overgrowth = ["AromaOfChaos", "ByrdonisNest", "DenseVegetation",
        "JungleMazeAdventure", "LuminousChoir", "MorphicGrove", "SapphireSeed", "SunkenStatue", "TabletOfTruth",
        "UnrestSite", "Wellspring", "WhisperingHollow", "WoodCarvings"];
    private static readonly string[] Underdocks = ["AbyssalBaths", "DrowningBeacon", "EndlessConveyor", "PunchOff",
        "SpiralingWhirlpool", "SunkenStatue", "SunkenTreasury", "DoorsOfLightAndDark", "TrashHeap", "WaterloggedScriptorium"];

    internal static Type[] Pool(ActDefinition act)
    {
        string[] local = act.GetType() == typeof(Overgrowth) ? Overgrowth
            : act.GetType() == typeof(Underdocks) ? Underdocks : throw new InvalidOperationException("Unreviewed event act");
        Type[] pool = act.EffectiveEventPool.ToArray();
        if (!pool.Select(t => t.Name).SequenceEqual(local.Concat(Shared))
            || pool.Distinct().Count() != pool.Length || pool.Any(type => !type.IsSealed))
            throw new InvalidOperationException("Reviewed whole native event pool changed");
        return pool;
    }

    internal static ulong NaturalMask(IReadOnlyList<Type> pool)
    {
        ulong result = 0;
        for (int i = 0; i < pool.Count; i++) if (pool[i].Name != "WarHistorianRepy") result |= 1UL << i;
        return result;
    }

    // Exhaustive candidate exclusion is source-pinned to GenerateInitialOptions
    // of every entry in the two pools above. These three first-page key sequences
    // are unique, including all dynamic keys/locked variants of their competitors.
    // NO_OPTIONS alone is intentionally ambiguous and never identifies SelfHelpBook.
    internal static string? Identify(PublicOptionsObserved options)
    {
        if (options.Options.Any(o => o.Price is not null)) return null;
        string[] keys = options.Options.Select(o => o.Key).ToArray();
        if (keys.SequenceEqual(new[] { "DECIPHER", "SMASH" })) return "TabletOfTruth";
        if (keys.SequenceEqual(new[] { "BLOODY_INK", "TENTACLE_QUILL", "PRICKLY_SPONGE" })) return "WaterloggedScriptorium";
        if (keys.Length == 3 && (keys[0] is "READ_THE_BACK" or "READ_THE_BACK_LOCKED")
            && (keys[1] is "READ_PASSAGE" or "READ_PASSAGE_LOCKED")
            && (keys[2] is "READ_ENTIRE_BOOK" or "READ_ENTIRE_BOOK_LOCKED")) return "SelfHelpBook";
        return null;
    }

    internal static bool UnmodifiedSelection(PublicEvidenceAssets assets)
    {
        NaturalSourceCollector.InitializeNativeModels();
        // Between the public map snapshot and selection, RunDriver only adds the
        // coordinate and rolls room odds. For all actual hook listener types we
        // require the inherited no-op methods; modifiers cannot change eligibility
        // assets or replace the selected event/visited identity.
        var listeners = new List<Type>();
        foreach (string id in assets.Deck.Select(c => c.Id))
        {
            var model = ModelDb.All<CardModel>().SingleOrDefault(m => m.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        foreach (string id in assets.Relics.Select(r => r.Id))
        {
            var model = ModelDb.All<RelicModel>().SingleOrDefault(m => m.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        foreach (string id in assets.Potions.OfType<string>())
        {
            var model = ModelDb.All<PotionModel>().SingleOrDefault(m => m.GetType().Name == id);
            if (model is null) return false; listeners.Add(model.GetType());
        }
        string[] hooks = [nameof(AbstractModel.ModifyNextEvent), nameof(AbstractModel.ModifyUnknownMapPointRoomTypes),
            nameof(AbstractModel.ModifyOddsIncreaseForUnrolledRoomType)];
        return listeners.All(type => hooks.All(hook => type.GetMethod(hook)?.DeclaringType == typeof(AbstractModel)));
    }

    internal static (ulong Must, ulong May) Eligibility(IReadOnlyList<Type> pool, PublicEvidenceAssets assets, int selectionFloor)
    {
        ulong must = 0, may = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            // Exact public gates are the IsAllowed implementations. Card/pet/
            // hidden-bag predicates deliberately remain unknown. No sampled
            // hidden state participates in the predicate or its normalizer.
            bool? allowed = pool[i].Name switch
            {
                "CrystalSphere" or "DollRoom" or "FakeMerchant" or "PotionCourier" or "RanwidTheElder"
                    or "RelicTrader" or "StoneOfAllTime" or "Symbiote" or "WarHistorianRepy" or "WelcomeToWongos" => false,
                "LuminousChoir" => assets.Gold < 149 ? false : null,
                "MorphicGrove" => assets.Gold >= 100 && assets.Deck.Count(c => !c.Keywords.Contains("Eternal")) >= 2,
                "UnrestSite" => assets.Hp <= assets.MaxHp * 0.70m,
                "WhisperingHollow" => assets.Gold >= 44,
                "EndlessConveyor" => assets.Gold >= 120,
                "PunchOff" => selectionFloor >= 6,
                "TrashHeap" => assets.Hp > 5,
                "WaterloggedScriptorium" => assets.Gold >= 55,
                "SlipperyBridge" => selectionFloor > 6 && assets.Deck.Any(c => !c.Keywords.Contains("Eternal")),
                "TeaMaster" => assets.Gold >= 150,
                "TheFutureOfPotions" => assets.Potions.Count(p => p is not null) >= 2,
                "TheLegendsWereTrue" => assets.Deck.Length > 0 && assets.Hp >= 10,
                "ByrdonisNest" or "WoodCarvings" or "SpiralingWhirlpool" => null,
                "AromaOfChaos" or "DenseVegetation" or "JungleMazeAdventure" or "SapphireSeed" or "SunkenStatue"
                    or "TabletOfTruth" or "Wellspring" or "AbyssalBaths" or "DrowningBeacon" or "SunkenTreasury"
                    or "DoorsOfLightAndDark" or "BrainLeech" or "RoomFullOfCheese" or "SelfHelpBook" or "ThisOrThat" => true,
                _ => throw new InvalidOperationException("Unreviewed event eligibility")
            };
            if (allowed != false) may |= 1UL << i;
            if (allowed == true) must |= 1UL << i;
        }
        return (must, may);
    }
}
