using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicReshuffleTests
{
    private static PublicCard Card(string id) => new(id, 0, 1, -1, "Skill", []);
    private static (DecisionPacket Root, NativeEntryAssets Entry) Fixture(string variation = "valid")
    {
        PublicCard[] deck = Enumerable.Repeat(Card("StrikeSilent"), 5)
            .Concat(Enumerable.Repeat(Card("DefendSilent"), 5)).Concat([Card("Neutralize"), Card("Survivor")]).ToArray();
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, deck,
            [new("RingOfTheSnake", new Dictionary<string, int>())], [null, null], 3, 2, 0, 0);
        var assets = new PublicEvidenceAssets(56, 70, 99, deck, entry.Relics, entry.Potions.ToImmutableArray(), 3, 2, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        void Fact(PublicCombatFactKind kind, PublicCard? card = null) => recorder.Record(owner,
            new PublicCombatFact(kind, cards: card is null ? [] : [card]));
        long Decision(PublicObservation observation, int revision) => recorder.Record(owner,
            new PublicCombatDecision("player_decision", observation, [new(revision, "end_turn")],
                recorder.Capture().Events[^1].EventOrdinal, !recorder.Capture().Events.Any(item => item.Payload is PublicEvidenceGap)));
        Fact(PublicCombatFactKind.Started);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.EntryAssets, assets: assets));
        foreach (var card in deck.Take(7)) Fact(PublicCombatFactKind.CardDrawn, card);
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
        recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0, model: "SludgeSpinner"));
        var observation = new PublicObservation("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            deck.Take(7).ToArray(), [], [], deck.Skip(7).Select(card => new CardCount(card, 1)).ToArray(), [], 5,
            entry.Potions, ["RingOfTheSnake"], [], [new(0, "SludgeSpinner", 42, 42, 0, [], [])], [], null);
        long first = Decision(observation, 0);
        recorder.Record(owner, new PublicCombatActionTaken(first, new(0, "end_turn")));
        foreach (var card in deck.Skip(7)) Fact(PublicCombatFactKind.CardDrawn, card);
        observation = observation with { Turn = 2, Hand = deck.Skip(7).ToArray(), Discard = deck.Take(7).ToArray(),
            UnknownDraw = [], DrawCount = 0 };
        long second = Decision(observation, 1);
        recorder.Record(owner, new PublicCombatActionTaken(second, new(1, "end_turn")));
        Fact(PublicCombatFactKind.Shuffled);
        if (variation == "gap") recorder.RecordGap(owner, PublicEvidenceGapReason.Interrupted);
        if (variation == "generation") Fact(PublicCombatFactKind.HiddenCardGenerated);
        foreach (var card in deck.Take(5)) Fact(PublicCombatFactKind.CardDrawn, card);
        if (variation == "second_shuffle") Fact(PublicCombatFactKind.Shuffled);
        observation = observation with { Turn = 3, Hand = deck.Take(5).ToArray(), Discard = [],
            UnknownDraw = deck.Skip(5).Select(card => new CardCount(card, 1)).ToArray(), DrawCount = 7 };
        if (variation == "oversized") observation = observation with
        { UnknownDraw = [new(Card("StrikeSilent"), 1), .. observation.UnknownDraw], DrawCount = 8 };
        if (variation == "unidentified") observation = observation with { UnidentifiedDrawCount = 1, UnknownDraw = observation.UnknownDraw.Skip(1).ToArray() };
        if (variation == "known") observation = observation with { KnownDraw = [new(0, deck[5])], UnknownDraw = observation.UnknownDraw.Skip(1).ToArray() };
        if (variation != "missing") Decision(observation, 2);
        return (new("player_decision", observation, [new(2, "end_turn")], PublicEvidence: recorder.Capture()), entry);
    }

    [Theory]
    [InlineData("valid", 1)]
    [InlineData("missing", 0)]
    [InlineData("gap", 0)]
    [InlineData("generation", 0)]
    [InlineData("second_shuffle", 0)]
    [InlineData("oversized", 0)]
    [InlineData("unidentified", 0)]
    [InlineData("known", 0)]
    public void JointPublicWitnessIsRequiredAndFirstCycleIsUnchanged(string variation, int count)
    {
        var (root, entry) = Fixture(variation);
        string before = PublicJson.Serialize(root);
        var first = NativePublicCombatPrefixCondition.Create(root).Combats[0];
        var condition = NativePublicReshuffleCondition.Create(root);
        Assert.Equal(count, condition.EligibleShuffleCount);
        Assert.Equal(12, first.Shuffle!.DrawPrefixIds.Length);
        Assert.Equal("first_reshuffle", first.DrawPrefix!.StopReason);
        if (count > 0)
        {
            var target = Assert.Single(condition.Combats[0].Targets);
            Assert.Equal(entry.Deck.Select(card => card.Id).Order(), target.PoolIds.Order());
            Assert.Equal(entry.Deck.Take(5).Select(card => card.Id), target.DrawPrefixIds);
            Assert.True(target.WitnessEventOrdinal > target.ShuffleEventOrdinal);
        }
        Assert.Equal(before, PublicJson.Serialize(root));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("pool")]
    [InlineData("alias")]
    [InlineData("conflict")]
    [InlineData("final_alias")]
    [InlineData("dispose")]
    [InlineData("extra")]
    public async Task OnlyExactOwnedNativeReshuffleConsumesTargetAndScopeCleansUp(string failure)
    {
        var (root, entry) = Fixture();
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("reshuffle-marker-fixture", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        var state = new CombatState(run); state.AddPlayerCreature(player.Creature);
        state.AddMonster((MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone(), CombatSide.Enemy);
        player.ResetCombatState(); player.PopulateCombatState(run.Rng.Shuffle);
        // Explicit test lifecycle gives the hypothetical fixture the detached public pool.
        foreach (var card in player.PlayerCombatState!.DrawPile.Cards.ToArray()) CardPileCmd.Remove(card);
        foreach (string id in entry.Deck.Select(card => card.Id))
        {
            var deckCard = player.Deck.Cards.First(card => card.GetType().Name == id);
            var copy = (CardModel)deckCard.MutableClone(); copy.AssignOwner(player);
            copy.AssignDeckVersionInternal(deckCard); CardPileCmd.Add(copy, PileType.Discard);
        }
        var random = new Rng(73, "reshuffle-proposal");
        int words = 0;
        IDisposable Force(IReadOnlyList<ulong> forced, Rng rng, string purpose)
        {
            var queue = new Queue<ulong>(forced);
            bool aborted = false;
            return new Finish(LabelRandomScope.Enter(_ =>
            {
                if (failure is "alias" or "conflict" || failure == "final_alias" && queue.Count == 1)
                { aborted = true; throw new InvalidOperationException(failure + " is unresolved"); }
                words++; return queue.Dequeue();
            }), () =>
            {
                if (!aborted) Assert.Empty(queue);
                if (failure == "dispose") throw new InvalidOperationException("forcing disposal failed");
                if (failure == "extra") rng.NextUnsignedLong();
            });
        }
        var proposal = new NativePublicReshuffleProposal(NativePublicReshuffleCondition.Create(root), random.NextUnsignedLong, Force);
        proposal.AttachHypotheticalRun(run); proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        int correctionWords = 0;
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => { correctionWords++; return 0; }));
        Assert.Equal(0, correctionWords);
        if (failure == "pool") CardPileCmd.Remove(player.PlayerCombatState.DiscardPile.Cards[0]);
        using (failure == "missing" ? null : proposal.EnterScope())
        using (LabelRandomScope.Enter(_ => ulong.MaxValue, proposal.BeginShuffle))
        {
            var foreign = player.PlayerCombatState.DiscardPile.Cards.ToList();
            run.Rng.Shuffle.Shuffle(foreign); // Same RNG and physical cards, different call owner.
            Assert.Equal(0, words);
            if (failure != "none")
            {
                if (failure == "missing") await CardPileCmd.Shuffle(state, player);
                else if (failure == "pool") await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() => CardPileCmd.Shuffle(state, player));
                else
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CardPileCmd.Shuffle(state, player));
                    if (failure is "alias" or "conflict" or "final_alias") Assert.Contains(failure, error.Message);
                }
                Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
                Assert.Null(LabelCombatReshuffleScope.Current);
                return;
            }
            await CardPileCmd.Shuffle(state, player);
            Assert.Equal(11, words);
            Assert.Equal(Enumerable.Repeat("StrikeSilent", 5), player.PlayerCombatState.DrawPile.Cards.Take(5).Select(card => card.GetType().Name));
            proposal.ValidateCompletion();
            Assert.True(proposal.AcceptCorrection(() => ulong.MaxValue));
            Assert.Null(LabelCombatReshuffleScope.Current);
        }
        Assert.Null(LabelCombatReshuffleScope.Current);
    }

    private sealed class Finish(IDisposable inner, Action finish) : IDisposable
    { public void Dispose() { inner.Dispose(); finish(); } }

    [Fact]
    public async Task TwoCardFinalWordFailureNeverAcknowledgesNativeShuffleSuccess()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "two-card-reshuffle-failure",
            Deck: ["DefendSilent", "DefendSilent"], Enemy: "SludgeSpinner", EnemyHp: 1000));
        var player = session.State.Players.Single();
        foreach (var card in player.PlayerCombatState!.Hand.Cards.ToArray()) CardPileCmd.Add(card, PileType.Discard);
        bool acknowledged = false;
        using (LabelCombatReshuffleScope.Enter())
        using (LabelRandomScope.Enter(_ => ulong.MaxValue, (rng, cards) =>
        {
            Assert.Equal(2, cards.Count);
            LabelCombatReshuffleScope.Current!.AfterSuccessfulShuffle(() => acknowledged = true);
            return LabelRandomScope.Enter(_ => throw new InvalidOperationException("last raw word failed"));
        }))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CardPileCmd.Shuffle(session.State, player));
            Assert.Equal("last raw word failed", error.Message);
            Assert.False(acknowledged);
            Assert.Null(LabelCombatReshuffleScope.Current);
        }
        Assert.Null(LabelCombatReshuffleScope.Current);
    }

    [Fact]
    public async Task NativeSuccessiveCyclesKeepSeparateWitnessesAndExtendOnlyTheirOwnPrefix()
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "two-native-reshuffle-cycles",
            Deck: Enumerable.Range(0, 12).Select(index => index % 2 == 0 ? "Backflip" : "DefendSilent").ToArray(),
            Enemy: "SludgeSpinner", EnemyHp: 1000, Hp: 1000, MaxHp: 1000));
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        DecisionPacket Anchor(DecisionPacket packet) => packet with { Observation = packet.Observation! with
        {
            History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                .. packet.Observation.History.Skip(1)],
        } };
        var current = Anchor(session.Observe()); recorder.ObserveCombatDecision(owner, current);
        for (int turn = 0; turn < 4; turn++)
        {
            current = Anchor(await session.StepAsync(current.Actions.Single(action => action.Kind == "end_turn")));
            recorder.ObserveCombatDecision(owner, current);
        }
        var root = current with { PublicEvidence = recorder.Capture() };
        var condition = NativePublicReshuffleCondition.Create(root);
        var targets = condition.Combats[0].Targets;
        Assert.Equal(2, targets.Count);
        Assert.Equal([0, 1], targets.Select(target => target.ReshuffleOrdinal));
        // The two cards drawn before the second reshuffle are now in hand.
        Assert.Equal([12, 10], targets.Select(target => target.PoolIds.Count));
        Assert.Equal([12, 3], targets.Select(target => target.DrawPrefixIds.Count));
        Assert.True(targets[0].WitnessEventOrdinal < targets[1].ShuffleEventOrdinal);
        Assert.Equal(12, NativePublicCombatPrefixCondition.Create(root).Combats[0].Shuffle!.DrawPrefixIds.Length);
    }

    [Theory]
    [InlineData(11002UL, 12, 1)]
    [InlineData(11004UL, 11, 9)]
    public async Task RealRetainedPublicHistoriesWitnessLaterPoolWithoutSourceState(ulong seed, int poolCount, int prefixCount)
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
        // Round-trip discards any possibility of retaining native references in the certificate input.
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
        var condition = NativePublicReshuffleCondition.Create(root);
        var target = Assert.Single(condition.Combats[0].Targets);
        Assert.Equal(poolCount, target.PoolIds.Count);
        Assert.Equal(prefixCount, target.DrawPrefixIds.Count);
        if (seed == 11002)
        {
            Assert.Equal(76, target.ShuffleEventOrdinal); Assert.Equal(80, target.WitnessEventOrdinal);
            Assert.Equal(["DefendSilent"], target.DrawPrefixIds);
            Assert.Equal(5, target.PoolIds.Count(id => id == "DefendSilent"));
        }
    }
}
