using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class PublicDecisionSnapshotTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void DetachedPacketsRetainExactWireBytesAndIsolateEveryMutableDto(int version, bool choice)
    {
        var fixture = Fixture(version, choice);
        var packet = fixture.Packet;
        string original = PublicJson.Serialize(packet);
        var reference = PublicJson.Read<DecisionPacket>(original);
        var copy = PublicDecisionSnapshot.Copy(packet);
        var sibling = PublicDecisionSnapshot.Copy(packet);
        Assert.Equal(PublicJson.Serialize(reference), PublicJson.Serialize(copy));
        Assert.Equal(original, PublicJson.Serialize(copy));
        Assert.Same(packet.PublicEvidence, copy.PublicEvidence);
        Assert.NotSame(packet.Observation, copy.Observation);
        Assert.NotSame(packet.Actions, copy.Actions);
        CorruptMutableExports(copy);
        Assert.Equal(original, PublicJson.Serialize(packet));
        Assert.Equal(original, PublicJson.Serialize(sibling));
        if (packet.PublicEvidence is { } evidence)
        {
            string before = PublicRunEvidenceJson.Serialize(evidence);
            fixture.CorruptBorrowedInputs();
            Assert.Equal(before, PublicRunEvidenceJson.Serialize(evidence));
            var appended = evidence.Append(new(evidence.Events.Length, null,
                new PublicEvidenceGap(PublicEvidenceGapReason.ObservationMissing)));
            Assert.Equal(evidence.Events.Length + 1, appended.Events.Length);
            Assert.Equal(before, PublicRunEvidenceJson.Serialize(evidence));
            Assert.Equal(original, PublicJson.Serialize(sibling));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TerminalPacketsKeepEvidenceAndExactBytes(int version)
    {
        var packet = Fixture(version, false).Packet with { Status = "terminal_settled", Observation = null, Actions = [] };
        var copy = PublicDecisionSnapshot.Copy(packet);
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(copy));
        Assert.Same(packet.PublicEvidence, copy.PublicEvidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedNativeReplayKeepsSavedEvidenceAndIndependentContinuation(bool completeMap)
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = completeMap ? NativeTapePrior.MapVersion : NativeTapePrior.RewardsVersion,
            EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version,
                PublicEvidenceProfile: completeMap ? PublicRunEvidence.CompleteMapVersion : PublicRunEvidence.Version,
                PublicMapObservationProfile: completeMap ? PublicMapObservationProfiles.CompleteGraphV1 : null),
        };
        // An inspected same-recipe lifecycle fixture, never fresh source data or
        // a posterior estimate. The original native run and fork remain distinct.
        var recipe = prior.Draw(new Rng(24002, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 0, DecisionIndex = 0 };
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        var saved = world.Observe(); string before = PublicJson.Serialize(saved);
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        Assert.NotSame(world.NativeRun, fork.NativeRun);
        Assert.NotSame(world.NativeCombatRoom, fork.NativeCombatRoom);
        Assert.NotSame(world.NativeRun.Players.Single(), fork.NativeRun.Players.Single());
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            var packet = world.Observe();
            string wire = PublicJson.Serialize(packet);
            Assert.Equal(wire, PublicJson.Serialize(fork.Observe()));
            CorruptMutableExports(world.Observe());
            Assert.Equal(wire, PublicJson.Serialize(world.Observe()));
            var action = policy.Choose(packet);
            var result = await fork.StepAsync(action);
            CorruptMutableExports(result);
            Assert.Equal(wire, PublicJson.Serialize(world.Observe()));
            await world.StepAsync(action);
            Assert.Equal(before, PublicJson.Serialize(saved));
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(before, PublicJson.Serialize(saved));
    }

    [Fact]
    public void SharedEvidenceGraphHasOnlyImmutablePropertiesOrReviewedDefensiveGetters()
    {
        // A new payload/property must pass this review boundary before it can be
        // shared. Mutable legacy DTO roots below are adversarially exercised by
        // CorruptMutableExports; all their nested arrays/dictionaries are visited.
        var defensive = new HashSet<(Type, string)>
        {
            (typeof(PublicEvidenceAssets), "Deck"), (typeof(PublicEvidenceAssets), "Relics"),
            (typeof(PublicOffer), "Card"), (typeof(PublicOffer), "Relic"),
            (typeof(PublicCardsObserved), "Choice"),
            (typeof(PublicCombatFact), "Cards"), (typeof(PublicCombatFact), "Choice"),
            (typeof(PublicPreSettlementFact), "Hand"), (typeof(PublicPreSettlementFact), "Exhaust"),
            (typeof(PublicCombatDecision), "Observation"), (typeof(PublicCombatDecision), "Actions"),
            (typeof(PublicCombatActionTaken), "Action"),
        };
        var checkedTypes = new HashSet<Type>();
        void Check(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            { Check(type.GetGenericArguments()[0]); return; }
            if (!checkedTypes.Add(type)) return;
            if (type == typeof(PublicEvidencePayload))
            {
                foreach (var payload in type.GetCustomAttributes<JsonDerivedTypeAttribute>()) Check(payload.DerivedType);
                return;
            }
            Assert.True(type.IsSealed, $"Unreviewed extensible evidence type: {type}");
            Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(property.SetMethod is null || property.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(IsExternalInit)), $"Mutable evidence property: {type.Name}.{property.Name}");
                if (!defensive.Contains((type, property.Name))) Check(property.PropertyType);
            }
        }
        Check(typeof(PublicRunEvidence));
        var fixtureTypes = Fixture(2, true).Packet.PublicEvidence!.Events.Select(e => e.Payload.GetType()).ToHashSet();
        Assert.True(fixtureTypes.SetEquals(typeof(PublicEvidencePayload).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.DerivedType)), "The mutation fixture must exercise every declared evidence payload");
    }

    // Deliberately use public getters only. ImmutableArray is traversed, never
    // unwrapped with unsafe APIs; ordinary mutable arrays/dictionaries are changed.
    private static void CorruptMutableExports(object? value)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object? current)
        {
            if (current is null || current is string || current.GetType().IsPrimitive
                || current.GetType().IsEnum || current is decimal || !visited.Add(current)) return;
            if (current is Array array)
            {
                foreach (var item in array) Visit(item);
                var element = array.GetType().GetElementType()!;
                for (int i = 0; i < array.Length; i++) array.SetValue(element.IsValueType ? Activator.CreateInstance(element) : null, i);
            }
            else if (current is IDictionary dictionary)
            {
                foreach (var item in dictionary.Values) Visit(item);
                dictionary.Clear();
            }
            else if (current is IEnumerable sequence)
            { foreach (var item in sequence) Visit(item); }
            else
                foreach (var property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0)) Visit(property.GetValue(current));
        }
        Visit(value);
    }

    private sealed record SnapshotFixture(DecisionPacket Packet, Action CorruptBorrowedInputs);

    private static SnapshotFixture Fixture(int version, bool choice)
    {
        var card = new PublicCard("Strike", 0, 1, 0, "Attack", ["Strike"],
            new(false, false, 1, 0, false, false, 0, false, false, false, false, null, [new("set", 1)]),
            [new("Test", 1)], new("Affliction", 1), new Dictionary<string, string> { ["counter"] = "0" });
        var relic = new PublicRelic("Bottle", new Dictionary<string, int> { ["count"] = 1 }, [card]);
        var cards = new[] { card };
        var potions = new string?[] { "Potion" };
        var assets = new PublicEvidenceAssets(70, 70, 99, cards, [relic],
            ImmutableCollectionsMarshal.AsImmutableArray(potions), 3, 1, 0, 0);
        var candidates = new PublicChoice("visible", 1, 1, true, cards, "canonical_unordered_reveal", [cards]);
        var observation = new PublicObservation("nosl.public.v3", 70, 10, 1, 70, 70, 0, 3, 0,
            cards, cards, cards, [new(card, 1)], [new(0, card)], 2, potions, ["Bottle"], [new("Strength", 1)],
            [new(0, "Enemy", 10, 10, 0, [new("Weak", 1)], [new("Attack", 5, 1)])], [], choice ? candidates : null,
            RelicStates: [relic], Orbs: [new("Lightning", 1, 1)], Pets: [new(1, "Pet", 1, 1, 0, [new("Strength", 1)])],
            RunContext: new(PublicRunContext.Version, 0, 1, 0, true));
        PublicAction[] actions = choice ? [new(0, "choose", Selection: [0])] : [new(0, "play", 0, 0), new(0, "end_turn")];
        string status = choice ? "card_choice" : "player_decision";
        if (version == 0) return new(new(status, observation, actions), () => { });
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets), null,
            version == 1 ? PublicRunEvidence.Version : PublicRunEvidence.CompleteMapVersion);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        var offers = new PublicOffer[] { new("card", PublicOfferKind.Card, card: card), new("relic", PublicOfferKind.Relic, relic: relic) };
        var groups = new PublicOfferGroup[] { new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.ChooseOne,
            ImmutableCollectionsMarshal.AsImmutableArray(offers)) };
        long offered = recorder.Record(owner, new PublicOffersObserved(ImmutableCollectionsMarshal.AsImmutableArray(groups)));
        recorder.Record(owner, new PublicOptionChosen(offered, "card"));
        var options = new PublicVisibleOption[] { new("continue", false) };
        offered = recorder.Record(owner, new PublicOptionsObserved(ImmutableCollectionsMarshal.AsImmutableArray(options)));
        recorder.Record(owner, new PublicOptionChosen(offered, "continue"));
        offered = recorder.Record(owner, new PublicCardsObserved(candidates));
        var selection = new[] { 0 };
        recorder.Record(owner, new PublicCardsChosen(offered, ImmutableCollectionsMarshal.AsImmutableArray(selection), false));
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, assets));
        owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, 1);
        var start = new PublicMapCoordinate(0, 0); var boss = new PublicMapCoordinate(0, 1);
        var nodes = new PublicMapNode[] { new(start, PublicMapNodeType.Start), new(boss, PublicMapNodeType.Boss) };
        var edges = new PublicMapEdge[] { new(start, boss) };
        var mapOptions = new PublicMapOption[] { new(boss, true) }; var bosses = new[] { boss };
        var map = version == 1 ? null : new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            ImmutableCollectionsMarshal.AsImmutableArray(nodes), ImmutableCollectionsMarshal.AsImmutableArray(edges),
            start, ImmutableCollectionsMarshal.AsImmutableArray(bosses));
        offered = recorder.Record(owner, new PublicMapObserved(start,
            ImmutableCollectionsMarshal.AsImmutableArray(nodes), ImmutableCollectionsMarshal.AsImmutableArray(edges),
            ImmutableCollectionsMarshal.AsImmutableArray(mapOptions), map));
        recorder.Record(owner, new PublicMapChosen(offered, boss));
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.EntryAssets, assets: assets));
        var intents = new PublicIntent[] { new("Attack", 5, 1) };
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0,
            model: "Enemy", intents: ImmutableCollectionsMarshal.AsImmutableArray(intents)));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.CardPlayed, cards: cards,
            energySpent: 1, starsSpent: 0, resultPile: PublicCardPile.Discard));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.ChoiceOffered, choice: candidates));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.Damage, targetSlot: 0,
            targetModel: "Enemy", damage: new(0, 1, 0, 9, false)));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.PreSettlement,
            settlement: new(70, 70, cards, cards)));
        long decision = recorder.Record(owner, new PublicCombatDecision(status, observation, actions,
            recorder.Capture().Events[^1].EventOrdinal, true));
        recorder.Record(owner, new PublicCombatActionTaken(decision, actions[0]));
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory, assets));
        recorder.RecordGap(null, PublicEvidenceGapReason.ObservationMissing);
        var events = recorder.Capture().Events.ToArray();
        var evidence = new PublicRunEvidence(recorder.Capture().SchemaVersion, false,
            ImmutableCollectionsMarshal.AsImmutableArray(events));
        return new(new(status, observation, actions, evidence), () =>
        {
            foreach (var value in new object[] { cards, relic, potions, candidates, observation, actions,
                offers, groups, options, selection, nodes, edges, mapOptions, bosses, intents, events })
                CorruptMutableExports(value);
        });
    }
}
