using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativeConstructedLanternShuffleTests(ITestOutputHelper output)
{
    // Literal pre-change source bytes from the retained main artifact:
    // artifacts/reports/constructed-owner-ruby-v1/lantern-boundary/response.jsonl.
    // The artifact is read-only provenance, never a source root for these tests.
    private const string OwnerPriorV1Json = """
        {"schemaVersion":"nosl.constructed-native-event-owner-map-rewards-state-tape-prior.v1","setup":{"encounter":"MysteriousKnightEventEncounter","deck":["GrandFinale","GrandFinale","GrandFinale"],"potions":[],"relics":[],"hp":70,"maxHp":70,"gold":null},"eventOwner":{"event":"TheLanternKey","act":"Hive","fixtureFloor":1,"choiceRule":"keep-the-key-then-fight-v1"},"sourcePolicyId":"nosl-public-rules-v2","sourceDecisionHorizon":16,"rootSelection":"opening","decisionIndex":0,"primitiveLaw":"independent-map-act-seed-raw-cursor-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1","primitiveImplementation":"sha256-address-expansion-with-explicit-conditioned-overrides-v1","rootLaw":"one-declared-native-event-owned-combat-fixed-public-stopping-rule-with-absence-v1","setupLaw":"fixed-acts-declared-event-location-base-inventory-native-event-owner-public-choices-v1"}
        """;
    private const string OwnerGenerationV1Json = """
        {"schema_version":"nosl.native-constructed-tape.source-generation.v1","source_prior_schema":"nosl.constructed-native-event-owner-map-rewards-state-tape-prior.v1","setup":{"encounter":"MysteriousKnightEventEncounter","deck":["GrandFinale","GrandFinale","GrandFinale"],"potions":[],"relics":[],"hp":70,"maxHp":70,"gold":null},"setup_law":"fixed-acts-declared-event-location-base-inventory-native-event-owner-public-choices-v1","source_policy":"nosl-public-rules-v2","source_script":"nosl-natural-public-script-v3","primitive_law":"independent-map-act-seed-raw-cursor-rewards-origin-seed-raw-cursor-and-native-full-state-partitions-v1","primitive_implementation":"sha256-address-expansion-with-explicit-conditioned-overrides-v1","character":"Silent","ascension":10,"simulator":"5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0","random_domains":{"native_run_seed":"NOSL-NATIVE-TAPE-V1:{RunSeed:X16}","native_state_words":"sha256-u64le:4E4F534C54415031,TapeSeed,State0,State1,State2,State3","rewards_words":"sha256-u64le:4E4F534C52574431,TapeSeed,InitialSeed,RawCursor","map_words":"sha256-u64le:4E4F534C4D415031,TapeSeed,ActIndex,InitialSeed,RawCursor"},"event_owner":{"event":"TheLanternKey","act":"Hive","fixtureFloor":1,"choiceRule":"keep-the-key-then-fight-v1"}}
        """;
    private const string ShuffleVersion = "nosl-constructed-native-event-tape-shuffle-v2-public-evidence-v2";
    private const string ShuffleProfile = "owned-constructed-native-event-tape-shuffle-v2-public-evidence-v2";
    private const string RejectionVersion = "nosl-constructed-native-event-tape-rejection-v1-public-evidence-v2";
    private const string RejectionProfile = "owned-constructed-native-event-tape-conditional-v1-public-evidence-v2";
    private const ulong SourceSeed = 44121;
    private const int AttemptLimit = 16;
    private static string[] MixedDeck => ["StrikeSilent", "StrikeSilent+", "StrikeSilent", "DefendSilent", "DefendSilent+",
        "DefendSilent", "Neutralize+", "Survivor", "DeadlyPoison", "BladeDance+", "DaggerThrow", "Backflip",
        "Acrobatics", "DodgeAndRoll", "Dash", "PoisonedStab"];
    private static NativeConstructedTapePrior Prior => new()
    {
        SchemaVersion = NativeConstructedTapePrior.EventOwnerVersion,
        EventOwner = new() { Event = "TheLanternKey", Act = "Hive", FixtureFloor = 1, ChoiceRule = "keep-the-key-then-fight-v1" },
        Setup = new()
        {
            Encounter = "MysteriousKnightEventEncounter", Deck = MixedDeck,
            Potions = ["FirePotion", "BlockPotion"], Relics = ["BagOfPreparation"], Hp = 70, MaxHp = 70, Gold = 110,
        },
        SourcePolicyId = PublicContinuationPolicies.ReviewedId, SourceDecisionHorizon = 48,
    };
    private static NativeConstructedTapePrior BaselinePrior => Prior with
    {
        Setup = new() { Encounter = "MysteriousKnightEventEncounter", Deck = ["GrandFinale", "GrandFinale", "GrandFinale"],
            Potions = [], Relics = [], Hp = 70, MaxHp = 70 }, SourceDecisionHorizon = 16,
    };

    [Fact]
    public async Task FixedMixedDeckConditionsOnlyOneNativeShuffleAndRetainsExactV5OwnedWorlds()
    {
        // Predeclared engineering fixture, not a source search or coverage corpus.
        var prior = Prior; var root = await DetachedRoot(prior); string before = PublicJson.Serialize(root);
        PublicEvidenceInput.Validate(root);
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, root.PublicEvidence!.SchemaVersion);
        Assert.False(root.PublicEvidence.CompleteFromRunStart);
        var condition = NativePublicCombatPrefixCondition.CreateConstructedLanternStartup(root, prior);
        var input = Assert.Single(condition.Combats).Value;
        Assert.Equal(1, condition.EligibleShuffleCount); Assert.Equal(0, condition.EligibleHpCount);
        Assert.NotNull(input.Shuffle); Assert.Null(input.Hp); Assert.Null(input.SlugHp); Assert.Null(input.ToadpoleHp);
        Assert.Null(input.DrawPrefix); Assert.Equal(16, input.Shuffle.DeckCount); Assert.Equal(9, input.Shuffle.DrawPrefixKeys.Length);
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV2(root, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV3(root, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV4(root, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV5(root, out _, out _));
        Assert.Equal(0, NativePublicCombatPrefixCondition.CreateConstructed(root, prior).EligibleShuffleCount);
        Assert.Equal(0, NativePublicCombatPrefixCondition.Create(root).EligibleShuffleCount);
        Assert.True(NativeInitialShuffleCondition.TryCreateLanternStartup(root, out _, out string? reason), reason);
        Assert.Equal(before, PublicJson.Serialize(root));

        var source = new NativeConstructedTapeSource(root, prior);
        Assert.True(source.UsesPrimitiveConditioning); Assert.False(source.UsesRubyFormationConditioning);
        Assert.Equal(ShuffleProfile, source.PosteriorProfile);
        foreach (ulong seed in new ulong[] { 74121, 74122 })
        {
            await using var world = Assert.IsType<NativeRunWorld>(await source.SampleWorldAsync(seed, AttemptLimit));
            Assert.Equal(before, PublicJson.Serialize(world.Observe()));
            var enemy = Assert.Single(world.Observe().Observation!.Enemies);
            Assert.Equal(("MysteriousKnight", 108, 108, 6m), (enemy.Id, enemy.Hp, enemy.MaxHp, enemy.Block));
            Assert.Equal(new[] { new PublicIntent("Attack", 17, 1) }, enemy.Intents);
            Assert.Equal(new[] { "PlatingPower", "StrengthPower" }, enemy.Powers.Select(power => power.Id).Order());
            Assert.All(enemy.Powers, power => Assert.Equal(6m, power.Amount));
            var owner = Assert.IsType<TheLanternKey>(Assert.IsType<EventRoom>(world.NativeRun.BaseRoom).Event);
            await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
            var forkOwner = Assert.IsType<TheLanternKey>(Assert.IsType<EventRoom>(fork.NativeRun.BaseRoom).Event);
            Assert.NotSame(world.NativeRun, fork.NativeRun); Assert.NotSame(world.NativeCombatRoom, fork.NativeCombatRoom);
            Assert.NotSame(owner, forkOwner); Assert.NotSame(owner.ForcedCombatExtraRewards.Single(), forkOwner.ForcedCombatExtraRewards.Single());
            Assert.Equal(before, PublicJson.Serialize(fork.Observe()));
            var forkOutcome = await FinishByEndingTurns(fork);
            Assert.Equal(before, PublicJson.Serialize(world.Observe()));
            var outcome = await FinishByEndingTurns(world);
            Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(forkOutcome));
            Assert.Equal(TerminalKind.Loss, outcome.TerminalKind); Assert.True(outcome.SettlementComplete);
            Assert.True(outcome.HpEventDiagnosticsComplete); Assert.True(outcome.ResourceProvenanceComplete);
            Assert.True(owner.IsFinished); Assert.False(owner.IsAwaitingForcedCombat);
            Assert.Empty(owner.ForcedCombatExtraRewards); Assert.Empty(world.NativeCombatRoom.GeneratedRewards);
            Assert.Null(world.NativeRun.CurrentRoom); Assert.Null(fork.NativeRun.CurrentRoom);
            Assert.Null(world.NativeRun.Players.Single().PlayerCombatState);
            var ended = world.Observe().PublicEvidence!.Events.Where(e => e.Payload is PublicOwnerEnded).ToArray();
            Assert.Equal(new[] { PublicEvidenceOwnerOutcome.Defeat, PublicEvidenceOwnerOutcome.Completed },
                ended.Select(e => ((PublicOwnerEnded)e.Payload).Outcome));
        }
        Assert.Equal(2, source.ProposalAudit.Count(row => row.Status == "accepted"));
        Assert.All(source.ProposalAudit, row =>
        {
            Assert.InRange(row.Attempt, 1, AttemptLimit); Assert.Equal(1, row.ConditionedShuffles);
            Assert.Equal(0, row.ConditionedHpCount); Assert.True(row.ConditionedTapeCells > 0);
        });
        output.WriteLine(PublicJson.Serialize(source.ProposalAudit));
    }

    [Fact]
    public async Task KnightAndPlatingStartupHooksPreservePhysicalCardsMetadataAndShuffleState()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("lantern-startup-pile-proof", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        await Prior.Setup.ApplyAsync(run);
        var state = new CombatState(run); state.AddPlayerCreature(player.Creature);
        player.ResetCombatState(); player.PopulateCombatState(run.Rng.Shuffle);
        var monster = (MysteriousKnight)ModelDb.Monster<MysteriousKnight>().MutableClone();
        state.AddMonster(monster, CombatSide.Enemy);
        var piles = player.PlayerCombatState!.AllPiles;
        var cards = piles.Select(pile => pile.Cards.ToArray()).ToArray();
        var metadata = piles.Select(pile => PublicJson.Serialize(pile.Cards.Select(PublicViews.Card).ToArray())).ToArray();
        string shuffle = JsonSerializer.Serialize(run.Rng.Shuffle.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });
        void AssertUnchanged()
        {
            for (int i = 0; i < piles.Count; i++)
            {
                Assert.True(cards[i].SequenceEqual(piles[i].Cards, ReferenceEqualityComparer.Instance), "Physical pile " + piles[i].Type);
                Assert.Equal(metadata[i], PublicJson.Serialize(piles[i].Cards.Select(PublicViews.Card).ToArray()));
            }
            Assert.Equal(shuffle, JsonSerializer.Serialize(run.Rng.Shuffle.ToSerializable(), new JsonSerializerOptions { IncludeFields = true }));
        }
        Assert.Equal(108, monster.Creature.CurrentHp);
        monster.SetUpForCombat(); monster.RollMove(state.PlayerCreatures);
        Assert.Equal("RAM_MOVE", monster.NextMove!.StateId); AssertUnchanged();
        await monster.AfterAddedToRoom(); AssertUnchanged();
        Assert.Equal(6m, monster.Creature.GetPower<StrengthPower>()!.Amount);
        Assert.Equal(6m, monster.Creature.GetPower<PlatingPower>()!.Amount);
        await Hook.BeforeSideTurnStart(state, CombatSide.Player, state.PlayerCreatures); AssertUnchanged();
        Assert.Equal(6m, monster.Creature.Block);
        await Hook.AfterSideTurnStart(state, CombatSide.Enemy, state.Enemies); AssertUnchanged();
        Assert.Equal(6m, monster.Creature.GetPower<PlatingPower>()!.Amount);
        await Hook.BeforeSideTurnEndEarly(state, CombatSide.Enemy, state.Enemies); AssertUnchanged();
        Assert.Equal(12m, monster.Creature.Block);
        state.RoundNumber = 2;
        await Hook.AfterSideTurnStart(state, CombatSide.Enemy, state.Enemies); AssertUnchanged();
        Assert.Equal(5m, monster.Creature.GetPower<PlatingPower>()!.Amount);
    }

    [Theory]
    [InlineData("unknown_card")]
    [InlineData("card_hook")]
    [InlineData("innate")]
    [InlineData("enchantment")]
    [InlineData("affliction")]
    [InlineData("relic_hook")]
    [InlineData("unknown_potion")]
    [InlineData("power_model")]
    [InlineData("power_amount")]
    [InlineData("power_source")]
    [InlineData("power_target")]
    [InlineData("enemy_hp")]
    [InlineData("enemy_max_hp")]
    [InlineData("enemy_block")]
    [InlineData("enemy_power")]
    [InlineData("intent_damage")]
    [InlineData("intent_repeats")]
    [InlineData("parentless")]
    [InlineData("incomplete_parent")]
    [InlineData("incomplete_child")]
    [InlineData("wrong_choice")]
    public async Task ValidPublicDtosFailClosedForUncertifiedEntryOwnerAndStartup(string change)
    {
        var root = await DetachedRoot(Prior);
        var changed = Mutate(root, payload =>
        {
            if (payload is PublicCombatFact { FactKind: PublicCombatFactKind.EntryAssets, Assets: { } assets }
                && change is "unknown_card" or "card_hook" or "innate" or "enchantment" or "affliction" or "relic_hook" or "unknown_potion")
            {
                var deck = assets.Deck; var relics = assets.Relics; var potions = assets.Potions;
                deck[0] = change switch
                {
                    "unknown_card" => deck[0] with { Id = "unregistered-card" },
                    "card_hook" => deck[0] with { Id = "Void" },
                    "innate" => deck[0] with { Keywords = ["Innate"] },
                    "enchantment" => deck[0] with { Enchantments = [new("Swift", 1)] },
                    "affliction" => deck[0] with { Affliction = new("Hexed", 1) },
                    _ => deck[0],
                };
                if (change == "relic_hook") relics = [.. relics, new("GamblingChip", new Dictionary<string, int>())];
                if (change == "unknown_potion") potions = potions.SetItem(0, "unregistered-potion");
                return new PublicCombatFact(PublicCombatFactKind.EntryAssets, assets: new(assets.Hp, assets.MaxHp, assets.Gold,
                    deck, relics, potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed));
            }
            if (payload is PublicCombatFact { FactKind: PublicCombatFactKind.PowerChanged, Model: "PlatingPower" } power)
                return change switch
                {
                    "power_model" => Power(power, model: "SuckPower"),
                    "power_amount" => Power(power, amount: 5),
                    "power_source" => Power(power, source: -2),
                    "power_target" => Power(power, target: -2),
                    _ => payload,
                };
            if (payload is PublicCombatFact { FactKind: PublicCombatFactKind.IntentPublished } intent
                && change is "intent_damage" or "intent_repeats")
                return new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: intent.TargetSlot, model: intent.Model,
                    intents: [new("Attack", change == "intent_damage" ? 18 : 17, change == "intent_repeats" ? 2 : 1)]);
            if (payload is PublicCombatDecision decision && change is "enemy_hp" or "enemy_max_hp" or "enemy_block" or "enemy_power")
            {
                var enemy = decision.Observation.Enemies.Single();
                enemy = change switch
                {
                    "enemy_hp" => enemy with { Hp = 107 },
                    "enemy_max_hp" => enemy with { MaxHp = 109 },
                    "enemy_block" => enemy with { Block = 5 },
                    _ => enemy with { Powers = [.. enemy.Powers, new("SuckPower", 1)] },
                };
                return new PublicCombatDecision(decision.Status, decision.Observation with { Enemies = [enemy] }, decision.Actions,
                    decision.HistoryThroughEventOrdinal, decision.HistoryCompleteFromCombatStart);
            }
            if (change == "incomplete_child" && payload is PublicCombatDecision incomplete)
                return new PublicCombatDecision(incomplete.Status, incomplete.Observation, incomplete.Actions,
                    incomplete.HistoryThroughEventOrdinal, false);
            if (payload is PublicOwnerStarted owner && (change == "parentless" && owner.OwnerKind == PublicEvidenceOwnerKind.Combat
                || change == "incomplete_parent" && owner.OwnerKind == PublicEvidenceOwnerKind.Event
                || change == "incomplete_child" && owner.OwnerKind == PublicEvidenceOwnerKind.Combat))
                return new PublicOwnerStarted(owner.OwnerKind, owner.ActIndex, owner.Floor,
                    change == "parentless" ? null : owner.ParentOwnerOrdinal,
                    change == "parentless" && owner.CompleteFromOwnerStart);
            if (change == "wrong_choice" && payload is PublicOptionChosen { Key: "KEEP_THE_KEY" } chosen)
                return new PublicOptionChosen(chosen.OfferEventOrdinal, "RETURN_THE_KEY");
            return payload;
        });
        PublicEvidenceInput.Validate(changed);
        string before = PublicJson.Serialize(changed);
        var condition = NativePublicCombatPrefixCondition.CreateConstructedLanternStartup(changed, Prior);
        Assert.Equal(0, condition.EligibleShuffleCount); Assert.Equal(0, condition.EligibleHpCount);
        Assert.True(condition.Combats.Count <= 1); Assert.Equal(before, PublicJson.Serialize(changed));
    }

    [Fact]
    public async Task LaterKnightTurnsDoNotExtendTheStartupShuffleCertificate()
    {
        var prior = Prior with { RootSelection = "first_player_turn_2" };
        var root = await DetachedRoot(prior); PublicEvidenceInput.Validate(root);
        Assert.Equal(2, root.Observation!.Turn);
        var input = Assert.Single(NativePublicCombatPrefixCondition.CreateConstructedLanternStartup(root, prior).Combats).Value;
        Assert.NotNull(input.Shuffle); Assert.Null(input.DrawPrefix); Assert.Equal(9, input.Shuffle.DrawPrefixKeys.Length);
        Assert.True(root.PublicEvidence!.Events.Count(e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardDrawn }) > 9);
        Assert.Null(input.Hp); Assert.Null(input.SlugHp); Assert.Null(input.ToadpoleHp);
        Assert.Equal(0, NativePublicCombatPrefixCondition.CreateConstructed(root, prior).EligibleShuffleCount);
    }

    [Fact]
    public async Task UncertifiedNativeInnateOpeningFallsBackUnderTheNewVersion()
    {
        var prior = BaselinePrior with { Setup = BaselinePrior.Setup with { Deck = ["Backstab", "Backstab", "Backstab"] } };
        var root = await DetachedRoot(prior); var source = new NativeConstructedTapeSource(root, prior);
        Assert.False(source.UsesPrimitiveConditioning); Assert.Equal(ShuffleProfile, source.PosteriorProfile);
        Assert.Equal(ShuffleVersion, NativeConstructedTapeSource.ImplementationFor(prior));
        await using var world = await source.SampleWorldAsync(74121, 1);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        var row = Assert.Single(source.ProposalAudit);
        Assert.Equal("accepted", row.Status); Assert.Equal(0, row.ConditionedShuffles);
        Assert.Equal(0, row.ConditionedHpCount); Assert.Equal(0, row.ConditionedTapeCells);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectionBindsConditioningVersionBeforeExecutionAndKeepsHistoricalSourceIdentity(bool enabled)
    {
        var prior = BaselinePrior; var contract = Contract(prior, enabled);
        Assert.Equal(OwnerPriorV1Json, PublicJson.Serialize(contract.Options.Prior));
        Assert.Equal("f6e4e7f90adf93fa36eb1e97fa35709cbff6d13e20a788ca5ec3178407236fd6", contract.Options.Prior.Identity);
        Assert.Equal(OwnerGenerationV1Json, contract.SourceGenerationJson);
        Assert.Equal("1a7d623d2df19e89b79d5e5f64420b5680c679a17cc1aa1cfa9deb5836975969", contract.SourceGenerationIdentity);
        var historicalRecipe = new NativeTapeRecipe(16304907411750691291UL, 5805845383119718627UL, 787680976606561640UL, 0, 0);
        var historicalAudit = contract.Provenance(historicalRecipe)["audit_only"]!;
        Assert.Equal("constructed-native-tape-random-family-v1:253a01933e6d6b977ba60cc678d95559b4cffa209abab9d41fa3be266f768a0a",
            historicalAudit["source_random_family_alias"]!.GetValue<string>());
        Assert.Equal("constructed-native-tape-configured-source-v1:f1d14b5a038caeaccd2c36d34006d2f4f189a6812ddaf7b829c80a02ccc3d427/combat:0",
            historicalAudit["underlying_battle_alias"]!.GetValue<string>());
        string version = enabled ? ShuffleVersion : RejectionVersion, profile = enabled ? ShuffleProfile : RejectionProfile;
        Assert.Equal(RejectionVersion, NativeConstructedTapeSource.EventOwnerImplementationVersion);
        Assert.Equal(RejectionProfile, NativeConstructedTapeSource.EventOwnerProfile);
        Assert.Equal(version, NativeConstructedTapeSource.ImplementationFor(prior, enabled));
        Assert.Equal(version, JsonNode.Parse(contract.Json)!["sampler_version"]!.GetValue<string>());
        // GrandFinale's ShouldPlay hook remains deliberately uncertified. The
        // enabled protocol still binds v2 before discovering this fallback.
        var request = Request(contract); bool disposed = false;
        using var document = JsonDocument.Parse(request.ToJsonString());
        var result = await NativeConstructedTapeDataset.CollectAsync(document.RootElement, testHooks: new(
            AfterSourceDisposed: () => disposed = true,
            WrapPosteriorSource: source => { Assert.True(disposed); Assert.Equal(profile, source.PosteriorProfile); return source; }));
        var report = JsonNode.Parse(PublicJson.Serialize(result))!;
        Assert.Equal(1, report["source_worlds_disposed"]!.GetValue<int>());
        Assert.False(report["trainable"]!.GetValue<bool>()); Assert.False(report["formal_labels"]!.GetValue<bool>());
        var audit = Assert.Single(report["records"]!.AsArray())!["audit_only"]!;
        Assert.False(audit["conditioning_eligible"]!.GetValue<bool>());
        Assert.Equal(enabled ? "entry_hook_not_certified:GrandFinale.ShouldPlay" : "declared_event_owner_ordinary_tape_rejection_v1",
            audit["conditioning_reason"]!.GetValue<string>());
        foreach (string field in new[] { "sampler_version", "posterior_implementation" })
            Assert.Equal(version, audit[field]!.GetValue<string>());
        Assert.Equal(profile, audit["posterior_profile"]!.GetValue<string>());
        Assert.Equal(version, audit["versions"]!["sampler"]!.GetValue<string>());
        Assert.Equal(version, audit["versions"]!["posterior_implementation"]!.GetValue<string>());
        Assert.Equal(profile, audit["versions"]!["posterior_profile"]!.GetValue<string>());
        Assert.All(audit["posterior_proposals"]!.AsArray(), row =>
        {
            Assert.Equal(0, row!["conditionedShuffles"]!.GetValue<int>());
            Assert.Equal(0, row["conditionedHpCount"]!.GetValue<int>());
        });
    }

    private static PublicCombatFact Power(PublicCombatFact fact, string? model = null, decimal? amount = null,
        int? source = null, int? target = null) => new(PublicCombatFactKind.PowerChanged,
        targetSlot: target ?? fact.TargetSlot, sourceSlot: source ?? fact.SourceSlot, model: model ?? fact.Model,
        amount: amount ?? fact.Amount, targetModel: fact.TargetModel);

    private static DecisionPacket Mutate(DecisionPacket root, Func<PublicEvidencePayload, PublicEvidencePayload> edit)
    {
        var evidence = root.PublicEvidence!;
        var events = evidence.Events.Select(e => new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, edit(e.Payload))).ToImmutableArray();
        var last = (PublicCombatDecision)events[^1].Payload;
        return root with { Observation = last.Observation with { History = root.Observation!.History },
            PublicEvidence = new(evidence.SchemaVersion, evidence.CompleteFromRunStart, events) };
    }

    private static async Task<DecisionPacket> DetachedRoot(NativeConstructedTapePrior prior)
    {
        var recipe = prior.Draw(new Rng(SourceSeed, NativeConstructedTapePrior.SourceDrawDomain));
        var world = Assert.IsType<NativeRunWorld>(await NativeRunWorld.OpenConstructedLabelTapeAsync(prior, recipe,
            NativeLabelTape.ForConstructedPrior(prior, recipe)));
        DecisionPacket root;
        try { root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe())); }
        finally { await world.DisposeAsync(); }
        Assert.Null(world.NativeRun.CurrentRoom); Assert.Null(world.NativeRun.Players.Single().PlayerCombatState);
        Assert.Throws<ObjectDisposedException>(() => world.Observe());
        return root;
    }

    private static async Task<RolloutOutcome> FinishByEndingTurns(NativeRunWorld world)
    {
        int lastTurn = 0;
        for (int step = 0; step < 24 && world.Observe().Status != "terminal_settled"; step++)
        {
            var packet = world.Observe(); lastTurn = packet.Observation!.Turn;
            await world.StepAsync(packet.Actions.Single(action => action.Kind == "end_turn"));
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        return await world.RecordSettledAsync("lantern-shuffle-end-turn", lastTurn);
    }

    private static NativeConstructedTapeDataset.CollectionContract Contract(NativeConstructedTapePrior prior, bool enabled)
    {
        var receipt = JsonNode.Parse(PublicJson.Serialize(NativeCompleteMapBuildReceipt.RuntimeIdentity()))!.AsObject();
        receipt["wrapper_source_sha256"] = new string('a', 64); receipt["vendor_source_sha256"] = new string('b', 64);
        return NativeConstructedTapeDataset.CollectionContract.Create(new()
        {
            Prior = prior, EnableConditioning = enabled, CollectionId = "lantern-shuffle-engineering-fixture",
            SourceDrawSeeds = [SourceSeed], WallBudgetSeconds = 30,
        }, new() { Mode = "T0", ContinuationPolicyId = PublicContinuationPolicies.ReviewedId,
            EvaluationSeeds = [74121], MaxPosteriorAttempts = AttemptLimit, MaxDecisions = 1 }, NativeConstructedTapeDataset.Purpose, receipt.ToJsonString());
    }

    private static JsonObject Request(NativeConstructedTapeDataset.CollectionContract contract)
    {
        var options = JsonNode.Parse(PublicJson.Serialize(contract.Options))!.AsObject();
        foreach (string key in new[] { "primitiveLaw", "primitiveImplementation", "rootLaw", "setupLaw" }) options["prior"]!.AsObject().Remove(key);
        return new() { ["op"] = "native_constructed_tape_candidates", ["purpose"] = NativeConstructedTapeDataset.Purpose,
            ["options"] = options, ["teacherOptions"] = JsonNode.Parse(PublicJson.Serialize(contract.Teacher)),
            ["build_receipt_json"] = contract.Receipt.Json };
    }
}
