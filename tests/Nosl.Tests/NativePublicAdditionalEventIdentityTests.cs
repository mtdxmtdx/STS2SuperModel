using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicAdditionalEventIdentityTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData("IMMERSE,ABSTAIN", nameof(AbyssalBaths))]
    [InlineData("SOLO_QUEST,JOIN_FORCES", nameof(JungleMazeAdventure))]
    [InlineData("PLAIN,ORNATE", nameof(ThisOrThat))]
    public void OnlyCompleteOrderedUnlockedUnpricedFirstPageIdentifiesEvent(string keys, string id)
    {
        var options = keys.Split(',').Select(key => new PublicVisibleOption(key, false)).ToImmutableArray();
        Assert.Equal(id, NativeEventSelectionCertificate.Identify(new(options)));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.Reverse().ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.RemoveAt(1))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.Add(new("EXTRA", false)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(0, new(options[0].Key, true)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(options.SetItem(1, new(options[1].Key, false, 1)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new([new("LINGER", false), new("EXIT_BATHS", false)])));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TeaPagePreservesPublicLockVariantsAndRejectsInconsistentMetadata(bool boneLocked, bool emberLocked)
    {
        ImmutableArray<PublicVisibleOption> page = [new(boneLocked ? "BONE_TEA_LOCKED" : "BONE_TEA", boneLocked),
            new(emberLocked ? "EMBER_TEA_LOCKED" : "EMBER_TEA", emberLocked), new("TEA_OF_DISCOURTESY", false)];
        Assert.Equal(nameof(TeaMaster), NativeEventSelectionCertificate.Identify(new(page)));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(page.SetItem(0, new(page[0].Key, !boneLocked)))));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(page.Reverse().ToImmutableArray())));
        Assert.Null(NativeEventSelectionCertificate.Identify(new(page.SetItem(2, new("TEA_OF_DISCOURTESY", false, 1)))));
    }

    [Theory]
    [InlineData(nameof(AbyssalBaths))]
    [InlineData(nameof(JungleMazeAdventure))]
    [InlineData(nameof(ThisOrThat))]
    [InlineData(nameof(TeaMaster))]
    public void NativeInitialPageIsIndependentOfLocalRandomOutcome(string id)
    {
        NaturalSourceCollector.InitializeNativeModels();
        string? page = null;
        foreach (string seed in new[] { "event-page-a", "event-page-b", "event-page-c" })
        {
            var run = new RunState(seed, new Overgrowth());
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
            var model = (EventModel)ModelDb.All<EventModel>().Single(e => e.GetType().Name == id).MutableClone();
            model.AssignOwner(player); model.BeginEvent(run);
            var observed = new PublicOptionsObserved(model.CurrentOptions.Select(o => new PublicVisibleOption(o.Key, o.IsLocked)).ToImmutableArray());
            Assert.Equal(id, NativeEventSelectionCertificate.Identify(observed));
            string current = PublicJson.Serialize(observed); page ??= current; Assert.Equal(page, current);
        }
    }

    [Theory]
    [InlineData(24205UL, nameof(AbyssalBaths))]
    [InlineData(24207UL, nameof(JungleMazeAdventure))]
    [InlineData(24208UL, nameof(ThisOrThat))]
    public async Task NewlyInspectedRootsConditionWholeEventPermutationAndRetainExactPublicSuffix(ulong drawSeed, string id)
    {
        // These are the already completed, fully accounted v7 diagnostic sources.
        // Reproduction seeds are used only to create test evidence, never supplied to inference.
        var recipe = Prior.Draw(new Rng(drawSeed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        string original = PublicJson.Serialize(root);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.Contains(id, condition!.TargetEventIds);
        foreach (ulong independent in new ulong[] { 15347, 81093 })
        {
            var proposalRecipe = recipe with { ProposalSeed = independent };
            var plan = condition.Prepare(proposalRecipe, 4096);
            var tape = NativeLabelTape.ForDeclaredPrior(Prior, proposalRecipe,
                eventPermutationCondition: condition, eventPermutationPlan: plan, expectedPublicEvidence: root.PublicEvidence);
            await using var replay = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, proposalRecipe, tape);
            Assert.NotNull(replay); tape.ValidateProposalCompletion();
            Assert.Equal(original, PublicJson.Serialize(replay.Observe()));
            Assert.Equal(original, PublicJson.Serialize(root));
            Assert.Equal(1, tape.ConditionedEventPermutations);
            Assert.Equal(condition.TargetCount, tape.ConditionedPublicEvents);
            Assert.True(tape.AcceptCorrection(() => throw new InvalidOperationException("Whole-permutation root constant cancels")));
        }
    }
}
