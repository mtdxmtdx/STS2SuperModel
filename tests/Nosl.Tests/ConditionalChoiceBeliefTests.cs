using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;

namespace Nosl.Tests;

public sealed class ConditionalChoiceBeliefTests
{
    private static readonly string[] SmallDeck = ["Prepared", "Survivor", "Acrobatics", "Backflip", "Neutralize", "StrikeSilent", "DefendSilent", "Dash", "Slice", "Footwork"];

    [Fact]
    public async Task PendingSurvivorErasesHiddenOrderAndRngAndForksOnlyItsAcceptedSampledOrigin()
    {
        await using var stable = await FindSource("Survivor", SmallDeck);
        Assert.Equal(BeliefSampler.ExchangeableProfile, BeliefSampler.PosteriorProfileFor(stable));
        await using var a = stable.ForkExact();
        await using var b = stable.ForkExact();
        var pile = b.State.Players[0].PlayerCombatState!.DrawPile;
        var reversed = pile.Cards.Reverse().ToArray();
        foreach (var card in reversed) pile.RemoveInternal(card);
        foreach (var card in reversed) pile.AddInternal(card);
        b.ReseedFuture(987654321);
        await a.StepAsync(Play(a, "Survivor")); await b.StepAsync(Play(b, "Survivor"));
        string sourcePacket = PublicJson.Serialize(a.Observe()); string sourceRandom = Streams(a);
        Assert.Equal(sourcePacket, PublicJson.Serialize(b.Observe()));
        Assert.True(BeliefSampler.UsesConditionalChoicePosterior(a));
        Assert.Equal(BeliefSampler.ConditionalChoiceProfile, BeliefSampler.PosteriorProfileFor(a));
        Assert.True(BeliefSampler.UsesConditionalChoicePosterior(b));
        var wa = await BeliefSampler.SampleWorldAsync(a, 4123, 1);
        await using var wb = await BeliefSampler.SampleWorldAsync(b, 4123, 1);
        Assert.Equal(sourcePacket, PublicJson.Serialize(wa.Observe()));
        Assert.Equal(Draw(wa), Draw(wb)); Assert.Equal(Streams(wa), Streams(wb));
        Assert.NotEqual(sourceRandom, Streams(wa));
        await using var resampled = await BeliefSampler.SampleWorldAsync(wa, 987, 1);
        await using var direct = await BeliefSampler.SampleWorldAsync(a, 987, 1);
        Assert.Equal(Draw(direct), Draw(resampled)); Assert.Equal(Streams(direct), Streams(resampled));
        Assert.Throws<InvalidOperationException>(() => wa.ForkExact()); // Coroutine still suspended.
        await using var independentWorld = await BeliefSampler.SampleWorldAsync(a, 4124, 1);
        Assert.NotEqual(Streams(wa), Streams(independentWorld));
        await using var left = await wa.ForkForContinuationAsync();
        await using var right = await wa.ReplayToChoiceAsync();
        Assert.Equal(Draw(left), Draw(right)); Assert.Equal(Streams(left), Streams(right));
        Assert.NotSame(left.State.RunState.Rng, right.State.RunState.Rng);
        Assert.NotSame(left.State.Players[0].PlayerRng, right.State.Players[0].PlayerRng);
        Assert.NotSame(left.State.Enemies[0].Monster!.Rng, right.State.Enemies[0].Monster!.Rng);
        string untouchedRight = PublicJson.Serialize(right.Observe()); string untouchedRng = Streams(right);
        await wa.DisposeAsync(); // Child origins/coroutines must survive parent disposal.
        await left.StepAsync(left.Observe().Actions[0]);
        Assert.False(left.HasConditionalChoiceOrigin);
        Assert.Equal(untouchedRight, PublicJson.Serialize(right.Observe())); Assert.Equal(untouchedRng, Streams(right));
        await right.StepAsync(right.Observe().Actions[^1]);
        Assert.False(right.HasConditionalChoiceOrigin);
        Assert.Equal(sourcePacket, PublicJson.Serialize(a.Observe())); Assert.Equal(sourceRandom, Streams(a));
        await Finish(left); await Finish(right);
    }

    [Fact]
    public async Task PreparedConditionalPosteriorMatchesExactSmallEnumerationAndRetainsRejectionMass()
    {
        await using var source = await FindSource("Prepared", SmallDeck);
        var prior = BeliefSampler.EnumerateDrawPosterior(source.Observe().Observation!);
        Assert.Equal(6, prior.Count);
        int oldDrawEvents = source.Observe().Observation!.History.Count(x => x.Kind == "draw");
        await source.StepAsync(Play(source, "Prepared"));
        Assert.Equal("card_choice", source.Observe().Status);
        var observedDraw = source.Observe().Observation!.History.Where(x => x.Kind == "draw").Skip(oldDrawEvents).Single();
        string drawnSignature = PublicJson.Serialize(PublicJson.Read<PublicCard>(observedDraw.Detail));
        var conditional = prior.Where(x => PublicJson.Serialize(x.Order[0]) == drawnSignature).ToArray();
        Assert.Equal(2, conditional.Length); Assert.Equal(1d / 3, conditional.Sum(x => x.Probability), 10);
        var possible = conditional.Select(x => string.Join("|", x.Order.Skip(1).Select(PublicJson.Serialize))).ToArray();
        var counts = possible.ToDictionary(x => x, _ => 0);
        string sourcePacket = PublicJson.Serialize(source.Observe()); string sourceRng = Streams(source);
        int rejected = 0;
        for (ulong seed = 0; seed < 240; seed++)
        {
            try { await using var one = await BeliefSampler.SampleWorldAsync(source, seed, 1); }
            catch (PosteriorSamplingException) { rejected++; }
            await using var sample = await BeliefSampler.SampleWorldAsync(source, seed, 64);
            Assert.Equal(sourcePacket, PublicJson.Serialize(sample.Observe()));
            string order = Draw(sample); Assert.Contains(order, possible); counts[order]++;
        }
        Assert.InRange(rejected, 110, 200);
        Assert.All(counts.Values, count => Assert.InRange(count / 240d, .38, .62));
        Assert.Equal(sourcePacket, PublicJson.Serialize(source.Observe())); Assert.Equal(sourceRng, Streams(source));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BeliefSampler.SampleWorldAsync(source, 1, 0));
    }

    [Fact]
    public async Task KnownPhysicalDrawPositionsAndCompleteSuffixSurviveConditionalSampling()
    {
        string[] deck = ["ThinkingAhead", "Prepared", "Survivor", "Acrobatics", "Backflip", "Neutralize", "StrikeSilent", "DefendSilent", "Dash", "Slice", "Footwork", "DeadlyPoison", "CloakAndDagger", "LegSweep"];
        await using var source = await FindSource("ThinkingAhead", deck, requiredHand: "Prepared", distinctDraw: false);
        await source.StepAsync(Play(source, "ThinkingAhead"));
        var choice = source.Observe();
        int selected = Array.FindIndex(choice.Observation!.Choice!.Candidates, c => c.Id != "Prepared");
        await source.StepAsync(choice.Actions.Single(a => a.Selection!.SequenceEqual(new[] { selected })));
        Assert.Equal("player_decision", source.Observe().Status);
        Assert.Single(source.Observe().Observation!.KnownDraw);
        var known = source.Observe().Observation!.KnownDraw[0].Card;
        await source.StepAsync(Play(source, "Prepared"));
        Assert.Equal("card_choice", source.Observe().Status);
        Assert.Contains(source.Observe().Observation!.Hand, c => PublicJson.Serialize(c) == PublicJson.Serialize(known));
        string root = PublicJson.Serialize(source.Observe());
        for (ulong seed = 0; seed < 24; seed++)
        {
            // Prepared's observed draw was known at the origin, so every proposal matches.
            await using var sample = await BeliefSampler.SampleWorldAsync(source, seed, 1);
            Assert.Equal(root, PublicJson.Serialize(sample.Observe()));
            await using var branch = await sample.ForkForContinuationAsync();
            Assert.Equal(root, PublicJson.Serialize(branch.Observe()));
        }
        source.Knowledge.Events.Add(new("MISSING_SUFFIX_EVENT", "test"));
        Assert.Throws<NotSupportedException>(() => BeliefSampler.UsesConditionalChoicePosterior(source));
    }

    [Fact]
    public async Task ChoiceTeacherKeepsAllCandidatesWhileUnreviewedInitialContentDoesNotAcquireFastProvenance()
    {
        await using var source = await FindSource("Survivor", SmallDeck);
        await source.StepAsync(Play(source, "Survivor"));
        var result = await CombatTeacher.EvaluateAsync(source, new() { EvaluationSeeds = [19, 23], MaxPosteriorAttempts = 1 });
        Assert.Equal(BeliefSampler.ConditionalChoiceProfile, result.Scope);
        using var record = JsonDocument.Parse(PublicJson.Serialize(TeacherDataset.Record(result, "run", "combat", "family", [19, 23], [])));
        Assert.Equal(BeliefSampler.ConditionalChoiceProfile, record.RootElement.GetProperty("audit_only").GetProperty("posterior_profile").GetString());
        Assert.False(record.RootElement.GetProperty("public_input").TryGetProperty("posterior_profile", out _));
        Assert.Equal(source.Observe().Actions.Length, result.Candidates.Length);
        Assert.All(result.Candidates, c => Assert.All(c.Outcomes, o => Assert.True(o.IsTrueTerminal, o.Detail)));
        Assert.All(result.Candidates, c => Assert.All(c.Outcomes, o =>
        { Assert.True(o.HpEventDiagnosticsComplete); Assert.True(o.ResourceProvenanceComplete); }));
        Assert.Equal("MASKED_NO_CERTIFIED_UTILITY_SUPPORT", result.Ranking.Method);
        await using var broad = await CombatSession.CreateAsync(new(Deck: ["Survivor", "StrikeSilent", "Adrenaline"], Potions: ["PowerPotion"]));
        await broad.StepAsync(broad.Observe().Actions.First(a => a.Kind == "discard_potion"));
        await broad.StepAsync(Play(broad, "Survivor"));
        Assert.Equal("card_choice", broad.Observe().Status);
        Assert.False(broad.HasConditionalChoiceOrigin);
        Assert.False(BeliefSampler.UsesConditionalChoicePosterior(broad));
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, BeliefSampler.PosteriorProfileFor(broad));
    }

    [Fact]
    public async Task DiscardReshuffleChoiceConditionsFutureRngAndRetainsAcceptedStateAcrossForks()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Prepared", "StrikeSilent", "DefendSilent", "Neutralize", "Slice", "Footwork", "Dash"],
            Enemy: "LeafSlimeS", EnemyHp: 80));
        await source.StepAsync(Play(source, "Neutralize"));
        await source.StepAsync(Play(source, "Slice"));
        await source.StepAsync(Play(source, "StrikeSilent"));
        var before = source.Observe().Observation!;
        Assert.Equal(0, before.DrawCount); Assert.Equal(3, before.Discard.Length);
        var shufflePrior = BeliefSampler.EnumerateDrawPosterior(before with
        { DrawCount = 3, UnknownDraw = before.Discard.Select(c => new CardCount(c, 1)).ToArray(), KnownDraw = [] });
        Assert.Equal(6, shufflePrior.Count);
        await source.StepAsync(Play(source, "Prepared"));
        var root = source.Observe(); Assert.Equal("card_choice", root.Status);
        Assert.Equal(before.History.Count(x => x.Kind == "shuffle") + 1, root.Observation!.History.Count(x => x.Kind == "shuffle"));
        string draw = root.Observation.History.Last(x => x.Kind == "draw").Detail;
        var conditional = shufflePrior.Where(x => PublicJson.Serialize(x.Order[0]) == draw).ToArray();
        Assert.Equal(2, conditional.Length);
        var counts = conditional.Select(x => string.Join("|", x.Order.Skip(1).Select(PublicJson.Serialize))).ToDictionary(x => x, _ => 0);
        for (ulong seed = 0; seed < 128; seed++)
        {
            await using var world = await BeliefSampler.SampleWorldAsync(source, seed, 64);
            Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
            string order = Draw(world); Assert.Contains(order, counts.Keys); counts[order]++;
            await using var branch = await world.ForkForContinuationAsync();
            Assert.Equal(order, Draw(branch)); Assert.Equal(Streams(world), Streams(branch));
            var selected = world.Observe().Actions[0];
            await world.StepAsync(selected); await branch.StepAsync(selected);
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(branch.Observe()));
            Assert.Equal(Streams(world), Streams(branch));
        }
        Assert.All(counts.Values, count => Assert.InRange(count / 128d, .35, .65));
    }

    [Fact]
    public async Task MultiEnemyPriorCannotBecomeReviewedAfterOnlyOneEnemyRemains()
    {
        await using var source = await CombatSession.CreateAsync(new(Deck: ["Survivor", "StrikeSilent", "DefendSilent", "Neutralize"],
            Enemies: ["TwigSlimeS", "LeafSlimeS"], EnemyHp: 1));
        Assert.False(BeliefSampler.UsesExchangeablePosterior(source));
        var strike = source.Observe().Actions.First(a => a.Kind == "play" && source.Observe().Observation!.Hand[a.Slot].Id == "StrikeSilent");
        await source.StepAsync(strike);
        Assert.Single(source.Observe().Observation!.Enemies);
        Assert.False(BeliefSampler.UsesExchangeablePosterior(source));
        await source.StepAsync(Play(source, "Survivor"));
        Assert.Equal("card_choice", source.Observe().Status);
        Assert.False(BeliefSampler.UsesConditionalChoicePosterior(source));
        Assert.False(source.HasConditionalChoiceOrigin);
        Assert.Equal(BeliefSampler.WholeSetupReplayProfile, BeliefSampler.PosteriorProfileFor(source));
    }

    private static async Task<CombatSession> FindSource(string card, string[] deck, string? requiredHand = null, bool distinctDraw = true)
    {
        // Explicit constructed test fixtures only. Selection uses public hand/multiset
        // facts, never hidden order or outcome, and creates no corpus examples.
        for (int index = 0; index < 40; index++)
        {
            var s = await CombatSession.CreateAsync(new(Seed: $"conditional-choice-test:{index}", Deck: deck, Enemy: "LeafSlimeS", EnemyHp: 80));
            var o = s.Observe().Observation!;
            if (o.Hand.Any(c => c.Id == card) && (requiredHand is null || o.Hand.Any(c => c.Id == requiredHand))
                && (!distinctDraw || o.DrawCount == 3 && o.UnknownDraw.All(x => x.Count == 1))) return s;
            await s.DisposeAsync();
        }
        throw new InvalidOperationException("Constructed public test fixture not found");
    }
    private static PublicAction Play(CombatSession s, string id) => s.Observe().Actions.First(a => a.Kind == "play" && s.Observe().Observation!.Hand[a.Slot].Id == id);
    private static string Draw(CombatSession s) => string.Join("|", s.State.Players[0].PlayerCombatState!.DrawPile.Cards.Select(c => PublicJson.Serialize(PublicViews.Card(c))));
    private static string Streams(CombatSession s) => JsonSerializer.Serialize(new { run = s.State.RunState.Rng.ToSerializable(),
        player = s.State.Players[0].PlayerRng.ToSerializable().Rngs, monsters = s.State.Enemies.Select(e => e.Monster!.Rng.ToSerializable()).ToArray() }, new JsonSerializerOptions { IncludeFields = true });
    private static async Task Finish(CombatSession s)
    {
        var policy = new PublicRulePolicy();
        for (int i = 0; i < 200 && s.Observe().Status != "terminal_settled"; i++) await s.StepAsync(policy.Choose(s.Observe()));
        Assert.Equal("terminal_settled", s.Observe().Status);
    }
}
