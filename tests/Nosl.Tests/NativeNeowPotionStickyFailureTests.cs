using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeNeowPotionStickyFailureTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Fact]
    public async Task IncompletePhialForceDisposalPoisonsOwningTapeBeforeGrantCompletionCallback()
    {
        // Fixed native source supplies only the detached public two-potion grant.
        // Stop at its first combat: later source history is irrelevant here.
        var recipe = Prior.Draw(new Rng(24001, "nosl-native-tape-source-draw-v1"))
            with { CombatIndex = 0, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        Assert.True(NativeNeowPotionCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, recipe, neowPotionCondition: condition);
        using var labels = tape.EnterScope();
        var run = new RunState(recipe.IndependentRunSeed, ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord); tape.AttachHypotheticalRun(run);
        var room = new EventRoom(() => (Neow)ModelDb.Event<Neow>().MutableClone());
        run.PushRoom(room); await room.Enter(run);

        // Let actual pickup establish the owned relic, third slot, and native
        // full-state stream; pause before either potion-generation draw begins.
        LabelPhialHolsterContext? captured = null;
        using (LabelPhialHolsterScope.Enter(context =>
        {
            captured = context; throw new PauseBeforePotionDraws();
        }))
            await Assert.ThrowsAsync<PauseBeforePotionDraws>(() => RelicCmd.Obtain(ModelDb.Relic<PhialHolster>(), player));
        Assert.NotNull(captured); Assert.Equal(3, player.PotionSlots.Count);
        Assert.All(player.PotionSlots, potion => Assert.Null(potion));
        Assert.Equal(LabelRandomProvenance.FullStatePartition, captured!.Rng.ToSerializable().LabelProvenance!.Partition);
        Assert.False(tape.HasConditionedWordFailure); Assert.Equal(0, tape.ConditionedCells);

        // Re-enter the same native seam through the tape's installed scope. This
        // preserves callback suppression and uses its actual owned proposal and
        // ForcePrefixWords, rather than a test substitute for the forcing code.
        var begin = typeof(LabelPhialHolsterScope).GetMethod("BeginGrant", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Player, Rng, IDisposable?>>();
        var boundary = Assert.IsAssignableFrom<IDisposable>(begin(player, captured.Rng));
        int before = captured.Rng.Counter;
        captured.Rng.NextFloat(); // Consume one of the four conditioned words.
        Assert.Equal(before + 1, captured.Rng.Counter); Assert.Equal(1, tape.ConditionedCells);
        Assert.False(tape.HasConditionedWordFailure);
        var original = Assert.Throws<InvalidOperationException>(boundary.Dispose);
        Assert.Contains("PhialHolster potions prefix", original.Message);
        Assert.Contains("did not consume its complete conditioned word sequence", original.Message);
        Assert.Equal(0, tape.ConditionedNeowPotions); // force.Dispose failed before complete().
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(tape.RequireSuccessfulConditionedWords).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.AcceptCorrection(() => 0)).InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy()).InnerException);
        // A later absent public observation cannot replace the original fatal error.
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.CheckPublicPrefix(null)).InnerException);
        boundary.Dispose(); // Idempotent cleanup cannot clear the sticky failure.
        Assert.True(tape.HasConditionedWordFailure); Assert.Equal(1, tape.ConditionedCells);
    }

    [Theory]
    [InlineData("native_failure")]
    [InlineData("cancellation")]
    [InlineData("public_mismatch")]
    [InlineData("callback_failure")]
    public async Task ExplicitPhialAbortPreservesOriginalExceptionAndCannotAcceptPartialGrants(string failureKind)
    {
        // Fixed native source supplies only the detached public two-potion grant.
        // Stop at its first combat: later source history is irrelevant here.
        var recipe = Prior.Draw(new Rng(24001, "nosl-native-tape-source-draw-v1"))
            with { CombatIndex = 0, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        {
            Assert.NotNull(source);
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        Assert.True(NativeNeowPotionCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, recipe, neowPotionCondition: condition);
        using var labels = tape.EnterScope();
        var run = new RunState(recipe.IndependentRunSeed, ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord); tape.AttachHypotheticalRun(run);
        var room = new EventRoom(() => (Neow)ModelDb.Event<Neow>().MutableClone());
        run.PushRoom(room); await room.Enter(run);

        // Let actual pickup establish the owned relic, third slot, and native
        // full-state stream; pause before either potion-generation draw begins.
        LabelPhialHolsterContext? captured = null;
        using (LabelPhialHolsterScope.Enter(context =>
        {
            captured = context; throw new PauseBeforePotionDraws();
        }))
            await Assert.ThrowsAsync<PauseBeforePotionDraws>(() => RelicCmd.Obtain(ModelDb.Relic<PhialHolster>(), player));
        Assert.NotNull(captured); Assert.Equal(3, player.PotionSlots.Count);
        Assert.All(player.PotionSlots, potion => Assert.Null(potion));
        Assert.Equal(LabelRandomProvenance.FullStatePartition, captured!.Rng.ToSerializable().LabelProvenance!.Partition);
        Assert.False(tape.HasConditionedWordFailure); Assert.Equal(0, tape.ConditionedCells);

        // Re-enter the same native seam through the tape's installed scope. This
        // preserves callback suppression and uses its actual owned proposal and
        // ForcePrefixWords, rather than a test substitute for the forcing code.
        var begin = typeof(LabelPhialHolsterScope).GetMethod("BeginGrant", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Player, Rng, IDisposable?>>();
        var boundary = Assert.IsAssignableFrom<IDisposable>(begin(player, captured.Rng));
        captured.Rng.NextFloat(); // One of four words; neither potion grant is complete.
        Exception original;
        if (failureKind == "callback_failure")
        {
            // Visit the next exact private state so the second forced draw fails.
            var next = captured.Rng.ToSerializable();
            var word = typeof(NativeLabelTape).GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<LabelRandomState, ulong>>(tape);
            word(new(next.state0, next.state1, next.state2, next.state3));
            original = Assert.Throws<InvalidOperationException>(() => captured.Rng.NextFloat());
        }
        else original = failureKind switch
        {
            "cancellation" => new OperationCanceledException("fixture canceled during native grant"),
            "public_mismatch" => new NativePublicConstraintMismatchException("fixture public grant mismatch"),
            _ => new InvalidDataException("fixture native grant failure"),
        };

        // Follow PhialHolster.AfterObtained's catch/abort/using cleanup order.
        var observed = Record.Exception((Action)(() =>
        {
            using (boundary)
            {
                try { throw original; }
                catch
                {
                    Assert.IsAssignableFrom<IAbortableLabelPhialHolsterBoundary>(boundary).Abort();
                    throw;
                }
            }
        }));
        Assert.Same(original, observed);
        boundary.Dispose(); // Repeated cleanup cannot introduce or clear a failure.
        Assert.Equal(0, tape.ConditionedNeowPotions);
        Assert.Equal(1, tape.ConditionedCells);
        if (failureKind == "callback_failure")
        {
            Assert.True(tape.HasConditionedWordFailure);
            Assert.Same(original, Assert.Throws<InvalidOperationException>(tape.RequireSuccessfulConditionedWords).InnerException);
            Assert.Same(original, Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy()).InnerException);
        }
        else
        {
            Assert.False(tape.HasConditionedWordFailure);
            tape.RequireSuccessfulConditionedWords();
        }
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
        Assert.Throws<InvalidOperationException>(() => tape.AcceptCorrection(() => 0));
    }

    private sealed class PauseBeforePotionDraws : Exception;
}
