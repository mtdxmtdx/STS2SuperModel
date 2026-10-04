using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativeToadpoleStartupTests
{
    private static PublicEvidenceAssets Assets(NativeEntryAssets entry) => new(entry.Hp, entry.MaxHp,
        entry.Gold, entry.Deck, entry.Relics, entry.Potions.ToImmutableArray(), entry.MaxEnergy,
        entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);

    private static DecisionPacket Anchor(DecisionPacket packet, NativeEntryAssets entry) => packet with
    { Observation = packet.Observation! with
    {
        History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
            .. packet.Observation.History.Skip(1)],
    } };
    private static DecisionPacket WithEvidence(DecisionPacket packet)
    {
        var entry = PublicJson.Read<NativeEntryAssets>(packet.Observation!.History[1].Detail);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.ObserveCombatDecision(owner, packet);
        return packet with { PublicEvidence = recorder.Capture() };
    }
    private static async Task<DecisionPacket> Opening()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "toadpole-startup-certificate", Encounter: "ToadpolesWeak"));
        return WithEvidence(Anchor(session.Observe(), NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions)));
    }

    [Theory]
    [InlineData("valid", null)]
    [InlineData("damage", null)]
    [InlineData("missing", "toadpole_hp_complete_ordered_roster_required")]
    [InlineData("foreign", "toadpole_hp_complete_ordered_roster_required")]
    [InlineData("duplicate", "toadpole_hp_distinct_native_range_required")]
    [InlineData("range", "toadpole_hp_distinct_native_range_required")]
    [InlineData("intent", "toadpole_hp_front_rear_startup_required")]
    public async Task OnlyCompleteOrderedPublicToadpolesEnableTheNewHpHelper(string variation, string? reason)
    {
        var root = await Opening();
        var enemies = root.Observation!.Enemies;
        var observation = root.Observation with { Enemies = variation switch
        {
            "damage" => enemies.Select(enemy => enemy with { Hp = 1 }).ToArray(),
            "missing" => [enemies[0]],
            "foreign" => [enemies[0], enemies[1] with { Id = "SludgeSpinner" }],
            "duplicate" => [enemies[0], enemies[1] with { Hp = 1, MaxHp = enemies[0].MaxHp }],
            "range" => [enemies[0], enemies[1] with { Hp = 1, MaxHp = 27 }],
            _ => enemies,
        } };
        if (variation == "intent") observation = observation with
        { History = observation.History.Select(item => item.Kind == "intent_published"
            ? item with { Detail = item.Detail.Replace("Buff", "Debuff", StringComparison.Ordinal) } : item).ToArray() };
        root = WithEvidence(root with { Observation = observation, Actions = [new(0, "end_turn")] });
        string before = PublicJson.Serialize(root);
        Assert.False(NativeInitialHpCondition.TryCreate(root, out _, out _)); // Legacy duplicate rule is unchanged.
        var input = NativePublicCombatPrefixCondition.Create(root).Combats[0];
        Assert.NotNull(input.Shuffle);
        Assert.Equal(reason is null ? 2 : 0, input.HpCount);
        Assert.Equal(reason, input.HpReason);
        Assert.Equal(reason is null, input.ToadpoleHp is not null);
        Assert.Equal(before, PublicJson.Serialize(root));
        Assert.Equal([0, 1], enemies.Select(enemy => enemy.Slot));
        Assert.Equal("Buff", enemies[0].Intents.Single().Kind);
        Assert.Equal(new PublicIntent("Attack", 8, 1), enemies[1].Intents.Single());
    }

    private static ulong OriginalWord(LabelRandomState state) => new MegaRandom(new SerializableRng
    { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong();

    [Fact]
    public async Task ComposedNativeStartupUsesOrderedToadpoleHpAndPreservesCompletePublicOpening()
    {
        var root = await Opening();
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var random = new Rng(62831, "composed-toadpole-startup");
        int forced = 0;
        var proposal = new NativePublicCombatPrefixProposal(NativePublicCombatPrefixCondition.Create(root),
            random.NextUnsignedLong, (words, _) =>
            {
                int cursor = 0;
                return LabelRandomScope.Enter(_ => { forced++; return words[cursor++]; });
            });
        bool attached = false;
        using var scope = LabelRandomScope.Enter(OriginalWord, proposal.BeginShuffle, context =>
        {
            if (!attached)
            {
                var run = (RunState)context.Creature.CombatState!.RunState;
                proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, entry, run.Rng.Shuffle);
                attached = true;
            }
            return proposal.BeginMonsterHp(context);
        });
        await using var session = await CombatSession.CreateAsync(new(Seed: "independent-toadpole-opening", Encounter: "ToadpolesWeak"));
        proposal.ValidateCompletion();
        Assert.Equal(2, proposal.ConditionedHpCount); Assert.Equal(1, proposal.ConditionedShuffleCount);
        Assert.Equal(2 + entry.Deck.Length - 1, forced);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(WithEvidence(Anchor(session.Observe(),
            NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions)))));
        _ = proposal.AcceptCorrection(random.NextUnsignedLong);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("range")]
    [InlineData("used_hp")]
    [InlineData("front")]
    [InlineData("slot")]
    [InlineData("rng")]
    [InlineData("factory")]
    [InlineData("earlier_hp")]
    public async Task ActualFactoryConsumesTwoOrderedHpWordsAndRejectsBrokenContext(string variation)
    {
        var root = await Opening();
        var condition = NativePublicCombatPrefixCondition.Create(root).Combats[0].ToadpoleHp!;
        var random = new Rng(7821, "toadpole-hp-proposal");
        int slots = 0, forced = 0;
        using var scope = LabelRandomScope.Enter(OriginalWord, beginMonsterHp: context =>
        {
            int slot = slots;
            if (variation == "range") context = context with { MinHp = 21 };
            if (variation == "used_hp") context = context with { UsedHp = [22] };
            if (variation == "front") ((Toadpole)context.Creature.Monster!).IsFront = false;
            if (variation == "slot") context.Creature.AssignSlotName("front");
            if (variation == "rng") context = context with { Rng = new Rng(9) };
            if (variation == "factory") ((RunState)context.Creature.CombatState!.RunState).PushRoom(
                new CombatRoom(() => (MonsterModel)ModelDb.Monster<Toadpole>().MutableClone(), RoomType.Monster, "ToadpolesWeak"));
            if (variation == "earlier_hp" && slot == 1) context.Creature.CombatState!.Enemies[0].SetCurrentHpInternal(1);
            Assert.Equal(slot, context.Creature.CombatState!.Enemies.Count); // Callback precedes AddCreature.
            var plan = condition.CreateProposal(context, slot, random.NextUnsignedLong);
            slots++;
            return LabelRandomScope.Enter(_ => { forced++; return plan.RawWord; });
        });
        if (variation != "none")
        {
            // Exercise the actual room directly: the scenario wrapper reads its
            // Engine before awaiting an early failed setup and can mask that error.
            var run = EncounterCoverage.CreateRun("toadpole-hp-owned", "ToadpolesWeak", 10);
            var room = EncounterCoverage.CreateRoom("ToadpolesWeak", run); run.PushRoom(room);
            await Assert.ThrowsAsync<InvalidOperationException>(() => room.Enter(run));
            Assert.Equal(variation == "earlier_hp" ? 1 : 0, forced);
            return;
        }
        await using var session = await CombatSession.CreateAsync(new(Seed: "toadpole-hp-owned", Encounter: "ToadpolesWeak"));
        Assert.Equal(2, slots); Assert.Equal(2, forced);
        Assert.Equal(root.Observation!.Enemies.Select(enemy => enemy.MaxHp), session.Observe().Observation!.Enemies.Select(enemy => enemy.MaxHp));
        Assert.True(((Toadpole)session.State.Enemies[0].Monster!).IsFront);
        Assert.False(((Toadpole)session.State.Enemies[1].Monster!).IsFront);
    }

    [Fact]
    public void OrderedTwoToadpoleFiniteLawMatchesAllTwentyDistinctPublicHpPairs()
    {
        const int bits = 3, domain = 8;
        foreach (int first in Enumerable.Range(22, 5))
        foreach (int second in Enumerable.Range(22, 5).Where(value => value != first))
        {
            int[] targets = [first, second]; int matches = 0;
            for (int a = 0; a < domain; a++)
            for (int b = 0; b < domain; b++)
            {
                int nativeFirst = 22 + a * 5 / domain;
                int nativeSecond = Enumerable.Range(22, 5).Where(value => value != nativeFirst).ElementAt(b * 4 / domain);
                if (nativeFirst == first && nativeSecond == second) matches++;
            }
            var ratio = new ShuffleRational(1, 1);
            for (int slot = 0; slot < 2; slot++)
            {
                ulong bound = NativeHpProposal.RootEnvelopeBucket(22, 26, targets.Take(slot), bits);
                var plan = NativeHpProposal.Create(22, 26, targets[slot], targets.Take(slot).Order().ToArray(),
                    bound, () => ulong.MaxValue, bits)!;
                ratio = ratio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
                int accepted = 0;
                for (ulong residue = 0; residue < bound; residue++)
                    if (plan.AcceptCorrection(() => bound + residue)) accepted++;
                Assert.Equal(plan.BucketSize, (ulong)accepted);
            }
            Assert.Equal(new ShuffleRational(matches, domain * domain), ratio);
        }
    }

    [Fact]
    public async Task NativeToadpoleCycleAndThornsRetaliationPreserveDrawAndMaxHp()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "toadpole-turn-closure", Encounter: "ToadpolesWeak",
            Deck: Enumerable.Repeat("StrikeSilent", 12).ToArray(), Hp: 1000, MaxHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Record(DecisionPacket packet)
        { packet = Anchor(packet, entry); recorder.ObserveCombatDecision(owner, packet); return packet; }
        var current = Record(session.Observe()); int[] maxHp = current.Observation!.Enemies.Select(enemy => enemy.MaxHp).ToArray();
        current = Record(await session.StepAsync(current.Actions.Single(action => action.Kind == "end_turn")));
        Assert.Contains(current.Observation!.Enemies[0].Powers, power => power.Id == "ThornsPower" && power.Amount == 2);
        int hp = current.Observation.Hp, draw = current.Observation.DrawCount;
        current = Record(await session.StepAsync(current.Actions.First(action => action.Kind == "play" && action.Target == 0)));
        Assert.Equal(hp - 2, current.Observation!.Hp); Assert.Equal(draw, current.Observation.DrawCount);
        current = Record(await session.StepAsync(current.Actions.Single(action => action.Kind == "end_turn")));
        Assert.DoesNotContain(current.Observation!.Enemies[0].Powers, power => power.Id == "ThornsPower");
        Assert.Contains(current.Observation.Enemies[1].Powers, power => power.Id == "ThornsPower" && power.Amount == 2);
        Assert.Equal(maxHp, current.Observation.Enemies.Select(enemy => enemy.MaxHp));
        var root = current with { PublicEvidence = recorder.Capture() };
        var input = NativePublicCombatPrefixCondition.Create(root).Combats[0];
        Assert.Equal(2, input.HpCount); Assert.Equal(12, input.Shuffle!.DrawPrefixIds.Length);
        Assert.Single(NativePublicReshuffleCondition.Create(root).Combats[0].Targets);
        Assert.Equal("observed_prefix_complete", NativePublicReshuffleCondition.Create(root).CombatAudits[0].StopReason);
    }

    [Fact]
    public async Task Actual11004ThirdCombatNowConditionsBothToadpoleHpSlotsAndTheLaterTurn()
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
        };
        var recipe = prior.Draw(new Rng(11004, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
        var condition = NativePublicCombatPrefixCondition.Create(root);
        var input = condition.Combats[2];
        Assert.NotNull(input.ToadpoleHp); Assert.Null(input.Hp); Assert.Equal(2, input.HpCount);
        Assert.Equal(5, condition.EligibleHpCount);
        Assert.Equal([26, 25], root.Observation!.Enemies.Select(enemy => enemy.MaxHp));
        Assert.Contains(root.Observation.Enemies, enemy => enemy.Hp < enemy.MaxHp);
        Assert.Equal(12, input.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("observed_prefix_complete", input.DrawPrefix!.StopReason);
    }
}
