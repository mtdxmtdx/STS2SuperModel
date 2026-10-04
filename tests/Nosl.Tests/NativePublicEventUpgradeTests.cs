using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicEventUpgradeTests
{
    private const string MixedDeck = "Acrobatics,Acrobatics+,Blur,DeadlyPoison,StrikeSilent,AscendersBane";
    private static NativeTapePrior Prior => NativePublicBrainLeechCardTests.Prior;

    [Fact]
    public void DoorsIdentityRequiresTheWholeOrderedUnlockedUnpricedInitialPage()
    {
        ImmutableArray<PublicVisibleOption> options = [new("LIGHT", false), new("DARK", false)];
        Assert.Equal(nameof(DoorsOfLightAndDark), NativeEventSelectionCertificate.Identify(new(options)));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.Reverse().ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.RemoveAt(1))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(0, new("LIGHT", true)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(1, new("DARK", false, 1)))));
    }

    [Theory]
    [InlineData("StrikeSilent,StrikeSilent", 2, 1)]
    [InlineData("StrikeSilent,StrikeSilent,StrikeSilent+,DefendSilent+", 2, 1)]
    [InlineData("DefendSilent,DefendSilent,StrikeSilent,AscendersBane", 3, 1)]
    [InlineData("DefendSilent,DefendSilent,StrikeSilent,StrikeSilent,Neutralize+,Acrobatics+", 4, 1)]
    [InlineData(MixedDeck, 4, 2)]
    [InlineData("Acrobatics,Blur,DeadlyPoison,Neutralize,Survivor,StrikeSilent+", 5, 2)]
    [InlineData("DeadlyPoison,Neutralize,Survivor,StrikeSilent,StrikeSilent+,DefendSilent+,AscendersBane", 4, 2)]
    public async Task DetachedUnorderedPairConditionsTheCompleteNativeShuffleAndPreservesEveryAsset(
        string deck, int eligibleCount, int distinctSelected)
    {
        var source = await Source(deck);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Root));
        string detached = PublicJson.Serialize(root);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var target = condition!.Target;
        Assert.Equal(eligibleCount, target.PoolKeys.Count);
        Assert.Equal(2, target.SelectedKeys.Count);
        Assert.Equal(distinctSelected, target.SelectedKeys.Distinct().Count());
        Assert.All(target.PoolKeys, key => Assert.Equal(0, PublicJson.Read<PublicCard>(key).Upgrade));
        Assert.Equal(ConditionalUnorderedPairShuffleProposal.Envelope(target.PoolKeys, target.SelectedKeys.Reverse().ToArray()), condition.Envelope);
        Assert.Equal(PublicJson.Serialize(source.Before), PublicJson.Serialize(target.BeforeAssets));
        Assert.Equal(PublicJson.Serialize(NativePublicRunEvidence.Assets(source.Run.Players[0])), PublicJson.Serialize(target.AfterAssets));

        // Source order and physical duplicate identity are absent from the detached condition.
        // Different independent proposal streams must all replay the same full public settlement.
        foreach (ulong seed in new ulong[] { 73, 918273 })
        {
            var tape = new NativeLabelTape(new(91, 82, seed, 0, 0));
            var proposal = Proposal(condition, tape, seed);
            var actual = await Boundary(deck, proposal.ObservePublicEvidence);
            proposal.AttachHypotheticalRun(actual.Run);
            Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            int counter = actual.Doors.Rng.Counter;
            using (LabelRandomScope.Enter(Word(tape), proposal.BeginShuffle)) await Light(actual);
            proposal.ValidateCompletion();
            Assert.Equal(2, proposal.ConditionedUpgradeCount);
            Assert.Equal(1, proposal.ConditionedShuffleCount);
            Assert.Equal(eligibleCount - 1, actual.Doors.Rng.Counter - counter);
            Assert.Equal(eligibleCount - 1, tape.ConditionedCells);
            Assert.Equal(condition.Envelope, proposal.Envelope);
            Assert.True(proposal.NativeToProposalRatio.Numerator > 0);
            Assert.True(proposal.NativeToProposalRatio.Numerator * condition.Envelope.Denominator
                <= condition.Envelope.Numerator * proposal.NativeToProposalRatio.Denominator);
            Assert.True(proposal.AcceptCorrection(() => ulong.MaxValue));
            Assert.Equal(detached, PublicJson.Serialize(actual.Root));
            Assert.Equal(detached, PublicJson.Serialize(root));
            Assert.Equal(PublicJson.Serialize(target.AfterAssets), PublicJson.Serialize(NativePublicRunEvidence.Assets(actual.Run.Players[0])));
        }
    }

    [Theory]
    [InlineData("dark", "doors_immediate_light_settlement_required")]
    [InlineData("missing_settlement", "doors_complete_public_boundary_required")]
    [InlineData("missing_after_assets", "doors_immediate_light_settlement_required")]
    [InlineData("missing_before_assets", "doors_immediate_public_before_assets_required")]
    [InlineData("gap", "event_upgrades_require_complete_public_evidence")]
    [InlineData("repeat", "one_public_doors_visit_required")]
    [InlineData("listener", "doors_inventory_hook_closure_not_certified")]
    [InlineData("unsupported_card", "doors_card_upgrade_not_certified")]
    [InlineData("enchanted", "doors_card_upgrade_not_certified")]
    [InlineData("afflicted", "doors_card_upgrade_not_certified")]
    [InlineData("changed_card_metadata", "doors_card_upgrade_not_certified")]
    [InlineData("one_upgrade", "doors_exact_two_upgrade_delta_required")]
    public async Task MissingOrUncertifiedPublicBoundariesFailClosed(string change, string expected)
    {
        var source = await Source(MixedDeck);
        var events = source.Root.PublicEvidence!.Events;
        int page = events.IndexOf(events.Single(e => e.Payload is PublicOptionsObserved));
        int beforeAt = page - 2, end = page + 2;
        if (change == "missing_settlement") events = events.Take(end).ToImmutableArray();
        else if (change == "dark") events = Replace(events, page + 1, new PublicOptionChosen(page, "DARK"));
        else if (change == "missing_before_assets") events = Replace(events, beforeAt, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        else if (change == "missing_after_assets") events = Replace(events, end, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        else if (change == "gap") events = events.Add(new(events.Length, null, new PublicEvidenceGap(PublicEvidenceGapReason.ObservationMissing)));
        else if (change == "repeat")
        {
            long owner = events.Where(e => e.OwnerOrdinal is not null).Max(e => e.OwnerOrdinal!.Value) + 1;
            events = events.Add(new(events.Length, owner, new PublicOwnerStarted(PublicEvidenceOwnerKind.Event, 0, 3, null, true)));
            events = events.Add(new(events.Length, owner, events[page].Payload));
        }
        else if (change == "one_upgrade")
        {
            var cards = source.Before.Deck;
            var selected = source.Run.Players[0].Deck.Cards.First(c => c.CurrentUpgradeLevel == 1 && c.GetType().Name == "Blur");
            cards[Array.FindIndex(cards, c => c.Id == "Blur")] = PublicViews.Card(selected);
            events = Replace(events, end, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, With(source.Before, deck: cards)));
        }
        else
        {
            var assets = source.Before;
            if (change == "listener") assets = With(assets, relics: assets.Relics.Append(new(nameof(DingyRug), new Dictionary<string, int>())).ToArray());
            else
            {
                var cards = assets.Deck;
                cards[0] = change switch
                {
                    "unsupported_card" => PublicViews.Card(ModelDb.All<CardModel>().Single(c => c.GetType().Name == "Backflip")),
                    "enchanted" => cards[0] with { Enchantments = [new("Sharp", 1)] },
                    "afflicted" => cards[0] with { Affliction = new("Bound", 1) },
                    _ => cards[0] with { Cost = cards[0].Cost + 1 },
                };
                assets = With(assets, deck: cards);
            }
            events = Replace(events, beforeAt, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, assets));
        }
        var root = source.Root with { PublicEvidence = new(PublicRunEvidence.Version, change != "gap", events) };
        Assert.False(NativePublicEventUpgradeCondition.TryCreate(root, Prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal(expected, reason);
        Assert.False(NativePublicEventUpgradeCondition.TryCreate(source.Root with { PublicEvidence = null }, Prior, out _, out _));
        Assert.False(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior with { SchemaVersion = NativeTapePrior.Version }, out _, out _));
    }

    [Theory]
    [InlineData("StrikeSilent+,DefendSilent+,AscendersBane")]
    [InlineData("StrikeSilent,DefendSilent+,AscendersBane")]
    public async Task FewerThanTwoEligibleNativeCardsDeclinesThePairProposal(string deck)
    {
        var source = await Source(deck);
        Assert.False(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal("doors_requires_two_upgradeable_cards", reason);
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("max_hp")]
    [InlineData("gold")]
    [InlineData("relics")]
    [InlineData("potions")]
    [InlineData("max_energy")]
    [InlineData("potion_slots")]
    [InlineData("orb_slots")]
    [InlineData("removals")]
    public async Task CompleteBeforeAndAfterAssetsAreMandatory(string field)
    {
        var source = await Source(MixedDeck);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior, out var condition, out _));
        var target = condition!.Target;
        var events = source.Root.PublicEvidence!.Events;
        int before = checked((int)target.ChoiceOrdinal - 3), end = checked((int)target.EndOrdinal);
        var changedAfter = Change(target.AfterAssets, field);
        var changed = Replace(events, end, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, changedAfter));
        Assert.False(NativePublicEventUpgradeCondition.TryCreate(source.Root with { PublicEvidence = new(PublicRunEvidence.Version, true, changed) },
            Prior, out _, out var reason));
        Assert.Equal("doors_noncard_assets_changed", reason);

        changed = Replace(changed, before, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Change(target.BeforeAssets, field)));
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(source.Root with { PublicEvidence = new(PublicRunEvidence.Version, true, changed) },
            Prior, out var differentBefore, out reason), reason);
        var beforeTape = new NativeLabelTape(new(11, 12, 13, 0, 0));
        var beforeProposal = Proposal(differentBefore!, beforeTape, 97);
        var beforeRun = await Boundary(MixedDeck, beforeProposal.ObservePublicEvidence);
        beforeProposal.AttachHypotheticalRun(beforeRun.Run);
        using (LabelRandomScope.Enter(Word(beforeTape), beforeProposal.BeginShuffle))
            await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => Light(beforeRun));
        Assert.Equal(0, beforeTape.ConditionedCells);
        Assert.Throws<InvalidOperationException>(beforeProposal.ValidateCompletion);

        var afterTape = new NativeLabelTape(new(21, 22, 23, 0, 0));
        var afterProposal = Proposal(condition, afterTape, 101);
        var afterRun = await Boundary(MixedDeck, entry => afterProposal.ObservePublicEvidence(entry.EventOrdinal == target.EndOrdinal
            ? new(entry.EventOrdinal, entry.OwnerOrdinal, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, changedAfter)) : entry));
        afterProposal.AttachHypotheticalRun(afterRun.Run);
        using (LabelRandomScope.Enter(Word(afterTape), afterProposal.BeginShuffle))
            await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => Light(afterRun));
        Assert.Equal(target.PoolKeys.Count - 1, afterTape.ConditionedCells);
        Assert.Equal(0, afterProposal.ConditionedUpgradeCount);
        Assert.Throws<InvalidOperationException>(afterProposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => afterProposal.AcceptCorrection(() => 0));
    }

    [Theory]
    [InlineData("foreign_rng")]
    [InlineData("foreign_owner")]
    [InlineData("missing_card")]
    [InlineData("duplicate_card")]
    [InlineData("unsorted_pool")]
    [InlineData("noncard")]
    [InlineData("missing_choice")]
    public async Task InvalidOwnedShuffleBoundariesFailBeforeForcedWritesAndRemainFailed(string change)
    {
        var source = await Source(MixedDeck);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior, out var condition, out _));
        var tape = new NativeLabelTape(new(31, 41, 59, 0, 0));
        var proposal = Proposal(condition!, tape, 47);
        var fixture = await Boundary(MixedDeck, change == "missing_choice" ? null : proposal.ObservePublicEvidence);
        proposal.AttachHypotheticalRun(fixture.Run);
        ObserveChoice(fixture);
        var pool = Pool(fixture);
        if (change == "foreign_owner")
        {
            var foreignRun = new RunState("foreign-doors-owner", ascensionLevel: 10);
            var foreign = Player.CreateForNewRun(ModelDb.Character<Silent>(), foreignRun);
            var clone = (CardModel)((CardModel)pool[0]!).MutableClone(); clone.AssignOwner(foreign); foreign.Deck.AddInternal(clone);
            pool[0] = clone;
        }
        if (change == "missing_card") pool = pool.Skip(1).ToArray();
        if (change == "duplicate_card") pool[0] = pool[1];
        if (change == "unsorted_pool") Array.Reverse(pool);
        if (change == "noncard") pool[0] = "Acrobatics";
        Assert.Throws<InvalidOperationException>(() => proposal.BeginShuffle(change == "foreign_rng" ? new Rng(981) : fixture.Doors.Rng, pool));
        Assert.Equal(0, tape.ConditionedCells);
        Assert.Throws<InvalidOperationException>(() => proposal.BeginShuffle(fixture.Doors.Rng, Pool(fixture)));
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
    }

    [Theory]
    [InlineData("first_alias")]
    [InlineData("later_alias")]
    [InlineData("incomplete")]
    [InlineData("foreign_stream")]
    [InlineData("duplicate_begin")]
    public async Task RealTapeRejectsAliasingIncompleteConsumptionAndRepeatedOwnership(string failure)
    {
        var source = await Source(MixedDeck);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior, out var condition, out _));
        var tape = new NativeLabelTape(new(71, 81, 91, 0, 0));
        var proposal = Proposal(condition!, tape, 17);
        var fixture = await Boundary(MixedDeck, proposal.ObservePublicEvidence);
        proposal.AttachHypotheticalRun(fixture.Run);
        if (failure is "first_alias" or "later_alias")
        {
            var alias = new Rng(fixture.Doors.Rng.ToSerializable());
            if (failure == "later_alias") alias.NextUnsignedLong();
            var state = alias.ToSerializable();
            _ = Word(tape)(new(state.state0, state.state1, state.state2, state.state3));
            using (LabelRandomScope.Enter(Word(tape), proposal.BeginShuffle))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Light(fixture));
            Assert.Equal(failure == "first_alias" ? 0 : 1, tape.ConditionedCells);
        }
        else
        {
            ObserveChoice(fixture);
            var boundary = proposal.BeginShuffle(fixture.Doors.Rng, Pool(fixture))!;
            if (failure == "incomplete")
            {
                fixture.Doors.Rng.NextUnsignedLong();
                Assert.Throws<InvalidOperationException>(boundary.Dispose);
            }
            else if (failure == "foreign_stream")
            {
                Assert.Throws<InvalidOperationException>(() => new Rng(109).NextUnsignedLong());
                Assert.Throws<InvalidOperationException>(boundary.Dispose);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => proposal.BeginShuffle(fixture.Doors.Rng, Pool(fixture)));
                Assert.IsAssignableFrom<IAbortableLabelShuffleBoundary>(boundary).Abort(new InvalidOperationException("Duplicate native boundary"));
                boundary.Dispose();
            }
        }
        Assert.Equal(0, proposal.ConditionedUpgradeCount);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginShuffle(fixture.Doors.Rng, Pool(fixture)));
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
    }

    [Fact]
    public async Task ConsumingAllWordsWithoutSettlementOrWithARepeatedChoiceCannotComplete()
    {
        var source = await Source(MixedDeck);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior, out var condition, out _));
        foreach (bool duplicateChoice in new[] { false, true })
        {
            var tape = new NativeLabelTape(new(111, 222, 333, 0, 0));
            var proposal = Proposal(condition!, tape, 71);
            var fixture = await Boundary(MixedDeck, proposal.ObservePublicEvidence);
            proposal.AttachHypotheticalRun(fixture.Run);
            using (LabelRandomScope.Enter(Word(tape), proposal.BeginShuffle)) await Light(fixture, settle: false);
            Assert.Equal(1, proposal.ConditionedShuffleCount);
            Assert.Equal(0, proposal.ConditionedUpgradeCount);
            Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
            if (duplicateChoice)
            {
                var choice = fixture.Root.PublicEvidence!.Events.Single(e => e.Payload is PublicOptionChosen);
                Assert.Throws<InvalidOperationException>(() => proposal.ObservePublicEvidence(choice));
                Assert.Throws<InvalidOperationException>(fixture.Evidence.ExitFloor);
                Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            }
            else
            {
                fixture.Evidence.ExitFloor(); proposal.ValidateCompletion();
                Assert.Throws<InvalidOperationException>(() => proposal.BeginShuffle(fixture.Doors.Rng, Pool(fixture)));
                Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            }
        }
    }

    [Fact]
    public async Task NativeSwapFailureAfterTheLastWordAbortsTheRealProposalAndPreservesTheException()
    {
        var source = await Source(MixedDeck);
        Assert.True(NativePublicEventUpgradeCondition.TryCreate(source.Root, Prior, out var condition, out _));
        var tape = new NativeLabelTape(new(71, 81, 91, 0, 0));
        var proposal = Proposal(condition!, tape, 79);
        var fixture = await Boundary(MixedDeck, proposal.ObservePublicEvidence);
        proposal.AttachHypotheticalRun(fixture.Run); ObserveChoice(fixture);
        var nativePool = Pool(fixture).Cast<CardModel>().ToArray();
        var original = new FormatException("Native swap failed after the last Doors word");
        var pool = new ThrowingCards(nativePool, 2 * (nativePool.Length - 2) + 1, original);
        int before = fixture.Doors.Rng.Counter, ordinaryWords = 0;
        using (LabelRandomScope.Enter(state => { ordinaryWords++; return Word(tape)(state); }, proposal.BeginShuffle))
        {
            Assert.Same(original, Assert.Throws<FormatException>(() => fixture.Doors.Rng.Shuffle(pool)));
            Assert.Equal(nativePool.Length - 1, fixture.Doors.Rng.Counter - before);
            Assert.Equal(nativePool.Length - 1, tape.ConditionedCells);
            Assert.Equal(0, ordinaryWords);
            _ = new Rng(592).NextUnsignedLong();
            Assert.Equal(1, ordinaryWords); // Aborted forcing has restored its enclosing word scope.
        }
        Assert.Equal(0, proposal.ConditionedShuffleCount);
        Assert.Equal(0, proposal.ConditionedUpgradeCount);
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Same(original, tape.ConditionedWordError!.InnerException);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
    }

    private sealed class ThrowingCards(CardModel[] cards, int throwOnSet, Exception failure) : Collection<CardModel>(cards.ToList())
    {
        private int _sets;
        protected override void SetItem(int index, CardModel item)
        {
            if (++_sets == throwOnSet) throw failure;
            base.SetItem(index, item);
        }
    }

    private sealed record Fixture(RunState Run, DoorsOfLightAndDark Doors, NativePublicRunEvidence Evidence, PublicEvidenceAssets Before)
    {
        internal DecisionPacket Root => new("fixture", null, [], Evidence.Capture());
    }
    private static async Task<Fixture> Boundary(string deck, Action<PublicRunEvidenceEvent>? observer = null)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("constructed-public-doors-light-fixture", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        foreach (var card in player.Deck.Cards.ToArray()) player.Deck.RemoveInternal(card);
        foreach (string spec in deck.Split(','))
        {
            string id = spec.TrimEnd('+');
            var card = (CardModel)ModelDb.All<CardModel>().Single(c => c.GetType().Name == id).MutableClone();
            card.AssignOwner(player); if (spec.EndsWith('+')) card.Upgrade(); player.Deck.AddInternal(card);
        }
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        var point = run.Map.StartingMapPoint.Children.OrderBy(p => p.coord.col).First(); point.PointType = MapPointType.Unknown;
        var evidence = new NativePublicRunEvidence(run, onAppended: observer); evidence.BeginRun(true);
        evidence.Map([point], point); run.AddVisitedMapCoord(point.coord);
        var room = new EventRoom(() => (DoorsOfLightAndDark)ModelDb.Event<DoorsOfLightAndDark>().MutableClone());
        run.PushRoom(room); await room.Enter(run); evidence.EnterFloor();
        return new(run, (DoorsOfLightAndDark)room.Event, evidence, NativePublicRunEvidence.Assets(player));
    }
    private static async Task<Fixture> Source(string deck)
    {
        var fixture = await Boundary(deck);
        // Each native integer draw picks its highest bucket: the sorted pool remains in order.
        using (LabelRandomScope.Enter(_ => ulong.MaxValue)) await Light(fixture);
        return fixture;
    }
    private static void ObserveChoice(Fixture fixture) => fixture.Evidence.EventOptions(fixture.Doors.CurrentOptions,
        fixture.Doors.CurrentOptions.Single(o => o.Key == "LIGHT"));
    private static async Task Light(Fixture fixture, bool settle = true)
    {
        var choice = fixture.Doors.CurrentOptions.Single(o => o.Key == "LIGHT");
        ObserveChoice(fixture); await fixture.Doors.ChooseOption(choice);
        Assert.True(fixture.Doors.IsFinished); if (settle) fixture.Evidence.ExitFloor();
    }
    private static object?[] Pool(Fixture fixture) => fixture.Run.Players[0].Deck.Cards.Where(c => c.IsUpgradable)
        .OrderBy(c => c).Cast<object?>().ToArray();
    private static NativePublicEventUpgradeProposal Proposal(NativePublicEventUpgradeCondition condition, NativeLabelTape tape, ulong seed) =>
        new(condition, new Rng(seed).NextUnsignedLong, Force(tape), typeof(NativeLabelTape)
            .GetMethod("CaptureConditionedFailure", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<Exception>>(tape));
    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> Force(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
    private static ImmutableArray<PublicRunEvidenceEvent> Replace(ImmutableArray<PublicRunEvidenceEvent> events, int at, PublicEvidencePayload payload) =>
        events.SetItem(at, new(events[at].EventOrdinal, events[at].OwnerOrdinal, payload));
    private static PublicEvidenceAssets With(PublicEvidenceAssets source, PublicCard[]? deck = null, PublicRelic[]? relics = null) =>
        new(source.Hp, source.MaxHp, source.Gold, deck ?? source.Deck, relics ?? source.Relics, source.Potions,
            source.MaxEnergy, source.PotionSlots, source.OrbSlots, source.CardRemovalsUsed);
    private static PublicEvidenceAssets Change(PublicEvidenceAssets source, string field) => new(
        source.Hp - (field == "hp" ? 1 : 0), source.MaxHp + (field == "max_hp" ? 1 : 0),
        source.Gold + (field == "gold" ? 1 : 0), source.Deck,
        field == "relics" ? source.Relics.Append(new(nameof(LavaRock), new Dictionary<string, int>())).ToArray() : source.Relics,
        field == "potions" ? source.Potions.SetItem(0, "PowderedDemise") : field == "potion_slots" ? source.Potions.Add(null) : source.Potions,
        source.MaxEnergy + (field == "max_energy" ? 1 : 0), source.PotionSlots + (field == "potion_slots" ? 1 : 0),
        source.OrbSlots + (field == "orb_slots" ? 1 : 0), source.CardRemovalsUsed + (field == "removals" ? 1 : 0));
}
