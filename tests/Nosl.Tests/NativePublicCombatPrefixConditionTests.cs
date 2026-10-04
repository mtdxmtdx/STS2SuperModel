using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class NativePublicCombatPrefixConditionTests
{
    private static PublicCard Card(string id) => new(id, 0, 1, -1, "Skill", []);
    private static NativeEntryAssets Entry(string relic = "Pomander") => new("nosl.native-entry-assets.v1", 56, 70, 99,
        new[] { "StrikeSilent", "StrikeSilent", "DefendSilent", "DefendSilent", "Nightmare", "Neutralize", "Survivor", "AscendersBane" }.Select(Card).ToArray(),
        [new("RingOfTheSnake", new Dictionary<string, int> { ["isMelted"] = 0 }), new(relic, new Dictionary<string, int>())],
        ["FirePotion", null], 3, 2, 0, 0);
    private static PublicEvidenceAssets Assets(NativeEntryAssets entry) => new(entry.Hp, entry.MaxHp, entry.Gold,
        entry.Deck, entry.Relics, entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
    private static PublicRunEvidenceRecorder Recorder(bool began = true) => new(began ? new("Silent", 10, Assets(Entry())) : null);
    private static DecisionPacket Opening(string relic = "Pomander")
    {
        var entry = Entry(relic);
        var hand = entry.Deck.Take(7).ToArray();
        PublicEvent[] history = [new("combat_started", "Silent:A10"), new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
            .. hand.Select(card => new PublicEvent("draw", PublicJson.Serialize(card))), new("player_turn", "1"),
            new("intent_published", PublicJson.Serialize(new { slot = 0, id = "SludgeSpinner", intents = Array.Empty<PublicIntent>() }))];
        return new("player_decision", new("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            hand, [], [], [new(entry.Deck[^1], 1)], [], 1, entry.Potions, ["RingOfTheSnake", relic], [],
            [new(0, "SludgeSpinner", 42, 42, 0, [], [])], history, null), [new(0, "end_turn")]);
    }

    [Fact]
    public void CountsAllCombatOwnersAndFreezesTheirFirstStableSnapshots()
    {
        var recorder = Recorder();
        long eventOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        long forced = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1, eventOwner);
        recorder.Record(forced, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(forced, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        recorder.Record(eventOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        long earlier = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2);
        var opening = Opening();
        long first = recorder.ObserveCombatDecision(earlier, opening);
        var later = opening with { Observation = opening.Observation! with
        {
            Turn = 2, Enemies = [opening.Observation.Enemies[0] with { Hp = 1, MaxHp = 999 }],
            History = [.. opening.Observation.History, new("draw", PublicJson.Serialize(Card("Nightmare"))), new("shuffle", "known_positions_reset")],
        } };
        recorder.ObserveCombatDecision(earlier, later);
        recorder.Record(earlier, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        long current = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 3);
        recorder.ObserveCombatDecision(current, opening);
        var evidence = recorder.Capture();
        string before = PublicRunEvidenceJson.Serialize(evidence);
        var condition = NativePublicCombatPrefixCondition.Create(opening with { PublicEvidence = evidence });
        Assert.Equal(3, condition.Combats.Count);
        Assert.Equal("no_observed_stable_decision", condition.Combats[0].ShuffleReason);
        Assert.Equal(2, condition.EligibleShuffleCount);
        Assert.Equal(2, condition.EligibleHpCombatCount);
        Assert.Equal(2, condition.EligibleHpCount);
        Assert.Equal(first, condition.Combats[1].DecisionEventOrdinal);
        Assert.Equal(earlier, condition.Combats[1].OwnerOrdinal);
        Assert.Equal(current, condition.Combats[2].OwnerOrdinal);
        Assert.Equal(7, condition.Combats[1].Shuffle!.DrawPrefixIds.Length);
        Assert.Equal(42, condition.Combats[1].Hp!.Targets["SludgeSpinner"].TargetHp);
        Assert.Equal(PublicJson.Serialize(Entry()), condition.Combats[1].EntryJson);
        condition.Combats[1].Shuffle!.DrawPrefixIds[0] = "mutated";
        Assert.Equal("StrikeSilent", condition.Combats[1].Shuffle!.DrawPrefixIds[0]);
        Assert.Equal(before, PublicRunEvidenceJson.Serialize(evidence));
    }

    [Theory]
    [InlineData("entry", "typed_entry_history_required")]
    [InlineData("interrupted_draw", "uninterrupted_initial_draw_history_required")]
    [InlineData("intents", "startup_intent_history_required")]
    [InlineData("unsafe_hook", "entry_hook_not_certified:GamblingChip.AfterSideTurnStart")]
    [InlineData("gap", "complete_combat_history_required")]
    [InlineData("owner_start", "complete_combat_history_required")]
    [InlineData("upgrade", "initial_draw_upgrade_pool_mismatch")]
    public void MissingOrUncertifiedStartupRemainsUnconditionedWithAReason(string change, string reason)
    {
        var recorder = Recorder();
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2, completeFromOwnerStart: change != "owner_start");
        var packet = Opening(change == "unsafe_hook" ? "GamblingChip" : "Pomander");
        var history = packet.Observation!.History;
        packet = packet with { Observation = packet.Observation with { History = change switch
        {
            "entry" => history.Where(e => e.Kind != NativeEntryAssets.EventKind).ToArray(),
            "intents" => history.Where(e => e.Kind != "intent_published").ToArray(),
            "interrupted_draw" => [.. history.Take(4), new("shuffle", "known_positions_reset"), .. history.Skip(4)],
            "upgrade" => [.. history.Take(2), new("draw", PublicJson.Serialize(Card("StrikeSilent") with { Upgrade = 1 })), .. history.Skip(3)],
            _ => history,
        } } };
        if (change == "gap") recorder.RecordGap(owner, PublicEvidenceGapReason.ObservationMissing);
        recorder.ObserveCombatDecision(owner, packet);
        var condition = NativePublicCombatPrefixCondition.Create(recorder.Capture());
        Assert.Equal(0, condition.EligibleShuffleCount);
        Assert.Equal(0, condition.EligibleHpCount);
        Assert.Equal(reason, condition.Combats[0].ShuffleReason);
        Assert.Equal(reason, condition.Combats[0].HpReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRunStartOrGlobalGapNeverGuessesNativeCombatIndices(bool globalGap)
    {
        var recorder = Recorder(globalGap);
        if (globalGap) recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 4);
        recorder.ObserveCombatDecision(owner, Opening());
        var condition = NativePublicCombatPrefixCondition.Create(recorder.Capture());
        Assert.Equal("public_combat_index_not_certified", condition.Combats[0].ShuffleReason);
        Assert.Equal(0, condition.EligibleShuffleCount);
    }

    [Fact]
    public void AnOwnerLocalGapDoesNotEraseEarlierOrLaterObservedOwnerStarts()
    {
        var recorder = Recorder();
        for (int i = 0; i < 3; i++)
        {
            long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, i + 2);
            if (i == 1) recorder.RecordGap(owner, PublicEvidenceGapReason.ObservationMissing);
            recorder.ObserveCombatDecision(owner, Opening());
            recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        }
        var condition = NativePublicCombatPrefixCondition.Create(recorder.Capture());
        Assert.Equal(new[] { 0, 1, 2 }, condition.Combats.Keys);
        Assert.Equal(2, condition.EligibleShuffleCount);
        Assert.Equal("complete_combat_history_required", condition.Combats[1].ShuffleReason);
        Assert.NotNull(condition.Combats[0].Hp);
        Assert.NotNull(condition.Combats[2].Hp);
    }

    [Fact]
    public void PublishedNativeCombatCoordinateMustAgreeWithObserverCount()
    {
        var recorder = Recorder();
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2);
        var packet = Opening();
        recorder.ObserveCombatDecision(owner, packet with { Observation = packet.Observation! with
        {
            Schema = PublicRunContext.ObservationSchema,
            RunContext = new(PublicRunContext.Version, 0, 2, 1, true),
        } });
        var condition = NativePublicCombatPrefixCondition.Create(recorder.Capture());
        Assert.Equal("public_combat_index_disagrees", condition.Combats[0].ShuffleReason);
    }

    [Fact]
    public void NonPublicSourcesCannotSupplyMissingTypedEvidence()
    {
        var condition = NativePublicCombatPrefixCondition.Create(Opening());
        Assert.Empty(condition.Combats);
        Assert.Equal("public_run_evidence_required", condition.Reason);
    }
}
