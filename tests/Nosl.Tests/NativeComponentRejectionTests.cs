using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeComponentRejectionTests
{
    [Fact]
    public void FiniteWholeComponentRetriesPreserveLatentCountsAndVariableLengthMass()
    {
        int[] emitted = new int[2]; int exhausted = 0; bool sawTwo = false, sawThree = false;
        // Uniform eight-ticket candidate: C=0 succeeds with probability1/4;
        // C=1 succeeds with probability3/4. Execution uses two or three words.
        for (int first = 0; first < 8; first++)
        for (int second = 0; second < 8; second++)
        {
            int trial = 0; int[] tickets = [first, second];
            try
            {
                var result = NativeComponentRejection.Sample("finite_map", 2,
                    () => Candidate(tickets[trial++]), value => value.Match);
                emitted[result.Value.Context]++;
                sawTwo |= result.Trace.Count == 2; sawThree |= result.Trace.Count == 3;
                Assert.Equal(trial, result.Stats.CompletedTrials);
                Assert.Equal(tickets.Take(trial).Sum(ticket => Candidate(ticket).Trace.Count), result.Stats.TotalWordDraws);
            }
            catch (NativeComponentBudgetExceededException exception)
            {
                exhausted++; Assert.Equal(2, trial); Assert.Equal(2, exception.Stats.CompletedTrials);
                Assert.Equal(Candidate(first).Trace.Count + Candidate(second).Trace.Count, exception.Stats.TotalWordDraws);
            }
        }
        Assert.Equal(new[] { 12, 36 }, emitted); Assert.Equal(16, exhausted);
        Assert.True(sawTwo && sawThree);
        // Native matching masses are1/8 and3/8; successful subdensities are
        // 12/64 and36/64. Both have exactly the same p/q=2/3.
        Assert.Equal(new ShuffleRational(2, 3), new ShuffleRational(8, emitted[0]));
        Assert.Equal(new ShuffleRational(2, 3), new ShuffleRational(24, emitted[1]));
        Assert.Equal(new ShuffleRational(1, 4), new ShuffleRational(emitted[0], emitted.Sum()));
        // Conditioning each fixed C separately would instead weight emission
        // by7/16 versus15/16. A latent context must not be held unweighted.
        Assert.NotEqual(new ShuffleRational(1, 4), new ShuffleRational(7, 22));
    }

    [Fact]
    public void AliasAndNativeFailuresStopInsteadOfRetryingShorterCandidates()
    {
        int[] emitted = new int[2];
        for (int context = 0; context < 2; context++)
        for (int first = 0; first < 2; first++)
        for (int second = 0; second < 2; second++)
        {
            int calls = 0; int[] coins = [first, second];
            try
            {
                _ = NativeComponentRejection.Sample("alias_counterexample", 2, () =>
                {
                    int coin = coins[calls++];
                    if (context == 1 && coin == 0) throw new InvalidOperationException("Earlier cell reached");
                    return Trial(new FiniteValue(context, coin == 1), coin == 1 ? 1 : 2);
                }, value => value.Match);
                emitted[context]++;
            }
            catch (NativeComponentBudgetExceededException) { Assert.Equal(2, calls); }
            catch (InvalidOperationException) { Assert.Equal(1, context); Assert.Equal(1, calls); }
        }
        // Accepted traces are fresh in both contexts, yet capped retry with an
        // earlier alias gives3/4 vs1/2. This is not a constant-normalizer proof.
        Assert.Equal(new[] { 3, 2 }, emitted);
        int trials = 0;
        Assert.Throws<OperationCanceledException>(() => NativeComponentRejection.Sample<FiniteValue>("canceled", 3,
            () => { trials++; throw new OperationCanceledException(); }, _ => true));
        Assert.Equal(1, trials);
        using var canceled = new CancellationTokenSource();
        var cancellation = Assert.Throws<OperationCanceledException>(() => NativeComponentRejection.Sample("partial", 3,
            () => NativeComponentRejection.Evaluate(() =>
            {
                var rng = new Rng(20); _ = rng.NextUnsignedLong(); return rng.NextUnsignedLong();
            }, () => { canceled.Cancel(); return 1UL; }, canceled.Token), _ => true, canceled.Token));
        var partial = NativeComponentRejection.FailureStats(cancellation);
        Assert.NotNull(partial); Assert.Equal(0, partial.CompletedTrials);
        Assert.Equal(2, partial.TotalWordDraws); Assert.Equal(2, partial.TotalDistinctCells);
    }

    [Fact]
    public void DetachedTrialOracleRetainsEqualStateAliasesWithoutLeakingFailedWords()
    {
        int auxiliaryWords = 0;
        var trial = NativeComponentRejection.Evaluate(() =>
        {
            var first = new Rng(17); var clone = first.CloneExact();
            return (first.NextUnsignedLong(), clone.NextUnsignedLong());
        }, () => { auxiliaryWords++; return 1234UL; });
        Assert.Equal((1234UL, 1234UL), trial.Value);
        Assert.Equal(1, auxiliaryWords); Assert.Equal(1, trial.DistinctCells); Assert.Equal(2, trial.Trace.Count);
        Assert.Equal(trial.Trace[0], trial.Trace[1]);
        using var outer = LabelRandomScope.Enter(_ => 9876);
        Assert.Equal(9876UL, new Rng(17).NextUnsignedLong());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterTheLastWordCannotSelectACompletedTrial(bool duringPredicate)
    {
        using var canceled = new CancellationTokenSource(); int predicates = 0;
        var exception = Assert.Throws<OperationCanceledException>(() => NativeComponentRejection.Sample("late_cancel", 2,
            () => NativeComponentRejection.Evaluate(() =>
            {
                ulong result = new Rng(77).NextUnsignedLong();
                if (!duringPredicate) canceled.Cancel();
                return result;
            }, () => 456, canceled.Token), _ =>
            { predicates++; canceled.Cancel(); return true; }, canceled.Token));
        var stats = NativeComponentRejection.FailureStats(exception);
        Assert.NotNull(stats); Assert.Equal(1, stats.CompletedTrials); Assert.Equal(1, stats.TotalWordDraws);
        Assert.Equal(duringPredicate ? 1 : 0, predicates);
    }

    [Theory]
    [InlineData(9001UL, true)]
    [InlineData(9004UL, false)]
    public async Task NativeActComponentReplaysAllThreeDrawsAndPreservesTheConstructor(ulong sourceDraw, bool overgrowth)
    {
        var prior = new NativeTapePrior
        {
            EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version),
        };
        var recipe = prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); root = source.Observe(); }
        Assert.True(NativeActSelectionCondition.TryCreate(root, prior, out var condition, out var reason), reason);
        ulong successWord = overgrowth ? 0UL : ulong.MaxValue;
        // One failed complete trial followed by a successful complete trial.
        var words = new Queue<ulong>([~successWord, 12, 13, successWord, 22, 23]);
        var plan = condition!.Prepare(recipe.IndependentRunSeed, 2, () => words.Dequeue());
        Assert.Empty(words); Assert.Equal(2, plan.Stats.CompletedTrials); Assert.Equal(6, plan.Stats.TotalWordDraws);
        Assert.Equal(3, plan.Trace.Count); Assert.Equal(new[] { successWord, 22UL, 23UL }, plan.Trace.Select(word => word.Word));
        Assert.Equal(new ShuffleRational(1, 2), plan.ConditionalOutputRatio);
        Assert.Equal(new ShuffleRational(2, 3), plan.SuccessfulSubdensityRatio);
        Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("Constant correction")));
        int ordinal = 0;
        using (LabelRandomScope.Enter(state =>
        {
            Assert.Equal(plan.Trace[ordinal].State, state); return plan.Trace[ordinal++].Word;
        }))
        {
            var acts = ActDefinition.GetRandomList(recipe.IndependentRunSeed);
            Assert.Equal(overgrowth ? typeof(Overgrowth) : typeof(Underdocks), acts[0].GetType());
            Assert.IsType<Hive>(acts[1]); Assert.IsType<Glory>(acts[2]);
        }
        Assert.Equal(3, ordinal);
        var exception = Assert.Throws<NativeComponentBudgetExceededException>(() =>
            condition.Prepare(recipe.IndependentRunSeed, 2, () => ~successWord));
        Assert.Equal("act_selection", exception.Component); Assert.Equal(6, exception.Stats.TotalWordDraws);
    }

    private sealed record FiniteValue(int Context, bool Match);
    private static NativeComponentTrial<FiniteValue> Candidate(int ticket)
    {
        int context = ticket & 1, first = (ticket >> 1) & 1, second = (ticket >> 2) & 1;
        bool match = context == 0 ? first == 0 && second == 0 : first == 1 || second == 1;
        return Trial(new FiniteValue(context, match), first == 1 ? 2 : 3);
    }
    private static NativeComponentTrial<T> Trial<T>(T value, int length) =>
        new(value, Enumerable.Range(0, length).Select(index =>
            new NativeComponentWord(new((ulong)index + 1, 2, 3, 4), (ulong)index)).ToArray(), length);
}
