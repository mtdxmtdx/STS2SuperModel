using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class ReviewedPublicRuleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EnoughNativeBlockStopsFinesseAndPlaysUsefulNonAttackBeforeSettledWin()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Finesse", "Finesse", "BladeDance"], EnemyHp: 12));
        string sourcePacket = PublicJson.Serialize(source.Observe());
        await using var branch = await source.ForkForContinuationAsync();
        var policy = new ReviewedPublicRulePolicy();
        var packet = branch.Observe();
        for (int i = 0; i < 2; i++)
        {
            var action = policy.Choose(packet);
            Assert.Equal("Finesse", packet.Observation!.Hand[action.Slot].Id);
            packet = await branch.StepAsync(action);
        }
        Assert.Equal(8m, packet.Observation!.Block);
        Assert.Equal(5, packet.Observation.Enemies.Single().Intents.Single().Damage);
        string unchanged = PublicJson.Serialize(packet);
        var useful = policy.Choose(packet);
        Assert.Equal("BladeDance", packet.Observation.Hand[useful.Slot].Id);
        Assert.Contains(useful, packet.Actions);
        Assert.Equal(unchanged, PublicJson.Serialize(packet));
        Assert.Equal("Finesse", packet.Observation.Hand[new PublicRulePolicy().Choose(packet).Slot].Id);
        packet = await branch.StepAsync(useful);
        for (int i = 0; i < 4 && packet.Status == "player_decision"; i++)
            packet = await branch.StepAsync(policy.Choose(packet));
        Assert.Equal("terminal_settled", packet.Status);
        var facts = await branch.SettleAsync();
        Assert.Equal("win", facts.Result);
        Assert.Equal(70, facts.FinalHp);
        Assert.Equal(0, facts.RewardSelectionsMade);
        Assert.Equal(sourcePacket, PublicJson.Serialize(source.Observe()));
        output.WriteLine("Native block 8 >= incoming 5; v2 played BladeDance, then three native Shivs settled a full-HP win; v1 selected more Finesse.");
    }

    [Fact]
    public async Task ImpatienceStillDrawsFinesseWhenMoreNativeBlockIsUseful()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Finesse", "Impatience", "BladeDance"], EnemyHp: 20));
        var packet = source.Observe();
        packet = await source.StepAsync(packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == "Finesse"));
        Assert.Equal(4m, packet.Observation!.Block);
        Assert.Contains(packet.Observation.Discard, c => c.Id == "Finesse");
        var policy = new ReviewedPublicRulePolicy();
        var draw = policy.Choose(packet);
        Assert.Equal("Impatience", packet.Observation.Hand[draw.Slot].Id);
        packet = await source.StepAsync(draw);
        var defense = policy.Choose(packet);
        Assert.Equal("Finesse", packet.Observation!.Hand[defense.Slot].Id);
        packet = await source.StepAsync(defense);
        Assert.Equal(8m, packet.Observation!.Block);
        Assert.Equal("BladeDance", packet.Observation.Hand[policy.Choose(packet).Slot].Id);
    }

    [Theory]
    [InlineData("Finesse")]
    [InlineData("Impatience")]
    public async Task ReviewedClosedFamilyExitsByLegalEndTurnsAndOnlyActualSettlementIsLoss(string card)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: [card, card], EnemyHp: 65));
        string original = Snapshot(source);
        await using var branch = await source.ForkForContinuationAsync();
        var policy = new ReviewedPublicRulePolicy();
        var packet = branch.Observe();
        Assert.Equal("play", new PublicRulePolicy().Choose(packet).Kind);
        int actions = 0;
        while (packet.Status == "player_decision" && actions < 32)
        {
            var action = policy.Choose(packet);
            Assert.Equal("end_turn", action.Kind);
            Assert.Contains(action, packet.Actions);
            packet = await branch.StepAsync(action); actions++;
        }
        Assert.Equal("terminal_settled", packet.Status);
        var facts = await branch.SettleAsync();
        Assert.Equal("loss", facts.Result);
        Assert.Equal(0, facts.FinalHp);
        Assert.Equal(14, actions);
        Assert.Equal(PublicJson.Serialize(facts), PublicJson.Serialize(await branch.SettleAsync()));
        var outcome = RolloutRecorder.Settled(branch, facts, policy.Id, actions);
        Assert.Equal(TerminalKind.Loss, outcome.TerminalKind);
        Assert.True(outcome.IsTrueTerminal && outcome.SettlementComplete);
        Assert.Equal(EvaluationStatus.Scored, ObjectiveEvaluator.Evaluate(outcome).Status);

        ulong[] seeds = [101, 102];
        var options = new TeacherOptions { ContinuationPolicyId = policy.Id, EvaluationSeeds = seeds, MaxDecisions = 32 };
        var result = await CombatTeacher.EvaluateAsync(source, options);
        Assert.Equal(source.Observe().Actions.Select(PublicJson.Serialize), result.Candidates.Select(c => PublicJson.Serialize(c.Action)));
        Assert.Equal(6, result.Costs.WorldsAllocated);
        Assert.Equal(6, result.Costs.WorldsCompleted);
        Assert.All(result.Candidates, c => Assert.All(c.Outcomes, o =>
        {
            Assert.Equal(TerminalKind.Loss, o.TerminalKind);
            Assert.True(o.SettlementComplete);
            Assert.Equal(0, o.HpAfterSettlement);
            Assert.Equal(policy.Id, o.ContinuationPolicyId);
        }));
        var truncated = await CombatTeacher.EvaluateAsync(source, options with { MaxDecisions = 2 });
        Assert.All(truncated.Candidates, c =>
        {
            Assert.Null(c.Evaluation.ExpectedCost);
            Assert.All(c.Outcomes, o =>
            {
                Assert.Equal(TerminalKind.ComputeTruncated, o.TerminalKind);
                Assert.False(o.IsTrueTerminal || o.SettlementComplete);
                Assert.Null(o.PlayerAlive);
            });
        });
        using var record = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "v2-run", "v2-combat", "v2-family", seeds, [])));
        Assert.Equal(PublicContinuationPolicies.ReviewedDatasetVersion, record.RootElement.GetProperty("audit_only").GetProperty("dataset_version").GetString());
        Assert.Equal(original, Snapshot(source));
        output.WriteLine($"{card} x2: {actions} native end turns settled loss; teacher completed all six candidate/world records; budget 2 remained truncated.");
    }

    [Theory]
    [InlineData("FlashOfSteel", 13)]
    [InlineData("FlashOfSteel+", 9)]
    public async Task ProfitableNativeAttackDrawCycleRemainsWinningAndUnpenalized(string card, int plays)
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: [card, card], EnemyHp: 65));
        var policy = new ReviewedPublicRulePolicy();
        var packet = source.Observe();
        int actions = 0;
        while (packet.Status == "player_decision" && actions < 64)
        {
            var action = policy.Choose(packet);
            Assert.Equal("play", action.Kind);
            Assert.Equal(PublicJson.Serialize(new PublicRulePolicy().Choose(packet)), PublicJson.Serialize(action));
            packet = await source.StepAsync(action); actions++;
        }
        Assert.Equal("terminal_settled", packet.Status);
        var facts = await source.SettleAsync();
        Assert.Equal("win", facts.Result);
        Assert.Equal(70, facts.FinalHp);
        Assert.Equal(plays, actions);
        Assert.Equal(0d, ObjectiveEvaluator.Evaluate(RolloutRecorder.Settled(source, facts, policy.Id, 1)).Cost);
        output.WriteLine($"{card} x2: {actions} atomic plays, settled win, cost 0.");
    }

    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    public async Task HiddenDrawOrderAndFutureReplacementDoNotChangePublicPolicyOrTeacher(string mode)
    {
        // Find an ordinary opening with the two distinct cards still hidden. All
        // setup attempts are native; nothing is moved to manufacture public knowledge.
        CombatSession? found = null;
        for (int i = 0; i < 32; i++)
        {
            var trial = await CombatSession.CreateAsync(new(Seed: "v2-hidden-" + i,
                Deck: Enumerable.Repeat("Finesse", 8).Append("Impatience").ToArray(), EnemyHp: 65));
            if (trial.Observe().Observation!.UnknownDraw.Length == 2) { found = trial; break; }
            await trial.DisposeAsync();
        }
        Assert.NotNull(found);
        await using var source = found!;
        CombatSession? replacement = null;
        var sourceOrder = source.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(c => c.GetType().Name).ToArray();
        for (int i = 32; i < 128; i++)
        {
            var trial = await CombatSession.CreateAsync(source.InitialScenario with { Seed = "v2-hidden-" + i });
            if (PublicJson.Serialize(trial.Observe()) == PublicJson.Serialize(source.Observe())
                && !sourceOrder.SequenceEqual(trial.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(c => c.GetType().Name)))
            { replacement = trial; break; }
            await trial.DisposeAsync();
        }
        Assert.NotNull(replacement);
        await using var replaced = replacement!;
        Assert.NotEqual(PublicJson.Serialize(source.State.RunState.Rng.ToSerializable()),
            PublicJson.Serialize(replaced.State.RunState.Rng.ToSerializable()));
        Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(replaced.Observe()));
        var policy = new ReviewedPublicRulePolicy();
        Assert.Equal(PublicJson.Serialize(policy.Choose(source.Observe())), PublicJson.Serialize(policy.Choose(replaced.Observe())));
        string original = Snapshot(source), replacedOriginal = Snapshot(replaced);
        var options = new TeacherOptions { Mode = mode, ContinuationPolicyId = policy.Id,
            ExplorationSeeds = mode == "T1" ? [7] : [], EvaluationSeeds = [101], TreeDepth = 2, MaxDecisions = 32 };
        var first = await CombatTeacher.EvaluateAsync(source, options);
        var second = await CombatTeacher.EvaluateAsync(replaced, options);
        Assert.Equal(PublicJson.Serialize(first.Candidates), PublicJson.Serialize(second.Candidates));
        Assert.Equal(first.ContinuationVersion, second.ContinuationVersion);
        Assert.Equal(source.Observe().Actions.Length, first.Candidates.Length);
        Assert.Equal(first.Costs.WorldsAllocated, first.Costs.WorldsCompleted);
        Assert.Equal(mode == "T0" ? policy.Id : PublicContinuationPolicies.ReviewedTreeId,
            first.ContinuationVersion.Split(':')[0]);
        Assert.Equal(PublicContinuationPolicies.ReviewedDatasetVersion, PublicContinuationPolicies.DatasetVersion(first.ContinuationVersion));
        Assert.Equal(original, Snapshot(source));
        Assert.Equal(replacedOriginal, Snapshot(replaced));
        output.WriteLine($"{mode}: distinct hidden draw order plus future RNG replacement preserved every candidate outcome and frozen identity; sources unchanged.");
    }

    [Fact]
    public async Task UnreviewedEffectsOrUnidentifiedDrawFailClosedAndNativeChoicesRemainLegal()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Finesse", "Finesse"], EnemyHp: 65));
        var packet = source.Observe(); var o = packet.Observation!;
        var variants = new[]
        {
            o with { UnidentifiedDrawCount = 1 },
            o with { DrawCount = 1 },
            o with { Powers = [new("DexterityPower", 1)] },
            o with { Relics = ["RingOfTheSnake", "LetterOpener"] },
            o with { Potions = ["FirePotion"] },
            o with { Enemies = [o.Enemies[0] with { Powers = [new("StrengthPower", 1)] }] },
            o with { Enemies = [o.Enemies[0] with { Intents = [new("Attack", 6, 1)] }] },
            o with { Hand = o.Hand.Select(c => c with { Enchantments = [new("Momentum", 1)] }).ToArray() },
            o with { Hand = o.Hand.Select(c => c with { Affliction = new("Sapping", 1) }).ToArray() },
            o with { Hand = o.Hand.Select(c => c with { Details = null }).ToArray() },
        };
        var policy = new ReviewedPublicRulePolicy();
        foreach (var variant in variants)
        {
            var changed = packet with { Observation = variant };
            Assert.Equal(PublicJson.Serialize(new PublicRulePolicy().Choose(changed)), PublicJson.Serialize(policy.Choose(changed)));
        }
        // A useful hidden draw must remain available to the continuation.
        await using var mixed = await CombatSession.CreateAsync(new(Seed: "v2-useful-draw",
            Deck: ["Finesse", "Finesse", "BladeDance"], EnemyHp: 12));
        var useful = mixed.Observe().Observation!.Hand.Single(c => c.Id == "BladeDance");
        var potentialDraw = packet with { Observation = o with { DrawCount = 1, UnknownDraw = [new(useful, 1)], Block = 8 } };
        Assert.Equal("play", policy.Choose(potentialDraw).Kind);

        await using var choice = await CombatSession.CreateAsync(new(Deck: ["Survivor", "Finesse", "Impatience"]));
        var before = choice.Observe();
        var survivor = before.Actions.First(a => a.Kind == "play" && before.Observation!.Hand[a.Slot].Id == "Survivor");
        var choicePacket = await choice.StepAsync(survivor);
        Assert.Equal("card_choice", choicePacket.Status);
        string unchanged = PublicJson.Serialize(choicePacket);
        var selected = policy.Choose(choicePacket);
        Assert.Equal("choose", selected.Kind);
        Assert.Contains(selected, choicePacket.Actions);
        Assert.Equal(PublicJson.Serialize(new PublicRulePolicy().Choose(choicePacket)), PublicJson.Serialize(selected));
        Assert.Equal(unchanged, PublicJson.Serialize(choicePacket));
        Assert.Equal("player_decision", (await choice.StepAsync(selected)).Status);
        Assert.Throws<ArgumentException>(() => new TeacherOptions { ContinuationPolicyId = "typo" }.Validate());
        Assert.Throws<ArgumentException>(() => PublicContinuationPolicies.DatasetVersion("typo"));
        Assert.IsType<PublicRulePolicy>(PublicContinuationPolicies.Create(new TeacherOptions().ContinuationPolicyId));
    }

    [Fact]
    public async Task NativeSourceSelectionIsExplicitAndDefaultRemainsV1()
    {
        var options = new NaturalSourceOptions(MaxRoots: 1, MaxFloors: 1, MaxDecisionsPerRun: 100,
            ContinuationPolicyId: PublicContinuationPolicies.ReviewedId);
        var report = await NaturalSourceCollector.CollectAsync(options);
        Assert.Single(report.Roots);
        Assert.Equal(PublicContinuationPolicies.ReviewedId, report.Roots[0].CombatPolicy);
        Assert.Equal(PublicContinuationPolicies.LegacyId, new NaturalSourceOptions().ContinuationPolicyId);
        await Assert.ThrowsAsync<ArgumentException>(() => NaturalSourceCollector.CollectAsync(
            options with { ContinuationPolicyId = "typo" }, new PublicRulePolicy()));
    }

    [Fact]
    public async Task StandaloneWorkerAdvertisesPoliciesBeforeResetAndRejectsUnknownSelection()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{typeof(CombatSession).Assembly.Location}\"")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.StandardInput.WriteLineAsync("{\"op\":\"continuation_policies\"}");
        await process.StandardInput.WriteLineAsync("{\"op\":\"reset\",\"scenario\":{\"deck\":[\"Finesse\",\"Finesse\"]}}");
        await process.StandardInput.WriteLineAsync("{\"op\":\"continue\",\"continuationPolicyId\":\"typo\"}");
        process.StandardInput.Close();
        using var capabilities = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync())!);
        Assert.Equal("nosl.continuation-policies.v1", capabilities.RootElement.GetProperty("version").GetString());
        Assert.Equal(new[] { PublicContinuationPolicies.LegacyId, PublicContinuationPolicies.ReviewedId },
            capabilities.RootElement.GetProperty("supportedPolicyIds").EnumerateArray().Select(x => x.GetString()));
        using var reset = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync())!);
        Assert.Equal("player_decision", reset.RootElement.GetProperty("status").GetString());
        using var rejected = JsonDocument.Parse((await process.StandardOutput.ReadLineAsync())!);
        Assert.Equal("invalid_operation", rejected.RootElement.GetProperty("status").GetString());
        Assert.Contains("Unknown public continuation policy", rejected.RootElement.GetProperty("message").GetString());
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static string Snapshot(CombatSession source) => PublicJson.Serialize(new
    {
        packet = source.Observe(), runRng = source.State.RunState.Rng.ToSerializable(),
        playerRng = source.State.Players[0].PlayerRng.ToSerializable(),
        assets = CombatAssetSnapshot.Capture(source.State.Players[0]),
    });
}
