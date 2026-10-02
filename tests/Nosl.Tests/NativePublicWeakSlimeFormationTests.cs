using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicWeakSlimeFormationTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Fact]
    public void ExhaustiveFiniteNativeFormationTicketsAndConditionalFullWordMass()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = new Overgrowth().MonsterEncounterCandidates.Single(item => item.IdEntry == "SLIMES_WEAK");
        const int bits = 2, domain = 1 << bits, lowBits = 64 - bits;
        var masses = new Dictionary<string, int>(StringComparer.Ordinal);
        for (ulong a = 0; a < domain; a++)
        for (ulong b = 0; b < domain; b++)
        for (ulong c = 0; c < domain; c++)
        {
            var words = new Queue<ulong>([a << lowBits, b << lowBits, c << lowBits]);
            using var scope = LabelRandomScope.Enter(_ => words.Dequeue());
            string key = string.Join(",", encounter.CreateMonsters(new Rng(17)).Select(item => item.Monster.GetType().Name));
            masses[key] = masses.GetValueOrDefault(key) + 1;
            Assert.Empty(words);
        }
        Assert.Equal(4, masses.Count); Assert.All(masses.Values, mass => Assert.Equal(16, mass));
        Assert.Equal(4, NativeWeakSlimeFormationCatalog.Tickets.Count);
        foreach (var ticket in NativeWeakSlimeFormationCatalog.Tickets)
        {
            Assert.Equal(new[] { ticket.Choices[0], 0, ticket.Choices[2] }, ticket.Choices);
            Assert.Equal(new ShuffleRational(masses[string.Join(",", ticket.Roster)], domain * domain * domain),
                NativeWeakSlimeFormationPlan.Mass(ticket.Roster));
            var seen = new HashSet<string>();
            for (ulong a = 0; a < 2; a++)
            for (ulong b = 0; b < domain; b++)
            for (ulong c = 0; c < 2; c++)
            for (int lowMask = 0; lowMask < 8; lowMask++)
            {
                ulong Low(int i) => (lowMask & (1 << i)) == 0 ? 0UL : (1UL << lowBits) - 1;
                var auxiliary = new Queue<ulong>([a, Low(0), b, Low(1), c, Low(2)]);
                var plan = NativeWeakSlimeFormationPlan.Create(ticket.Roster, auxiliary.Dequeue, bits);
                Assert.Empty(auxiliary); Assert.True(seen.Add(string.Join(",", plan.RawWords)));
                Assert.Equal(new[] { 2, 1, 2 }, plan.Factors.Select(factor => factor.Bound));
                Assert.Equal(new ShuffleRational(1, 4), plan.NativeToProposalRatio);
                Assert.Equal(plan.NativeToProposalRatio, plan.Envelope);
                Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("Fixed mass cancels")));
                var proposed = new Queue<ulong>(plan.RawWords);
                using var scope = LabelRandomScope.Enter(_ => proposed.Dequeue());
                var rng = new Rng(97);
                var roster = encounter.CreateMonsters(rng);
                Assert.Equal(ticket.Roster, roster.Select(item => item.Monster.GetType().Name));
                Assert.All(roster, item => Assert.Null(item.SlotName)); Assert.Equal(3, rng.Counter); Assert.Empty(proposed);
            }
            Assert.Equal(128, seen.Count);
            foreach (ulong endpoint in new[] { 0UL, ulong.MaxValue })
            {
                var plan = NativeWeakSlimeFormationPlan.Create(ticket.Roster, () => endpoint);
                for (int i = 0; i < plan.RawWords.Count; i++)
                {
                    var factor = plan.Factors[i];
                    Assert.Equal(factor.BucketStart + (endpoint == 0 ? 0 : factor.BucketSize - 1), plan.RawWords[i] >> 11);
                    Assert.Equal(endpoint == 0 ? 0UL : 2047UL, plan.RawWords[i] & 2047);
                }
                var proposed = new Queue<ulong>(plan.RawWords);
                using var scope = LabelRandomScope.Enter(_ => proposed.Dequeue());
                Assert.Equal(ticket.Roster, encounter.CreateMonsters(new Rng(77)).Select(item => item.Monster.GetType().Name));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DetachedConditionRetainsAllOrderedStartupSlotsAndIgnoresLaterSnapshot(int ticketIndex)
    {
        var root = await Opening(ticketIndex); string before = PublicJson.Serialize(root);
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.Equal(NativeWeakSlimeFormationCatalog.Tickets[ticketIndex].Roster, condition!.Roster);
        var later = root with { Observation = root.Observation! with { Enemies = [] } };
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(later, Prior, out var retained, out reason), reason);
        Assert.Equal(condition.Roster, retained!.Roster); Assert.Equal(before, PublicJson.Serialize(root));
        Assert.Equal(new ShuffleRational(1, 4), condition.Envelope);
    }

    [Theory]
    [InlineData("missing_history")]
    [InlineData("forced_owner")]
    [InlineData("gap")]
    [InlineData("missing_slot")]
    [InlineData("reordered_slots")]
    [InlineData("duplicate_type")]
    [InlineData("missing_entry")]
    [InlineData("wrong_origin")]
    public async Task MissingPublicCertificateOnlyDisablesProposal(string change)
    {
        var root = await Opening(0);
        if (change == "missing_history") root = root with { PublicEvidence = null };
        else
        {
            var evidence = root.PublicEvidence!;
            long owner = evidence.Events.Last(entry => entry.Payload is PublicOwnerStarted).OwnerOrdinal!.Value;
            var events = evidence.Events.TakeWhile(entry => entry.Payload is not PublicCombatDecision).ToArray();
            int start = Array.FindIndex(events, entry => entry.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.IntentPublished });
            if (change is "missing_slot" or "reordered_slots" or "duplicate_type" or "wrong_origin")
            {
                int i = change == "missing_slot" ? start + 2 : start;
                var intent = (PublicCombatFact)events[i].Payload;
                events[i] = new(events[i].EventOrdinal, events[i].OwnerOrdinal, change == "missing_slot" ? new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)
                    : new PublicCombatFact(PublicCombatFactKind.IntentPublished,
                        targetSlot: change == "reordered_slots" ? 5 : intent.TargetSlot,
                        model: change == "duplicate_type" ? "TwigSlimeS" : change == "wrong_origin" ? "CorpseSlug" : intent.Model,
                        intents: intent.Intents));
            }
            else
            {
                int i = Array.FindIndex(events, entry => change == "forced_owner" ? entry.OwnerOrdinal == owner
                    : entry.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.EntryAssets });
                events[i] = new(events[i].EventOrdinal, events[i].OwnerOrdinal, change == "forced_owner"
                    ? new PublicOwnerStarted(PublicEvidenceOwnerKind.Combat, 0, 2, 0, true)
                    : change == "gap" ? new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)
                    : new PublicCombatFact(PublicCombatFactKind.PlayerTurnEnded));
                if (change == "forced_owner")
                {
                    events = new[] { events[0], new PublicRunEvidenceEvent(1, 0,
                        new PublicOwnerStarted(PublicEvidenceOwnerKind.Event, 0, 2, null, true)) }
                        .Concat(events.Skip(i).Select((entry, index) => new PublicRunEvidenceEvent(index + 2, entry.OwnerOrdinal, entry.Payload))).ToArray();
                }
            }
            root = root with { PublicEvidence = new(evidence.SchemaVersion,
                !events.Any(entry => entry.Payload is PublicEvidenceGap), events.ToImmutableArray()) };
        }
        Assert.False(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out _));
        Assert.Null(condition);
    }

    [Theory]
    [InlineData("wrong_owner")]
    [InlineData("wrong_room")]
    [InlineData("wrong_stream")]
    [InlineData("used_stream")]
    [InlineData("wrong_act")]
    [InlineData("same_named_encounter")]
    [InlineData("wrong_index")]
    [InlineData("skipped_hook")]
    [InlineData("repeat")]
    public async Task OwnershipAndFactoryGuardsRemainUnresolvedBeforeSampling(string change)
    {
        var root = await Opening(0);
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out _));
        var (run, room, context) = Context(change == "wrong_act");
        int sampled = 0;
        var proposal = new NativePublicWeakSlimeFormationProposal(condition!, () => { sampled++; return ulong.MaxValue; },
            (words, _, _) => Force(words));
        proposal.AttachHypotheticalRun(run);
        var entry = PublicJson.Read<NativeEntryAssets>(condition!.EntryJson);
        if (change == "wrong_index")
        { Assert.Throws<InvalidOperationException>(() => proposal.CombatEntering(1, entry, run.Rng.Shuffle)); return; }
        proposal.CombatEntering(0, entry, run.Rng.Shuffle); run.PushRoom(room);
        if (change == "skipped_hook")
        { Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion); Assert.Equal(0, sampled); return; }
        switch (change)
        {
            case "wrong_owner": context = context with { Run = new RunState("foreign") }; break;
            case "wrong_room": context = context with { Room = new CombatRoom(() => Array.Empty<MonsterModel>()) }; break;
            case "wrong_stream": context = context with { Rng = run.Rng.Niche }; break;
            case "used_stream": context.Rng.NextInt(2); break;
            case "same_named_encounter": context = context with { Encounter = new Overgrowth().MonsterEncounterCandidates.Single(e => e.IdEntry == "SLIMES_WEAK") }; break;
            case "repeat": using (proposal.BeginFormation(context)) { context.Rng.NextInt(2); context.Rng.NextInt(1); context.Rng.NextInt(2); } sampled = 0; break;
        }
        Assert.Throws<InvalidOperationException>(() => proposal.BeginFormation(context)); Assert.Equal(0, sampled);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("wrong_draw_stream")]
    [InlineData("missing_middle_word")]
    [InlineData("extra_draw")]
    public async Task OwningTapePreservesAliasStreamAndAllThreeWordConsumptionGuards(string change)
    {
        var root = await Opening(0);
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out _));
        var (run, room, context) = Context();
        var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var proposal = new NativePublicWeakSlimeFormationProposal(condition!, new Rng(5454).NextUnsignedLong, ForceTape(tape));
        proposal.AttachHypotheticalRun(run);
        proposal.CombatEntering(0, PublicJson.Read<NativeEntryAssets>(condition!.EntryJson), run.Rng.Shuffle); run.PushRoom(room);
        if (change == "alias")
        {
            var snap = context.Rng.ToSerializable();
            Word(tape)(new(snap.state0, snap.state1, snap.state2, snap.state3));
        }
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var forced = proposal.BeginFormation(context);
            if (change == "wrong_draw_stream") run.Rng.Niche.NextInt(2);
            else
            {
                context.Rng.NextInt(2);
                if (change != "missing_middle_word") context.Rng.NextInt(1);
                context.Rng.NextInt(2);
                if (change == "extra_draw") context.Rng.NextInt(2);
            }
        });
        if (change == "alias") Assert.Contains("alias correction is unresolved", error.Message);
        if (change == "wrong_draw_stream") Assert.Contains("unexpected RNG state or stream", error.Message);
        if (change == "extra_draw") Assert.Contains("exactly three draws", error.Message);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Fact]
    public void OrdinaryDisabledParityAndOffTapeCallbackSamplingPreserveNativeDrawOrder()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = new Overgrowth().MonsterEncounterCandidates.Single(item => item.IdEntry == "SLIMES_WEAK");
        foreach (ulong seed in new ulong[] { 0, 1, 1234, ulong.MaxValue })
        {
            var ordinary = new Rng(seed); var expected = encounter.CreateMonsters(ordinary);
            var disabled = new Rng(seed);
            using (LabelSlimesWeakScope.Enter(_ => null))
            {
                var actual = encounter.CreateMonsters(disabled);
                Assert.Equal(expected.Select(item => item.Monster.GetType()), actual.Select(item => item.Monster.GetType()));
            }
            Assert.Equal(PublicJson.Serialize(ordinary.ToSerializable()), PublicJson.Serialize(disabled.ToSerializable()));
            Assert.Equal(3, ordinary.Counter);
        }
        int intercepted = 0, callbacks = 0;
        using var tape = LabelRandomScope.Enter(_ => { intercepted++; return 0; });
        using var callback = LabelSlimesWeakScope.Enter(context =>
        { callbacks++; Assert.Equal(0, context.Rng.Counter); new Rng(87).NextInt(2); return null; });
        encounter.CreateMonsters(new Rng(456));
        Assert.Equal(1, callbacks); Assert.Equal(3, intercepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeFactoryCombinesFormationHpAndShuffleAndRetainsPhysicalSlotOrder(int ticketIndex)
    {
        var root = await Opening(ticketIndex);
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root, Prior, out var condition, out _));
        var (run, room, _) = Context();
        var tape = new NativeLabelTape(new(123, 234, 345, 0, 0));
        var prefix = new NativePublicCombatPrefixProposal(NativePublicCombatPrefixCondition.Create(root),
            new Rng(4545).NextUnsignedLong, (words, purpose) => ForceTape(tape)(words,
                purpose.EndsWith("shuffle", StringComparison.Ordinal) ? run.Rng.Shuffle : run.Rng.Niche, purpose));
        var proposal = new NativePublicWeakSlimeFormationProposal(condition!, new Rng(5656).NextUnsignedLong, ForceTape(tape));
        prefix.AttachHypotheticalRun(run); proposal.AttachHypotheticalRun(run);
        var entry = PublicJson.Read<NativeEntryAssets>(condition!.EntryJson);
        prefix.CombatEntering(0, entry, run.Rng.Shuffle); proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        run.PushRoom(room);
        using var scopedTape = LabelRandomScope.Enter(Word(tape), prefix.BeginShuffle, prefix.BeginMonsterHp);
        using var scopedFormation = LabelSlimesWeakScope.Enter(proposal.BeginFormation);
        await room.Enter(run);
        prefix.ValidateCompletion(); proposal.ValidateCompletion();
        Assert.Equal(condition.Roster, room.Engine.State.Enemies.Select(enemy => enemy.Monster!.GetType().Name));
        Assert.Equal(root.Observation!.Enemies.Select(enemy => enemy.MaxHp), room.Engine.State.Enemies.Select(enemy => enemy.MaxHp));
        Assert.Equal(3, room.Engine.State.Enemies.Select(enemy => enemy.Monster).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(1, proposal.ConditionedCombatCount); Assert.Equal(new ShuffleRational(1, 4), proposal.NativeToProposalRatio);
    }

    [Theory]
    [InlineData(11006UL, "TwigSlimeS", "LeafSlimeM", "LeafSlimeS")]
    [InlineData(11007UL, "LeafSlimeS", "TwigSlimeM", "TwigSlimeS")]
    public async Task ExistingMeasuredRootsRetainActualNativeOwningReplayAndSettlement(ulong seed, string first, string medium, string last)
    {
        var prior = Prior.Freeze();
        var sourceRecipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, sourceRecipe,
            NativeLabelTape.ForDeclaredPrior(prior, sourceRecipe)))
        { Assert.NotNull(source); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe())); }
        string before = PublicJson.Serialize(root);
        Assert.True(NativePublicWeakSlimeFormationCondition.TryCreate(root, prior, out var condition, out var why), why);
        Assert.Equal(new[] { first, medium, last }, condition!.Roster);
        // Reuse the existing coordinate solely to isolate raw formation-word
        // preimages in the production lifecycle, not as posterior evidence. No
        // recipe or source object is passed into condition construction.
        var hypothetical = sourceRecipe with { ProposalSeed = sourceRecipe.ProposalSeed ^ 12345UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical);
        var proposal = new NativePublicWeakSlimeFormationProposal(condition,
            new Rng(hypothetical.ProposalSeed, "weak-slime-fixture").NextUnsignedLong, ForceTape(tape));
        RunState? owned = null;
        using var scopedFormation = LabelSlimesWeakScope.Enter(context =>
        {
            // This detached helper fixture connects the existing production
            // factory to the new proposal. Forks exercise the owning tape's
            // copied overrides without resampling this separate fixture scope.
            if (owned is not null) return null;
            owned = context.Run!;
            proposal.AttachHypotheticalRun(owned);
            var player = owned.Players.Single();
            var entry = NativeEntryAssets.Capture(CombatAssetSnapshot.Capture(player), player.Creature.CurrentHp,
                player.PotionSlots.Select(potion => potion?.GetType().Name).ToArray());
            proposal.CombatEntering(0, entry, owned.Rng.Shuffle);
            return proposal.BeginFormation(context);
        });
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, hypothetical, tape);
        Assert.NotNull(world); proposal.ValidateCompletion();
        Assert.Equal(1, proposal.ConditionedCombatCount); Assert.Equal(3, tape.ConditionedCells);
        Assert.Equal(before, PublicJson.Serialize(world.Observe()));
        Assert.True(proposal.AcceptCorrection(() => throw new InvalidOperationException("Quarter mass cancels")));
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    private static async Task<DecisionPacket> Opening(int ticketIndex)
    {
        var ticket = NativeWeakSlimeFormationCatalog.Tickets[ticketIndex];
        using var formation = LabelSlimesWeakScope.Enter(_ => Force(NativeWeakSlimeFormationPlan.Create(ticket.Roster,
            new Rng(8989).NextUnsignedLong).RawWords));
        await using var session = await CombatSession.CreateAsync(new(Seed: "weak-slime-public", Encounter: "SlimesWeak"));
        var packet = session.Observe();
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        packet = packet with { Observation = packet.Observation! with
        { History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)), .. packet.Observation.History.Skip(1)] } };
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long map = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, 1);
        var from = new PublicMapCoordinate(0, 0); var to = new PublicMapCoordinate(0, 1);
        long offered = recorder.Record(map, new PublicMapObserved(from,
            [new(from, PublicMapNodeType.Ancient), new(to, PublicMapNodeType.Monster)], [new(from, to)], [new(to, true)]));
        recorder.Record(map, new PublicMapChosen(offered, to)); recorder.Record(map, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2);
        recorder.ObserveCombatDecision(owner, packet);
        return packet with { PublicEvidence = recorder.Capture() };
    }
    private static (RunState Run, CombatRoom Room, LabelSlimesWeakFormationContext Context) Context(bool wrongAct = false)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("owned-weak-slime", wrongAct ? new Underdocks() : new Overgrowth(), 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        var encounter = (wrongAct ? new Overgrowth() : run.Act).MonsterEncounterCandidates.Single(item => item.IdEntry == "SLIMES_WEAK");
        var rng = new Rng(7654);
        var room = new CombatRoom(() => (encounter, encounter.CreateMonsters(rng)), RoomType.Monster);
        return (run, room, new(run, room, encounter, rng, rng));
    }
    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> ForceTape(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
    private static IDisposable Force(IReadOnlyList<ulong> words)
    { int index = 0; return LabelRandomScope.Enter(_ => words[index++]); }
}
