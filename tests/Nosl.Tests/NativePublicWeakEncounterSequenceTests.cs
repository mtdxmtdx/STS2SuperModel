using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicWeakEncounterSequenceTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Fact]
    public void ExhaustiveReducedBitLawCoversAllNonemptyIdentitySetsAndWildcards()
    {
        const int bits = 3, domain = 1 << bits;
        // Exhaust all native high-word prefixes at each length. Every raw low
        // suffix cancels identically and is checked separately below.
        for (int length = 1; length <= 3; length++)
        {
            var native = new Dictionary<string, int>();
            void Enumerate(int[] remaining, int[] sequence)
            {
                if (sequence.Length == length)
                {
                    string key = string.Join(',', sequence);
                    native[key] = native.GetValueOrDefault(key) + 1; return;
                }
                for (int high = 0; high < domain; high++)
                {
                    int index = (int)((double)high / domain * remaining.Length);
                    Enumerate(remaining.Where((_, i) => i != index).ToArray(), sequence.Append(remaining[index]).ToArray());
                }
            }
            Enumerate([0, 1, 2, 3], []);
            int combinations = (int)Math.Pow(15, length);
            for (int combo = 0; combo < combinations; combo++)
            {
                int value = combo;
                var allowed = Enumerable.Range(0, length).Select(_ =>
                {
                    int mask = value % 15 + 1; value /= 15;
                    return (IReadOnlyList<int>)Enumerable.Range(0, 4).Where(i => (mask & (1 << i)) != 0).ToArray();
                }).ToArray();
                var branches = NativeWeakEncounterSequencePlan.Branches(allowed, bits);
                Assert.InRange(branches.Length, 0, 24);
                var expected = native.Where(pair => pair.Key.Split(',').Select(int.Parse)
                    .Select((identity, slot) => allowed[slot].Contains(identity)).All(match => match)).ToArray();
                Assert.Equal(expected.Length, branches.Length);
                BigInteger total = expected.Sum(pair => pair.Value);
                Assert.Equal(new ShuffleRational(total, BigInteger.Pow(domain, length)),
                    NativeWeakEncounterSequencePlan.Probability(allowed, bits));
                foreach (var branch in branches)
                {
                    Assert.Equal(new BigInteger(native[string.Join(',', branch.EncounterIndices)]), branch.BucketProduct);
                    // q(branch) × q(high-prefix|branch) is exactly 1/total.
                    var q = new ShuffleRational(branch.BucketProduct, total * branch.BucketProduct);
                    Assert.Equal(new ShuffleRational(total, BigInteger.Pow(domain, length)),
                        new ShuffleRational(q.Denominator, BigInteger.Pow(domain, length) * q.Numerator));
                }
            }
        }
    }

    [Fact]
    public void ProposalPreservesAmbiguousPhysicalOutcomesExactWeightsAndAllLowBits()
    {
        const int bits = 3, domain = 1 << bits, lowBits = 64 - bits;
        IReadOnlyList<int>[] allowed = [[0, 1, 2, 3], [2, 3], [0, 1]];
        var branches = NativeWeakEncounterSequencePlan.Branches(allowed, bits);
        int total = (int)branches.Sum(branch => (decimal)branch.BucketProduct);
        var sampled = new Dictionary<string, int>();
        for (int ticket = 0; ticket < total; ticket++)
        foreach (ulong low in new[] { 0UL, (1UL << lowBits) - 1 })
        {
            int calls = 0;
            // The first word is the exact branch ticket. Subsequent even calls
            // choose bucket offset zero without UniformBelow rejection.
            ulong Next() => calls++ == 0 ? (ulong)ticket : calls % 2 == 0 ? (ulong)domain * 6 : low;
            var plan = NativeWeakEncounterSequencePlan.Create(allowed, Next, bits);
            string key = string.Join(',', plan.EncounterIndices);
            sampled[key] = sampled.GetValueOrDefault(key) + 1;
            Assert.All(plan.RawWords, word => Assert.Equal(low, word & ((1UL << lowBits) - 1)));
            Assert.Equal(NativeWeakEncounterSequencePlan.Probability(allowed, bits), plan.NativeToProposalRatio);
            Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("No correction draw needed")));
            int[] remaining = [0, 1, 2, 3];
            for (int slot = 0; slot < 3; slot++)
            {
                int index = (int)((double)(plan.RawWords[slot] >> lowBits) / domain * remaining.Length);
                Assert.Equal(plan.EncounterIndices[slot], remaining[index]);
                remaining = remaining.Where((_, i) => i != index).ToArray();
            }
        }
        foreach (var branch in branches)
            Assert.Equal((int)branch.BucketProduct * 2, sampled[string.Join(',', branch.EncounterIndices)]);
        Assert.Empty(NativeWeakEncounterSequencePlan.Branches([[1], [1]]));
        Assert.Throws<ArgumentException>(() => NativeWeakEncounterSequencePlan.Create([[1], [1]], () => 0));
        Assert.Equal(NativeWeakEncounterSequencePlan.Probability([[0, 1], [2]]),
            NativeWeakEncounterSequencePlan.Probability([[0, 0, 1, 1], [2, 2]]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeFirstThreeBagDrawsPinAllBoundariesOrderTagsAndWordCount(bool overgrowth)
    {
        var branches = NativeWeakEncounterSequencePlan.Branches([[0, 1, 2, 3], [0, 1, 2, 3], [0, 1, 2, 3]]);
        Assert.Equal(24, branches.Length);
        foreach (var branch in branches)
        for (int endpoints = 0; endpoints < 8; endpoints++)
        {
            ActDefinition act = overgrowth ? new Overgrowth() : new Underdocks();
            NativeFirstEncounterCondition.ValidatePool(act);
            var candidates = act.MonsterEncounterCandidates.Where(encounter => encounter.IsWeak).ToArray();
            ulong[] words = branch.Factors.Select((factor, slot) =>
            {
                bool upper = (endpoints & (1 << slot)) != 0;
                return ((factor.BucketStart + (upper ? factor.BucketSize - 1 : 0)) << 11) | (upper ? 2047UL : 0UL);
            }).ToArray();
            int draws = 0; var rng = new Rng(123);
            using var scope = LabelRandomScope.Enter(_ => words[draws++]);
            foreach (int identity in branch.EncounterIndices)
                Assert.Same(candidates[identity], act.PickEncounter(RoomType.Monster, rng));
            Assert.Equal(3, draws); Assert.Equal(3, rng.Counter);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedRouteCertificateExcludesForcedFightsAndAllowsUnknownNormalRooms(bool forced)
    {
        var evidence = Evidence(forced: forced, unknownNormal: true);
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(evidence, out var condition, out var reason), reason);
        Assert.Equal(typeof(Overgrowth), condition!.TargetActType);
        Assert.Equal(3, condition.PrefixLength);
        Assert.Equal(new[] { 0, 1, 2 }, condition.Targets.Select(target => target.NormalSlot));
        Assert.Equal(new[] { 0, 1, 2 }, condition.Targets.Select(target => Assert.Single(target.EncounterIndices)));
        if (forced)
        {
            var forcedOwner = evidence.Events.Single(e => e.Payload is PublicOwnerStarted
                { OwnerKind: PublicEvidenceOwnerKind.Combat, ParentOwnerOrdinal: not null }).OwnerOrdinal;
            Assert.DoesNotContain(condition.Targets, target => target.CombatOwnerOrdinal == forcedOwner);
        }
        var detached = PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(evidence));
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(detached, out var copied, out reason), reason);
        Assert.Equal(condition.Envelope, copied!.Envelope);
    }

    [Fact]
    public void OptionalFailuresRetainSoundEarlierTargetsOrDisableWithoutRejectingRoot()
    {
        var gap = Evidence(gapAfterFirst: true);
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(gap, out var earlier, out var reason), reason);
        Assert.Single(earlier!.Targets); Assert.Equal(1, earlier.PrefixLength);
        Assert.Equal(new ShuffleRational(1, 4), earlier.Envelope);
        Assert.False(NativePublicWeakEncounterSequenceCondition.TryCreate(null, out _, out _));
        Assert.False(NativePublicWeakEncounterSequenceCondition.TryCreate(Evidence(firstMapType: PublicMapNodeType.Shop), out _, out _));
        var missingRoster = Evidence(firstRoster: ["UnreviewedMonster"]);
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(missingRoster, out var later, out reason), reason);
        Assert.Equal(new[] { 1, 2 }, later!.Targets.Select(target => target.NormalSlot));
        Assert.Equal(3, later.PrefixLength);
        var impossible = Evidence(firstRoster: ["Nibbit"]);
        Assert.False(NativePublicWeakEncounterSequenceCondition.TryCreate(impossible, out _, out _));
    }

    [Theory]
    [InlineData(11004UL, 2)]
    [InlineData(11004UL, 3)]
    [InlineData(11007UL, 2)]
    [InlineData(11007UL, 3)]
    public async Task ExistingRootsProvideActualTwoAndThreeNormalEncounterTraces(ulong seed, int length)
    {
        var prior = Prior.Freeze();
        var sourceRecipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root; int globalSlot;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, sourceRecipe,
            NativeLabelTape.ForDeclaredPrior(prior, sourceRecipe)))
        {
            Assert.NotNull(source); globalSlot = source.EligibleSlotsVisited - 1;
            root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        }
        var all = root.PublicEvidence!;
        var combats = all.Events.Where(entry => entry.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Combat, ParentOwnerOrdinal: null }).ToArray();
        Assert.True(combats.Length >= length);
        var prefix = length < combats.Length ? all.Events.Take(checked((int)combats[length].EventOrdinal)).ToImmutableArray() : all.Events;
        var evidence = new PublicRunEvidence(PublicRunEvidence.Version,
            !prefix.Any(e => e.Payload is PublicEvidenceGap or PublicOwnerStarted { CompleteFromOwnerStart: false }), prefix);
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(root with { PublicEvidence = evidence }, prior,
            out var condition, out var reason), reason);
        Assert.Equal(length, condition!.PrefixLength); Assert.Equal(length, condition.Targets.Count);
        // Existing source coordinates are reused only as a controlled routing and
        // replay fixture. Condition construction receives detached public evidence
        // only; no acceptance rate or posterior throughput claim follows.
        var hypothetical = sourceRecipe with { ProposalSeed = sourceRecipe.ProposalSeed ^ 918273UL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, hypothetical);
        var proposal = new NativePublicWeakEncounterSequenceProposal(condition,
            new Rng(hypothetical.ProposalSeed, "weak-sequence-fixture").NextUnsignedLong, Force(tape));
        using var scope = Scope(tape, context =>
        {
            tape.AttachHypotheticalRun(context.Run); proposal.AttachHypotheticalRun(context.Run);
            return proposal.BeginGeneration(context);
        });
        await using var world = await NativeRunWorld.OpenAsync(prior.Execution, hypothetical.IndependentRunSeed, globalSlot);
        Assert.NotNull(world); proposal.ValidateCompletion();
        Assert.Equal(length, tape.ConditionedCells); Assert.Equal(length, proposal.ConditionedEncounterCount);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        Assert.True(proposal.AcceptCorrection(() => throw new InvalidOperationException("Root mass cancels")));
        var replay = tape.ReplayCopy();
        using var replayScope = Scope(replay, null);
        await using var copy = await NativeRunWorld.OpenAsync(prior.Execution, hypothetical.IndependentRunSeed, globalSlot);
        Assert.NotNull(copy);
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int step = 0; step < 200 && world.Observe().Status != "terminal_settled"; step++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(copy.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await copy.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await copy.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(length, tape.ConditionedCells);
    }

    [Fact]
    public void OwnedAdapterRejectsForeignRunsStreamsAliasesMissingWordsAndPoolDrift()
    {
        Assert.True(NativePublicWeakEncounterSequenceCondition.TryCreate(Evidence(), out var condition, out _));
        var run = FreshRun(); var tape = new NativeLabelTape(new(1, 2, 3, 0, 0));
        NativePublicWeakEncounterSequenceProposal New(NativeLabelTape target) => new(condition!, new Rng(432).NextUnsignedLong, Force(target));
        var proposal = New(tape);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(run, run.Act, run.Rng.UpFront)));
        proposal.AttachHypotheticalRun(run);
        Assert.Throws<InvalidOperationException>(() => proposal.AttachHypotheticalRun(run));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(FreshRun(), run.Act, run.Rng.UpFront)));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(run, run.Act, run.Rng.Shuffle)));
        var snap = run.Rng.UpFront.ToSerializable(); Word(tape)(new(snap.state0, snap.state1, snap.state2, snap.state3));
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var forced = proposal.BeginGeneration(new(run, run.Act, run.Rng.UpFront));
            _ = run.Rng.UpFront.NextUnsignedLong();
        });
        Assert.Equal(0, tape.ConditionedCells); Assert.True(tape.HasConditionedWordFailure);
        Assert.Throws<InvalidOperationException>(tape.RequireSuccessfulConditionedWords);
        var skipped = New(new(new(4, 5, 6, 0, 0))); skipped.AttachHypotheticalRun(run);
        Assert.Throws<InvalidOperationException>(() => skipped.BeginGeneration(new(run, run.Act, run.Rng.UpFront)).Dispose());
        Assert.Throws<InvalidOperationException>(skipped.ValidateCompletion);
        var wrongAct = FreshRun(false);
        Assert.Throws<NativePublicConstraintMismatchException>(() => condition!.CreateProposal(new(wrongAct, wrongAct.Act,
            wrongAct.Rng.UpFront), () => throw new InvalidOperationException("Wrong act must not sample")));
        var entries = Assert.IsType<List<EncounterDefinition>>(run.Act.MonsterEncounterCandidates);
        int a = entries.FindIndex(e => e.IsWeak), b = entries.FindIndex(a + 1, e => e.IsWeak);
        (entries[a], entries[b]) = (entries[b], entries[a]);
        Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal(new(run, run.Act, run.Rng.UpFront), () => 0));
    }

    private static PublicRunEvidence Evidence(bool forced = false, bool unknownNormal = false,
        bool gapAfterFirst = false, PublicMapNodeType firstMapType = PublicMapNodeType.Monster, string[]? firstRoster = null)
    {
        var assets = new PublicEvidenceAssets(70, 70, 99, [], [], [], 3, 0, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, assets));
        int row = 0;
        void Map(PublicMapNodeType type)
        {
            long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, row + 1);
            var from = new PublicMapCoordinate(0, row); var to = new PublicMapCoordinate(0, row + 1);
            long observed = recorder.Record(owner, new PublicMapObserved(from,
                [new(from, row == 0 ? PublicMapNodeType.Ancient : PublicMapNodeType.Monster), new(to, type)],
                [new(from, to)], [new(to, true)]));
            recorder.Record(owner, new PublicMapChosen(observed, to));
            recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed)); row++;
        }
        void Combat(string[] roster, long? parent = null)
        {
            long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, row + 1, parent);
            recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.Started));
            recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1));
            for (int i = 0; i < roster.Length; i++)
                recorder.Record(owner, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: i, model: roster[i]));
            recorder.Record(owner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        }
        Map(firstMapType); Combat(firstRoster ?? ["FuzzyWurmCrawler"]);
        if (gapAfterFirst) recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
        if (forced)
        {
            Map(PublicMapNodeType.Unknown);
            long parent = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, row + 1);
            Combat(["Nibbit"], parent);
            recorder.Record(parent, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        Map(unknownNormal ? PublicMapNodeType.Unknown : PublicMapNodeType.Monster); Combat(["Nibbit"]);
        Map(PublicMapNodeType.Monster); Combat(["ShrinkerBeetle"]);
        return recorder.Capture();
    }

    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> Force(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
    private static IDisposable Scope(NativeLabelTape tape, Func<LabelNormalEncounterContext, IDisposable?>? begin)
    {
        var rewards = (NativeRewardsOracle)typeof(NativeLabelTape).GetField("_rewardsOracle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tape)!;
        return LabelRandomScope.EnterRewardProvenance(rewards.Word, Word(tape), beginNormalEncounter: begin);
    }
    private static RunState FreshRun(bool overgrowth = true)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-weak-sequence-guards", [overgrowth ? new Overgrowth() : new Underdocks(), new Hive(), new Glory()], ascensionLevel: 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord); return run;
    }
}
