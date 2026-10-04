using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class FiniteRegenTests
{
    private static async Task<CombatSession> Source(int hp = 50, bool potion = true, string seed = "finite-regen-fixture")
    {
        var source = await CombatSession.CreateAsync(new(Seed: seed, Deck: ["Finesse", "Finesse", "StrikeSilent"],
            EnemyHp: 6, Hp: hp, MaxHp: 70, Potions: potion ? ["RegenPotion"] : []));
        if (potion) await source.StepAsync(source.Observe().Actions.First(a => a.Kind == "potion"));
        return source;
    }

    [Fact]
    public async Task NativeHealingStallsFiveTurnsThenFinishesAtImmutableAnchor()
    {
        await using var source = await Source();
        string original = PublicJson.Serialize(source.Observe());
        var result = await AnchoredRegenEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [401, 402] });
        Assert.Equal(original, PublicJson.Serialize(source.Observe()));
        Assert.Equal(1, result.Anchor.StartPlayerTurn); Assert.Equal(6, result.Anchor.DeadlinePlayerTurn);
        Assert.Equal(-15, result.MeanExtraNetHpLoss); Assert.True(result.SettledHpComparisonMask);
        Assert.False(result.AbsoluteUtilityComparisonMask); Assert.Equal(Eligibility.Unresolved, result.Eligibility);
        Assert.All(result.Worlds, pair =>
        {
            Assert.Equal(50, pair.Baseline.Outcome.HpAfterSettlement); Assert.Equal(65, pair.Plan.Outcome.HpAfterSettlement);
            Assert.Equal(50, pair.Plan.Outcome.HpAtCombatStart); Assert.Equal(-15, pair.Plan.Objective.NetHpLoss);
            Assert.Equal(15, pair.Plan.Outcome.HealingReceived); Assert.Equal(0, pair.Plan.Outcome.CumulativeHpDamage);
            Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, pair.Plan.Objective.Status);
            Assert.Equal(new InventoryQuantity("RegenPotion", 1), Assert.Single(pair.Plan.Outcome.InventoryStart));
            Assert.Empty(pair.Plan.Outcome.InventoryEnd); Assert.Null(pair.Plan.Outcome.SpecifiedFinishSuccess);
            Assert.Equal("finished", pair.Plan.Controller.Status); Assert.Equal("regen_exhausted", pair.Plan.Controller.ExitReason);
            Assert.Equal(result.Anchor, pair.Plan.Controller.Anchor);
            Assert.Equal(new[] { 5, 4, 3, 2, 1 }, pair.Plan.PublicTrace.Where(t => t.Action.Kind == "end_turn").Select(t => t.Regen));
            Assert.All(pair.Plan.PublicTrace.Where(t => t.Action.Kind == "end_turn"), t => Assert.True(t.Block >= 5));
            Assert.All(pair.Plan.PublicTrace, t => Assert.Equal(6, t.FixedDeadlinePlayerTurn));
            Assert.Equal("play", pair.Plan.PublicTrace.Last().Action.Kind);
        });
        var export = Environment.GetEnvironmentVariable("NOSL_REGEN_FIXTURE_PATH");
        if (export is not null)
        {
            Directory.CreateDirectory(export);
            await File.WriteAllTextAsync(Path.Combine(export, "finite-regen-record-v2.jsonl"), PublicJson.Serialize(FiniteRegenDataset.Record(result)) + "\n");
            var packet = source.Observe(); var policy = new FiniteRegenPolicy(result.Anchor); var publics = new List<object>();
            while (packet.Status == "player_decision")
            {
                var action = policy.Choose(packet);
                if (publics.Count == 0 || packet.Observation!.Turn == 2 && publics.Count == 1 || policy.Controller.Status != "active")
                    publics.Add(RegenStudentContext.Input(result.Anchor, packet, policy.Controller));
                packet = await source.StepAsync(action);
            }
            await File.WriteAllLinesAsync(Path.Combine(export, "finite-regen-public-v2.jsonl"), publics.Select(PublicJson.Serialize));
            var settled = await source.SettleAsync();
            await File.WriteAllTextAsync(Path.Combine(export, "finite-regen-terminal-v2.json"), PublicJson.Serialize(new
            {
                decision_status = "terminal", terminal_public = new { result = settled.Result, events = settled.Events,
                    final_hp = settled.FinalHp, final_max_hp = settled.FinalMaxHp }
            }) + "\n");
        }
    }

    [Theory]
    [InlineData(68, true, 1, "finished", "full_hp", 70)]
    [InlineData(70, true, 0, "aborted", "full_hp", 70)]
    [InlineData(50, false, 0, "aborted", "regen_exhausted", 50)]
    public async Task FullHpAndNoRemainingBenefitReturnImmediatelyToFrozenBaseline(int hp, bool potion,
        int endTurns, string status, string reason, int finalHp)
    {
        await using var source = await Source(hp, potion);
        var result = await AnchoredRegenEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [403] });
        var plan = Assert.Single(result.Worlds).Plan;
        Assert.Equal(finalHp, plan.Outcome.HpAfterSettlement); Assert.Equal(status, plan.Controller.Status);
        Assert.Equal(reason, plan.Controller.ExitReason); Assert.Equal(endTurns, plan.PublicTrace.Count(t => t.Action.Kind == "end_turn"));
    }

    [Fact]
    public async Task ScopeAndDefenseFailureAbortPermanentlyWithoutResettingDeadline()
    {
        await using var source = await Source();
        var root = source.Observe(); var anchor = FiniteRegenPolicy.Anchor(root);
        foreach (var packet in new[]
        {
            root with { Observation = root.Observation! with { Enemies = [root.Observation.Enemies[0] with { Intents = [new("Attack", 6, 1)] }] } },
            root with { Actions = root.Actions.Where(a => a.Kind != "play" || root.Observation!.Hand[a.Slot].Id != "Finesse").ToArray() },
        })
        {
            var controller = new FiniteRegenPolicy(anchor);
            Assert.Equal(new PublicRulePolicy().Choose(packet), controller.Choose(packet));
            Assert.Equal("aborted", controller.Controller.Status);
            Assert.Contains(controller.Controller.ExitReason, new[] { "reviewed_safety_scope_failed", "no_safe_defense" });
            Assert.Equal(new PublicRulePolicy().Choose(root), controller.Choose(root));
            Assert.Equal(anchor, controller.Controller.Anchor);
        }
        Assert.Throws<ArgumentException>(() => new FiniteRegenPolicy(anchor with { DeadlinePlayerTurn = 7 }));
        var expired = root with { Observation = root.Observation! with { Turn = 7,
            History = root.Observation.History.Append(new PublicEvent("player_turn", "7")).ToArray() } };
        var bounded = new FiniteRegenPolicy(anchor);
        Assert.Equal(new PublicRulePolicy().Choose(expired), bounded.Choose(expired));
        Assert.Equal("fixed_deadline_expired", bounded.Controller.ExitReason); Assert.Equal(6, bounded.Controller.Anchor.DeadlinePlayerTurn);
        var historyController = new FiniteRegenPolicy(anchor);
        await source.StepAsync(historyController.Choose(root));
        historyController.Choose(source.Observe());
        historyController.Choose(root);
        Assert.Equal("unresolved", historyController.Controller.Status); Assert.Equal("public_history_incomplete", historyController.Controller.ExitReason);
        var fabricated = root with { Observation = root.Observation! with { Hp = 65, Powers = [] } };
        var factsController = new FiniteRegenPolicy(anchor);
        factsController.Choose(fabricated);
        Assert.Equal("unresolved", factsController.Controller.Status);
        Assert.Equal("public_history_incomplete", factsController.Controller.ExitReason);
        Assert.Throws<ArgumentException>(() => RegenStudentContext.Input(anchor, fabricated, new(anchor, "finished", "regen_exhausted", 1)));
        Assert.Throws<NotSupportedException>(() => FiniteRegenPolicy.Anchor(root with { Observation = root.Observation with { Turn = 2 } }));
    }

    [Fact]
    public async Task OriginalCombatHpAndUnknownMassAreRetained()
    {
        await using var source = await Source();
        var initial = FiniteRegenPolicy.Anchor(source.Observe()); var controller = new FiniteRegenPolicy(initial);
        while (source.Observe().Observation!.Turn == 1) await source.StepAsync(controller.Choose(source.Observe()));
        Assert.Equal(55, source.Observe().Observation!.Hp); Assert.Equal(50, source.StartHp);
        var observed = source.Observe(); var original = PublicJson.Read<DecisionPacket>(initial.PublicSummary).Observation!;
        var rewound = observed with { Observation = observed.Observation! with { Hp = original.Hp, Powers = original.Powers } };
        var rewindController = new FiniteRegenPolicy(initial);
        rewindController.Choose(rewound);
        Assert.Equal("unresolved", rewindController.Controller.Status);
        Assert.Equal("public_history_incomplete", rewindController.Controller.ExitReason);
        var result = await AnchoredRegenEvaluator.EvaluateAsync(source, new() { EvaluationSeeds = [404], MaxDecisionsPerPolicy = 1 });
        Assert.Equal(2, result.Anchor.StartPlayerTurn); Assert.Equal(6, result.Anchor.DeadlinePlayerTurn);
        Assert.Null(result.MeanExtraNetHpLoss); Assert.False(result.SettledHpComparisonMask);
        var plan = Assert.Single(result.Worlds).Plan;
        Assert.Equal(50, plan.Outcome.HpAtCombatStart); Assert.Null(plan.Outcome.HpAfterSettlement);
        Assert.Equal(TerminalKind.ComputeTruncated, plan.Outcome.TerminalKind); Assert.Equal("unresolved", plan.Controller.Status);
        Assert.Equal(0, result.PlanSummary.Losses); Assert.Equal(1, result.PlanSummary.ComputeTruncated);
        Assert.Equal(new EstimateInterval(-70, 70), result.ExtraExpectedLossInterval);
    }

    [Fact]
    public async Task HiddenSourceReplacementCannotChangeEvaluationAtEqualPublicAnchor()
    {
        await using var a = await Source(seed: "regen-hidden-A");
        // Select an equal public hand order using independently declared seeds; no hidden order is read or installed.
        CombatSession? b = null;
        try
        {
            for (int i = 0; i < 32; i++)
            {
                var candidate = await Source(seed: "regen-hidden-B-" + i);
                if (PublicJson.Serialize(candidate.Observe()) == PublicJson.Serialize(a.Observe())) { b = candidate; break; }
                await candidate.DisposeAsync();
            }
            Assert.NotNull(b);
            Assert.NotEqual(PublicJson.Serialize(a.State.RunState.Rng.ToSerializable()), PublicJson.Serialize(b!.State.RunState.Rng.ToSerializable()));
            var options = new RegenEvaluationOptions { EvaluationSeeds = [405, 406] };
            Assert.Equal(PublicJson.Serialize(await AnchoredRegenEvaluator.EvaluateAsync(a, options)),
                PublicJson.Serialize(await AnchoredRegenEvaluator.EvaluateAsync(b, options)));
        }
        finally { if (b is not null) await b.DisposeAsync(); }
    }

    [Fact]
    public void FiniteBenefitGateHasNoHuntWindowBudgetOrSuccessThreshold()
    {
        var plan = AnchoredBonusPlan.Begin("anchor", "healing", "finite-regen", "baseline", 1, BonusPlanKind.SafeFiniteStall, 6);
        Assert.Equal(Eligibility.EligibleNotMandatory, plan.EvaluateFiniteStall(new FiniteStallEvidence("anchor", "baseline", "finite-regen", true, new(-15, -14), true)));
        Assert.Equal(Eligibility.Ineligible, PreferenceGates.SafeFiniteStall(EstimateInterval.Exact(0), true));
        Assert.Equal(Eligibility.Ineligible, PreferenceGates.SafeFiniteStall(EstimateInterval.Exact(-15), false));
        Assert.Equal(Eligibility.Unresolved, PreferenceGates.SafeFiniteStall(new(-15, 1), true));
        Assert.Throws<ArgumentException>(() => plan.EvaluateFiniteStall(new FiniteStallEvidence("new-anchor", "baseline", "finite-regen", true, new(-15, -14), true)));
    }
}
