using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeRewardsTapeTests
{
    private static NativeTapePrior Legacy => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };
    private static NativeTapePrior Hybrid => Legacy with
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        Execution = Legacy.Execution with { PublicEvidenceProfile = PublicRunEvidence.Version },
    };

    [Fact]
    public void ExplicitLawKeepsLegacyIdentityAndRequiresEvidenceChannel()
    {
        Assert.Equal("56b2f605f9b4fa82a9c5ce8d7ce0513310c552d2bc41e08eee88a2d9eddb25c4", Legacy.Identity);
        Assert.False(Legacy.Freeze().UsesRewardsProvenance);
        Assert.True(Hybrid.Freeze().UsesRewardsProvenance);
        Assert.NotEqual(Legacy.Identity, Hybrid.Identity);
        Assert.Throws<ArgumentException>(() => (Legacy with { SchemaVersion = NativeTapePrior.RewardsVersion }).Freeze());
        Assert.DoesNotContain("usesRewardsProvenance", PublicJson.Serialize(Hybrid));
        Assert.NotEqual(NativeTapeReplayDataset.DatasetVersion, NativeTapeReplayDataset.DatasetFor(Hybrid));
        Assert.Equal(NativeTapeReplayDataset.ImplementationVersion, NativeTapeReplayDataset.ImplementationFor(Legacy));
    }

    [Fact]
    public void OracleReplayRetainsOverridesAndRejectsEarlierCellMutation()
    {
        var address = new LabelRandomAddressV1(LabelRandomProvenance.RewardsOrigin, 91, 4);
        var oracle = new NativeRewardsOracle(17);
        oracle.ForceFresh(address, 123);
        Assert.False(oracle.WasVisited(address));
        Assert.Equal(123UL, oracle.Word(address));
        Assert.True(oracle.WasVisited(address));
        Assert.Throws<InvalidOperationException>(() => oracle.ForceFresh(address, 123));
        var replay = oracle.ReplayCopy();
        replay.ForceFresh(address, 123);
        Assert.Throws<InvalidOperationException>(() => replay.ForceFresh(address, 124));
        Assert.Equal(123UL, replay.Word(address));
        var future = address with { RawCursor = 5 };
        Assert.Equal(oracle.Word(future), replay.Word(future));
        Assert.Equal(2, replay.DistinctCells);
        Assert.Equal(1, replay.ConditionedCells);
        Assert.Throws<InvalidOperationException>(() => oracle.Word(address with { OriginFamily = "unknown" }));
    }

    [Fact]
    public async Task OwnedHybridReplaysBothPartitionsAndPreservesPublicEvidenceThroughSettlement()
    {
        // A fixed lifecycle fixture, not a claim about independent posterior acceptance.
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 1, DecisionIndex = 0 };
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe, tape);
        Assert.NotNull(world);
        Assert.True(tape.UsesRewardsProvenance);
        Assert.True(tape.RewardsCells > 0);
        Assert.True(tape.DistinctCells > tape.RewardsCells);
        Assert.NotNull(world.Observe().PublicEvidence);
        var source = new NativeTapeReplaySource(world.Observe(), Hybrid);
        Assert.Contains("rewards-state-tape", source.PosteriorProfile);
        Assert.False(source.UsesConditionalFirstReward);
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
        Assert.NotNull(world.Observe().PublicEvidence);
    }

    [Fact]
    public async Task HybridOpeningActuallyAppliesStateConditioningAndReplaysIt()
    {
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 0, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe)))
        { Assert.NotNull(original); root = original.Observe(); }
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out var shuffle, out var why), why);
        Assert.True(NativeInitialHpCondition.TryCreate(root, out var hp, out why), why);
        Assert.True(NativeNeowCondition.TryCreate(root, Hybrid, out var neow, out why), why);
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe with { ProposalSeed = 88899 }, shuffle,
            hpCondition: hp, neowCondition: neow);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution,
            recipe with { ProposalSeed = 88899 }, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.ConditionApplied); Assert.True(tape.NeowConditionApplied);
        Assert.Equal(hp!.EnemyCount, tape.ConditionedHpCount);
        Assert.Equal(PublicJson.Serialize(root.Observation), PublicJson.Serialize(world.Observe().Observation));
        // Unpicked Neow options may differ, so this is kernel execution evidence,
        // not an accepted posterior world until the whole public prefix matches.
        await using var fork = await world.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
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
    }

    [Fact]
    public async Task HybridRetainsJointStatePrefixAndEncounterButRejectsOldRewardForcing()
    {
        // Public necessary predicates from the existing fixed fixture. This does
        // not migrate its old posterior labels or assert full hybrid root equality.
        var recipe = Legacy.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Legacy.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); root = source.Observe(); }
        Assert.True(NativeFirstRewardCondition.TryCreate(root, Hybrid, out var oldReward, out _));
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Hybrid, recipe,
            firstRewardCondition: oldReward));
        Assert.True(NativeInitialPrefixCondition.TryCreate(root, Hybrid, out var prefix, out _));
        Assert.True(NativeFirstEncounterCondition.TryCreate(root, Hybrid, out var encounters, out _));
        Assert.True(NativeNeowCondition.TryCreate(root, Hybrid, out var neow, out _));
        var plan = prefix!.Prepare(new(101, 202, 303, 1, 0), 64);
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, plan.SelectedRecipe, neowCondition: neow,
            firstEncounterCondition: encounters, initialPrefixPlan: plan);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, plan.SelectedRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.FirstEncounterConditionApplied); Assert.True(tape.NeowConditionApplied);
        Assert.True(tape.RewardsCells > 0); Assert.False(tape.FirstRewardConditionApplied);
        await using var fork = await world.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        var rng = new Rng(1234); _ = tape.AcceptCorrection(rng.NextUnsignedLong);
    }

    [Fact]
    public async Task PublicRewardAndNeowConditioningReplayTheWholeNativePrefix()
    {
        // Fixed native integration fixture. Independent inference does not receive
        // this source recipe and still requires whole-packet equality plus correction.
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 1, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe)))
        { Assert.NotNull(original); root = original.Observe(); }
        var rewards = NativePublicRewardCondition.Create(root.PublicEvidence!);
        Assert.Single(rewards.Targets);
        var resources = NativePublicRewardResourceCondition.Create(root.PublicEvidence!, rewards);
        Assert.NotNull(resources.Targets[0].GoldCertificate.RoomType); // Exercise real direct-map guards.
        Assert.True(resources.Targets[0].GoldCertificate.Envelope.Numerator < resources.Targets[0].GoldCertificate.Envelope.Denominator);
        Assert.True(NativeNeowCondition.TryCreate(root, Hybrid, out var neow, out var why), why);
        Assert.Equal(2, neow!.PositivePrefixIds.Count);
        var proposed = recipe with { ProposalSeed = 223344 };
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, proposed, neowCondition: neow, publicRewardCondition: rewards,
            expectedPublicEvidence: root.PublicEvidence, publicResourceCondition: resources);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, proposed, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(3, tape.ConditionedPublicRewardCards);
        Assert.Equal(1, tape.ConditionedResourcePresence); Assert.Equal(1, tape.ConditionedResourceGold);
        Assert.Equal(resources.Targets[0].Potion is null ? 0 : 1, tape.ConditionedResourcePotions);
        Assert.True(tape.PublicResourceRatio!.Value.Numerator * tape.PublicResourceEnvelope!.Value.Denominator
            <= tape.PublicResourceEnvelope.Value.Numerator * tape.PublicResourceRatio.Value.Denominator);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(root.PublicEvidence!.Events.Length, tape.PublicPrefixEventsChecked);
        var ratio = tape.PublicRewardRatio!.Value; var envelope = tape.PublicRewardEnvelope!.Value;
        Assert.True(ratio.Numerator > 0);
        Assert.True(ratio.Numerator * envelope.Denominator <= envelope.Numerator * ratio.Denominator);
        var random = new Rng(4545); _ = tape.AcceptCorrection(random.NextUnsignedLong);
        await using var fork = await world.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        var observedCounts = (tape.ConditionedResourcePresence, tape.ConditionedResourceGold,
            tape.ConditionedResourcePotions, tape.ConditionedPublicRewardCards);
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
        Assert.Equal(observedCounts, (tape.ConditionedResourcePresence, tape.ConditionedResourceGold,
            tape.ConditionedResourcePotions, tape.ConditionedPublicRewardCards)); // No unseen future rewards conditioned.

    }

    [Fact]
    public async Task BoundedHybridCollectionRetainsExplicitLawQuarantineAndAttemptedMass()
    {
        var result = await NativeTapeReplayDataset.CollectAsync(new()
        { Prior = Hybrid, SourceDrawSeeds = [9001], CollectionId = "hybrid-contract-fixture", WallBudgetSeconds = 30 },
            new() { EvaluationSeeds = [401], MaxPosteriorAttempts = 1, MaxDecisions = 2 });
        using var json = JsonDocument.Parse(PublicJson.Serialize(result));
        var report = json.RootElement;
        Assert.False(report.GetProperty("trainable").GetBoolean());
        Assert.False(report.GetProperty("formalTraining").GetBoolean());
        Assert.Equal(256, report.GetProperty("options").GetProperty("eventPermutationMaxTrials").GetInt32());
        Assert.False(report.GetProperty("budgetExpired").GetBoolean());
        Assert.Equal(Hybrid.Identity, report.GetProperty("priorIdentity").GetString());
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        Assert.Equal("native_rewards_tape_replay_development_candidate", record.GetProperty("record_kind").GetString());
        Assert.Equal(NativeTapeReplayDataset.DatasetFor(Hybrid), record.GetProperty("schema_version").GetString());
        Assert.Equal(PublicRunEvidence.StudentSchema, record.GetProperty("public_input").GetProperty("schema_version").GetString());
        var audit = record.GetProperty("audit_only");
        Assert.Equal(NativeTapePrior.RewardsVersion, audit.GetProperty("source_prior").GetString());
        Assert.False(audit.GetProperty("source_seed_conditioning").GetBoolean());
        Assert.False(audit.GetProperty("trainable").GetBoolean());
        Assert.Single(audit.GetProperty("posterior_proposals").EnumerateArray());
        Assert.True(audit.GetProperty("public_weak_encounter_sequence_eligible").GetBoolean());
        Assert.InRange(audit.GetProperty("public_weak_encounter_targets").GetInt32(), 1, 3);
        Assert.InRange(audit.GetProperty("public_weak_encounter_prefix_length").GetInt32(), 1, 3);
        Assert.All(audit.GetProperty("posterior_proposals").EnumerateArray(), proposal =>
        {
            Assert.InRange(proposal.GetProperty("conditionedWeakEncounters").GetInt32(), 0, 3);
            Assert.False(string.IsNullOrWhiteSpace(proposal.GetProperty("weakEncounterEnvelope").GetString()));
        });
        foreach (var action in record.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.Equal(1, action.GetProperty("allocated_worlds").GetInt32());
            Assert.Equal(0, action.GetProperty("error_worlds").GetInt32());
            Assert.Equal(1, new[] { "completed_worlds", "truncated_worlds", "error_worlds", "other_worlds" }
                .Sum(key => action.GetProperty(key).GetInt32()));
            if (action.GetProperty("completed_worlds").GetInt32() == 0)
                Assert.All(action.GetProperty("masks").EnumerateObject(), mask => Assert.False(mask.Value.GetBoolean()));
        }
        Assert.All(audit.GetProperty("posterior_proposals").EnumerateArray(), proposal =>
            Assert.True(proposal.GetProperty("rewardsTapeCells").GetInt32() >= 0));
    }
}
