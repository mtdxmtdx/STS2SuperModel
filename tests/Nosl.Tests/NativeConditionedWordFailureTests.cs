using System.Reflection;
using Nosl.Worker;
using Nosl.Contracts;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeConditionedWordFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastWordFailureCannotBeHiddenByCounterAdvancementOrNativeCatch(bool prefix)
    {
        var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        MarkFinalWordFailure(tape, prefix);
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => tape.AcceptCorrection(() => 0));
        Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
        var prefixError = Assert.Throws<InvalidOperationException>(() => tape.CheckPublicPrefix(null));
        Assert.Contains("alias correction is unresolved", prefixError.Message);
        Assert.True(tape.HasConditionedWordFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteDisposalCannotBeCaughtAndThenAcceptedOrReplayed(bool prefix)
    {
        var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var native = new Rng(4567, "incomplete-words");
        var forced = BeginWords(tape, native, prefix);
        Assert.Equal(1UL, native.NextUnsignedLong());
        var original = Assert.Throws<InvalidOperationException>(forced.Dispose);
        Assert.Contains("did not consume its complete conditioned word sequence", original.Message);
        forced.Dispose(); // Disposal is idempotent even after its first failure.
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(tape.RequireSuccessfulConditionedWords).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.AcceptCorrection(() => 0)).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy()).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.CheckPublicPrefix(null)).InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitAbortSkipsIncompleteCleanupButNeverClearsPriorCallbackFailure(bool prefix)
    {
        var clean = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var native = new Rng(4567, "aborted-words");
        var scope = Assert.IsAssignableFrom<IAbortableConditionedWordScope>(BeginWords(clean, native, prefix));
        native.NextUnsignedLong();
        scope.Abort(); scope.Dispose(); scope.Dispose();
        Assert.False(clean.HasConditionedWordFailure);
        clean.RequireSuccessfulConditionedWords();
        Assert.Equal(1, clean.ConditionedCells);

        var failed = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var original = MarkFinalWordFailure(failed, prefix, abort: true);
        Assert.True(failed.HasConditionedWordFailure);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(failed.RequireSuccessfulConditionedWords).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => failed.ReplayCopy()).InnerException);
    }

    private static IDisposable BeginWords(NativeLabelTape tape, Rng native, bool prefix) => prefix
        ? typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape)([1, 2], native, "fixture")
        : typeof(NativeLabelTape).GetMethod("ForceWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, string, IDisposable>>(tape)([1, 2], "fixture");

    [Fact]
    public void CompleteSuccessfulWordsStillPermitCorrectionAndOwnedReplay()
    {
        var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var native = new Rng(4567, "successful-prefix");
        var force = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
        using (force([1, 2], native, "fixture"))
        { Assert.Equal(1UL, native.NextUnsignedLong()); Assert.Equal(2UL, native.NextUnsignedLong()); }
        tape.ValidateProposalCompletion();
        Assert.True(tape.AcceptCorrection(() => throw new InvalidOperationException()));
        Assert.NotNull(tape.ReplayCopy());
    }

    private static Exception MarkFinalWordFailure(NativeLabelTape tape, bool prefix = true, bool abort = false)
    {
        var native = new Rng(4567, "last-word-failure");
        var snapshot = native.ToSerializable();
        var preview = new MegaRandom(snapshot);
        preview.NextULong(); preview.FillSerializableState(snapshot);
        var last = new LabelRandomState(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3);
        var word = typeof(NativeLabelTape).GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<LabelRandomState, ulong>>(tape);
        word(last); // Deliberately visit only the final hypothetical address.
        IDisposable forced = prefix
            ? typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape)([1, 2], native, "fixture")
            : typeof(NativeLabelTape).GetMethod("ForceWords", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<IReadOnlyList<ulong>, string, IDisposable>>(tape)([1, 2], "fixture");
        Exception original;
        using (forced)
        {
            native.NextUnsignedLong();
            original = Assert.Throws<InvalidOperationException>(() => native.NextUnsignedLong());
            Assert.Contains("alias correction is unresolved", original.Message);
            Assert.Equal(2, native.Counter); // Counter is not a success acknowledgment.
            if (abort) Assert.IsAssignableFrom<IAbortableConditionedWordScope>(forced).Abort();
        }
        forced.Dispose();
        return original;
    }

    private static NativeTapePrior Hybrid => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    private static async Task<DecisionPacket> DetachedRoot()
    {
        var prior = Hybrid.Freeze();
        var recipe = prior.Draw(new Rng(11001, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world);
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("mismatch")]
    [InlineData("null")]
    [InlineData("cancellation")]
    [InlineData("component_budget")]
    [InlineData("event_budget")]
    public async Task ActualSourceLoopRetainsCallbackFailureBeforeAnyLaterRejection(string later)
    {
        var root = await DetachedRoot();
        int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe recipe,
            NativeLabelTape tape, CancellationToken token)
        {
            opened++;
            MarkFinalWordFailure(tape); // Simulate native code catching its callback error.
            if (later == "prefix") tape.CheckPublicPrefix(null);
            if (later == "mismatch") throw new NativePublicConstraintMismatchException("later public mismatch");
            if (later == "cancellation") throw new OperationCanceledException("later cancellation");
            if (later == "event_budget") throw new NativeComponentBudgetExceededException("public_event_permutation", new(256, 256, 256, 1));
            if (later == "component_budget") throw new NativeComponentBudgetExceededException("initial_prefix", new(64, 64, 64, 1));
            return Task.FromResult<NativeRunWorld?>(null);
        }
        var source = new NativeTapeReplaySource(root, Hybrid, enableConditioning: false, nativeOpenerForTests: Open);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => source.SampleWorldAsync(501, 4));
        Assert.Contains("alias correction is unresolved", failure.Message);
        Assert.Equal(1, opened); // A failure cannot become a new proposal attempt.
        var audit = Assert.Single(source.ProposalAudit);
        Assert.Equal("proposal_engine_error", audit.Status);
        Assert.Contains("alias correction is unresolved", audit.Detail);
    }

    [Fact]
    public async Task RuntimeEventNamedBudgetCannotRetryOrOverwritePreparedPrefixStatistics()
    {
        var root = await DetachedRoot();
        int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe recipe,
            NativeLabelTape tape, CancellationToken token)
        {
            opened++;
            throw new NativeComponentBudgetExceededException("public_event_permutation", new(999, 999, 999, 999));
        }
        var source = new NativeTapeReplaySource(root, Hybrid, initialPrefixMaxTrials: 256, nativeOpenerForTests: Open);
        await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(501, 4));
        Assert.Equal(1, opened);
        var last = source.ProposalAudit[^1];
        Assert.Equal("component_budget_exhausted", last.Status);
        Assert.NotNull(last.InitialPrefixStats);
        Assert.InRange(last.InitialPrefixStats.CompletedTrials, 1, 256);
        Assert.NotEqual(999, last.InitialPrefixStats.TotalWordDraws);
        Assert.Null(last.EventPermutationStats);
    }

    [Fact]
    public async Task CleanEventPreparationExhaustionConsumesAttemptAndKeepsPrefixStats()
    {
        // An already observed prefix of the same fixed11007 source battle.
        var recipe = Hybrid.Draw(new Rng(11007, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 1, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe)))
        { Assert.NotNull(world); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe())); }
        int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe candidate,
            NativeLabelTape tape, CancellationToken token)
        { opened++; return Task.FromResult<NativeRunWorld?>(null); }
        var source = new NativeTapeReplaySource(root, Hybrid, initialPrefixMaxTrials: 256,
            nativeOpenerForTests: Open, eventPermutationMaxTrials: 1);
        Assert.True(source.UsesConditionalEventPermutation);
        await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(501, 4));
        Assert.Equal(4, source.ProposalAudit.Length);
        var exhausted = source.ProposalAudit.Where(a => a.EventPermutationStats is { CompletedTrials: 1 }
            && a.Status == "component_budget_exhausted").ToArray();
        Assert.NotEmpty(exhausted);
        Assert.All(exhausted, a =>
        {
            Assert.NotNull(a.InitialPrefixStats); Assert.InRange(a.InitialPrefixStats.CompletedTrials, 1, 256);
            Assert.Equal(1, a.EventPermutationMaxTrials);
            Assert.Null(a.EventPermutationCorrection); Assert.Equal(0, a.ConditionedPublicEvents);
            Assert.Contains("public_event_permutation", a.Detail);
        });
        Assert.Equal(opened, source.ProposalAudit.Count(a => a.Status == "absent_under_declared_source_horizon"));
    }

    [Fact]
    public async Task GenuineAbsentAttemptsKeepTheirOriginalUnresolvedBudgetAccounting()
    {
        var root = await DetachedRoot();
        int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe recipe,
            NativeLabelTape tape, CancellationToken token)
        { opened++; return Task.FromResult<NativeRunWorld?>(null); }
        var source = new NativeTapeReplaySource(root, Hybrid, enableConditioning: false, nativeOpenerForTests: Open);
        await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(501, 2));
        Assert.Equal(2, opened);
        Assert.Equal(2, source.ProposalAudit.Length);
        Assert.All(source.ProposalAudit, audit => Assert.Equal("absent_under_declared_source_horizon", audit.Status));
    }
}
