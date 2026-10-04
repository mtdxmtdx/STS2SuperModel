using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeEarlyPublicPrefixTests
{
    private static NativeTapePrior Hybrid => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    private static async Task<(NativeTapeRecipe Recipe, DecisionPacket Root)> DetachedRoot()
    {
        // Fixed same-recipe lifecycle fixture, not a posterior sample or new source cohort.
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 0, DecisionIndex = 0 };
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe));
        Assert.NotNull(world);
        return (recipe, PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutsideCombatContradictionPrecedesTargetCombatEntryGuard(bool map)
    {
        var (recipe, root) = await DetachedRoot();
        var original = root.PublicEvidence!;
        var entry = original.Events.First(e => map ? e.Payload is PublicMapObserved : e.Payload is PublicOptionsObserved);
        PublicEvidencePayload changed;
        if (entry.Payload is PublicMapObserved slice)
            changed = new PublicMapObserved(slice.Current, slice.Nodes.Select((node, i) => i == 0
                ? new PublicMapNode(node.Coordinate, node.NodeType == PublicMapNodeType.Monster
                    ? PublicMapNodeType.Rest : PublicMapNodeType.Monster) : node).ToImmutableArray(), slice.Edges, slice.Options);
        else
        {
            var options = (PublicOptionsObserved)entry.Payload;
            changed = new PublicOptionsObserved(options.Options.Select((option, i) => i == 0
                ? new PublicVisibleOption("different-public-option", option.IsLocked, option.Price) : option).ToImmutableArray());
        }
        // End at the altered offer, before its choice references or any combat event.
        var events = original.Events.Take((int)entry.EventOrdinal + 1).ToImmutableArray()
            .SetItem((int)entry.EventOrdinal, new(entry.EventOrdinal, entry.OwnerOrdinal, changed));
        var target = new PublicRunEvidence(PublicRunEvidence.Version, original.CompleteFromRunStart, events);
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe, expectedEntryJson: "deliberately-different-later-entry",
            expectedPublicEvidence: target);
        var error = await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe, tape));
        Assert.Equal($"Published run evidence differs at event {entry.EventOrdinal} ({changed.GetType().Name})", error.Message);
        Assert.Equal(entry.EventOrdinal, tape.PublicPrefixEventsChecked);
        Assert.Equal(0, tape.ConditionedCells);
        var onlyLaterGuard = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe,
            expectedEntryJson: "deliberately-different-later-entry");
        var later = await Assert.ThrowsAsync<NativePublicConstraintMismatchException>(() =>
            NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe, onlyLaterGuard));
        Assert.Equal("Published target combat entry differs", later.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingDetachedReplayAndContinuationPreserveBytesAfterTargetEnds(bool shortPrefix)
    {
        var (recipe, root) = await DetachedRoot();
        var original = root.PublicEvidence!;
        var target = shortPrefix ? new PublicRunEvidence(PublicRunEvidence.Version, original.CompleteFromRunStart,
            original.Events.Take(3).ToImmutableArray()) : original;
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe, expectedPublicEvidence: target);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe, tape);
        Assert.NotNull(world);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(target.Events.Length, tape.PublicPrefixEventsChecked);
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        var action = policy.Choose(root);
        await world.StepAsync(action); await fork.StepAsync(action);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.True(world.Observe().PublicEvidence!.Events.Length > original.Events.Length);
        Assert.Equal(target.Events.Length, tape.PublicPrefixEventsChecked);
    }

    [Fact]
    public void RecorderObserverSeesValidatedAppendsOnceWithoutChangingSourceBytes()
    {
        var observed = new List<PublicRunEvidenceEvent>();
        var recorder = new PublicRunEvidenceRecorder(null, observed.Add);
        var ordinary = new PublicRunEvidenceRecorder(null);
        foreach (var current in new[] { recorder, ordinary })
        {
            long owner = current.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1, completeFromOwnerStart: false);
            long offer = current.Record(owner, new PublicOptionsObserved([new("continue", false)]));
            current.Record(owner, new PublicOptionChosen(offer, "continue"));
            Assert.Throws<ArgumentException>(() => current.Record(owner, new PublicOptionChosen(offer, "continue")));
            current.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        Assert.Equal(PublicRunEvidenceJson.Serialize(ordinary.Capture()), PublicRunEvidenceJson.Serialize(recorder.Capture()));
        Assert.Equal(recorder.Capture().Events, observed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ObserverExceptionsKeepOriginalIdentityAndNeverBecomeProjectionGaps(int kind)
    {
        Exception original = kind switch
        {
            0 => new ArgumentException("observer"), 1 => new JsonException("observer"),
            2 => new NotSupportedException("observer"), 3 => new InvalidOperationException("observer"),
            4 => new FormatException("observer"), _ => new OverflowException("observer"),
        };
        var recorder = new PublicRunEvidenceRecorder(null, entry => { if (entry.EventOrdinal == 2) throw original; });
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        var thrown = Record.Exception(() => recorder.ObserveCombatHistory(owner, [new("combat_started", "Silent:A10")]));
        Assert.Same(original, thrown);
        Assert.Equal(3, recorder.Capture().Events.Length);
        Assert.IsType<PublicCombatFact>(recorder.Capture().Events[^1].Payload);
    }

    [Fact]
    public void MalformedHistoryStillProducesGapWhenObserverSucceeds()
    {
        var observed = new List<PublicRunEvidenceEvent>();
        var recorder = new PublicRunEvidenceRecorder(null, observed.Add);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
        recorder.ObserveCombatHistory(owner, [new("combat_started", "wrong-start")]);
        Assert.Equal(PublicEvidenceGapReason.UnsupportedObservation,
            Assert.IsType<PublicEvidenceGap>(recorder.Capture().Events[^1].Payload).Reason);
        Assert.Equal(recorder.Capture().Events, observed);
    }

    [Fact]
    public void InitialEventObserverFailurePropagatesWithoutReplacement()
    {
        var original = new InvalidOperationException("initial-event-observer");
        var thrown = Assert.Throws<InvalidOperationException>(() => new PublicRunEvidenceRecorder(null, _ => throw original));
        Assert.Same(original, thrown);
    }

    [Fact]
    public void StickyWordFailureWinsBeforeDetachedMismatchAndRetainsOriginalCause()
    {
        var target = new PublicRunEvidenceRecorder(null).Capture();
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, new(11, 22, 33, 0, 0), expectedPublicEvidence: target);
        var random = new Rng(4567, "early-prefix-sticky-failure");
        using (tape.EnterScope()) random.CloneExact().NextUnsignedLong();
        var force = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
        Exception original;
        using (force([1], random, "fixture"))
            original = Assert.Throws<InvalidOperationException>(() => random.NextUnsignedLong());
        var different = new PublicRunEvidenceEvent(0, null, new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted));
        var thrown = Assert.Throws<InvalidOperationException>(() => tape.ObservePublicEvidence(different));
        Assert.Same(original, thrown.InnerException);
        Assert.Equal(0, tape.PublicPrefixEventsChecked);
    }
}
