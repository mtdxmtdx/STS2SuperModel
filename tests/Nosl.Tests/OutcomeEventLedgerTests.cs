using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;

namespace Nosl.Tests;

public sealed class OutcomeEventLedgerTests
{
    [Fact]
    public async Task OverkillRevivalAndVictoryHealRecordActualCommittedAmounts()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "ledger-revival", Hp: 1,
            Potions: ["FairyInABottle"], Relics: ["BurningBlood"]));
        await session.StepAsync(session.Observe().Actions.Single(a => a.Kind == "end_turn"));
        Assert.True(session.State.Players[0].Creature.IsAlive);
        await Win(session);
        var outcome = Outcome(session);
        Assert.Equal(1, outcome.CumulativeHpDamage);
        Assert.Equal(outcome.HpAfterSettlement, outcome.HealingReceived);
        Assert.Equal(0, outcome.OtherHpAdjustment);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        var resource = Assert.Single(outcome.ResourceEvents);
        Assert.Equal(("consumed", "FairyInABottle", 1), (resource.Kind, resource.ResourceId, resource.Quantity));
        Assert.True(outcome.ResourceProvenanceComplete);
        Assert.Empty(outcome.InventoryEnd);
        Assert.Equal(2, session.Knowledge.OutcomeLedger.HpEvents.Count(e => e.Kind == "heal"));
    }

    [Fact]
    public async Task DirectSetsMaxCapsAndHealingRemainObservableAfterCombatDetach()
    {
        await using var session = await CombatSession.CreateAsync(new(Hp: 10, MaxHp: 20, Relics: ["BurningBlood"]));
        var player = session.State.Players.Single();
        var ledger = session.Knowledge.OutcomeLedger;
        Assert.Empty(ledger.HpEvents); // All acquisition and initial HP setup precede the fixed anchor.
        await CreatureCmd.Heal(player.Creature, 100);
        await CreatureCmd.SetCurrentHp(player.Creature, 7);
        await CreatureCmd.SetMaxHp(player.Creature, 5);
        await player.AfterCombatEnd();
        Assert.Null(player.Creature.CombatState);
        await CreatureCmd.SetCurrentHp(player.Creature, 2);
        await player.Relics.OfType<BurningBlood>().Single().AfterCombatVictory();
        ledger.Seal(player);
        Assert.Equal(new[] { "heal", "set", "maxcap", "set", "heal" }, ledger.HpEvents.Select(e => e.Kind));
        Assert.Equal(0, ledger.Damage);
        Assert.Equal(13, ledger.Healing); // Ten to the old cap, then three to the new cap.
        Assert.Equal(-18, ledger.OtherHpAdjustment);
        Assert.Equal(5, player.Creature.CurrentHp);
        Assert.True(ledger.HpComplete);
        int events = ledger.HpEvents.Count;
        await CreatureCmd.Heal(player.Creature, -1);
        Assert.Equal(events, ledger.HpEvents.Count); // The sealed boundary owns no later mutations.
    }

    [Fact]
    public async Task GenerationDiscardAndMidcombatClonePreserveIndependentPrefixes()
    {
        await using var source = await CombatSession.CreateAsync(new(Seed: "ledger-brew", EnemyHp: 100,
            Potions: ["EntropicBrew", "FirePotion"]));
        await source.StepAsync(source.Observe().Actions.Single(a => a.Kind == "potion" && a.Slot == 0));
        await source.StepAsync(source.Observe().Actions.Single(a => a.Kind == "discard_potion" && a.Slot == 1));
        await source.StepAsync(source.Observe().Actions.Single(a => a.Kind == "end_turn"));
        var prefix = source.Knowledge.OutcomeLedger.ResourceEvents.ToArray();
        var hpPrefix = source.Knowledge.OutcomeLedger.HpEvents.ToArray();
        Assert.Equal(1, prefix.Count(e => e.Kind == "generated")); // A10 has two potion slots.
        Assert.Equal(1, prefix.Count(e => e.Kind == "consumed"));
        Assert.Equal(1, prefix.Count(e => e.Kind == "discarded"));
        await using var branch = source.ForkExact();
        Assert.Equal(prefix, branch.Knowledge.OutcomeLedger.ResourceEvents);
        Assert.Equal(source.Knowledge.OutcomeLedger.HpEvents, branch.Knowledge.OutcomeLedger.HpEvents);
        await branch.StepAsync(branch.Observe().Actions.First(a => a.Kind == "discard_potion"));
        await CreatureCmd.Heal(branch.State.Players[0].Creature, 2);
        await Win(branch);
        Assert.Equal(prefix, source.Knowledge.OutcomeLedger.ResourceEvents);
        Assert.Equal(hpPrefix, source.Knowledge.OutcomeLedger.HpEvents);
        var outcome = Outcome(branch);
        Assert.True(outcome.ResourceProvenanceComplete);
        Assert.Equal(prefix.Length + 1, outcome.ResourceEvents.Length);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.Equal(5, outcome.CumulativeHpDamage); // The original prefix hit is counted once, with no clone reconstruction damage.
        Assert.Equal(2, outcome.HealingReceived);
        await Win(source);
        Assert.True(Outcome(source).ResourceProvenanceComplete);
    }

    [Fact]
    public async Task OutcomeObserverDoesNotChangeNativeActionsStateOrRandomStreams()
    {
        static async Task<(string Facts, string Random)> Run(bool track)
        {
            await using var session = await CombatSession.CreateAsync(new(Seed: "ledger-native-invariance", Hp: 20,
                Potions: ["EntropicBrew"], Relics: ["BurningBlood", "ChosenCheese"]));
            if (!track) session.Knowledge.OutcomeLedger.Detach();
            await session.StepAsync(session.Observe().Actions.Single(a => a.Kind == "potion"));
            await session.StepAsync(session.Observe().Actions.Single(a => a.Kind == "end_turn"));
            await Win(session);
            var facts = await session.SettleAsync();
            var random = JsonSerializer.Serialize(new
            {
                run = session.State.RunState.Rng.ToSerializable(), player = session.State.Players[0].PlayerRng.ToSerializable(),
                enemies = session.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray(),
            }, new JsonSerializerOptions { IncludeFields = true });
            return (PublicJson.Serialize(facts), random);
        }
        Assert.Equal(await Run(false), await Run(true));
    }

    [Fact]
    public async Task UnclassifiedRemovalStaysExplicitlyIncomplete()
    {
        await using var session = await CombatSession.CreateAsync(new(Potions: ["FirePotion"]));
        var player = session.State.Players[0];
        player.RemovePotionInternal(player.PotionSlots[0]!);
        await Win(session);
        Assert.False(Outcome(session).ResourceProvenanceComplete);
        Assert.Equal("removed", Assert.Single(Outcome(session).ResourceEvents).Kind);
        Assert.True(Outcome(session).HpEventDiagnosticsComplete);
    }

    private static async Task Win(CombatSession session)
    {
        foreach (var enemy in session.State.Enemies.ToArray()) await CreatureCmd.Kill(enemy);
        session.Room.Engine.CheckWinCondition();
        await session.SettleAsync();
    }

    private static RolloutOutcome Outcome(CombatSession session) => RolloutRecorder.Settled(session,
        session.SettleAsync().GetAwaiter().GetResult(), PublicContinuationPolicies.LegacyId, 1);
}
