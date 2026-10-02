using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativeInitialPrefixConditionTests
{
    [Theory]
    [InlineData(9001UL, true)]
    [InlineData(9004UL, false)]
    public async Task JointInitialPrefixReplaysTheNativeConstructorAndRetainsLaterAliases(ulong sourceDraw, bool mapConditioned)
    {
        var prior = new NativeTapePrior
        {
            EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version),
        };
        var auxiliary = prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        DecisionPacket packet;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, auxiliary, new(auxiliary)))
        { Assert.NotNull(source); packet = source.Observe(); }
        Assert.True(NativeInitialPrefixCondition.TryCreate(packet, prior, out var condition, out var reason), reason);
        Assert.Equal(mapConditioned, condition!.MapTravel is not null);
        var plan = condition.Prepare(auxiliary, 128);
        Assert.Equal(auxiliary, plan.AuxiliaryRecipe);
        Assert.NotEqual(auxiliary.RunSeed, plan.SelectedRecipe.RunSeed);
        Assert.Equal(auxiliary.TapeSeed, plan.SelectedRecipe.TapeSeed);
        Assert.Equal(auxiliary.ProposalSeed, plan.SelectedRecipe.ProposalSeed);
        Assert.Equal(auxiliary.CombatIndex, plan.SelectedRecipe.CombatIndex);
        Assert.Equal(auxiliary.DecisionIndex, plan.SelectedRecipe.DecisionIndex);
        Assert.Equal(plan.Stats.CompletedTrials, plan.RunSeedDraws);
        Assert.True(plan.Stats.TotalWordDraws >= plan.Trace.Count);
        Assert.True(plan.Trace.Count > NativeInitialPrefixProposal.MapTraceOffset);
        Assert.Equal(mapConditioned, plan.ConditionsMapTravel);
        Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("Symbolic constant cancels")));

        ulong wrongActWord = condition.TargetActType == typeof(Sts2Sim.Core.Content.Acts.Overgrowth) ? ulong.MaxValue : 0;
        var forced = new Queue<ulong>(new[] { 123UL, wrongActWord, 12UL, 13UL, plan.SelectedRecipe.RunSeed }
            .Concat(plan.Trace.DistinctBy(word => word.State).Select(word => word.Word)));
        var twoTrials = condition.Prepare(auxiliary, 2, () => forced.Dequeue());
        Assert.Empty(forced); Assert.Equal(2, twoTrials.Stats.CompletedTrials);
        Assert.Equal(plan.SelectedRecipe, twoTrials.SelectedRecipe);
        Assert.Equal(plan.Trace, twoTrials.Trace);
        Assert.Equal(3 + plan.Trace.Count, twoTrials.Stats.TotalWordDraws);
        using var canceled = new CancellationTokenSource();
        var cancellation = Assert.Throws<OperationCanceledException>(() => condition.Prepare(auxiliary, 2,
            () => { canceled.Cancel(); return 123UL; }, canceled.Token));
        Assert.Equal(1, NativeInitialPrefixCondition.FailureRunSeedDraws(cancellation));
        var canceledStats = NativeComponentRejection.FailureStats(cancellation);
        Assert.NotNull(canceledStats); Assert.Equal(0, canceledStats.CompletedTrials); Assert.Equal(0, canceledStats.TotalWordDraws);

        var overrides = plan.Trace.GroupBy(word => word.State).ToDictionary(group => group.Key, group => group.First().Word);
        Assert.All(plan.Trace, word => Assert.Equal(overrides[word.State], word.Word));
        int ordinal = 0, boundaries = 0; RunState? constructing = null;
        RunState replay;
        using (LabelRandomScope.Enter(state =>
            {
                if (ordinal == plan.Trace.Count)
                    return overrides.TryGetValue(state, out ulong priorWord) ? priorWord : OriginalWord(state);
                Assert.Equal(plan.Trace[ordinal].State, state);
                return plan.Trace[ordinal++].Word;
            }, beginMapGeneration: context =>
            {
                boundaries++; constructing = context.Run; plan.ValidateMapBoundary(context, ordinal);
                Assert.Empty(context.Run.Players);
                return new OnDispose(() => Assert.Equal(plan.Trace.Count, ordinal));
            }))
        {
            replay = new RunState(plan.SelectedRecipe.IndependentRunSeed, ascensionLevel: 10);
            Assert.Same(replay, constructing); Assert.Equal(1, boundaries); Assert.Equal(plan.Trace.Count, ordinal);
            Assert.Equal(condition.TargetActType, replay.Act.GetType());
            Assert.True(condition.MatchesMap(replay.Map));
            if (mapConditioned) Assert.True(condition.MapTravel!.MatchesMap(replay.Map));
            // An exact later clone/recreated generator reuses the selected cell,
            // even though that cell came from initial act rather than map draws.
            var repeated = plan.Trace[0];
            Assert.Equal(repeated.Word, Primitive(repeated.State).NextULong());
        }
        // Regeneration from only the frozen trace reproduces the whole map shape.
        int replayed = 0;
        using (LabelRandomScope.Enter(state =>
            { Assert.Equal(plan.Trace[replayed].State, state); return plan.Trace[replayed++].Word; }))
        {
            var second = new RunState(plan.SelectedRecipe.IndependentRunSeed, ascensionLevel: 10);
            Assert.Equal(NativeMapTravelConditionTests.MapShape(replay.Map), NativeMapTravelConditionTests.MapShape(second.Map));
        }
        Assert.Equal(plan.Trace.Count, replayed);
        var unavailable = packet with { Observation = packet.Observation! with
            { RunContext = packet.Observation.RunContext! with { CompleteFromRunStart = false } } };
        Assert.False(NativeInitialPrefixCondition.TryCreate(unavailable, prior, out _, out _));
    }

    private static MegaRandom Primitive(LabelRandomState state) => new(new SerializableRng
    { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 });
    private static ulong OriginalWord(LabelRandomState state) => Primitive(state).NextULong();
    private sealed class OnDispose(Action action) : IDisposable { public void Dispose() => action(); }
}
