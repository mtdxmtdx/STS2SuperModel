using System.Collections.Immutable;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativeFairyDrawClosureTests
{
    private sealed class Recorder(CombatSession session)
    {
        private readonly NativeEntryAssets _entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        private PublicRunEvidenceRecorder? _recorder;
        private long _owner;
        internal DecisionPacket Record(DecisionPacket packet)
        {
            if (_recorder is null)
            {
                var assets = new PublicEvidenceAssets(_entry.Hp, _entry.MaxHp, _entry.Gold, _entry.Deck,
                    _entry.Relics, _entry.Potions.ToImmutableArray(), _entry.MaxEnergy, _entry.PotionSlots, _entry.OrbSlots, _entry.CardRemovalsUsed);
                _recorder = new(new("Silent", 10, assets));
                _owner = _recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
            }
            packet = packet with { Observation = packet.Observation! with
            {
                // Explicit constructed-fixture owner coordinates, not an inferred natural run.
                Schema = PublicRunContext.ObservationSchema,
                RunContext = new(PublicRunContext.Version, 0, 1, 0, true),
                History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(_entry)), .. packet.Observation.History.Skip(1)],
            } };
            _recorder.ObserveCombatDecision(_owner, packet);
            return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet with { PublicEvidence = _recorder.Capture() }));
        }
    }

    private sealed record Revival(DecisionPacket Opening, DecisionPacket AfterRevival, RolloutOutcome Outcome);

    private static async Task<Revival> NativeRevival(int count)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "fairy-draw-closure:" + count,
            Deck: [.. Enumerable.Repeat("DaggerSpray", 8), .. Enumerable.Repeat("Deflect", 4)],
            Potions: Enumerable.Repeat("FairyInABottle", count).ToArray(), Hp: 1, MaxHp: 10,
            Enemies: Enumerable.Repeat("TwigSlimeS", count).ToArray(), EnemyHp: 1));
        var recorder = new Recorder(session);
        var opening = recorder.Record(session.Observe());
        var current = recorder.Record(await session.StepAsync(opening.Actions.Single(a => a.Kind == "end_turn")));
        Assert.Equal("player_decision", current.Status);
        Assert.Equal(3, current.Observation!.Hp);
        Assert.All(current.Observation.Potions, Assert.Null);
        Assert.Equal(count, current.PublicEvidence!.Events.Count(e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.PotionUsed, Model: "FairyInABottle" }));
        var afterRevival = current;
        current = await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "DaggerSpray"));
        Assert.Equal("terminal_settled", current.Status);
        return new(opening, afterRevival, RolloutRecorder.Settled(session, await session.SettleAsync(), PublicContinuationPolicies.ReviewedId, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CountedAutomaticRevivalPreservesDrawPrefixAndTerminalResourceMasks(int count)
    {
        var fixture = await NativeRevival(count);
        string before = PublicJson.Serialize(fixture.AfterRevival);
        Assert.False(NativeInitialShuffleCondition.TryCreate(fixture.Opening, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV2(fixture.Opening, out _, out _));
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV3(fixture.Opening, out _, out _));
        Assert.True(NativeInitialShuffleCondition.TryCreatePublicCombatV4(fixture.Opening, out _, out string? reason), reason);
        var input = NativePublicCombatPrefixCondition.Create(fixture.AfterRevival).Combats[0];
        Assert.Equal("observed_prefix_complete", input.DrawPrefix!.StopReason);
        Assert.Equal(12, input.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal(fixture.AfterRevival.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicCombatFact>()
            .Where(f => f.FactKind == PublicCombatFactKind.CardDrawn).Select(f => f.Cards.Single().Id), input.Shuffle.DrawPrefixIds);
        Assert.Equal(before, PublicJson.Serialize(fixture.AfterRevival));

        var outcome = fixture.Outcome;
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.True(outcome.SettlementComplete);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.Equal(1 + (count - 1) * 3, outcome.CumulativeHpDamage);
        Assert.Equal(count * 3, outcome.HealingReceived);
        Assert.Equal(3, outcome.HpAfterSettlement);
        Assert.True(outcome.InventorySnapshotsComplete);
        Assert.True(outcome.ResourceProvenanceComplete);
        Assert.Equal([new InventoryQuantity("FairyInABottle", count)], outcome.InventoryStart);
        Assert.Empty(outcome.InventoryEnd);
        Assert.Equal(count, outcome.ResourceEvents.Length);
        Assert.All(outcome.ResourceEvents, e => Assert.Equal(("consumed", "FairyInABottle", 1), (e.Kind, e.ResourceId, e.Quantity)));
        var batch = ObjectiveEvaluator.EvaluateBatch([outcome], 1);
        Assert.Null(batch.ExpectedCost);
        Assert.Equal(1, batch.ValueUnresolved);
        Assert.Contains("inventory_value_unresolved:FairyInABottle", batch.Reasons);
        Assert.False(batch.FormalLabelsAllowed);
        TeacherCandidate[] candidates = [new(fixture.Opening.Actions.Single(a => a.Kind == "end_turn"), [outcome], batch)];
        var result = new TeacherResult(fixture.Opening, "fairy-native-fixture", PublicContinuationPolicies.ReviewedId,
            ObjectiveProfile.Candidate.Id, candidates, 0, 1, 0, new(1, 1, 1, 2, 0, 0, 0, 0, 0),
            "constructed-native-closure-regression", [], TeacherRanking.Evaluate(candidates, ObjectiveProfile.Candidate));
        using var json = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "fairy-fixture", "combat", "branch", [1701], [])));
        var target = json.RootElement.GetProperty("targets").GetProperty("actions")[0];
        Assert.False(target.GetProperty("masks").GetProperty("value").GetBoolean());
        Assert.True(target.GetProperty("masks").GetProperty("expected_final_hp").GetBoolean());
        Assert.True(target.GetProperty("masks").GetProperty("potion_net_change").GetBoolean());
        Assert.Equal(-count, target.GetProperty("potion_net_change").GetDouble());
        Assert.Equal("objective_value_unresolved", target.GetProperty("quality").GetString());
    }

    [Theory]
    [InlineData("ReptileTrinket", "AfterPotionUsed")]
    [InlineData("LizardTail", "ShouldDieLate")]
    public async Task FairyDoesNotAdmitOtherPotionOrDeathListeners(string relic, string hook)
    {
        await using var session = await CombatSession.CreateAsync(new(Potions: ["FairyInABottle"], Relics: ["RingOfTheSnake", relic]));
        var root = new Recorder(session).Record(session.Observe());
        Assert.False(NativeInitialShuffleCondition.TryCreatePublicCombatV4(root, out _, out string? reason));
        Assert.Equal("entry_hook_not_certified:" + relic + "." + hook, reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticUseRequiresAvailableCopyAndReconciledPublicInventory(bool retainConsumed)
    {
        var fixture = await NativeRevival(1);
        var root = fixture.AfterRevival;
        var events = root.PublicEvidence!.Events.Select(e =>
        {
            if (!retainConsumed && e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.Damage, TargetSlot: -2 })
                return new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal,
                    new PublicCombatFact(PublicCombatFactKind.PotionUsed, model: "FairyInABottle"));
            if (retainConsumed && e.Payload is PublicCombatDecision { Observation.Turn: 2 } decision)
                return new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, new PublicCombatDecision(decision.Status,
                    decision.Observation with { Potions = ["FairyInABottle", null] }, decision.Actions,
                    decision.HistoryThroughEventOrdinal, decision.HistoryCompleteFromCombatStart));
            return e;
        }).ToImmutableArray();
        var condition = NativePublicCombatPrefixCondition.Create(new PublicRunEvidence(PublicRunEvidence.Version, true, events));
        Assert.Equal(retainConsumed ? "draw_cycle_automatic_potion_inventory_mismatch" : "draw_cycle_automatic_potion_not_certified",
            condition.Combats[0].DrawPrefix!.StopReason);
    }

    [Fact]
    public async Task DiscardingFairyOnlyRemovesItsListenerAndKeepsPublicInventoryConsistent()
    {
        await using var session = await CombatSession.CreateAsync(new(Potions: ["FairyInABottle"]));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "discard_potion")));
        Assert.Equal("observed_prefix_complete", NativePublicCombatPrefixCondition.Create(current).Combats[0].DrawPrefix!.StopReason);
        Assert.All(current.Observation!.Potions, Assert.Null);
    }

    [Fact]
    public async Task NativeConditionalInitialShuffleReplaysRevivalAndItsFullTerminalLedger()
    {
        var original = await NativeRevival(2);
        var entry = PublicJson.Read<NativeEntryAssets>(original.Opening.Observation!.History[1].Detail);
        var random = new Rng(278621, "fairy-independent-proposal");
        var proposal = new NativePublicCombatPrefixProposal(NativePublicCombatPrefixCondition.Create(original.AfterRevival),
            random.NextUnsignedLong, (words, _) =>
            {
                var queue = new Queue<ulong>(words);
                return LabelRandomScope.Enter(_ => queue.Dequeue());
            });
        bool attached = false;
        using var scope = LabelRandomScope.Enter(state => new MegaRandom(new SerializableRng
        { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong(),
            proposal.BeginShuffle, context =>
            {
                if (!attached)
                {
                    var run = (RunState)context.Creature.CombatState!.RunState;
                    proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, entry, run.Rng.Shuffle); attached = true;
                }
                return proposal.BeginMonsterHp(context);
            });
        var replay = await NativeRevival(2);
        proposal.ValidateCompletion();
        Assert.Equal(1, proposal.ConditionedShuffleCount);
        Assert.Equal(PublicJson.Serialize(original.AfterRevival), PublicJson.Serialize(replay.AfterRevival));
        Assert.Equal(PublicJson.Serialize(original.Outcome), PublicJson.Serialize(replay.Outcome));
        _ = proposal.AcceptCorrection(random.NextUnsignedLong);
    }
}
