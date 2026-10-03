using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeMapEventPermutationTests
{
    private static NativeTapePrior Prior => NativePublicMapReconstructionTests.Prior;

    [Theory]
    [InlineData(24002UL, "SunkenStatue", 6L, 146L)]
    [InlineData(24007UL, "WoodCarvings", 5L, 121L)]
    [InlineData(24008UL, "RoomFullOfCheese", 6L, 134L)]
    public async Task RetainedNativeMapRootsAdmitPublicEventTargetsAndReplayTheCompletePacket(
        ulong seed, string eventId, long owner, long page)
    {
        var fixture = await Fixture(seed);
        var root = fixture.Root;
        string before = PublicJson.Serialize(root);
        // These native fixtures regenerate already retained development packets.
        // Only their detached public packet and declared prior reach extraction.
        Assert.True(NativePublicInitialMapCondition.TryCreate(root.PublicEvidence, Prior, out _, out var initialReason), initialReason);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.Equal([eventId], condition!.TargetEventIds);
        Assert.Equal([owner], condition.OwnerOrdinals);
        Assert.IsType<PublicOptionsObserved>(root.PublicEvidence!.Events[(int)page].Payload);
        Assert.Equal(owner, root.PublicEvidence.Events[(int)page].OwnerOrdinal);
        Assert.True(condition.Matches(fixture.Permutation));
        var source = new NativeTapeReplaySource(root, Prior);
        Assert.True(source.UsesConditionalEventPermutation);
        Assert.Equal(1, source.PublicEventTargets);
        Assert.True(source.UsesPublicMapReconstruction);
        Assert.False(source.UsesConditionalInitialPrefix);
        Assert.Equal(before, PublicJson.Serialize(root));

        // Identity words test native routing only, never posterior admission or
        // throughput. Production Prepare below receives an unrelated recipe.
        var identity = new NativeEventPermutationPlan(fixture.Words,
            new(1, fixture.Words.Length, fixture.Words.Length, fixture.Words.Length), 1);
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(root.PublicEvidence, Prior, out var map, out var mapReason), mapReason);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe,
            expectedPublicEvidence: root.PublicEvidence, mapReconstructionCondition: map,
            eventPermutationCondition: condition, eventPermutationPlan: identity);
        await using var replay = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, fixture.Recipe, tape);
        Assert.NotNull(replay); tape.ValidateProposalCompletion();
        Assert.Equal(before, PublicJson.Serialize(replay.Observe()));
        Assert.Equal(1, tape.ReconstructedMaps); Assert.Equal(0, tape.MapCells);
        Assert.Equal(1, tape.ConditionedEventPermutations); Assert.Equal(1, tape.ConditionedPublicEvents);
        Assert.Equal(condition.Pool.Count - 1, tape.ConditionedCells);
        Assert.True(tape.AcceptCorrection(() => throw new Exception("Fixed envelopes cancel")));
        await using var fork = await replay.ForkForContinuationAsync();
        Assert.Equal(before, PublicJson.Serialize(fork.Observe()));
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 200 && replay.Observe().Status != "terminal_settled"; i++)
        {
            Assert.Equal(PublicJson.Serialize(replay.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(replay.Observe());
            await replay.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", replay.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await replay.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        tape.ValidateProposalCompletion();

        var independent = condition.Prepare(new NativeTapeRecipe(11, 22, 33, 2, 0), 4096);
        int at = 0;
        var scratch = NativeComponentRejection.Evaluate(() =>
        {
            var pool = condition.Pool.ToList(); pool.UnstableShuffle(new Rng(932)); return pool;
        }, () => independent.RawWords[at++]);
        Assert.True(condition.Matches(scratch.Value));
        Assert.Equal(condition.Pool.Count - 1, independent.RawWords.Count);
        Assert.Equal(independent.Stats.TotalWordDraws, independent.Stats.TotalDistinctCells);
        Assert.Equal(independent.Stats.CompletedTrials * independent.RawWords.Count, independent.Stats.TotalWordDraws);
    }

    [Theory]
    [InlineData("GRAB_SWORD,DIVE_INTO_WATER", "SunkenStatue")]
    [InlineData("BIRD,SNAKE,TORUS", "WoodCarvings")]
    [InlineData("BIRD,TORUS", "WoodCarvings")]
    [InlineData("GORGE,SEARCH", "RoomFullOfCheese")]
    public void ReviewedFirstPagesRequireTheWholeOrderedSignatureAndNoPrice(string keys, string expected)
    {
        var options = keys.Split(',').Select(key => new PublicVisibleOption(key, false)).ToImmutableArray();
        Assert.Equal(expected, NativeEventSelectionCertificate.Identify(new(options)));
        Assert.Equal(expected, NativeEventSelectionCertificate.Identify(new(options.Select(o => new PublicVisibleOption(o.Key, true)).ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.Reverse().ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.Skip(1).ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(0, new(options[0].Key, false, 0)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new([new("CLAIM", false), new("SEARCH", false)])));
    }

    [Fact]
    public async Task MissingMapCaptureKeepsIndependentEventCertificateAndUnknownPageFallsBack()
    {
        var fixture = await Fixture(24007);
        var events = fixture.Root.PublicEvidence!.Events.Select(entry => entry.Payload is PublicMapObserved map
            ? new PublicRunEvidenceEvent(entry.EventOrdinal, entry.OwnerOrdinal,
                new PublicMapObserved(map.Current, map.Nodes, map.Edges, map.Options, PublicCurrentMapCapture.Missing()))
            : entry).ToImmutableArray();
        var missing = fixture.Root with { PublicEvidence = new(PublicRunEvidence.CompleteMapVersion, true, events) };
        string before = PublicJson.Serialize(missing);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(missing, Prior, out var condition, out var reason), reason);
        Assert.Equal(["WoodCarvings"], condition!.TargetEventIds);
        var source = new NativeTapeReplaySource(missing, Prior);
        Assert.True(source.UsesConditionalEventPermutation);
        Assert.False(source.UsesPublicMapReconstruction); Assert.False(source.UsesConditionalInitialPrefix);
        Assert.Equal(before, PublicJson.Serialize(missing));

        // The first unidentified event disables the whole certificate. It cannot
        // silently skip a pull and constrain a later ID against the wrong cursor.
        int firstPage = 121;
        events = events.Take(firstPage + 1).ToImmutableArray().SetItem(firstPage,
            new(firstPage, events[firstPage].OwnerOrdinal, new PublicOptionsObserved([new("FUTURE_OPTION", false)])));
        var unsupported = fixture.Root with { PublicEvidence = new(PublicRunEvidence.CompleteMapVersion, true, events) };
        Assert.False(NativePublicEventPermutationCondition.TryCreate(unsupported, Prior, out _, out reason));
        Assert.Equal("certified_public_event_prefix_required", reason);
    }

    [Fact]
    public async Task MapSourceCleanFixedKExhaustionConsumesOnlyCompleteOuterAttempts()
    {
        var fixture = await Fixture(24007);
        int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe recipe, NativeLabelTape tape, CancellationToken token)
        { opened++; return Task.FromResult<NativeRunWorld?>(null); }
        var source = new NativeTapeReplaySource(fixture.Root, Prior, nativeOpenerForTests: Open, eventPermutationMaxTrials: 1);
        await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(501, 4));
        Assert.Equal(4, source.ProposalAudit.Length);
        var exhausted = source.ProposalAudit.Where(a => a.Status == "component_budget_exhausted").ToArray();
        Assert.NotEmpty(exhausted);
        Assert.All(exhausted, audit =>
        {
            Assert.Equal(1, audit.EventPermutationMaxTrials);
            Assert.Equal(new NativeComponentStats(1, 30, 30, 30), audit.EventPermutationStats);
            Assert.Null(audit.InitialPrefixStats); Assert.Null(audit.InitialPrefixCorrection);
            Assert.Null(audit.EventPermutationCorrection); Assert.Equal(0, audit.ConditionedPublicEvents);
            Assert.Equal(0, audit.MapTapeCells); Assert.Equal(0, audit.ReconstructedMaps);
            Assert.Contains("public_event_permutation", audit.Detail);
        });
        Assert.Equal(opened, source.ProposalAudit.Count(a => a.Status == "absent_under_declared_source_horizon"));
        Assert.Equal(4 - opened, exhausted.Length);
    }

    [Theory]
    [InlineData("native_failure")]
    [InlineData("cancellation")]
    [InlineData("runtime_budget")]
    public async Task MapSourceNeverRetriesRuntimeFailureAsCleanEventPreparationExhaustion(string failure)
    {
        var fixture = await Fixture(24007);
        Exception original = failure switch
        {
            "cancellation" => new OperationCanceledException("native event replay cancelled"),
            "runtime_budget" => new NativeComponentBudgetExceededException("public_event_permutation", new(999, 999, 999, 999)),
            _ => new FormatException("native event replay failed"),
        };
        int opened = 0;
        Task<NativeRunWorld?> Open(NativeRunExecutionOptions _, NativeTapeRecipe recipe, NativeLabelTape tape, CancellationToken token)
        { opened++; throw original; }
        var source = new NativeTapeReplaySource(fixture.Root, Prior, nativeOpenerForTests: Open, eventPermutationMaxTrials: 256);
        var error = await Record.ExceptionAsync(() => source.SampleWorldAsync(501, 4));
        if (failure == "runtime_budget") Assert.IsType<PosteriorSamplingException>(error);
        else Assert.Same(original, error);
        Assert.Equal(1, opened);
        var audit = Assert.Single(source.ProposalAudit);
        Assert.Equal(failure switch { "cancellation" => "computation_cancelled", "runtime_budget" => "component_budget_exhausted",
            _ => "proposal_engine_error" }, audit.Status);
        Assert.NotNull(audit.EventPermutationStats);
        Assert.InRange(audit.EventPermutationStats.CompletedTrials, 1, 256);
        Assert.Equal(audit.EventPermutationStats.CompletedTrials * 30, audit.EventPermutationStats.TotalWordDraws);
        Assert.Equal(NativeEventPermutationPlan.CorrectionClaim, audit.EventPermutationCorrection);
        Assert.Null(audit.InitialPrefixStats);
        Assert.Equal(0, audit.ConditionedPublicEvents);
    }

    private sealed record NativeFixture(DecisionPacket Root, NativeTapeRecipe Recipe, Type[] Permutation, ulong[] Words);
    private static async Task<NativeFixture> Fixture(ulong seed)
    {
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var captureTape = NativeLabelTape.ForDeclaredPrior(Prior, recipe);
        var word = typeof(NativeLabelTape).GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<LabelRandomState, ulong>>(captureTape);
        Type[]? permutation = null; ulong[]? words = null;
        using var scope = LabelEventGenerationScope.Enter(context =>
        {
            if (context.ActIndex != 0) return null;
            words = new ulong[context.Pool.Count - 1];
            var clone = new Rng(context.Rng.ToSerializable());
            for (int i = 0; i < words.Length; i++)
            {
                var state = clone.ToSerializable();
                words[i] = word(new(state.state0, state.state1, state.state2, state.state3));
                clone.NextUnsignedLong();
            }
            return new OnDispose(() => permutation = context.CompletedPermutation!.ToArray());
        });
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world); Assert.NotNull(permutation); Assert.NotNull(words);
        return new(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe())), recipe, permutation, words);
    }
    private sealed class OnDispose(Action action) : IDisposable { public void Dispose() => action(); }
}
