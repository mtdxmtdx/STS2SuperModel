using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicCorpseSlugIntentTests
{
    private static PublicEvidenceAssets Assets(NativeEntryAssets entry) => new(entry.Hp, entry.MaxHp,
        entry.Gold, entry.Deck, entry.Relics, entry.Potions.ToImmutableArray(), entry.MaxEnergy,
        entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);

    private static DecisionPacket WithEvidence(DecisionPacket packet, bool forced = false, bool earlierCombat = false)
    {
        var entry = PublicJson.Read<NativeEntryAssets>(packet.Observation!.History[1].Detail);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long? parent = forced ? recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 2) : null;
        if (earlierCombat)
        {
            long earlier = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
            recorder.Record(earlier, new PublicCombatFact(PublicCombatFactKind.Started));
            recorder.Record(earlier, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2, parent);
        recorder.ObserveCombatDecision(owner, packet);
        return packet with { PublicEvidence = recorder.Capture() };
    }

    private static async Task<DecisionPacket> Opening(string encounter)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "slug-intents", Encounter: encounter));
        var packet = session.Observe();
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        return WithEvidence(packet with { Observation = packet.Observation! with
        {
            History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                .. packet.Observation.History.Skip(1)],
        }});
    }

    [Theory]
    [InlineData("CorpseSlugsWeak", 2)]
    [InlineData("CorpseSlugsNormal", 3)]
    public async Task DetachedConditionUsesFirstPublicShapesAndCountsEveryOwner(string encounter, int count)
    {
        var root = await Opening(encounter);
        string before = PublicJson.Serialize(root);
        var condition = NativePublicCorpseSlugIntentCondition.Create(root);
        var input = Assert.Single(condition.Combats).Value;
        Assert.Equal(count, input.EnemyCount);
        Assert.Equal(before, PublicJson.Serialize(root));
        // Later visible moves do not replace initial publication or use private move names.
        var later = root with { Observation = root.Observation! with
        { Enemies = root.Observation.Enemies.Select(enemy => enemy with { Intents = [new("Stun", null, null)] }).ToArray() } };
        Assert.Equal(input, Assert.Single(NativePublicCorpseSlugIntentCondition.Create(later).Combats).Value);
        Assert.Empty(NativePublicCorpseSlugIntentCondition.Create(WithEvidence(root, forced: true)).Combats);
        var shifted = NativePublicCorpseSlugIntentCondition.Create(WithEvidence(root, earlierCombat: true));
        Assert.Equal(1, Assert.Single(shifted.Combats).Key);
    }

    [Theory]
    [InlineData("damage")]
    [InlineData("repeats")]
    [InlineData("extra_intent")]
    [InlineData("cycle")]
    [InlineData("power")]
    [InlineData("missing_intent")]
    public async Task UnsupportedPublicStartupFallsBack(string change)
    {
        var root = await Opening("CorpseSlugsNormal");
        var history = root.Observation!.History.ToArray();
        int start = Array.FindIndex(history, entry => entry.Kind == "intent_published");
        if (change == "power") history[2] = history[2] with { Detail = history[2].Detail.Replace("RavenousPower", "StrengthPower") };
        else if (change == "missing_intent") history = history.Take(start + 1).ToArray();
        else
        {
            PublicIntent[] intents = change switch
            {
                "damage" => [new("Attack", 100, 2)],
                "repeats" => [new("Attack", 3, 1)],
                "extra_intent" => [new("Attack", 3, 2), new("Debuff", null, null)],
                _ => [new("Debuff", null, null)],
            };
            for (int i = start; i < (change == "cycle" ? history.Length : start + 1); i++)
                history[i] = history[i] with { Detail = PublicJson.Serialize(new { slot = i - start, id = "CorpseSlug", intents }) };
        }
        root = WithEvidence(root with { Observation = root.Observation with { History = history } });
        Assert.Empty(NativePublicCorpseSlugIntentCondition.Create(root).Combats);
    }

    [Fact]
    public void FiniteThreeBucketLawAndNativeDoubleBoundariesAreExact()
    {
        const int bits = 4, domain = 1 << bits;
        var masses = new int[3];
        for (ulong high = 0; high < domain; high++)
        {
            using var scope = LabelRandomScope.Enter(_ => high << (64 - bits));
            masses[new Rng(123).NextInt(3)]++;
        }
        Assert.Equal(new[] { 6, 5, 5 }, masses);
        for (int index = 0; index < 3; index++)
        {
            var factor = ConditionalShuffleProposal.Factor(3, index, bits);
            var observed = new HashSet<ulong>();
            for (ulong offset = 0; offset < factor.BucketSize; offset++)
            foreach (ulong low in new[] { 0UL, (1UL << (64 - bits)) - 1 })
            {
                var words = new Queue<ulong>([100 * factor.BucketSize + offset, low]);
                var plan = NativeCorpseSlugIntentPlan.Create(index, words.Dequeue, bits);
                Assert.True(observed.Add(plan.RawWord));
                Assert.Equal(new ShuffleRational(masses[index], domain), plan.NativeToProposalRatio);
                Assert.Equal(plan.NativeToProposalRatio, plan.Envelope);
                Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("No correction draw needed")));
                Assert.Equal(factor.BucketStart + offset, plan.RawWord >> (64 - bits));
            }
            var native = ConditionalShuffleProposal.Factor(3, index);
            ulong[] nativeMasses = [3002399751580331, 3002399751580330, 3002399751580331];
            Assert.Equal(nativeMasses[index], native.BucketSize);
            foreach (ulong high in new[] { native.BucketStart, native.BucketStart + native.BucketSize - 1 })
            {
                using var scope = LabelRandomScope.Enter(_ => (high << 11) | 2047);
                Assert.Equal(index, new Rng(123).NextInt(3));
            }
        }
    }

    [Fact]
    public void OrdinaryInitializerDrawsBeforeEnumeratingAndCallbackSamplingIsOffTape()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var rng = new Rng(456); int enumerations = 0;
        IEnumerable<MonsterModel> Lazy()
        {
            Assert.Equal(1, rng.Counter); enumerations++;
            yield return (MonsterModel)ModelDb.Monster<CorpseSlug>().MutableClone();
        }
        CorpseSlug.EnsureCorpseSlugsStartWithDifferentMoves(Lazy(), rng);
        Assert.Equal(1, enumerations);
        int tapeWords = 0, callbacks = 0;
        using var tape = LabelRandomScope.Enter(_ => { tapeWords++; return 0; });
        using var scope = LabelCorpseSlugScope.Enter(context =>
        { callbacks++; new Rng(789).NextInt(3); return null; });
        CorpseSlug.EnsureCorpseSlugsStartWithDifferentMoves(
            new[] { (MonsterModel)ModelDb.Monster<CorpseSlug>().MutableClone() }, new Rng(123));
        Assert.Equal(1, callbacks); Assert.Equal(1, tapeWords);
    }

    private static (RunState Run, CombatRoom Room, LabelCorpseSlugInitialIntentsContext Context) Context(int count)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("slug-intent-owned", new Underdocks(), 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        var encounter = UnderdocksEncounters.BatchA.Concat(UnderdocksEncounters.BatchB)
            .Single(item => item.Name == (count == 2 ? "CorpseSlugsWeak" : "CorpseSlugsNormal"));
        var rng = new Rng(7654);
        var monsters = Enumerable.Range(0, count).Select(_ => (MonsterModel)ModelDb.Monster<CorpseSlug>().MutableClone()).ToArray();
        var room = new CombatRoom(() => (encounter, encounter.CreateMonsters(rng)), RoomType.Monster);
        return (run, room, new(run, room, encounter, rng, rng, monsters));
    }

    [Theory]
    [InlineData("wrong_owner")]
    [InlineData("wrong_room")]
    [InlineData("wrong_stream")]
    [InlineData("used_stream")]
    [InlineData("wrong_roster")]
    [InlineData("wrong_encounter")]
    [InlineData("repeat")]
    [InlineData("wrong_index")]
    [InlineData("skipped")]
    public async Task RuntimeOwnershipAndPhaseGuardsFailBeforeSampling(string change)
    {
        var root = await Opening("CorpseSlugsWeak");
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var condition = NativePublicCorpseSlugIntentCondition.Create(root);
        var (run, room, context) = Context(2);
        int sampled = 0;
        var proposal = new NativePublicCorpseSlugIntentProposal(condition, () => { sampled++; return ulong.MaxValue; },
            (_, _, _) => LabelRandomScope.Enter(_ => 0));
        proposal.AttachHypotheticalRun(run);
        if (change == "wrong_index")
        { Assert.Throws<InvalidOperationException>(() => proposal.CombatEntering(1, entry, run.Rng.Shuffle)); return; }
        proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        if (change == "skipped")
        { Assert.Throws<InvalidOperationException>(() => proposal.ValidateCompletion()); Assert.Equal(0, sampled); return; }
        run.PushRoom(room);
        switch (change)
        {
            case "wrong_owner": context = context with { Run = new RunState("foreign") }; break;
            case "wrong_room": context = context with { Room = new CombatRoom(() => context.Monsters) }; break;
            case "wrong_stream": context = context with { Rng = run.Rng.Niche }; break;
            case "used_stream": context.Rng.NextInt(3); break;
            case "wrong_roster": context = context with { Monsters = [context.Monsters[0]] }; break;
            case "wrong_encounter": context = context with { Encounter = UnderdocksEncounters.BatchA[1] }; break;
            case "repeat": using (proposal.BeginInitialIntents(context)) { context.Rng.NextInt(3); } sampled = 0; break;
        }
        var error = Record.Exception(() => proposal.BeginInitialIntents(context));
        if (change == "wrong_roster") Assert.IsType<NativePublicConstraintMismatchException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, sampled);
    }

    [Theory]
    [InlineData("CorpseSlugsWeak", 2)]
    [InlineData("CorpseSlugsNormal", 3)]
    public async Task NativeFactoriesPublishConditionedIntentsAndContinueWithoutChangingTransitions(string encounter, int count)
    {
        var root = await Opening(encounter);
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var condition = NativePublicCorpseSlugIntentCondition.Create(root);
        var (run, room, _) = Context(count);
        var hpShuffle = new NativePublicCombatPrefixProposal(NativePublicCombatPrefixCondition.Create(root),
            new Rng(9898).NextUnsignedLong, (words, _) => Force(words));
        var proposal = new NativePublicCorpseSlugIntentProposal(condition, new Rng(8787).NextUnsignedLong,
            (words, rng, _) => { Assert.Equal(0, rng.Counter); return Force(words); });
        hpShuffle.AttachHypotheticalRun(run); proposal.AttachHypotheticalRun(run);
        // This is the actual normal-driver order: owner notification before PushRoom and Prepare.
        hpShuffle.CombatEntering(0, entry, run.Rng.Shuffle); proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        var knowledge = new PublicKnowledge(); knowledge.BeginCombat(); room.ConfigureObserver(knowledge);
        run.PushRoom(room);
        using var tape = LabelRandomScope.Enter(_ => ulong.MaxValue, hpShuffle.BeginShuffle, hpShuffle.BeginMonsterHp);
        using var scope = LabelCorpseSlugScope.Enter(proposal.BeginInitialIntents);
        await room.Enter(run);
        proposal.ValidateCompletion(); hpShuffle.ValidateCompletion();
        Assert.Equal(1, proposal.ConditionedCombatCount);
        Assert.Equal(root.Observation!.Enemies.Select(enemy => PublicJson.Serialize(enemy.Intents)),
            room.Engine.State.Enemies.Select(enemy => PublicJson.Serialize(PublicViews.Intents(enemy))));
        Assert.Equal(root.Observation.Enemies.Select(enemy => enemy.MaxHp), room.Engine.State.Enemies.Select(enemy => enemy.MaxHp));
        Assert.Equal(root.Observation.History.Skip(2), knowledge.Events.Skip(1));
        int first = condition.Combats[0].StarterIndex;
        for (int slot = 0; slot < count; slot++)
        {
            var enemy = room.Engine.State.Enemies[slot];
            var monster = Assert.IsType<CorpseSlug>(enemy.Monster);
            Assert.Equal((first + slot) % 3, monster.StarterMoveIdx);
            await monster.PerformMove();
            monster.RollMove(room.Engine.State.PlayerCreatures);
            PublicIntent next = Assert.Single(PublicViews.Intents(enemy));
            PublicIntent[] shapes = [new("Attack", 3, 2), new("Attack", 9, 1), new("Debuff", null, null)];
            Assert.Equal(shapes[(first + slot + 1) % 3], next);
        }
        Assert.True(proposal.AcceptCorrection(() => throw new InvalidOperationException()));
    }


    [Theory]
    [InlineData("alias")]
    [InlineData("wrong_draw_stream")]
    [InlineData("skipped_draw")]
    [InlineData("extra_draw")]
    public async Task ProductionTapeRetainsAliasExactStreamAndCompletionGuards(string change)
    {
        var root = await Opening("CorpseSlugsWeak");
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var (run, room, context) = Context(2);
        var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var proposal = new NativePublicCorpseSlugIntentProposal(NativePublicCorpseSlugIntentCondition.Create(root),
            new Rng(4545).NextUnsignedLong, ForceTape(tape));
        proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, entry, run.Rng.Shuffle); run.PushRoom(room);
        if (change == "alias")
        {
            var snapshot = context.Rng.ToSerializable();
            Word(tape)(new(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3));
        }
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var forced = proposal.BeginInitialIntents(context);
            if (change == "alias") context.Rng.NextInt(3);
            if (change == "wrong_draw_stream") run.Rng.Niche.NextInt(3);
            if (change == "extra_draw") { context.Rng.NextInt(3); context.Rng.NextInt(3); }
        });
        Assert.Equal(change == "extra_draw" ? 1 : 0, tape.ConditionedCells);
        if (change == "extra_draw") Assert.Contains("exactly one draw", error.Message);
        if (change == "alias") Assert.Contains("alias correction is unresolved", error.Message);
        if (change == "wrong_draw_stream") Assert.Contains("unexpected RNG state or stream", error.Message);
        if (change == "skipped_draw") Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }


    [Theory]
    [InlineData("CorpseSlugsWeak")]
    [InlineData("CorpseSlugsNormal")]
    public async Task WholeHypotheticalReplayRetainsPublicHistoryAndOwnedSettlement(string encounter)
    {
        var root = await Opening(encounter);
        string before = PublicJson.Serialize(root);
        var condition = NativePublicCorpseSlugIntentCondition.Create(root);
        var prefixCondition = NativePublicCombatPrefixCondition.Create(root);
        var proposals = new List<NativePublicCorpseSlugIntentProposal>();
        async Task Lifecycle(RunState run, RunDriver driver)
        {
            var tape = new NativeLabelTape(new(123, 234, 345, 0, 0));
            var prefix = new NativePublicCombatPrefixProposal(prefixCondition, new Rng(8989).NextUnsignedLong,
                (words, purpose) => ForceTape(tape)(words,
                    purpose.EndsWith("shuffle", StringComparison.Ordinal) ? run.Rng.Shuffle : run.Rng.Niche, purpose));
            var proposal = new NativePublicCorpseSlugIntentProposal(condition, new Rng(7878).NextUnsignedLong, ForceTape(tape));
            prefix.AttachHypotheticalRun(run); proposal.AttachHypotheticalRun(run); proposals.Add(proposal);
            var original = driver.CombatObserverDecorator!;
            driver.CombatObserverDecorator = observer =>
            {
                var decorated = original(observer);
                var player = run.Players.Single();
                var entry = NativeEntryAssets.Capture(CombatAssetSnapshot.Capture(player), player.Creature.CurrentHp,
                    player.PotionSlots.Select(potion => potion?.GetType().Name).ToArray());
                prefix.CombatEntering(0, entry, run.Rng.Shuffle); proposal.CombatEntering(0, entry, run.Rng.Shuffle);
                return decorated;
            };
            using var tapeScope = LabelRandomScope.Enter(Word(tape), prefix.BeginShuffle, prefix.BeginMonsterHp);
            using var slugScope = LabelCorpseSlugScope.Enter(proposal.BeginInitialIntents);
            await driver.RunOneInjectedCombatAsync(RoomType.Monster,
                encounter == "CorpseSlugsWeak" ? "CORPSE_SLUGS_WEAK" : "CORPSE_SLUGS_NORMAL");
        }
        string seed = Enumerable.Range(0, 32).Select(index => "slug-continuation-" + index)
            .First(candidate => ActDefinition.GetRandomList(candidate)[0] is Underdocks);
        await using var world = await NativeRunWorld.OpenConstructedLifecycleFixtureAsync(new(MaxFloors: 8), seed, 0, Lifecycle);
        Assert.NotNull(world);
        Assert.Equal(root.Observation!.History, world.Observe().Observation!.History);
        proposals[0].ValidateCompletion();
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
        Assert.All(proposals, proposal => { proposal.ValidateCompletion(); Assert.Equal(1, proposal.ConditionedCombatCount); });
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> ForceTape(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);

    private static IDisposable Force(IReadOnlyList<ulong> words)
    {
        int index = 0;
        return LabelRandomScope.Enter(_ => words[index++]);
    }
}
