using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeInitialHpConditionTests
{
    private static DecisionPacket Root(params (string Id, int Hp)[] monsters)
    {
        PublicCard Card(string id) => new(id, 0, 1, -1, "Skill", []);
        var deck = new[] { "StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent", "Nightmare",
            "Neutralize", "Survivor", "AscendersBane" }.Select(Card).ToArray();
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, deck,
            [new("RingOfTheSnake", new Dictionary<string, int> { ["isMelted"] = 0 }),
             new("Pomander", new Dictionary<string, int>())], [], 3, 2, 0, 0);
        var hand = deck.Take(7).ToArray();
        var enemies = monsters.Select((monster, slot) =>
            new PublicEnemy(slot, monster.Id, monster.Hp, monster.Hp, 0, [], [])).ToArray();
        PublicEvent[] history = [new("combat_started", "Silent:A10"),
            new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
            .. hand.Select(card => new PublicEvent("draw", PublicJson.Serialize(card))), new("player_turn", "1"),
            .. enemies.Select(enemy => new PublicEvent("intent_published",
                PublicJson.Serialize(new { slot = enemy.Slot, id = enemy.Id })))];
        return new("player_decision", new("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            hand, [], [], [new(deck[^1], 1)], [], 1, [], ["RingOfTheSnake", "Pomander"], [],
            enemies, history, null), [new(0, "end_turn")]);
    }

    [Theory]
    [InlineData("duplicate", "initial_hp_unique_startup_roster_required")]
    [InlineData("missing", "initial_hp_unique_startup_roster_required")]
    [InlineData("replacement_slot", "initial_hp_unique_startup_roster_required")]
    [InlineData("replacement_type", "initial_hp_unique_startup_roster_required")]
    [InlineData("duplicate_slot", "initial_hp_unique_startup_roster_required")]
    [InlineData("range", "initial_hp_certified_range_required")]
    public void OnlyCompleteOriginalLifetimeSlotsAndCertifiedMaxHpCanConditionLaterRoots(string change, string reason)
    {
        var root = change == "duplicate" ? Root(("ShrinkerBeetle", 40), ("ShrinkerBeetle", 41))
            : Root(("ShrinkerBeetle", 41), ("SludgeSpinner", 42));
        var observation = root.Observation!;
        root = root with { Observation = change switch
        {
            "missing" => observation with { Enemies = [observation.Enemies[0]] },
            "replacement_slot" => observation with
                { Enemies = [observation.Enemies[0] with { Slot = 2 }, observation.Enemies[1]] },
            "replacement_type" => observation with
                { Enemies = [observation.Enemies[0] with { Id = "TwigSlimeS", Hp = 9, MaxHp = 9 }, observation.Enemies[1]] },
            "duplicate_slot" => observation with
                { Enemies = [observation.Enemies[0], observation.Enemies[1] with { Slot = 0 }] },
            "range" => observation with
                { Enemies = [observation.Enemies[0] with { Hp = 39, MaxHp = 39 }, observation.Enemies[1]] },
            _ => observation,
        }};
        root = root with { Observation = root.Observation! with
            { Turn = 4, History = [.. root.Observation.History, new("action", "later-action")] } };
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out _, out string? shuffleReason), shuffleReason);
        Assert.False(NativeInitialHpCondition.TryCreate(root, out var condition, out string? actualReason));
        Assert.Null(condition);
        Assert.Equal(reason, actualReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterStableAndPendingChoiceRootsRetainHpTargetsByLifetimeSlot(bool pendingChoice)
    {
        var opening = Root(("ShrinkerBeetle", 41), ("SludgeSpinner", 42));
        Assert.True(NativeInitialHpCondition.TryCreate(opening, out var initial, out string? initialReason), initialReason);
        var later = opening with
        {
            Status = pendingChoice ? "card_choice" : "player_decision",
            Actions = pendingChoice ? [new(8, "choose", Selection: [0])] : [new(8, "end_turn")],
            Observation = opening.Observation! with
            {
                Turn = 4,
                Enemies = opening.Observation.Enemies.Reverse().Select(enemy => enemy with
                    { Hp = enemy.MaxHp - 7, Block = 5, Powers = [new("StrengthPower", 2)] }).ToArray(),
                Choice = pendingChoice ? new("Nightmare", 1, 1, false, [opening.Observation.Hand[0]]) : null,
                History = [.. opening.Observation.History, new("action", "later-action"),
                    new("power_changed", PublicJson.Serialize(new { targetSlot = 0, id = "PaperCutsPower", amount = 2 })),
                    new("player_turn", "4"),
                    new("intent_published", PublicJson.Serialize(new { slot = 1, id = "SludgeSpinner" }))],
            },
        };
        Assert.True(NativeInitialHpCondition.TryCreate(later, out var condition, out string? reason), reason);
        Assert.Equal(initial!.Envelope, condition!.Envelope);
        Assert.Equal(initial.Targets.OrderBy(pair => pair.Key), condition.Targets.OrderBy(pair => pair.Key));
        Assert.Equal(41, condition.Targets["ShrinkerBeetle"].TargetHp);
        Assert.Equal(42, condition.Targets["SludgeSpinner"].TargetHp);
    }

    [Fact]
    public async Task GenuineNativeLaterDamagedEnemyRootRetainsItsRolledInitialHp()
    {
        var prior = new NativeRunPrior { EligibleSlots = 1, Execution = new(MaxFloors: 1, SourceDecisionHorizon: 64) };
        var recipe = prior.Draw(new Rng(8001, "nosl-owned-native-run-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenAsync(prior.Execution, recipe.IndependentRunSeed, recipe.Slot);
        Assert.NotNull(world);
        Assert.False(world.IsConstructedLifecycleFixture);
        var opening = world.Observe();
        Assert.True(NativeInitialHpCondition.TryCreate(opening, out var initial, out string? initialReason), initialReason);
        var attack = opening.Actions.First(action => action.Kind == "play" && action.Target >= 0
            && opening.Observation!.Hand[action.Slot].Type == "Attack");
        await world.StepAsync(attack);
        var later = await world.StepAsync(world.Observe().Actions.Single(action => action.Kind == "end_turn"));
        Assert.Equal("player_decision", later.Status);
        Assert.True(later.Observation!.Turn >= 2);
        Assert.Contains(later.Observation.History, item => item.Kind == "damage");
        Assert.Contains(later.Observation.Enemies, enemy => enemy.Hp < enemy.MaxHp);
        Assert.True(NativeInitialHpCondition.TryCreate(later, out var condition, out string? reason), reason);
        Assert.Equal(initial!.Targets.OrderBy(pair => pair.Key), condition!.Targets.OrderBy(pair => pair.Key));
        Assert.Equal(initial.Envelope, condition.Envelope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualNativeCreationOrderUsesPublicTypeIdentityAndAConstantEnvelope(bool reverse)
    {
        var root = Root(("ShrinkerBeetle", 41), ("SludgeSpinner", 42));
        Assert.True(NativeInitialHpCondition.TryCreate(root, out var condition, out string? reason), reason);
        if (!ModelDb.Contains(typeof(ShrinkerBeetle))) ModelDb.Init(ContentRegistry.AllTypes);
        var run = new RunState("conditional-hp-order", new Overgrowth(), ascensionLevel: 10);
        var state = new CombatState(run);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<NativeHpProposal>();
        var proposalRandom = new Rng(91);
        using (LabelRandomScope.Enter(_ => throw new InvalidOperationException("Unexpected unconstrained draw"),
            beginMonsterHp: context =>
            {
                var plan = condition!.CreateProposal(context, seen, proposalRandom.NextUnsignedLong);
                plans.Add(plan);
                seen.Add(context.Creature.Monster!.GetType().Name);
                return LabelRandomScope.Enter(_ => plan.RawWord);
            }))
        {
            MonsterModel[] monsters = [(MonsterModel)ModelDb.Monster<ShrinkerBeetle>().MutableClone(),
                (MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone()];
            foreach (var monster in reverse ? monsters.Reverse() : monsters)
                state.AddMonster(monster, CombatSide.Enemy);
        }
        Assert.Equal(2, condition!.EnemyCount);
        Assert.Equal(2, plans.Count);
        Assert.Equal(2, run.Rng.Niche.Counter);
        Assert.All(state.Enemies, enemy => Assert.Equal(condition.Targets[enemy.Monster!.GetType().Name].TargetHp, enemy.MaxHp));
        Assert.Equal(1UL << 52, condition.Targets["ShrinkerBeetle"].RootMaxBucketSize);
        Assert.Equal(1UL << 53, condition.Targets["SludgeSpinner"].RootMaxBucketSize);
        Assert.Equal(new ShuffleRational(1, 2), condition.Envelope);
        // Shrinker first: 1/3 × 1; Spinner first: 1/2 × 1/2. Their densities differ.
        Assert.Equal(reverse ? new[] { 2, 2 } : new[] { 3, 1 }, plans.Select(plan => plan.Factor.Bound));
        Assert.All(plans, plan => Assert.True(plan.BucketSize <= plan.RootMaxBucketSize));
    }

    [Fact]
    public void EveryCertifiedRangeMatchesGenuineNativeA10CreatureSetup()
    {
        if (!ModelDb.Contains(typeof(ShrinkerBeetle))) ModelDb.Init(ContentRegistry.AllTypes);
        var run = new RunState("certified-a10-ranges", new Overgrowth(), ascensionLevel: 10);
        var ranges = NativeInitialHpCondition.CertifiedRanges.ToArray();
        Assert.Equal(54, ranges.Length);
        foreach (var (id, min, max) in ranges)
        {
            var type = ContentRegistry.AllTypes.Single(type => type.Name == id);
            var model = (MonsterModel)ModelDb.Get(type).MutableClone();
            var state = new CombatState(run);
            int callbacks = 0;
            using var scope = LabelRandomScope.Enter(_ => 0, beginMonsterHp: context =>
            {
                callbacks++;
                Assert.True(context.MinHp == min && context.MaxHp == max,
                    $"{id}: certificate {min}..{max}, native {context.MinHp}..{context.MaxHp}");
                return null;
            });
            state.AddMonster(model, CombatSide.Enemy);
            Assert.Equal(1, callbacks);
        }
    }

    [Fact]
    public void PublicHpRangeIsCheckedAgainstActualNativeSetup()
    {
        Assert.True(NativeInitialHpCondition.TryCreate(Root(("ShrinkerBeetle", 41)), out var condition, out _));
        if (!ModelDb.Contains(typeof(ShrinkerBeetle))) ModelDb.Init(ContentRegistry.AllTypes);
        var state = new CombatState(new RunState("bad-hp-context", new Overgrowth(), ascensionLevel: 10));
        using var scope = LabelRandomScope.Enter(_ => 0, beginMonsterHp: context =>
        {
            Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal(
                context with { MinHp = 39 }, [], () => 0));
            return null;
        });
        state.AddMonster((MonsterModel)ModelDb.Monster<ShrinkerBeetle>().MutableClone(), CombatSide.Enemy);
    }
}
