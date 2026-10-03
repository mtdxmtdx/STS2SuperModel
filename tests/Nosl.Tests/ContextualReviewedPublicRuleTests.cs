using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class ContextualReviewedPublicRuleTests
{
    [Theory]
    [InlineData("Finesse")]
    [InlineData("Impatience")]
    public async Task NativeV2AndV3ClosedCyclesTakeIdenticalLegalExitsAndSettleActualLoss(string card)
    {
        await using var legacy = await OpenNative(false, [card, card]);
        await using var contextual = await OpenNative(true, [card, card]);
        var policy = new ContextualReviewedPublicRulePolicy();
        var original = PublicJson.Serialize(contextual.Observe());
        var context = contextual.Observe().Observation!.RunContext!;
        context.Validate();
        Assert.False(context.CompleteFromRunStart);
        Assert.Null(context.CombatEntryIndex); // Constructed native entry cannot invent a run history.
        Assert.Equal("play", new ReviewedPublicRulePolicy().Choose(contextual.Observe()).Kind);
        int actions = 0;
        while (contextual.Observe().Status == "player_decision" && actions < 32)
        {
            var v2 = legacy.Observe(); var v3 = contextual.Observe();
            Assert.Equal(PublicJson.Serialize(v2), PublicJson.Serialize(StripContext(v3)));
            var exit = policy.Choose(v3);
            Assert.Equal("end_turn", exit.Kind);
            Assert.Same(v3.Actions.Single(a => a.Kind == "end_turn"), exit);
            Assert.Equal(new ReviewedPublicRulePolicy().Choose(v2), exit);
            await legacy.StepAsync(exit); await contextual.StepAsync(exit); actions++;
        }
        Assert.Equal(14, actions);
        Assert.Equal("terminal_settled", contextual.Observe().Status);
        var outcome = await contextual.RecordSettledAsync(policy.Id, actions);
        Assert.Equal(TerminalKind.Loss, outcome.TerminalKind);
        Assert.True(outcome.IsTrueTerminal && outcome.SettlementComplete);
        Assert.Equal(0, outcome.HpAfterSettlement);
        Assert.Equal(PublicJson.Serialize(await legacy.RecordSettledAsync(policy.Id, actions)), PublicJson.Serialize(outcome));
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(await contextual.RecordSettledAsync(policy.Id, actions)));

        await using var source = await OpenNative(true, [card, card]);
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        var options = new TeacherOptions { ContinuationPolicyId = policy.Id, EvaluationSeeds = [101, 102], MaxDecisions = 32 };
        var result = await CombatTeacher.EvaluateAsync(new ConstructedSource(source), options);
        Assert.Equal(6, result.Costs.WorldsAllocated);
        Assert.Equal(6, result.Costs.WorldsCompleted);
        Assert.Equal(source.Observe().Actions, result.Candidates.Select(c => c.Action));
        Assert.All(result.Candidates, c => Assert.All(c.Outcomes, o => Assert.Equal(TerminalKind.Loss, o.TerminalKind)));
        var capped = await CombatTeacher.EvaluateAsync(new ConstructedSource(source), options with { MaxDecisions = 2 });
        AssertUnresolved(capped, options.EvaluationSeeds);
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
    }

    [Theory]
    [InlineData("Finesse")]
    [InlineData("Impatience")]
    public async Task NativeContextKeepsUsefulDefenseThenUsefulBladeDanceAndShivs(string second)
    {
        await using var v2 = await OpenNative(false, ["Finesse", second, "BladeDance"], 12);
        await using var v3 = await OpenNative(true, ["Finesse", second, "BladeDance"], 12);
        var policy = new ContextualReviewedPublicRulePolicy();
        async Task Play(string id)
        {
            var packet = v3.Observe();
            Assert.Equal(PublicJson.Serialize(v2.Observe()), PublicJson.Serialize(StripContext(packet)));
            var action = policy.Choose(packet);
            Assert.Equal(id, packet.Observation!.Hand[action.Slot].Id);
            Assert.Equal(new ReviewedPublicRulePolicy().Choose(v2.Observe()), action);
            Assert.Contains(action, packet.Actions);
            string before = PublicJson.Serialize(packet);
            policy.Choose(packet);
            Assert.Equal(before, PublicJson.Serialize(packet));
            await v2.StepAsync(action); await v3.StepAsync(action);
        }
        await Play("Finesse");
        Assert.Equal(4m, v3.Observe().Observation!.Block);
        if (second == "Impatience") await Play("Impatience");
        await Play("Finesse");
        Assert.Equal(8m, v3.Observe().Observation!.Block);
        Assert.Contains(v3.Observe().Observation!.Hand[new ReviewedPublicRulePolicy().Choose(v3.Observe()).Slot].Id,
            new[] { "Finesse", "Impatience" });
        await Play("BladeDance");
        while (v3.Observe().Status == "player_decision") await Play("Shiv");
        Assert.Equal("terminal_settled", v3.Observe().Status);
        var outcome = await v3.RecordSettledAsync(policy.Id, 1);
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.Equal(70, outcome.HpAfterSettlement);
        Assert.Equal(0, ObjectiveEvaluator.Evaluate(outcome).Cost);
    }

    [Theory]
    [InlineData("FlashOfSteel", 13)]
    [InlineData("FlashOfSteel+", 9)]
    public async Task NativeContextPreservesProfitableAttackDrawCycles(string card, int expectedPlays)
    {
        await using var source = await OpenNative(true, [card, card]);
        var policy = new ContextualReviewedPublicRulePolicy();
        int actions = 0;
        while (source.Observe().Status == "player_decision" && actions < 32)
        {
            var packet = source.Observe();
            var action = policy.Choose(packet);
            Assert.Equal("play", action.Kind);
            Assert.Equal(new PublicRulePolicy().Choose(packet), action);
            await source.StepAsync(action); actions++;
        }
        Assert.Equal(expectedPlays, actions);
        Assert.Equal("terminal_settled", source.Observe().Status);
        var outcome = await source.RecordSettledAsync(policy.Id, 1);
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.Equal(0, ObjectiveEvaluator.Evaluate(outcome).Cost);
    }

    [Fact]
    public async Task NativeContextPreservesUsefulUnknownDrawAndPendingChoice()
    {
        await using var source = await OpenNative(true,
            [.. Enumerable.Repeat("Finesse", 30), "BladeDance"]);
        var policy = new ContextualReviewedPublicRulePolicy();
        for (int i = 0; i < 2; i++)
        {
            var packet = source.Observe();
            var draw = policy.Choose(packet);
            Assert.Equal("Finesse", packet.Observation!.Hand[draw.Slot].Id);
            await source.StepAsync(draw);
        }
        var enoughBlock = source.Observe();
        Assert.Equal(8m, enoughBlock.Observation!.Block);
        Assert.Contains(enoughBlock.Observation.UnknownDraw, c => c.Card.Id == "BladeDance");
        Assert.Equal("Finesse", enoughBlock.Observation.Hand[policy.Choose(enoughBlock).Slot].Id);

        await using var choice = await OpenNative(true, ["Survivor", "Finesse", "Impatience"]);
        var before = choice.Observe();
        var survivor = before.Actions.Single(a => a.Kind == "play" && before.Observation!.Hand[a.Slot].Id == "Survivor");
        var pending = await choice.StepAsync(survivor);
        Assert.Equal("card_choice", pending.Status);
        Assert.Equal(PublicRunContext.ObservationSchema, pending.Observation!.Schema);
        var selected = policy.Choose(pending);
        Assert.Equal(new PublicRulePolicy().Choose(pending), selected);
        Assert.Contains(selected, pending.Actions);
        Assert.Equal("player_decision", (await choice.StepAsync(selected)).Status);
    }

    [Fact]
    public async Task ContextValidationAndAllExistingMechanicGuardsFailClosed()
    {
        await using var source = await OpenNative(true, ["Finesse", "Finesse"]);
        var packet = source.Observe(); var o = packet.Observation!; var context = o.RunContext!;
        var policy = new ContextualReviewedPublicRulePolicy();
        var invalidContexts = new[] { context with { SchemaVersion = "future" }, context with { ActIndex = -1 },
            context with { Floor = -1 }, context with { CompleteFromRunStart = true }, context with { CombatEntryIndex = 0 } };
        var variants = invalidContexts.Select(c => o with { RunContext = c }).Concat(new[]
        {
            o with { RunContext = null }, o with { Schema = "nosl.public.v2" }, o with { Schema = "nosl.public.v4" },
            o with { UnidentifiedDrawCount = 1 }, o with { DrawCount = 1 },
            o with { Powers = [new("DexterityPower", 1)] }, o with { Potions = ["FirePotion"] },
            o with { Relics = ["RingOfTheSnake", "LetterOpener"] },
            o with { RelicStates = null }, o with { Orbs = null }, o with { Pets = null },
            o with { Enemies = [o.Enemies[0] with { Powers = [new("StrengthPower", 1)] }] },
            o with { Enemies = [o.Enemies[0] with { Intents = [new("Attack", 6, 1)] }] },
            o with { Hand = o.Hand.Select(c => c with { Enchantments = [new("Momentum", 1)] }).ToArray() },
            o with { Hand = o.Hand.Select(c => c with { Affliction = new("Sapping", 1) }).ToArray() },
            o with { Hand = o.Hand.Select(c => c with { Details = null }).ToArray() },
        });
        foreach (var variant in variants)
        {
            var changed = packet with { Observation = variant };
            Assert.Equal(new PublicRulePolicy().Choose(changed), policy.Choose(changed));
        }
        // Complete and explicitly unavailable histories both validate; neither
        // changes the certified combat mechanic or supplies a hidden count.
        Assert.Equal("end_turn", policy.Choose(packet with { Observation = o with
            { RunContext = context with { CompleteFromRunStart = true, CombatEntryIndex = 0 } } }).Kind);

        await using var unsupported = await OpenNative(true, ["Finesse", "Finesse"], relic: "LetterOpener");
        var unsupportedPacket = unsupported.Observe();
        Assert.Equal(new PublicRulePolicy().Choose(unsupportedPacket), policy.Choose(unsupportedPacket));
        var options = new TeacherOptions { ContinuationPolicyId = policy.Id, EvaluationSeeds = [201, 202], MaxDecisions = 2 };
        var capped = await CombatTeacher.EvaluateAsync(new ConstructedSource(unsupported), options);
        AssertUnresolved(capped, options.EvaluationSeeds);
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task OptInHasSeparateTeacherTreeDatasetAndPriorIdentities(string mode)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Finesse", "Finesse"]));
        var options = new TeacherOptions { Mode = mode, ContinuationPolicyId = PublicContinuationPolicies.ReviewedId,
            EvaluationSeeds = [101], ExplorationSeeds = mode == "T1" ? [7] : [], TreeDepth = 2, MaxDecisions = 32 };
        var old = await CombatTeacher.EvaluateAsync(source, options);
        var successor = await CombatTeacher.EvaluateAsync(source, options with { ContinuationPolicyId = PublicContinuationPolicies.ContextualReviewedId });
        Assert.Equal(mode == "T0" ? PublicContinuationPolicies.ContextualReviewedId : PublicContinuationPolicies.ContextualReviewedTreeId,
            successor.ContinuationVersion.Split(':')[0]);
        if (mode == "T1") Assert.Equal(old.ContinuationVersion.Split(':')[1], successor.ContinuationVersion.Split(':')[1]);
        Assert.Equal(PublicContinuationPolicies.ContextualReviewedDatasetVersion, PublicContinuationPolicies.DatasetVersion(successor.ContinuationVersion));
        using var record = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(successor, "v3", "combat", "family", [101], options.ExplorationSeeds)));
        var audit = record.RootElement.GetProperty("audit_only");
        Assert.Equal(PublicContinuationPolicies.ContextualReviewedDatasetVersion, audit.GetProperty("dataset_version").GetString());
        Assert.Equal(audit.GetProperty("dataset_version").GetString(), audit.GetProperty("versions").GetProperty("dataset").GetString());
        Assert.Equal(PublicContinuationPolicies.LegacyId, new TeacherOptions().ContinuationPolicyId);
        Assert.Equal(PublicContinuationPolicies.LegacyId, new NaturalSourceOptions().ContinuationPolicyId);
        Assert.Equal(PublicContinuationPolicies.ReviewedId, new NativeRunExecutionOptions().SourcePolicyId);
        var prior = new NativeTapePrior();
        Assert.NotEqual(prior.Identity, (prior with { Execution = prior.Execution with
            { SourcePolicyId = PublicContinuationPolicies.ContextualReviewedId } }).Identity);
    }

    private static void AssertUnresolved(TeacherResult result, ulong[] seeds)
    {
        Assert.Equal(result.PublicRoot.Actions, result.Candidates.Select(c => c.Action));
        Assert.Equal(result.PublicRoot.Actions.Length * seeds.Length, result.Costs.WorldsAllocated);
        Assert.Equal(0, result.Costs.WorldsCompleted);
        Assert.Empty(result.Ranking.Pairs);
        Assert.All(result.Candidates, c =>
        {
            Assert.Null(c.Evaluation.ExpectedCost);
            Assert.Equal(seeds.Length, c.Outcomes.Length);
            Assert.All(c.Outcomes, o =>
            {
                Assert.Equal(TerminalKind.ComputeTruncated, o.TerminalKind);
                Assert.False(o.IsTrueTerminal || o.SettlementComplete);
                Assert.Null(o.PlayerAlive);
            });
        });
        using var record = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "v3", "combat", "family", seeds, [])));
        var audit = record.RootElement.GetProperty("audit_only");
        Assert.Equal(PublicContinuationPolicies.ContextualReviewedDatasetVersion, audit.GetProperty("dataset_version").GetString());
        Assert.Equal(result.Costs.WorldsAllocated, audit.GetProperty("n_unresolved").GetInt32());
        foreach (var row in record.RootElement.GetProperty("targets").GetProperty("actions").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty("value").ValueKind);
            Assert.All(row.GetProperty("masks").EnumerateObject(), mask => Assert.False(mask.Value.GetBoolean()));
        }
    }

    private static DecisionPacket StripContext(DecisionPacket packet) => packet with
    { Observation = packet.Observation! with { Schema = "nosl.public.v2", RunContext = null } };

    // Test-only constructed lifecycle. Both channels use the real run observer,
    // native engine, independently owned replay forks and native settlement.
    // No production prior, natural-source coverage or all-game proof is claimed.
    private static async Task<NativeRunWorld> OpenNative(bool context, string[] deck, int enemyHp = 65, string? relic = null)
    {
        async Task Lifecycle(RunState run, RunDriver driver)
        {
            var player = run.Players.Single();
            foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
            foreach (string id in deck)
            {
                var card = (CardModel)ModelDb.All<CardModel>().Single(c => c.GetType().Name == id.TrimEnd('+')).MutableClone();
                card.AssignOwner(player);
                if (id.EndsWith('+')) card.Upgrade();
                player.Deck.AddInternal(card);
            }
            if (relic is not null) await RelicCmd.Obtain((RelicModel)ModelDb.All<RelicModel>()
                .Single(r => r.GetType().Name == relic).MutableClone(), player);
            await driver.RunOneInjectedCombatAsync(RoomType.Monster, run.Act.MonsterEncounterCandidates.First().IdEntry,
                (_, state) =>
                {
                    foreach (var enemy in state.Enemies.ToArray()) state.RemoveCreature(enemy);
                    var twig = state.AddMonster((TwigSlimeS)ModelDb.Monster<TwigSlimeS>().MutableClone(), CombatSide.Enemy);
                    twig.SetMaxHpInternal(enemyHp); twig.SetCurrentHpInternal(enemyHp);
                });
        }
        return (await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(
            PublicContextProfile: context ? PublicRunContext.Version : null), "contextual-reviewed-cycle", 0, Lifecycle))!;
    }

    private sealed class ConstructedSource(NativeRunWorld world) : ITeacherSource
    {
        public int StartHp => world.StartHp;
        public int StartMaxHp => world.StartMaxHp;
        public string?[] StartPotions => world.StartPotions;
        public DecisionPacket Observe() => world.Observe();
        public string PosteriorProfile => "constructed-fixed-native-cycle-fixture";
        public string PriorWarning => "Test-only deterministic mechanic fixture; no natural posterior claim";
        public Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts) => world.ForkForContinuationAsync();
        public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => null;
    }
}
