using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class DelayedPotionTimingTests(ITestOutputHelper output)
{
    private const string PolicyId = "test-public-next-turn-flex-attack-defend-v1";
    private static readonly ulong[] Seeds = Enumerable.Range(9420000, 32).Select(x => (ulong)x).ToArray();
    private static readonly Scenario Recipe = new(Seed: "potion-timing-source-0", Enemy: "TwigSlimeS", EnemyHp: 20,
        Hp: 60, MaxHp: 70, Deck: ["DefendSilent", "DefendSilent", "DefendSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent"],
        Potions: ["FlexPotion"]);

    // This finite continuation receives only a detached public DTO. Both arms use
    // this same continuation: only their first root action differs.
    private static PublicAction Continue(DecisionPacket packet)
    {
        var o = packet.Observation!;
        return (o.Turn >= 2 ? packet.Actions.FirstOrDefault(a => a.Kind == "potion" && o.Potions[a.Slot] == "FlexPotion") : null)
            ?? packet.Actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Id == "StrikeSilent")
            ?? packet.Actions.FirstOrDefault(a => a.Kind == "play" && o.Hand[a.Slot].Id == "DefendSilent")
            ?? packet.Actions.Single(a => a.Kind == "end_turn");
    }
    private static DecisionPacket Public(CombatSession s) => PublicJson.Read<DecisionPacket>(PublicJson.Serialize(s.Observe()));
    private static string Snapshot(CombatSession s) => PublicJson.Serialize(new { Packet = s.Observe(), Rng = s.State.RunState.Rng.ToSerializable() });
    private static async Task Prefix(CombatSession s)
    {
        for (int i = 0; i < 3; i++)
        {
            var packet = Public(s);
            await s.StepAsync(packet.Actions.First(a => a.Kind == "play" && packet.Observation!.Hand[a.Slot].Id == "DefendSilent"));
        }
    }

    [Fact]
    public async Task SafeWaitThenUseSamePotionBeatsWastingItNowAndSurvivesTrueFutureReplacement()
    {
        await using var source = await CombatSession.CreateAsync(Recipe);
        string opening = PublicJson.Serialize(source.Observe());
        // Select by identical public opening only, before any comparison outcomes.
        // Use ordinary native setup seeds; never edit HP, piles or RNG in place.
        CombatSession? replacement = null;
        for (int i = 1; i <= 128; i++)
        {
            var trial = await CombatSession.CreateAsync(Recipe with { Seed = "potion-timing-source-" + i });
            if (PublicJson.Serialize(trial.Observe()) == opening) { replacement = trial; break; }
            await trial.DisposeAsync();
        }
        Assert.NotNull(replacement);
        await using var replaced = replacement!;
        await Prefix(source); await Prefix(replaced);
        var root = Public(source); var o = root.Observation!;
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(replaced.Observe()));
        Assert.Equal(source.PublicTrace, replaced.PublicTrace);
        Assert.NotEqual(PublicJson.Serialize(source.State.RunState.Rng.ToSerializable()), PublicJson.Serialize(replaced.State.RunState.Rng.ToSerializable()));
        Assert.Equal(10, o.Ascension); Assert.Equal(1, o.Turn); Assert.Equal(60, o.StartHp); Assert.Equal(60, o.Hp);
        Assert.Equal(0, o.Energy); Assert.Equal(15, o.Block); Assert.Equal(0, o.DrawCount);
        Assert.Equal(3, o.Hand.Length); Assert.All(o.Hand, c => Assert.Equal("StrikeSilent", c.Id));
        Assert.Equal(3, o.Discard.Length); Assert.All(o.Discard, c => Assert.Equal("DefendSilent", c.Id));
        Assert.Equal("RingOfTheSnake", Assert.Single(o.Relics));
        var enemy = Assert.Single(o.Enemies); Assert.Equal("TwigSlimeS", enemy.Id); Assert.Equal(20, enemy.Hp);
        Assert.Equal(5, Assert.Single(enemy.Intents).Damage);
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, BeliefSampler.PosteriorProfileFor(source));
        // Nine is an eligibility result, never an action override. The public
        // script can wait at this root and play attacks after its later use.
        Assert.Equal(Eligibility.EligibleNotMandatory, PreferenceGates.Potion(EstimateInterval.Exact(9)));
        Assert.Equal("end_turn", Continue(root).Kind);
        Assert.Equal(Continue(root), Continue(Public(replaced)));

        string original = Snapshot(source), replacedOriginal = Snapshot(replaced);
        var first = await Evaluate(source);
        var second = await Evaluate(replaced);
        Assert.Equal(PublicJson.Serialize(first), PublicJson.Serialize(second));
        Assert.Equal(original, Snapshot(source)); Assert.Equal(replacedOriginal, Snapshot(replaced));
        Assert.Contains(first, p => p.HpSaved == 0); Assert.Contains(first, p => p.HpSaved == 5);
        var plan = new RelativeEvaluationPlan(Seeds.Select(x => x.ToString()).ToArray(),
            "32 fixed seeds 9420000..9420031; scripts and support fixed before sampling", true);
        // Mechanics support: late wins on turn 2 without damage; early wins on
        // turn 3 after zero or one unblocked 5-damage attack. No inferred extrema.
        var relative = CommonResourceObjective.EvaluateBatch(first.Select(p => p.Early).ToArray(), first.Select(p => p.Late).ToArray(), plan,
            support: new(0, 5 + .2 * 25 / 60, "restricted TwigSlimeS/Flex mechanics in DELAYED_POTION_TIMING_EVIDENCE.md"));
        Assert.True(relative.RelativeValueMask); Assert.Equal(1, relative.PreferredArm); Assert.False(relative.FormalLabelsAllowed);
        Assert.Equal(32, relative.ResolvedWorlds);
        output.WriteLine(PublicJson.Serialize(new { SourceSeed = Recipe.Seed, ReplacementSeed = replaced.InitialScenario.Seed,
            Worlds = first.Length, HpSavedCounts = first.GroupBy(p => p.HpSaved).Select(g => new { HpSaved = g.Key, Count = g.Count() }),
            MeanHpSaved = first.Average(p => p.HpSaved), Relative = relative }));
        string? directory = Environment.GetEnvironmentVariable("NOSL_POTION_TIMING_EVIDENCE_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "native-paired-timing.json"), PublicJson.Serialize(new {
                Scenario = Recipe, ReplacementSeed = replaced.InitialScenario.Seed, PublicRoot = root,
                PrefixActions = source.ReplayActions, PolicyId, Plan = plan, Relative = relative, Pairs = first,
                TrueHiddenReplacementMatched = true, SourceUnchanged = true }));
        }
    }

    private sealed record Step(DecisionPacket Before, PublicAction Action, DecisionPacket After);
    private sealed record Arm(RolloutOutcome Outcome, Step[] Trace);
    private sealed record Pair(ulong Seed, AuditedOutcome Early, AuditedOutcome Late, int HpSaved, Step[] EarlyTrace, Step[] LateTrace);

    private static async Task<Pair[]> Evaluate(CombatSession source)
    {
        var rows = new List<Pair>(); var root = Public(source);
        string rootKey = EmptyPotionContinuationProof.RootKey(root);
        foreach (ulong seed in Seeds)
        {
            await using var world = await BeliefSampler.SampleWorldAsync(source, seed, 1024);
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
            string untouched = Snapshot(world);
            await using var early = await world.ForkForContinuationAsync();
            await using var late = await world.ForkForContinuationAsync();
            Assert.NotSame(world.State, early.State); Assert.NotSame(early.State, late.State);
            Assert.NotSame(early.Room, late.Room);
            Assert.Equal(Snapshot(early), Snapshot(late));
            var use = root.Actions.Single(a => a.Kind == "potion");
            var wait = root.Actions.Single(a => a.Kind == "end_turn");
            var a = await Run(early, use); Assert.Equal(untouched, Snapshot(world));
            Assert.Equal(untouched, Snapshot(late));
            var b = await Run(late, wait); Assert.Equal(untouched, Snapshot(world));
            Assert.Equal(60, b.Trace[0].After.Observation!.Hp); // Entire first wait is safe.
            Assert.Equal(2, b.Trace[0].After.Observation!.Turn);
            Assert.Equal("potion", b.Trace[1].Action.Kind); Assert.Equal("play", b.Trace[2].Action.Kind);
            Assert.Equal(60, b.Outcome.HpAfterSettlement); Assert.Equal(2, b.Outcome.PlayerTurnsElapsed);
            Assert.Equal(3, a.Outcome.PlayerTurnsElapsed);
            Assert.DoesNotContain(a.Trace.First(t => t.Before.Observation!.Turn == 2).Before.Observation!.Powers,
                p => p.Id == "FlexPotionPower" || p.Id == "StrengthPower" && p.Amount != 0);
            foreach (var arm in new[] { a, b })
            {
                var x = arm.Outcome;
                Assert.Equal(TerminalKind.Win, x.TerminalKind); Assert.Equal(60, x.HpAtCombatStart);
                Assert.True(x.SettlementComplete); Assert.True(x.HpEventDiagnosticsComplete); Assert.True(x.ResourceProvenanceComplete);
                Assert.Equal(0, x.HealingReceived); Assert.Equal(0, x.OtherHpAdjustment);
                Assert.Equal(60 - x.HpAfterSettlement, x.CumulativeHpDamage);
                Assert.Equal(new InventoryQuantity("FlexPotion", 1), Assert.Single(x.InventoryStart)); Assert.Empty(x.InventoryEnd);
                var consumed = Assert.Single(x.ResourceEvents); Assert.Equal("consumed", consumed.Kind);
                Assert.Equal("FlexPotion", consumed.ResourceId); Assert.Equal(1, consumed.Quantity);
                Assert.Equal(1, arm.Trace.Count(t => t.Action.Kind == "potion"));
                Assert.Empty(x.PermanentChanges); Assert.Equal(x.PersistentAssetsAtStartJson, x.PersistentAssetsAfterSettlementJson);
                Assert.Null(ObjectiveEvaluator.Evaluate(x).Cost);
            }
            var audit = new RelativeOutcomeAudit(seed.ToString(), rootKey, "combat-start:60hp:FlexPotion=1",
                "same-public-finite-next-turn-flex-script", BeliefSampler.WholeSetupReplayProfile, true, PublicJson.Serialize(use));
            var earlyOutcome = new AuditedOutcome(a.Outcome, audit);
            var lateOutcome = new AuditedOutcome(b.Outcome, audit with { RootActionIdentity = PublicJson.Serialize(wait) });
            var cancellation = CommonResourceObjective.Evaluate(earlyOutcome, lateOutcome);
            Assert.True(cancellation.RelativeValueMask, string.Join(",", cancellation.Reasons));
            Assert.Equal(new CancelledInventoryTerm("FlexPotion", 1, true), Assert.Single(cancellation.CancelledInventory));
            Assert.False(cancellation.FirstAbsoluteValueMask); Assert.False(cancellation.SecondAbsoluteValueMask);
            int saved = b.Outcome.HpAfterSettlement!.Value - a.Outcome.HpAfterSettlement!.Value;
            Assert.Contains(saved, new[] { 0, 5 });
            rows.Add(new(seed, earlyOutcome, lateOutcome, saved, a.Trace, b.Trace));
        }
        return rows.ToArray();
    }

    private static async Task<Arm> Run(CombatSession branch, PublicAction first)
    {
        var trace = new List<Step>(); var packet = Public(branch);
        while (packet.Status == "player_decision" && trace.Count < 16)
        {
            var action = trace.Count == 0 ? first : Continue(packet);
            await branch.StepAsync(action);
            var next = Public(branch); trace.Add(new(packet, action, next)); packet = next;
        }
        Assert.Equal("terminal_settled", packet.Status);
        var facts = await branch.SettleAsync(); Assert.Equal(0, facts.RewardSelectionsMade);
        return new(RolloutRecorder.Settled(branch, facts, PolicyId, trace[^1].Before.Observation!.Turn), trace.ToArray());
    }
}
