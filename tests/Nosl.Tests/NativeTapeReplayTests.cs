using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeTapeReplayTests
{
    private static NativeTapePrior Prior => new()
    { EligibleCombats = 1, EligibleDecisionsPerCombat = 1, Execution = new(MaxFloors: 1, SourceDecisionHorizon: 64) };
    private static NativeTapeRecipe Recipe => Prior.Draw(new Rng(8001, "nosl-native-tape-source-draw-v1"));

    [Fact]
    public async Task OwnedTapeForkReplaysFutureAndFixedLocalIndicesRetainAbsentMass()
    {
        var recipe = Recipe;
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, new(recipe));
        Assert.NotNull(world);
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        int decisions = 0;
        while (world.Observe().Status != "terminal_settled" && decisions < 200)
        {
            var packet = world.Observe();
            Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(packet);
            await world.StepAsync(action); await fork.StepAsync(action); decisions++;
            if (decisions == 2)
            {
                var later = recipe with { DecisionIndex = 2 };
                await using var direct = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, later, new(later));
                Assert.NotNull(direct);
                Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(direct.Observe()));
                var publicCoordinates = new NativeTapeReplaySource(direct.Observe(),
                    Prior with { EligibleDecisionsPerCombat = 4 }, enableConditioning: false);
                Assert.Equal(2, publicCoordinates.ConditionedPublicDecisionIndex);
                try { await using var sample = await publicCoordinates.SampleWorldAsync(101, 1); }
                catch (PosteriorSamplingException) { }
                Assert.All(publicCoordinates.ProposalAudit, audit => Assert.Equal(2, audit.Recipe.DecisionIndex));
            }
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));

        var missing = recipe with { DecisionIndex = 100000 };
        Assert.Null(await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, missing, new(missing)));
        var absentCombat = recipe with { CombatIndex = 1 };
        Assert.Null(await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, absentCombat, new(absentCombat)));
    }

    [Fact]
    public async Task ConditionalNativeOpeningAndContinuationRetainTheOwnedTapeAcrossReplay()
    {
        var recipe = Recipe;
        DecisionPacket packet;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); packet = source.Observe(); }
        Assert.True(NativeInitialShuffleCondition.TryCreate(packet, out var condition, out var reason), reason);
        Assert.True(NativeInitialHpCondition.TryCreate(packet, out var hpCondition, out var hpReason), hpReason);
        Assert.True(NativeNeowCondition.TryCreate(packet, Prior, out var neowCondition, out var neowReason), neowReason);
        var incomplete = new NativeLabelTape(recipe, condition, hpCondition: hpCondition, neowCondition: neowCondition);
        Assert.Null(await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution with { SourceDecisionHorizon = 1 }, recipe, incomplete));
        Assert.True(incomplete.NeowConditionApplied);
        Assert.Equal(0, incomplete.ConditionedHpCount);
        int correctionWords = 0;
        Assert.Throws<InvalidOperationException>(() => incomplete.AcceptCorrection(() => { correctionWords++; return 0; }));
        Assert.Equal(0, correctionWords);
        // A controlled same-prefix execution fixture, not an independent-posterior
        // sample: actual inference separately draws every recipe without source input.
        var proposal = recipe with { ProposalSeed = 778899 };
        var tape = new NativeLabelTape(proposal, condition, hpCondition: hpCondition, neowCondition: neowCondition);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, proposal, tape);
        Assert.NotNull(world); Assert.True(tape.ConditionApplied);
        Assert.InRange(tape.ConditionedCells, condition!.DeckCount - 1 + hpCondition!.EnemyCount + 13,
            condition.DeckCount - 1 + hpCondition.EnemyCount + 15);
        Assert.Equal(hpCondition.EnemyCount, tape.ConditionedHpCount);
        Assert.True(tape.NeowConditionApplied);
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(world.Observe()));
        var correction = new Rng(990011);
        _ = tape.AcceptCorrection(correction.NextUnsignedLong); // Density test lives in the finite exact suites.
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 200 && world.Observe().Status != "terminal_settled"; i++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));

        // An already revealed equal-state cell cannot be overwritten to force
        // the draw prefix. This is computation failure, never a retryable mismatch.
        var aliasedTape = new NativeLabelTape(proposal, condition, hpCondition: hpCondition, neowCondition: neowCondition);
        using (aliasedTape.EnterScope())
            new RunRngSet(proposal.IndependentRunSeed).Shuffle.NextInt(5);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, proposal, aliasedTape));
        Assert.Contains("earlier tape cell", failure.Message);
    }

    [Fact]
    public async Task PublicOnlySamplerFreezesInputsAndCancellationCannotDropAssignedSourceAttempts()
    {
        var recipe = Recipe;
        DecisionPacket packet;
        await using (var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, new(recipe)))
        { Assert.NotNull(world); packet = world.Observe(); }
        var clone = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet));
        var a = new NativeTapeReplaySource(packet, Prior);
        var b = new NativeTapeReplaySource(clone, Prior);
        packet.Actions[0] = new(123, "mutated"); packet.Observation!.History[0] = new("mutated", "audit must not enter");
        Assert.Equal(PublicJson.Serialize(clone), PublicJson.Serialize(a.Observe()));
        // One fixed draw exercises public-only deterministic proposals even if its
        // declared budget is inconclusive. No success quota or hidden source seed.
        async Task Sample(NativeTapeReplaySource source)
        {
            try { await using var sample = await source.SampleWorldAsync(101, 1); }
            catch (PosteriorSamplingException) { }
        }
        await Sample(a); await Sample(b);
        foreach (var proposal in a.ProposalAudit)
        {
            using var legacyAudit = System.Text.Json.JsonDocument.Parse(PublicJson.Serialize(proposal));
            Assert.False(legacyAudit.RootElement.TryGetProperty("conditionedWeakEncounters", out _));
            Assert.False(legacyAudit.RootElement.TryGetProperty("weakEncounterEnvelope", out _));
            Assert.False(legacyAudit.RootElement.TryGetProperty("eventPermutationStats", out _));
            Assert.False(legacyAudit.RootElement.TryGetProperty("eventPermutationMaxTrials", out _));
            Assert.False(legacyAudit.RootElement.TryGetProperty("conditionedPublicEvents", out _));
        }
        Assert.Equal(a.ProposalAudit.Select(x => (x.Recipe, x.Status, x.ConditionedTapeCells)),
            b.ProposalAudit.Select(x => (x.Recipe, x.Status, x.ConditionedTapeCells)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        object report = await NativeTapeReplayDataset.CollectAsync(new()
        { Prior = Prior, SourceDrawSeeds = [8001, 8002], CollectionId = "cancelled-source-proof" },
            new() { EvaluationSeeds = [101] }, cancelled.Token);
        using var doc = System.Text.Json.JsonDocument.Parse(PublicJson.Serialize(report));
        Assert.Equal(2, doc.RootElement.GetProperty("attempts").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("recordedRoots").GetInt32());
        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("eventPermutationMaxTrials", out _));
        Assert.All(doc.RootElement.GetProperty("attempts").EnumerateArray(), attempt =>
            Assert.Equal("not_executed_computation_cancelled", attempt.GetProperty("status").GetString()));
    }
}
