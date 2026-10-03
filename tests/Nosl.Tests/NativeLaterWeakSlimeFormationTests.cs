using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeLaterWeakSlimeFormationTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 4, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task LaterWeakPullUsesEveryCombatOwnerAndForcesOnlyItsNativeFactory(int normalSlot, bool eventCombat)
    {
        var root = await LaterRoot(normalSlot, eventCombat);
        string before = PublicJson.Serialize(root);
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(root, Prior, out var sequence, out var reason), reason);
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out reason), reason);
        Assert.Equal(normalSlot, sequence!.Targets.Single(target => target.CombatOwnerOrdinal == condition!.CombatOwnerOrdinal).NormalSlot);
        Assert.Equal(normalSlot + (eventCombat ? 1 : 0), condition!.CombatIndex);
        Assert.Equal(new[] { "LeafSlimeS", "LeafSlimeM", "TwigSlimeS" }, condition.Roster);
        // The current snapshot can lose an original slime. Retained startup
        // evidence, including its physical order, still owns the proposal.
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root with
            { Observation = root.Observation! with { Enemies = [] } }, Prior, out var retained, out reason), reason);
        Assert.Equal(condition.Roster, retained!.Roster);

        var run = new RunState("later-weak-slime-owned", new Overgrowth(), 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        var encounter = run.Act.MonsterEncounterCandidates.Single(item => item.IdEntry == "SLIMES_WEAK");
        var rng = new Rng(7654);
        var room = new CombatRoom(() => (encounter, encounter.CreateMonsters(rng)), RoomType.Monster);
        var context = new LabelSlimesWeakFormationContext(run, room, encounter, rng, rng);
        int sampled = 0, forced = 0;
        var proposal = new NativePublicWeakSlimeFormationProposal(condition,
            () => { sampled++; return ulong.MaxValue; }, (words, stream, _) =>
            {
                Assert.Same(rng, stream); forced += words.Count;
                var pending = new Queue<ulong>(words);
                return LabelRandomScope.Enter(_ => pending.Dequeue());
            });
        proposal.AttachHypotheticalRun(run);
        var entry = PublicJson.Read<NativeEntryAssets>(condition.EntryJson);
        for (int index = 0; index < condition.CombatIndex; index++)
        {
            proposal.CombatEntering(index, entry, run.Rng.Shuffle);
            Assert.Null(proposal.BeginFormation(context));
        }
        Assert.Equal(0, sampled); Assert.Equal(0, forced);
        proposal.CombatEntering(condition.CombatIndex, entry, run.Rng.Shuffle);
        run.PushRoom(room);
        using (LabelSlimesWeakScope.Enter(proposal.BeginFormation)) await room.Enter(run);
        proposal.ValidateCompletion();
        Assert.Equal(condition.Roster, room.Engine.State.Enemies.Select(enemy => enemy.Monster!.GetType().Name));
        Assert.Equal(3, forced); Assert.Equal(3, rng.Counter); Assert.Equal(1, proposal.ConditionedCombatCount);
        Assert.Equal(new ShuffleRational(1, 4), proposal.NativeToProposalRatio);
        Assert.Equal(proposal.NativeToProposalRatio, proposal.Envelope);
        Assert.True(proposal.AcceptCorrection(() => throw new InvalidOperationException("Fixed public mass cancels")));
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    [Theory]
    [InlineData("route_gap")]
    [InlineData("event_parent")]
    [InlineData("fourth_normal")]
    public async Task UnprovedLaterNormalOriginDisablesOnlyTheFormationProposal(string change)
    {
        var root = await LaterRoot(change == "fourth_normal" ? 3 : 1, false, change);
        Assert.False(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out _));
        Assert.Null(condition);
    }

    private static async Task<DecisionPacket> LaterRoot(int normalSlot, bool eventCombat, string? change = null)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var plan = NativeWeakSlimeFormationPlan.Create(
            ["LeafSlimeS", "LeafSlimeM", "TwigSlimeS"], new Rng(8989).NextUnsignedLong);
        using var formation = LabelSlimesWeakScope.Enter(_ =>
        {
            var words = new Queue<ulong>(plan.RawWords);
            return LabelRandomScope.Enter(_ => words.Dequeue());
        });
        await using var session = await CombatSession.CreateAsync(new(Seed: "later-weak-slime-public", Encounter: "SlimesWeak"));
        var packet = session.Observe();
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        packet = packet with { Observation = packet.Observation! with
        { History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)), .. packet.Observation.History.Skip(1)] } };
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        int row = 0;
        void Map(PublicMapNodeType kind)
        {
            long map = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, row + 1);
            var from = new PublicMapCoordinate(0, row); var to = new PublicMapCoordinate(0, row + 1);
            long offered = recorder.Record(map, new PublicMapObserved(from,
                [new(from, row == 0 ? PublicMapNodeType.Ancient : PublicMapNodeType.Monster), new(to, kind)],
                [new(from, to)], [new(to, true)]));
            recorder.Record(map, new PublicMapChosen(offered, to));
            recorder.Record(map, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed)); row++;
        }
        void EarlierCombat(string model, long? parent = null)
        {
            long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, row + 1, parent);
            recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.Started));
            recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
            recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0, model: model));
            recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        for (int slot = 0; slot < normalSlot; slot++)
        {
            Map(PublicMapNodeType.Monster);
            EarlierCombat(new[] { "FuzzyWurmCrawler", "Nibbit", "ShrinkerBeetle" }[slot]);
        }
        if (eventCombat)
        {
            Map(PublicMapNodeType.Unknown);
            long evt = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, row + 1);
            EarlierCombat("FuzzyWurmCrawler", evt);
            recorder.Record(evt, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        Map(PublicMapNodeType.Unknown);
        if (change == "route_gap") recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
        long? parent = change == "event_parent" ? recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, row + 1) : null;
        long target = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, row + 1, parent);
        recorder.ObserveCombatDecision(target, packet);
        return packet with { PublicEvidence = recorder.Capture() };
    }
}
