using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicUnknownRoomTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };
    private static readonly RoomType[] Results = [RoomType.Monster, RoomType.Elite, RoomType.Treasure, RoomType.Shop, RoomType.Event];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryFiniteGridWordAndNativeFloatBoundaryHasExactlyOneCertifiedPreimage(bool excludeShop)
    {
        NativeUnknownRoomOdds[] states = [new(.1f, -1f, .02f, .03f), new(.2f, -2f, .04f, .06f),
            new(.9f, -5f, .15f, .06f), new(0f, -1f, 0f, 1f), new(.125f, .125f, .25f, .125f)];
        const int bits = 6;
        foreach (var state in states)
        {
            var buckets = Results.ToDictionary(result => result, result => NativeUnknownRoomMath.Bucket(state, excludeShop, result, bits));
            Assert.Equal(1UL << bits, buckets.Values.Aggregate(0UL, (sum, bucket) => sum + bucket.Size));
            for (ulong high = 0; high < 1UL << bits; high++)
            {
                var expected = Assert.Single(buckets, pair => high >= pair.Value.Start && high < pair.Value.Start + pair.Value.Size).Key;
                // All low suffixes share the exact high-word native conversion.
                Assert.Equal(expected, Roll(state, excludeShop, high << (64 - bits)));
            }
            foreach (var result in Results)
            {
                var bucket = NativeUnknownRoomMath.Bucket(state, excludeShop, result);
                if (bucket.Size == 0) continue;
                foreach (ulong low in new[] { 0UL, 2047UL })
                {
                    Assert.Equal(result, Roll(state, excludeShop, (bucket.Start << 11) | low));
                    Assert.Equal(result, Roll(state, excludeShop, ((bucket.Start + bucket.Size - 1) << 11) | low));
                }
                if (bucket.Start > 0) Assert.NotEqual(result, Roll(state, excludeShop, ((bucket.Start - 1) << 11) | 2047));
                if (bucket.Start + bucket.Size < 1UL << 53)
                    Assert.NotEqual(result, Roll(state, excludeShop, (bucket.Start + bucket.Size) << 11));
            }
        }
    }

    [Fact]
    public void PublicSequenceUsesNativeUpdatesAndRetainsOnlyProvedPrefixes()
    {
        var evidence = Evidence();
        string before = PublicJson.Serialize(evidence);
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(evidence, out var condition, out var reason), reason);
        Assert.Equal([RoomType.Event, RoomType.Monster], condition!.Targets.Select(target => target.RoomType));
        Assert.Equal(new(.1f, -1f, .02f, .03f), condition.Targets[0].Before);
        Assert.Equal(new(.2f, -2f, .04f, .06f), condition.Targets[0].After);
        Assert.Equal(new(.1f, -3f, .04f + .02f, .06f + .03f), condition.Targets[1].After);
        Assert.Equal(before, PublicJson.Serialize(evidence));
        Assert.Equal(condition.Targets.Aggregate(new ShuffleRational(1, 1), (mass, target) =>
            mass.Multiply(target.Bucket.Size, 1UL << 53)), condition.Envelope);
        foreach (var target in condition.Targets)
        foreach (ulong low in new[] { 0UL, 2047UL })
        {
            int draw = 0;
            var plan = NativeUnknownRoomMath.Create(target, () => draw++ == 0 ? target.Bucket.Size * 2 : low);
            Assert.Equal(2, draw); Assert.Equal(low, plan.RawWords.Single() & 2047UL);
            Assert.Equal(target.RoomType, Roll(target.Before, target.ExcludeShop, plan.RawWords.Single()));
            Assert.Equal(new(target.Bucket.Size, 1UL << 53), plan.NativeToProposalRatio);
        }
        foreach (string modifier in new[] { "JuzuBracelet", "GoldenCompass", "LanternKey", "UnreviewedRelic" })
        {
            var modified = Evidence(secondRelic: modifier);
            Assert.True(NativePublicUnknownRoomCondition.TryCreate(modified, out var prefix, out _));
            Assert.Single(prefix!.Targets); Assert.Equal("unknown_room_public_modifier_closure_required", prefix.SuffixFallbackReason);
        }
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(Evidence(gapBeforeSecond: true), out var gap, out _));
        Assert.Single(gap!.Targets); Assert.Equal("unknown_room_history_gap", gap.SuffixFallbackReason);
        Assert.False(NativePublicUnknownRoomCondition.TryCreate(Evidence(missingGraph: true), out _, out reason));
        Assert.Equal("unknown_room_complete_shop_blacklist_required", reason);
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(Evidence(previousShop: true, missingSecondGraph: true), out var afterShop, out _));
        Assert.Equal(2, afterShop!.Targets.Count); Assert.True(afterShop.Targets[1].ExcludeShop);
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(Evidence(shopChildren: true), out var beforeShops, out _));
        Assert.True(beforeShops!.Targets[0].ExcludeShop);
        Assert.Equal(.03f, beforeShops.Targets[0].After.Shop);
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(Evidence(shopChildren: true, mixedShopChildren: true), out var mixed, out _));
        Assert.False(mixed!.Targets[0].ExcludeShop);
        Assert.Equal(.06f, mixed.Targets[0].After.Shop);
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(Evidence(nestedShop: true), out var nested, out _));
        Assert.Equal(2, nested!.Targets.Count); Assert.True(nested.Targets[1].ExcludeShop);
    }

    [Fact]
    public void OwnedNativeBoundaryKeepsOneAdvanceNativeUpdateFutureWordsAndStickyFailures()
    {
        var evidence = Evidence();
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(evidence, out var condition, out _));
        var run = FreshRun();
        var tape = new NativeLabelTape(new(32, 64, 128, 0, 0));
        var failures = new List<Exception>();
        var proposal = new NativePublicUnknownRoomProposal(condition!, new Rng(891).NextUnsignedLong, Force(tape), failures.Add);
        proposal.AttachHypotheticalRun(run); tape.AttachHypotheticalRun(run);
        using var words = LabelRandomScope.Enter(Word(tape));
        using var scope = LabelUnknownRoomScope.Enter(proposal.BeginResolution);
        int eventAt = 0;
        foreach (var target in condition!.Targets)
        {
            while (eventAt <= target.MapEndEventOrdinal) proposal.ObservePublicEvidence(evidence.Events[eventAt++]);
            while (run.TotalFloor < target.Floor) run.AddVisitedMapCoord(new(0, run.TotalFloor));
            var point = run.Map.GetPoint(0, target.Coordinate.Row)!;
            point.PointType = MapPointType.Unknown;
            Assert.Equal(target.RoomType, RoomFactory.ResolveRoomType(run, point));
            Assert.Equal(target.After, NativeUnknownRoomOdds.Read(run.Odds.UnknownMapPoint));
            Assert.Equal(target.Index + 1, run.Rng.UnknownMapPoint.Counter);
            Assert.Equal(target.Index + 1, tape.ConditionedCells);
        }
        proposal.ValidateCompletion();
        Assert.True(proposal.AcceptCorrection(() => throw new Exception("Root mass equals envelope")));
        Assert.Equal(condition.Envelope, proposal.NativeToProposalRatio); Assert.Empty(failures);
        // The next raw cell is still the owning oracle value, and the base engine
        // has advanced once at each roll regardless of the substituted word.
        var expectedRng = new Rng(run.Rng.UnknownMapPoint.ToSerializable());
        var state = expectedRng.ToSerializable();
        ulong expected = Word(tape)(new(state.state0, state.state1, state.state2, state.state3));
        Assert.Equal(expected, run.Rng.UnknownMapPoint.NextUnsignedLong());
        Assert.Equal(2, tape.ConditionedCells);

        var missing = NewReady(condition, evidence, out var missingRun, out var missingTape, out var missingErrors);
        var context = new LabelUnknownRoomContext(missingRun, missingRun.CurrentMapPoint!, missingRun.Rng.UnknownMapPoint, false);
        using (LabelRandomScope.Enter(Word(missingTape)))
            Assert.Throws<InvalidOperationException>(() => missing.BeginResolution(context)!.Dispose());
        Assert.NotEmpty(missingErrors); Assert.Throws<InvalidOperationException>(missing.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => missing.BeginResolution(context));

        var alias = NewReady(condition, evidence, out var aliasRun, out var aliasTape, out var aliasErrors);
        var aliasState = aliasRun.Rng.UnknownMapPoint.ToSerializable();
        Word(aliasTape)(new(aliasState.state0, aliasState.state1, aliasState.state2, aliasState.state3));
        using (LabelRandomScope.Enter(Word(aliasTape)))
        using (LabelUnknownRoomScope.Enter(alias.BeginResolution))
            Assert.Throws<InvalidOperationException>(() => RoomFactory.ResolveRoomType(aliasRun, aliasRun.CurrentMapPoint!));
        Assert.NotEmpty(aliasErrors); Assert.True(aliasTape.HasConditionedWordFailure);
    }

    [Fact]
    public async Task RetainedSource24002ReplaysBothPublicUnknownRoomsAndEveryFutureNativeCell()
    {
        var prior = Prior.Freeze();
        var recipe = prior.Draw(new Rng(24002, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        Assert.True(NativePublicUnknownRoomCondition.TryCreate(root, prior, out var condition, out var reason), reason);
        Assert.Equal([3, 5], condition!.Targets.Select(target => target.Floor));
        Assert.Equal([RoomType.Event, RoomType.Monster], condition.Targets.Select(target => target.RoomType));
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 92117UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical, expectedPublicEvidence: root.PublicEvidence);
        var proposal = new NativePublicUnknownRoomProposal(condition, new Rng(hypothetical.ProposalSeed,
            "public-unknown-fixture").NextUnsignedLong, Force(tape));
        int forwarded = 0; RunState? owned = null;
        using var scope = LabelUnknownRoomScope.Enter(context =>
        {
            if (owned is null) { owned = context.Run; proposal.AttachHypotheticalRun(owned); }
            if (proposal.ConditionedRoomCount < condition.Targets.Count)
            {
                var target = condition.Targets[proposal.ConditionedRoomCount];
                // The owning tape has already checked the actual native recorder
                // prefix. Forward exactly those verified events to this isolated
                // proposal fixture; production forwards them as they are appended.
                var prefix = (NativePublicPrefixConstraint)typeof(NativeLabelTape)
                    .GetField("_publicPrefixConstraint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tape)!;
                Assert.Equal(target.MapEndEventOrdinal + 1, prefix.CheckedEvents);
                while (forwarded < prefix.CheckedEvents)
                    proposal.ObservePublicEvidence(root.PublicEvidence!.Events[forwarded++]);
            }
            return proposal.BeginResolution(context);
        });
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world); proposal.ValidateCompletion(); Assert.Equal(2, tape.ConditionedCells);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(condition.Envelope, proposal.NativeToProposalRatio);
        Assert.True(proposal.AcceptCorrection(() => throw new Exception("No correction word required")));
        using var disabled = LabelUnknownRoomScope.Enter(_ => null);
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe()); await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(2, tape.ConditionedCells);
    }

    private static NativePublicUnknownRoomProposal NewReady(NativePublicUnknownRoomCondition condition,
        PublicRunEvidence evidence, out RunState run, out NativeLabelTape tape, out List<Exception> errors)
    {
        run = FreshRun(); tape = new(new(1, 2, 3, 0, 0)); errors = [];
        var proposal = new NativePublicUnknownRoomProposal(condition, new Rng(88).NextUnsignedLong, Force(tape), errors.Add);
        proposal.AttachHypotheticalRun(run); tape.AttachHypotheticalRun(run);
        var target = condition.Targets[0];
        foreach (var item in evidence.Events.Take((int)target.MapEndEventOrdinal + 1)) proposal.ObservePublicEvidence(item);
        while (run.TotalFloor < target.Floor) run.AddVisitedMapCoord(new(0, run.TotalFloor));
        run.CurrentMapPoint!.PointType = MapPointType.Unknown;
        return proposal;
    }

    private static RoomType Roll(NativeUnknownRoomOdds state, bool excludeShop, ulong word)
    {
        var rng = new Rng(73);
        var odds = new UnknownMapPointOdds(rng, NullOddsHooks.Instance)
        { MonsterOdds = state.Monster, EliteOdds = state.Elite, TreasureOdds = state.Treasure, ShopOdds = state.Shop };
        int draws = 0;
        using var scope = LabelRandomScope.Enter(_ => { draws++; return word; });
        var result = odds.Roll(excludeShop ? [RoomType.Shop] : []);
        Assert.Equal(1, draws); Assert.Equal(1, rng.Counter); return result;
    }

    private static PublicRunEvidence Evidence(string? secondRelic = null, bool gapBeforeSecond = false,
        bool missingGraph = false, bool missingSecondGraph = false, bool previousShop = false, bool shopChildren = false,
        bool nestedShop = false, bool mixedShopChildren = false)
    {
        var assets = new PublicEvidenceAssets(70, 70, 99, [], [], [], 3, 0, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets), null, PublicRunEvidence.CompleteMapVersion);
        PublicMapNodeType[] types = [PublicMapNodeType.Ancient, PublicMapNodeType.Monster, PublicMapNodeType.Unknown,
            (shopChildren || previousShop) ? PublicMapNodeType.Shop : nestedShop ? PublicMapNodeType.Unknown : PublicMapNodeType.Monster,
            nestedShop ? PublicMapNodeType.Monster : PublicMapNodeType.Unknown, PublicMapNodeType.Boss];
        var nodes = types.Select((type, row) => new PublicMapNode(new(0, row), type)).ToImmutableArray();
        var edges = Enumerable.Range(0, 5).Select(row => new PublicMapEdge(new(0, row), new(0, row + 1))).ToImmutableArray();
        // This branch is outside the current/next choice slice at the unknown
        // roll. Only the complete graph distinguishes all-shop from mixed children.
        var graphNodes = mixedShopChildren
            ? nodes.Add(new(new(1, 3), PublicMapNodeType.Monster)).OrderBy(node => node.Coordinate.Row)
                .ThenBy(node => node.Coordinate.Col).ToImmutableArray() : nodes;
        var graphEdges = mixedShopChildren
            ? edges.Add(new(new(0, 2), new(1, 3))).Add(new(new(1, 3), new(0, 4)))
                .OrderBy(edge => edge.From.Row).ThenBy(edge => edge.From.Col)
                .ThenBy(edge => edge.To.Row).ThenBy(edge => edge.To.Col).ToImmutableArray() : edges;
        var graph = new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete, graphNodes, graphEdges, new(0, 0), [new(0, 5)]);
        for (int row = 0; row < (nestedShop ? 3 : 4); row++)
        {
            if (row == 3 && gapBeforeSecond) recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
            if (row == 3 && secondRelic is not null)
                assets = new(70, 70, 99, [], [new(secondRelic, new Dictionary<string, int>())], [], 3, 0, 0, 0);
            long mapOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, row + 1);
            bool missing = missingGraph || row == 3 && missingSecondGraph;
            long observed = recorder.Record(mapOwner, new PublicMapObserved(new(0, row), [nodes[row], nodes[row + 1]],
                [edges[row]], [new(new(0, row + 1), true)], missing ? PublicCurrentMapCapture.Missing() : graph));
            recorder.Record(mapOwner, new PublicMapChosen(observed, new(0, row + 1)));
            recorder.Record(mapOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, assets));
            var kind = row == 1 ? PublicEvidenceOwnerKind.Event
                : row == 2 && (previousShop || shopChildren) ? PublicEvidenceOwnerKind.Shop : PublicEvidenceOwnerKind.Combat;
            long room = recorder.BeginOwner(kind, 0, row + 2);
            if (kind == PublicEvidenceOwnerKind.Combat)
                recorder.Record(room, new PublicCombatFact(PublicCombatFactKind.Started));
            if (nestedShop && row == 1)
            {
                long child = recorder.BeginOwner(PublicEvidenceOwnerKind.Shop, 0, row + 2, room);
                recorder.Record(child, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, assets));
            }
            recorder.Record(room, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, assets));
        }
        return recorder.Capture();
    }

    private static RunState FreshRun()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-unknown-lifecycle", [new Overgrowth(), new Hive(), new Glory()], ascensionLevel: 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        // A synthetic connected native map isolates the resolver's owned seam;
        // the separate source24002 fixture exercises the unmodified full lifecycle.
        typeof(RunState).GetProperty(nameof(RunState.Map))!.SetValue(run, new TestMap());
        run.AddVisitedMapCoord(new(0, 0)); return run;
    }
    private sealed class TestMap : ActMap
    {
        protected override MapPoint?[,] Grid { get; } = new MapPoint?[1, 6];
        public override MapPoint BossMapPoint => Grid[0, 5]!;
        public override MapPoint StartingMapPoint => Grid[0, 0]!;
        internal TestMap()
        {
            for (int row = 0; row < 6; row++) Grid[0, row] = new(0, row)
            { PointType = row == 0 ? MapPointType.Ancient : row == 5 ? MapPointType.Boss : MapPointType.Monster };
            for (int row = 0; row < 5; row++) Grid[0, row]!.AddChildPoint(Grid[0, row + 1]!);
        }
    }
    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> Force(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
}
