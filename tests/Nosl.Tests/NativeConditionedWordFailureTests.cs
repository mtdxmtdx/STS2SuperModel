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

    private static void MarkFinalWordFailure(NativeLabelTape tape, bool prefix = true)
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
        using (forced)
        {
            native.NextUnsignedLong();
            var original = Assert.Throws<InvalidOperationException>(() => native.NextUnsignedLong());
            Assert.Contains("alias correction is unresolved", original.Message);
            Assert.Equal(2, native.Counter); // Counter is not a success acknowledgment.
        }
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
