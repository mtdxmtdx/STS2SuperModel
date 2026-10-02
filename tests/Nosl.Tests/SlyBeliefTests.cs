using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Afflictions;
using Sts2Sim.Core.Models.Enchantments;

namespace Nosl.Tests;

public sealed class SlyBeliefTests
{
    // The actual held-out diagnostic's observed prefix only. The revision-9 student
    // selection and later outcomes are deliberately absent from conditioning evidence.
    internal static readonly Scenario DiagnosticScenario = new(
        Seed: "NOSL-OFFLINE-HOLDOUT-v4-FIRST-TRIAL:discard-draw:0",
        Deck: ["Prepared+", "DaggerThrow", "Acrobatics", "Reflex", "Tactician", "DefendSilent", "StrikeSilent"],
        Enemy: "TwigSlimeM", EnemyHp: 42, Hp: 18);
    internal static readonly PublicAction[] DiagnosticPrefix = [
        new(0, "play", 2, 0), new(1, "play", 0, 0), new(2, "choose", Selection: [3]),
        new(3, "play", 4, 0), new(4, "play", 3, -2), new(5, "play", 0, -2),
        new(6, "choose", Selection: [2, 3]), new(7, "end_turn"), new(8, "play", 2, -2)];

    [Fact]
    public async Task ActualRevisionNineAcceptsExactlyOneOfSixCompletePacketsAndThirtyOrderedActions()
    {
        await using var stable = await Diagnostic(8);
        var before = stable.Observe();
        Assert.Equal(BeliefSampler.SlyExchangeableProfile, BeliefSampler.PosteriorProfileFor(stable));
        Assert.Equal(new[] { "Acrobatics", "DaggerThrow", "DefendSilent" }, before.Observation!.UnknownDraw.Select(c => c.Card.Id).Order());
        Assert.All(before.Observation.UnknownDraw, c => Assert.Equal(1, c.Count));
        var prior = BeliefSampler.EnumerateDrawPosterior(before.Observation);
        Assert.Equal(6, prior.Count);
        await using var source = stable.ForkExact();
        await source.StepAsync(DiagnosticPrefix[8]);
        string packet = PublicJson.Serialize(source.Observe());
        Assert.Equal("card_choice", source.Observe().Status);
        Assert.Equal(30, source.Observe().Actions.Length);
        Assert.Equal(30, source.Observe().Actions.Select(PublicJson.Serialize).Distinct().Count());
        Assert.All(source.Observe().Actions, a => Assert.Equal(2, a.Selection!.Length));
        Assert.Equal(BeliefSampler.SlyConditionalChoiceProfile, BeliefSampler.PosteriorProfileFor(source));
        Assert.Equal(new[] { "DefendSilent", "Acrobatics" }, source.Observe().Observation!.History.Skip(before.Observation.History.Length)
            .Where(e => e.Kind == "draw").Select(e => PublicJson.Read<PublicCard>(e.Detail).Id));
        int accepted = 0; double mass = 0;
        foreach (var (order, probability) in prior)
        {
            await using var world = stable.ForkExact();
            var pile = world.State.Players[0].PlayerCombatState!.DrawPile;
            var cards = order.Select(c => pile.Cards.Single(x => Signature(x) == PublicJson.Serialize(c))).ToArray();
            Reorder(pile, cards);
            bool match = await CombatSession.ReplayChoiceSuffixAsync(world, [DiagnosticPrefix[8]], [PublicJson.Serialize(before), packet]);
            if (!match) continue;
            accepted++; mass += probability;
            Assert.Equal(packet, PublicJson.Serialize(world.Observe())); // Includes the complete ordered action array.
            Assert.Equal("DaggerThrow", Assert.Single(world.State.Players[0].PlayerCombatState!.DrawPile.Cards).GetType().Name);
        }
        Assert.Equal(1, accepted); Assert.Equal(1d / 6, mass, 12);
        int rejections = 0;
        for (ulong seed = 0; seed < 24; seed++)
        {
            try { await using var proposal = await BeliefSampler.SampleWorldAsync(source, seed, 1); }
            catch (PosteriorSamplingException) { rejections++; }
            await using var world = await BeliefSampler.SampleWorldAsync(source, seed, 128);
            Assert.Equal(packet, PublicJson.Serialize(world.Observe()));
        }
        Assert.True(rejections > 0); // Observed rejection is not discarded or relabeled a loss.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrderedSlyChoiceUsesNativePlayPilesEnergyHistoryAndCounters(bool tacticianFirst)
    {
        var source = await Diagnostic(9);
        var world = await BeliefSampler.SampleWorldAsync(source, 9001, 128);
        await using var branch = await world.ForkForContinuationAsync();
        string acceptedRng = Streams(world);
        Assert.Equal(acceptedRng, Streams(branch));
        Assert.NotSame(world.State.RunState.Rng, branch.State.RunState.Rng);
        Assert.NotSame(world.State.Players[0].PlayerRng, branch.State.Players[0].PlayerRng);
        Assert.NotEqual(Streams(source), acceptedRng);
        var before = branch.Observe().Observation!;
        string first = tacticianFirst ? "Tactician" : "Reflex", second = tacticianFirst ? "Reflex" : "Tactician";
        int a = Array.FindIndex(before.Choice!.Candidates, c => c.Id == first), b = Array.FindIndex(before.Choice.Candidates, c => c.Id == second);
        var action = branch.Observe().Actions.Single(x => x.Selection!.SequenceEqual(new[] { a, b }));
        await world.StepAsync(action);
        string expected = PublicJson.Serialize(world.Observe()), expectedRng = Streams(world);
        await world.DisposeAsync(); await source.DisposeAsync(); // Child owns accepted future streams and coroutine.
        await branch.StepAsync(action);
        Assert.Equal(expected, PublicJson.Serialize(branch.Observe())); Assert.Equal(expectedRng, Streams(branch));
        Assert.False(branch.HasConditionalChoiceOrigin);
        var after = branch.Observe().Observation!;
        Assert.Equal(before.Energy + 1, after.Energy);
        Assert.Empty(branch.State.Players[0].PlayerCombatState!.PlayPile.Cards);
        Assert.Equal(before.Counters!.CardsDiscarded + 2, after.Counters!.CardsDiscarded);
        Assert.Equal(before.Counters.CardsPlayedCombat + 3, after.Counters.CardsPlayedCombat); // Two Sly plays, then suspended Prepared finishes.
        Assert.Equal(before.Counters.ManualCardsPlayed + 1, after.Counters.ManualCardsPlayed);
        var suffix = after.History.Skip(before.History.Length).ToArray();
        Assert.Equal(new[] { first, second }, suffix.Where(e => e.Kind == "card_started").Select(e => PublicJson.Read<PublicCard>(e.Detail).Id));
        var drawn = suffix.Where(e => e.Kind == "draw").Select(e => PublicJson.Read<PublicCard>(e.Detail).Id).ToArray();
        Assert.Equal(tacticianFirst ? new[] { "DaggerThrow", "Tactician" } : new[] { "DaggerThrow" }, drawn);
        Assert.Equal(before.Counters.CardsDrawnCombat + drawn.Length, after.Counters.CardsDrawnCombat);
        Assert.Equal(tacticianFirst ? 1 : 0, suffix.Count(e => e.Kind == "shuffle"));
        Assert.Contains(after.Discard, c => c.Id == "Reflex");
        Assert.Equal(tacticianFirst, after.Hand.Any(c => c.Id == "Tactician"));
        Assert.Equal(!tacticianFirst, after.Discard.Any(c => c.Id == "Tactician"));
        foreach (var e in suffix.Where(e => e.Kind == "card_played"))
        {
            using var played = JsonDocument.Parse(e.Detail);
            Assert.Equal(0, played.RootElement.GetProperty("energySpent").GetInt32());
        }
    }

    [Theory]
    [InlineData("Reflex", 0)]
    [InlineData("Reflex", 1)]
    [InlineData("Tactician", 0)]
    [InlineData("Tactician", 1)]
    public async Task PublicUpgradeDeterminesPaidManualAndFreeDiscardEffects(string id, int upgrade)
    {
        string[] deck = [id + (upgrade == 1 ? "+" : ""), "Survivor", "Prepared", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "StrikeSilent", "StrikeSilent", "StrikeSilent", "Slice", "Footwork"];
        await using var stable = await FindSource(deck, o => o.Hand.Any(c => c.Id == id) && o.Hand.Any(c => c.Id == "Survivor"));
        await using var manual = stable.ForkExact(); await using var discard = stable.ForkExact();
        var initial = stable.Observe().Observation!;
        var publicCard = initial.Hand.Single(c => c.Id == id);
        Assert.Equal(3, publicCard.Cost); Assert.Equal(upgrade, publicCard.Upgrade); Assert.Contains("Sly", publicCard.Keywords);
        await manual.StepAsync(Play(manual, id));
        var paid = manual.Observe().Observation!;
        Assert.Equal(initial.Energy - 3 + (id == "Tactician" ? 1 + upgrade : 0), paid.Energy);
        Assert.Equal(initial.Counters!.CardsDrawnCombat + (id == "Reflex" ? 2 + upgrade : 0), paid.Counters!.CardsDrawnCombat);
        Assert.Equal(3, EnergySpent(paid.History, id));
        await discard.StepAsync(Play(discard, "Survivor"));
        int slot = Array.FindIndex(discard.Observe().Observation!.Choice!.Candidates, c => c.Id == id);
        await discard.StepAsync(discard.Observe().Actions.Single(a => a.Selection!.SequenceEqual(new[] { slot })));
        var free = discard.Observe().Observation!;
        Assert.Equal(initial.Energy - 1 + (id == "Tactician" ? 1 + upgrade : 0), free.Energy);
        Assert.Equal(initial.Counters.CardsDrawnCombat + (id == "Reflex" ? 2 + upgrade : 0), free.Counters!.CardsDrawnCombat);
        Assert.Equal(0, EnergySpent(free.History, id));
        Assert.Equal(1, free.Counters.ManualCardsPlayed); Assert.Equal(2, free.Counters.CardsPlayed);
    }

    [Fact]
    public async Task EndTurnFlushDoesNotAutoplaySly()
    {
        await using var s = await CombatSession.CreateAsync(new(Deck: ["Reflex", "Tactician", "DefendSilent", "StrikeSilent", "Prepared", "Survivor", "Slice"], Enemy: "LeafSlimeS", EnemyHp: 80));
        var before = s.Observe().Observation!;
        Assert.Contains(before.Hand, c => c.Id == "Reflex"); Assert.Contains(before.Hand, c => c.Id == "Tactician");
        await s.StepAsync(s.Observe().Actions.Single(a => a.Kind == "end_turn"));
        var after = s.Observe().Observation!;
        Assert.DoesNotContain(after.History.Skip(before.History.Length), e => e.Kind is "card_started" or "card_played");
        Assert.Equal(before.Counters!.CardsPlayedCombat, after.Counters!.CardsPlayedCombat);
        Assert.Equal(before.Counters.CardsDrawnCombat + 5, after.Counters.CardsDrawnCombat);
        Assert.Equal(3, after.Energy);
    }

    [Fact]
    public async Task SamePublicSourceErasesPrivateOrderFutureRngAndIndistinguishableInstances()
    {
        string[] deck = ["Survivor", "Reflex", "Tactician", "Prepared", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "StrikeSilent", "Slice"];
        await using var a = await FindSource(deck, o => o.Hand.Any(c => c.Id == "Survivor") && o.UnknownDraw.Any(c => c.Count >= 2));
        await using var b = a.ForkExact();
        var pile = b.State.Players[0].PlayerCombatState!.DrawPile;
        var old = pile.Cards.ToArray();
        var replacements = old.Reverse().Select(c => { var replacement = (CardModel)c.MutableClone(); replacement.AssignOwner(b.State.Players[0]); return replacement; }).ToArray();
        Reorder(pile, replacements); b.ReseedFuture(999999);
        Assert.DoesNotContain(pile.Cards, c => old.Contains(c));
        Assert.Equal(PublicJson.Serialize(a.Observe()), PublicJson.Serialize(b.Observe()));
        await a.StepAsync(Play(a, "Survivor")); await b.StepAsync(Play(b, "Survivor"));
        string root = PublicJson.Serialize(a.Observe());
        Assert.Equal(root, PublicJson.Serialize(b.Observe()));
        for (ulong seed = 0; seed < 8; seed++)
        {
            await using var wa = await BeliefSampler.SampleWorldAsync(a, seed, 1);
            await using var wb = await BeliefSampler.SampleWorldAsync(b, seed, 1);
            Assert.Equal(root, PublicJson.Serialize(wa.Observe())); Assert.Equal(root, PublicJson.Serialize(wb.Observe()));
            Assert.Equal(Draw(wa), Draw(wb)); Assert.Equal(Streams(wa), Streams(wb));
            var action = wa.Observe().Actions.First();
            await wa.StepAsync(action); await wb.StepAsync(action);
            Assert.Equal(PublicJson.Serialize(wa.Observe()), PublicJson.Serialize(wb.Observe()));
        }
    }

    [Fact]
    public async Task KnownPhysicalReflexAndDuplicateUnknownsSurviveConditionalSampling()
    {
        string[] deck = ["ThinkingAhead", "Prepared", "Reflex", "Tactician", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "DefendSilent", "StrikeSilent", "Slice", "Survivor", "Footwork"];
        await using var s = await FindSource(deck, o => new[] { "ThinkingAhead", "Prepared", "Reflex" }.All(id => o.Hand.Any(c => c.Id == id)));
        await s.StepAsync(Play(s, "ThinkingAhead"));
        int index = Array.FindIndex(s.Observe().Observation!.Choice!.Candidates, c => c.Id == "Reflex");
        await s.StepAsync(s.Observe().Actions.Single(a => a.Selection!.SequenceEqual(new[] { index })));
        Assert.Equal("Reflex", Assert.Single(s.Observe().Observation!.KnownDraw).Card.Id);
        Assert.Contains(s.Observe().Observation!.UnknownDraw, c => c.Count > 1);
        await s.StepAsync(Play(s, "Prepared"));
        string root = PublicJson.Serialize(s.Observe());
        for (ulong seed = 0; seed < 8; seed++)
        {
            await using var world = await BeliefSampler.SampleWorldAsync(s, seed, 1);
            Assert.Equal(root, PublicJson.Serialize(world.Observe()));
            Assert.Contains(world.Observe().Observation!.Hand, c => c.Id == "Reflex");
        }
        s.Knowledge.Events.Add(new("MISSING_SUFFIX_EVENT", "test"));
        Assert.Equal(BeliefSampler.UnsupportedProfile, BeliefSampler.PosteriorProfileFor(s));
        await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(s, 42));
    }

    [Theory]
    [InlineData("CalculatedGamble")]
    [InlineData("HandTrick")]
    [InlineData("Adrenaline")]
    public async Task UnsupportedInitialCardsCannotAcquireSlyProfileAfterRemoval(string unsupported)
    {
        await using var s = await CombatSession.CreateAsync(new(Deck: ["Reflex", "Tactician", "Survivor", "StrikeSilent", unsupported]));
        foreach (var pile in s.State.Players[0].PlayerCombatState!.AllPiles)
            foreach (var card in pile.Cards.Where(c => c.GetType().Name == unsupported).ToArray()) pile.RemoveInternal(card);
        Assert.False(BeliefSampler.UsesExchangeablePosterior(s));
        await s.StepAsync(Play(s, "Survivor"));
        Assert.False(s.HasConditionalChoiceOrigin);
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, BeliefSampler.PosteriorProfileFor(s));
    }

    [Fact]
    public async Task FamilyIdentityIsInitialPriorAndExistingGuardsRemainClosed()
    {
        await using var s = await CombatSession.CreateAsync(new(Deck: ["Reflex", "Tactician", "Survivor", "StrikeSilent", "DefendSilent"]));
        // Guard invariant only: direct removal is not claimed to be naturally reachable.
        foreach (var pile in s.State.Players[0].PlayerCombatState!.AllPiles)
            foreach (var card in pile.Cards.Where(c => c.GetType().Name is "Reflex" or "Tactician").ToArray()) pile.RemoveInternal(card);
        Assert.Null(TeacherRanking.RestrictedSupport(s, ObjectiveProfile.Candidate));
        Assert.Equal(BeliefSampler.SlyExchangeableProfile, BeliefSampler.PosteriorProfileFor(s));
        await s.StepAsync(Play(s, "Survivor"));
        Assert.Equal(BeliefSampler.SlyConditionalChoiceProfile, BeliefSampler.PosteriorProfileFor(s));
        await using var clean = await CombatSession.CreateAsync(new(Deck: ["Reflex", "Tactician", "Survivor", "StrikeSilent"]));
        await using var enchanted = clean.ForkExact(); await using var afflicted = clean.ForkExact(); await using var temporary = clean.ForkExact();
        await CardCmd.Enchant<Momentum>(enchanted.State.Players[0].PlayerCombatState!.Hand.Cards.Single(c => c.GetType().Name == "StrikeSilent"), 1);
        Assert.NotNull(await CardCmd.Afflict<Bound>(afflicted.State.Players[0].PlayerCombatState!.Hand.Cards.Single(c => c.GetType().Name == "StrikeSilent"), 1));
        CardCmd.ApplySingleTurnSly(temporary.State.Players[0].PlayerCombatState!.Hand.Cards.Single(c => c.GetType().Name == "Survivor"));
        Assert.False(BeliefSampler.UsesExchangeablePosterior(enchanted)); Assert.False(BeliefSampler.UsesExchangeablePosterior(afflicted)); Assert.False(BeliefSampler.UsesExchangeablePosterior(temporary));
        Assert.DoesNotContain("Reflex", NativeBeliefCertificate.Cards); Assert.DoesNotContain("Tactician", NativeBeliefCertificate.Cards);
        await using var old = await CombatSession.CreateAsync(new(Deck: ["Prepared", "Survivor", "StrikeSilent"]));
        Assert.Equal("reviewed-stable-exchangeable-v1", BeliefSampler.PosteriorProfileFor(old));
        Assert.NotNull(TeacherRanking.RestrictedSupport(old, ObjectiveProfile.Candidate));
        await old.StepAsync(Play(old, "Survivor"));
        Assert.Equal("reviewed-stable-origin-conditional-choice-v1", BeliefSampler.PosteriorProfileFor(old));
    }

    [Fact]
    public async Task NativeCarryInCannotAcquireConstructedSlyPriorOrChoiceReplay()
    {
        await using var constructed = await CombatSession.CreateAsync(new(Deck: ["Reflex", "Tactician", "Survivor"]));
        var sly = constructed.Observe().Observation!.Hand.Single(c => c.Id == "Reflex");
        bool callbackCompleted = false;
        var report = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(new(MaxRoots: 1, SeedPrefix: "nosl-m5-natural-proof-20261001"), async (root, boundary) =>
        {
            var deck = root.PermanentDeck.Append(sly).ToArray();
            var error = Assert.Throws<NotSupportedException>(() => CombatSession.ImportNative(root with { PermanentDeck = deck },
                boundary with { InitialAssets = boundary.InitialAssets with { Deck = deck.Select(PublicJson.Serialize).Order(StringComparer.Ordinal).ToArray() } }));
            Assert.Contains("unreviewed_entry_card", error.Message);
            await using var native = CombatSession.ImportNative(root, boundary);
            Assert.Equal(NativeBeliefCertificate.Profile, BeliefSampler.PosteriorProfileFor(native));
            await native.StepAsync(Play(native, "Survivor"));
            Assert.Equal("card_choice", native.Observe().Status);
            Assert.False(native.HasConditionalChoiceOrigin);
            Assert.Equal(BeliefSampler.UnsupportedProfile, BeliefSampler.PosteriorProfileFor(native));
            await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleWorldAsync(native, 42));
            callbackCompleted = true;
        });
        // The source collector records callback exceptions; assert outside its catch.
        Assert.All(report.Runs, run => Assert.Null(run.Error));
        Assert.True(callbackCompleted);
    }

    internal static async Task<CombatSession> Diagnostic(int steps)
    {
        var s = await CombatSession.CreateAsync(DiagnosticScenario);
        foreach (var action in DiagnosticPrefix.Take(steps)) await s.StepAsync(action);
        return s;
    }
    private static async Task<CombatSession> FindSource(string[] deck, Func<PublicObservation, bool> predicate)
    {
        // Choose only on public fixture properties, never hidden order or future outcomes.
        for (int i = 0; i < 100; i++)
        {
            var s = await CombatSession.CreateAsync(new(Seed: $"sly-posterior-test:{i}", Deck: deck, Enemy: "LeafSlimeS", EnemyHp: 80));
            if (predicate(s.Observe().Observation!)) return s;
            await s.DisposeAsync();
        }
        throw new InvalidOperationException("Public test fixture not found");
    }
    private static PublicAction Play(CombatSession s, string id) => s.Observe().Actions.First(a => a.Kind == "play" && s.Observe().Observation!.Hand[a.Slot].Id == id);
    private static void Reorder(CardPile pile, CardModel[] cards)
    {
        foreach (var c in pile.Cards.ToArray()) pile.RemoveInternal(c);
        foreach (var c in cards) pile.AddInternal(c);
    }
    private static string Signature(CardModel c) => PublicJson.Serialize(PublicViews.Card(c));
    private static string Draw(CombatSession s) => string.Join("|", s.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(Signature));
    private static string Streams(CombatSession s) => JsonSerializer.Serialize(new { run = s.State.RunState.Rng.ToSerializable(), player = s.State.Players[0].PlayerRng.ToSerializable().Rngs,
        enemies = s.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray() }, new JsonSerializerOptions { IncludeFields = true });
    private static int EnergySpent(PublicEvent[] history, string id)
    {
        foreach (var e in history.Where(e => e.Kind == "card_played"))
        {
            using var json = JsonDocument.Parse(e.Detail);
            if (json.RootElement.GetProperty("card").GetProperty("id").GetString() == id) return json.RootElement.GetProperty("energySpent").GetInt32();
        }
        throw new InvalidOperationException("Card was not played");
    }
}
