using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Events;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeTerminalEdgeTests
{
    [Fact]
    public async Task L06_LethalStrikeAndNativeThornsKeepTheNativeDoubleDeathVerdict()
    {
        var setup = new Scenario(Seed: "NOSL-TERMINAL-THORNS", Encounter: "SpinyToadNormal",
            Deck: ["StrikeSilent"], Hp: 5, MaxHp: 100, EnemyHp: 6, Relics: ["MeatOnTheBone"]);
        await using var session = await CombatSession.CreateAsync(setup);
        var enemy = Assert.Single(session.State.Enemies);
        var native = await RunNative(setup, "end_turn", "StrikeSilent");

        await session.StepAsync(Action(session, "end_turn"));
        Assert.Equal(5, enemy.GetPower<ThornsPower>()!.Amount);
        Assert.Equal(5, session.State.Players.Single().Creature.CurrentHp);
        Assert.Equal("terminal_settled", (await session.StepAsync(Action(session, "StrikeSilent"))).Status);

        // The native BeforeDamageReceived retaliation kills the player, then the
        // in-flight Strike still kills the toad. Use the native verdict, not enemy HP.
        Assert.Equal(0, enemy.CurrentHp);
        Assert.Equal(0, native.Enemy.CurrentHp);
        Assert.False(native.Room.Engine.Won);
        var facts = await session.SettleAsync();
        Assert.Equal("loss", facts.Result);
        Assert.Equal(0, facts.FinalHp);
        Assert.Empty(session.Room.GeneratedRewards);
        Assert.Empty(native.Room.GeneratedRewards);
        var outcome = AssertNativeSettlement(session, facts, native, lastTurn: 2);
        Assert.False(outcome.PlayerAlive);
        Assert.Equal(5, outcome.CumulativeHpDamage);
        Assert.Equal(0, outcome.HealingReceived); // MeatOnTheBone cannot rescue a loss.
        Assert.Empty(outcome.ResourceEvents);
        await AssertSettlementDoesNotRepeat(session, facts, native);
    }

    [Fact]
    public async Task L06_DelayedBombWaitsForRegenAndNativeVictorySettlementBeforeSealingLedger()
    {
        var setup = new Scenario(Seed: "NOSL-TERMINAL-DELAYED", Encounter: "SpinyToadNormal",
            Deck: ["TheBomb"], Hp: 38, MaxHp: 100, EnemyHp: 40,
            Potions: ["RegenPotion"], Relics: ["MeatOnTheBone"]);
        await using var session = await CombatSession.CreateAsync(setup);
        var enemy = Assert.Single(session.State.Enemies);
        var native = await RunNative(setup, "RegenPotion", "TheBomb", "end_turn", "end_turn", "end_turn");
        await session.StepAsync(Action(session, "RegenPotion"));
        await session.StepAsync(Action(session, "TheBomb"));
        var player = session.State.Players.Single();
        Assert.Equal(3, player.Creature.GetPower<TheBombPower>()!.Amount);
        Assert.Equal(38, player.Creature.CurrentHp);
        Assert.False(session.Knowledge.OutcomeLedger.HpComplete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SettleAsync());

        foreach (var (hp, countdown) in new[] { (43, 2), (22, 1) })
        {
            Assert.Equal("player_decision", (await session.StepAsync(Action(session, "end_turn"))).Status);
            Assert.True(session.Room.Engine.IsInProgress);
            Assert.Equal(hp, player.Creature.CurrentHp);
            Assert.Equal(countdown, player.Creature.GetPower<TheBombPower>()!.Amount);
            Assert.Equal(40, enemy.CurrentHp);
            Assert.Empty(session.Room.GeneratedRewards);
        }

        Assert.Equal("terminal_settled", (await session.StepAsync(Action(session, "end_turn"))).Status);
        var facts = await session.SettleAsync();
        Assert.Equal("win", facts.Result);
        Assert.True(native.Room.Engine.Won);
        Assert.Equal(0, enemy.CurrentHp);
        Assert.Equal(0, native.Enemy.CurrentHp);
        // Third-turn Regen heals 22 -> 25 before TheBomb; native victory then heals 25 -> 37.
        using var before = JsonDocument.Parse(Assert.Single(facts.Events, e => e.Kind == "pre_settlement").Detail);
        Assert.Equal(25, before.RootElement.GetProperty("hp").GetInt32());
        Assert.Equal(37, facts.FinalHp);
        Assert.Empty(player.Creature.Powers);
        Assert.Empty(native.Player.Creature.Powers);
        Assert.Single(session.Room.GeneratedRewards);
        Assert.Single(native.Room.GeneratedRewards);
        var outcome = AssertNativeSettlement(session, facts, native, lastTurn: 3);
        Assert.Equal(25, outcome.CumulativeHpDamage);
        Assert.Equal(24, outcome.HealingReceived);
        Assert.Equal(new[] { "heal:38:43", "heal:43:47", "loss:47:22", "heal:22:25", "heal:25:37" },
            native.Trace.Hp.Select(e => $"{e.Kind}:{e.Before}:{e.After}"));
        var consumed = Assert.Single(outcome.ResourceEvents);
        Assert.Equal("RegenPotion", consumed.ResourceId);
        Assert.Equal("consumed", consumed.Kind);
        Assert.Single(outcome.InventoryStart, item => item.ResourceId == "RegenPotion" && item.Count == 1);
        Assert.Empty(outcome.InventoryEnd);
        await AssertSettlementDoesNotRepeat(session, facts, native);
    }

    private static PublicAction Action(CombatSession session, string step)
    {
        var packet = session.Observe();
        return packet.Actions.Single(action => step switch
        {
            "end_turn" => action.Kind == "end_turn",
            "RegenPotion" => action.Kind == "potion" && packet.Observation!.Potions[action.Slot] == step,
            _ => action.Kind == "play" && packet.Observation!.Hand[action.Slot].Id == step,
        });
    }

    private static RolloutOutcome AssertNativeSettlement(
        CombatSession session, TerminalFacts facts, NativeResult native, int lastTurn)
    {
        Assert.Equal(1, native.Trace.SettlementCallbacks);
        Assert.Equal(facts.FinalHp, native.Trace.HpAtSettlementCallback);
        Assert.Equal(native.Trace.Hp.Count, native.Trace.HpMutationsAtSettlementCallback);
        Assert.Equal(native.Trace.Resources.Count, native.Trace.ResourceMutationsAtSettlementCallback);
        Assert.Equal(session.Room.GeneratedRewards.Count, native.Trace.RewardSetsAtSettlementCallback);
        Assert.Equal(0, native.Decisions.Remaining);
        Assert.Equal(0, native.Decisions.RewardDecisions);
        Assert.False(native.Room.Engine.IsInProgress);
        Assert.Equal(native.Room.Engine.Won ? "win" : "loss", facts.Result);
        Assert.Equal(native.Player.Creature.CurrentHp, facts.FinalHp);
        Assert.Equal(native.Player.Creature.MaxHp, facts.FinalMaxHp);
        Assert.Equal(native.Player.PotionSlots.Select(p => p?.GetType().Name), facts.Potions);
        Assert.Equal(native.Trace.Hp, session.Knowledge.OutcomeLedger.HpEvents);
        Assert.Equal(native.Trace.Resources, session.Knowledge.OutcomeLedger.ResourceEvents);
        Assert.Equal(0, facts.RewardSelectionsMade);
        Assert.Equal("AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION", facts.Boundary);
        var outcome = RolloutRecorder.Settled(session, facts, "native-terminal-edge", lastTurn);
        Assert.True(outcome.IsTrueTerminal && outcome.SettlementComplete);
        Assert.True(outcome.HpEventDiagnosticsComplete && outcome.ResourceProvenanceComplete);
        Assert.Equal((double)facts.FinalHp,
            facts.StartHp - outcome.CumulativeHpDamage!.Value + outcome.HealingReceived!.Value + outcome.OtherHpAdjustment!.Value);
        return outcome;
    }

    private static async Task AssertSettlementDoesNotRepeat(CombatSession session, TerminalFacts facts, NativeResult native)
    {
        string terminal = PublicJson.Serialize(facts);
        string nativeHp = PublicJson.Serialize(native.Trace.Hp);
        string nativeResources = PublicJson.Serialize(native.Trace.Resources);
        string adapterHp = PublicJson.Serialize(session.Knowledge.OutcomeLedger.HpEvents);
        var offered = session.Room.GeneratedRewards.ToArray();
        var nativeOffered = native.Room.GeneratedRewards.ToArray();
        await session.Room.ResolveOutcomeAsync();
        await native.Room.ResolveOutcomeAsync();
        Assert.Same(facts, await session.SettleAsync());
        Assert.Equal(terminal, PublicJson.Serialize(await session.SettleAsync()));
        Assert.Equal(nativeHp, PublicJson.Serialize(native.Trace.Hp));
        Assert.Equal(nativeResources, PublicJson.Serialize(native.Trace.Resources));
        Assert.Equal(adapterHp, PublicJson.Serialize(session.Knowledge.OutcomeLedger.HpEvents));
        Assert.Equal(facts.FinalHp, session.State.Players.Single().Creature.CurrentHp);
        Assert.Equal(facts.FinalHp, native.Player.Creature.CurrentHp);
        Assert.Equal(offered, session.Room.GeneratedRewards);
        Assert.Equal(nativeOffered, native.Room.GeneratedRewards);
    }

    private sealed record NativeResult(CombatRoom Room, Player Player, Creature Enemy, NativeTrace Trace, ScriptedDecisions Decisions);

    private static async Task<NativeResult> RunNative(Scenario setup, params string[] steps)
    {
        // Independent native host path: construct declared carry-in assets, then let
        // RunDriver own every action, verdict, automatic hook and settlement callback.
        var run = EncounterCoverage.CreateRun(setup.Seed, setup.Encounter, 10);
        var player = run.Players.Single();
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        foreach (string id in setup.Deck!)
        {
            var card = (CardModel)ModelDb.All<CardModel>().Single(model => model.GetType().Name == id).MutableClone();
            card.AssignOwner(player);
            player.Deck.AddInternal(card);
        }
        player.Creature.SetMaxHpInternal(setup.MaxHp!.Value);
        player.Creature.SetCurrentHpInternal(setup.Hp!.Value);
        foreach (string id in setup.Potions ?? [])
        {
            var potion = (PotionModel)ModelDb.All<PotionModel>().Single(model => model.GetType().Name == id).MutableClone();
            potion.AssignOwner(player);
            player.AddPotionInternal(potion);
        }
        foreach (string id in setup.Relics ?? [])
            await RelicCmd.Obtain((RelicModel)ModelDb.All<RelicModel>().Single(model => model.GetType().Name == id).MutableClone(), player);
        var trace = new NativeTrace();
        player.OutcomeObserver = trace;
        var decisions = new ScriptedDecisions(steps);
        Creature? enemy = null;
        var driver = new RunDriver(run, decisions, useAvailablePotions: false)
        {
            AutomaticCombatSettlementCompleted = () =>
            {
                var room = Assert.IsType<CombatRoom>(run.CurrentRoom);
                Assert.False(room.Engine.IsInProgress);
                Assert.Equal(room.Engine.Won, room.Won);
                trace.SettlementCallbacks++;
                trace.HpAtSettlementCallback = player.Creature.CurrentHp;
                trace.HpMutationsAtSettlementCallback = trace.Hp.Count;
                trace.ResourceMutationsAtSettlementCallback = trace.Resources.Count;
                trace.RewardSetsAtSettlementCallback = room.GeneratedRewards.Count;
            },
        };
        try
        {
            var room = await driver.RunOneInjectedCombatAsync(RoomType.Monster,
                EncounterCoverage.Find(setup.Encounter!).Definition.IdEntry, (_, state) =>
            {
                enemy = Assert.Single(state.Enemies);
                enemy.SetMaxHpInternal(setup.EnemyHp!.Value);
                enemy.SetCurrentHpInternal(setup.EnemyHp.Value);
            });
            return new(room, player, enemy!, trace, decisions);
        }
        finally { player.OutcomeObserver = null; }
    }

    private sealed class ScriptedDecisions(IEnumerable<string> steps) : IRunDecisionSource
    {
        private readonly Queue<string> _steps = new(steps);
        public int Remaining => _steps.Count;
        public int RewardDecisions { get; private set; }
        public Task<CombatDecision> ChooseCombatActionAsync(CombatState state)
        {
            Assert.NotEmpty(_steps); // An unexpected extra decision fails instead of looping.
            string step = _steps.Dequeue();
            var player = state.Players.Single();
            if (step == "end_turn") return Task.FromResult<CombatDecision>(new CombatDecision.EndTurn());
            if (step == "RegenPotion")
                return Task.FromResult<CombatDecision>(new CombatDecision.UsePotion(
                    player.PotionSlots.Single(p => p?.GetType().Name == step)!, player.Creature));
            var card = player.PlayerCombatState!.Hand.Cards.Single(c => c.GetType().Name == step);
            return Task.FromResult<CombatDecision>(new CombatDecision.PlayCard(card,
                card.TargetType == TargetType.AnyEnemy ? Assert.Single(state.HittableEnemies) : null));
        }
        public Task<MapPoint> ChooseMapPointAsync(IReadOnlyList<MapPoint> options) => throw new NotSupportedException();
        public Task<EventOption> ChooseEventOptionAsync(IReadOnlyList<EventOption> options) => throw new NotSupportedException();
        public Task<RewardDecision> ChooseRewardActionAsync(RewardsSet rewards)
        {
            RewardDecisions++;
            throw new InvalidOperationException("The native comparison must stop before its first reward decision.");
        }
    }

    private sealed class NativeTrace : IPlayerOutcomeObserver
    {
        public List<HpMutation> Hp { get; } = [];
        public List<ResourceEvent> Resources { get; } = [];
        public int SettlementCallbacks { get; set; }
        public int HpAtSettlementCallback { get; set; }
        public int HpMutationsAtSettlementCallback { get; set; }
        public int ResourceMutationsAtSettlementCallback { get; set; }
        public int RewardSetsAtSettlementCallback { get; set; }
        public void HpChanged(Player player, HpMutationKind kind, int before, int after, int maxBefore, int maxAfter)
        {
            if (before != after || maxBefore != maxAfter)
                Hp.Add(new(kind.ToString().ToLowerInvariant(), before, after, maxBefore, maxAfter));
        }
        public void PotionChanged(Player player, string potionId, PotionMutationKind kind)
        {
            Assert.Equal(PotionMutationKind.Consumed, kind);
            Resources.Add(new("consumed", potionId, 1, "native_potion_slot_consumed"));
        }
    }
}
