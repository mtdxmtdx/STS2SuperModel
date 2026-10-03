using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePublicActPrefixReplayTests
{
    private static NativeTapePrior Prior => NativePublicMapReconstructionTests.Prior;
    private sealed record Fixture(DecisionPacket Root, NativeTapeRecipe Recipe,
        NativePublicActPrefixCondition Act, NativePublicMapReconstructionCondition Map, ulong[] EventWords);

    private static async Task<Fixture> Load(ulong seed = 24002)
    {
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var capture = NativeLabelTape.ForDeclaredPrior(Prior, recipe);
        ulong[]? words = null;
        using var observe = LabelEventGenerationScope.Enter(context =>
        {
            if (context.ActIndex != 0) return null;
            words = new ulong[context.Pool.Count - 1];
            var clone = new Rng(context.Rng.ToSerializable());
            for (int i = 0; i < words.Length; i++)
            {
                var state = clone.ToSerializable();
                words[i] = Word(capture)(new(state.state0, state.state1, state.state2, state.state3));
                clone.NextUnsignedLong();
            }
            return null;
        });
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
        Assert.True(NativePublicActPrefixCondition.TryCreate(root, Prior, out var act, out var reason), reason);
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(root.PublicEvidence, Prior, out var map, out reason), reason);
        return new(root, recipe, act!, map!, words!);
    }

    [Theory]
    [InlineData(24002UL)]
    [InlineData(24007UL)]
    [InlineData(24008UL)]
    public async Task NativeIdentityRootAndOwnedContinuationStayExact(ulong seed)
    {
        var fixture = await Load(seed);
        // Source tape reads here are test-only identity replay, never production proposal input.
        var original = NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe);
        var actWords = new List<ulong>();
        using (LabelRandomScope.Enter(state =>
        {
            ulong word = Word(original)(state); actWords.Add(word); return word;
        })) _ = ActDefinition.GetRandomList(fixture.Recipe.IndependentRunSeed);
        var queue = new Queue<ulong>(actWords);
        var plan = fixture.Act.Prepare(fixture.Recipe, 1, queue.Dequeue);
        Assert.Empty(queue);
        NativePublicEventPermutationCondition.TryCreate(fixture.Root, Prior, out var events, out _);
        if (seed == 24002) Assert.NotNull(events);
        var eventPlan = events is null ? null : new NativeEventPermutationPlan(fixture.EventWords,
            new(1, fixture.EventWords.Length, fixture.EventWords.Length, fixture.EventWords.Length), 1);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe,
            mapReconstructionCondition: fixture.Map, publicActPrefixPlan: plan,
            eventPermutationCondition: events, eventPermutationPlan: eventPlan);
        await using var replay = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, fixture.Recipe, tape);
        Assert.NotNull(replay); tape.ValidateProposalCompletion();
        Assert.Equal(PublicJson.Serialize(fixture.Root), PublicJson.Serialize(replay.Observe()));
        Assert.Equal(0, tape.MapCells); Assert.Equal(3, tape.ConditionedPublicActWords);
        if (events is not null) Assert.Equal(1, tape.ConditionedEventPermutations);
        Assert.True(tape.AcceptCorrection(() => throw new Exception("Constant corrections must not draw")));
        await using var fork = await replay.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && replay.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(replay.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(replay.Observe()); await replay.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", replay.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await replay.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
    }

    [Fact]
    public async Task ActualEventPermutationRejectsAnUpFrontAliasOfAnActCell()
    {
        var fixture = await Load();
        Assert.True(NativePublicEventPermutationCondition.TryCreate(fixture.Root, Prior, out var condition, out _));
        var actPlan = fixture.Act.Prepare(fixture.Recipe, 64);
        var eventPlan = condition!.Prepare(fixture.Recipe, 4096);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe,
            mapReconstructionCondition: fixture.Map, publicActPrefixPlan: actPlan);
        RunState run;
        using (tape.EnterScope())
        {
            run = new RunState(fixture.Recipe.IndependentRunSeed, 10);
            tape.AttachHypotheticalRun(run);
            run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
            run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        }
        var state = actPlan.Trace[0].State;
        var snapshot = run.Rng.UpFront.ToSerializable();
        snapshot.state0 = state.State0; snapshot.state1 = state.State1;
        snapshot.state2 = state.State2; snapshot.state3 = state.State3;
        run.Rng.UpFront.LoadFromSerializable(snapshot);
        var force = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
        var proposal = new NativePublicEventPermutationProposal(condition, eventPlan, force);
        proposal.AttachHypotheticalRun(run);
        using (proposal.BeginGeneration(new(run, run.Act, 0, run.Rng.UpFront, condition.Pool)))
            Assert.Throws<InvalidOperationException>(() => run.Rng.UpFront.NextUnsignedLong());
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Equal(actPlan.Trace[0].Word, Word(tape)(state));
        Assert.Throws<InvalidOperationException>(tape.RequireSuccessfulConditionedWords);
    }

    [Fact]
    public async Task SourceEnablesOnlyMapPathAndPublicPacketContainsNoActTrace()
    {
        var fixture = await Load();
        var source = new NativeTapeReplaySource(fixture.Root, Prior);
        Assert.True(source.UsesConditionalPublicActPrefix); Assert.False(source.UsesConditionalInitialPrefix);
        Assert.Equal(PublicJson.Serialize(fixture.Root), PublicJson.Serialize(source.Observe()));
        Assert.False(new NativeTapeReplaySource(fixture.Root, Prior, enableConditioning: false).UsesConditionalPublicActPrefix);
        var evidence = fixture.Root.PublicEvidence!;
        var entries = evidence.Events.Select(e => e.Payload is PublicMapObserved map
            ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, new PublicMapObserved(map.Current, map.Nodes, map.Edges, map.Options)) : e).ToImmutableArray();
        var legacy = Prior with { SchemaVersion = NativeTapePrior.RewardsVersion, Execution = Prior.Execution with
            { PublicEvidenceProfile = PublicRunEvidence.Version, PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1 } };
        var root = fixture.Root with { PublicEvidence = new(PublicRunEvidence.Version, true, entries) };
        var oldSource = new NativeTapeReplaySource(root, legacy);
        Assert.False(oldSource.UsesConditionalPublicActPrefix); Assert.True(oldSource.UsesConditionalInitialPrefix);
    }

    [Fact]
    public async Task CleanActExhaustionConsumesOuterAttemptsAndUsesFreshUnchangedRecipes()
    {
        var fixture = await Load();
        // Select deterministic auxiliary coins only, without native worlds or outcome-dependent source retries.
        ulong outerSeed = Enumerable.Range(0, 64).Select(i => (ulong)i).First(seed =>
        {
            var random = new Rng(seed, "nosl-native-tape-independent-proposals-v1");
            for (int i = 0; i < 2; i++)
            {
                var recipe = Prior.Draw(random);
                try { _ = fixture.Act.Prepare(recipe, 1); return false; }
                catch (NativeComponentBudgetExceededException) { }
            }
            return true;
        });
        int opened = 0;
        var source = new NativeTapeReplaySource(fixture.Root, Prior, initialPrefixMaxTrials: 1,
            nativeOpenerForTests: (_, _, _, _) => { opened++; throw new Exception("Exhausted act must not open a run"); });
        var error = await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(outerSeed, 2));
        Assert.Equal(2, error.Attempts); Assert.Equal(0, opened);
        Assert.Equal(2, source.ProposalAudit.Length);
        var expected = new Rng(outerSeed, "nosl-native-tape-independent-proposals-v1");
        foreach (var audit in source.ProposalAudit)
        {
            Assert.Equal(source.DrawConditionedRecipe(expected), audit.Recipe);
            Assert.Equal("component_budget_exhausted", audit.Status);
            Assert.Equal(new NativeComponentStats(1, 3, 3, 3), audit.PublicActPrefixStats);
            Assert.Equal(1, audit.PublicActPrefixMaxTrials); Assert.Equal("1/2", audit.PublicActPrefixNullMass);
            Assert.Null(audit.PublicActPrefixCorrection); Assert.Null(audit.InitialPrefixStats);
            Assert.Null(audit.EventPermutationStats); Assert.Null(audit.AuxiliaryRecipe);
            Assert.Equal(0, audit.ConditionedTapeCells); Assert.Equal(0, audit.ConditionedPublicActWords);
        }
        Assert.NotEqual(source.ProposalAudit[0].Recipe, source.ProposalAudit[1].Recipe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwallowedActCallbackFailureCannotHideBehindAbsenceOrMismatch(bool mismatch)
    {
        var fixture = await Load(); int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe recipe, NativeLabelTape tape, CancellationToken cancel)
        {
            opened++;
            Assert.Throws<InvalidOperationException>(() => Word(tape)(new(0, 0, 0, 0)));
            if (mismatch) throw new NativePublicConstraintMismatchException("later public mismatch");
            return Task.FromResult<NativeRunWorld?>(null);
        }
        var source = new NativeTapeReplaySource(fixture.Root, Prior, initialPrefixMaxTrials: 256,
            eventPermutationMaxTrials: 4096, nativeOpenerForTests: Open);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.SampleWorldAsync(51, 2));
        Assert.Equal(1, opened);
        var audit = source.ProposalAudit[^1];
        Assert.Equal("proposal_engine_error", audit.Status); Assert.NotNull(audit.PublicActPrefixStats);
        Assert.NotNull(audit.PublicActPrefixCorrection); Assert.Equal(0, audit.ConditionedPublicActWords);
        string serialized = PublicJson.Serialize(audit);
        Assert.DoesNotContain("state0", serialized); Assert.DoesNotContain("\"trace\":", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
}
