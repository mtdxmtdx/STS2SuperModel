using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativePrefixOuterRetryTests
{
    [Fact]
    public void FiniteNullOuterAttemptsPreserveAliasPosteriorAndDownstreamCorrection()
    {
        var single = new List<int?>();
        NativeComponentTrial<(int Seed, bool Match)> Candidate(int ticket)
        {
            int seed = ticket & 1;
            var words = new Queue<ulong>([(ulong)((ticket >> 1) & 1), (ulong)((ticket >> 2) & 1)]);
            return NativeComponentRejection.Evaluate(() =>
            {
                var act = new Rng(17); var map = seed == 0 ? act.CloneExact() : new Rng(18);
                ulong a = act.NextUnsignedLong(), m = map.NextUnsignedLong();
                return (seed, a == 0 && m == 0);
            }, () => words.Dequeue());
        }
        for (int first = 0; first < 8; first++)
        for (int second = 0; second < 8; second++)
        {
            int next = 0; int[] tickets = [first, second];
            try
            {
                var selected = NativeComponentRejection.Sample("initial_prefix", 2,
                    () => Candidate(tickets[next++]), value => value.Match);
                single.Add(selected.Value.Seed);
            }
            catch (NativeComponentBudgetExceededException error)
            { Assert.Equal(2, error.Stats.CompletedTrials); single.Add(null); }
        }
        Assert.Equal(26, single.Count(x => x == 0)); Assert.Equal(13, single.Count(x => x == 1));
        Assert.Equal(25, single.Count(x => x is null));
        int[] counts = new int[3];
        foreach (int? first in single)
        foreach (int? second in single) counts[(first ?? second) ?? 2]++;
        Assert.Equal([2314, 1157, 625], counts);
        Assert.Equal(new ShuffleRational(2, 1), new ShuffleRational(counts[0], counts[1]));

        // Downstream evidence has likelihood1/2 for A and1 for B. Force that
        // evidence, retain its exact p/q correction, and retry only null outcomes.
        // A future observable distinguishing A/B must have posterior mean1/2.
        int?[] corrected = single.SelectMany(value => Enumerable.Range(0, 2)
            .Select(coin => value == 0 && coin == 1 ? null : value)).ToArray();
        Array.Clear(counts);
        foreach (int? first in corrected)
        foreach (int? second in corrected) counts[(first ?? second) ?? 2]++;
        Assert.Equal([5304, 5304, 5776], counts);
        Assert.Equal(new ShuffleRational(1, 2), new ShuffleRational(counts[1], counts[0] + counts[1]));
    }

    [Fact]
    public async Task NativeCleanExhaustionConsumesOuterAttemptsAndIndependentWorldCanComplete()
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
        }.Freeze();
        var recipe = prior.Draw(new Rng(11001, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        var bounded = new NativeTapeReplaySource(root, prior, initialPrefixMaxTrials: 1);
        var exhausted = await Assert.ThrowsAsync<PosteriorSamplingException>(() => bounded.SampleWorldAsync(501, 2));
        Assert.Equal(2, exhausted.Attempts); Assert.Equal(2, bounded.ProposalAudit.Length);
        Assert.All(bounded.ProposalAudit, audit =>
        {
            Assert.Equal("component_budget_exhausted", audit.Status);
            Assert.Equal(1, audit.InitialPrefixStats!.CompletedTrials);
            Assert.Equal(1, audit.InitialPrefixRunSeedDraws);
            Assert.True(audit.InitialPrefixStats.TotalWordDraws > 0);
            Assert.Equal(0, audit.ConditionedTapeCells);
            Assert.Null(audit.InitialPrefixCorrection);
        });
        Assert.NotEqual(bounded.ProposalAudit[0].AuxiliaryRecipe, bounded.ProposalAudit[1].AuxiliaryRecipe);
        // Keep the same recorded root/seed/budget after adding the public act
        // predicate. Skipping wrong-act map words changes the deterministic
        // proposal stream: this fixture now accepts its first K64 outer attempt.
        // The K1 case above still proves two clean exhaustions are retried and
        // fully charged; no new successful root or seed was searched here.
        var laterRecipe = prior.Draw(new Rng(11008, "nosl-native-tape-source-draw-v1"));
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, laterRecipe,
            NativeLabelTape.ForDeclaredPrior(prior, laterRecipe)))
        { Assert.NotNull(original); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(original.Observe())); }
        var continuing = new NativeTapeReplaySource(root, prior, initialPrefixMaxTrials: 64);
        await using var world = await continuing.SampleWorldAsync(501, 16);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal("accepted", continuing.ProposalAudit[^1].Status);
        var accepted = Assert.Single(continuing.ProposalAudit);
        Assert.InRange(accepted.InitialPrefixStats!.CompletedTrials, 1, 64);
        Assert.Equal(accepted.InitialPrefixStats.CompletedTrials, accepted.InitialPrefixRunSeedDraws);
        Assert.Equal(Enumerable.Range(1, continuing.ProposalAudit.Length), continuing.ProposalAudit.Select(a => a.Attempt));
        Assert.Equal(continuing.ProposalAudit.Length, continuing.ProposalAudit.Select(a => a.AuxiliaryRecipe).Distinct().Count());
    }
}
