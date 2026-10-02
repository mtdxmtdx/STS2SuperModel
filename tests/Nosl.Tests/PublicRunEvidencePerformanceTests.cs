using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class PublicRunEvidencePerformanceTests
{
    private static PublicRunEvidenceRecorder Recorder() => new(new("Silent", 10,
        new(70, 70, 99, [], [], [], 3, 0, 0, 0)));
    private static PublicObservation Observation() => new("nosl.public.v2", 70, 10, 1, 70, 70,
        0, 3, 0, [], [], [], [], [], 0, [], [], [], [], [], null);
    private static PublicCombatDecision Decision(long through, bool complete) =>
        new("player_decision", Observation(), [new(0, "end_turn")], through, complete);

    [Fact]
    public void RejectedAppendsPreserveCachedCaptureOrdinalsOffersAndParentLifecycle()
    {
        var recorder = Recorder();
        long parent = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1, parent);
        var before = recorder.Capture();
        string beforeJson = PublicRunEvidenceJson.Serialize(before);
        Assert.Same(before, recorder.Capture());
        Assert.Throws<ArgumentException>(() => recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1, 99));
        Assert.Throws<ArgumentException>(() => recorder.Record(parent, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed)));
        Assert.Throws<ArgumentException>(() => recorder.Record(combat, Decision(2, false)));
        Assert.Throws<ArgumentException>(() => recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory)));
        Assert.Same(before, recorder.Capture());
        Assert.Equal(3, recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Started)));
        long decision = recorder.Record(combat, Decision(3, true));
        var offered = recorder.Capture();
        Assert.Throws<ArgumentException>(() => recorder.Record(combat, new PublicCombatActionTaken(decision, new(1, "end_turn"))));
        Assert.Throws<ArgumentException>(() => recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Interrupted)));
        Assert.Same(offered, recorder.Capture());
        Assert.Equal(5, recorder.Record(combat, new PublicCombatActionTaken(decision, new(0, "end_turn"))));
        var consumed = recorder.Capture();
        Assert.Throws<ArgumentException>(() => recorder.Record(combat, new PublicCombatActionTaken(decision, new(0, "end_turn"))));
        Assert.Same(consumed, recorder.Capture());
        recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        Assert.Throws<ArgumentException>(() => recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Shuffled)));
        long child = recorder.BeginOwner(PublicEvidenceOwnerKind.OutsideChoice, 0, 1, parent);
        Assert.Equal(2, child);
        recorder.Record(child, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        recorder.Record(parent, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        Assert.Equal(beforeJson, PublicRunEvidenceJson.Serialize(before));
        Assert.Equal(5, offered.Events.Length);
        Assert.Equal(6, consumed.Events.Length);
        var final = recorder.Capture();
        Assert.Equal(PublicRunEvidenceJson.Serialize(final), PublicRunEvidenceJson.Serialize(
            new(PublicRunEvidence.Version, final.CompleteFromRunStart, final.Events)));
    }

    [Fact]
    public void GlobalGapsAndConsumedOffersCannotMutateOtherBranchesOfAnImmutablePrefix()
    {
        var recorder = Recorder();
        long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Started));
        var prefix = recorder.Capture();
        string beforeJson = PublicRunEvidenceJson.Serialize(prefix);
        var interrupted = prefix.Append(new(3, null, new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)));
        Assert.Throws<ArgumentException>(() => interrupted.Append(new(4, combat, Decision(2, true))));
        interrupted = interrupted.Append(new(4, combat, Decision(2, false)));
        var complete = prefix.Append(new(3, combat, Decision(2, true)));
        var selected = complete.Append(new(4, combat, new PublicCombatActionTaken(3, new(0, "end_turn"))));
        Assert.Throws<ArgumentException>(() => selected.Append(new(5, combat, new PublicCombatActionTaken(3, new(0, "end_turn")))));
        var independent = complete.Append(new(4, combat, new PublicCombatActionTaken(3, new(0, "end_turn"))));
        Assert.True(complete.CompleteFromRunStart);
        Assert.False(interrupted.CompleteFromRunStart);
        Assert.Equal(PublicRunEvidenceJson.Serialize(selected), PublicRunEvidenceJson.Serialize(independent));
        Assert.Equal(beforeJson, PublicRunEvidenceJson.Serialize(prefix));
        Assert.Same(prefix, recorder.Capture());
        foreach (var branch in new[] { prefix, interrupted, complete, selected, independent })
            Assert.Equal(PublicRunEvidenceJson.Serialize(branch), PublicRunEvidenceJson.Serialize(
                new(PublicRunEvidence.Version, branch.CompleteFromRunStart, branch.Events)));
    }

    [Fact]
    public async Task EveryRealNativePrefixHasIdenticalIncrementalFullAndStrictWireValidation()
    {
        var source = await NaturalSourceCollector.CollectAsync(new(MaxFloors: 8, MaxRoots: 2, MaxRootsPerCombat: 1,
            SeedPrefix: "owned-native-opening", ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version));
        Assert.Null(Assert.Single(source.Runs).Error);
        Assert.Equal(2, source.Roots.Length);
        var native = source.Roots[^1].PublicRoot.PublicEvidence!;
        var incremental = new PublicRunEvidence(PublicRunEvidence.Version, true, [native.Events[0]]);
        var saved = new List<(PublicRunEvidence Evidence, string Json)>();
        for (int i = 1; i < native.Events.Length; i++)
        {
            var next = native.Events[i];
            Assert.Throws<ArgumentException>(() => incremental.Append(new(i + 1, next.OwnerOrdinal, next.Payload)));
            incremental = incremental.Append(next);
            var events = native.Events.Take(i + 1).ToImmutableArray();
            bool complete = events[0].Payload is PublicRunStarted
                && !events.Any(e => e.Payload is PublicEvidenceGap or PublicOwnerStarted { CompleteFromOwnerStart: false });
            string fullJson = PublicRunEvidenceJson.Serialize(new(PublicRunEvidence.Version, complete, events));
            Assert.Equal(fullJson, PublicRunEvidenceJson.Serialize(incremental));
            Assert.Equal(fullJson, PublicRunEvidenceJson.Serialize(PublicRunEvidenceJson.Read(fullJson)));
            saved.Add((incremental, fullJson));
        }
        Assert.Equal(PublicRunEvidenceJson.Serialize(native), PublicRunEvidenceJson.Serialize(incremental));
        foreach (var captured in saved)
            Assert.Equal(captured.Json, PublicRunEvidenceJson.Serialize(captured.Evidence));
    }
}
