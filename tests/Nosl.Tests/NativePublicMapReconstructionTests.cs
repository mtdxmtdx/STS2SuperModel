using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicMapReconstructionTests
{
    internal static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ImportedSemanticGraphPreservesRuntimeWithPermutedInsertionOrders(bool overgrowth, bool boots)
    {
        NaturalSourceCollector.InitializeNativeModels();
        const string seed = "complete-map-runtime";
        var native = new RunState(seed, Acts(overgrowth), 10);
        var nodes = Nodes(native.Map); var edges = Edges(native.Map);
        var retained = StandardActMap.CreateRng(native.Rng.Seed, 0);
        var rngBefore = retained.ToSerializable();
        var imported = StandardActMap.ReconstructForLabels(retained, 15, nodes.Reverse().ToArray(), edges.Reverse().ToArray());
        Assert.Equal(rngBefore, retained.ToSerializable());
        Assert.Equal(NativeMapTravelConditionTests.MapShape(native.Map), NativeMapTravelConditionTests.MapShape(imported));
        Assert.Contains(nodes, node => native.Map.GetPoint(node.Coordinate)!.Children.Select(p => p.coord)
            .SequenceEqual(imported.GetPoint(node.Coordinate)!.Children.Select(p => p.coord)) == false);
        int callbacks = 0;
        RunState reconstructed;
        using (LabelMapConstructionScope.Enter(context =>
        {
            callbacks++;
            Assert.Empty(context.Run.Players); Assert.Null(context.Run.Map);
            return imported;
        })) reconstructed = new RunState(seed, Acts(overgrowth), 10);
        Assert.Equal(1, callbacks); Assert.IsType<StandardActMap>(reconstructed.Map);
        foreach (var run in new[] { native, reconstructed })
        {
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
            if (boots) await RelicCmd.Obtain(ModelDb.Relic<WingedBoots>(), player);
        }
        foreach (var node in nodes.OrderBy(n => n.Coordinate.row).ThenBy(n => n.Coordinate.col))
        {
            var original = native.Map.GetPoint(node.Coordinate)!;
            var copy = reconstructed.Map.GetPoint(node.Coordinate)!;
            Assert.NotSame(original, copy);
            Assert.Equal(original.PointType, copy.PointType);
            Assert.Equal(original.parents.Select(p => p.coord).OrderBy(c => c), copy.parents.Select(p => p.coord).OrderBy(c => c));
            var originalChoices = MapTravel.GetTravelablePointsFrom(native, original).ToArray();
            var copyChoices = MapTravel.GetTravelablePointsFrom(reconstructed, copy).ToArray();
            Assert.Equal(originalChoices.Select(p => p.coord).OrderBy(c => c), copyChoices.Select(p => p.coord).OrderBy(c => c));
            if (originalChoices.Length > 0)
                Assert.Equal(NativeSourceMapChoice.Choose(originalChoices, 25, 70).coord,
                    NativeSourceMapChoice.Choose(copyChoices, 25, 70).coord);
            Assert.Equal(RoomFactory.ResolveRoomType(native, original), RoomFactory.ResolveRoomType(reconstructed, copy));
        }
        Assert.Same(imported.StartingMapPoint, imported.GetPoint(3, 0));
        Assert.Same(imported.BossMapPoint, imported.GetPoint(3, 16));
        Assert.Empty(imported.GetPointsInRow(16));
        Assert.Equal(native.Map.startMapPoints.Select(p => p.coord).OrderBy(c => c), imported.startMapPoints.Select(p => p.coord).OrderBy(c => c));
        Assert.Equal(native.Rng.UnknownMapPoint.ToSerializable(), reconstructed.Rng.UnknownMapPoint.ToSerializable());
        for (int i = 0; i < 5; i++)
            Assert.Equal(native.PullNextEncounter(RoomType.Monster).IdEntry, reconstructed.PullNextEncounter(RoomType.Monster).IdEntry);
        Assert.Equal(native.Rng.UpFront.ToSerializable(), reconstructed.Rng.UpFront.ToSerializable());
        if (boots)
        {
            foreach (var run in new[] { native, reconstructed })
            {
                var from = run.Map.GetAllMapPoints().First(p => run.Map.GetPointsInRow(p.coord.row + 1)
                    .Any(c => !p.Children.Contains(c)));
                var to = run.Map.GetPointsInRow(from.coord.row + 1).First(c => !from.Children.Contains(c));
                run.AddVisitedMapCoord(from.coord); run.AddVisitedMapCoord(to.coord);
                await new RestSiteRoom().Enter(run);
                Assert.Equal(1, Assert.Single(run.Players[0].Relics.OfType<WingedBoots>()).TimesUsed);
            }
        }
    }

    [Fact]
    public void UnknownShopExclusionAndSecondBossUseNativeSemanticConnections()
    {
        var native = new RunState("complete-map-shop", new Overgrowth(), 10);
        var unknown = native.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.Unknown);
        foreach (var child in unknown.Children) child.PointType = MapPointType.Shop;
        var imported = StandardActMap.ReconstructForLabels(StandardActMap.CreateRng(native.Rng.Seed, 0), 15,
            Nodes(native.Map).Reverse().ToArray(), Edges(native.Map).Reverse().ToArray(), hasSecondBoss: true);
        RunState reconstructed;
        using (LabelMapConstructionScope.Enter(_ => imported)) reconstructed = new RunState("complete-map-shop", new Overgrowth(), 10);
        var copy = imported.GetPoint(unknown.coord)!;
        Assert.All(copy.Children, p => Assert.Equal(MapPointType.Shop, p.PointType));
        foreach (var run in new[] { native, reconstructed })
        {
            run.Odds.UnknownMapPoint.MonsterOdds = 0;
            run.Odds.UnknownMapPoint.TreasureOdds = 0;
            run.Odds.UnknownMapPoint.ShopOdds = 1;
        }
        using (LabelRandomScope.Enter(_ => ulong.MaxValue))
        {
            // With shop odds one, omitting the semantic child-type exclusion
            // would select Shop. Both native and reconstructed graphs forbid it.
            Assert.Equal(RoomType.Event, RoomFactory.ResolveRoomType(native, unknown));
            Assert.Equal(RoomType.Event, RoomFactory.ResolveRoomType(reconstructed, copy));
        }
        Assert.NotNull(imported.SecondBossMapPoint);
        Assert.Same(imported.SecondBossMapPoint, imported.GetPoint(3, 17));
        Assert.Same(imported.SecondBossMapPoint, Assert.Single(imported.BossMapPoint.Children));
        Assert.Same(imported.BossMapPoint, Assert.Single(imported.SecondBossMapPoint.parents));
        Assert.Empty(imported.GetPointsInRow(17));
    }

    [Theory]
    [InlineData("old_law")]
    [InlineData("old_profile")]
    [InlineData("missing_capture")]
    [InlineData("wrong_script")]
    [InlineData("gap")]
    public void UnsupportedObservationFallsBackWithoutChangingEvidence(string change)
    {
        var prior = Prior;
        var run = new RunState("complete-map-condition", Acts(true), 10);
        var evidence = Evidence(run, change == "missing_capture");
        if (change == "old_law") prior = prior with { SchemaVersion = NativeTapePrior.RewardsVersion };
        if (change == "old_profile") prior = prior with { Execution = prior.Execution with
            { PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1 } };
        if (change == "wrong_script") prior = prior with { Execution = prior.Execution with { OutsideCombatScript = "unsupported" } };
        if (change == "gap") evidence = new(evidence.SchemaVersion, false, evidence.Events.SetItem(4,
            new(4, evidence.Events[4].OwnerOrdinal, new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted))));
        string before = PublicJson.Serialize(evidence);
        Assert.False(NativePublicMapReconstructionCondition.TryCreate(evidence, prior, out var condition, out var reason));
        Assert.Null(condition); Assert.NotNull(reason); Assert.Equal(before, PublicJson.Serialize(evidence));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProposalReconstructsFreshOwnedForksAndLeavesLaterMapsNative(bool overgrowth)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var source = new RunState("public-complete-fixture", Acts(overgrowth), 10);
        var evidence = PublicJson.Read<PublicRunEvidence>(PublicJson.Serialize(Evidence(source)));
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(evidence, Prior, out var condition, out var reason), reason);
        var shapes = new List<string>(); var maps = new List<ActMap>();
        for (int fork = 0; fork < 2; fork++)
        {
            var proposal = new NativePublicMapReconstructionProposal(condition!);
            Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            var oracle = new NativeMapOracle(123);
            using var random = LabelRandomScope.EnterMapRewardProvenance(oracle.Word, new NativeRewardsOracle(123).Word, s => s.State0);
            using var construction = LabelMapConstructionScope.Enter(proposal.Construct);
            var run = new RunState("independent-held-root", Acts(overgrowth), 10);
            Assert.Equal(0, oracle.DistinctCells);
            proposal.AttachHypotheticalRun(run); proposal.ValidateCompletion();
            Assert.Equal(1, proposal.ReconstructedMapCount);
            Assert.Equal(NativeMapTravelConditionTests.MapShape(source.Map), NativeMapTravelConditionTests.MapShape(run.Map));
            maps.Add(run.Map);
            Assert.Throws<InvalidOperationException>(() => new RunState("foreign-root", Acts(overgrowth), 10));
            run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
            run.AdvanceToNextAct();
            Assert.Equal(1, run.CurrentActIndex); Assert.True(oracle.DistinctCells > 0);
            Assert.Equal(1, proposal.ReconstructedMapCount); proposal.ValidateCompletion();
            shapes.Add(NativeMapTravelConditionTests.MapShape(run.Map));
        }
        Assert.NotSame(maps[0], maps[1]); Assert.Equal(shapes[0], shapes[1]);
    }

    [Theory]
    [InlineData("old_law")]
    [InlineData("wrong_rng")]
    [InlineData("advanced_rng")]
    [InlineData("wrong_acts")]
    [InlineData("late_boundary")]
    [InlineData("unattached_repeat")]
    public void ProposalRejectsWrongOrRepeatedConstructorBoundary(string change)
    {
        var source = new RunState("public-complete-guards", Acts(true), 10);
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(Evidence(source), Prior, out var condition, out var reason), reason);
        var proposal = new NativePublicMapReconstructionProposal(condition!);
        var oracle = new NativeMapOracle(123);
        using var random = change == "old_law"
            ? LabelRandomScope.EnterRewardProvenance(new NativeRewardsOracle(123).Word, s => s.State0)
            : LabelRandomScope.EnterMapRewardProvenance(oracle.Word, new NativeRewardsOracle(123).Word, s => s.State0);
        if (change == "late_boundary")
        {
            Assert.Throws<InvalidOperationException>(() => proposal.Construct(new(source, source.Act, StandardActMap.CreateRng(source.Rng.Seed, 0))));
            return;
        }
        using var construction = LabelMapConstructionScope.Enter(context =>
        {
            if (change == "wrong_rng") context = context with { Rng = new Rng(101) };
            if (change == "advanced_rng") context.Rng.NextUnsignedLong();
            return proposal.Construct(context);
        });
        if (change == "unattached_repeat")
            _ = new RunState("first-root", Acts(true), 10);
        Assert.Throws<InvalidOperationException>(() => new RunState("wrong-root",
            change == "wrong_acts" ? [new Overgrowth()] : Acts(true), 10));
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Fact]
    public void FiniteIndependentMapMarginalizationPreservesPosteriorAndOldAliasesDoNot()
    {
        // Exhaust every assignment of a root, act, other latent, and two fresh
        // Map bits. A nontrivial shared map function has Q(M=0)=1/2 for all roots.
        int[] native = [0, 0], imported = [0, 0];
        for (int root = 0; root < 2; root++)
        for (int act = 0; act < 2; act++)
        for (int other = 0; other < 2; other++)
        {
            bool remainingPublicMatch = root == 0 || other == act;
            if (remainingPublicMatch) imported[root]++;
            for (int firstMapWord = 0; firstMapWord < 2; firstMapWord++)
            for (int secondMapWord = 0; secondMapWord < 2; secondMapWord++)
                if ((firstMapWord ^ secondMapWord) == 0 && remainingPublicMatch) native[root]++;
        }
        Assert.Equal([8, 4], native); Assert.Equal([4, 2], imported);
        Assert.Equal(native[0] * imported.Sum(), imported[0] * native.Sum());

        // Under the old state law only root zero aliases its Map draw to the
        // already observed other draw. Pinning M while holding that root loses
        // its root-dependent likelihood, changing posterior odds from 2:1 to 1:1.
        int[] aliased = [0, 0], incorrectlyImported = [0, 0];
        for (int root = 0; root < 2; root++)
        for (int other = 0; other < 2; other++)
        for (int independent = 0; independent < 2; independent++)
        {
            int map = root == 0 ? other : independent;
            if (other == 0) incorrectlyImported[root]++;
            if (other == 0 && map == 0) aliased[root]++;
        }
        Assert.Equal([2, 1], aliased); Assert.Equal([2, 2], incorrectlyImported);
        Assert.NotEqual(aliased[0] * incorrectlyImported.Sum(), incorrectlyImported[0] * aliased.Sum());
    }

    [Fact]
    public async Task RegeneratingTheMarginalizedActZeroComponentFailsClosedEvenIfCaught()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var source = new RunState("public-complete-regeneration", Acts(true), 10);
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(Evidence(source), Prior, out var condition, out var reason), reason);
        var proposal = new NativePublicMapReconstructionProposal(condition!);
        var oracle = new NativeMapOracle(123);
        using var random = LabelRandomScope.EnterMapRewardProvenance(oracle.Word, new NativeRewardsOracle(123).Word, s => s.State0);
        using var construction = LabelMapConstructionScope.Enter(proposal.Construct);
        var run = new RunState("held-regeneration-root", Acts(true), 10);
        proposal.AttachHypotheticalRun(run);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RelicCmd.Obtain(ModelDb.Relic<GoldenCompass>(), player));
        Assert.Equal("The marginalized act-zero Map component cannot be regenerated", error.Message);
        Assert.Equal(0, oracle.DistinctCells);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    internal static ActDefinition[] Acts(bool overgrowth) => [overgrowth ? new Overgrowth() : new Underdocks(), new Hive(), new Glory()];
    internal static LabelMapNode[] Nodes(ActMap map) => Points(map).Select(p => new LabelMapNode(p.coord, p.PointType)).ToArray();
    internal static LabelMapEdge[] Edges(ActMap map) => Points(map).SelectMany(p => p.Children.Select(c => new LabelMapEdge(p.coord, c.coord))).ToArray();
    private static IEnumerable<MapPoint> Points(ActMap map) => map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint)
        .Concat(map.SecondBossMapPoint is { } second ? [second] : []);

    internal static PublicRunEvidence Evidence(RunState run, bool missing = false)
    {
        PublicRelic Relic(string id) => new(id, new Dictionary<string, int> { ["isWax"] = 0, ["isMelted"] = 0, ["stackCount"] = 1 });
        PublicEvidenceAssets Assets(params string[] ids) => new(70, 70, 99, [], ids.Select(Relic).ToArray(), [null, null], 3, 2, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets("RingOfTheSnake")), null,
            PublicRunEvidence.CompleteMapVersion);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        long offer = recorder.Record(owner, new PublicOptionsObserved([new("GoldenPearl", false), new("WingedBoots", false), new("LeafyPoultice", false)]));
        recorder.Record(owner, new PublicOptionChosen(offer, "GoldenPearl"));
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Assets("RingOfTheSnake", "GoldenPearl")));
        long map = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, 1);
        var current = run.Map.StartingMapPoint; var choices = current.Children.ToArray();
        long observed = recorder.Record(map, NativePublicMapSlice.Observe(run.Map, current, choices,
            PublicMapObservationProfiles.CompleteGraphV1, missing ? null : NativePublicCurrentMapCapture.Observe(run.Map, 0)));
        var chosen = NativeSourceMapChoice.Choose(choices, 70, 70);
        recorder.Record(map, new PublicMapChosen(observed, new(chosen.coord.col, chosen.coord.row)));
        recorder.Record(map, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        return recorder.Capture();
    }
}
