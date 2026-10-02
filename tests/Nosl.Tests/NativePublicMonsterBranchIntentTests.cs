using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicMonsterBranchIntentTests
{
    private sealed record Fixture(DecisionPacket Root, NativeEntryAssets Entry);
    private static async Task<Fixture> PublicRoot(string model, int turns = 1, bool zeroTurnWord = false)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "public-branches:" + model,
            Enemy: model, EnemyHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Anchor(DecisionPacket packet) => packet with { Observation = packet.Observation! with
        {
            History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                .. packet.Observation.History.Skip(1)],
        }};
        var root = Anchor(session.Observe()); recorder.ObserveCombatDecision(owner, root);
        for (int turn = 1; turn < turns; turn++)
        {
            using var force = zeroTurnWord ? LabelRandomScope.Enter(_ => 0) : null;
            root = Anchor(await session.StepAsync(root.Actions.Single(action => action.Kind == "end_turn")));
            recorder.ObserveCombatDecision(owner, root);
        }
        return new(root with { PublicEvidence = recorder.Capture() }, entry);
    }

    private static (RunState Run, CombatRoom Room) Owned(string model)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("owned-public-branches", new Overgrowth(), 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        MonsterModel Monster() => model switch
        {
            "SludgeSpinner" => (MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone(),
            "LeafSlimeS" => (MonsterModel)ModelDb.Monster<LeafSlimeS>().MutableClone(),
            _ => throw new ArgumentException(model),
        };
        var room = new CombatRoom(Monster); run.PushRoom(room); return (run, room);
    }

    [Theory]
    [InlineData("SludgeSpinner", 2, 2, 1)]
    [InlineData("LeafSlimeS", 1, 1, 1)]
    public async Task DetachedTargetsUsePublicPublicationHistoryAndNativeRollOrdinals(string model, int turns, int rolls, int branches)
    {
        var f = await PublicRoot(model, turns);
        string before = PublicJson.Serialize(f.Root);
        var condition = NativePublicMonsterBranchIntentCondition.Create(f.Root);
        var input = Assert.Single(condition.Combats).Value;
        Assert.Equal(rolls, condition.EligibleRollCount); Assert.Equal(branches, condition.EligibleBranchCount);
        Assert.Equal(Enumerable.Range(1, turns), input.Targets.Select(target => target.RollOrdinal));
        var later = f.Root with { Observation = f.Root.Observation! with
        { Enemies = f.Root.Observation.Enemies.Select(enemy => enemy with { Intents = [new("Stun", null, null)] }).ToArray() } };
        Assert.Equal(input.Targets, NativePublicMonsterBranchIntentCondition.Create(later).Combats[0].Targets);
        Assert.Equal(before, PublicJson.Serialize(f.Root));
    }

    [Theory]
    [InlineData("SludgeSpinner", 2)]
    [InlineData("SludgeSpinner", 3)]
    [InlineData("LeafSlimeS", 1)]
    public async Task ActualOwnedNativeLifecycleConsumesExactlyOneWordPerWeightedRoll(string model, int turns)
    {
        var f = await PublicRoot(model, turns);
        var condition = NativePublicMonsterBranchIntentCondition.Create(f.Root);
        var (run, room) = Owned(model);
        var tape = new NativeLabelTape(new(456, 567, 678, 0, 0));
        var proposal = new NativePublicMonsterBranchIntentProposal(condition, new Rng(111).NextUnsignedLong, ForceTape(tape));
        proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, f.Entry, run.Rng.Shuffle);
        using var words = LabelRandomScope.Enter(Word(tape));
        using var scope = LabelMonsterMoveScope.Enter(proposal.BeginRoll, proposal.BeginBranch);
        await room.Enter(run);
        for (int turn = 1; turn < turns; turn++) await room.Engine.EndPlayerTurnAsync();
        proposal.ValidateCompletion();
        Assert.Equal(condition.EligibleBranchCount, proposal.ConditionedBranchCount);
        Assert.Equal(condition.EligibleRollCount, proposal.ConditionedRollCount);
        Assert.Equal(condition.EligibleBranchCount, run.Rng.MonsterAi.Counter);
        Assert.Equal(f.Root.Observation!.Enemies.Single().Intents.Select(intent => intent.Kind),
            PublicViews.Intents(room.Engine.State.Enemies.Single()).Select(intent => intent.Kind));
        Assert.True(proposal.NativeToProposalRatio.Numerator * proposal.Envelope.Denominator
            <= proposal.Envelope.Numerator * proposal.NativeToProposalRatio.Denominator);
        Assert.True(proposal.AcceptCorrection(() => ulong.MaxValue));
        // The proposal releases ownership after each native roll and lets later
        // unobserved rolls continue through the same native graph and stream.
        await room.Engine.EndPlayerTurnAsync();
        proposal.ValidateCompletion();
        Assert.Equal(condition.EligibleBranchCount + 1, run.Rng.MonsterAi.Counter);
    }

    [Theory]
    [InlineData("foreign_stream")]
    [InlineData("wrong_owner")]
    [InlineData("wrong_index")]
    [InlineData("missing_scope")]
    [InlineData("partial")]
    [InlineData("alias")]
    [InlineData("extra_draw")]
    [InlineData("missing_draw")]
    public async Task OwnershipAndIncompleteNativeCallbacksRemainUnresolved(string change)
    {
        var f = await PublicRoot("LeafSlimeS");
        var condition = NativePublicMonsterBranchIntentCondition.Create(f.Root);
        var (run, room) = Owned("LeafSlimeS");
        var tape = new NativeLabelTape(new(456, 567, 678, 0, 0));
        int samples = 0;
        var random = new Rng(119);
        var proposal = new NativePublicMonsterBranchIntentProposal(condition, () => { samples++; return random.NextUnsignedLong(); },
            (raw, rng, purpose) =>
            {
                if (change == "alias")
                { var snapshot = rng.ToSerializable(); Word(tape)(new(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3)); }
                var inner = ForceTape(tape)(raw, rng, purpose);
                return new DisposeAction(() => { inner.Dispose(); if (change == "extra_draw") rng.NextFloat(); });
            });
        proposal.AttachHypotheticalRun(change == "wrong_owner" ? new RunState("foreign") : run);
        if (change is "wrong_owner" or "wrong_index")
        {
            Assert.Throws<InvalidOperationException>(() => proposal.CombatEntering(change == "wrong_index" ? 1 : 0, f.Entry, run.Rng.Shuffle));
            Assert.Equal(0, samples); return;
        }
        proposal.CombatEntering(0, f.Entry, run.Rng.Shuffle);
        if (change == "partial") { Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion); return; }
        using var words = LabelRandomScope.Enter(Word(tape));
        using var scope = change == "missing_scope" ? null : LabelMonsterMoveScope.Enter(
            context => proposal.BeginRoll(change == "foreign_stream" ? new(context.Monster, run.Rng.Niche) : context),
            context =>
            {
                var resource = proposal.BeginBranch(context);
                if (change == "missing_draw") { resource!.Dispose(); return null; }
                return resource;
            });
        var error = await Record.ExceptionAsync(() => room.Enter(run));
        if (change == "missing_scope")
        { Assert.Null(error); Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion); }
        else
        {
            Assert.NotNull(error); Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            if (change == "alias") Assert.Contains("alias correction is unresolved", error.Message);
            if (change == "foreign_stream") Assert.Equal(0, samples);
        }
    }

    [Fact]
    public async Task OriginalWeightEvaluationOrderAndCallbackIsolationArePreserved()
    {
        var (run, room) = Owned("LeafSlimeS"); await room.Enter(run);
        var monster = room.Engine.State.Enemies.Single().Monster!;
        var random = (RandomBranchState)monster.MoveStateMachine!.States["RAND"];
        int calls = 0;
        for (int i = 0; i < random.States.Count; i++)
        {
            var state = random.States[i]; state.Weight = () => { calls++; return 1; }; random.States[i] = state;
        }
        using (LabelRandomScope.Enter(_ => 0)) random.GetNextState(monster.Creature, run.Rng.MonsterAi);
        Assert.Equal(3, calls); // Two Sum getters plus the selected first traversal getter.
        calls = 0; int nativeWords = 0, callbackCalls = 0;
        using var tape = LabelRandomScope.Enter(_ => { nativeWords++; return 0; });
        using var scope = LabelMonsterMoveScope.Enter(_ => null, context =>
        {
            callbackCalls++; Assert.Null(context.Roll); Assert.Equal(2, calls);
            Assert.Equal(2, context.Weights.Count); new Rng(991).NextFloat(); return new DisposeAction(() => { });
        });
        random.GetNextState(monster.Creature, run.Rng.MonsterAi);
        Assert.Equal(3, calls); Assert.Equal(1, callbackCalls); Assert.Equal(1, nativeWords);
    }

    [Theory]
    [InlineData("repeat_turn")]
    [InlineData("duplicate_slot")]
    [InlineData("missing_turn")]
    [InlineData("wrong_shape")]
    [InlineData("incomplete_owner")]
    [InlineData("gap")]
    public async Task AmbiguousOrUncertifiedPublicHistoryNeverInventsARoll(string change)
    {
        var f = await PublicRoot("SludgeSpinner", 2);
        var events = f.Root.PublicEvidence!.Events.ToArray();
        int turn = Array.FindLastIndex(events, item => item.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.PlayerTurnStarted });
        int intent = turn + 1;
        if (change == "repeat_turn") events[turn] = new(events[turn].EventOrdinal, events[turn].OwnerOrdinal,
            new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
        if (change == "missing_turn") events[turn] = new(events[turn].EventOrdinal, events[turn].OwnerOrdinal,
            new PublicCombatFact(PublicCombatFactKind.PlayerTurnEnded));
        if (change == "wrong_shape") events[intent] = new(events[intent].EventOrdinal, events[intent].OwnerOrdinal,
            new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0, model: "SludgeSpinner", intents: [new("Stun", null, null)]));
        if (change == "duplicate_slot") events[intent] = new(events[intent].EventOrdinal, events[intent].OwnerOrdinal,
            new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 1, model: "SludgeSpinner", intents: [new("Attack", 12, 1)]));
        if (change == "gap") events[turn] = new(events[turn].EventOrdinal, events[turn].OwnerOrdinal, new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted));
        if (change == "incomplete_owner")
        {
            // An incomplete owner boundary is enough to disable map lifecycle claims.
            var owner = (PublicOwnerStarted)events[1].Payload;
            events[1] = new(1, events[1].OwnerOrdinal, new PublicOwnerStarted(owner.OwnerKind, owner.ActIndex, owner.Floor, null, completeFromOwnerStart: false));
        }
        if (change is "gap" or "incomplete_owner")
            for (int i = change == "gap" ? turn : 0; i < events.Length; i++)
                if (events[i].Payload is PublicCombatDecision decision)
                    events[i] = new(events[i].EventOrdinal, events[i].OwnerOrdinal,
                        new PublicCombatDecision(decision.Status, decision.Observation, decision.Actions,
                            decision.HistoryThroughEventOrdinal, false));
        var evidence = new PublicRunEvidence(PublicRunEvidence.Version, change is not ("gap" or "incomplete_owner"), events.ToImmutableArray());
        var condition = NativePublicMonsterBranchIntentCondition.Create(f.Root with { PublicEvidence = evidence });
        Assert.Equal(0, condition.EligibleBranchCount);
    }

    [Fact]
    public async Task NativeCannotRepeatRetainsItsZeroWordRepeatAndUsesGlobalCorrection()
    {
        var f = await PublicRoot("SludgeSpinner", 2, zeroTurnWord: true);
        var condition = NativePublicMonsterBranchIntentCondition.Create(f.Root);
        Assert.All(condition.Combats[0].Targets, target => Assert.Equal(NativePublicMonsterIntentShape.AttackDebuff, target.Shape));
        var (run, room) = Owned("SludgeSpinner");
        var tape = new NativeLabelTape(new(331, 442, 553, 0, 0));
        var proposal = new NativePublicMonsterBranchIntentProposal(condition, new Rng(118).NextUnsignedLong, ForceTape(tape));
        proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, f.Entry, run.Rng.Shuffle);
        using var words = LabelRandomScope.Enter(Word(tape));
        using var scope = LabelMonsterMoveScope.Enter(proposal.BeginRoll, proposal.BeginBranch);
        await room.Enter(run); await room.Engine.EndPlayerTurnAsync(); proposal.ValidateCompletion();
        Assert.Equal(new ShuffleRational(1, 1UL << 53), proposal.NativeToProposalRatio);
        Assert.True(proposal.Envelope.Numerator * proposal.NativeToProposalRatio.Denominator
            > proposal.NativeToProposalRatio.Numerator * proposal.Envelope.Denominator);
        ulong envelope = NativePublicMonsterBranchIntentProposal.EnvelopeSize(3, 0, false);
        Assert.True(proposal.AcceptCorrection(() => 2 * envelope));
        Assert.False(proposal.AcceptCorrection(() => 2 * envelope + 1));
    }

    [Theory]
    [InlineData(11004UL)]
    [InlineData(11006UL)]
    [InlineData(11007UL)]
    public async Task MeasuredPublicRootsRetainTheSpecificPreviouslyFailingIntentTargets(ulong sourceSeed)
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
        };
        var recipe = prior.Draw(new Rng(sourceSeed, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        var root = world.Observe(); string before = PublicJson.Serialize(root);
        var condition = NativePublicMonsterBranchIntentCondition.Create(root);
        if (sourceSeed == 11004)
        {
            Assert.Contains(condition.Combats[0].Targets, target => target.IntentEventOrdinal == 54
                && target.Shape == NativePublicMonsterIntentShape.Attack && target.RollOrdinal == 2);
            var firstCycle = NativePublicCombatPrefixCondition.Create(root).Combats[0];
            var reshuffles = NativePublicReshuffleCondition.Create(root);
            long firstShuffle = reshuffles.Combats[0].Targets[0].ShuffleEventOrdinal;
            var later = condition.Combats[0].Targets.Where(target => target.IntentEventOrdinal > firstShuffle).ToArray();
            Assert.NotEmpty(later);
            Assert.All(later, target =>
            {
                Assert.True(target.RollOrdinal > 2);
                Assert.True(target.IntentEventOrdinal > firstCycle.DrawPrefix!.ThroughEventOrdinal);
                Assert.True(target.IntentEventOrdinal <= reshuffles.CombatAudits[0].ThroughEventOrdinal);
                var observed = (PublicCombatFact)root.PublicEvidence!.Events[(int)target.IntentEventOrdinal].Payload;
                Assert.Equal(PublicCombatFactKind.IntentPublished, observed.FactKind);
                Assert.Equal("SludgeSpinner", observed.Model);
            });
            Assert.All(condition.Combats, combat => Assert.All(combat.Value.Targets, target =>
                Assert.True(target.IntentEventOrdinal <= reshuffles.CombatAudits[combat.Key].ThroughEventOrdinal)));
        }
        else
            Assert.Contains(condition.Combats.Values.SelectMany(input => input.Targets), target =>
                target.Model == "LeafSlimeS" && target.RollOrdinal == 1);
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    [Theory]
    [InlineData("gap", "owner_evidence_gap")]
    [InlineData("unsupported_transition", "draw_cycle_power_not_certified:ConfusionPower")]
    public async Task ReshuffleEndpointBridgeStopsBeforeUnprovedTransitions(string change, string stop)
    {
        var f = await PublicRoot("SludgeSpinner", 3);
        var full = NativePublicMonsterBranchIntentCondition.Create(f.Root);
        Assert.Equal([1, 2, 3], full.Combats[0].Targets.Select(target => target.RollOrdinal));
        var events = f.Root.PublicEvidence!.Events.ToArray();
        int shuffle = Array.FindIndex(events, item => item.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.Shuffled });
        Assert.True(shuffle >= 0);
        int changed = shuffle + 1;
        events[changed] = new(events[changed].EventOrdinal, events[changed].OwnerOrdinal,
            change == "gap" ? new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)
                : new PublicCombatFact(PublicCombatFactKind.PowerChanged, targetSlot: -2, model: "ConfusionPower", amount: 1));
        if (change == "gap")
            for (int i = changed + 1; i < events.Length; i++)
                if (events[i].Payload is PublicCombatDecision decision)
                    events[i] = new(events[i].EventOrdinal, events[i].OwnerOrdinal,
                        new PublicCombatDecision(decision.Status, decision.Observation, decision.Actions,
                            decision.HistoryThroughEventOrdinal, false));
        var root = f.Root with { PublicEvidence = new(PublicRunEvidence.Version, change != "gap", events.ToImmutableArray()) };
        string before = PublicJson.Serialize(root);
        var reshuffles = NativePublicReshuffleCondition.Create(root);
        Assert.Equal(stop, reshuffles.CombatAudits[0].StopReason);
        Assert.Equal(events[shuffle].EventOrdinal, reshuffles.CombatAudits[0].ThroughEventOrdinal);
        var condition = NativePublicMonsterBranchIntentCondition.Create(root);
        Assert.Equal([1, 2], condition.Combats[0].Targets.Select(target => target.RollOrdinal));
        Assert.All(condition.Combats[0].Targets, target => Assert.True(target.IntentEventOrdinal < events[changed].EventOrdinal));
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> ForceTape(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
    private sealed class DisposeAction(Action finish) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; finish(); }
    }
}
