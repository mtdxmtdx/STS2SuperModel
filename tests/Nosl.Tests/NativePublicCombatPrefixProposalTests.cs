using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicCombatPrefixProposalTests
{
    private static (RunState Run, NativeEntryAssets Entry, NativePublicCombatPrefixCondition Condition) Setup(int combats = 2)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-combat-prefix-unit-fixture", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        var entry = NativeEntryAssets.Capture(CombatAssetSnapshot.Capture(player), player.Creature.CurrentHp,
            player.PotionSlots.Select(p => p?.GetType().Name).ToArray());
        var assets = new PublicEvidenceAssets(entry.Hp, entry.MaxHp, entry.Gold, entry.Deck, entry.Relics,
            entry.Potions.ToImmutableArray(), entry.MaxEnergy, entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        var hand = entry.Deck.Take(7).ToArray();
        PublicEvent[] history = [new("combat_started", "Silent:A10"), new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
            .. hand.Select(card => new PublicEvent("draw", PublicJson.Serialize(card))), new("player_turn", "1"),
            new("intent_published", PublicJson.Serialize(new { slot = 0, id = "SludgeSpinner", intents = Array.Empty<PublicIntent>() }))];
        var packet = new DecisionPacket("player_decision", new("nosl.public.v2", entry.Hp, 10, 1, entry.Hp, entry.MaxHp,
            0, 3, 0, hand, [], [], entry.Deck.Skip(7).Select(card => new CardCount(card, 1)).ToArray(), [], entry.Deck.Length - 7,
            entry.Potions, entry.Relics.Select(relic => relic.Id).ToArray(), [], [new(0, "SludgeSpinner", 42, 42, 0, [], [])], history, null),
            [new(0, "end_turn")]);
        for (int i = 0; i < combats; i++)
        {
            long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, i + 2);
            recorder.ObserveCombatDecision(owner, packet);
            recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        }
        var condition = NativePublicCombatPrefixCondition.Create(recorder.Capture());
        Assert.Equal(combats, condition.EligibleShuffleCount);
        Assert.Equal(combats, condition.EligibleHpCount);
        return (run, entry, condition);
    }

    private sealed class ForceHarness
    {
        internal readonly Dictionary<LabelRandomState, ulong> Words = [];
        internal readonly HashSet<LabelRandomState> Visited = [];
        internal int CompletedScopes;
        internal IDisposable Force(IReadOnlyList<ulong> words, string purpose)
        {
            int index = 0; bool failed = false;
            var scope = LabelRandomScope.Enter(state =>
            {
                try
                {
                    if (!Visited.Add(state)) throw new InvalidOperationException("earlier tape cell; alias unresolved");
                    ulong word = words[index++];
                    if (Words.TryGetValue(state, out ulong previous) && previous != word)
                        throw new InvalidOperationException("preexisting cell conflict; unresolved");
                    Words[state] = word;
                    return word;
                }
                catch { failed = true; throw; }
            });
            return new FinishScope(scope, () => { if (!failed) { Assert.Equal(words.Count, index); CompletedScopes++; } });
        }
    }
    private sealed class FinishScope(IDisposable inner, Action finished) : IDisposable
    {
        public void Dispose() { inner.Dispose(); finished(); }
    }

    private static CombatState Enter(NativePublicCombatPrefixProposal proposal, RunState run, NativeEntryAssets entry, int index)
    {
        proposal.CombatEntering(index, entry, run.Rng.Shuffle);
        var state = new CombatState(run);
        var player = run.Players.Single();
        state.AddPlayerCreature(player.Creature);
        state.AddMonster((MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone(), CombatSide.Enemy);
        player.ResetCombatState();
        player.PopulateCombatState(run.Rng.Shuffle);
        return state;
    }

    [Fact]
    public void ComposesActualNativeStartupsResetsHpIdentityAndRetainsAllCorrections()
    {
        var (run, entry, condition) = Setup();
        var random = new Rng(12489, "public-combat-prefix-proposal-test");
        var harness = new ForceHarness();
        var proposal = new NativePublicCombatPrefixProposal(condition, random.NextUnsignedLong, harness.Force);
        proposal.AttachHypotheticalRun(run);
        using var scope = LabelRandomScope.Enter(_ => throw new InvalidOperationException("Unforced startup draw"),
            proposal.BeginShuffle, proposal.BeginMonsterHp);
        int correctionWords = 0;
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => { correctionWords++; return ulong.MaxValue; }));
        Assert.Equal(0, correctionWords);
        var first = Enter(proposal, run, entry, 0);
        Assert.Equal(42, first.Enemies.Single().MaxHp);
        Assert.Equal(condition.Combats[0].Shuffle!.DrawPrefixIds,
            run.Players.Single().PlayerCombatState!.DrawPile.Cards.Take(7).Select(card => card.GetType().Name));
        var firstRatio = proposal.NativeToProposalRatio;
        var firstEnvelope = proposal.Envelope;
        Assert.Throws<InvalidOperationException>(() => proposal.ValidateCompletion());
        var second = Enter(proposal, run, entry, 1);
        Assert.Equal(42, second.Enemies.Single().MaxHp);
        Assert.Equal(2, proposal.ConditionedHpCount); // The repeated public type belongs to a new combat.
        Assert.Equal(2, proposal.ConditionedShuffleCount);
        Assert.Equal(4, harness.CompletedScopes);
        Assert.Equal(firstEnvelope.Multiply(firstEnvelope.Numerator, firstEnvelope.Denominator), proposal.Envelope);
        Assert.True(proposal.NativeToProposalRatio.Numerator * firstRatio.Denominator
            < firstRatio.Numerator * proposal.NativeToProposalRatio.Denominator);
        proposal.ValidateCompletion();
        Assert.True(proposal.AcceptCorrection(() => ulong.MaxValue));
        int forced = harness.Words.Count;
        Assert.Null(proposal.BeginShuffle(run.Rng.Shuffle, run.Players.Single().PlayerCombatState!.DrawPile.Cards.Cast<object?>().ToArray()));
        Assert.Equal(forced, harness.Words.Count); // Later shuffles remain ordinary native tape draws.
    }

    [Fact]
    public void EntryAndNativeOwnerChecksPrecedeProposalWords()
    {
        var (run, entry, condition) = Setup(1);
        int draws = 0;
        var proposal = new NativePublicCombatPrefixProposal(condition, () => { draws++; return ulong.MaxValue; }, (_, _) => throw new Exception());
        Assert.Throws<InvalidOperationException>(() => proposal.CombatEntering(0, entry, run.Rng.Shuffle));
        proposal.AttachHypotheticalRun(run);
        Assert.Throws<InvalidOperationException>(() => proposal.AttachHypotheticalRun(run));
        Assert.Throws<InvalidOperationException>(() => proposal.CombatEntering(0, entry, new Rng(1)));
        Assert.Throws<InvalidOperationException>(() => proposal.CombatEntering(1, entry, run.Rng.Shuffle));
        Assert.Throws<NativePublicConstraintMismatchException>(() => proposal.CombatEntering(0, entry with { Gold = entry.Gold + 1 }, run.Rng.Shuffle));
        Assert.Equal(0, draws);
    }

    [Fact]
    public void AnotherCombatStateInTheSameRunCannotSupplyConditionedHp()
    {
        var (run, entry, condition) = Setup(1);
        var random = new Rng(18);
        var harness = new ForceHarness();
        var proposal = new NativePublicCombatPrefixProposal(condition, random.NextUnsignedLong, harness.Force);
        proposal.AttachHypotheticalRun(run);
        proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        var owned = new CombatState(run);
        owned.AddPlayerCreature(run.Players.Single().Creature);
        var other = new CombatState(run);
        using var scope = LabelRandomScope.Enter(_ => 0, beginMonsterHp: proposal.BeginMonsterHp);
        var error = Assert.Throws<InvalidOperationException>(() => other.AddMonster(
            (MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone(), CombatSide.Enemy));
        Assert.Contains("owned native run", error.Message);
        Assert.Empty(harness.Words);
    }

    [Fact]
    public void OwnedCreatureWithForeignHpRngIsAnUnresolvedStreamError()
    {
        var (run, entry, condition) = Setup(1);
        int draws = 0;
        var proposal = new NativePublicCombatPrefixProposal(condition, () => { draws++; return 0; }, (_, _) => throw new Exception());
        proposal.AttachHypotheticalRun(run);
        proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        var state = new CombatState(run);
        state.AddPlayerCreature(run.Players.Single().Creature);
        using var scope = LabelRandomScope.Enter(_ => 0, beginMonsterHp: context =>
            proposal.BeginMonsterHp(context with { Rng = new Rng(147) }));
        Assert.Throws<InvalidOperationException>(() => state.AddMonster(
            (MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone(), CombatSide.Enemy));
        Assert.Equal(0, draws);
    }

    [Fact]
    public void VisitedAndPreexistingStateCellConflictsPropagateAsUnresolved()
    {
        foreach (bool visited in new[] { false, true })
        {
            var (run, entry, condition) = Setup(1);
            var random = new Rng(182);
            var harness = new ForceHarness();
            var proposal = new NativePublicCombatPrefixProposal(condition, random.NextUnsignedLong, harness.Force);
            proposal.AttachHypotheticalRun(run);
            var state = run.Rng.Niche.ToSerializable();
            var address = new LabelRandomState(state.state0, state.state1, state.state2, state.state3);
            if (visited) harness.Visited.Add(address);
            else harness.Words.Add(address, 0); // Target 42 needs a nonzero high bucket.
            using var scope = LabelRandomScope.Enter(_ => 0, proposal.BeginShuffle, proposal.BeginMonsterHp);
            var error = Assert.Throws<InvalidOperationException>(() => Enter(proposal, run, entry, 0));
            Assert.Contains("unresolved", error.Message);
        }
    }
}
