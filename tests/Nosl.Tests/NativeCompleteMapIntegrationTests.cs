using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeCompleteMapIntegrationTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(11001UL)]
    [InlineData(11004UL)]
    public async Task OwningMapLawGeneratesThenMarginalizesOnlyPublicGraphAndForksExactly(ulong seed)
    {
        var prior = Prior.Freeze();
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 0, DecisionIndex = 0 };
        var generatingTape = NativeLabelTape.ForDeclaredPrior(prior, recipe);
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, generatingTape))
        {
            Assert.NotNull(original);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe()));
        }
        Assert.True(generatingTape.UsesMapProvenance);
        Assert.True(generatingTape.MapCells > 0);
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, root.PublicEvidence!.SchemaVersion);
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(root.PublicEvidence, prior, out var condition, out var reason), reason);
        var source = new NativeTapeReplaySource(root, prior);
        Assert.True(source.UsesPublicMapReconstruction);
        Assert.False(source.UsesConditionalInitialPrefix);
        Assert.Contains("map-rewards-tape-marginalized", source.PosteriorProfile);
        Assert.False(new NativeTapeReplaySource(root, prior, enableConditioning: false).UsesPublicMapReconstruction);
        // Same recipe isolates native lifecycle equality; actual inference never receives this source recipe.
        var hypothetical = recipe with { ProposalSeed = recipe.ProposalSeed ^ 1739UL };
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(root, prior, out var encounters, out _));
        NativeNeowCondition.TryCreate(root, prior, out var neow, out _);
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical, expectedPublicEvidence: root.PublicEvidence,
            mapReconstructionCondition: condition, weakEncounterCondition: encounters, neowCondition: neow);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(0, tape.MapCells); Assert.Equal(1, tape.ReconstructedMaps);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(0, tape.MapCells); tape.ValidateProposalCompletion();

        var old = prior with { SchemaVersion = NativeTapePrior.RewardsVersion,
            Execution = prior.Execution with { PublicEvidenceProfile = PublicRunEvidence.Version,
                PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1 } };
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(old, hypothetical,
            mapReconstructionCondition: condition));
        var missingEvents = root.PublicEvidence.Events.Select(e => e.Payload is PublicMapObserved m
            ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal,
                new PublicMapObserved(m.Current, m.Nodes, m.Edges, m.Options, PublicCurrentMapCapture.Missing())) : e).ToImmutableArray();
        var missing = root with { PublicEvidence = new(PublicRunEvidence.CompleteMapVersion, true, missingEvents) };
        Assert.False(new NativeTapeReplaySource(missing, prior).UsesPublicMapReconstruction);
    }
    [Fact]
    public async Task BoundedNewPriorRecordKeepsV5SchemaAndPublicMapAudit()
    {
        var result = await NativeTapeReplayDataset.CollectAsync(new()
        {
            Prior = Prior, SourceDrawSeeds = [11001], CollectionId = "map-law-contract-fixture",
            WallBudgetSeconds = 30, EventPermutationMaxTrials = 4,
        }, new() { EvaluationSeeds = [401], MaxPosteriorAttempts = 1, MaxDecisions = 2 });
        using var doc = System.Text.Json.JsonDocument.Parse(PublicJson.Serialize(result));
        var report = doc.RootElement;
        Assert.False(report.GetProperty("budgetExpired").GetBoolean());
        var record = Assert.Single(report.GetProperty("records").EnumerateArray());
        Assert.Equal("nosl.native-map-rewards-tape-replay-development.v1", record.GetProperty("schema_version").GetString());
        Assert.Equal("native_map_rewards_tape_replay_development_candidate", record.GetProperty("record_kind").GetString());
        var input = record.GetProperty("public_input"); var audit = record.GetProperty("audit_only");
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, input.GetProperty("schema_version").GetString());
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, input.GetProperty("public_evidence").GetProperty("schemaVersion").GetString());
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, audit.GetProperty("public_evidence_profile").GetString());
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, audit.GetProperty("versions").GetProperty("public_evidence").GetString());
        Assert.Equal(PublicRunEvidence.CompleteMapStudentSchema, audit.GetProperty("versions").GetProperty("public_schema").GetString());
        Assert.Equal(NativeTapePrior.MapVersion, audit.GetProperty("source_prior").GetString());
        Assert.True(audit.GetProperty("public_complete_map_reconstruction_eligible").GetBoolean());
        Assert.False(audit.GetProperty("initial_prefix_conditioning_eligible").GetBoolean());
        Assert.False(audit.GetProperty("trainable").GetBoolean());
        Assert.False(audit.GetProperty("source_seed_conditioning").GetBoolean());
        var proposal = Assert.Single(audit.GetProperty("posterior_proposals").EnumerateArray());
        Assert.Equal(0, proposal.GetProperty("mapTapeCells").GetInt32());
        Assert.InRange(proposal.GetProperty("reconstructedMaps").GetInt32(), 0, 1);
        Assert.Equal(NativePublicMapReconstructionCondition.SupportContract,
            proposal.GetProperty("mapMarginalizationContract").GetString());
        Assert.False(proposal.TryGetProperty("initialPrefixCorrection", out var correction) && correction.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

}
