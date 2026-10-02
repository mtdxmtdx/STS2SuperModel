using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Cards;

namespace Nosl.Tests;

public sealed class NativeChoiceReplayTests
{
    private static NaturalSourceOptions Cohort => new(Runs: 100, MaxFloors: 12, MaxRoots: 200,
        MaxRootsPerCombat: 8, SeedPrefix: "nosl-m5-natural-proof-20261001", SourceRunPrefix: "native-choice-regression");

    [Fact]
    public async Task ActualPendingCohortConditionsOwnedOriginsAndPreservesSourceAcrossAllCandidateSettlements()
    {
        var baseline = await NaturalSourceCollector.CollectAsync(Cohort);
        int stable = 0, pending = 0, blocked = 0, candidates = 0, settled = 0;
        bool sly = false, exactShuffle = false;
        var detached = new List<CombatSession>();
        var report = await NaturalSourceCollector.CollectWithNativeBoundaryAsync(Cohort, async (root, boundary) =>
        {
            if (boundary.IsStable)
            {
                try { await using var old = CombatSession.ImportNative(root, boundary);
                    if (!old.NativeCertificate!.HasGenerationPotionPrior) stable++; }
                catch (NotSupportedException) { }
                return;
            }
            CombatSession imported;
            try { imported = await CombatSession.ImportNativeAsync(root, boundary); }
            catch (NotSupportedException e)
            {
                Assert.Equal("native_certificate:unreviewed_encounter", e.Message);
                Assert.Contains(root.Encounter, new[] { "CorpseSlugsWeak", "TwoTailedRatsNormal" });
                blocked++; return;
            }
            await using (imported)
            {
                pending++;
                string packet = PublicJson.Serialize(root.PublicRoot);
                string sourceRng = Streams(imported), originRng = Streams(boundary.ChoiceReplay!.Origin);
                var sourceDraw = boundary.State.Players.Single().PlayerCombatState!.DrawPile.Cards.ToArray();
                Assert.True(imported.HasNativeProvenance);
                Assert.Equal(BeliefSampler.NativeConditionalChoiceProfile, BeliefSampler.PosteriorProfileFor(imported));
                Assert.Equal(packet, PublicJson.Serialize(imported.Observe()));
                Assert.NotSame(boundary.State, imported.State);
                Assert.Throws<InvalidOperationException>(() => imported.ForkExact());
                await Assert.ThrowsAsync<NotSupportedException>(() => BeliefSampler.SampleByReplayAsync(imported, 81));
                await Assert.ThrowsAsync<NotSupportedException>(() => CombatSession.ImportNativeAsync(root, boundary with { ChoiceReplay = null }));
                await Assert.ThrowsAsync<NotSupportedException>(() => CombatSession.ImportNativeAsync(root, boundary with { HistoryComplete = false }));
                var replay = boundary.ChoiceReplay;
                await Assert.ThrowsAsync<NotSupportedException>(() => CombatSession.ImportNativeAsync(root, boundary with
                    { ChoiceReplay = replay with { Packets = replay.Packets.Skip(1).ToArray() } }));
                var corrupted = replay.Packets.ToArray(); corrupted[0] += " ";
                await Assert.ThrowsAsync<NotSupportedException>(() => CombatSession.ImportNativeAsync(root, boundary with
                    { ChoiceReplay = replay with { Packets = corrupted } }));

                await using var world = await BeliefSampler.SampleWorldAsync(imported, 81, 256);
                Assert.Equal(packet, PublicJson.Serialize(world.Observe()));
                Assert.NotEqual(sourceRng, Streams(world));
                await using var fork = await world.ForkForContinuationAsync();
                Assert.Equal(Draw(world), Draw(fork)); Assert.Equal(Streams(world), Streams(fork));
                Assert.NotSame(world.State.RunState.Rng, fork.State.RunState.Rng);
                Assert.NotSame(world.Knowledge.OutcomeLedger, fork.Knowledge.OutcomeLedger);
                detached.Add(await world.ReplayToChoiceAsync()); // Survives collector and its borrowed origin.

                if (root.PublicRoot.Observation!.Choice!.Source == "Survivor")
                {
                    await using var changedOrigin = replay.Origin.ForkExact();
                    Reverse(changedOrigin.State.Players.Single().PlayerCombatState!.DrawPile);
                    Reverse(changedOrigin.State.Players.Single().Deck);
                    changedOrigin.ReseedFuture(999999);
                    await using var changed = await CombatSession.ImportNativeAsync(root with { ActualSeed = "ignored-audit-seed" },
                        boundary with { ChoiceReplay = replay with { Origin = changedOrigin } });
                    Assert.Equal(packet, PublicJson.Serialize(changed.Observe()));
                    await using var changedWorld = await BeliefSampler.SampleWorldAsync(changed, 81, 1);
                    Assert.Equal(Draw(world), Draw(changedWorld)); Assert.Equal(Streams(world), Streams(changedWorld));
                    await using var resampled = await BeliefSampler.SampleWorldAsync(world, 82, 1);
                    await using var direct = await BeliefSampler.SampleWorldAsync(imported, 82, 1);
                    Assert.Equal(Draw(direct), Draw(resampled)); Assert.Equal(Streams(direct), Streams(resampled));
                }

                var result = await CombatTeacher.EvaluateAsync(imported, new() { EvaluationSeeds = [101, 102], MaxDecisions = 200 });
                Assert.Equal(BeliefSampler.NativeConditionalChoiceProfile, result.Scope);
                Assert.Equal(root.PublicRoot.Actions.Length, result.Candidates.Length);
                Assert.All(result.Candidates, c => Assert.All(c.Outcomes, o =>
                {
                    Assert.True(o.IsTrueTerminal, o.Detail);
                    Assert.True(o.HpEventDiagnosticsComplete);
                    Assert.True(o.ResourceProvenanceComplete);
                }));
                candidates += result.Candidates.Length; settled += result.Costs.WorldsCompleted;
                Assert.Equal(result.Costs.WorldsAllocated, result.Costs.WorldsCompleted);

                if (root.Encounter == "SeapunkWeak")
                {
                    // This real native Survivor root exposes Reflex. Choosing it must
                    // execute native Sly/discard draws in the accepted world's stream.
                    int reflex = Array.FindIndex(world.Observe().Observation!.Choice!.Candidates, c => c.Id == "Reflex");
                    Assert.True(reflex >= 0);
                    var action = world.Observe().Actions.Single(a => a.Selection!.SequenceEqual(new[] { reflex }));
                    int beforeDraws = world.Observe().Observation!.History.Count(x => x.Kind == "draw");
                    await world.StepAsync(action); await fork.StepAsync(action);
                    Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
                    Assert.Equal(Streams(world), Streams(fork));
                    Assert.Equal(beforeDraws + 2, world.Observe().Observation!.History.Count(x => x.Kind == "draw"));
                    sly = true;
                }
                if (root.PublicRoot.Observation!.Choice!.Source == "Acrobatics")
                {
                    await VerifyExactNativeShufflePosterior(imported, boundary.ChoiceReplay.Origin);
                    exactShuffle = true;
                }
                Assert.Equal(packet, PublicJson.Serialize(imported.Observe()));
                Assert.Equal(sourceRng, Streams(imported));
                Assert.Equal(originRng, Streams(boundary.ChoiceReplay.Origin));
                Assert.Equal(sourceDraw, boundary.State.Players.Single().PlayerCombatState!.DrawPile.Cards);
            }
        });
        try
        {
            Assert.All(report.Runs, r => Assert.Null(r.Error));
            Assert.Equal(PublicJson.Serialize(baseline), PublicJson.Serialize(report));
            Assert.Equal(145, stable); Assert.Equal(5, pending); Assert.Equal(2, blocked);
            Assert.Equal(19, candidates); Assert.Equal(38, settled);
            Assert.True(sly); Assert.True(exactShuffle);
            foreach (var session in detached)
            {
                await Finish(session);
                Assert.True(session.Knowledge.OutcomeLedger.HpComplete);
            }
        }
        finally { foreach (var session in detached) await session.DisposeAsync(); }
    }

    private static async Task VerifyExactNativeShufflePosterior(CombatSession source, CombatSession origin)
    {
        var o = origin.Observe().Observation!;
        Assert.Equal(2, o.DrawCount); Assert.All(o.UnknownDraw, x => Assert.Equal("StrikeSilent", x.Card.Id));
        // Native Acrobatics draws the two remaining Strikes, reshuffles its public
        // discard, then observes one more card before suspending for its discard.
        var shuffle = o.Discard.GroupBy(PublicJson.Serialize)
            .Select(g => new CardCount(g.First(), g.Count())).ToArray();
        var prior = BeliefSampler.EnumerateDrawPosterior(o with
            { DrawCount = o.Discard.Length, KnownDraw = [], UnknownDraw = shuffle });
        string observed = source.Observe().Observation!.History.Last(x => x.Kind == "draw").Detail;
        var accepted = prior.Where(x => PublicJson.Serialize(x.Order[0]) == observed).ToArray();
        double mass = accepted.Sum(x => x.Probability);
        Assert.InRange(mass, .01, .99);
        var probabilities = accepted.ToDictionary(x => string.Join("|", x.Order.Skip(1).Select(PublicJson.Serialize)), x => x.Probability / mass);
        int rejected = 0; ulong? rejectedSeed = null;
        var counts = probabilities.ToDictionary(x => x.Key, _ => 0);
        for (ulong seed = 0; seed < 128; seed++)
        {
            try { await using var one = await BeliefSampler.SampleWorldAsync(source, seed, 1); }
            catch (PosteriorSamplingException e) { Assert.Equal(1, e.Attempts); rejected++; rejectedSeed ??= seed; }
            await using var sample = await BeliefSampler.SampleWorldAsync(source, seed, 256);
            Assert.Equal(PublicJson.Serialize(source.Observe()), PublicJson.Serialize(sample.Observe()));
            string order = Draw(sample); Assert.Contains(order, probabilities.Keys); counts[order]++;
            await using var branch = await sample.ReplayToChoiceAsync();
            Assert.Equal(order, Draw(branch)); Assert.Equal(Streams(sample), Streams(branch));
        }
        Assert.InRange(rejected / 128d, 1 - mass - .15, 1 - mass + .15);
        Assert.NotNull(rejectedSeed);
        var incomplete = await CombatTeacher.EvaluateAsync(source, new()
            { EvaluationSeeds = [rejectedSeed.Value], MaxPosteriorAttempts = 1 });
        Assert.Equal(source.Observe().Actions.Length, incomplete.Candidates.Length);
        Assert.Equal(0, incomplete.Costs.WorldsCompleted);
        Assert.All(incomplete.Candidates, c => Assert.All(c.Outcomes, o =>
        {
            Assert.Equal(TerminalKind.ComputeTruncated, o.TerminalKind);
            Assert.Contains("posterior_budget_exhausted", o.Detail);
            Assert.False(o.IsTrueTerminal);
        }));
        // Compare first remaining-card marginals rather than unreliable tiny-cell
        // frequencies for the many distinct whole orders in this native multiset.
        foreach (var signature in accepted.Select(x => PublicJson.Serialize(x.Order[1])).Distinct())
        {
            double expected = probabilities.Where(x => x.Key.StartsWith(signature + "|", StringComparison.Ordinal)).Sum(x => x.Value);
            double actual = counts.Where(x => x.Key.StartsWith(signature + "|", StringComparison.Ordinal)).Sum(x => x.Value) / 128d;
            Assert.InRange(actual, Math.Max(0, expected - .15), Math.Min(1, expected + .15));
        }
    }

    private static string Draw(CombatSession s) => string.Join("|", s.State.Players.Single().PlayerCombatState!.DrawPile.Cards.Select(c => PublicJson.Serialize(PublicViews.Card(c))));
    private static void Reverse(CardPile pile)
    {
        var cards = pile.Cards.Reverse().ToArray();
        foreach (var card in cards) pile.RemoveInternal(card);
        foreach (var card in cards) pile.AddInternal(card);
    }
    private static string Streams(CombatSession s) => JsonSerializer.Serialize(new
    {
        run = s.State.RunState.Rng.ToSerializable(), player = s.State.Players.Single().PlayerRng.ToSerializable().Rngs,
        monsters = s.State.Enemies.Select(x => x.Monster!.Rng.ToSerializable()).ToArray(),
    }, new JsonSerializerOptions { IncludeFields = true });
    private static async Task Finish(CombatSession s)
    {
        var policy = new PublicRulePolicy();
        for (int i = 0; i < 200 && s.Observe().Status != "terminal_settled"; i++) await s.StepAsync(policy.Choose(s.Observe()));
        Assert.Equal("terminal_settled", s.Observe().Status);
    }
}
