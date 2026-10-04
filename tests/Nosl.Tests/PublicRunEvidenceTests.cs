using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class PublicRunEvidenceTests
{
    private static PublicCard Card(string id = "Strike") => new(id, 0, 1, 0, "Attack", ["Strike"],
        new(false, false, 1, 0, false, false, 0, false, false, false, false, null, [new("set", 1)]),
        [new("Test", 1)], null, new Dictionary<string, string> { ["counter"] = "0" });
    private static PublicEvidenceAssets Assets() => new(70, 70, 99, [Card()], [new("RingOfTheSnake", new Dictionary<string, int>())],
        [null, null], 3, 2, 0, 0);
    private static PublicRunEvidenceRecorder Recorder(bool fromStart = true) => new(fromStart ? new("Silent", 10, Assets()) : null);
    private static PublicObservation Observation(PublicEvent[]? history = null) => new("nosl.public.v2", 70, 10, 1, 70, 70,
        0, 3, 0, [Card()], [], [], [], [], 0, [null, null], [], [], [], history ?? [], null);
    private static DecisionPacket Packet(PublicEvent[] history, int revision = 0) => new("player_decision", Observation(history), [new(revision, "end_turn")]);
    private static PublicEvent Start() => new("combat_started", "Silent:A10");

    [Fact]
    public void RecorderOwnsOrdinalsAndSupportsNestedOwnersWithoutPrivateIdentities()
    {
        var recorder = Recorder();
        long eventOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        long combatOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1, eventOwner);
        recorder.ObserveCombatDecision(combatOwner, Packet([Start()]));
        Assert.Throws<ArgumentException>(() => recorder.Record(eventOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed)));
        recorder.Record(combatOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory, Assets()));
        recorder.Record(eventOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        var snapshot = recorder.Capture();
        Assert.True(snapshot.CompleteFromRunStart);
        Assert.Equal(0, eventOwner); Assert.Equal(1, combatOwner);
        Assert.Equal(Enumerable.Range(0, snapshot.Events.Length).Select(x => (long)x), snapshot.Events.Select(e => e.EventOrdinal));
        Assert.Throws<ArgumentException>(() => recorder.Record(combatOwner, new PublicCombatFact(PublicCombatFactKind.Shuffled)));
        Assert.Equal(PublicRunEvidenceJson.Serialize(snapshot), PublicRunEvidenceJson.Serialize(recorder.Capture()));
    }

    [Fact]
    public void MissingRunOrOwnerStartAndExplicitGapsCannotClaimCompletePrefixes()
    {
        var absent = Recorder(false); Assert.False(absent.Capture().CompleteFromRunStart);
        Assert.IsType<PublicEvidenceGap>(absent.Capture().Events[0].Payload);
        var recorder = Recorder();
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 5, completeFromOwnerStart: false);
        long decision = recorder.ObserveCombatDecision(owner, Packet([Start()]));
        Assert.False(recorder.Capture().CompleteFromRunStart);
        Assert.False(Assert.IsType<PublicCombatDecision>(recorder.Capture().Events[(int)decision].Payload).HistoryCompleteFromCombatStart);
        Assert.Contains(recorder.Capture().Events, e => e.Payload is PublicEvidenceGap { Reason: PublicEvidenceGapReason.OwnerStartNotObserved });
        Assert.Throws<ArgumentException>(() => new PublicRunEvidence(PublicRunEvidence.Version, true, recorder.Capture().Events));
        Assert.Throws<ArgumentException>(() => new PublicRunEvidence("future", false, recorder.Capture().Events));
        Assert.Throws<ArgumentException>(() => new PublicRunEvidence(PublicRunEvidence.Version, false, []));
    }

    [Fact]
    public void PublicDtoMutationOnInputOrAccessCannotChangeAnySavedSnapshot()
    {
        var card = Card();
        var relic = new PublicRelic("Bottle", new Dictionary<string, int> { ["count"] = 1 }, [card]);
        var cards = new[] { card }; var relics = new[] { relic };
        var start = new PublicRunStarted("Silent", 10, new(70, 70, 99, cards, relics, [null], 3, 1, 0, 0));
        var recorder = new PublicRunEvidenceRecorder(start);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.OutsideChoice, 0, 1);
        var input = new PublicChoice("visible", 1, 1, true, cards, "canonical_unordered_reveal", [cards]);
        long offer = recorder.Record(owner, new PublicCardsObserved(input));
        var snapshot = recorder.Capture(); string before = PublicRunEvidenceJson.Serialize(snapshot);
        card.Keywords[0] = "mutated"; card.Details!.EnergyModifiers[0] = new("set", 999);
        ((Dictionary<string, string>)card.PublicState!)["counter"] = "99";
        ((Dictionary<string, int>)relic.Details)["count"] = 999; cards[0] = Card("Other"); relics[0] = new("Other", new Dictionary<string, int>());
        start.Assets.Deck[0].Keywords[0] = "mutated_getter";
        var exported = Assert.IsType<PublicCardsObserved>(snapshot.Events[(int)offer].Payload).Choice;
        exported.Candidates[0].Enchantments![0] = new("Changed", 999);
        exported.Bundles![0][0].Keywords[0] = "changed_bundle";
        Assert.Equal(before, PublicRunEvidenceJson.Serialize(snapshot));
        recorder.Record(owner, new PublicCardsChosen(offer, [], true));
        Assert.Equal(before, PublicRunEvidenceJson.Serialize(snapshot));
    }

    [Fact]
    public void ImmutableArrayInputsAreCopiedEvenWhenBackedByBorrowedMutableArrays()
    {
        var options = new[] { new PublicVisibleOption("LEAVE", false) };
        var observed = new PublicOptionsObserved(ImmutableCollectionsMarshal.AsImmutableArray(options));
        options[0] = new("LOCKED", true);
        Assert.Equal("LEAVE", observed.Options[0].Key);
    }

    [Fact]
    public void AllDisplayedRewardTypesExtraAlternativesAndRerollsPrecedeChoices()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, 2);
        var groups = ImmutableArray.Create(
            new PublicOfferGroup(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.ChooseOne,
                [new("card0", PublicOfferKind.Card, card: Card()), new("card1", PublicOfferKind.Card, card: Card("Defend"))]),
            new PublicOfferGroup(PublicOfferGroupKind.Extra, PublicOfferSelectionMode.Independent,
                [new("gold", PublicOfferKind.Gold, gold: 15), new("potion", PublicOfferKind.Potion, potion: "DexterityPotion")]),
            new PublicOfferGroup(PublicOfferGroupKind.Alternative, PublicOfferSelectionMode.ChooseOne,
                [new("relic", PublicOfferKind.Relic, relic: new("Anchor", new Dictionary<string, int>()))], 0));
        long offer = recorder.Record(owner, new PublicOffersObserved(groups));
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicOptionChosen(offer + 1, "card0")));
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicOptionChosen(offer, "missing")));
        recorder.Record(owner, new PublicOptionChosen(offer, "card1"));
        long reroll = recorder.Record(owner, new PublicOffersObserved([
            new(PublicOfferGroupKind.Reroll, PublicOfferSelectionMode.ChooseOne, [new("next", PublicOfferKind.Card, card: Card("Blur"))])], offer));
        recorder.Record(owner, new PublicOptionChosen(reroll, "next"));
        var value = Assert.IsType<PublicOffersObserved>(recorder.Capture().Events[(int)offer].Payload);
        Assert.Equal(5, value.Groups.Sum(g => g.Offers.Length));
        Assert.Equal("Strike", value.Groups[0].Offers[0].Card!.Id);
        Assert.Throws<ArgumentException>(() => new PublicOffer("bad", PublicOfferKind.Potion, card: Card(), potion: "GhostInAJar"));
        Assert.Throws<ArgumentException>(() => new PublicOffersObserved([new(PublicOfferGroupKind.Alternative,
            PublicOfferSelectionMode.ChooseOne, [], 1)]));
    }

    [Fact]
    public void LockedEventAndRestOptionsPricesAndShopInventoryRemainVisible()
    {
        var recorder = Recorder(); long eventOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 2);
        long offer = recorder.Record(eventOwner, new PublicOptionsObserved([new("PAY", true, 80), new("LEAVE", false)]));
        Assert.Throws<ArgumentException>(() => recorder.Record(eventOwner, new PublicOptionChosen(offer, "PAY")));
        recorder.Record(eventOwner, new PublicOptionChosen(offer, "LEAVE"));
        long rest = recorder.BeginOwner(PublicEvidenceOwnerKind.Rest, 0, 3);
        long restOffer = recorder.Record(rest, new PublicOptionsObserved([new("REST", false), new("SMITH", true)]));
        recorder.Record(rest, new PublicOptionChosen(restOffer, "REST"));
        long shop = recorder.BeginOwner(PublicEvidenceOwnerKind.Shop, 0, 4);
        long shopOffer = recorder.Record(shop, new PublicOffersObserved([new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.Independent,
            [new("card", PublicOfferKind.Card, price: 45, card: Card()), new("remove", PublicOfferKind.Service, price: 75, serviceKey: "REMOVE_CARD"),
             new("leave", PublicOfferKind.Continue)])]));
        recorder.Record(shop, new PublicOptionChosen(shopOffer, "leave"));
        var json = PublicRunEvidenceJson.Serialize(recorder.Capture());
        Assert.Contains("\"price\":80", json); Assert.Contains("\"isLocked\":true", json); Assert.Contains("\"price\":75", json);
    }

    [Fact]
    public void OutsideCardChoicesPreserveCandidateOrderBundlesAndCancellation()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.OutsideChoice, 0, 2);
        var choice = new PublicChoice("RemoveCard", 1, 2, true, [Card(), Card("Defend")], "canonical_unordered_reveal",
            [[Card(), Card("Blur")], [Card("Defend")]]);
        long offer = recorder.Record(owner, new PublicCardsObserved(choice));
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicCardsChosen(offer, [2], false)));
        recorder.Record(owner, new PublicCardsChosen(offer, [], true));
        var captured = Assert.IsType<PublicCardsObserved>(recorder.Capture().Events[(int)offer].Payload).Choice;
        Assert.Equal("canonical_unordered_reveal", captured.CandidateOrder); Assert.Equal(2, captured.Bundles![0].Length);
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicCardsChosen(offer, [0], false)));
        Assert.Throws<ArgumentException>(() => new PublicCardsChosen(offer, [0, 0], false));
        Assert.Throws<ArgumentException>(() => new PublicCardsObserved(choice with { Bundles = [[]] }));
    }

    [Fact]
    public void MapSlicePreservesUnknownIconsAndOrdinaryVersusAdditionalOptions()
    {
        var current = new PublicMapCoordinate(1, 2); var ordinary = new PublicMapCoordinate(1, 3); var extra = new PublicMapCoordinate(2, 3);
        var nodes = ImmutableArray.Create(new PublicMapNode(current, PublicMapNodeType.Monster),
            new PublicMapNode(ordinary, PublicMapNodeType.Unknown), new PublicMapNode(extra, PublicMapNodeType.Rest));
        var map = new PublicMapObserved(current, nodes, [new(current, ordinary)], [new(ordinary, true), new(extra, false)]);
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, 3);
        long offer = recorder.Record(owner, map); recorder.Record(owner, new PublicMapChosen(offer, extra));
        Assert.Equal(PublicMapNodeType.Unknown, map.Nodes[1].NodeType);
        Assert.Throws<ArgumentException>(() => new PublicMapObserved(current, nodes, [new(current, ordinary)], [new(extra, true)]));
        Assert.Throws<ArgumentException>(() => new PublicMapObserved(current, nodes, [new(current, new(99, 99))], []));
        Assert.DoesNotContain("encounter", PublicRunEvidenceJson.Serialize(recorder.Capture()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CumulativeCombatHistoriesLinkSnapshotsAndActionsWithoutDuplicatingEvents()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        var first = Packet([Start(), new("draw", PublicJson.Serialize(Card()))]);
        long decision = recorder.ObserveCombatDecision(owner, first);
        var stable = Assert.IsType<PublicCombatDecision>(recorder.Capture().Events[(int)decision].Payload);
        Assert.Empty(stable.Observation.History); Assert.True(stable.HistoryCompleteFromCombatStart);
        Assert.Equal(decision - 1, stable.HistoryThroughEventOrdinal);
        recorder.Record(owner, new PublicCombatActionTaken(decision, first.Actions[0]));
        var history = first.Observation!.History.Concat([new PublicEvent("action", PublicJson.Serialize(first.Actions[0])),
            new PublicEvent("shuffle", "known_positions_reset")]).ToArray();
        recorder.ObserveCombatDecision(owner, Packet(history, 1));
        recorder.ObserveCombatHistory(owner, history);
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory, Assets()));
        var snapshot = recorder.Capture();
        Assert.True(snapshot.CompleteFromRunStart);
        Assert.Equal(1, snapshot.Events.Count(e => e.Payload is PublicCombatActionTaken));
        Assert.Equal(3, snapshot.Events.Count(e => e.Payload is PublicCombatFact));
        Assert.Equal(PublicRunEvidenceJson.Serialize(snapshot), PublicRunEvidenceJson.Serialize(PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(snapshot))));
        first.Observation.Hand[0].Keywords[0] = "mutation"; stable.Observation.Hand[0].Keywords[0] = "mutation";
        first.Actions[0] = new(1, "play", 99);
        Assert.Equal("Strike", stable.Observation.Hand[0].Keywords[0]); Assert.Equal("end_turn", stable.Actions[0].Kind);
    }

    [Theory]
    [InlineData("unknown_future_event", "{\"actualSeed\":\"DO_NOT_EXPORT\"}")]
    [InlineData("draw", "{\"actualSeed\":\"DO_NOT_EXPORT\"}")]
    [InlineData("intent_published", "{\"slot\":0,\"id\":\"Enemy\",\"intents\":null}")]
    [InlineData("player_turn", "not_a_number")]
    public void UnknownOrMalformedHistoryCreatesGapAndCannotLeakRawPayload(string kind, string detail)
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        long ordinal = recorder.ObserveCombatDecision(owner, Packet([Start(), new(kind, detail)]));
        var evidence = recorder.Capture();
        Assert.False(evidence.CompleteFromRunStart);
        Assert.False(Assert.IsType<PublicCombatDecision>(evidence.Events[(int)ordinal].Payload).HistoryCompleteFromCombatStart);
        Assert.Contains(evidence.Events, e => e.Payload is PublicEvidenceGap { Reason: PublicEvidenceGapReason.UnsupportedObservation });
        string json = PublicRunEvidenceJson.Serialize(evidence);
        Assert.DoesNotContain("DO_NOT_EXPORT", json); Assert.DoesNotContain("actualSeed", json);
    }

    [Fact]
    public void TruncatedOrRewrittenCumulativeHistoryCannotReplaceTheObservedPrefix()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.ObserveCombatDecision(owner, Packet([Start(), new("player_turn", "1")]));
        recorder.ObserveCombatHistory(owner, [Start()]);
        recorder.ObserveCombatHistory(owner, [Start(), new("player_turn", "2")]);
        var evidence = recorder.Capture(); Assert.False(evidence.CompleteFromRunStart);
        Assert.Single(evidence.Events.Where(e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.PlayerTurnStarted }));
        Assert.Equal(2, evidence.Events.Count(e => e.Payload is PublicEvidenceGap { Reason: PublicEvidenceGapReason.Interrupted }));
    }

    [Fact]
    public void DecisionMustReferenceTheWholePrecedingHistoryAndCannotAcceptUntypedDetails()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.Started));
        var observation = Observation(); var actions = new[] { new PublicAction(0, "end_turn") };
        Assert.Throws<ArgumentException>(() => new PublicCombatDecision("player_decision", Observation([Start()]), actions, 2, true));
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicCombatDecision("player_decision", observation, actions, 1, true)));
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicCombatDecision("player_decision", observation, actions, 2, false)));
        recorder.Record(owner, new PublicCombatDecision("player_decision", observation, actions, 2, true));
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicCombatActionTaken(3, new(0, "play", 0))));
    }

    [Fact]
    public void LegacyPublicBytesAreUnchangedAndOptionalEvidenceHasIndependentCodec()
    {
        const string expected = """{"schema":"nosl.public.v2","startHp":70,"ascension":10,"turn":1,"hp":70,"maxHp":70,"block":0,"energy":3,"stars":0,"hand":[],"discard":[],"exhaust":[],"unknownDraw":[],"knownDraw":[],"drawCount":0,"potions":[null,null],"relics":[],"powers":[],"enemies":[],"history":[],"choice":null,"counters":null,"relicStates":null,"gold":0,"startGold":0,"orbCapacity":0,"orbs":null,"pets":null,"unidentifiedDrawCount":0}""";
        Assert.Equal(expected, PublicJson.Serialize(Observation() with { Hand = [] }));
        Assert.DoesNotContain("evidence", expected);
        var evidence = Recorder().Capture(); string json = PublicRunEvidenceJson.Serialize(evidence);
        Assert.Equal(json, PublicRunEvidenceJson.Serialize(PublicRunEvidenceJson.Read(json)));
        Assert.Throws<JsonException>(() => PublicRunEvidenceJson.Read(json.Replace("\"kind\":\"run_started\"", "\"kind\":\"run_started\",\"actualSeed\":\"x\"")));
        Assert.Throws<JsonException>(() => PublicRunEvidenceJson.Read(json.Replace("run_started", "future_kind")));
        Assert.Throws<ArgumentException>(() => PublicRunEvidenceJson.Read(json.Replace("\"eventOrdinal\":0", "\"eventOrdinal\":1")));
        Assert.Throws<ArgumentException>(() => PublicRunEvidenceJson.Read(json.Replace("\"completeFromRunStart\":true", "\"completeFromRunStart\":false")));
    }

    [Fact]
    public void TypedPayloadsRejectContradictoryShapesAndHiddenDrawIdentities()
    {
        Assert.Throws<ArgumentException>(() => new PublicCombatFact(PublicCombatFactKind.Shuffled, cards: [Card()]));
        Assert.Throws<ArgumentException>(() => new PublicCombatFact(PublicCombatFactKind.Damage));
        Assert.Throws<ArgumentException>(() => new PublicCombatFact((PublicCombatFactKind)999));
        Assert.Throws<ArgumentException>(() => new PublicOffer("invalid", (PublicOfferKind)999));
        Assert.Throws<ArgumentException>(() => new PublicCombatDecision("player_decision", Observation() with { UnidentifiedDrawCount = 1 }, [new(0, "end_turn")], 0, false));
        var hidden = Observation() with { UnidentifiedDrawCount = 1, DrawCount = 1 };
        var valid = new PublicCombatDecision("player_decision", hidden, [new(0, "end_turn")], 0, false);
        Assert.Empty(valid.Observation.UnknownDraw); Assert.Equal(1, valid.Observation.UnidentifiedDrawCount);
    }
    [Fact]
    public void ExistingSupportedPublicHistoryKindsProjectWithoutDroppingObservedPayloads()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        PublicEvent Event(string kind, object payload) => new(kind, PublicJson.Serialize(payload));
        var assets = Assets();
        var history = new[] {
            Start(), Event("native_entry_assets", new { schemaVersion = "nosl.native-entry-assets.v1", assets.Hp, assets.MaxHp,
                assets.Gold, assets.Deck, assets.Relics, assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed }),
            new("player_turn", "1"), new("player_turn_ended", ""),
            Event("intent_published", new { slot = 0, id = "Louse", intents = new[] { new PublicIntent("Attack", 6, 1) } }),
            Event("draw", Card()), Event("card_started", Card()), Event("card_generated", Card()),
            Event("card_played", new { card = Card(), energySpent = 1, starsSpent = 0, resultPile = "Discard" }),
            new("hidden_card_generated", "draw"), new("potion_used", "DexterityPotion"),
            Event("damage", new { target = "Louse", targetSlot = 0, sourceSlot = -2, blocked = 0m, unblocked = 6m, overkill = 0m, hpAfter = 4, killed = false }),
            Event("power_changed", new { target = "player", targetSlot = -2, sourceSlot = (int?)null, id = "Dexterity", amount = 2m }),
            new("shuffle", "known_positions_reset"),
            Event("choice", new PublicChoice("Choose", 0, 1, true, [Card()], "canonical_unordered_reveal", [[Card("Blur")]])),
            Event("automatic_selection", new { source = "Choose", cards = new[] { Card() }, unidentifiedCount = 1 }),
            Event("pre_settlement", new { hp = 70, maxHp = 70, hand = new[] { Card() }, exhaust = Array.Empty<PublicCard>() }) };
        recorder.ObserveCombatHistory(owner, history);
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory, assets));
        var evidence = recorder.Capture();
        Assert.True(evidence.CompleteFromRunStart);
        Assert.Equal(history.Length, evidence.Events.Count(e => e.Payload is PublicCombatFact));
        var damage = Assert.Single(evidence.Events.Select(e => e.Payload).OfType<PublicCombatFact>(), f => f.FactKind == PublicCombatFactKind.Damage);
        Assert.Equal("Louse", damage.TargetModel); Assert.Equal(6, damage.Damage!.Unblocked);
        var selection = Assert.Single(evidence.Events.Select(e => e.Payload).OfType<PublicCombatFact>(), f => f.FactKind == PublicCombatFactKind.AutomaticSelection);
        Assert.Equal(1, selection.UnidentifiedCount);
        Assert.Equal(PublicRunEvidenceJson.Serialize(evidence), PublicRunEvidenceJson.Serialize(PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(evidence))));
    }

    [Fact]
    public void GlobalGapInvalidatesOpenCombatHistoryAndCompletedEmptyCombatIsRejected()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        Assert.Throws<ArgumentException>(() => recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory)));
        recorder.ObserveCombatHistory(owner, [Start()]);
        recorder.RecordGap(null, PublicEvidenceGapReason.ObservationMissing);
        long decision = recorder.ObserveCombatDecision(owner, Packet([Start()]));
        Assert.False(Assert.IsType<PublicCombatDecision>(recorder.Capture().Events[(int)decision].Payload).HistoryCompleteFromCombatStart);
    }

    [Fact]
    public void DuplicateJsonPropertiesAndCaseAliasesAreRejectedAtEveryDepth()
    {
        var json = PublicRunEvidenceJson.Serialize(Recorder().Capture());
        Assert.Throws<JsonException>(() => PublicRunEvidenceJson.Read(json.Replace("\"ascension\":10", "\"ascension\":10,\"ascension\":10")));
        Assert.Throws<JsonException>(() => PublicRunEvidenceJson.Read(json.Replace("\"id\":\"Strike\"", "\"id\":\"Strike\",\"id\":\"Other\"")));
        foreach (string card in new[] {
            PublicJson.Serialize(Card()).Replace("\"id\":\"Strike\"", "\"id\":\"Strike\",\"Id\":\"Other\""),
            PublicJson.Serialize(Card()).Replace("\"counter\":\"0\"", "\"counter\":\"0\",\"counter\":\"99\"") })
        {
            var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
            recorder.ObserveCombatHistory(owner, [Start(), new("draw", card)]);
            Assert.False(recorder.Capture().CompleteFromRunStart);
            Assert.Contains(recorder.Capture().Events, e => e.Payload is PublicEvidenceGap);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualActionEchoIsSingleUseAndMustBeTheImmediateNextHistoryEvent(bool reordered)
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        var packet = Packet([Start()]);
        long decision = recorder.ObserveCombatDecision(owner, packet);
        recorder.Record(owner, new PublicCombatActionTaken(decision, packet.Actions[0]));
        var action = new PublicEvent("action", PublicJson.Serialize(packet.Actions[0]));
        recorder.ObserveCombatHistory(owner, reordered ? [Start(), new("shuffle", "known_positions_reset"), action]
            : [Start(), action, action]);
        Assert.False(recorder.Capture().CompleteFromRunStart);
        Assert.Single(recorder.Capture().Events.Where(e => e.Payload is PublicCombatActionTaken));
        Assert.Contains(recorder.Capture().Events, e => e.Payload is PublicEvidenceGap);
    }

    [Fact]
    public void NullHistoryDetailAndDirectMissingStartDecisionCannotClaimCompleteness()
    {
        var recorder = Recorder(); long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        Assert.Throws<ArgumentException>(() => recorder.Record(owner,
            new PublicCombatDecision("player_decision", Observation(), [new(0, "end_turn")], 1, false)));
        var invalid = new PublicRunEvidenceEvent(2, owner,
            new PublicCombatDecision("player_decision", Observation(), [new(0, "end_turn")], 1, false));
        var json = PublicRunEvidenceJson.Serialize(recorder.Capture());
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        malformed["events"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(PublicJson.Serialize(invalid)));
        Assert.Throws<ArgumentException>(() => PublicRunEvidenceJson.Read(malformed.ToJsonString()));
        // Direct typed construction and the strict decoder use the same boundary validation.
        Assert.Throws<ArgumentException>(() => new PublicRunEvidence(PublicRunEvidence.Version, true,
            recorder.Capture().Events.Add(invalid)));
        recorder.ObserveCombatHistory(owner, [Start(), new("player_turn_ended", null!)]);
        Assert.False(recorder.Capture().CompleteFromRunStart);
        Assert.Contains(recorder.Capture().Events, e => e.Payload is PublicEvidenceGap { Reason: PublicEvidenceGapReason.UnsupportedObservation });
    }

}
