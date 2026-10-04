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
    private sealed record RiskWorld(string[] DrawOrder, int WeightNumerator, int WeightDenominator, Arm Lottery, Arm CertainEight, Arm CertainThree);
    private sealed record BoundaryWorld(int HuntPosition, int WeightNumerator, int WeightDenominator, Arm Baseline, Arm Plan, PublicControllerContext FinalController);
    private sealed record WeightedPair(string Seed, int HuntPosition, int WeightNumerator, int WeightDenominator, Arm Baseline, Arm Plan);
    private static DecisionPacket Public(CombatSession session) => PublicJson.Read<DecisionPacket>(PublicJson.Serialize(session.Observe()));
    private static string Snapshot(CombatSession session) => PublicJson.Serialize(new
    {
        Packet = session.Observe(), RunRng = session.State.RunState.Rng.ToSerializable(),
        PlayerRng = session.State.Players.Single().PlayerRng.ToSerializable().Rngs,
        MonsterRng = session.State.Enemies.Select(enemy => enemy.Monster!.Rng.ToSerializable()).ToArray(),
    });
    private static PublicAction Play(DecisionPacket packet, string id) => packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == id);
    private static PublicAction End(DecisionPacket packet) => packet.Actions.Single(a => a.Kind == "end_turn");
    private static async Task Play(CombatSession session, string id) => await session.StepAsync(Play(Public(session), id));

    [Fact]
    public async Task P01_P02_ExactTenOrderLawScoresNativeLotteryAndCertainLosses()
    {
        var recipe = new Scenario(Seed: "native-preference-p01-p02-0", Enemy: "MechaKnight", EnemyHp: 5, Hp: 60, MaxHp: 70,
            Deck: ["Prepared+", "Deflect+", "Deflect+", "DefendSilent+", "DefendSilent",
                "Wound", "Wound", "Wound", "Wound", "StrikeSilent", "StrikeSilent", "StrikeSilent"]);
        CombatSession? selected = null;
        // Select only the public opening composition, before inspecting any hidden order
        // or executing any compared policy. The first matching native setup is retained.
        for (int index = 0; index < 2048; index++)
        {
            var candidate = await CombatSession.CreateAsync(recipe with { Seed = "native-preference-p01-p02-" + index });
            var opening = Public(candidate).Observation!;
            if (opening.Hand.Count(c => c.Id == "Wound") == 2
                && opening.Hand.Count(c => c.Id == "Prepared" && c.Upgrade == 1) == 1
                && opening.Hand.Count(c => c.Id == "Deflect" && c.Upgrade == 1) == 2
                && opening.Hand.Count(c => c.Id == "DefendSilent") == 2)
            { selected = candidate; break; }
            await candidate.DisposeAsync();
        }
        Assert.NotNull(selected);
        await using var source = selected!;
        var root = Public(source); var o = root.Observation!;
        Assert.Equal(1, o.Turn); Assert.Equal(60, o.StartHp); Assert.Equal(60, o.Hp); Assert.Equal(3, o.Energy);
        Assert.Equal(30, Assert.Single(Assert.Single(o.Enemies).Intents).Damage);
        Assert.Empty(o.KnownDraw); Assert.Equal(5, o.DrawCount); Assert.Empty(source.ReplayActions);
        Assert.Equal(3, Assert.Single(o.UnknownDraw, c => c.Card.Id == "StrikeSilent").Count);
        Assert.Equal(2, Assert.Single(o.UnknownDraw, c => c.Card.Id == "Wound").Count);
        var law = BeliefSampler.EnumerateDrawPosterior(o);
        Assert.Equal(10, law.Count); Assert.All(law, item => Assert.Equal(.1, item.Probability, 12));
        Assert.Equal(1, law.Sum(item => item.Probability), 12);
        string original = Snapshot(source);
        string[] sourceOrder = source.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(card => card.GetType().Name).ToArray();
        var rows = new List<RiskWorld>();
        foreach (var item in law)
        {
            await using var world = source.ForkExact();
            Assert.All(world.State.Players.Single().PlayerCombatState!.DrawPile.Cards,
                card => Assert.DoesNotContain(source.State.Players.Single().PlayerCombatState!.DrawPile.Cards, originalCard => ReferenceEquals(card, originalCard)));
            PermuteUnseenDraw(world, item.Order);
            Assert.Equal(original, Snapshot(world)); Assert.Equal(source.PublicTrace, world.PublicTrace);
            // Existing native exact clones own the cards/room/RNG and pending choices.
            // No reseeding, HP/energy reset, public-history edit, or fixture outcome edit.
            await using var lottery = world.ForkExact();
            await using var eight = world.ForkExact();
            await using var three = world.ForkExact();
            Assert.NotSame(world.State, lottery.State); Assert.NotSame(lottery.Room, eight.Room);
            var a = await Run(lottery, "p01-p02-prepared-lottery-public-v1", Lottery);
            Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(world));
            Assert.Equal(original, Snapshot(eight)); Assert.Equal(original, Snapshot(three));
            var b = await Run(eight, "p01-p02-certain-eight-public-v1", packet => CertainDefense(packet, false));
            Assert.Equal(original, Snapshot(three)); Assert.Equal(original, Snapshot(world));
            var c = await Run(three, "p01-p02-certain-three-public-v1", packet => CertainDefense(packet, true));
            Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(world));
            bool bothWounds = item.Order.Take(2).All(card => card.Id == "Wound");
            AssertPlainOutcome(a, bothWounds ? 30 : 60, bothWounds ? 2 : 1);
            AssertPlainOutcome(b, 52, 2); AssertPlainOutcome(c, 57, 2);
            var choice = Assert.Single(a.Trace, step => step.Action.Kind == "choose");
            Assert.All(choice.Action.Selection!, slot => Assert.Equal("Wound", choice.Before.Observation!.Choice!.Candidates[slot].Id));
            Assert.Equal(22, b.Trace.Single(step => step.Action.Kind == "end_turn").Before.Observation!.Block);
            Assert.Equal(27, c.Trace.Single(step => step.Action.Kind == "end_turn").Before.Observation!.Block);
            Assert.Equal(sourceOrder, source.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(card => card.GetType().Name));
            rows.Add(new(item.Order.Select(card => card.Id).ToArray(), 1, law.Count, a, b, c));
        }
        // Cross-check the unchanged source order through concrete native replay as well.
        // This guards this fixture's exact-clone lifecycle against the native entry path.
        var representative = rows.Single(row => row.DrawOrder.SequenceEqual(sourceOrder));
        foreach (var (expected, policy) in new (Arm Arm, Func<DecisionPacket, PublicAction> Policy)[]
        {
            (representative.Lottery, Lottery),
            (representative.CertainEight, packet => CertainDefense(packet, false)),
            (representative.CertainThree, packet => CertainDefense(packet, true)),
        })
        {
            await using var native = await source.ForkForContinuationAsync();
            var actual = await Run(native, expected.Outcome.ContinuationPolicyId, policy);
            Assert.Equal(PublicJson.Serialize(expected), PublicJson.Serialize(actual));
            Assert.Equal(original, Snapshot(source));
        }
        // Ten equal-mass classes are the complete declared law, not empirical seed frequencies.
        Assert.Equal(9, rows.Count(row => row.Lottery.Outcome.HpAfterSettlement == 60));
        Assert.Single(rows, row => row.Lottery.Outcome.HpAfterSettlement == 30);
        var lotterySummary = ObjectiveEvaluator.EvaluateBatch(rows.Select(row => row.Lottery.Outcome), law.Count);
        var eightSummary = ObjectiveEvaluator.EvaluateBatch(rows.Select(row => row.CertainEight.Outcome), law.Count);
        var threeSummary = ObjectiveEvaluator.EvaluateBatch(rows.Select(row => row.CertainThree.Outcome), law.Count);
        Assert.Equal(3, lotterySummary.ExpectedNetHpLoss); Assert.Equal(8, eightSummary.ExpectedNetHpLoss); Assert.Equal(3, threeSummary.ExpectedNetHpLoss);
        Assert.Equal(3.3, lotterySummary.ExpectedCost!.Value, 12);
        Assert.Equal(8 + .2 * 64 / 60, eightSummary.ExpectedCost!.Value, 12);
        Assert.Equal(3 + .2 * 9 / 60, threeSummary.ExpectedCost!.Value, 12);
        Assert.True(lotterySummary.ExpectedCost < eightSummary.ExpectedCost); // P01: lower mean can justify this bounded risk.
        Assert.True(threeSummary.ExpectedCost < lotterySummary.ExpectedCost); // P02: equal mean favors the stable route.
        Assert.False(lotterySummary.FormalLabelsAllowed);
        await Evidence("p01-p02", new { Scenario = source.InitialScenario, PublicRoot = root,
            ExactLaw = "declared exchangeable five-card law; all ten multiset orders, rational mass 1/10 each; not a native setup-seed posterior",
            LotterySummary = lotterySummary, CertainEightSummary = eightSummary, CertainThreeSummary = threeSummary, Worlds = rows });
    }

    private static PublicAction Lottery(DecisionPacket packet)
    {
        var o = packet.Observation!;
        if (o.Choice is not null)
            return packet.Actions.First(action => action.Kind == "choose" && action.Selection!.All(slot => o.Choice.Candidates[slot].Id == "Wound"));
        return (o.Turn == 1 ? packet.Actions.FirstOrDefault(action => action.Kind == "play" && o.Hand[action.Slot].Id == "Prepared") : null)
            ?? packet.Actions.FirstOrDefault(action => action.Kind == "play" && o.Hand[action.Slot].Id == "StrikeSilent") ?? End(packet);
    }

    private static PublicAction CertainDefense(DecisionPacket packet, bool extraDefend)
    {
        var o = packet.Observation!;
        if (o.Turn == 1)
            return packet.Actions.FirstOrDefault(action => action.Kind == "play" && o.Hand[action.Slot].Id == "Deflect")
                ?? packet.Actions.FirstOrDefault(action => action.Kind == "play" && o.Hand[action.Slot].Id == "DefendSilent" && o.Hand[action.Slot].Upgrade == 1)
                ?? (extraDefend ? packet.Actions.FirstOrDefault(action => action.Kind == "play" && o.Hand[action.Slot].Id == "DefendSilent") : null)
                ?? End(packet);
        return packet.Actions.FirstOrDefault(action => action.Kind == "play" && o.Hand[action.Slot].Id == "StrikeSilent") ?? End(packet);
    }

    private static void PermuteUnseenDraw(CombatSession world, PublicCard[] order)
    {
        var packet = Public(world); Assert.Empty(packet.Observation!.KnownDraw);
        string before = Snapshot(world);
        var pile = world.State.Players.Single().PlayerCombatState!.DrawPile;
        var original = pile.Cards.ToArray();
        var groups = original.GroupBy(card => PublicJson.Serialize(PublicViews.Card(card)))
            .ToDictionary(group => group.Key, group => new Queue<Sts2Sim.Core.Models.CardModel>(group));
        var arranged = order.Select(card => groups[PublicJson.Serialize(card)].Dequeue()).ToArray();
        Assert.Equal(original.Length, arranged.Length); Assert.All(groups.Values, Assert.Empty);
        foreach (var card in original) pile.RemoveInternal(card);
        foreach (var card in arranged) pile.AddInternal(card);
        Assert.Equal(arranged, pile.Cards);
        Assert.All(original, card => Assert.Contains(pile.Cards, other => ReferenceEquals(card, other)));
        Assert.Equal(before, Snapshot(world));
    }

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
    public async Task P05_EligibleCurrentPotionStillLosesToLaterSameBottleUse()
    {
        const string timingPolicyId = "test-public-flex-turn-three-strike-defend-v1";
        var recipe = new Scenario(Seed: "native-preference-p05-joint", Encounter: "SpinyToadNormal", EnemyHp: 27,
            Hp: 90, MaxHp: 100, Deck: ["StrikeSilent", "StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent"],
            Potions: ["FlexPotion"]);
        await using var source = await CombatSession.CreateAsync(recipe);
        await source.StepAsync(End(Public(source))); // Native non-attacking spikes move applies Thorns5.
        await Play(source, "DefendSilent"); await Play(source, "DefendSilent");
        var root = Public(source); var o = root.Observation!; var enemy = Assert.Single(o.Enemies);
        Assert.Equal(90, o.StartHp); Assert.Equal(90, o.Hp); Assert.Equal(100, o.MaxHp);
        Assert.Equal(2, o.Turn); Assert.Equal(10, o.Block); Assert.Equal(1, o.Energy); Assert.Equal(0, o.DrawCount);
        Assert.Equal("SpinyToad", enemy.Id); Assert.Equal(27, enemy.Hp);
        Assert.Equal(25, Assert.Single(enemy.Intents).Damage);
        Assert.Equal(5, Assert.Single(enemy.Powers, power => power.Id == "ThornsPower").Amount);
        Assert.Equal(3, o.Hand.Length); Assert.All(o.Hand, card => Assert.Equal("StrikeSilent", card.Id));
        Assert.Equal(2, o.Discard.Length); Assert.All(o.Discard, card => Assert.Equal("DefendSilent", card.Id));
        Assert.Equal("RingOfTheSnake", Assert.Single(o.Relics));
        PublicAction AttacksThenDefense(DecisionPacket packet) => packet.Actions.FirstOrDefault(action =>
            action.Kind == "play" && packet.Observation!.Hand[action.Slot].Id == "StrikeSilent")
            ?? packet.Actions.FirstOrDefault(action => action.Kind == "play" && packet.Observation!.Hand[action.Slot].Id == "DefendSilent")
            ?? End(packet);
        PublicAction TimingContinuation(DecisionPacket packet) =>
            (packet.Observation!.Turn >= 3 ? packet.Actions.FirstOrDefault(action => action.Kind == "potion"
                && packet.Observation.Potions[action.Slot] == "FlexPotion") : null) ?? AttacksThenDefense(packet);
        string original = Snapshot(source);
        await using var never = await source.ForkForContinuationAsync();
        await using var now = await source.ForkForContinuationAsync();
        await using var later = await source.ForkForContinuationAsync();
        Assert.Equal(original, Snapshot(never)); Assert.Equal(original, Snapshot(now)); Assert.Equal(original, Snapshot(later));
        Assert.NotSame(never.State, now.State); Assert.NotSame(now.Room, later.Room);
        var baseline = await Run(never, "test-public-strike-defend-never-potion-v1", AttacksThenDefense);
        Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(now)); Assert.Equal(original, Snapshot(later));
        var use = root.Actions.Single(action => action.Kind == "potion"); var wait = End(root);
        bool first = true;
        var early = await Run(now, timingPolicyId, packet => { if (first) { first = false; return use; } return TimingContinuation(packet); });
        Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(later));
        first = true;
        var late = await Run(later, timingPolicyId, packet => { if (first) { first = false; return wait; } return TimingContinuation(packet); });
        Assert.Equal(original, Snapshot(source));
        foreach (var (arm, hp, turn) in new[] { (baseline, 51, 4), (early, 70, 3), (late, 75, 3) })
        {
            var result = arm.Outcome;
            Assert.Equal(TerminalKind.Win, result.TerminalKind); Assert.True(result.PlayerAlive); Assert.True(result.SettlementComplete);
            Assert.Equal(90, result.HpAtCombatStart); Assert.Equal(hp, result.HpAfterSettlement); Assert.Equal(turn, result.PlayerTurnsElapsed);
            Assert.Equal(100, result.MaxHpStart); Assert.Equal(100, result.MaxHpAfterSettlement);
            Assert.True(result.HpEventDiagnosticsComplete); Assert.Equal(90 - hp, result.CumulativeHpDamage);
            Assert.Equal(0, result.HealingReceived); Assert.Equal(0, result.OtherHpAdjustment);
            Assert.True(result.ResourceProvenanceComplete); Assert.Empty(result.PermanentChanges);
            Assert.Equal(result.PersistentAssetsAtStartJson, result.PersistentAssetsAfterSettlementJson);
            Assert.Equal(new InventoryQuantity("FlexPotion", 1), Assert.Single(result.InventoryStart));
            Assert.Equal(0, arm.ExtraCardRewards);
        }
        Assert.Equal(new InventoryQuantity("FlexPotion", 1), Assert.Single(baseline.Outcome.InventoryEnd));
        Assert.Empty(baseline.Outcome.ResourceEvents); Assert.DoesNotContain(baseline.Trace, step => step.Action.Kind == "potion");
        Assert.Equal(EvaluationStatus.Scored, ObjectiveEvaluator.Evaluate(baseline.Outcome).Status);
        foreach (var arm in new[] { early, late })
        {
            Assert.Empty(arm.Outcome.InventoryEnd);
            var consumed = Assert.Single(arm.Outcome.ResourceEvents);
            Assert.Equal("consumed", consumed.Kind); Assert.Equal("FlexPotion", consumed.ResourceId); Assert.Equal(1, consumed.Quantity);
            Assert.Single(arm.Trace, step => step.Action.Kind == "potion");
            Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, ObjectiveEvaluator.Evaluate(arm.Outcome).Status);
        }
        Assert.Equal(2, early.Trace.Single(step => step.Action.Kind == "potion").Before.Observation!.Turn);
        Assert.Equal(3, late.Trace.Single(step => step.Action.Kind == "potion").Before.Observation!.Turn);
        Assert.Equal(75, late.Trace[0].After.Observation!.Hp); Assert.Equal(3, late.Trace[0].After.Observation!.Turn);
        var baselineThirdEnd = baseline.Trace.Single(step => step.Action.Kind == "end_turn" && step.Before.Observation!.Turn == 3);
        Assert.Equal(19, Assert.Single(baselineThirdEnd.Before.Observation!.Enemies).Intents.Single().Damage);
        Assert.DoesNotContain(baselineThirdEnd.Before.Observation.Enemies.Single().Powers, power => power.Id == "ThornsPower");
        Assert.Equal(51, baselineThirdEnd.After.Observation!.Hp);
        double currentHpSaved = early.Outcome.HpAfterSettlement!.Value - baseline.Outcome.HpAfterSettlement!.Value;
        Assert.Equal(19, currentHpSaved);
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.Potion(EstimateInterval.Exact(currentHpSaved)));
        var audit = new RelativeOutcomeAudit("declared-deterministic-spiny-toad-world", EmptyPotionContinuationProof.RootKey(root),
            "combat-start:90hp:max100:FlexPotion=1", timingPolicyId, "constructed-native-five-card-deterministic-continuation-v1", true, PublicJson.Serialize(use));
        var relative = CommonResourceObjective.Evaluate(new(early.Outcome, audit),
            new(late.Outcome, audit with { RootActionIdentity = PublicJson.Serialize(wait) }));
        Assert.True(relative.RelativeValueMask, string.Join(",", relative.Reasons));
        Assert.Equal(5 + .2 * (20 * 20 - 15 * 15) / 90, relative.FirstMinusSecondCost!.Value, 12);
        Assert.True(relative.FirstMinusSecondCost > 0); // Positive early-minus-late cost prefers later use.
        Assert.False(relative.FirstAbsoluteValueMask); Assert.False(relative.SecondAbsoluteValueMask); Assert.False(relative.FormalLabelsAllowed);
        Assert.Equal(new CancelledInventoryTerm("FlexPotion", 1, true), Assert.Single(relative.CancelledInventory));
        await Evidence("p05-joint", new { Scenario = recipe, PublicRoot = root,
            MechanicsScope = "native deterministic spikes5, explosion25 removing Thorns, lash19 cycle; five-card deck fully drawn every turn; constructed frozen continuations",
            NeverUse = baseline, UseNow = early, UseLater = late, CurrentWholeCombatHpSaved = currentHpSaved,
            CurrentUseEligibility = PreferenceGates.Potion(EstimateInterval.Exact(currentHpSaved)), RelativeTiming = relative });
    }

    [Fact]
    public async Task B01_ExactJointFiveAndEightyNativeBoundaryKeepsUncertaintySeparate()
    {
        const string baselineId = "test-public-strike-first-v1";
        const string planId = "test-public-adrenaline-backflip-hunt-or-abort-v1";
        var recipe = new Scenario(Seed: "native-preference-b01-0", Enemy: "MechaKnight", EnemyHp: 5, Hp: 60, MaxHp: 70,
            Deck: ["Adrenaline", "Backflip", "TheHunt", .. Enumerable.Repeat("StrikeSilent", 9)]);
        CombatSession? selected = null;
        for (int index = 0; index < 128; index++)
        {
            var candidate = await CombatSession.CreateAsync(recipe with { Seed = "native-preference-b01-" + index });
            var opening = Public(candidate).Observation!;
            if (opening.Hand.Count(card => card.Id == "Adrenaline") == 1
                && opening.Hand.Count(card => card.Id == "Backflip") == 1
                && opening.Hand.Count(card => card.Id == "StrikeSilent") == 5)
            { selected = candidate; break; }
            await candidate.DisposeAsync();
        }
        Assert.NotNull(selected);
        await using var source = selected!;
        var root = Public(source); var o = root.Observation!;
        Assert.Equal(60, o.StartHp); Assert.Equal(60, o.Hp); Assert.Equal(3, o.Energy); Assert.Equal(1, o.Turn);
        Assert.Equal(30, Assert.Single(Assert.Single(o.Enemies).Intents).Damage);
        Assert.Empty(o.KnownDraw); Assert.Equal(5, o.DrawCount); Assert.Empty(source.ReplayActions);
        Assert.Equal(1, Assert.Single(o.UnknownDraw, card => card.Card.Id == "TheHunt").Count);
        Assert.Equal(4, Assert.Single(o.UnknownDraw, card => card.Card.Id == "StrikeSilent").Count);
        var law = BeliefSampler.EnumerateDrawPosterior(o);
        Assert.Equal(5, law.Count); Assert.All(law, item => Assert.Equal(.2, item.Probability, 12));
        Assert.Equal(1, law.Sum(item => item.Probability), 12);
        var contract = AnchoredBonusPlan.Begin(PublicJson.Serialize(root), "native Hunt fatal and offered CardReward", planId, baselineId, o.Turn);
        Assert.Equal(2, contract.Context.DeadlinePlayerTurn);
        string original = Snapshot(source);
        string[] sourceOrder = source.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(card => card.GetType().Name).ToArray();
        var rows = new List<BoundaryWorld>();
        foreach (var item in law)
        {
            // TheHunt requires concrete native reward hooks. Recreate each source arm
            // FIRST, then install its declared hidden order. Never replay a mutated arm
            // as though that permutation had been produced by the source Scenario seed.
            await using var baseline = await source.ForkForContinuationAsync();
            await using var plan = await source.ForkForContinuationAsync();
            PermuteUnseenDraw(baseline, item.Order); PermuteUnseenDraw(plan, item.Order);
            Assert.Equal(original, Snapshot(baseline)); Assert.Equal(original, Snapshot(plan));
            Assert.Equal(source.PublicTrace, baseline.PublicTrace); Assert.Equal(source.PublicTrace, plan.PublicTrace);
            Assert.False(baseline.State.IsProjection); Assert.False(plan.State.IsProjection);
            Assert.NotSame(baseline.Room, plan.Room); Assert.NotSame(baseline.State, plan.State);
            var controller = contract;
            PublicAction Attempt(DecisionPacket packet)
            {
                var current = packet.Observation!;
                Assert.Equal(contract.Context.AnchorPublicSummary, controller.Context.AnchorPublicSummary);
                Assert.Equal(contract.Context.StartPlayerTurn, controller.Context.StartPlayerTurn);
                Assert.Equal(contract.Context.DeadlinePlayerTurn, controller.Context.DeadlinePlayerTurn);
                if (controller.Context.State == BonusPlanState.Aborted) return StrikeFirst(packet);
                var draw = packet.Actions.FirstOrDefault(action => action.Kind == "play" && current.Hand[action.Slot].Id == "Adrenaline")
                    ?? packet.Actions.FirstOrDefault(action => action.Kind == "play" && current.Hand[action.Slot].Id == "Backflip");
                if (draw is not null) return draw;
                var hunt = packet.Actions.FirstOrDefault(action => action.Kind == "play" && current.Hand[action.Slot].Id == "TheHunt");
                if (hunt is not null) return hunt;
                // Deliberately frozen public abort, not an optimal continuation claim.
                controller = controller.Advance(current.Turn, false, "four draws completed without public Hunt; abort", abort: true);
                return End(packet);
            }
            var a = await Run(baseline, baselineId, StrikeFirst);
            Assert.Equal(original, Snapshot(source)); Assert.Equal(original, Snapshot(plan));
            var b = await Run(plan, planId, Attempt);
            Assert.Equal(original, Snapshot(source));
            bool fatal = b.HuntFatalPlayerTurn.HasValue && b.Outcome.TerminalKind == TerminalKind.Win;
            bool earned = fatal && b.ExtraCardRewards == 1;
            bool deadline = fatal && b.HuntFatalPlayerTurn!.Value <= contract.Context.DeadlinePlayerTurn;
            // Only these flags are enriched from recorded native facts; HP, resources,
            // win/loss, damage, healing and settlement records are never synthesized.
            b = b with { Outcome = b.Outcome with { SpecifiedFinishSuccess = fatal, EarnedBonus = earned, DeadlineMet = deadline } };
            if (earned && deadline) controller = controller.Advance(b.HuntFatalPlayerTurn!.Value, false,
                "native Hunt fatal with actual offered CardReward", bonusEarned: true);
            int position = Array.FindIndex(item.Order, card => card.Id == "TheHunt");
            AssertPlainOutcome(a, 60, 1);
            Assert.Equal(TerminalKind.Win, b.Outcome.TerminalKind); Assert.True(b.Outcome.PlayerAlive);
            Assert.True(b.Outcome.SettlementComplete); Assert.True(b.Outcome.HpEventDiagnosticsComplete);
            Assert.Equal(position < 4 ? 60 : 35, b.Outcome.HpAfterSettlement);
            Assert.Equal(position < 4 ? 0 : 25, b.Outcome.CumulativeHpDamage);
            Assert.Equal(0, b.Outcome.HealingReceived); Assert.Equal(0, b.Outcome.OtherHpAdjustment);
            Assert.Equal(position < 4 ? 1 : 0, b.ExtraCardRewards);
            Assert.Equal(position < 4, fatal && earned && deadline);
            Assert.Equal(position < 4 ? BonusPlanState.Finished : BonusPlanState.Aborted, controller.Context.State);
            Assert.Equal(contract.Context.AnchorPublicSummary, controller.Context.AnchorPublicSummary);
            Assert.Equal(1, controller.Context.StartPlayerTurn); Assert.Equal(2, controller.Context.DeadlinePlayerTurn);
            Assert.Equal("Adrenaline", b.Trace[0].Before.Observation!.Hand[b.Trace[0].Action.Slot].Id);
            Assert.Equal(4, b.Trace[0].After.Observation!.Energy);
            Assert.Equal("Backflip", b.Trace[1].Before.Observation!.Hand[b.Trace[1].Action.Slot].Id);
            Assert.Equal(3, b.Trace[1].After.Observation!.Energy); Assert.Equal(5, b.Trace[1].After.Observation!.Block);
            if (position == 4)
            {
                Assert.Null(b.HuntFatalPlayerTurn); Assert.Empty(b.Outcome.PermanentChanges);
                Assert.Equal("StrikeSilent", b.Trace[^1].Before.Observation!.Hand[b.Trace[^1].Action.Slot].Id);
                Assert.Equal(2, b.Outcome.PlayerTurnsElapsed); Assert.Equal(EvaluationStatus.Scored, ObjectiveEvaluator.Evaluate(b.Outcome).Status);
            }
            else
            {
                Assert.Contains(b.Outcome.PermanentChanges, change => change.Kind == "earned_extra_reward_opportunity:CardReward" && change.Amount == 1);
                var value = ObjectiveEvaluator.Evaluate(b.Outcome);
                Assert.Equal(EvaluationStatus.ObjectiveValueUnresolved, value.Status);
                Assert.Contains("permanent_future_value_unresolved:earned_extra_reward_opportunity:CardReward", value.Reasons);
            }
            Assert.True(b.Outcome.ResourceProvenanceComplete);
            Assert.Empty(b.Outcome.InventoryStart); Assert.Empty(b.Outcome.InventoryEnd); Assert.Empty(b.Outcome.ResourceEvents);
            Assert.Equal(sourceOrder, source.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(card => card.GetType().Name));
            rows.Add(new(position, 1, law.Count, a, b, controller.Context));
        }
        double[] deltas = rows.Select(row => (double)(row.Baseline.Outcome.HpAfterSettlement!.Value - row.Plan.Outcome.HpAfterSettlement!.Value)).ToArray();
        double expectedExtraLoss = deltas.Sum() / law.Count;
        double success = (double)rows.Count(row => row.Plan.Outcome.SpecifiedFinishSuccess == true && row.Plan.Outcome.EarnedBonus == true
            && row.Plan.Outcome.DeadlineMet == true && row.Plan.Outcome.PlayerAlive == true) / law.Count;
        bool safe = rows.All(row => row.Baseline.Outcome.PlayerAlive == true && row.Plan.Outcome.PlayerAlive == true);
        Assert.Equal(5, expectedExtraLoss); Assert.Equal(.8, success);
        var evidence = new WholePlanEvidence(contract.Context.AnchorPublicSummary, baselineId, planId, true,
            EstimateInterval.Exact(expectedExtraLoss), EstimateInterval.Exact(success), safe);
        Assert.Equal(Eligibility.EligibleNotMandatory, contract.Evaluate(evidence));
        // Regression inputs for the fixed-sample estimator/gate path only: these
        // enumerated classes are not iid samples and no confidence-coverage claim is made.
        var lossBounds = FixedSampleEstimates.Mean(deltas, law.Count, 0, 25);
        var successBounds = FixedSampleEstimates.SpecifiedSuccess(rows.Select(row => row.Plan.Outcome), law.Count);
        Assert.True(lossBounds.Lower < 5 && lossBounds.Upper > 5);
        Assert.True(successBounds.Lower < .8 && successBounds.Upper > .8);
        Assert.Equal(Eligibility.Unresolved, contract.Evaluate(evidence with { ExtraExpectedLoss = lossBounds, UnconditionalSuccess = successBounds }));
        var summary = ObjectiveEvaluator.EvaluateBatch(rows.Select(row => row.Plan.Outcome), law.Count);
        Assert.Null(summary.ExpectedCost); Assert.Equal(4, summary.ValueUnresolved); Assert.False(summary.FormalLabelsAllowed);
        await Evidence("b01", new { Scenario = source.InitialScenario, PublicRoot = root, Anchor = contract.Context, BaselineId = baselineId,
            ExactLaw = "declared exchangeable five-position law, rational mass 1/5 each; frozen Strike-first baseline and public attempt/abort script; not the native setup-seed posterior",
            ExpectedExtraLoss = expectedExtraLoss, UnconditionalSuccess = success, SafetyAcceptable = safe,
            ExactEligibility = contract.Evaluate(evidence), FixedSampleFormulaLossBounds = lossBounds, FixedSampleFormulaSuccessBounds = successBounds,
            FormulaBoundsAreConfidenceCoverageClaims = false, PlanSummary = summary, Worlds = rows });
    }

    private static PublicAction StrikeFirst(DecisionPacket packet) => packet.Actions.FirstOrDefault(action =>
        action.Kind == "play" && packet.Observation!.Hand[action.Slot].Id == "StrikeSilent") ?? End(packet);

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
        while (packet.Status is "player_decision" or "card_choice" && trace.Count < 32)
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
