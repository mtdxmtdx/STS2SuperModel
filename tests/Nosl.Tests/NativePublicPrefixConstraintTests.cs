using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class NativePublicPrefixConstraintTests
{
    private static PublicRunEvidenceRecorder Recorded(string key = "visible-choice")
    {
        var recorder = new PublicRunEvidenceRecorder(null);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1, completeFromOwnerStart: false);
        long offer = recorder.Record(owner, new PublicOptionsObserved([new(key, false)]));
        recorder.Record(owner, new PublicOptionChosen(offer, key));
        recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, null));
        return recorder;
    }

    [Fact]
    public void OwnedAppendOnlyPrefixCanRejectEarlyWithoutConstrainingCandidateFuture()
    {
        var recorder = Recorded(); var target = recorder.Capture();
        var constraint = new NativePublicPrefixConstraint(target);
        for (int length = 1; length <= target.Events.Length; length++)
        {
            constraint.Check(new(PublicRunEvidence.Version, false, target.Events.Take(length).ToImmutableArray()));
            Assert.Equal(length, constraint.CheckedEvents);
        }
        recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, 2);
        constraint.Check(recorder.Capture());
        Assert.Equal(target.Events.Length, constraint.CheckedEvents);
        Assert.Throws<InvalidOperationException>(() => constraint.Check(new(PublicRunEvidence.Version, false,
            target.Events.Take(1).ToImmutableArray())));
        Assert.Throws<InvalidOperationException>(() => constraint.Check(null));
    }

    [Fact]
    public void PublicContradictionIsDistinctFromRecorderFailure()
    {
        var target = Recorded().Capture();
        var constraint = new NativePublicPrefixConstraint(target);
        var error = Assert.Throws<NativePublicConstraintMismatchException>(() => constraint.Check(Recorded("other-visible-choice").Capture()));
        Assert.Contains("event 3 (PublicOptionsObserved)", error.Message);
        Assert.Equal(3, constraint.CheckedEvents);
        Assert.Equal(Recorded().Capture().Events.Length, target.Events.Length);
    }
}
