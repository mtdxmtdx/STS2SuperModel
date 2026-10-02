using System.Numerics;
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
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativeFirstEncounterConditionTests
{
    private static NativeTapePrior Prior => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };

    [Theory]
    [InlineData(9001UL, true)]
    [InlineData(9004UL, false)]
    public async Task NativeCarryInCertifiesAndInterceptsOnlyItsFreshActGeneration(ulong sourceDraw, bool overgrowth)
    {
        // Controlled public evidence fixtures only. No source recipe enters the
        // condition or proposal; its run below is a separate native generation.
        var prior = Prior;
        var recipe = prior.Draw(new Rng(sourceDraw, "nosl-native-tape-source-draw-v1"));
        DecisionPacket packet;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); packet = source.Observe(); }
        Assert.True(NativeFirstEncounterCondition.TryCreate(packet, prior, out var condition, out var reason), reason);
        Assert.Equal(overgrowth ? typeof(Overgrowth) : typeof(Underdocks), condition!.TargetActType);
        Assert.Equal(overgrowth ? "FUZZY_WURM_CRAWLER_WEAK" : "SLUDGE_SPINNER_WEAK", condition.TargetEncounterId);
        const ulong domain = 1UL << 53;
        Assert.Equal(new ShuffleRational(overgrowth ? domain + 1 : domain - 1, 4 * domain), condition.Envelope);

        var run = FreshRun(overgrowth);
        int scopes = 0, draws = 0; NativeFirstEncounterProposal? plan = null;
        var beforeAssets = CombatAssetSnapshot.Capture(run.Players[0]);
        var beforeHp = run.Players[0].Creature.CurrentHp;
        using (LabelRandomScope.Enter(OriginalWord, beginNormalEncounter: context =>
            {
                scopes++; Assert.Same(run, context.Run); Assert.Same(run.Acts[0], context.Act);
                Assert.Same(run.Rng.UpFront, context.Rng);
                plan = condition.CreateProposal(context, new Rng(2468).NextUnsignedLong);
                var ordinary = context.Rng.CloneExact();
                var inner = LabelRandomScope.Enter(state =>
                {
                    var expected = ordinary.ToSerializable();
                    Assert.Equal(new LabelRandomState(expected.state0, expected.state1, expected.state2, expected.state3), state);
                    ulong original = ordinary.NextUnsignedLong();
                    int ordinal = draws++;
                    return ordinal < 2 ? plan.RawWords[ordinal] : original;
                });
                return new ClosingScope(inner, () =>
                    Assert.Equal(ordinary.ToSerializable(), context.Rng.ToSerializable()));
            }))
        {
            Assert.NotNull(run.CurrentAncientEventType); // Native pre-generation of every act.
            Assert.Equal(1, scopes); Assert.True(draws > 2);
            var first = run.PullNextEncounter(RoomType.Monster);
            var second = run.PullNextEncounter(RoomType.Monster);
            var pool = run.Act.MonsterEncounterCandidates.Where(encounter => encounter.IsWeak).ToArray();
            Assert.Same(pool[plan!.FirstEncounterIndex], first);
            Assert.Equal(condition.TargetEncounterId, second.IdEntry); Assert.NotSame(first, second);
            int afterGeneration = run.Rng.UpFront.Counter;
            _ = run.PullNextEncounter(RoomType.Monster);
            Assert.Equal(afterGeneration, run.Rng.UpFront.Counter); // Stored rooms consume no new words.
        }
        Assert.Equal(PublicJson.Serialize(beforeAssets), PublicJson.Serialize(CombatAssetSnapshot.Capture(run.Players[0])));
        Assert.Equal(beforeHp, run.Players[0].Creature.CurrentHp); Assert.All(run.Players[0].PotionSlots, Assert.Null);
        Assert.Equal(condition.Envelope, plan!.Envelope);
        Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("Constant envelope needs no correction word")));

        var wrongAct = FreshRun(!overgrowth);
        Assert.Throws<NativePublicConstraintMismatchException>(() => condition.CreateProposal(
            new(wrongAct, wrongAct.Act, wrongAct.Rng.UpFront), () => throw new InvalidOperationException("Wrong act must not sample")));
        Assert.Throws<InvalidOperationException>(() => condition.CreateProposal(
            new(run, run.Act, run.Rng.Shuffle), () => 0));
        var unavailable = packet with { Observation = packet.Observation! with
            { RunContext = packet.Observation.RunContext! with { CompleteFromRunStart = false } } };
        Assert.False(NativeFirstEncounterCondition.TryCreate(unavailable, prior, out _, out _));
        var later = packet with { Observation = packet.Observation! with
            { RunContext = packet.Observation.RunContext! with { Floor = 4, CombatEntryIndex = 2 } } };
        Assert.False(NativeFirstEncounterCondition.TryCreate(later, prior, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void FiniteJointLawPreservesFirstEncounterAndDownstreamHpEvidence(int target)
    {
        const int bits = 3, domain = 1 << bits;
        var branches = NativeFirstEncounterProposal.Branches(target, bits);
        var nativeCounts = new int[4]; int compatible = 0;
        foreach (int high0 in Enumerable.Range(0, domain))
        foreach (int high1 in Enumerable.Range(0, domain))
        {
            int first = (int)((double)high0 / domain * 4);
            int[] remaining = Enumerable.Range(0, 4).Where(index => index != first).ToArray();
            int second = remaining[(int)((double)high1 / domain * 3)];
            bool match = second == target;
            var branch = branches.SingleOrDefault(item => item.FirstIndex == first);
            bool proposed = branch is not null && Inside(branch.First, high0) && Inside(branch.Second, high1);
            Assert.Equal(match, proposed);
            if (!match) continue;
            compatible++; nativeCounts[first]++;
            ulong total = branches.Aggregate(0UL, (sum, item) => sum + item.Second.BucketSize);
            // q(first) * q(word0|first) * q(word1|first,target).
            var q = new ShuffleRational(branch!.Second.BucketSize,
                (BigInteger)total * branch.First.BucketSize * branch.Second.BucketSize);
            var pOverQ = new ShuffleRational(q.Denominator, domain * domain * q.Numerator);
            Assert.Equal(NativeFirstEncounterProposal.Probability(target, bits), pOverQ);
        }
        Assert.Equal(new ShuffleRational(compatible, domain * domain), NativeFirstEncounterProposal.Probability(target, bits));
        Assert.Equal(target == 0 ? new[] { 0, 6, 6, 6 } : new[] { 6, 6, 0, 4 }, nativeCounts);
        int ticketCount = branches.Sum(branch => (int)branch.Second.BucketSize);
        var proposalFirstCounts = new int[4];
        for (int ticket = 0; ticket < ticketCount; ticket++)
        {
            bool firstWord = true;
            var plan = NativeFirstEncounterProposal.Create(target, () =>
            {
                if (!firstWord) return ulong.MaxValue;
                firstWord = false;
                // Above UniformBelow's rejection threshold, with residue ticket.
                return (ulong)(ticketCount + ticket);
            }, bits);
            proposalFirstCounts[plan.FirstEncounterIndex]++;
        }
        Assert.Equal(nativeCounts.Select(count => count / (domain / 4)), proposalFirstCounts);
        // A latent native HP event tied to first encounter 3 must retain this
        // conditional probability; uniform first-choice cancellation biases it.
        var hpProbability = new ShuffleRational(nativeCounts[3], compatible);
        if (target == 2)
        {
            Assert.Equal(new ShuffleRational(1, 4), hpProbability);
            Assert.NotEqual(new ShuffleRational(1, 3), hpProbability);
        }
        var plans = Enumerable.Range(0, 12).Select(seed =>
            NativeFirstEncounterProposal.Create(target, new Rng((ulong)seed).NextUnsignedLong, bits)).ToArray();
        Assert.All(plans, plan =>
        {
            int first = (int)((double)(plan.RawWords[0] >> (64 - bits)) / domain * 4);
            int[] remaining = Enumerable.Range(0, 4).Where(index => index != first).ToArray();
            int second = remaining[(int)((double)(plan.RawWords[1] >> (64 - bits)) / domain * 3)];
            Assert.Equal(first, plan.FirstEncounterIndex); Assert.Equal(target, second);
            Assert.Equal(NativeFirstEncounterProposal.Probability(target, bits), plan.NativeToProposalRatio);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeBagBoundariesPinPoolOrderWeightsAndTwoDrawCount(bool overgrowth)
    {
        Assert.Equal(new[] { 3002399751580331UL, 3002399751580330UL, 3002399751580331UL },
            Enumerable.Range(0, 3).Select(index => ConditionalShuffleProposal.Factor(3, index).BucketSize));
        foreach (int first in Enumerable.Range(0, 4))
        foreach (int second in Enumerable.Range(0, 3))
        foreach (bool upperFirst in new[] { false, true })
        foreach (bool upperSecond in new[] { false, true })
        {
            ActDefinition act = NewAct(overgrowth); NativeFirstEncounterCondition.ValidatePool(act);
            var candidates = act.MonsterEncounterCandidates.Where(encounter => encounter.IsWeak).ToArray();
            var firstFactor = ConditionalShuffleProposal.Factor(4, first);
            var secondFactor = ConditionalShuffleProposal.Factor(3, second);
            ulong Word(ConditionalShuffleFactor factor, bool upper) =>
                ((factor.BucketStart + (upper ? factor.BucketSize - 1 : 0)) << 11) | (upper ? 2047UL : 0);
            ulong[] words = [Word(firstFactor, upperFirst), Word(secondFactor, upperSecond)];
            int count = 0; var rng = new Rng(19);
            using var scope = LabelRandomScope.Enter(_ => words[count++]);
            Assert.Same(candidates[first], act.PickEncounter(RoomType.Monster, rng));
            Assert.Same(candidates.Where((_, index) => index != first).ToArray()[second], act.PickEncounter(RoomType.Monster, rng));
            Assert.Equal(2, count); Assert.Equal(2, rng.Counter);
        }
        var maxLow = NativeFirstEncounterProposal.Create(overgrowth ? 0 : 2, () => ulong.MaxValue);
        Assert.All(maxLow.RawWords, word => Assert.Equal(2047UL, word & 2047UL));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InactiveCallbackPreservesOrdinaryNativeGeneration(bool overgrowth)
    {
        var ordinary = FreshRun(overgrowth); var observed = FreshRun(overgrowth);
        int calls = 0;
        string[] expected = Enumerable.Range(0, 15).Select(_ => ordinary.PullNextEncounter(RoomType.Monster).IdEntry).ToArray();
        string[] actual;
        using (LabelRandomScope.Enter(OriginalWord, beginNormalEncounter: _ => { calls++; return null; }))
            actual = Enumerable.Range(0, 15).Select(_ => observed.PullNextEncounter(RoomType.Monster).IdEntry).ToArray();
        Assert.Equal(1, calls); Assert.Equal(expected, actual);
        Assert.Equal(ordinary.Rng.UpFront.ToSerializable(), observed.Rng.UpFront.ToSerializable());
    }

    [Theory]
    [InlineData("order")]
    [InlineData("tag")]
    [InlineData("count")]
    public void OfficialWeakPoolDriftFailsClosed(string drift)
    {
        var act = new Overgrowth();
        var entries = Assert.IsType<List<EncounterDefinition>>(act.MonsterEncounterCandidates);
        int first = entries.FindIndex(entry => entry.IsWeak);
        int second = entries.FindIndex(first + 1, entry => entry.IsWeak);
        if (drift == "order") (entries[first], entries[second]) = (entries[second], entries[first]);
        else if (drift == "count") entries.RemoveAt(first);
        else entries[first] = new EncounterDefinition(
            (Func<MonsterModel>)(() => throw new NotSupportedException("Only the encounter metadata is inspected")),
            [EncounterTag.Slimes], true, "FuzzyWurmCrawler") { IdEntry = "FUZZY_WURM_CRAWLER_WEAK" };
        Assert.Throws<InvalidOperationException>(() => NativeFirstEncounterCondition.ValidatePool(act));
    }

    private static bool Inside(ConditionalShuffleFactor factor, int high) =>
        (ulong)high >= factor.BucketStart && (ulong)high < factor.BucketStart + factor.BucketSize;
    private static ActDefinition NewAct(bool overgrowth) => overgrowth ? new Overgrowth() : new Underdocks();
    private static RunState FreshRun(bool overgrowth)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("first-encounter-native-scope", [NewAct(overgrowth), new Hive(), new Glory()], ascensionLevel: 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord);
        return run;
    }
    private static ulong OriginalWord(LabelRandomState state) => new MegaRandom(new SerializableRng
    { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong();
    private sealed class ClosingScope(IDisposable inner, Action check) : IDisposable
    { public void Dispose() { inner.Dispose(); check(); } }
}
