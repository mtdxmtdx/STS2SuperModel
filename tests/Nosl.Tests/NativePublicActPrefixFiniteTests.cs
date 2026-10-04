using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativePublicActPrefixFiniteTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactHeldSeedKernelIncludesNullMassAndRetainsAliasPosteriorAndLaterCorrection(int budget)
    {
        // Three independent fair bits are mapped to raw-word endpoints. Only
        // the first controls the two-way act choice; both singleton words stay
        // in the complete native trace. Two held seeds have the same act mass.
        int[] prior = new int[2], conditioned = new int[2], correctedPrior = new int[2], corrected = new int[2];
        int[] nulls = new int[2], successes = new int[2];
        int trialSequences = 1 << (3 * budget);
        for (int seed = 0; seed < 2; seed++)
        {
            string runSeed = $"finite-held-public-act:{seed}";
            for (int ticket = 0; ticket < 8; ticket++)
            {
                var trial = Trial(runSeed, ticket);
                for (int suffix = 0; suffix < 16; suffix++)
                {
                    if (trial.Value[0] is not Overgrowth || !Suffix(trial.Trace, seed, suffix)) continue;
                    prior[seed]++;
                    if (Correction(seed, suffix)) correctedPrior[seed]++;
                }
            }
            var emittedTraces = new Dictionary<string, int>();
            for (int sequence = 0; sequence < trialSequences; sequence++)
            {
                int index = 0;
                NativeComponentResult<IReadOnlyList<ActDefinition>> selected;
                try
                {
                    selected = NativeComponentRejection.Sample("act_selection", budget,
                        () => Trial(runSeed, (sequence >> (3 * index++)) & 7), acts => acts[0] is Overgrowth);
                }
                catch (NativeComponentBudgetExceededException error)
                {
                    nulls[seed]++;
                    Assert.Equal(new NativeComponentStats(budget, 3L * budget, 3L * budget, 3), error.Stats);
                    continue;
                }
                successes[seed]++;
                string traceKey = string.Join(",", selected.Trace.Select(w => w.Word));
                emittedTraces[traceKey] = emittedTraces.GetValueOrDefault(traceKey) + 1;
                Assert.Equal(3, selected.Trace.Count);
                var plan = new NativeActSelectionProposal(typeof(Overgrowth), selected.Trace, selected.Stats, budget);
                Assert.Equal(new ShuffleRational(1, 2), plan.ConditionalOutputRatio);
                Assert.Equal(new ShuffleRational(1 << budget, 2 * ((1 << budget) - 1)), plan.SuccessfulSubdensityRatio);
                Assert.True(plan.AcceptCorrection(() => throw new Exception("Act correction must not draw")));
                for (int suffix = 0; suffix < 16; suffix++)
                {
                    if (!Suffix(selected.Trace, seed, suffix)) continue;
                    conditioned[seed]++;
                    // A separately conditioned downstream observation has p/q
                    // 1/2 for seed0 and 1 for seed1. Preserve this correction.
                    if (Correction(seed, suffix)) corrected[seed]++;
                }
            }
            Assert.Equal(4, emittedTraces.Count);
            foreach (int count in emittedTraces.Values)
                Assert.Equal(new ShuffleRational(1 << budget, 2 * ((1 << budget) - 1)),
                    new ShuffleRational(trialSequences, 8 * count));
        }
        Assert.All(nulls, count => Assert.Equal(trialSequences >> budget, count));
        Assert.Equal(successes[0], successes[1]);
        Assert.Equal(new ShuffleRational(1, 1 << budget), new ShuffleRational(nulls[0], trialSequences));
        Assert.Equal(new ShuffleRational(2, 1), new ShuffleRational(prior[0], prior[1]));
        Assert.Equal(new ShuffleRational(prior[0], prior[1]), new ShuffleRational(conditioned[0], conditioned[1]));
        Assert.Equal(new ShuffleRational(1, 1), new ShuffleRational(correctedPrior[0], correctedPrior[1]));
        Assert.Equal(new ShuffleRational(correctedPrior[0], correctedPrior[1]), new ShuffleRational(corrected[0], corrected[1]));
        // Enumerate two independent outer outcomes: retrying the explicit null
        // atom multiplies both held-seed masses by the same finite factor.
        int none = 2 * trialSequences * 16 - corrected.Sum();
        int[] outerAccepted = [0, 0];
        int[] outcomes = Enumerable.Repeat(-1, none).Concat(Enumerable.Repeat(0, corrected[0]))
            .Concat(Enumerable.Repeat(1, corrected[1])).ToArray();
        foreach (int first in outcomes)
        foreach (int second in outcomes)
        {
            int result = first >= 0 ? first : second;
            if (result >= 0) outerAccepted[result]++;
        }
        Assert.Equal(new ShuffleRational(correctedPrior[0], correctedPrior[1]),
            new ShuffleRational(outerAccepted[0], outerAccepted[1]));
    }

    private static NativeComponentTrial<IReadOnlyList<ActDefinition>> Trial(string seed, int ticket)
    {
        int draw = 0;
        var trial = NativeComponentRejection.Evaluate(() => ActDefinition.GetRandomList(seed),
            () => ((ticket >> draw++) & 1) == 0 ? 0UL : ulong.MaxValue);
        Assert.Equal(3, draw); Assert.Equal(3, trial.DistinctCells); return trial;
    }

    private static bool Suffix(IReadOnlyList<NativeComponentWord> trace, int seed, int suffix)
    {
        // Independent Map and event components retain separate public predicates.
        if ((suffix & 2) != 0 || (suffix & 4) != 0) return false;
        var state = trace[0].State;
        var restored = new Rng(new SerializableRng
            { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 });
        var words = trace.ToDictionary(w => w.State, w => w.Word);
        ulong later;
        using (LabelRandomScope.Enter(address => words.TryGetValue(address, out var value) ? value
            : (suffix & 1) == 0 ? 0UL : ulong.MaxValue))
            later = (seed == 0 ? restored : new Rng(882733, "finite-independent-suffix")).NextUnsignedLong();
        return later == 0;
    }
    private static bool Correction(int seed, int suffix) => seed == 1 || (suffix & 8) == 0;
}
