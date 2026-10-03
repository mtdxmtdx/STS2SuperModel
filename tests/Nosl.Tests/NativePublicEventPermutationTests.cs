using System.Collections.Immutable;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicEventPermutationTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Fact]
    public void ObservingTypedBoundaryPreservesOrdinaryNativeGenerationAndEventSelection()
    {
        NaturalSourceCollector.InitializeNativeModels();
        RunState Fresh()
        {
            var run = new RunState("ordinary-event-boundary", [new Overgrowth(), new Hive(), new Glory()], ascensionLevel: 10);
            run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
            run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord); return run;
        }
        var ordinary = Fresh();
        var expected = Enumerable.Range(0, 64).Select(_ => ordinary.PullNextEvent()).ToArray();
        var observed = Fresh(); var contexts = new List<LabelEventGenerationContext>();
        using (LabelEventGenerationScope.Enter(context => { contexts.Add(context); return null; }))
            Assert.Equal(expected, Enumerable.Range(0, 64).Select(_ => observed.PullNextEvent()).ToArray());
        Assert.Equal(3, contexts.Count);
        Assert.All(contexts, context =>
        {
            Assert.Same(observed, context.Run);
            Assert.NotNull(context.CompletedPermutation);
            Assert.Equal(context.Pool.OrderBy(type => type.Name), context.CompletedPermutation.OrderBy(type => type.Name));
        });
        Assert.Equal(PublicJson.Serialize(ordinary.Rng.UpFront.ToSerializable()),
            PublicJson.Serialize(observed.Rng.UpFront.ToSerializable()));
    }

    [Fact]
    public void FiniteScannerContainsEveryHiddenEligibilityPathIncludingAmbiguityWrapAndRevisits()
    {
        const ulong natural = 0b1011; // Entry 2 never appears, even in fallback.
        ulong[] must = [0b0001, 0, 0b0010], may = [0b1001, 0b1011, 0b1010];
        ulong[] candidates = [1, 2, 3, 8]; // 3 is an ambiguous public signature.
        int contained = 0, excluded = 0, revisits = 0;
        foreach (int[] permutation in Permutations([0, 1, 2, 3]))
        foreach (int cursor in new[] { 0, 1, 5 })
        foreach (ulong visited in new ulong[] { 0, 3, natural })
        {
            var possible = new HashSet<string>();
            for (ulong a = 0; a < 16; a++)
            for (ulong b = 0; b < 16; b++)
            for (ulong c = 0; c < 16; c++)
            {
                ulong[] eligibility = [a, b, c];
                if (Enumerable.Range(0, 3).Any(i => (eligibility[i] & must[i]) != must[i]
                        || (eligibility[i] & ~may[i]) != 0)) continue;
                var sequence = NativeSelections(permutation, natural, eligibility, cursor, visited);
                if (sequence.Distinct().Count() < sequence.Length) revisits++;
                possible.Add(string.Join(',', sequence));
            }
            foreach (ulong first in candidates)
            foreach (ulong second in candidates)
            foreach (ulong third in candidates)
            {
                ulong[] masks = [first, second, third];
                var observations = Enumerable.Range(0, 3)
                    .Select(i => new NativeEventScanObservation(masks[i], must[i], may[i])).ToArray();
                bool exactExistential = possible.Any(sequence => sequence.Split(',').Select(int.Parse)
                    .Select((id, i) => (masks[i] & (1UL << id)) != 0).All(match => match));
                Assert.Equal(exactExistential, NativeEventScanSuperset.Matches(permutation, natural,
                    observations, cursor, visited));
                if (exactExistential) contained++; else excluded++;
            }
        }
        Assert.True(contained > 0 && excluded > 0 && revisits > 0);
    }

    [Fact]
    public void WholeNativeShuffleRejectionRetainsUnequalBucketMassAndRootConstantCorrection()
    {
        var trials = new List<NativeComponentTrial<int[]>>();
        for (ulong a = 0; a < 4; a++)
        for (ulong b = 0; b < 4; b++)
        {
            int position = 0; ulong[] words = [a << 62, b << 62];
            trials.Add(NativeComponentRejection.Evaluate(() =>
            {
                var pool = new List<int> { 0, 1, 2 };
                pool.UnstableShuffle(new Rng(31)); return pool.ToArray();
            }, () => words[position++]));
        }
        bool Matches(int[] permutation) => NativeEventScanSuperset.Matches(permutation, 7,
            [new(2, 7, 7)], initialCursor: 1);
        int failures = trials.Count(trial => !Matches(trial.Value));
        var native = trials.Where(trial => Matches(trial.Value)).GroupBy(t => string.Join(',', t.Value))
            .ToDictionary(g => g.Key, g => g.Count());
        var emitted = new Dictionary<string, int>(); int exhausted = 0;
        foreach (var first in trials)
        foreach (var second in trials)
        {
            int index = 0; NativeComponentTrial<int[]>[] attempts = [first, second];
            try
            {
                var result = NativeComponentRejection.Sample("finite_event_permutation", 2,
                    () => attempts[index++], Matches);
                string key = string.Join(',', result.Value);
                emitted[key] = emitted.GetValueOrDefault(key) + 1;
                Assert.Equal(index * 2, result.Stats.TotalWordDraws);
            }
            catch (NativeComponentBudgetExceededException) { exhausted++; }
        }
        Assert.Equal(failures * failures, exhausted);
        Assert.True(native.Values.Distinct().Count() > 1); // Native floor buckets are unequal.
        foreach (var (key, mass) in native)
            Assert.Equal(mass * (16 + failures), emitted[key]);
        var plan = new NativeEventPermutationPlan([3, 7], new(2, 4, 4, 2), 2);
        Assert.True(plan.AcceptCorrection(() => throw new Exception("Constant envelope must not draw")));
        Assert.Contains("root-constant", NativeEventPermutationPlan.CorrectionClaim);
    }

    [Theory]
    [InlineData(11004UL, "SelfHelpBook,WaterloggedScriptorium")]
    [InlineData(11007UL, "TabletOfTruth")]
    public async Task ExistingNativeRootsUseOneWholePermutationAndReplayEveryPublicObservation(ulong seed, string ids)
    {
        var fixture = await Fixture(seed);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(fixture.Root));
        string before = PublicJson.Serialize(root);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.Equal(ids.Split(','), condition!.TargetEventIds);
        Assert.True(condition.Matches(fixture.Permutation));
        Assert.Equal(before, PublicJson.Serialize(root));

        // Identity replay isolates typed-boundary routing from posterior acceptance.
        // Source words are test evidence only; production Prepare uses an independent RNG.
        var identity = new NativeEventPermutationPlan(fixture.Words,
            new(1, fixture.Words.Length, fixture.Words.Length, fixture.Words.Length), 1);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe);
        var proposal = new NativePublicEventPermutationProposal(condition, identity, Force(tape));
        using var scope = LabelEventGenerationScope.Enter(context =>
        {
            if (context.ActIndex != 0) return proposal.BeginGeneration(context);
            proposal.AttachHypotheticalRun(context.Run);
            var boundary = proposal.BeginGeneration(context);
            return new OnDispose(() =>
            {
                boundary!.Dispose();
                // A new proposal cannot reuse a completed native certificate,
                // even while this same run still satisfies fresh setup checks.
                var stale = new NativePublicEventPermutationProposal(condition, identity, Force(tape));
                stale.AttachHypotheticalRun(context.Run);
                Assert.Throws<InvalidOperationException>(() => stale.BeginGeneration(context));
            });
        });
        await using var replay = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, fixture.Recipe, tape);
        Assert.NotNull(replay); proposal.ValidateCompletion();
        Assert.Equal(condition.Pool.Count - 1, tape.ConditionedCells);
        Assert.Equal(condition.TargetCount, proposal.ConditionedEventCount);
        Assert.Equal(1, proposal.ConditionedShuffleCount);
        Assert.True(proposal.AcceptCorrection(() => throw new Exception("No correction word expected")));
        Assert.Equal(before, PublicJson.Serialize(replay.Observe()));

        var independent = condition.Prepare(fixture.Recipe with { ProposalSeed = fixture.Recipe.ProposalSeed ^ 71267 }, maxTrials: 4096);
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

    [Fact]
    public async Task CertificateStopsBeforeAmbiguousOrModifiedEvidenceAndKeepsHiddenEligibilityUnknown()
    {
        var fixture = await Fixture(11004);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(fixture.Root, Prior, out var condition, out _));
        var events = fixture.Root.PublicEvidence!.Events.ToArray();
        int second = Array.FindIndex(events, entry => entry.OwnerOrdinal == condition!.OwnerOrdinals[1]
            && entry.Payload is PublicOptionsObserved);
        events[second] = new(events[second].EventOrdinal, events[second].OwnerOrdinal,
            new PublicOptionsObserved([new("NO_OPTIONS", false)]));
        // Stop the evidence at this page so subsequent choices need no mutation.
        var prefix = new PublicRunEvidence(PublicRunEvidence.Version, true, events.Take(second + 1).ToImmutableArray());
        Assert.True(NativePublicEventPermutationCondition.TryCreate(fixture.Root with { PublicEvidence = prefix },
            Prior, out var retained, out _));
        Assert.Equal(["SelfHelpBook"], retained!.TargetEventIds);
        Assert.Null(NativeEventSelectionCertificate.Identify(new([new("NO_OPTIONS", false)])));
        Assert.Null(NativeEventSelectionCertificate.Identify(new([new("DECIPHER", false), new("SMASH", false, 1)])));
        Assert.False(NativePublicEventPermutationCondition.TryCreate(fixture.Root with { PublicEvidence = null }, Prior, out _, out _));
        Assert.False(NativePublicEventPermutationCondition.TryCreate(fixture.Root,
            Prior with { SchemaVersion = NativeTapePrior.Version }, out _, out _));

        var assets = ((PublicRunStarted)events[0].Payload).Assets;
        var rich = new PublicEvidenceAssets(assets.Hp, assets.MaxHp, 149, assets.Deck, assets.Relics,
            assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
        var poor = new PublicEvidenceAssets(assets.Hp, assets.MaxHp, 148, assets.Deck, assets.Relics,
            assets.Potions, assets.MaxEnergy, assets.PotionSlots, assets.OrbSlots, assets.CardRemovalsUsed);
        var pool = NativeEventSelectionCertificate.Pool(new Overgrowth());
        ulong choir = 1UL << Array.FindIndex(pool, type => type.Name == "LuminousChoir");
        var possible = NativeEventSelectionCertificate.Eligibility(pool, rich, 6);
        Assert.Equal(0UL, possible.Must & choir); Assert.Equal(choir, possible.May & choir);
        Assert.Equal(0UL, NativeEventSelectionCertificate.Eligibility(pool, poor, 6).May & choir);
    }

    [Fact]
    public async Task OwnedBoundaryRejectsForeignPoolReuseSkippedWordsAliasesAndFalseCompletion()
    {
        var fixture = await Fixture(11007);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(fixture.Root, Prior, out var condition, out _));
        var plan = condition!.Prepare(fixture.Recipe, maxTrials: 4096);
        RunState Fresh()
        {
            var run = new RunState("event-permutation-guard", [new Overgrowth(), new Hive(), new Glory()], ascensionLevel: 10);
            run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
            run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord); return run;
        }
        LabelEventGenerationContext Context(RunState run) => new(run, run.Act, 0, run.Rng.UpFront, condition.Pool);
        var owned = Fresh();
        NativePublicEventPermutationProposal New(NativeLabelTape tape) => new(condition, plan, Force(tape));
        var proposal = New(new(new(31, 41, 59, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(Context(owned)));
        proposal.AttachHypotheticalRun(owned);
        Assert.Throws<InvalidOperationException>(() => proposal.AttachHypotheticalRun(owned));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(Context(Fresh())));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(owned, owned.Act, 0, new Rng(1), condition.Pool)));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(owned, owned.Act, 0, owned.Rng.UpFront,
            condition.Pool.Reverse().ToArray())));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(Context(owned))!.Dispose());
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(Context(owned)));

        var fakeRun = Fresh(); var fakeTape = new NativeLabelTape(new(32, 42, 60, 0, 0));
        var fake = New(fakeTape); fake.AttachHypotheticalRun(fakeRun);
        {
            using var boundary = fake.BeginGeneration(Context(fakeRun));
            for (int i = 0; i < plan.RawWords.Count; i++) fakeRun.Rng.UpFront.NextUnsignedLong();
        }
        Assert.Throws<InvalidOperationException>(fake.ValidateCompletion);

        var aliasRun = Fresh(); var aliasTape = new NativeLabelTape(new(33, 43, 61, 0, 0));
        var snapshot = aliasRun.Rng.UpFront.ToSerializable();
        _ = Word(aliasTape)(new(snapshot.state0, snapshot.state1, snapshot.state2, snapshot.state3));
        var alias = New(aliasTape); alias.AttachHypotheticalRun(aliasRun);
        {
            using var boundary = alias.BeginGeneration(Context(aliasRun));
            Assert.Throws<InvalidOperationException>(() => aliasRun.Rng.UpFront.NextUnsignedLong());
        }
        Assert.Throws<InvalidOperationException>(alias.ValidateCompletion);

        var abortedRun = Fresh();
        var original = new FormatException("Native failure after all shuffle words");
        int consumed = 0;
        var aborted = new NativePublicEventPermutationProposal(condition, plan,
            (words, _, _) => LabelRandomScope.Enter(_ => words[consumed++],
                beginShuffle: (_, _) => new OnDispose(() => throw original)));
        aborted.AttachHypotheticalRun(abortedRun);
        using (LabelEventGenerationScope.Enter(aborted.BeginGeneration))
        {
            Assert.Same(original, Assert.Throws<FormatException>(() => _ = abortedRun.CurrentAncientEventType));
        }
        Assert.Equal(plan.RawWords.Count, consumed);
        Assert.Throws<InvalidOperationException>(aborted.ValidateCompletion);
        Assert.Equal(0, aborted.ConditionedShuffleCount);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => condition.Prepare(fixture.Recipe, maxTrials: 4096, cancellationToken: canceled.Token));
    }

    [Theory]
    [InlineData(11004UL)]
    [InlineData(11007UL)]
    public async Task OwningTapeComposesEventAndEncounterPlansThroughExactContinuation(ulong seed)
    {
        var fixture = await Fixture(seed);
        Assert.True(NativePublicEventPermutationCondition.TryCreate(fixture.Root, Prior, out var condition, out _));
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(fixture.Root, Prior, out var encounters, out _));
        var source = new NativeTapeReplaySource(fixture.Root, Prior);
        Assert.True(source.UsesConditionalEventPermutation);
        Assert.Equal(condition!.TargetCount, source.PublicEventTargets);
        // Identity words isolate lifecycle composition only; they are never an inference input.
        var identity = new NativeEventPermutationPlan(fixture.Words,
            new(1, fixture.Words.Length, fixture.Words.Length, fixture.Words.Length), 1);
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe,
            eventPermutationCondition: condition));
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe,
            eventPermutationPlan: identity));
        Assert.Throws<ArgumentException>(() => NativeLabelTape.ForDeclaredPrior(
            Prior with { SchemaVersion = NativeTapePrior.Version }, fixture.Recipe,
            eventPermutationCondition: condition, eventPermutationPlan: identity));
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, fixture.Recipe,
            expectedPublicEvidence: fixture.Root.PublicEvidence, weakEncounterCondition: encounters,
            eventPermutationCondition: condition, eventPermutationPlan: identity);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, fixture.Recipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.Equal(PublicJson.Serialize(fixture.Root), PublicJson.Serialize(world.Observe()));
        Assert.Equal(1, tape.ConditionedEventPermutations);
        Assert.Equal(condition.TargetCount, tape.ConditionedPublicEvents);
        Assert.Equal(encounters!.Targets.Count, tape.ConditionedWeakEncounters);
        Assert.True(tape.AcceptCorrection(() => throw new Exception("Both fixed envelopes cancel")));
        int conditioned = tape.ConditionedCells;
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(conditioned, tape.ConditionedCells); tape.ValidateProposalCompletion();
    }

    private sealed record NativeFixture(DecisionPacket Root, NativeTapeRecipe Recipe, Type[] Permutation, ulong[] Words);
    private static async Task<NativeFixture> Fixture(ulong seed)
    {
        var recipe = Prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        var captureTape = NativeLabelTape.ForDeclaredPrior(Prior, recipe);
        Type[]? permutation = null; ulong[]? words = null;
        using var scope = LabelEventGenerationScope.Enter(context =>
        {
            if (context.ActIndex != 0) return null;
            words = new ulong[context.Pool.Count - 1];
            var clone = new Rng(context.Rng.ToSerializable());
            for (int i = 0; i < words.Length; i++)
            {
                var state = clone.ToSerializable();
                words[i] = Word(captureTape)(new(state.state0, state.state1, state.state2, state.state3));
                clone.NextUnsignedLong();
            }
            return new OnDispose(() => permutation = context.CompletedPermutation!.ToArray());
        });
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        Assert.NotNull(world); Assert.NotNull(permutation); Assert.NotNull(words);
        return new(world.Observe(), recipe, permutation, words);
    }

    // Independent literal counter loop from the native selection contract.
    private static int[] NativeSelections(int[] pool, ulong natural, ulong[] eligibility, int cursor, ulong visited)
    {
        var result = new List<int>();
        foreach (ulong allowed in eligibility)
        {
            int selected = -1;
            for (int tries = 0; tries < pool.Length; tries++)
            {
                int id = pool[cursor % pool.Length];
                ulong bit = 1UL << id;
                if ((natural & bit) != 0 && (visited & bit) == 0 && (allowed & bit) != 0)
                { selected = id; break; }
                cursor++;
            }
            if (selected < 0)
                while ((natural & (1UL << pool[cursor % pool.Length])) == 0) cursor++;
            if (selected < 0) selected = pool[cursor % pool.Length];
            visited |= 1UL << selected; cursor++; result.Add(selected);
        }
        return result.ToArray();
    }

    private static IEnumerable<int[]> Permutations(int[] values) => values.Length == 0 ? [Array.Empty<int>()]
        : values.SelectMany(value => Permutations(values.Where(item => item != value).ToArray())
            .Select(rest => rest.Prepend(value).ToArray()));
    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> Force(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
    private sealed class OnDispose(Action action) : IDisposable { public void Dispose() => action(); }
}
