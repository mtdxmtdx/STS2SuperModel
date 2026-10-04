using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicActPrefixTests
{
    private static NativeTapePrior Prior => NativePublicMapReconstructionTests.Prior;
    private static readonly NativeTapeRecipe Recipe = new(101, 202, 303, 0, 0);

    private static (DecisionPacket Root, NativePublicActPrefixCondition Act, NativePublicMapReconstructionCondition Map)
        Certificate(bool overgrowth = true)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-act-map-certificate", NativePublicMapReconstructionTests.Acts(overgrowth), 10);
        var evidence = NativePublicMapReconstructionTests.Evidence(run);
        var entries = evidence.Events.ToBuilder();
        void Add(PublicEvidencePayload payload) => entries.Add(new(entries.Count, 2, payload));
        Add(new PublicOwnerStarted(PublicEvidenceOwnerKind.Combat, 0, 2, null, true));
        Add(new PublicCombatFact(PublicCombatFactKind.Started));
        Add(new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
        var roster = NativeOpeningEncounterCatalog.Entries.First(e => e.ActType == run.Act.GetType()).Rosters[0];
        for (int i = 0; i < roster.Count; i++)
            Add(new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: i, model: roster[i]));
        var root = new DecisionPacket("fixture", null, [], new(PublicRunEvidence.CompleteMapVersion, true, entries.ToImmutable()));
        Assert.True(NativePublicActPrefixCondition.TryCreate(root, Prior, out var act, out var reason), reason);
        Assert.True(NativePublicMapReconstructionCondition.TryCreate(root.PublicEvidence, Prior, out var map, out reason), reason);
        return (root, act!, map!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeConstructorConsumesExactlyThreeWordsAndReconstructsZeroWordMap(bool overgrowth)
    {
        var fixture = Certificate(overgrowth);
        ulong success = overgrowth ? 0UL : ulong.MaxValue;
        var words = new Queue<ulong>([~success, 12, 13, success, 22, 23]);
        var plan = fixture.Act.Prepare(Recipe, 2, words.Dequeue);
        Assert.Empty(words); Assert.Equal(Recipe, plan.Recipe);
        Assert.Equal(new NativeComponentStats(2, 6, 6, 3), plan.Stats);
        Assert.Equal(new ShuffleRational(1, 4), plan.NullMass);
        Assert.Equal(new ShuffleRational(2, 3), plan.SuccessfulSubdensityRatio);
        var tape = Tape(plan, fixture.Map);
        RunState run;
        using (tape.EnterScope()) run = new RunState(Recipe.IndependentRunSeed, 10);
        Assert.Equal(fixture.Act.TargetActType, run.Act.GetType());
        tape.AttachHypotheticalRun(run); tape.ValidateProposalCompletion();
        Assert.Equal(3, tape.ConditionedPublicActWords); Assert.Equal(3, tape.ConditionedCells);
        Assert.Equal(0, tape.MapCells); Assert.Equal(1, tape.ReconstructedMaps);
        Assert.True(tape.AcceptCorrection(() => throw new Exception("Root-constant correction must not draw")));
        // Recreated generators revisit the same full states and retain every selected word.
        using (tape.EnterScope())
            Assert.Equal(fixture.Act.TargetActType, ActDefinition.GetRandomList(Recipe.IndependentRunSeed)[0].GetType());
        foreach (var word in plan.Trace) Assert.Equal(word.Word, Word(tape)(word.State));
        var replay = tape.ReplayCopy();
        RunState fork;
        using (replay.EnterScope()) fork = new RunState(Recipe.IndependentRunSeed, 10);
        replay.AttachHypotheticalRun(fork); replay.ValidateProposalCompletion();
        Assert.Equal(NativeMapTravelConditionTests.MapShape(run.Map), NativeMapTravelConditionTests.MapShape(fork.Map));
        Assert.Equal(3, replay.ConditionedCells); Assert.Equal(0, replay.MapCells);
    }

    [Fact]
    public void EligibilityRequiresMapReconstructionAndUnambiguousReviewedPublicOrigin()
    {
        var fixture = Certificate();
        Assert.False(NativePublicActPrefixCondition.TryCreate(fixture.Root with { PublicEvidence = null }, Prior, out _, out _));
        foreach (string schema in new[] { NativeTapePrior.Version, NativeTapePrior.RewardsVersion })
            Assert.False(NativePublicActPrefixCondition.TryCreate(fixture.Root, Prior with { SchemaVersion = schema }, out _, out _));
        var original = fixture.Root.PublicEvidence!;
        // A complete map alone contains no act certificate.
        var mapOnly = original.Events.TakeWhile(e => e.Payload is not PublicOwnerStarted { OwnerKind: PublicEvidenceOwnerKind.Combat }).ToImmutableArray();
        Assert.False(NativePublicActPrefixCondition.TryCreate(fixture.Root with
            { PublicEvidence = new(original.SchemaVersion, true, mapOnly) }, Prior, out _, out _));
        // Hide the first intent behind an explicit gap without inventing an origin.
        var gapped = original.Events.Select(e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.PlayerTurnStarted }
            ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)) : e).ToImmutableArray();
        Assert.False(NativePublicActPrefixCondition.TryCreate(fixture.Root with
            { PublicEvidence = new(original.SchemaVersion, false, gapped) }, Prior, out _, out _));
        Assert.False(NativePublicActPrefixCondition.TryCreateCertified(typeof(Overgrowth), typeof(Underdocks), null, out _, out _));
        Assert.False(NativePublicActPrefixCondition.TryCreateCertified(null, null, typeof(Hive), out _, out _));
        Assert.True(NativePublicActPrefixCondition.TryCreateCertified(null, typeof(Overgrowth), null, out _, out _));
        var plan = fixture.Act.Prepare(Recipe, 8);
        Assert.Throws<ArgumentException>(() => new NativeLabelTape(Recipe, publicActPrefixPlan: plan));
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Prior, Recipe, publicActPrefixPlan: plan));
        Assert.Throws<ArgumentException>(() => Tape(plan, fixture.Map, Recipe with { RunSeed = 404 }));
    }

    [Theory]
    [InlineData("wrong_state")]
    [InlineData("extra_word")]
    [InlineData("missing_word")]
    [InlineData("wrong_seed")]
    [InlineData("wrong_act")]
    [InlineData("early_attach")]
    [InlineData("repeated_boundary")]
    [InlineData("wrong_rng")]
    [InlineData("advanced_rng")]
    public void ConstructorInvariantFailuresStaySticky(string fault)
    {
        var fixture = Certificate();
        var plan = fixture.Act.Prepare(Recipe, 8);
        var tape = Tape(plan, fixture.Map);
        var word = Word(tape);
        if (fault == "wrong_state") Assert.Throws<InvalidOperationException>(() => word(new(0, 0, 0, 0)));
        else if (fault == "extra_word")
        {
            foreach (var item in plan.Trace) word(item.State);
            Assert.Throws<InvalidOperationException>(() => word(plan.Trace[0].State));
        }
        else if (fault == "early_attach")
            Assert.Throws<InvalidOperationException>(() => tape.AttachHypotheticalRun(new RunState("foreign", 10)));
        else
        {
            int count = fault == "missing_word" ? 2 : 3;
            foreach (var item in plan.Trace.Take(count)) word(item.State);
            var begin = typeof(NativeLabelTape).GetMethod("BeginMapGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<LabelMapGenerationContext, IDisposable?>>(tape);
            using var provenance = NativeLabelTape.ForDeclaredPrior(Prior, Recipe).EnterScope();
            using var maps = LabelMapConstructionScope.Enter(context =>
            {
                var rng = fault == "wrong_rng" ? new Rng(777) : context.Rng;
                if (fault == "advanced_rng") rng.NextUnsignedLong();
                var actual = fault == "wrong_act"
                    ? new LabelMapGenerationContext(context.Run, new Underdocks(), rng)
                    : new LabelMapGenerationContext(context.Run, context.Act, rng);
                begin(actual);
                if (fault == "repeated_boundary") begin(actual);
                return null;
            });
            Assert.Throws<InvalidOperationException>(() => new RunState(fault == "wrong_seed" ? "foreign" : Recipe.IndependentRunSeed,
                NativePublicMapReconstructionTests.Acts(true), 10));
        }
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Throws<InvalidOperationException>(tape.RequireSuccessfulConditionedWords);
        Assert.Throws<InvalidOperationException>(() => tape.ReplayCopy());
    }

    [Fact]
    public void DuplicatePlanCellsAndOverrideConflictsAreFatalAndLaterForcedAliasCannotOverwrite()
    {
        var fixture = Certificate(); var plan = fixture.Act.Prepare(Recipe, 8);
        var cells = plan.Trace.ToArray(); cells[2] = cells[0];
        Assert.Throws<InvalidOperationException>(() => new NativePublicActPrefixPlan(Recipe,
            new(typeof(Overgrowth), cells, plan.Stats, plan.MaxTrials)));
        Assert.Throws<InvalidOperationException>(() => new NativeLabelTape(Recipe,
            overrides: new Dictionary<LabelRandomState, ulong> { [plan.Trace[0].State] = ~plan.Trace[0].Word },
            rewardsOracle: new(Recipe.TapeSeed), mapOracle: new(Recipe.TapeSeed),
            mapReconstructionCondition: fixture.Map, publicActPrefixPlan: plan));
        var tape = Tape(plan, fixture.Map);
        RunState run;
        using (tape.EnterScope()) run = new RunState(Recipe.IndependentRunSeed, 10);
        tape.AttachHypotheticalRun(run);
        var state = plan.Trace[0].State;
        var snapshot = run.Rng.UpFront.ToSerializable();
        snapshot.state0 = state.State0; snapshot.state1 = state.State1;
        snapshot.state2 = state.State2; snapshot.state3 = state.State3;
        run.Rng.UpFront.LoadFromSerializable(snapshot);
        var force = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
        using (force([plan.Trace[0].Word], run.Rng.UpFront, "public event permutation"))
            Assert.Throws<InvalidOperationException>(() => run.Rng.UpFront.NextUnsignedLong());
        Assert.True(tape.HasConditionedWordFailure);
        Assert.Equal(plan.Trace[0].Word, Word(tape)(state));
        Assert.Throws<InvalidOperationException>(tape.ValidateProposalCompletion);
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(3, 2)]
    public void CompletedNativeShapeDriftRetainsMeasuredWorkAndDoesNotRetry(int words, int cells)
    {
        var condition = NativeActSelectionCondition.ForReviewedPublicAct(typeof(Overgrowth));
        int trials = 0;
        var error = Assert.Throws<InvalidOperationException>(() => condition.PrepareTrials(2, () =>
        {
            trials++;
            var trace = Enumerable.Range(0, words).Select(i => new NativeComponentWord(
                new((ulong)(i % cells), 1, 2, 3), 0)).ToArray();
            return new(NativePublicMapReconstructionTests.Acts(true), trace, cells);
        }));
        Assert.Equal(1, trials);
        Assert.Equal(new NativeComponentStats(1, words, cells, words), NativeComponentRejection.FailureStats(error));
    }

    [Fact]
    public void PartialTrialAndCancellationNeverBecomeCleanNullOutcomes()
    {
        var fixture = Certificate();
        int draws = 0;
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Act.Prepare(Recipe, 2,
            () => ++draws == 2 ? throw new InvalidOperationException("fixture word fault") : 0UL));
        Assert.Equal(2, draws);
        Assert.Equal(new NativeComponentStats(0, 2, 2, 2), NativeComponentRejection.FailureStats(error));
        using var cancel = new CancellationTokenSource(); draws = 0;
        var canceled = Assert.Throws<OperationCanceledException>(() => fixture.Act.Prepare(Recipe, 2,
            () => { if (++draws == 2) cancel.Cancel(); return 0; }, cancel.Token));
        Assert.Equal(2, draws); Assert.Equal(0, NativeComponentRejection.FailureStats(canceled)!.CompletedTrials);
    }

    private static NativeLabelTape Tape(NativePublicActPrefixPlan plan, NativePublicMapReconstructionCondition map,
        NativeTapeRecipe? recipe = null) => NativeLabelTape.ForDeclaredPrior(Prior, recipe ?? plan.Recipe,
            mapReconstructionCondition: map, publicActPrefixPlan: plan);
    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
}
