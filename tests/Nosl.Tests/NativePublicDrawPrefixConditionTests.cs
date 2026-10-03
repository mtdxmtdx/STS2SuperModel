using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativePublicDrawPrefixConditionTests(ITestOutputHelper output)
{
    private static PublicCard Card(string id) => new(id, 0, 1, -1, "Skill", []);
    private sealed record Fixture(PublicRunEvidenceRecorder Recorder, long Owner, long First,
        NativeEntryAssets Entry, PublicObservation Observation, PublicAction Action);

    private static Fixture Opening(string action = "play", string played = "Backflip", string enemy = "SludgeSpinner",
        string potion = "SwiftPotion", PublicCard? second = null)
    {
        PublicCard[] deck = [Card(played), second ?? Card("DefendSilent"), Card("StrikeSilent"), Card("DefendSilent"),
            Card("Neutralize"), Card("DefendSilent"), Card("StrikeSilent"),
            Card("StrikeSilent"), Card("DefendSilent"), Card("StrikeSilent"), Card("Survivor"), Card("AscendersBane")];
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, deck,
            [new("RingOfTheSnake", new Dictionary<string, int>())], [potion, null], 3, 2, 0, 0);
        var assets = new PublicEvidenceAssets(56, 70, 99, deck, entry.Relics, entry.Potions.ToImmutableArray(), 3, 2, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.EntryAssets, assets: assets));
        foreach (var card in deck.Take(7)) recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: [card]));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0, model: enemy));
        var observation = new PublicObservation("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            deck.Take(7).ToArray(), [], [], deck.Skip(7).Select(card => new CardCount(card, 1)).ToArray(), [], 5,
            entry.Potions, ["RingOfTheSnake"], [], [new(0, enemy, 42, 42, 0, [], [])], [], null);
        var selected = new PublicAction(0, action, action is "play" or "potion" ? 0 : -1);
        long first = Decision(recorder, owner, observation, [selected]);
        return new(recorder, owner, first, entry, observation, selected);
    }

    private static long Decision(PublicRunEvidenceRecorder recorder, long owner, PublicObservation observation, PublicAction[] actions) =>
        recorder.Record(owner, new PublicCombatDecision(observation.Choice is null ? "player_decision" : "card_choice",
            observation, actions, recorder.Capture().Events.Last(item => item.OwnerOrdinal == owner).EventOrdinal, true));

    private static NativePublicCombatPrefixInput Condition(Fixture fixture) =>
        NativePublicCombatPrefixCondition.Create(fixture.Recorder.Capture()).Combats[0];

    [Fact]
    public void ExtendsAcrossCardAndTurnDrawsThenStopsBeforeFirstReshuffle()
    {
        var f = Opening();
        f.Recorder.Record(f.Owner, new PublicCombatActionTaken(f.First, f.Action));
        f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardStarted, cards: [f.Entry.Deck[0]]));
        foreach (var card in f.Entry.Deck.Skip(7).Take(2))
            f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: [card]));
        f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardPlayed, cards: [f.Entry.Deck[0]], resultPile: PublicCardPile.Discard));
        var next = f.Observation with { Hand = f.Entry.Deck.Skip(1).Take(8).ToArray(), Discard = [f.Entry.Deck[0]],
            DrawCount = 3, UnknownDraw = f.Entry.Deck.Skip(9).Select(card => new CardCount(card, 1)).ToArray() };
        var end = new PublicAction(1, "end_turn");
        long decision = Decision(f.Recorder, f.Owner, next, [end]);
        f.Recorder.Record(f.Owner, new PublicCombatActionTaken(decision, end));
        foreach (var card in f.Entry.Deck.Skip(9))
            f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: [card]));
        long lastDraw = f.Recorder.Capture().Events[^1].EventOrdinal;
        f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.Shuffled));
        f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: [f.Entry.Deck[1]]));
        string before = PublicRunEvidenceJson.Serialize(f.Recorder.Capture());
        var input = Condition(f);
        Assert.Equal(f.Entry.Deck.Select(card => card.Id), input.Shuffle!.DrawPrefixIds);
        Assert.Equal(7, input.DrawPrefix!.InitialDrawCount);
        Assert.Equal(12, input.DrawPrefix.DrawPrefixCount);
        Assert.Equal(lastDraw, input.DrawPrefix.ThroughEventOrdinal);
        Assert.Equal("first_reshuffle", input.DrawPrefix.StopReason);
        Assert.Equal(before, PublicRunEvidenceJson.Serialize(f.Recorder.Capture()));
    }

    [Theory]
    [InlineData("unknown_card", "draw_cycle_play_not_certified:ThinkingAhead")]
    [InlineData("unknown_potion", "draw_cycle_potion_not_certified:GamblersBrew")]
    [InlineData("enemy_turn", "draw_cycle_enemy_turn_not_certified")]
    [InlineData("hand_end", "draw_cycle_hand_end_effect_not_certified")]
    [InlineData("sly_discard", "draw_cycle_sly_discard_not_certified")]
    [InlineData("gap", "owner_evidence_gap")]
    [InlineData("global_gap", "global_evidence_gap")]
    [InlineData("generation", "draw_cycle_generation_not_certified")]
    [InlineData("new_power", "draw_cycle_power_not_certified:ConfusionPower")]
    [InlineData("missing_action", "draw_cycle_effect_without_certified_action")]
    public void StopsBeforeUnprovedTransitionsRatherThanConcatenatingDrawFacts(string change, string reason)
    {
        var f = Opening(action: change == "unknown_potion" ? "potion" : change is "enemy_turn" or "hand_end" ? "end_turn" : "play",
            played: change == "unknown_card" ? "ThinkingAhead" : change == "sly_discard" ? "Survivor" : "Backflip",
            enemy: change == "enemy_turn" ? "TwoTailedRat" : "SludgeSpinner",
            potion: change == "unknown_potion" ? "GamblersBrew" : "SwiftPotion",
            second: change == "hand_end" ? Card("Burn") : change == "sly_discard" ? Card("Reflex") with { Keywords = ["Sly"] } : null);
        if (change != "missing_action") f.Recorder.Record(f.Owner, new PublicCombatActionTaken(f.First, f.Action));
        if (change is "gap" or "global_gap") f.Recorder.RecordGap(change == "gap" ? f.Owner : null, PublicEvidenceGapReason.Interrupted);
        if (change == "generation") f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.HiddenCardGenerated));
        if (change == "new_power") f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.PowerChanged,
            targetSlot: -2, model: "ConfusionPower", amount: 1));
        // For an unknown potion this draw precedes PotionUsed, and ThinkingAhead's
        // unrecorded top insertion may occur only after its own first two draws.
        f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: [f.Entry.Deck[7]]));
        var input = Condition(f);
        Assert.NotNull(input.Shuffle);
        Assert.Equal(7, input.Shuffle.DrawPrefixIds.Length);
        Assert.Equal(reason, input.DrawPrefix!.StopReason);
    }

    [Theory]
    [InlineData("play", "Backflip", 2)]
    [InlineData("potion", "Backflip", 3)]
    public void ObservedSafeDrawEffectsCanExtendAPartialCycle(string kind, string card, int count)
    {
        var f = Opening(kind, card);
        f.Recorder.Record(f.Owner, new PublicCombatActionTaken(f.First, f.Action));
        foreach (var drawn in f.Entry.Deck.Skip(7).Take(count))
            f.Recorder.Record(f.Owner, new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: [drawn]));
        var input = Condition(f);
        Assert.Equal(7 + count, input.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("observed_prefix_complete", input.DrawPrefix!.StopReason);
    }

    [Theory]
    [InlineData("play", 9)]
    [InlineData("potion", 10)]
    public async Task NativeCardAndPotionEffectsThenTurnDrawsRespectTheCertifiedCycle(string kind, int afterEffect)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "native-draw-cycle:" + kind,
            Deck: Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? "Backflip" : "DefendSilent").ToArray(),
            Potions: ["SwiftPotion"], Enemy: "SludgeSpinner", EnemyHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        // Controlled native fixture: publish its explicitly supplied pre-setup inventory.
        // Production derives this anchor from typed observer evidence, never a graph.
        DecisionPacket Anchored(DecisionPacket packet) => packet with { Observation = packet.Observation! with
        {
            History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                .. packet.Observation.History.Skip(1)],
        } };
        var opening = Anchored(session.Observe());
        recorder.ObserveCombatDecision(owner, opening);
        var action = opening.Actions.First(a => a.Kind == kind && (kind == "potion"
            || opening.Observation!.Hand[a.Slot].Id == "Backflip"));
        var after = Anchored(await session.StepAsync(action));
        recorder.ObserveCombatDecision(owner, after);
        var condition = NativePublicCombatPrefixCondition.Create(recorder.Capture()).Combats[0];
        Assert.Equal(afterEffect, condition.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("observed_prefix_complete", condition.DrawPrefix!.StopReason);
        var next = Anchored(await session.StepAsync(after.Actions.Single(a => a.Kind == "end_turn")));
        recorder.ObserveCombatDecision(owner, next);
        var extended = NativePublicCombatPrefixCondition.Create(recorder.Capture()).Combats[0];
        Assert.Equal(12, extended.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("first_reshuffle", extended.DrawPrefix!.StopReason);
        Assert.Equal(next.Observation!.History.TakeWhile(e => e.Kind != "shuffle").Where(e => e.Kind == "draw")
            .Select(e => PublicJson.Read<PublicCard>(e.Detail).Id), extended.Shuffle.DrawPrefixIds);
    }

    private static NativeTapePrior Hybrid => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Theory]
    [InlineData("NeowsFury", false)]
    [InlineData("NeowsFury", true)]
    [InlineData("Strangle", false)]
    [InlineData("Strangle", true)]
    [InlineData("Finisher", false)]
    [InlineData("Finisher", true)]
    [InlineData("SuckerPunch", false)]
    [InlineData("SuckerPunch", true)]
    public async Task ReviewedV2CardAndPowerContinuationsPreserveThePileThroughNativeDraws(string id, bool upgraded)
    {
        string cardName = id + (upgraded ? "+" : "");
        await using var session = await CombatSession.CreateAsync(new(Seed: "closure-v2:" + cardName,
            Deck: Enumerable.Repeat(cardName, 6).Concat(Enumerable.Repeat("StrikeSilent", 6)).ToArray(),
            Potions: ["SwiftPotion"], Enemy: "SludgeSpinner", EnemyHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Record(DecisionPacket packet)
        {
            packet = packet with { Observation = packet.Observation! with
            {
                History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                    .. packet.Observation.History.Skip(1)],
            } };
            recorder.ObserveCombatDecision(owner, packet);
            var certificate = NativePublicCombatPrefixCondition.Create(recorder.Capture()).Combats[0];
            Assert.Equal("nosl.public-first-draw-cycle.v9", certificate.DrawPrefix!.CertificateVersion);
            Assert.Contains(certificate.DrawPrefix.StopReason, new[] { "observed_prefix_complete", "first_reshuffle" });
            return packet;
        }
        var current = Record(session.Observe());
        var strike = current.Actions.First(action => action.Kind == "play" && current.Observation!.Hand[action.Slot].Id == "StrikeSilent");
        current = Record(await session.StepAsync(strike));
        int beforeCardDrawCount = current.Observation!.DrawCount;
        var selected = current.Actions.First(action => action.Kind == "play" && current.Observation.Hand[action.Slot].Id == id);
        current = Record(await session.StepAsync(selected));
        Assert.Equal(beforeCardDrawCount, current.Observation!.DrawCount);
        if (id == "NeowsFury")
        {
            Assert.Empty(current.Observation.Discard);
            Assert.Contains(current.Observation.Exhaust, card => card.Id == id);
        }
        if (id == "Strangle") Assert.Contains(current.Observation.Enemies[0].Powers, power => power.Id == "StranglePower");
        if (id == "SuckerPunch") Assert.Contains(current.Observation.Enemies[0].Powers, power => power.Id == "WeakPower");
        int factsBefore = current.Observation.History.Length;
        var follow = current.Actions.FirstOrDefault(action => action.Kind == "play" && current.Observation.Hand[action.Slot].Id == "StrikeSilent")
            ?? current.Actions.First(action => action.Kind == "play");
        current = Record(await session.StepAsync(follow));
        if (id == "Strangle") Assert.True(current.Observation!.History.Skip(factsBefore).Count(item => item.Kind == "damage") >= 2);
        current = Record(await session.StepAsync(current.Actions.First(action => action.Kind == "potion")));
        Assert.Equal(10, NativePublicCombatPrefixCondition.Create(recorder.Capture()).Combats[0].Shuffle!.DrawPrefixIds.Length);
        current = Record(await session.StepAsync(current.Actions.Single(action => action.Kind == "end_turn")));
        Assert.Equal(12, NativePublicCombatPrefixCondition.Create(recorder.Capture()).Combats[0].Shuffle!.DrawPrefixIds.Length);
        var reshuffles = NativePublicReshuffleCondition.Create(current with { PublicEvidence = recorder.Capture() });
        var reshuffle = Assert.Single(reshuffles.Combats[0].Targets);
        Assert.Equal(id == "NeowsFury" ? 9 : 10, reshuffle.PoolIds.Count);
        Assert.Equal(3, reshuffle.DrawPrefixIds.Count);
        if (id == "Strangle") Assert.DoesNotContain(current.Observation!.Enemies[0].Powers, power => power.Id == "StranglePower");
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public async Task NativeNeowsFuryRetrievesItsBaseOrUpgradedLimitWithoutAHiddenDraw(bool upgraded, int count)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "closure-v2-retrieval",
            Deck: Enumerable.Repeat("NeowsFury" + (upgraded ? "+" : ""), 4)
                .Concat(Enumerable.Repeat("StrikeSilent", 8)).ToArray(),
            Potions: ["EnergyPotion"], Enemy: "SludgeSpinner", EnemyHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Record(DecisionPacket packet)
        {
            packet = packet with { Observation = packet.Observation! with
            {
                History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                    .. packet.Observation.History.Skip(1)],
            } };
            recorder.ObserveCombatDecision(owner, packet); return packet;
        }
        var current = Record(session.Observe());
        for (int i = 0; i < 3; i++) current = Record(await session.StepAsync(current.Actions.First(action =>
            action.Kind == "play" && current.Observation!.Hand[action.Slot].Id == "StrikeSilent")));
        Assert.Equal(3, current.Observation!.Discard.Length);
        current = Record(await session.StepAsync(current.Actions.Single(action => action.Kind == "potion")));
        current = Record(await session.StepAsync(current.Actions.First(action => action.Kind == "play"
            && current.Observation!.Hand[action.Slot].Id == "NeowsFury")));
        Assert.Equal(3 - count, current.Observation!.Discard.Length);
        Assert.Equal(3 + count, current.Observation.Hand.Length);
        Assert.Equal(5, current.Observation.DrawCount);
        Assert.Equal(7, current.Observation.History.Count(item => item.Kind == "draw"));
        var certificate = NativePublicCombatPrefixCondition.Create(recorder.Capture()).Combats[0];
        Assert.Equal("observed_prefix_complete", certificate.DrawPrefix!.StopReason);
    }

    [Theory]
    [InlineData(11004UL, 1, "Strangle")]
    [InlineData(11004UL, 2, "Finisher")]
    [InlineData(11007UL, 0, "NeowsFury")]
    [InlineData(11007UL, 2, "SuckerPunch")]
    public async Task RetainedMeasuredCardBlockersAreCrossedByTheV2Closure(ulong seed, int index, string id)
    {
        var prior = Hybrid;
        var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
        var input = NativePublicCombatPrefixCondition.Create(root).Combats[index];
        var played = root.PublicEvidence!.Events.First(item => item.OwnerOrdinal == input.OwnerOrdinal
            && item.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardPlayed } fact && fact.Cards[0].Id == id);
        Assert.True(input.DrawPrefix!.ThroughEventOrdinal >= played.EventOrdinal,
            $"{seed} combat {index}: {input.DrawPrefix.StopReason} at {input.DrawPrefix.ThroughEventOrdinal}, before {played.EventOrdinal}");
        Assert.DoesNotContain(id, input.DrawPrefix.StopReason);
        Assert.Equal("nosl.public-first-draw-cycle.v9", input.DrawPrefix.CertificateVersion);
        var full = NativePublicReshuffleCondition.Create(root).CombatAudits[index];
        output.WriteLine($"{seed} C{index}: first prefix={input.Shuffle!.DrawPrefixIds.Length}, through={input.DrawPrefix.ThroughEventOrdinal}, stop={input.DrawPrefix.StopReason}; full through={full.ThroughEventOrdinal}, stop={full.StopReason}");
    }

    [Theory]
    [InlineData(11002UL, 16, "first_reshuffle")]
    [InlineData(11003UL, 12, "observed_prefix_complete")]
    [InlineData(11004UL, 13, "first_reshuffle")]
    public async Task NativeRetainedHistoriesProveTheirWholeObservedFirstCycle(ulong sourceSeed, int firstCount, string stop)
    {
        var prior = Hybrid;
        var recipe = prior.Draw(new Rng(sourceSeed, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        var root = world.Observe();
        string before = PublicJson.Serialize(root);
        var condition = NativePublicCombatPrefixCondition.Create(root);
        var first = condition.Combats[0];
        Assert.NotNull(first.Shuffle);
        Assert.True(first.HpCount > 0, first.HpReason);
        Assert.Equal(firstCount, first.Shuffle.DrawPrefixIds.Length);
        Assert.Equal(stop, first.DrawPrefix!.StopReason);
        var facts = root.PublicEvidence!.Events.Where(item => item.OwnerOrdinal == first.OwnerOrdinal)
            .Select(item => item.Payload).OfType<PublicCombatFact>()
            .TakeWhile(fact => fact.FactKind != PublicCombatFactKind.Shuffled)
            .Where(fact => fact.FactKind == PublicCombatFactKind.CardDrawn).Select(fact => fact.Cards.Single().Id);
        Assert.Equal(facts, first.Shuffle.DrawPrefixIds);
        if (sourceSeed == 11004)
        {
            Assert.All(condition.Combats.Values, combat => Assert.NotNull(combat.Shuffle));
            Assert.Equal("first_reshuffle", condition.Combats[1].DrawPrefix!.StopReason);
            Assert.Equal("observed_prefix_complete", condition.Combats[2].DrawPrefix!.StopReason);
        }
        Assert.Equal(before, PublicJson.Serialize(root));
        if (sourceSeed is 11002 or 11003)
        {
            // Same-recipe native kernel verification, not an independent posterior
            // draw. Proposal randomness differs; every full public event stays exact.
            var proposed = recipe with { ProposalSeed = recipe.ProposalSeed ^ 0xabcdefUL };
            var tape = NativeLabelTape.ForDeclaredPrior(prior, proposed, publicCombatCondition: condition);
            await using var replay = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, proposed, tape);
            Assert.NotNull(replay);
            tape.ValidateProposalCompletion();
            Assert.Equal(condition.EligibleShuffleCount, tape.ConditionedPublicCombatShuffles);
            Assert.Equal(condition.EligibleHpCount, tape.ConditionedPublicCombatHp);
            Assert.Equal(before, PublicJson.Serialize(replay.Observe()));
        }
    }
}
