using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativeSlimeDrawClosureTests(ITestOutputHelper output)
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
    private static PublicCard Slimed => new("Slimed", 0, 1, -1, "Status", ["Exhaust"],
        new(false, false, 1, -1, false, false, 0, false, false, false, false, null, []), [], null,
        new Dictionary<string, string> { ["targetType"] = "None", ["tags"] = "" });

    private static DecisionPacket Fixture(string variation)
    {
        var defend = new PublicCard("DefendSilent", 0, 1, -1, "Skill", []);
        PublicCard[] deck = Enumerable.Repeat(defend, 7).ToArray();
        if (variation == "sly") deck[0] = new("Survivor", 0, 1, -1, "Skill", []);
        if (variation == "sly") deck[1] = new("Reflex", 0, -2, -1, "Skill", ["Sly"]);
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, deck,
            [new("RingOfTheSnake", new Dictionary<string, int>())], [null, null], 3, 2, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        void Fact(PublicCombatFactKind kind, PublicCard? card = null) => recorder.Record(owner,
            new PublicCombatFact(kind, cards: card is null ? [] : [card]));
        Fact(PublicCombatFactKind.Started);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.EntryAssets, assets: Assets(entry)));
        foreach (var card in deck) Fact(PublicCombatFactKind.CardDrawn, card);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
        string enemy = variation == "twig_s" ? "TwigSlimeS" : "LeafSlimeS";
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0, model: enemy));
        var observation = new PublicObservation("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            deck, [], [], [], [], 0, entry.Potions, ["RingOfTheSnake"], [],
            [new(0, enemy, 42, 42, 0, [], [new(variation == "attack_intent" ? "Attack" : "StatusCard", null, null)])], [], null);
        var action = new PublicAction(0, variation is "play_generation" or "sly" ? "play" : "end_turn", variation is "play_generation" or "sly" ? 0 : -1);
        long first = recorder.Record(owner, new PublicCombatDecision("player_decision", observation, [action], recorder.Capture().Events[^1].EventOrdinal, true));
        if (variation != "no_action") recorder.Record(owner, new PublicCombatActionTaken(first, action));
        if (variation == "gap") recorder.RecordGap(owner, PublicEvidenceGapReason.Interrupted);
        if (variation == "after_turn") Fact(PublicCombatFactKind.PlayerTurnEnded);
        if (variation == "hidden") Fact(PublicCombatFactKind.HiddenCardGenerated);
        else Fact(PublicCombatFactKind.CardGenerated, variation switch
        {
            "unknown" => defend,
            "sly_generated" => Slimed with { Keywords = ["Exhaust", "Sly"] },
            "modified" => Slimed with { Enchantments = [new("Sharp", 1)] },
            _ => Slimed,
        });
        if (variation == "too_many") Fact(PublicCombatFactKind.CardGenerated, Slimed);
        Fact(PublicCombatFactKind.PlayerTurnEnded);
        Fact(PublicCombatFactKind.Shuffled);
        foreach (var card in deck.Take(5)) Fact(PublicCombatFactKind.CardDrawn, card);
        if (variation == "after_draw") Fact(PublicCombatFactKind.CardGenerated, Slimed);
        observation = observation with { Turn = 2, Hand = deck.Take(5).ToArray(),
            UnknownDraw = [new(defend, 2), new(Slimed, variation == "oversized_pool" ? 2 : 1)],
            DrawCount = variation == "oversized_pool" ? 4 : 3 };
        if (variation != "missing_witness") recorder.Record(owner, new PublicCombatDecision("player_decision", observation,
            [new(1, "end_turn")], recorder.Capture().Events[^1].EventOrdinal, variation != "gap"));
        return new("player_decision", observation, [new(1, "end_turn")], PublicEvidence: recorder.Capture());
    }

    [Theory]
    [InlineData("valid", "observed_prefix_complete", 1)]
    [InlineData("hidden", "draw_cycle_generation_not_certified", 0)]
    [InlineData("unknown", "draw_cycle_generation_not_certified", 0)]
    [InlineData("modified", "draw_cycle_generation_not_certified", 0)]
    [InlineData("sly_generated", "draw_cycle_generation_not_certified", 0)]
    [InlineData("too_many", "draw_cycle_generation_not_certified", 0)]
    [InlineData("after_turn", "draw_cycle_generation_not_certified", 0)]
    [InlineData("after_draw", "draw_cycle_generation_not_certified", 0)]
    [InlineData("attack_intent", "draw_cycle_generation_not_certified", 0)]
    [InlineData("twig_s", "draw_cycle_generation_not_certified", 0)]
    [InlineData("no_action", "draw_cycle_generation_not_certified", 0)]
    [InlineData("play_generation", "draw_cycle_generation_not_certified", 0)]
    [InlineData("sly", "draw_cycle_sly_discard_not_certified", 0)]
    [InlineData("gap", "owner_evidence_gap", 0)]
    [InlineData("oversized_pool", "reshuffle_snapshot_pool_mismatch", 0)]
    [InlineData("missing_witness", "observed_prefix_complete", 0)]
    public void OnlyClosedPlainDiscardGenerationCanEnlargeTheWitnessedPool(string variation, string stop, int count)
    {
        var root = Fixture(variation); string before = PublicJson.Serialize(root);
        var first = NativePublicCombatPrefixCondition.Create(root).Combats[0];
        Assert.Equal(7, first.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("nosl.public-first-draw-cycle.v11", first.DrawPrefix!.CertificateVersion);
        var condition = NativePublicReshuffleCondition.Create(root);
        Assert.Equal(stop, condition.CombatAudits[0].StopReason);
        Assert.Equal(count, condition.EligibleShuffleCount);
        if (count > 0)
        {
            var target = Assert.Single(condition.Combats[0].Targets);
            Assert.Equal(8, target.PoolIds.Count); Assert.Single(target.PoolIds, id => id == "Slimed");
        }
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    private static async Task<DecisionPacket> NativeTurns(string enemy, bool playSlimed = false)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "slime-closure:" + enemy,
            Deck: Enumerable.Repeat("DefendSilent", 12).ToArray(), Enemy: enemy, EnemyHp: 1000, Hp: 1000, MaxHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Record(DecisionPacket packet)
        { packet = Anchor(packet, entry); recorder.ObserveCombatDecision(owner, packet); return packet; }
        var current = Record(session.Observe());
        for (int turn = 0; turn < 3; turn++)
        {
            current = Record(await session.StepAsync(current.Actions.Single(action => action.Kind == "end_turn")));
            if (playSlimed && current.Actions.FirstOrDefault(action => action.Kind == "play"
                && current.Observation!.Hand[action.Slot].Id == "Slimed") is { } play)
                current = Record(await session.StepAsync(play));
        }
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(current with { PublicEvidence = recorder.Capture() }));
    }

    [Theory]
    [InlineData("TwigSlimeS")]
    [InlineData("TwigSlimeM")]
    [InlineData("LeafSlimeS")]
    [InlineData("LeafSlimeM")]
    public async Task NativeSlimeTurnsPreserveInitialDrawsAndWitnessGeneratedStatusPools(string enemy)
    {
        var root = await NativeTurns(enemy, playSlimed: true);
        var input = NativePublicCombatPrefixCondition.Create(root).Combats[0];
        Assert.Equal(12, input.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("first_reshuffle", input.DrawPrefix!.StopReason);
        var later = NativePublicReshuffleCondition.Create(root);
        Assert.Equal("observed_prefix_complete", later.CombatAudits[0].StopReason);
        Assert.NotEmpty(later.Combats[0].Targets);
        var generated = root.PublicEvidence!.Events.Select(item => item.Payload).OfType<PublicCombatFact>()
            .Where(fact => fact.FactKind == PublicCombatFactKind.CardGenerated).ToArray();
        Assert.Equal(enemy != "TwigSlimeS", generated.Length > 0);
        Assert.All(generated, fact => Assert.True(NativePublicDrawPrefixCondition.IsPlainGeneratedSlimed(fact.Cards.Single()), PublicJson.Serialize(fact.Cards.Single())));
        if (enemy != "TwigSlimeS") Assert.Contains(later.Combats[0].Targets, target => target.PoolIds.Contains("Slimed"));
        if (enemy == "LeafSlimeM")
        {
            Assert.Contains(root.PublicEvidence.Events, item => item.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.CardPlayed, ResultPile: PublicCardPile.Exhaust } fact && fact.Cards[0].Id == "Slimed");
        }
    }

    private static ulong OriginalWord(LabelRandomState state) => new MegaRandom(new SerializableRng
    { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGeneratedPhysicalCardsRequireTheFixedWitnessedPoolAndReplayPublicHistory(bool differentPool)
    {
        var root = await NativeTurns("LeafSlimeM");
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var condition = NativePublicReshuffleCondition.Create(root);
        if (differentPool)
        {
            // The public entry-plus-generation inventory upper bound is unchanged. A smaller witnessed pool would have
            // a different envelope and must reject an actual larger latent pool, never
            // silently recompute its factor from the available native cards.
            long witness = condition.Combats[0].Targets[0].WitnessEventOrdinal;
            var events = root.PublicEvidence!.Events.Select(item =>
            {
                if (item.EventOrdinal != witness) return item;
                var snapshot = (PublicCombatDecision)item.Payload;
                var observation = snapshot.Observation;
                var counts = observation.UnknownDraw.ToArray();
                int index = Array.FindIndex(counts, count => count.Card.Id == "DefendSilent");
                counts[index] = counts[index] with { Count = counts[index].Count - 1 };
                return new PublicRunEvidenceEvent(item.EventOrdinal, item.OwnerOrdinal, new PublicCombatDecision(snapshot.Status,
                    observation with { UnknownDraw = counts.Where(count => count.Count > 0).ToArray(), DrawCount = observation.DrawCount - 1 },
                    snapshot.Actions, snapshot.HistoryThroughEventOrdinal, snapshot.HistoryCompleteFromCombatStart));
            }).ToImmutableArray();
            root = root with { PublicEvidence = new(PublicRunEvidence.Version, true, events) };
            condition = NativePublicReshuffleCondition.Create(root);
            Assert.Equal(13, condition.Combats[0].Targets[0].PoolIds.Count);
        }
        var random = new Rng(68294, "generated-physical-reshuffle");
        int generatedPhysical = 0;
        IDisposable Force(IReadOnlyList<ulong> words)
        {
            var queue = new Queue<ulong>(words);
            return LabelRandomScope.Enter(_ => queue.Dequeue());
        }
        var prefix = new NativePublicCombatPrefixProposal(NativePublicCombatPrefixCondition.Create(root),
            random.NextUnsignedLong, (words, _) => Force(words));
        var proposal = new NativePublicReshuffleProposal(condition, random.NextUnsignedLong, (words, _, _) => Force(words));
        bool attached = false;
        using var boundary = proposal.EnterScope();
        using var scope = LabelRandomScope.Enter(OriginalWord, (rng, cards) =>
        {
            if (LabelCombatReshuffleScope.Current is not null)
                generatedPhysical += cards.OfType<CardModel>().Count(card => card.DeckVersion is null);
            return proposal.BeginShuffle(rng, cards) ?? prefix.BeginShuffle(rng, cards);
        }, context =>
        {
            if (!attached)
            {
                var run = (RunState)context.Creature.CombatState!.RunState;
                proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, entry, run.Rng.Shuffle); attached = true;
                prefix.AttachHypotheticalRun(run); prefix.CombatEntering(0, entry, run.Rng.Shuffle);
            }
            return prefix.BeginMonsterHp(context);
        });
        if (differentPool)
        {
            await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => NativeTurns("LeafSlimeM"));
            Assert.True(generatedPhysical > 0);
            Assert.Equal(0, proposal.ConditionedShuffleCount);
            Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            return;
        }
        var replay = await NativeTurns("LeafSlimeM");
        proposal.ValidateCompletion(); prefix.ValidateCompletion(); Assert.True(generatedPhysical > 0);
        Assert.Equal(1, prefix.ConditionedShuffleCount);
        Assert.Equal(condition.EligibleShuffleCount, proposal.ConditionedShuffleCount);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(replay));
        _ = proposal.AcceptCorrection(random.NextUnsignedLong);
        _ = prefix.AcceptCorrection(random.NextUnsignedLong);
    }

    [Fact]
    public async Task NativeSlimedDrawShufflesDiscardBeforeExhaustingThePlayedCard()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "slimed-triggered-shuffle",
            Deck: ["Slimed", .. Enumerable.Repeat("DefendSilent", 6)], Enemy: "TwigSlimeS", EnemyHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Record(DecisionPacket packet)
        { packet = Anchor(packet, entry); recorder.ObserveCombatDecision(owner, packet); return packet; }
        var current = Record(session.Observe());
        foreach (string id in new[] { "DefendSilent", "DefendSilent", "Slimed" })
            current = Record(await session.StepAsync(current.Actions.First(action => action.Kind == "play"
                && current.Observation!.Hand[action.Slot].Id == id)));
        Assert.Contains(current.Observation!.Exhaust, card => card.Id == "Slimed");
        var condition = NativePublicReshuffleCondition.Create(current with { PublicEvidence = recorder.Capture() });
        var target = Assert.Single(condition.Combats[0].Targets);
        Assert.Equal(["DefendSilent", "DefendSilent"], target.PoolIds);
        Assert.Equal(["DefendSilent"], target.DrawPrefixIds);
        Assert.Equal("observed_prefix_complete", condition.CombatAudits[0].StopReason);
    }

    [Theory]
    [InlineData(11006UL, 55, 12, 76, "draw_cycle_play_not_certified:Juggling")]
    [InlineData(11007UL, 48, 14, 81, "first_reshuffle")]
    public async Task RetainedSlimeRootsCrossTheMeasuredFirstTurnDrawBlocker(ulong seed, long previousBlockedDraw,
        int initialCount, long through, string stop)
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
        };
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
        var condition = NativePublicCombatPrefixCondition.Create(root);
        var full = NativePublicReshuffleCondition.Create(root);
        Assert.True(condition.Combats[0].DrawPrefix!.ThroughEventOrdinal >= previousBlockedDraw);
        Assert.Equal(initialCount, condition.Combats[0].Shuffle!.DrawPrefixIds.Length);
        Assert.Equal(through, condition.Combats[0].DrawPrefix!.ThroughEventOrdinal);
        Assert.Equal(stop, condition.Combats[0].DrawPrefix!.StopReason);
        if (seed == 11007)
        {
            Assert.Equal("combat_owner_ended", full.CombatAudits[0].StopReason);
            Assert.Equal(123, full.CombatAudits[0].ThroughEventOrdinal);
            Assert.Single(full.Combats[0].Targets);
        }
        foreach (var (index, input) in condition.Combats)
        {
            var audit = full.CombatAudits[index];
            var opening = (PublicCombatDecision)root.PublicEvidence!.Events.Single(item => item.EventOrdinal == input.DecisionEventOrdinal).Payload;
            output.WriteLine($"{seed} C{index}: enemies={string.Join(',', opening.Observation.Enemies.Select(enemy => enemy.Id))}, first={input.Shuffle!.DrawPrefixIds.Length}, through={input.DrawPrefix!.ThroughEventOrdinal}, stop={input.DrawPrefix.StopReason}; full through={audit.ThroughEventOrdinal}, stop={audit.StopReason}, cycles={full.Combats.GetValueOrDefault(index)?.Targets.Count ?? 0}");
        }
    }
}
