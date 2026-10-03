using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Xunit.Abstractions;

namespace Nosl.Tests;

/// <summary>Declared constructed native-mechanics acceptance fixtures, not natural training sources.</summary>
public sealed class NativePreferenceAcceptanceTests(ITestOutputHelper output)
{
    private sealed record Step(DecisionPacket Before, PublicAction Action, DecisionPacket After);
    private sealed record Arm(RolloutOutcome Outcome, Step[] Trace, int ExtraCardRewards, int? HuntFatalPlayerTurn);
    private sealed record WeightedPair(string Seed, int HuntPosition, int WeightNumerator, int WeightDenominator, Arm Baseline, Arm Plan);
    private static DecisionPacket Public(CombatSession session) => PublicJson.Read<DecisionPacket>(PublicJson.Serialize(session.Observe()));
    private static string Snapshot(CombatSession session) => PublicJson.Serialize(new { Packet = session.Observe(), Rng = session.State.RunState.Rng.ToSerializable() });
    private static PublicAction Play(DecisionPacket packet, string id) => packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == id);
    private static PublicAction End(DecisionPacket packet) => packet.Actions.Single(a => a.Kind == "end_turn");
    private static async Task Play(CombatSession session, string id) => await session.StepAsync(Play(Public(session), id));

    [Fact]
    public async Task P03_SafeLongerBattleBeatsFasterFiveLossBattle()
    {
        var recipe = new Scenario(Seed: "native-preference-p03", Enemy: "TwigSlimeS", EnemyHp: 20, Hp: 60, MaxHp: 70,
            Deck: ["Footwork", "StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent"]);
        await using var source = await CombatSession.CreateAsync(recipe);
        await Play(source, "Footwork"); // Same legal prefix for both alternatives.
        var root = Public(source); var o = root.Observation!;
        Assert.Equal(1, o.Turn); Assert.Equal(2, o.Energy); Assert.Equal(0, o.DrawCount);
        Assert.Equal(5, Assert.Single(Assert.Single(o.Enemies).Intents).Damage);
        Assert.Equal(2, Assert.Single(o.Powers, p => p.Id == "DexterityPower").Amount);
        string original = Snapshot(source);
        await using var fast = await source.ForkForContinuationAsync();
        await using var safe = await source.ForkForContinuationAsync();
        Assert.Equal(Snapshot(fast), Snapshot(safe));
        var a = await Run(fast, "p03-faster-public-v1", packet =>
            packet.Actions.FirstOrDefault(x => x.Kind == "play" && packet.Observation!.Hand[x.Slot].Id == "StrikeSilent") ?? End(packet));
        Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(safe));
        var b = await Run(safe, "p03-safe-public-v1", packet =>
        {
            var current = packet.Observation!;
            return (current.Block < 5 ? packet.Actions.FirstOrDefault(x => x.Kind == "play" && current.Hand[x.Slot].Id == "DefendSilent") : null)
                ?? packet.Actions.FirstOrDefault(x => x.Kind == "play" && current.Hand[x.Slot].Id == "StrikeSilent") ?? End(packet);
        });
        Assert.Equal(original, Snapshot(source));
        AssertPlainOutcome(a, 55, 2); AssertPlainOutcome(b, 60, 3);
        Assert.True(ObjectiveEvaluator.Evaluate(b.Outcome).Cost < ObjectiveEvaluator.Evaluate(a.Outcome).Cost);
        Assert.True(b.Trace.Length > a.Trace.Length);
        Assert.All(b.Trace.Where(t => t.Action.Kind == "end_turn"), t => Assert.Equal(60, t.After.Observation!.Hp));
        await Evidence("p03", new { Scenario = recipe, PublicRoot = root, Faster = a, Safer = b,
            FasterCost = ObjectiveEvaluator.Evaluate(a.Outcome).Cost, SaferCost = ObjectiveEvaluator.Evaluate(b.Outcome).Cost });
    }

    [Fact]
    public async Task P04_WholeBattleFiveLossBeatsZeroNowThenTwelveLoss()
    {
        var recipe = new Scenario(Seed: "native-preference-p04", Enemy: "CubexConstruct", EnemyHp: 26, Hp: 60, MaxHp: 70,
            Deck: ["StrikeSilent", "StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent"]);
        await using var source = await CombatSession.CreateAsync(recipe);
        await source.StepAsync(End(Public(source))); // Native charge: initial block expires, strength rises to two.
        var root = Public(source); var o = root.Observation!;
        Assert.Equal(2, o.Turn); Assert.Equal(60, o.Hp); Assert.Equal(3, o.Energy); Assert.Equal(0, o.DrawCount);
        Assert.Equal(8, Assert.Single(o.Enemies).Intents.Single(i => i.Kind == "Attack").Damage);
        Assert.Equal(2, Assert.Single(Assert.Single(o.Enemies).Powers, p => p.Id == "StrengthPower").Amount);
        string original = Snapshot(source);
        await using var whole = await source.ForkForContinuationAsync();
        await using var immediate = await source.ForkForContinuationAsync();
        Assert.Equal(Snapshot(whole), Snapshot(immediate));
        var normal = new PublicRulePolicy();
        var a = await Run(whole, "p04-one-defense-then-public-rules-v1", packet =>
            packet.Observation!.Turn == 2 && packet.Observation.Block == 0 ? Play(packet, "DefendSilent") : normal.Choose(packet));
        Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(immediate));
        var b = await Run(immediate, "p04-full-first-defense-then-public-rules-v1", packet =>
            packet.Observation!.Turn == 2 && packet.Observation.Block < 10 ? Play(packet, "DefendSilent") : normal.Choose(packet));
        Assert.Equal(original, Snapshot(source));
        AssertPlainOutcome(a, 55, 3); AssertPlainOutcome(b, 48, 4);
        var firstA = a.Trace.First(t => t.Action.Kind == "end_turn");
        var firstB = b.Trace.First(t => t.Action.Kind == "end_turn");
        Assert.Equal(55, firstA.After.Observation!.Hp); Assert.Equal(60, firstB.After.Observation!.Hp);
        var later = b.Trace.Single(t => t.Before.Observation!.Turn == 3 && t.Action.Kind == "end_turn");
        Assert.Equal(8, Assert.Single(later.Before.Observation!.Enemies).Intents.Single(i => i.Kind == "Attack").Damage);
        Assert.Equal(4, Assert.Single(Assert.Single(later.Before.Observation.Enemies).Powers, p => p.Id == "StrengthPower").Amount);
        Assert.Equal(0, later.Before.Observation.Block); Assert.Equal(0, later.Before.Observation.Energy);
        Assert.Equal(48, later.After.Observation!.Hp);
        Assert.True(ObjectiveEvaluator.Evaluate(a.Outcome).Cost < ObjectiveEvaluator.Evaluate(b.Outcome).Cost);
        await Evidence("p04", new { Scenario = recipe, PublicRoot = root, WholeBattle = a, ImmediateDefense = b,
            WholeBattleCost = ObjectiveEvaluator.Evaluate(a.Outcome).Cost, ImmediateDefenseCost = ObjectiveEvaluator.Evaluate(b.Outcome).Cost });
    }

    [Fact]
    public async Task B02_ExactFivePositionLawAllowsAnEightLossNativeHuntWorld()
    {
        var recipe = new Scenario(Seed: "native-preference-b02-0", Enemy: "Nibbit", EnemyHp: 5, Hp: 60, MaxHp: 70,
            Deck: ["TheHunt", "Backflip", .. Enumerable.Repeat("StrikeSilent", 10)]);
        // Build one native witness per complete public draw-order class, before running
        // either policy. Seeds only instantiate classes; they never determine weights.
        var worlds = new Dictionary<int, CombatSession>();
        DecisionPacket? root = null;
        try
        {
            for (int index = 0; index < 2048 && worlds.Count < 5; index++)
            {
                var session = await CombatSession.CreateAsync(recipe with { Seed = "native-preference-b02-" + index });
                var packet = Public(session); var observation = packet.Observation!;
                bool matches = observation.Hand.Count(c => c.Id == "Backflip") == 1
                    && observation.Hand.Count(c => c.Id == "StrikeSilent") == 6
                    && observation.DrawCount == 5 && (root is null || PublicJson.Serialize(packet) == PublicJson.Serialize(root));
                if (!matches) { await session.DisposeAsync(); continue; }
                root ??= packet;
                int position = session.State.Players.Single().PlayerCombatState!.DrawPile.Cards
                    .Select((card, slot) => (card, slot)).Single(x => x.card.GetType().Name == "TheHunt").slot;
                if (!worlds.TryAdd(position, session)) await session.DisposeAsync();
            }
            Assert.NotNull(root); Assert.Equal(5, worlds.Count);
            var o = root!.Observation!;
            Assert.Equal(13, Assert.Single(o.Enemies).Intents.Single(i => i.Kind == "Attack").Damage);
            var law = BeliefSampler.EnumerateDrawPosterior(o);
            Assert.Equal(5, law.Count); Assert.All(law, item => Assert.Equal(.2, item.Probability, 12));
            Assert.Equal(1, law.Sum(item => item.Probability), 12);
            var anchor = FiniteHuntPolicy.Anchor(root);
            var rows = new List<WeightedPair>();
            foreach (var item in law)
            {
                int position = Array.FindIndex(item.Order, card => card.Id == "TheHunt");
                var world = worlds[position];
                Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
                Assert.Equal(item.Order.Select(PublicJson.Serialize), world.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(c => PublicJson.Serialize(PublicViews.Card(c))));
                string original = Snapshot(world);
                await using var baseline = await world.ForkForContinuationAsync();
                await using var plan = await world.ForkForContinuationAsync();
                Assert.Equal(Snapshot(baseline), Snapshot(plan));
                var normal = new PublicRulePolicy(); var hunt = new FiniteHuntPolicy(anchor);
                var a = await Run(baseline, normal.Id, normal.Choose);
                Assert.Equal(original, Snapshot(world)); Assert.Equal(original, Snapshot(plan));
                var b = await Run(plan, hunt.Id, hunt.Choose);
                Assert.Equal(original, Snapshot(world));
                AssertPlainOutcome(a, 60, 1);
                Assert.Equal(TerminalKind.Win, b.Outcome.TerminalKind); Assert.True(b.Outcome.SettlementComplete);
                Assert.Equal(1, b.ExtraCardRewards); Assert.NotNull(b.HuntFatalPlayerTurn);
                Assert.Equal(anchor, hunt.Controller.Anchor);
                Assert.Equal(1, b.Trace.Count(t => t.Action.Kind == "play" && t.Before.Observation!.Hand[t.Action.Slot].Id == "TheHunt"));
                Assert.Equal("Backflip", b.Trace[0].Before.Observation!.Hand[b.Trace[0].Action.Slot].Id);
                Assert.InRange(b.Outcome.PlayerTurnsElapsed, anchor.StartPlayerTurn, anchor.DeadlinePlayerTurn);
                Assert.Contains(b.Outcome.PermanentChanges, change => change.Kind == "earned_extra_reward_opportunity:CardReward" && change.Amount == 1);
                Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, ObjectiveEvaluator.Evaluate(b.Outcome).Status);
                Assert.Equal(position < 2 ? 60 : 52, b.Outcome.HpAfterSettlement);
                Assert.Equal(position < 2 ? 1 : 2, b.Outcome.PlayerTurnsElapsed);
                rows.Add(new(world.InitialScenario.Seed, position, 1, law.Count, a, b));
            }
            double expectedExtraLoss = (double)rows.Sum(row => row.WeightNumerator * (row.Baseline.Outcome.HpAfterSettlement!.Value - row.Plan.Outcome.HpAfterSettlement!.Value)) / law.Count;
            double success = (double)rows.Sum(row => row.WeightNumerator * (row.Plan.ExtraCardRewards == 1 && row.Plan.Outcome.PlayerAlive == true
                && row.Plan.HuntFatalPlayerTurn is int turn && turn <= anchor.DeadlinePlayerTurn ? 1 : 0)) / law.Count;
            Assert.Equal(4.8, expectedExtraLoss, 12); Assert.Equal(1, success, 12);
            Assert.Contains(rows, row => row.Baseline.Outcome.HpAfterSettlement - row.Plan.Outcome.HpAfterSettlement > 5);
            bool safetyAcceptable = rows.All(row => row.Baseline.Outcome.PlayerAlive == true && row.Plan.Outcome.PlayerAlive == true);
            var contract = AnchoredBonusPlan.Begin(anchor.PublicSummary, anchor.Goal, anchor.TemplateId, anchor.BaselinePolicyId, anchor.StartPlayerTurn);
            var eligibility = contract.Evaluate(new(anchor.PublicSummary, anchor.BaselinePolicyId, anchor.TemplateId, true,
                EstimateInterval.Exact(expectedExtraLoss), EstimateInterval.Exact(success), safetyAcceptable));
            Assert.Equal(Eligibility.EligibleNotMandatory, eligibility);
            await Evidence("b02", new { Scenario = recipe, PublicRoot = root, Anchor = anchor, ExactLaw = "declared exchangeable five-position draw law; one native witness per class",
                ExpectedExtraLoss = expectedExtraLoss, UnconditionalSuccess = success, SafetyAcceptable = safetyAcceptable, Eligibility = eligibility, Worlds = rows });
        }
        finally { foreach (var world in worlds.Values) await world.DisposeAsync(); }
    }

    private static void AssertPlainOutcome(Arm arm, int finalHp, int lastTurn)
    {
        var o = arm.Outcome;
        Assert.Equal(TerminalKind.Win, o.TerminalKind); Assert.True(o.PlayerAlive); Assert.True(o.SettlementComplete);
        Assert.Equal(60, o.HpAtCombatStart); Assert.Equal(finalHp, o.HpAfterSettlement); Assert.Equal(lastTurn, o.PlayerTurnsElapsed);
        Assert.True(o.HpEventDiagnosticsComplete); Assert.Equal(60 - finalHp, o.CumulativeHpDamage);
        Assert.Equal(0, o.HealingReceived); Assert.Equal(0, o.OtherHpAdjustment);
        Assert.Empty(o.InventoryStart); Assert.Empty(o.InventoryEnd); Assert.Empty(o.ResourceEvents); Assert.Empty(o.PermanentChanges);
        Assert.Equal(EvaluationStatus.Scored, ObjectiveEvaluator.Evaluate(o).Status);
    }

    private static async Task<Arm> Run(CombatSession branch, string policyId, Func<DecisionPacket, PublicAction> choose)
    {
        var trace = new List<Step>(); var packet = Public(branch);
        int eventCursor = packet.Observation!.History.Length; int? huntFatalTurn = null;
        while (packet.Status == "player_decision" && trace.Count < 32)
        {
            var action = choose(packet);
            Assert.Contains(action, packet.Actions);
            await branch.StepAsync(action);
            var next = Public(branch);
            var events = next.Observation?.History ?? (await branch.SettleAsync()).Events;
            if (events.Skip(eventCursor).Any(IsHuntFatal)) huntFatalTurn ??= packet.Observation!.Turn;
            eventCursor = events.Length;
            trace.Add(new(packet, action, next)); packet = next;
        }
        Assert.Equal("terminal_settled", packet.Status);
        var facts = await branch.SettleAsync(); Assert.Equal(0, facts.RewardSelectionsMade);
        return new(RolloutRecorder.Settled(branch, facts, policyId, trace[^1].Before.Observation!.Turn), trace.ToArray(),
            branch.Room.GeneratedRewards.SelectMany(r => r.ExtraRewards).Count(r => r.GetType().Name == "CardReward"), huntFatalTurn);
    }

    private static bool IsHuntFatal(PublicEvent e)
    {
        if (e.Kind != "power_changed") return false;
        using var json = JsonDocument.Parse(e.Detail);
        return json.RootElement.GetProperty("id").GetString() == "TheHuntPower"
            && json.RootElement.GetProperty("amount").GetDecimal() > 0;
    }

    private async Task Evidence(string name, object value)
    {
        string json = PublicJson.Serialize(value);
        output.WriteLine(json);
        string? directory = Environment.GetEnvironmentVariable("NOSL_NATIVE_PREFERENCE_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), json);
    }
}
