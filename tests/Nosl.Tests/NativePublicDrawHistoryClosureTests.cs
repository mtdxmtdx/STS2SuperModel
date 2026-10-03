using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativePublicDrawHistoryClosureTests(ITestOutputHelper output)
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
                    _entry.Relics, _entry.Potions.ToImmutableArray(), _entry.MaxEnergy, _entry.PotionSlots,
                    _entry.OrbSlots, _entry.CardRemovalsUsed);
                _recorder = new(new("Silent", 10, assets));
                _owner = _recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
            }
            // Explicitly supplied scenario entry; certificates below consume only detached public evidence.
            packet = packet with { Observation = packet.Observation! with
            {
                History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(_entry)),
                    .. packet.Observation.History.Skip(1)],
            } };
            _recorder.ObserveCombatDecision(_owner, packet);
            return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet with { PublicEvidence = _recorder.Capture() }));
        }
    }

    [Theory]
    [InlineData("Seapunk", "StrengthPower")]
    [InlineData("ShrinkerBeetle", "ShrinkPower")]
    [InlineData("FuzzyWurmCrawler", "StrengthPower")]
    [InlineData("Nibbit", "StrengthPower")]
    public async Task FullNativeEnemyCyclesPreserveDrawOrderThroughWitnessedReshuffles(string enemy, string power)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "draw-history:" + enemy,
            Deck: Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? "StrikeSilent" : "DefendSilent").ToArray(),
            Enemy: enemy, EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        for (int i = 0; i < 3; i++)
            current = recorder.Record(await session.StepAsync(current.Actions.Single(action => action.Kind == "end_turn")));
        Assert.Contains(current.Observation!.Powers.Concat(current.Observation.Enemies.SelectMany(e => e.Powers)), p => p.Id == power);
        AssertCompleteDrawWitness(current, 12, 10);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 8)]
    public async Task BlurRetainsNativeBlockThenExpiresWithoutInterruptingDrawWitness(bool upgraded, int block)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "blur-native-draw-history",
            Deck: Enumerable.Repeat("Blur" + (upgraded ? "+" : ""), 12).ToArray(),
            Enemy: "Nibbit", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        for (int play = 0; play < 3; play++)
            current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play")));
        Assert.Equal(3 * block, current.Observation!.Block);
        Assert.Contains(current.Observation.Powers, power => power.Id == "BlurPower" && power.Amount == 3);
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        // A lone A10 Nibbit butts for 13. Remaining block survives native start-turn
        // clearing before Blur ticks; later turns exercise its final removal too.
        Assert.Equal(3 * block - 13, current.Observation!.Block);
        Assert.Contains(current.Observation.Powers, power => power.Id == "BlurPower" && power.Amount == 2);
        Assert.Equal(12, NativePublicCombatPrefixCondition.Create(current).Combats[0].Shuffle!.DrawPrefixKeys.Length);
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 12, 5);
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.DoesNotContain(current.Observation!.Powers, power => power.Id == "BlurPower");
        AssertCompleteDrawWitness(current, 12, 10);
    }

    [Theory]
    [InlineData(false, 6, 3)]
    [InlineData(true, 8, 4)]
    public async Task PoisonedStabNativeTicksAndRemovalPreserveInitialAndReshuffledDraws(bool upgraded, int attack, int poison)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "poison-native-draw-history",
            Deck: Enumerable.Repeat("PoisonedStab" + (upgraded ? "+" : ""), 12).ToArray(),
            Enemy: "Nibbit", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play")));
        Assert.Equal(1000 - attack, current.Observation!.Enemies.Single().Hp);
        Assert.Contains(current.Observation.Enemies.Single().Powers, power => power.Id == "PoisonPower" && power.Amount == poison);
        int poisonDamage = 0;
        for (int turn = 0; turn < poison; turn++)
        {
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
            poisonDamage += poison - turn;
            Assert.Equal(1000 - attack - poisonDamage, current.Observation!.Enemies.Single().Hp);
            Assert.Equal(poison - turn - 1, current.Observation.Enemies.Single().Powers.SingleOrDefault(p => p.Id == "PoisonPower")?.Amount ?? 0);
            if (turn == 1) AssertCompleteDrawWitness(current, 12, 5);
        }
        Assert.DoesNotContain(current.Observation!.Enemies.Single().Powers, power => power.Id == "PoisonPower");
        Assert.Equal("observed_prefix_complete", NativePublicReshuffleCondition.Create(current).CombatAudits[0].StopReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoisonDeathDispatchesRavenousAndRemovesTheOwnerBeforeLaterDraws(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "poison-slug-death-draw-history",
            Deck: Enumerable.Repeat("PoisonedStab" + (upgraded ? "+" : ""), 12).ToArray(),
            Enemies: ["CorpseSlug", "CorpseSlug"], EnemyHp: 9));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play" && a.Target == 0)));
        Assert.Equal(2, current.Observation!.Enemies.Length);
        Assert.InRange(current.Observation.Enemies.Single(e => e.Slot == 0).Hp, 1, 3);
        long beforeTurn = current.PublicEvidence!.Events[^1].EventOrdinal;
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        var survivor = Assert.Single(current.Observation!.Enemies);
        Assert.Equal(1, survivor.Slot);
        Assert.Contains(survivor.Powers, power => power.Id == "StrengthPower" && power.Amount == 5);
        Assert.Contains(current.PublicEvidence!.Events, e => e.EventOrdinal > beforeTurn
            && e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.Damage, TargetSlot: 0, Damage.Killed: true });
        // Death removes the owner and its powers directly; it emits no synthetic
        // negative amount fact. The complete public damage/roster history is retained.
        Assert.DoesNotContain(current.Observation.Enemies.SelectMany(e => e.Powers), power => power.Id == "PoisonPower");
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 12, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackingScalesWeakTargetDamageAndPreservesSubsequentNativeReshuffle(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "tracking-native-draw-history",
            Deck: ["Tracking" + (upgraded ? "+" : ""), "Neutralize", "StrikeSilent",
                "DefendSilent", "DefendSilent+", "PoisonedStab"],
            Enemy: "SludgeSpinner", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        foreach (string id in new[] { "Tracking", "Neutralize", "StrikeSilent" })
            current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
                && current.Observation!.Hand[a.Slot].Id == id)));
        Assert.Contains(current.Observation!.Powers, power => power.Id == "TrackingPower" && power.Amount == 50);
        Assert.Contains(current.Observation.Enemies.Single().Powers, power => power.Id == "WeakPower");
        Assert.Equal(988, current.Observation.Enemies.Single().Hp); // Neutralize 3, then 6 * 1.5.
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 6, 5);
        Assert.DoesNotContain("Tracking", Assert.Single(NativePublicReshuffleCondition.Create(current).Combats[0].Targets).PoolIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhantomBladesRetainsShivMetadataWithoutMovingItIntoTheReshuffle(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "phantom-retained-draw",
            Deck: ["PhantomBlades" + (upgraded ? "+" : ""), "Shiv", "DefendSilent", "DefendSilent+"],
            Enemy: "SludgeSpinner", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "PhantomBlades")));
        Assert.Contains(current.Observation!.Powers, power => power.Id == "PhantomBladesPower" && power.Amount == (upgraded ? 12 : 9));
        Assert.Contains("Retain", current.Observation.Hand.Single(card => card.Id == "Shiv").Keywords);
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 4, 2);
        Assert.Equal(3, current.Observation!.Hand.Length);
        Assert.Contains("Retain", current.Observation.Hand.Single(card => card.Id == "Shiv").Keywords);
        var pool = Assert.Single(NativePublicReshuffleCondition.Create(current).Combats[0].Targets).PoolKeys;
        Assert.Equal(new[] { new NativePublicDrawKey("DefendSilent", 0), new NativePublicDrawKey("DefendSilent", 1) },
            pool.OrderBy(key => key.Upgrade));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WellLaidPlansWitnessUsesActualDiscardWhileTheWholeRemainingHandIsRetained(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "well-laid-retained-draw",
            Deck: [.. Enumerable.Repeat("WellLaidPlans" + (upgraded ? "+" : ""), 4),
                "StrikeSilent", "StrikeSilent+", "DefendSilent", "DefendSilent+"],
            Enemy: "SludgeSpinner", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "WellLaidPlans")));
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id != "WellLaidPlans")));
        var before = current.Observation!;
        var discarded = Assert.Single(before.Discard);
        Assert.Equal(5, before.Hand.Length);
        Assert.Equal(1, before.DrawCount);
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 8, 1);
        Assert.Equal(7, current.Observation!.Hand.Length);
        Assert.Empty(current.Observation.Discard);
        Assert.Equal(before.Hand.Select(PublicJson.Serialize), current.Observation.Hand.Take(5).Select(PublicJson.Serialize));
        var target = Assert.Single(NativePublicReshuffleCondition.Create(current).Combats[0].Targets);
        Assert.Equal(NativePublicDrawKey.From(discarded), Assert.Single(target.PoolKeys));
        Assert.Equal(target.PoolKeys, target.DrawPrefixKeys);
    }

    private static async Task<DecisionPacket> NativeMixedUpgradeHistory()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "mixed-upgrade-native-history",
            Deck: [.. Enumerable.Repeat("DefendSilent", 8), .. Enumerable.Repeat("DefendSilent+", 4)],
            Enemy: "SludgeSpinner", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        for (int turn = 0; turn < 2; turn++)
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        return current;
    }

    [Theory]
    [InlineData("Armaments", false)]
    [InlineData("Armaments", true)]
    [InlineData("BlessingOfTheForge", false)]
    public async Task UpgradeChangingActionsStopBeforeTheirMutationAndLeaveLaterReshufflesUnconditioned(string model, bool upgraded)
    {
        bool potion = model == "BlessingOfTheForge";
        await using var session = await CombatSession.CreateAsync(new(Seed: "uncertified-upgrade-action",
            Deck: [.. Enumerable.Repeat(potion ? "DefendSilent" : "Armaments" + (upgraded ? "+" : ""), 6),
                .. Enumerable.Repeat("StrikeSilent", 6)],
            Potions: potion ? [model] : [], Enemy: "SludgeSpinner", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        long beforeAction = current.PublicEvidence!.Events[^1].EventOrdinal;
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => potion ? a.Kind == "potion"
            : a.Kind == "play" && current.Observation!.Hand[a.Slot].Id == model)));
        if (current.Status == "card_choice")
            current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "choose")));
        Assert.Contains(current.Observation!.Hand, card => card.Upgrade == 1);
        for (int i = 0; i < 2; i++)
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.Contains(current.PublicEvidence!.Events, e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.Shuffled });
        var condition = NativePublicCombatPrefixCondition.Create(current).Combats[0];
        Assert.Equal(7, condition.Shuffle!.DrawPrefixKeys.Length);
        Assert.Equal(beforeAction, condition.DrawPrefix!.ThroughEventOrdinal);
        Assert.Equal(potion ? "draw_cycle_potion_not_certified:" + model : "draw_cycle_play_not_certified:" + model,
            condition.DrawPrefix.StopReason);
        Assert.Equal(0, NativePublicReshuffleCondition.Create(current).EligibleShuffleCount);
    }

    [Fact]
    public async Task MixedUpgradeInitialAndReshuffleProposalsReplayEveryPublicCardField()
    {
        var root = await NativeMixedUpgradeHistory();
        AssertCompleteDrawWitness(root, 12, 5);
        var condition = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(4, condition.Combats[0].Shuffle!.DrawPrefixKeys.Count(key => key.Upgrade == 1));
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var random = new Rng(257903, "mixed-upgrade-independent-proposal");
        IDisposable Force(IReadOnlyList<ulong> words)
        {
            var queue = new Queue<ulong>(words);
            return LabelRandomScope.Enter(_ => queue.Dequeue());
        }
        var prefix = new NativePublicCombatPrefixProposal(condition, random.NextUnsignedLong, (words, _) => Force(words));
        var reshuffle = new NativePublicReshuffleProposal(NativePublicReshuffleCondition.Create(root), random.NextUnsignedLong,
            (words, _, _) => Force(words));
        bool attached = false;
        using var boundary = reshuffle.EnterScope();
        using var scope = LabelRandomScope.Enter(state => new MegaRandom(new SerializableRng
        { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong(),
            (rng, cards) => reshuffle.BeginShuffle(rng, cards) ?? prefix.BeginShuffle(rng, cards), context =>
            {
                if (!attached)
                {
                    var run = (RunState)context.Creature.CombatState!.RunState;
                    reshuffle.AttachHypotheticalRun(run); reshuffle.CombatEntering(0, entry, run.Rng.Shuffle);
                    prefix.AttachHypotheticalRun(run); prefix.CombatEntering(0, entry, run.Rng.Shuffle);
                    attached = true;
                }
                return prefix.BeginMonsterHp(context);
            });
        var replay = await NativeMixedUpgradeHistory();
        prefix.ValidateCompletion(); reshuffle.ValidateCompletion();
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(replay));
        Assert.True(prefix.NativeToProposalRatio.Numerator > 0);
        Assert.True(reshuffle.NativeToProposalRatio.Numerator > 0);
        _ = prefix.AcceptCorrection(random.NextUnsignedLong);
        _ = reshuffle.AcceptCorrection(random.NextUnsignedLong);
    }

    [Theory]
    [InlineData("Peck", false, 3)]
    [InlineData("Peck", true, 4)]
    [InlineData("DaggerSpray", false, 2)]
    [InlineData("DaggerSpray", true, 2)]
    public async Task ReviewedMultiHitCardsKeepDrawWitnessUnderShrink(string id, bool upgraded, int hits)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "draw-history-card:" + id,
            Deck: Enumerable.Repeat(id + (upgraded ? "+" : ""), 12).ToArray(),
            Enemy: "ShrinkerBeetle", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.Contains(current.Observation!.Powers, power => power.Id == "ShrinkPower");
        int historyCount = current.Observation.History.Length;
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play")));
        Assert.Equal(hits, current.Observation!.History.Skip(historyCount).Count(e => e.Kind == "damage"));
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 12, 5);
    }

    private static void AssertCompleteDrawWitness(DecisionPacket root, int initialCount, int reshuffledCount)
    {
        string before = PublicJson.Serialize(root);
        var first = NativePublicCombatPrefixCondition.Create(root).Combats[0];
        Assert.Equal("nosl.public-first-draw-cycle.v9", first.DrawPrefix!.CertificateVersion);
        Assert.Equal(initialCount, first.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("first_reshuffle", first.DrawPrefix.StopReason);
        var reshuffles = NativePublicReshuffleCondition.Create(root);
        Assert.Equal("observed_prefix_complete", reshuffles.CombatAudits[0].StopReason);
        var target = Assert.Single(reshuffles.Combats[0].Targets);
        Assert.Equal(reshuffledCount, target.DrawPrefixIds.Count);
        var facts = root.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicCombatFact>().ToArray();
        Assert.Equal(facts.TakeWhile(f => f.FactKind != PublicCombatFactKind.Shuffled)
            .Where(f => f.FactKind == PublicCombatFactKind.CardDrawn).Select(f => f.Cards.Single().Id), first.Shuffle.DrawPrefixIds);
        Assert.Equal(facts.SkipWhile(f => f.FactKind != PublicCombatFactKind.Shuffled)
            .Where(f => f.FactKind == PublicCombatFactKind.CardDrawn).Select(f => f.Cards.Single().Id), target.DrawPrefixIds);
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaggerThrowDrawAndExplicitDiscardCrossNativeFirstCycleAndReshuffle(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "dagger-throw-draw-cycle",
            Deck: Enumerable.Repeat("DaggerThrow" + (upgraded ? "+" : ""), 12).ToArray(),
            Enemy: "ShrinkerBeetle", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play")));
        Assert.Equal("card_choice", current.Status);
        Assert.Equal("DaggerThrow", current.Observation!.Choice!.Source);
        var pending = NativePublicCombatPrefixCondition.Create(current).Combats[0];
        Assert.Equal(8, pending.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("observed_prefix_complete", pending.DrawPrefix!.StopReason);
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "choose")));
        Assert.Equal(2, current.Observation!.Discard.Length); // selected card plus completed DaggerThrow
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 12, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DaggerThrowChoiceCertifiesOnlySelectedCardAndStopsBeforeSlyAutoplay(bool selectSly)
    {
        // Every seven-card opening contains both types, and the eighth card is a real new draw.
        await using var session = await CombatSession.CreateAsync(new(Seed: "dagger-throw-sly-choice",
            Deck: [.. Enumerable.Repeat("DaggerThrow", 4), .. Enumerable.Repeat("Ricochet", 4)],
            Enemy: "ShrinkerBeetle", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "DaggerThrow")));
        Assert.Equal("card_choice", current.Status);
        Assert.Contains(current.Observation!.Choice!.Candidates, card => card.Id == "Ricochet");
        Assert.Equal(8, NativePublicCombatPrefixCondition.Create(current).Combats[0].Shuffle!.DrawPrefixIds.Length);
        long choiceBoundary = current.PublicEvidence!.Events[^1].EventOrdinal;
        string selectedId = selectSly ? "Ricochet" : "DaggerThrow";
        var select = current.Actions.First(a => a.Kind == "choose"
            && current.Observation.Choice.Candidates[a.Selection![0]].Id == selectedId);
        current = recorder.Record(await session.StepAsync(select));
        var completed = NativePublicCombatPrefixCondition.Create(current).Combats[0];
        Assert.Equal(8, completed.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal(selectSly ? "draw_cycle_sly_discard_not_certified" : "observed_prefix_complete",
            completed.DrawPrefix!.StopReason);
        if (selectSly)
        {
            Assert.Equal(choiceBoundary, completed.DrawPrefix.ThroughEventOrdinal);
            Assert.Contains(current.PublicEvidence!.Events, e => e.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.CardStarted } fact && fact.Cards.Single().Id == "Ricochet");
        }
        else
        {
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
            AssertCompleteDrawWitness(current, 8, 5);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Deflect", false)]
    [InlineData("Ricochet", true)]
    public async Task DaggerThrowAutomaticDiscardBoundaryGuardsEmptySafeAndSlySelections(string? other, bool sly)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "dagger-throw-automatic",
            Deck: other is null ? ["DaggerThrow"] : ["DaggerThrow", other],
            Enemy: "ShrinkerBeetle", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "DaggerThrow")));
        var automatic = Assert.Single(current.PublicEvidence!.Events.Where(e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.AutomaticSelection, Model: "DaggerThrow" }));
        var input = NativePublicCombatPrefixCondition.Create(current).Combats[0];
        Assert.Equal(sly ? "draw_cycle_sly_discard_not_certified" : "observed_prefix_complete", input.DrawPrefix!.StopReason);
        if (sly) Assert.Equal(automatic.EventOrdinal - 1, input.DrawPrefix.ThroughEventOrdinal);
        Assert.Equal(other is null ? 1 : 2, input.Shuffle!.DrawPrefixIds.Length);
    }

    private static async Task<DecisionPacket> NativeDaggerThrowReshuffle()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "dagger-throw-selection-reshuffle",
            Deck: [.. Enumerable.Repeat("DaggerThrow", 6), .. Enumerable.Repeat("DefendSilent", 6)],
            Enemy: "ShrinkerBeetle", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.Equal(0, current.Observation!.DrawCount);
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "DaggerThrow")));
        var pending = NativePublicReshuffleCondition.Create(current);
        var target = Assert.Single(pending.Combats[0].Targets);
        Assert.Equal(7, target.PoolIds.Count); // prior discarded hand, before DaggerThrow/selection enter Discard
        Assert.Single(target.DrawPrefixIds);
        Assert.Equal(current.PublicEvidence!.Events[^1].EventOrdinal, target.WitnessEventOrdinal);
        Assert.Equal("card_choice", current.Status);
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "choose")));
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        AssertCompleteDrawWitness(current, 12, 6);
        return current;
    }

    [Fact]
    public async Task DaggerThrowReshuffleWitnessBeforeSelectionReplaysExactPublicPacketAndCompletesBothKernels()
    {
        var root = await NativeDaggerThrowReshuffle();
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var random = new Rng(190327, "dagger-throw-independent-proposal");
        IDisposable Force(IReadOnlyList<ulong> words)
        {
            var queue = new Queue<ulong>(words);
            return LabelRandomScope.Enter(_ => queue.Dequeue());
        }
        var prefix = new NativePublicCombatPrefixProposal(NativePublicCombatPrefixCondition.Create(root),
            random.NextUnsignedLong, (words, _) => Force(words));
        var reshuffle = new NativePublicReshuffleProposal(NativePublicReshuffleCondition.Create(root),
            random.NextUnsignedLong, (words, _, _) => Force(words));
        bool attached = false;
        using var boundary = reshuffle.EnterScope();
        using var scope = LabelRandomScope.Enter(state => new MegaRandom(new SerializableRng
        { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong(),
            (rng, cards) => reshuffle.BeginShuffle(rng, cards) ?? prefix.BeginShuffle(rng, cards), context =>
            {
                if (!attached)
                {
                    var run = (RunState)context.Creature.CombatState!.RunState;
                    reshuffle.AttachHypotheticalRun(run); reshuffle.CombatEntering(0, entry, run.Rng.Shuffle);
                    prefix.AttachHypotheticalRun(run); prefix.CombatEntering(0, entry, run.Rng.Shuffle);
                    attached = true;
                }
                return prefix.BeginMonsterHp(context);
            });
        var replay = await NativeDaggerThrowReshuffle();
        prefix.ValidateCompletion(); reshuffle.ValidateCompletion();
        Assert.Equal(1, prefix.ConditionedShuffleCount);
        Assert.Equal(1, reshuffle.ConditionedShuffleCount);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(replay));
        _ = prefix.AcceptCorrection(random.NextUnsignedLong);
        _ = reshuffle.AcceptCorrection(random.NextUnsignedLong);
    }

    [Theory]
    [InlineData(24001UL, new[] { 12 })]
    [InlineData(24004UL, new[] { 16, 8 })]
    [InlineData(24007UL, new[] { 13, 14, 12 })]
    [InlineData(24008UL, new[] { 12, 15, 12 })]
    public async Task FreshMapHistoriesCrossMeasuredDrawGapsUnderReviewedClosures(ulong seed, int[] prefixCounts)
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
                PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
        };
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        // No source object, source seed, tape, proposal address or audit field reaches either condition.
        string before = PublicJson.Serialize(root);
        var first = NativePublicCombatPrefixCondition.Create(root);
        var full = NativePublicReshuffleCondition.Create(root);
        Assert.Equal(prefixCounts.Length, first.Combats.Count);
        for (int index = 0; index < prefixCounts.Length; index++)
        {
            var input = first.Combats[index];
            Assert.NotNull(input.Shuffle);
            if (seed == 24008 && index == 2) Assert.NotNull(input.Hp);
            Assert.Equal(prefixCounts[index], input.Shuffle.DrawPrefixIds.Length);
            Assert.Contains(input.DrawPrefix!.StopReason, new[] { "observed_prefix_complete", "first_reshuffle" });
            Assert.Contains(full.CombatAudits[index].StopReason,
                new[] { "observed_prefix_complete", "combat_owner_ended", "combat_pre_settlement" });
            Assert.True(input.DrawPrefix.ThroughEventOrdinal > input.DecisionEventOrdinal);
            output.WriteLine($"{seed} C{index}: first={input.Shuffle.DrawPrefixIds.Length}, through={input.DrawPrefix.ThroughEventOrdinal}, stop={input.DrawPrefix.StopReason}; full={full.CombatAudits[index]}, reshuffles={full.Combats.GetValueOrDefault(index)?.Targets.Count ?? 0}");
        }
        Assert.Equal(before, PublicJson.Serialize(root));
    }
}
