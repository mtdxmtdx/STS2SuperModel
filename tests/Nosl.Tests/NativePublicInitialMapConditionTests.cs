using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicInitialMapConditionTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        EligibleCombats = 1, EligibleDecisionsPerCombat = 1,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedSliceConditionsTheWholePrefixAndLeavesBothNativeActsAvailable(bool boots)
    {
        var recipe = new NativeTapeRecipe(17, 23, 29, 0, 0);
        foreach (bool overgrowth in new[] { true, false })
        {
            var random = new Rng(31, "public-map-native-fixture"); int draw = 0;
            var original = NativeComponentRejection.Evaluate(() => new RunState(recipe.IndependentRunSeed, ascensionLevel: 10),
                () => draw++ == 0 ? overgrowth ? 0UL : ulong.MaxValue : random.NextUnsignedLong());
            var evidence = Evidence(original.Value, boots ? "WingedBoots" : "GoldenPearl");
            // JSON round-trip is the entire certificate input. No source run, seed,
            // native map or native act is passed to TryCreate.
            var detached = PublicJson.Read<PublicRunEvidence>(PublicJson.Serialize(evidence));
            var root = new DecisionPacket("fixture", null, [], detached);
            Assert.True(NativeInitialPrefixCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
            Assert.NotNull(condition!.PublicMap); Assert.Null(condition.TargetActType);
            Assert.Null(condition.MapTravel); Assert.Equal(boots, condition.HasFreeTravel);
            Assert.True(condition.MatchesMap(original.Value.Map));
            var inputs = new Queue<ulong>(new[] { recipe.RunSeed }.Concat(original.Trace.DistinctBy(w => w.State).Select(w => w.Word)));
            var plan = condition.Prepare(recipe with { RunSeed = 997 }, 1, () => inputs.Dequeue());
            Assert.Empty(inputs); Assert.Equal(recipe, plan.SelectedRecipe);
            Assert.Equal(overgrowth ? typeof(Overgrowth) : typeof(Underdocks), plan.TargetActType);
            Assert.Equal(original.Trace, plan.Trace); Assert.Equal(1, plan.RunSeedDraws);
            Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("Root constant cancels")));

            NativeComponentTrial<RunState>? miss = null; ulong missSeed = 0;
            for (int i = 0; i < 32 && miss is null; i++)
            {
                missSeed = (ulong)(1000 + i); var missRandom = new Rng((ulong)(100 + i), "map-clean-miss-fixture");
                var candidate = NativeComponentRejection.Evaluate(() => new RunState(
                    (recipe with { RunSeed = missSeed }).IndependentRunSeed, ascensionLevel: 10), missRandom.NextUnsignedLong);
                if (!condition.MatchesMap(candidate.Value.Map)) miss = candidate;
            }
            Assert.NotNull(miss);
            var retryWords = new Queue<ulong>(new[] { missSeed }.Concat(miss.Trace.DistinctBy(w => w.State).Select(w => w.Word))
                .Concat(new[] { recipe.RunSeed }).Concat(original.Trace.DistinctBy(w => w.State).Select(w => w.Word)));
            var retried = condition.Prepare(recipe with { RunSeed = 997 }, 2, () => retryWords.Dequeue());
            Assert.Empty(retryWords); Assert.Equal(2, retried.RunSeedDraws);
            Assert.Equal(plan.SelectedRecipe, retried.SelectedRecipe); Assert.Equal(plan.Trace, retried.Trace);
            Assert.Equal(miss.Trace.Count + original.Trace.Count, retried.Stats.TotalWordDraws);
            var exhaustedWords = new Queue<ulong>(new[] { missSeed }.Concat(miss.Trace.DistinctBy(w => w.State).Select(w => w.Word)));
            var exhausted = Assert.Throws<NativeComponentBudgetExceededException>(() =>
                condition.Prepare(recipe, 1, () => exhaustedWords.Dequeue()));
            Assert.Empty(exhaustedWords); Assert.Equal(1, exhausted.Stats.CompletedTrials);
            Assert.Equal(miss.Trace.Count, exhausted.Stats.TotalWordDraws);

            var tape = NativeLabelTape.ForDeclaredPrior(Prior, plan.SelectedRecipe, initialPrefixPlan: plan);
            using (tape.EnterScope())
            {
                var replay = new RunState(plan.SelectedRecipe.IndependentRunSeed, ascensionLevel: 10);
                tape.AttachHypotheticalRun(replay); tape.ValidateProposalCompletion();
                Assert.True(condition.MatchesMap(replay.Map));
                Assert.Equal(NativeMapTravelConditionTests.MapShape(original.Value.Map), NativeMapTravelConditionTests.MapShape(replay.Map));
                int conditioned = tape.ConditionedCells, cells = tape.DistinctCells;
                // A later native map uses its ordinary tape words, not the consumed
                // constructor prefix. ReplayCopy reproduces those same words.
                NaturalSourceCollector.InitializeNativeModels();
                replay.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), replay));
                replay.AdvanceToNextAct();
                Assert.Equal(1, replay.CurrentActIndex); Assert.Equal(conditioned, tape.ConditionedCells);
                Assert.True(tape.DistinctCells > cells);
                var laterShape = NativeMapTravelConditionTests.MapShape(replay.Map);
                var copy = tape.ReplayCopy();
                using (copy.EnterScope())
                {
                    var repeated = new RunState(plan.SelectedRecipe.IndependentRunSeed, ascensionLevel: 10);
                    // Completion alone does not authorize another map callback.
                    repeated.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), repeated));
                    var error = Assert.Throws<InvalidOperationException>(() => repeated.AdvanceToNextAct());
                    Assert.Equal("Later map escaped its owned hypothetical run", error.Message);
                }
                var validCopy = tape.ReplayCopy();
                using (validCopy.EnterScope())
                {
                    var repeated = new RunState(plan.SelectedRecipe.IndependentRunSeed, ascensionLevel: 10);
                    validCopy.AttachHypotheticalRun(repeated);
                    repeated.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), repeated));
                    repeated.AdvanceToNextAct();
                    Assert.Equal(laterShape, NativeMapTravelConditionTests.MapShape(repeated.Map));
                    validCopy.ValidateProposalCompletion();
                }
            }
        }
    }

    [Theory]
    [InlineData("run_gap")]
    [InlineData("before_map_gap")]
    [InlineData("wrong_map_act")]
    [InlineData("wrong_map_floor")]
    [InlineData("missing_choice")]
    [InlineData("modifier")]
    [InlineData("unknown_neow")]
    [InlineData("extra_room")]
    public void MissingBoundariesOrUnreviewedModifiersDisableOnlyAcceleration(string change)
    {
        var run = new RunState("map-public-ineligible", ascensionLevel: 10);
        var evidence = Evidence(run, change == "unknown_neow" ? "GoldenCompass" : "GoldenPearl", change);
        string before = PublicJson.Serialize(evidence);
        Assert.False(NativePublicInitialMapCondition.TryCreate(evidence, Prior, out var condition, out var reason));
        Assert.Null(condition); Assert.NotNull(reason); Assert.Equal(before, PublicJson.Serialize(evidence));
        Assert.False(NativeInitialPrefixCondition.TryCreate(new("fixture", null, [], evidence), Prior, out _, out _));
    }

    [Fact]
    public void LaterGapsAndUnknownIconsCannotChangeAlreadyObservedInitialMap()
    {
        var run = new RunState("map-public-unknown", ascensionLevel: 10);
        var evidence = Evidence(run, "GoldenPearl", "later_gap");
        Assert.False(evidence.CompleteFromRunStart);
        Assert.True(NativePublicInitialMapCondition.TryCreate(evidence, Prior, out var condition, out var reason), reason);
        Assert.True(condition!.MatchesMap(run.Map));
        var current = run.Map.GetPointsInRow(1).First();
        var unknown = current.Children.First();
        unknown.PointType = Sts2Sim.Core.Map.MapPointType.Unknown;
        var slice = NativePublicMapSlice.Observe(run.Map, current, [unknown]);
        Assert.Equal(PublicMapNodeType.Unknown, slice.Nodes.Single(n => n.Coordinate.Row == 2).NodeType);
        // Later hidden topology and Unknown-room outcomes are not guessed or
        // unnecessarily made part of this first-slice rejection predicate.
        Assert.True(condition.MatchesMap(run.Map));
        var legacy = Prior with { SchemaVersion = NativeTapePrior.Version };
        Assert.False(NativePublicInitialMapCondition.TryCreate(evidence, legacy, out _, out _));
    }

    [Theory]
    [InlineData(9001UL)]
    [InlineData(9004UL)]
    public async Task RealRecordedOpeningRootsUseTypedMapWithoutFirstRewardCertificate(ulong sourceDraw)
    {
        var recipe = Prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        { Assert.NotNull(source); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe())); }
        Assert.False(NativeFirstRewardCondition.TryCreate(root, Prior, out _, out _));
        Assert.True(NativeInitialPrefixCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.NotNull(condition!.PublicMap);
        Assert.True(new NativeTapeReplaySource(root, Prior).UsesConditionalInitialPrefix);
        var plan = condition.Prepare(recipe with { RunSeed = 123, ProposalSeed = 987 }, 256);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, plan.SelectedRecipe, initialPrefixPlan: plan);
        await using var replay = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, plan.SelectedRecipe, tape);
        Assert.NotNull(replay); tape.ValidateProposalCompletion();
        var replaySlice = replay.Observe().PublicEvidence!.Events.First(e => e.Payload is PublicMapObserved).Payload;
        var rootSlice = root.PublicEvidence!.Events.First(e => e.Payload is PublicMapObserved).Payload;
        // These fixtures' initial selected relic may differ in the unconditioned
        // future, so compare invariant public geometry without option enumeration.
        Assert.Equal(PublicJson.Serialize(((PublicMapObserved)rootSlice).Nodes),
            PublicJson.Serialize(((PublicMapObserved)replaySlice).Nodes));
        Assert.Equal(PublicJson.Serialize(((PublicMapObserved)rootSlice).Edges),
            PublicJson.Serialize(((PublicMapObserved)replaySlice).Edges));
    }

    [Fact]
    public void JointFreshSeedRetriesPreserveAliasDependentFinitePosterior()
    {
        int[] emitted = new int[2]; int exhausted = 0;
        NativeComponentTrial<(int Seed, bool Match)> Candidate(int ticket)
        {
            int seed = ticket & 1;
            var words = new Queue<ulong>([(ulong)((ticket >> 1) & 1), (ulong)((ticket >> 2) & 1)]);
            return NativeComponentRejection.Evaluate(() =>
            {
                var act = new Rng(17); var map = seed == 0 ? act.CloneExact() : new Rng(18);
                ulong a = act.NextUnsignedLong(), m = map.NextUnsignedLong();
                return (seed, a == 0 && m == 0);
            }, () => words.Dequeue());
        }
        for (int first = 0; first < 8; first++)
        for (int second = 0; second < 8; second++)
        {
            int trial = 0; int[] tickets = [first, second];
            try
            {
                var selected = NativeComponentRejection.Sample("joint_seed_alias", 2,
                    () => Candidate(tickets[trial++]), c => c.Match);
                emitted[selected.Value.Seed]++;
                Assert.Equal(selected.Value.Seed == 0 ? 1 : 2, selected.Trace.Select(w => w.State).Distinct().Count());
            }
            catch (NativeComponentBudgetExceededException) { exhausted++; }
        }
        // Native matching masses 2/8 and 1/8 become 26/64 and 13/64.
        // The same root-constant p/q=8/13 applies despite seed-specific aliasing.
        Assert.Equal([26, 13], emitted); Assert.Equal(25, exhausted);
        Assert.Equal(new ShuffleRational(8, 13), new ShuffleRational(16, emitted[0]));
        Assert.Equal(new ShuffleRational(8, 13), new ShuffleRational(8, emitted[1]));
        // Holding seed through capped retries would instead produce ratio 12:7.
        Assert.NotEqual(new ShuffleRational(2, 1), new ShuffleRational(12, 7));
    }

    private static PublicRunEvidence Evidence(RunState run, string neow, string change = "")
    {
        PublicRelic Relic(string id) => new(id, new Dictionary<string, int> { ["isWax"] = 0, ["isMelted"] = 0, ["stackCount"] = 1 });
        PublicEvidenceAssets Assets(params string[] ids) => new(70, 70, 99, [], ids.Select(Relic).ToArray(), [null, null], 3, 2, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(change == "run_gap" ? null : new("Silent", 10, Assets("RingOfTheSnake")));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        long offered = recorder.Record(owner, new PublicOptionsObserved([new(neow, false),
            new(neow == "GoldenPearl" ? "WingedBoots" : "GoldenPearl", false), new("LeafyPoultice", false)]));
        recorder.Record(owner, new PublicOptionChosen(offered, neow));
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed,
            change == "modifier" ? Assets("RingOfTheSnake", neow, "GoldenCompass") : Assets("RingOfTheSnake", neow)));
        if (change == "before_map_gap") recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
        if (change == "extra_room")
        {
            long extra = recorder.BeginOwner(PublicEvidenceOwnerKind.Rest, 0, 1);
            recorder.Record(extra, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        long map = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, change == "wrong_map_act" ? 1 : 0,
            change == "wrong_map_floor" ? 2 : 1);
        var current = run.Map.StartingMapPoint;
        var choices = neow == "WingedBoots" ? run.Map.GetPointsInRow(1).ToArray() : current.Children.ToArray();
        long observed = recorder.Record(map, NativePublicMapSlice.Observe(run.Map, current, choices));
        var chosen = NativeSourceMapChoice.Choose(choices, 70, 70);
        if (change != "missing_choice") recorder.Record(map, new PublicMapChosen(observed, new(chosen.coord.col, chosen.coord.row)));
        recorder.Record(map, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        if (change == "later_gap") recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
        return recorder.Capture();
    }
}
