using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;

namespace Nosl.Tests;

public sealed class NativeConstructedLanternOwnerTests
{
    // Captured from the unmodified ordinary v1 implementation before the owner
    // extension. These are literal bytes, not an expectation rebuilt by its code.
    private const string PriorV1Json = """
        {"schemaVersion":"nosl.constructed-native-map-rewards-state-tape-prior.v1","setup":{"encounter":"SludgeSpinnerWeak","deck":["StrikeSilent","DefendSilent\u002B"],"potions":["FirePotion"],"relics":["Anchor"],"hp":41,"maxHp":70,"gold":77},"sourcePolicyId":"nosl-public-rules-v2","sourceDecisionHorizon":16,"rootSelection":"opening","decisionIndex":0,"primitiveLaw":"independent-map-act-seed-raw-cursor-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1","primitiveImplementation":"sha256-address-expansion-with-explicit-conditioned-overrides-v1","rootLaw":"one-declared-native-combat-fixed-public-stopping-rule-with-absence-v1","setupLaw":"fixed-acts-base-deck-hp-maxhp-gold-potions-then-native-relic-acquisition-v1"}
        """;
    private const string SourceGenerationV1Json = """
        {"schema_version":"nosl.native-constructed-tape.source-generation.v1","source_prior_schema":"nosl.constructed-native-map-rewards-state-tape-prior.v1","setup":{"encounter":"SludgeSpinnerWeak","deck":["StrikeSilent","DefendSilent\u002B"],"potions":["FirePotion"],"relics":["Anchor"],"hp":41,"maxHp":70,"gold":77},"setup_law":"fixed-acts-base-deck-hp-maxhp-gold-potions-then-native-relic-acquisition-v1","source_policy":"nosl-public-rules-v2","source_script":"nosl-natural-public-script-v3","primitive_law":"independent-map-act-seed-raw-cursor-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1","primitive_implementation":"sha256-address-expansion-with-explicit-conditioned-overrides-v1","character":"Silent","ascension":10,"simulator":"5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0","random_domains":{"native_run_seed":"NOSL-NATIVE-TAPE-V1:{RunSeed:X16}","native_state_words":"sha256-u64le:4E4F534C54415031,TapeSeed,State0,State1,State2,State3","rewards_words":"sha256-u64le:4E4F534C52574431,TapeSeed,InitialSeed,RawCursor","map_words":"sha256-u64le:4E4F534C4D415031,TapeSeed,ActIndex,InitialSeed,RawCursor"}}
        """;
    private const string PriorV1Identity = "5399744178768a1744fd4fc88f7add52978d493907b9732393bf2bbaf2488599";
    private const string SourceGenerationV1Identity = "6a3226e788339fa1f7a4411f9bfec6b8bcfa3c71717b40a1c71e85a62d34e0d9";
    private const string FamilyV1Alias = "constructed-native-tape-random-family-v1:8800c6577234078d11184d3a0d531024183b246cf95d372e6ae6f28c2c1049c9";
    private const string BattleV1Alias = "constructed-native-tape-configured-source-v1:ea26ca2bc7eca1232f7fdbc4acda45260a57ef1233b4bcce6549730e0ee1143e/combat:0";
    private static NativeTapeRecipe Recipe => new(0x0123456789abcdefUL,
        0x1020304050607080UL, 0x8877665544332211UL, 0, 0);

    private static NativeConstructedTapePrior OrdinaryPrior => new()
    {
        Setup = new()
        {
            Encounter = "SludgeSpinnerWeak", Deck = ["StrikeSilent", "DefendSilent+"],
            Potions = ["FirePotion"], Relics = ["Anchor"], Hp = 41, MaxHp = 70, Gold = 77,
        },
        SourceDecisionHorizon = 16,
    };

    private static NativeConstructedTapePrior OwnerPrior(bool victory = true) => new()
    {
        SchemaVersion = NativeConstructedTapePrior.EventOwnerVersion,
        EventOwner = new()
        {
            Event = "TheLanternKey", Act = "Hive", FixtureFloor = 1,
            ChoiceRule = "keep-the-key-then-fight-v1",
        },
        // Tiny explicit engineering fixtures make bounded ordinary rejection
        // replay demonstrable; they are not natural reachability/breadth evidence.
        Setup = new()
        {
            Encounter = "MysteriousKnightEventEncounter",
            Deck = victory ? [nameof(GrandFinale), nameof(GrandFinale), nameof(GrandFinale)] : [nameof(DefendSilent)],
            Hp = victory ? 70 : 1, MaxHp = 70, Gold = 77,
        },
        SourceDecisionHorizon = 16,
    };

    private static NativeConstructedTapeDataset.CollectionContract Contract(NativeConstructedTapePrior prior)
    {
        var receipt = JsonNode.Parse(PublicJson.Serialize(NativeCompleteMapBuildReceipt.RuntimeIdentity()))!.AsObject();
        receipt["wrapper_source_sha256"] = new string('a', 64);
        receipt["vendor_source_sha256"] = new string('b', 64);
        return NativeConstructedTapeDataset.CollectionContract.Create(new()
        {
            Prior = prior, CollectionId = "lantern-v1-compatibility-fixture", SourceDrawSeeds = [18001], WallBudgetSeconds = 30,
        }, new()
        {
            Mode = "T0", ContinuationPolicyId = PublicContinuationPolicies.ReviewedId,
            EvaluationSeeds = [701], MaxPosteriorAttempts = 1, MaxDecisions = 1,
        }, NativeConstructedTapeDataset.Purpose, receipt.ToJsonString());
    }

    [Fact]
    public void OrdinaryV1RetainsCapturedBytesIdentityAndPrimitiveFamily()
    {
        var contract = Contract(OrdinaryPrior);
        Assert.Equal(PriorV1Json, PublicJson.Serialize(contract.Options.Prior));
        Assert.Equal(PriorV1Identity, contract.Options.Prior.Identity);
        Assert.Equal(SourceGenerationV1Json, contract.SourceGenerationJson);
        Assert.Equal(SourceGenerationV1Identity, contract.SourceGenerationIdentity);
        Assert.Equal("nosl-constructed-native-tape-conditional-v1-public-evidence-v2",
            JsonNode.Parse(contract.Json)!["sampler_version"]!.GetValue<string>());
        var audit = contract.Provenance(Recipe)["audit_only"]!;
        Assert.Equal(FamilyV1Alias, audit["source_random_family_alias"]!.GetValue<string>());
        Assert.Equal(FamilyV1Alias, audit["source_run_group"]!.GetValue<string>());
        Assert.Equal(BattleV1Alias, audit["underlying_battle_alias"]!.GetValue<string>());
    }

    [Fact]
    public void OwnerContractIsExplicitFrozenAndSharesOnlyThePrimitiveFamilyWithV1()
    {
        var prior = OwnerPrior();
        var contract = Contract(prior);
        string identity = contract.Options.Prior.Identity;
        prior.Setup.Deck![0] = "external mutation";
        Assert.Equal(identity, contract.Options.Prior.Identity);
        Assert.Equal(nameof(GrandFinale), contract.Options.Prior.Setup.Deck![0]);
        Assert.Equal("nosl.constructed-native-event-owner-map-rewards-state-tape-prior.v1",
            contract.Options.Prior.SchemaVersion);
        Assert.Contains("TheLanternKey", contract.SourceGenerationJson);
        Assert.Contains("keep-the-key-then-fight-v1", contract.SourceGenerationJson);
        Assert.Equal("nosl-constructed-native-event-tape-rejection-v1-public-evidence-v2",
            JsonNode.Parse(contract.Json)!["sampler_version"]!.GetValue<string>());
        Assert.NotEqual(SourceGenerationV1Identity, contract.SourceGenerationIdentity);
        Assert.Equal(FamilyV1Alias, contract.Provenance(Recipe)["audit_only"]!["source_random_family_alias"]!.GetValue<string>());
        Assert.NotEqual(BattleV1Alias, contract.Provenance(Recipe)["audit_only"]!["underlying_battle_alias"]!.GetValue<string>());
        var later = Contract(contract.Options.Prior with { RootSelection = "first_player_turn_2", SourceDecisionHorizon = 32 });
        Assert.NotEqual(identity, later.Options.Prior.Identity);
        Assert.Equal(contract.SourceGenerationJson, later.SourceGenerationJson);
        Assert.Equal(contract.SourceGenerationIdentity, later.SourceGenerationIdentity);
        Assert.Equal(PublicJson.Serialize(contract.Provenance(Recipe)), PublicJson.Serialize(contract.Provenance(Recipe with { ProposalSeed = 1 })));
        var differentFloor = Contract(contract.Options.Prior with
        {
            EventOwner = contract.Options.Prior.EventOwner! with { FixtureFloor = 2 },
        });
        Assert.NotEqual(contract.SourceGenerationIdentity, differentFloor.SourceGenerationIdentity);
        Assert.Equal(FamilyV1Alias, differentFloor.Provenance(Recipe)["audit_only"]!["source_random_family_alias"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("missing_owner")]
    [InlineData("legacy_schema")]
    [InlineData("unknown_schema")]
    [InlineData("wrong_event")]
    [InlineData("wrong_act")]
    [InlineData("wrong_floor")]
    [InlineData("wrong_rule")]
    [InlineData("wrong_encounter")]
    public void OwnerExtensionRejectsAmbiguousOrMismatchedDeclarations(string mutation)
    {
        var prior = OwnerPrior();
        prior = mutation switch
        {
            "missing_owner" => prior with { EventOwner = null },
            "legacy_schema" => prior with { SchemaVersion = NativeConstructedTapePrior.Version },
            "unknown_schema" => prior with { SchemaVersion = "undeclared-owner-v2" },
            "wrong_event" => prior with { EventOwner = prior.EventOwner! with { Event = "PunchOff" } },
            "wrong_act" => prior with { EventOwner = prior.EventOwner! with { Act = "Underdocks" } },
            "wrong_floor" => prior with { EventOwner = prior.EventOwner! with { FixtureFloor = 0 } },
            "wrong_rule" => prior with { EventOwner = prior.EventOwner! with { ChoiceRule = "return-the-key" } },
            "wrong_encounter" => prior with { Setup = prior.Setup with { Encounter = "SludgeSpinnerWeak" } },
            _ => throw new ArgumentException("Unknown test mutation"),
        };
        var failure = Record.Exception(() => prior.Freeze());
        Assert.True(failure is ArgumentException or NotSupportedException, failure?.ToString() ?? "Invalid owner declaration was accepted");
    }

    [Fact]
    public async Task StrictOwnerProtocolBindsGenerationAndSamplerMetadataAfterSourceDisposal()
    {
        var prior = OwnerPrior();
        var contract = Contract(prior);
        bool disposed = false, samplerCreated = false;
        var hooks = new NativeConstructedTapeDataset.TestHooks(
            AfterSourceDisposed: () => disposed = true,
            WrapPosteriorSource: source =>
            {
                Assert.True(disposed);
                samplerCreated = true;
                return source;
            });
        var report = await Collect(Request(contract), hooks);
        Assert.True(disposed);
        Assert.True(samplerCreated);
        Assert.Equal(1, report["source_worlds_returned"]!.GetValue<int>());
        Assert.Equal(1, report["source_worlds_disposed"]!.GetValue<int>());
        Assert.Equal(1, report["sampled_worlds_returned"]!.GetValue<int>());
        Assert.False(report["trainable"]!.GetValue<bool>());
        Assert.False(report["formal_labels"]!.GetValue<bool>());
        string generationJson = report["source_generation_json"]!.GetValue<string>();
        Assert.Equal(contract.SourceGenerationJson, generationJson);
        Assert.Equal(prior.EventOwner, PublicJson.Read<NativeConstructedEventOwnerSetup>(
            JsonNode.Parse(generationJson)!["event_owner"]!.ToJsonString()));
        var attempt = Assert.Single(report["attempts"]!.AsArray())!;
        Assert.True(attempt["source_disposed"]!.GetValue<bool>());
        var recipe = PublicJson.Read<NativeTapeRecipe>(attempt["recipe"]!.ToJsonString());
        var record = Assert.Single(report["records"]!.AsArray())!;
        var audit = record["audit_only"]!;
        string family = Contract(OrdinaryPrior).Provenance(recipe)["audit_only"]!["source_random_family_alias"]!.GetValue<string>();
        Assert.Equal(family, audit["source_random_family_alias"]!.GetValue<string>());
        Assert.Equal(family, audit["source_run_group"]!.GetValue<string>());
        Assert.Equal(contract.SourceGenerationIdentity, audit["source_generation_identity"]!.GetValue<string>());
        Assert.Equal(generationJson, audit["source_generation_json"]!.GetValue<string>());
        Assert.Equal(NativeConstructedTapePrior.EventOwnerVersion, audit["declared_prior"]!["schemaVersion"]!.GetValue<string>());
        const string sampler = "nosl-constructed-native-event-tape-rejection-v1-public-evidence-v2";
        const string profile = "owned-constructed-native-event-tape-conditional-v1-public-evidence-v2";
        Assert.Equal(sampler, JsonNode.Parse(report["collection_contract_json"]!.GetValue<string>())!["sampler_version"]!.GetValue<string>());
        Assert.Equal(sampler, audit["sampler_version"]!.GetValue<string>());
        Assert.Equal(sampler, audit["posterior_implementation"]!.GetValue<string>());
        Assert.Equal(sampler, audit["versions"]!["sampler"]!.GetValue<string>());
        Assert.Equal(sampler, audit["versions"]!["posterior_implementation"]!.GetValue<string>());
        Assert.Equal(profile, audit["posterior_profile"]!.GetValue<string>());
        Assert.Equal(profile, audit["versions"]!["posterior_profile"]!.GetValue<string>());
        Assert.False(audit["conditioning_eligible"]!.GetValue<bool>());
        Assert.Equal("declared_event_owner_ordinary_tape_rejection_v1", audit["conditioning_reason"]!.GetValue<string>());

        foreach (string mutation in new[] { "legacy_owner", "legacy_null_owner", "unknown_owner_key" })
        {
            var malformed = Request(contract);
            var inputPrior = malformed["options"]!["prior"]!;
            if (mutation == "unknown_owner_key") inputPrior["eventOwner"]!["sourceSeed"] = "unapproved-private-input";
            else
            {
                inputPrior["schemaVersion"] = NativeConstructedTapePrior.Version;
                if (mutation == "legacy_null_owner") inputPrior["eventOwner"] = null;
            }
            disposed = false;
            await Assert.ThrowsAsync<ArgumentException>(() => Collect(malformed, hooks));
            Assert.False(disposed);
        }
    }

    [Fact]
    public async Task StructurallyValidPublicChoiceAndParentTamperingCannotDescribeTheDeclaredOwner()
    {
        var prior = OwnerPrior();
        var packet = await DetachedRoot(prior);
        foreach (bool parentless in new[] { false, true })
        {
            var events = packet.PublicEvidence!.Events.Select(item =>
            {
                PublicEvidencePayload payload = item.Payload;
                if (!parentless && payload is PublicOptionChosen { Key: "KEEP_THE_KEY" } chosen)
                    payload = new PublicOptionChosen(chosen.OfferEventOrdinal, "RETURN_THE_KEY");
                if (parentless && payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat } combat)
                    payload = new PublicOwnerStarted(combat.OwnerKind, combat.ActIndex, combat.Floor, null, combat.CompleteFromOwnerStart);
                return new PublicRunEvidenceEvent(item.EventOrdinal, item.OwnerOrdinal, payload);
            }).ToImmutableArray();
            var tampered = packet with { PublicEvidence = new(PublicRunEvidence.CompleteMapVersion, false, events) };
            PublicEvidenceInput.Validate(tampered);
            Assert.Throws<ArgumentException>(() => new NativeConstructedTapeSource(tampered, prior));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetachedPublicOwnerRootUsesOrdinaryReplayAndIndependentNativeForks(bool victory)
    {
        var prior = OwnerPrior(victory);
        var packet = await DetachedRoot(prior);
        AssertOwnerEvidence(packet);
        var source = new NativeConstructedTapeSource(packet, prior);
        var detachedSource = new NativeConstructedTapeSource(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet)), prior);
        Assert.False(source.UsesPrimitiveConditioning);
        Assert.False(detachedSource.UsesPrimitiveConditioning);
        Assert.Equal("owned-constructed-native-event-tape-conditional-v1-public-evidence-v2", source.PosteriorProfile);
        Assert.Equal("declared_event_owner_ordinary_tape_rejection_v1", source.ConditioningReason);
        await using var world = Assert.IsType<NativeRunWorld>(await source.SampleWorldAsync(101, 1));
        await using var repeated = Assert.IsType<NativeRunWorld>(await detachedSource.SampleWorldAsync(101, 1));
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(world.Observe()));
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(repeated.Observe()));
        Assert.NotEqual(Recipe, Assert.Single(source.ProposalAudit).Recipe);
        Assert.Equal(Assert.Single(source.ProposalAudit).Recipe, Assert.Single(detachedSource.ProposalAudit).Recipe);
        AssertOrdinaryAudit(Assert.Single(source.ProposalAudit), "accepted");
        Assert.IsType<Hive>(world.NativeRun.Acts[1]);
        Assert.Equal(1, world.NativeRun.CurrentActIndex);
        Assert.Equal(1, world.NativeRun.TotalFloor);
        Assert.Equal("MysteriousKnightEventEncounter", world.Encounter);
        var ownerRoom = Assert.IsType<EventRoom>(world.NativeRun.BaseRoom);
        var owner = Assert.IsType<TheLanternKey>(ownerRoom.Event);
        Assert.True(owner.IsAwaitingForcedCombat);
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        var forkOwner = Assert.IsType<TheLanternKey>(Assert.IsType<EventRoom>(fork.NativeRun.BaseRoom).Event);
        Assert.NotSame(world.NativeRun, fork.NativeRun);
        Assert.NotSame(world.NativeCombatRoom, fork.NativeCombatRoom);
        Assert.NotSame(owner, forkOwner);
        Assert.NotSame(owner.ForcedCombatExtraRewards.Single(), forkOwner.ForcedCombatExtraRewards.Single());
        string unchanged = PublicJson.Serialize(world.Observe());
        var forkOutcome = await Finish(fork, endTurnsOnly: !victory);
        Assert.Equal(unchanged, PublicJson.Serialize(world.Observe()));
        var outcome = await Finish(world, endTurnsOnly: !victory);
        Assert.Equal(PublicJson.Serialize(forkOutcome), PublicJson.Serialize(outcome));
        Assert.Equal(victory ? TerminalKind.Win : TerminalKind.Loss, outcome.TerminalKind);
        Assert.True(outcome.SettlementComplete);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.True(outcome.ResourceProvenanceComplete);
        Assert.DoesNotContain(world.SourceTrace, item => item.Kind is "map_choice" or "reward_choice");
        Assert.DoesNotContain(world.NativeRun.Players.Single().Deck.Cards, card => card is LanternKey);
        Assert.Null(world.NativeRun.CurrentRoom);
        Assert.Null(fork.NativeRun.CurrentRoom);
        Assert.Equal(0, world.NativeRun.CurrentRoomCount);
        Assert.Null(world.NativeRun.Players.Single().PlayerCombatState);
        if (victory)
        {
            var extra = Assert.IsType<SpecialCardReward>(Assert.Single(world.NativeCombatRoom.GeneratedRewards.SelectMany(set => set.ExtraRewards)));
            Assert.IsType<LanternKey>(extra.Card);
            Assert.False(extra.IsResolved);
            Assert.Same(world.NativeRun.Players.Single(), extra.Card.Owner);
            var forkExtra = Assert.IsType<SpecialCardReward>(Assert.Single(fork.NativeCombatRoom.GeneratedRewards.SelectMany(set => set.ExtraRewards)));
            Assert.NotSame(extra, forkExtra);
            Assert.NotSame(extra.Card, forkExtra.Card);
            Assert.Same(fork.NativeRun.Players.Single(), forkExtra.Card.Owner);
            Assert.NotEmpty(world.StandardRewardOpportunities);
            Assert.Contains(outcome.PermanentChanges, change => change.Kind == "earned_extra_reward_opportunity:SpecialCardReward" && change.Amount == 1);
        }
        else
        {
            Assert.True(owner.IsFinished);
            Assert.False(owner.IsAwaitingForcedCombat);
            Assert.Empty(owner.ForcedCombatExtraRewards);
            Assert.Empty(world.NativeCombatRoom.GeneratedRewards);
            Assert.Empty(world.StandardRewardOpportunities);
            Assert.Null(world.NativeRun.Players.Single().PlayerCombatState);
            Assert.DoesNotContain(outcome.PermanentChanges, change => change.Kind.StartsWith("earned_", StringComparison.Ordinal));
            var terminalEvidence = world.Observe().PublicEvidence!;
            var eventStart = terminalEvidence.Events.Single(item => item.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Event });
            var combatStart = terminalEvidence.Events.Single(item => item.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat });
            var eventEnd = Assert.Single(terminalEvidence.Events.Where(item => item.OwnerOrdinal == eventStart.OwnerOrdinal && item.Payload is PublicOwnerEnded));
            var combatEnd = Assert.Single(terminalEvidence.Events.Where(item => item.OwnerOrdinal == combatStart.OwnerOrdinal && item.Payload is PublicOwnerEnded));
            Assert.Equal(PublicEvidenceOwnerOutcome.Completed, ((PublicOwnerEnded)eventEnd.Payload).Outcome);
            Assert.Equal(PublicEvidenceOwnerOutcome.Defeat, ((PublicOwnerEnded)combatEnd.Payload).Outcome);
            Assert.True(combatEnd.EventOrdinal < eventEnd.EventOrdinal);
        }
    }

    [Fact]
    public async Task FixedMixedDeckRetainsEveryBoundedOrdinaryRejection()
    {
        // One fixed source and four predeclared independent proposals. Exhaustion
        // here says nothing about impossible content or practical broad coverage.
        var prior = OwnerPrior() with { Setup = OwnerPrior().Setup with
        {
            Deck = ["StrikeSilent", "DefendSilent", "Neutralize", "Survivor", "DaggerThrow", "CloakAndDagger", "Backflip", "PoisonedStab"],
        } };
        var packet = await DetachedRoot(prior);
        var source = new NativeConstructedTapeSource(packet, prior);
        Assert.False(source.UsesPrimitiveConditioning);
        var failure = await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(101, 4));
        Assert.Equal(4, failure.Attempts);
        Assert.Equal(new[] { 1, 2, 3, 4 }, source.ProposalAudit.Select(row => row.Attempt));
        Assert.All(source.ProposalAudit, row =>
        {
            Assert.Contains(row.Status, new[] { "public_constraint_mismatch", "public_packet_mismatch" });
            AssertOrdinaryAudit(row, row.Status);
        });
    }

    [Fact]
    public async Task OwnerAbsenceAndCancellationRemainDistinctAndReleaseTheNativeGraph()
    {
        var prior = OwnerPrior();
        Assert.Null(await Open(prior with { RootSelection = "first_pending_choice" }));
        Assert.Null(await Open(prior with { SourceDecisionHorizon = 2 }));
        var packet = await DetachedRoot(prior);
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(prior, before.Token));
        var cancelledSource = new NativeConstructedTapeSource(packet, prior, cancellationToken: before.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledSource.SampleWorldAsync(101, 4));
        Assert.Equal("computation_cancelled", Assert.Single(cancelledSource.ProposalAudit).Status);
        using var during = new CancellationTokenSource();
        var active = Assert.IsType<NativeRunWorld>(await Open(prior, during.Token));
        var run = active.NativeRun;
        try
        {
            var action = active.Observe().Actions.Single(item => item.Kind == "end_turn");
            during.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.StepAsync(action));
        }
        finally { await active.DisposeAsync(); }
        Assert.Throws<ObjectDisposedException>(() => active.Observe());
        Assert.Null(run.Players.Single().PlayerCombatState);
        Assert.Null(run.CurrentRoom);
        Assert.Equal(0, run.CurrentRoomCount);
    }

    private static Task<NativeRunWorld?> Open(NativeConstructedTapePrior prior, CancellationToken token = default) =>
        NativeRunWorld.OpenConstructedLabelTapeAsync(prior, Recipe, NativeLabelTape.ForConstructedPrior(prior, Recipe), token);

    private static JsonObject Request(NativeConstructedTapeDataset.CollectionContract contract)
    {
        var options = JsonNode.Parse(PublicJson.Serialize(contract.Options))!.AsObject();
        foreach (string computed in new[] { "primitiveLaw", "primitiveImplementation", "rootLaw", "setupLaw" })
            options["prior"]!.AsObject().Remove(computed);
        return new()
        {
            ["op"] = "native_constructed_tape_candidates", ["purpose"] = NativeConstructedTapeDataset.Purpose,
            ["options"] = options, ["teacherOptions"] = JsonNode.Parse(PublicJson.Serialize(contract.Teacher)),
            ["build_receipt_json"] = contract.Receipt.Json,
        };
    }

    private static async Task<JsonObject> Collect(JsonObject request, NativeConstructedTapeDataset.TestHooks hooks)
    {
        using var input = JsonDocument.Parse(request.ToJsonString());
        var result = await NativeConstructedTapeDataset.CollectAsync(input.RootElement, testHooks: hooks);
        return JsonNode.Parse(PublicJson.Serialize(result))!.AsObject();
    }

    private static async Task<DecisionPacket> DetachedRoot(NativeConstructedTapePrior prior)
    {
        var world = Assert.IsType<NativeRunWorld>(await Open(prior));
        DecisionPacket packet;
        try { packet = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe())); }
        finally { await world.DisposeAsync(); }
        Assert.Throws<ObjectDisposedException>(() => world.Observe());
        Assert.Null(world.NativeRun.Players.Single().PlayerCombatState);
        return packet;
    }

    private static void AssertOwnerEvidence(DecisionPacket packet)
    {
        PublicEvidenceInput.Validate(packet);
        var evidence = Assert.IsType<PublicRunEvidence>(packet.PublicEvidence);
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, evidence.SchemaVersion);
        Assert.False(evidence.CompleteFromRunStart);
        Assert.Null(evidence.Events[0].OwnerOrdinal);
        Assert.Equal(PublicEvidenceGapReason.RunStartNotObserved, Assert.IsType<PublicEvidenceGap>(evidence.Events[0].Payload).Reason);
        Assert.Single(evidence.Events.Where(item => item.Payload is PublicEvidenceGap));
        Assert.DoesNotContain(evidence.Events, item => item.Payload is PublicMapObserved or PublicMapChosen or PublicRunStarted);
        var ownerEvent = Assert.Single(evidence.Events.Where(item => item.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Event }));
        var combatEvent = Assert.Single(evidence.Events.Where(item => item.Payload is PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat }));
        var owner = Assert.IsType<PublicOwnerStarted>(ownerEvent.Payload);
        var combat = Assert.IsType<PublicOwnerStarted>(combatEvent.Payload);
        Assert.Null(owner.ParentOwnerOrdinal);
        Assert.Equal(ownerEvent.OwnerOrdinal, combat.ParentOwnerOrdinal);
        Assert.True(owner.CompleteFromOwnerStart);
        Assert.True(combat.CompleteFromOwnerStart);
        Assert.Equal((1, 1), (owner.ActIndex, owner.Floor));
        Assert.Equal((1, 1), (combat.ActIndex, combat.Floor));
        var choices = evidence.Events.Where(item => item.Payload is PublicOptionChosen).ToArray();
        Assert.Equal(new[] { "KEEP_THE_KEY", "FIGHT" }, choices.Select(item => ((PublicOptionChosen)item.Payload).Key));
        foreach (var choiceEvent in choices)
        {
            Assert.Equal(ownerEvent.OwnerOrdinal, choiceEvent.OwnerOrdinal);
            var choice = (PublicOptionChosen)choiceEvent.Payload;
            var offered = evidence.Events.Single(item => item.EventOrdinal == choice.OfferEventOrdinal);
            Assert.Equal(ownerEvent.OwnerOrdinal, offered.OwnerOrdinal);
            Assert.Contains(Assert.IsType<PublicOptionsObserved>(offered.Payload).Options, option => option.Key == choice.Key && !option.IsLocked);
            Assert.True(offered.EventOrdinal < choiceEvent.EventOrdinal);
            Assert.True(choiceEvent.EventOrdinal < combatEvent.EventOrdinal);
        }
        var context = Assert.IsType<PublicRunContext>(packet.Observation!.RunContext);
        Assert.Equal((1, 1), (context.ActIndex, context.Floor));
        Assert.False(context.CompleteFromRunStart);
        Assert.Null(context.CombatEntryIndex);
        Assert.DoesNotContain("actual_seed", PublicJson.Serialize(packet));
        Assert.DoesNotContain("source_draw_seed", PublicJson.Serialize(packet));
    }

    private static void AssertOrdinaryAudit(NativeConstructedTapeProposalAudit row, string status)
    {
        Assert.Equal(status, row.Status);
        Assert.Equal(0, row.ConditionedShuffles);
        Assert.Equal(0, row.ConditionedHpCount);
        Assert.Equal(0, row.ConditionedTapeCells);
        Assert.True(row.DistinctTapeCells > 0);
    }

    private static async Task<RolloutOutcome> Finish(NativeRunWorld world, bool endTurnsOnly)
    {
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        int lastTurn = 0;
        for (int step = 0; step < 32 && world.Observe().Status != "terminal_settled"; step++)
        {
            var packet = world.Observe();
            lastTurn = packet.Observation!.Turn;
            await world.StepAsync(endTurnsOnly ? packet.Actions.Single(action => action.Kind == "end_turn") : policy.Choose(packet));
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        return await world.RecordSettledAsync(endTurnsOnly ? "constructed-lantern-end-turn" : policy.Id, lastTurn);
    }
}
