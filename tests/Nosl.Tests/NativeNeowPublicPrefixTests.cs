using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeNeowPublicPrefixTests
{
    private static NativeTapePrior Legacy => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version),
    };
    private static NativeTapePrior Hybrid => Legacy with
    {
        SchemaVersion = NativeTapePrior.RewardsVersion,
        Execution = Legacy.Execution with { PublicEvidenceProfile = PublicRunEvidence.Version },
    };
    private static PublicRelic Relic(string id, int melted = 0) => new(id, new Dictionary<string, int>
    { ["isWax"] = 0, ["isMelted"] = melted, ["stackCount"] = 1 });

    private static DecisionPacket Root(PublicRunEvidence? evidence, bool retained = true, bool melted = false,
        string retainedId = nameof(WingedBoots))
    {
        PublicCard card = new("StrikeSilent", 0, 1, -1, "Attack", []);
        PublicRelic[] relics = retained ? [Relic("RingOfTheSnake"), Relic(retainedId, melted ? 1 : 0)]
            : [Relic("RingOfTheSnake")];
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, [card], relics,
            [null, null], 3, 2, 0, 0);
        PublicEvent[] history = [new("combat_started", "Silent:A10"),
            new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry))];
        return new("player_decision", new("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            [card], [], [], [], [], 0, [null, null], relics.Select(r => r.Id).ToArray(), [], [], history, null),
            [new(0, "end_turn")], PublicEvidence: evidence);
    }

    private static PublicRunEvidence Evidence(string change = "", string first = nameof(WingedBoots),
        string second = nameof(ScrollBoxes), string curse = nameof(LeafyPoultice))
    {
        var assets = new PublicEvidenceAssets(70, 70, 99, [], [Relic("RingOfTheSnake")], [null, null], 3, 2, 0, 0);
        var recorder = new PublicRunEvidenceRecorder(change == "run_start_gap" ? null
            : new(change == "character" ? "Ironclad" : "Silent", change == "ascension" ? 9 : 10, assets));
        if (change == "before_owner_gap") recorder.RecordGap(null, PublicEvidenceGapReason.ObservationMissing);
        if (change == "earlier_owner")
        {
            long earlier = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
            recorder.Record(earlier, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        }
        long owner = recorder.BeginOwner(change == "owner_kind" ? PublicEvidenceOwnerKind.Rest : PublicEvidenceOwnerKind.Event,
            change == "act" ? 1 : 0, change == "floor" ? 2 : 1, completeFromOwnerStart: change != "owner_start_gap");
        if (change == "before_options_gap") recorder.RecordGap(owner, PublicEvidenceGapReason.ObservationMissing);
        var options = new List<PublicVisibleOption>
        {
            new(first, false),
            new(change == "unknown_positive" ? "MassiveScroll" : second, change == "locked_positive"),
            new(change == "unknown_curse" ? "GoldenCompass" : curse, change == "locked_curse",
                change == "price" ? 1 : null),
        };
        if (change == "four_options") options.Add(new("extra", false));
        if (change == "two_options") options.RemoveAt(2);
        if (change == "earlier_options") recorder.Record(owner, new PublicOptionsObserved([new("PROCEED", false)]));
        long offered = recorder.Record(owner, new PublicOptionsObserved(options.ToImmutableArray()));
        if (change == "before_choice_gap") recorder.RecordGap(owner, PublicEvidenceGapReason.ObservationMissing);
        if (change != "missing_choice")
            recorder.Record(owner, new PublicOptionChosen(offered, change == "second_chosen" ? second : first));
        if (change == "later_gap") recorder.RecordGap(owner, PublicEvidenceGapReason.UnsupportedObservation);
        return recorder.Capture();
    }

    [Theory]
    [InlineData(NaturalSourceCollector.ScriptVersion)]
    [InlineData(NaturalSourceCollector.BoundedEventScriptVersion)]
    public void TypedInitialOffersCertifyBothPositivesWithoutRetainedInventory(string script)
    {
        var evidence = Evidence(first: nameof(PreciseScissors), second: nameof(PhialHolster));
        var prior = Hybrid with { Execution = Hybrid.Execution with { OutsideCombatScript = script } };
        foreach (var root in new[] { Root(evidence, retained: false),
            Root(evidence, melted: true, retainedId: nameof(PreciseScissors)),
            Root(evidence) with { Observation = Root(null).Observation! with { History = [] } } })
        {
            Assert.True(NativeNeowCondition.TryCreate(root, prior, out var condition, out var reason), reason);
            Assert.Equal([nameof(PreciseScissors), nameof(PhialHolster)], condition!.PositivePrefixIds);
            Assert.Equal(nameof(PreciseScissors), condition.TargetRelicId);
            Assert.Equal(nameof(LeafyPoultice), condition.ObservedCurseId);
        }
        var laterGap = Evidence("later_gap");
        Assert.False(laterGap.CompleteFromRunStart);
        Assert.True(NativeNeowCondition.TryCreate(Root(laterGap, retained: false), prior, out var later, out _));
        Assert.Equal(2, later!.PositivePrefixIds.Count);
    }

    [Theory]
    [InlineData("run_start_gap")]
    [InlineData("before_owner_gap")]
    [InlineData("earlier_owner")]
    [InlineData("owner_start_gap")]
    [InlineData("before_options_gap")]
    [InlineData("before_choice_gap")]
    [InlineData("character")]
    [InlineData("ascension")]
    [InlineData("owner_kind")]
    [InlineData("act")]
    [InlineData("floor")]
    [InlineData("unknown_positive")]
    [InlineData("unknown_curse")]
    [InlineData("locked_positive")]
    [InlineData("locked_curse")]
    [InlineData("price")]
    [InlineData("four_options")]
    [InlineData("two_options")]
    [InlineData("earlier_options")]
    [InlineData("missing_choice")]
    [InlineData("second_chosen")]
    public void UncertifiedPublicBoundaryFallsBackWithoutDroppingTheRoot(string change)
    {
        var evidence = Evidence(change);
        Assert.True(NativeNeowCondition.TryCreate(Root(evidence), Hybrid, out var retained, out var reason), reason);
        Assert.Equal([nameof(WingedBoots)], retained!.PositivePrefixIds);
        Assert.False(NativeNeowCondition.TryCreate(Root(evidence, retained: false), Hybrid, out _, out _));
    }

    [Fact]
    public void LegacyLawIgnoresTypedPrefixAndPreservesExistingIdentityAndProposalBytes()
    {
        Assert.Equal("56b2f605f9b4fa82a9c5ce8d7ce0513310c552d2bc41e08eee88a2d9eddb25c4", Legacy.Identity);
        Assert.True(NativeNeowCondition.TryCreate(Root(null), Legacy, out var absent, out _));
        Assert.True(NativeNeowCondition.TryCreate(Root(Evidence()), Legacy, out var present, out _));
        Assert.True(NativeNeowCondition.TryCreate(Root(null), Hybrid, out var fallback, out _));
        Assert.Equal([nameof(WingedBoots)], present!.PositivePrefixIds);
        Assert.False(NativeNeowCondition.TryCreate(Root(Evidence(), retained: false), Legacy, out _, out _));
        Type[] pool = NativePool();
        var original = ConditionalShuffleProposal.Create(pool.Select(t => t.Name).ToArray(), [nameof(WingedBoots)],
            () => ulong.MaxValue)!;
        foreach (var condition in new[] { absent!, present, fallback! })
        {
            Assert.Null(condition.ObservedCurseId);
            var proposal = condition.CreateProposal(pool, () => ulong.MaxValue);
            Assert.Equal(original.RawWords, proposal.Plan.RawWords);
            Assert.Equal(original.PhysicalPermutation, proposal.Plan.PhysicalPermutation);
            Assert.Equal(original.NativeToProposalRatio, proposal.Plan.NativeToProposalRatio);
            Assert.Equal(NativeNeowCondition.RootEnvelope, proposal.RootEnvelope);
        }
        Assert.Equal(NativeNeowCondition.MaximumEnvelope([14, 15, 16]),
            NativeNeowCondition.MaximumEnvelope([14, 15, 16], prefixLength: 1));
    }

    [Fact]
    public void SecondPositiveAbsenceIsANonmatchAndNativeShapeDriftIsAnError()
    {
        Assert.True(NativeNeowCondition.TryCreate(Root(Evidence(second: nameof(SmallCapsule))), Hybrid,
            out var condition, out _));
        Assert.Throws<NativePublicConstraintMismatchException>(() => condition!.CreateProposal(NativePool(), () => ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal(NativePool().Skip(2).ToArray(), () => ulong.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeNeowCondition.MaximumEnvelope([1], prefixLength: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeNeowCondition.MaximumEnvelope([3], prefixLength: 0));
    }

    [Fact]
    public async Task EveryNativeLatentPoolKeepsItsCurseAndBothObservedPositivePositions()
    {
        NaturalSourceCollector.InitializeNativeModels();
        Assert.True(NativeNeowCondition.TryCreate(Root(Evidence()), Hybrid, out var condition, out _));
        string[] curses = [nameof(CursedPearl), nameof(DowsingRod), nameof(HeftyTablet), nameof(LargeCapsule),
            nameof(LeafyPoultice), nameof(NeowsBones), nameof(NeowsSacrifice), nameof(PrecariousShears),
            nameof(SilkenTress), nameof(SilverCrucible)];
        var counts = new HashSet<int>(); var shapes = new HashSet<string>();
        for (int curse = 0; curse < 10; curse++)
            for (int booleans = 0; booleans < 8; booleans++)
            {
                var run = new RunState("neow-public-prefix-shape-proof", ascensionLevel: 10);
                run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
                _ = run.CurrentAncientEventType;
                var room = new EventRoom(() => (Neow)ModelDb.Event<Neow>().MutableClone());
                run.PushRoom(room);
                var factor = ConditionalShuffleProposal.Factor(10, curse);
                int draw = 0; NativeNeowProposal? proposal = null;
                using (LabelRandomScope.Enter(_ => draw++ == 0 ? factor.BucketStart << 11
                    : ((booleans >> (draw - 2)) & 1) == 0 ? 0UL : ulong.MaxValue,
                    (rng, items) =>
                    {
                        Assert.Same(room.Event.Rng, rng);
                        Type[] pool = items.Cast<Type>().ToArray();
                        counts.Add(pool.Length); shapes.Add(string.Join(",", pool.Select(t => t.Name).Order()));
                        proposal = condition!.CreateProposal(pool, () => ulong.MaxValue);
                        int word = 0;
                        return LabelRandomScope.Enter(_ => proposal.Plan.RawWords[word++]);
                    }))
                    await room.Enter(run);
                Assert.NotNull(proposal);
                Assert.Equal([nameof(WingedBoots), nameof(ScrollBoxes), curses[curse]], room.Event.CurrentOptions.Select(o => o.Key));
                Assert.All(room.Event.CurrentOptions, o => Assert.False(o.IsLocked));
                Assert.Equal(new ShuffleRational(1, proposal.Plan.PhysicalPermutation.Count * (proposal.Plan.PhysicalPermutation.Count - 1)),
                    proposal.Plan.PrefixProbability);
                Assert.Equal(NativeNeowCondition.MaximumEnvelope([14, 15, 16], prefixLength: 2), proposal.RootEnvelope);
                await room.Exit(run); run.PopCurrentRoom();
            }
        Assert.Equal([14, 15, 16], counts.Order());
        Assert.Equal(52, shapes.Count);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void FinitePrecisionCorrectionPreservesOrderedPrefixAndUnequalLatentPoolMass(int prefixLength, bool reverse)
    {
        const int bits = 3, domain = 1 << bits;
        var envelope = NativeNeowCondition.MaximumEnvelope([3, 4, 5], bits, prefixLength);
        string[] prefix = prefixLength == 1 ? ["0"] : reverse ? ["1", "0"] : ["0", "1"];
        var corrected = new List<ShuffleRational>(); var wrong = new List<ShuffleRational>();
        foreach (int count in new[] { 3, 4, 5 })
        {
            string[] ids = Enumerable.Range(0, count).Select(i => i.ToString()).ToArray();
            var native = new Dictionary<string, int>();
            int space = 1 << (bits * (count - 1));
            for (int encoded = 0; encoded < space; encoded++)
            {
                int[] order = Enumerable.Range(0, count).ToArray(); int words = encoded;
                for (int i = count - 1; i > 0; i--)
                {
                    int index = (int)((double)(words % domain) / domain * (i + 1)); words /= domain;
                    (order[i], order[index]) = (order[index], order[i]);
                }
                if (!order.Take(prefixLength).Select(i => ids[i]).SequenceEqual(prefix)) continue;
                string key = string.Join(",", order); native[key] = native.GetValueOrDefault(key) + 1;
            }
            var correctMass = new ShuffleRational(0, 1); var wrongMass = correctMass;
            foreach (var pair in native)
            {
                int[] order = pair.Key.Split(',').Select(int.Parse).ToArray();
                var words = new Queue<ulong>();
                var remaining = Enumerable.Range(0, count).Except(prefix.Select(int.Parse)).ToList();
                foreach (int item in order.Skip(prefixLength))
                {
                    if (remaining.Count > 1) words.Enqueue((ulong)(remaining.Count + remaining.IndexOf(item)));
                    remaining.Remove(item);
                }
                var plan = ConditionalShuffleProposal.Create(ids, prefix,
                    () => words.Count > 0 ? words.Dequeue() : ulong.MaxValue, bits)!;
                Assert.Equal(order, plan.PhysicalPermutation);
                var proposal = new NativeNeowProposal(plan, envelope);
                int accepted = 0, correctionSpace = (int)proposal.AcceptanceProbability.Denominator;
                for (int draw = 0; draw < correctionSpace; draw++)
                    if (proposal.AcceptCorrection(() => (ulong)draw)) accepted++;
                Assert.Equal(new ShuffleRational(accepted, correctionSpace), proposal.AcceptanceProbability);
                int permutations = Enumerable.Range(1, count - prefixLength).Aggregate(1, (a, b) => a * b);
                // Unequal latent weights 1/6, 2/6, 3/6 represent multiple native
                // curse/coin paths aliasing to the same pool. Preserve each atom.
                var joint = proposal.AcceptanceProbability.Multiply(count - 2, 6 * permutations);
                Assert.Equal(new ShuffleRational(pair.Value * (count - 2) * envelope.Denominator,
                    6 * space * envelope.Numerator), joint);
                correctMass = Add(correctMass, joint);
                wrongMass = Add(wrongMass, new ShuffleRational(plan.NativeToProposalRatio.Numerator * plan.Envelope.Denominator,
                    plan.NativeToProposalRatio.Denominator * plan.Envelope.Numerator).Multiply(count - 2, 6 * permutations));
            }
            Assert.Equal(Enumerable.Range(1, count - prefixLength).Aggregate(1, (a, b) => a * b), native.Count);
            corrected.Add(correctMass); wrong.Add(wrongMass);
        }
        Assert.NotEqual(Ratio(corrected[0], corrected[1]), Ratio(wrong[0], wrong[1]));
    }

    [Fact]
    public async Task PublicOriginDoesNotRelaxTheFirstRewardStructuralAssetChecks()
    {
        var recipe = Legacy.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Legacy.Execution, recipe, new(recipe)))
        { Assert.NotNull(source); root = source.Observe(); }
        Assert.True(NativeFirstRewardCondition.TryCreate(root, Hybrid, out _, out var why), why);
        Assert.True(NativeNeowCondition.TryCreate(root, Legacy, out var retained, out why), why);
        root = root with { PublicEvidence = Evidence(first: retained!.TargetRelicId) };
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        foreach (bool melted in new[] { false, true })
        {
            var changed = entry with { Relics = melted
                ? entry.Relics.Select(r => r.Id == retained.TargetRelicId ? Relic(r.Id, melted: 1) : r).ToArray()
                : entry.Relics.Where(r => r.Id != retained.TargetRelicId).ToArray() };
            var history = root.Observation.History.ToArray();
            history[1] = history[1] with { Detail = PublicJson.Serialize(changed) };
            var modifiedRoot = root with { Observation = root.Observation with { History = history } };
            Assert.True(NativeNeowCondition.TryCreate(modifiedRoot, Hybrid, out var publicOrigin, out why), why);
            Assert.Equal(2, publicOrigin!.PositivePrefixIds.Count);
            Assert.False(NativeFirstRewardCondition.TryCreate(modifiedRoot, Hybrid, out _, out why));
            Assert.Equal("first_reward_inventory_not_certified", why);
        }
        // A different ordinary positive cannot inherit the typed target's
        // narrower asset-origin proof merely by preserving relic count/flags.
        var replaced = entry with { Relics = entry.Relics.Select(r => r.Id == retained.TargetRelicId
            ? Relic(retained.TargetRelicId == nameof(WingedBoots) ? nameof(LeadPaperweight) : nameof(WingedBoots)) : r).ToArray() };
        var replacementHistory = root.Observation.History.ToArray();
        replacementHistory[1] = replacementHistory[1] with { Detail = PublicJson.Serialize(replaced) };
        var replacementRoot = root with { Observation = root.Observation with { History = replacementHistory } };
        Assert.True(NativeNeowCondition.TryCreate(replacementRoot, Hybrid, out _, out _));
        Assert.False(NativeFirstRewardCondition.TryCreate(replacementRoot, Hybrid, out _, out why));
        Assert.Equal("first_reward_neow_target_not_retained", why);
        Assert.False(NativeFirstEncounterCondition.TryCreate(replacementRoot, Hybrid, out _, out _));
        Assert.False(NativeInitialPrefixCondition.TryCreate(replacementRoot, Hybrid, out _, out _));
    }

    [Fact]
    public async Task RealHybridPublicPrefixMatchesBothOptionsAndReplaysConditionedWords()
    {
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 0, DecisionIndex = 0 };
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe)))
        { Assert.NotNull(original); root = original.Observe(); }
        Assert.True(NativeNeowCondition.TryCreate(root, Hybrid, out var neow, out var why), why);
        var offered = Assert.IsType<PublicOptionsObserved>(root.PublicEvidence!.Events[2].Payload);
        Assert.Equal(offered.Options.Take(2).Select(o => o.Key), neow!.PositivePrefixIds);
        Assert.True(NativeInitialShuffleCondition.TryCreate(root, out var shuffle, out why), why);
        Assert.True(NativeInitialHpCondition.TryCreate(root, out var hp, out why), why);
        var proposalRecipe = recipe with { ProposalSeed = 88899 };
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, proposalRecipe, shuffle, hpCondition: hp, neowCondition: neow);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, proposalRecipe, tape);
        Assert.NotNull(world); tape.ValidateProposalCompletion();
        Assert.True(tape.NeowConditionApplied); Assert.True(tape.ConditionApplied);
        Assert.Equal(PublicJson.Serialize(root), PublicJson.Serialize(world.Observe()));
        await using var fork = await world.ForkForContinuationAsync();
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        var action = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId).Choose(world.Observe());
        await world.StepAsync(action); await fork.StepAsync(action);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        // A lifecycle fixture with the original state recipe proves replay and
        // public equality, not an independently sampled posterior acceptance rate.
    }

    [Theory]
    [InlineData(nameof(WingedBoots), nameof(ScrollBoxes))]
    [InlineData(nameof(LavaRock), nameof(StoneHumidifier))]
    [InlineData(nameof(SmallCapsule), nameof(NeowsTalisman))]
    [InlineData(nameof(NutritiousOyster), nameof(Pomander))]
    public async Task NativeOpeningConditionsCurseAndOnlyRequiredCoins(string first, string second)
    {
        NaturalSourceCollector.InitializeNativeModels();
        Type[] curses = NativeCurses();
        string[][] pairs = [[nameof(LavaRock), nameof(SmallCapsule)],
            [nameof(NutritiousOyster), nameof(StoneHumidifier)], [nameof(NeowsTalisman), nameof(Pomander)]];
        foreach (Type curse in curses)
            for (int booleans = 0; booleans < 8; booleans++)
            {
                Assert.True(NativeNeowCondition.TryCreate(Root(Evidence(first: first, second: second, curse: curse.Name)),
                    Hybrid, out var condition, out _));
                if (curse == typeof(LargeCapsule) && pairs[0].Any(p => p == first || p == second))
                {
                    Assert.Throws<NativePublicConstraintMismatchException>(() => condition!.CreateOpeningProposal(curses, () => ulong.MaxValue));
                    continue;
                }
                var run = new RunState("neow-observed-opening", ascensionLevel: 10);
                run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
                _ = run.CurrentAncientEventType;
                var room = new EventRoom(() => (Neow)ModelDb.Event<Neow>().MutableClone());
                run.PushRoom(room);
                NativeNeowOpeningProposal? opening = null;
                NativeNeowProposal? shuffle = null;
                int rolls = 0, forced = 0, untouched = 0;
                var before = new Rng(0);
                using (LabelRandomScope.Enter(_ => throw new InvalidOperationException("Opening draw escaped its native boundary"),
                    beginNeowInitialOptions: context =>
                    {
                        Assert.Same(room.Event, context.Neow); Assert.Same(room.Event.Rng, context.Rng);
                        Assert.Equal(curses, context.AllowedCurses);
                        opening = condition!.CreateOpeningProposal(context.AllowedCurses, () => ulong.MaxValue);
                        before = context.Rng.CloneExact();
                        return LabelRandomScope.Enter(_ =>
                        {
                            int ordinal = rolls++;
                            if (opening.RawWords[ordinal] is { } word) { forced++; return word; }
                            untouched++;
                            return ((booleans >> (ordinal - 1)) & 1) == 0 ? 0UL : ulong.MaxValue;
                        });
                    }, beginShuffle: (rng, items) =>
                    {
                        Assert.NotNull(opening);
                        Type[] pool = items.Cast<Type>().ToArray();
                        int coin = 1;
                        for (int pairIndex = 0; pairIndex < 3; pairIndex++)
                        {
                            if (pairIndex == 0 && curse == typeof(LargeCapsule)) continue;
                            string[] pair = pairs[pairIndex];
                            string? required = pair.SingleOrDefault(p => p == first || p == second);
                            string expected = required ?? pair[(booleans >> (coin - 1)) & 1];
                            Assert.Contains(pool, type => type.Name == expected);
                            Assert.Equal(required is not null, opening.RawWords[coin++].HasValue);
                        }
                        Assert.Equal(coin, rolls);
                        for (int i = 0; i < rolls; i++) before.NextUnsignedLong();
                        Assert.Equal(PublicJson.Serialize(before.ToSerializable()), PublicJson.Serialize(rng.ToSerializable()));
                        shuffle = condition!.CreateProposal(pool, () => ulong.MaxValue, opening);
                        int ordinal = 0;
                        return LabelRandomScope.Enter(_ => shuffle.Plan.RawWords[ordinal++]);
                    }))
                    await room.Enter(run);
                Assert.Equal([first, second, curse.Name], room.Event.CurrentOptions.Select(option => option.Key));
                int requiredCoins = pairs.Count(pair => pair.Any(p => p == first || p == second));
                Assert.Equal(1 + requiredCoins, forced);
                Assert.Equal((curse == typeof(LargeCapsule) ? 2 : 3) - requiredCoins, untouched);
                var curseBucket = ConditionalShuffleProposal.Factor(10, Array.IndexOf(curses, curse));
                var ratio = new ShuffleRational(curseBucket.BucketSize, 1UL << 53).Multiply(1, 1 << requiredCoins);
                Assert.Equal(ratio, opening!.NativeToProposalRatio);
                Assert.Equal(shuffle!.Plan.NativeToProposalRatio.Multiply(ratio.Numerator, ratio.Denominator), shuffle.NativeToProposalRatio);
                Assert.Equal(NativeNeowCondition.MaximumEnvelope([14, 15, 16], prefixLength: 2)
                    .Multiply(ratio.Numerator, ratio.Denominator), shuffle.RootEnvelope);
                await room.Exit(run); run.PopCurrentRoom();
            }
    }

    [Fact]
    public void ImpossiblePublicOpeningRejectsAndNativeCursePoolDriftIsAnError()
    {
        foreach (var pair in new[] { (nameof(LavaRock), nameof(SmallCapsule)),
            (nameof(NutritiousOyster), nameof(StoneHumidifier)), (nameof(NeowsTalisman), nameof(Pomander)) })
        {
            Assert.True(NativeNeowCondition.TryCreate(Root(Evidence(first: pair.Item1, second: pair.Item2)), Hybrid, out var impossible, out _));
            Assert.Throws<NativePublicConstraintMismatchException>(() => impossible!.CreateOpeningProposal(NativeCurses(), () => ulong.MaxValue));
        }
        Assert.True(NativeNeowCondition.TryCreate(Root(Evidence()), Hybrid, out var condition, out _));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateOpeningProposal(NativeCurses().Reverse().ToArray(), () => ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateOpeningProposal(NativeCurses().Skip(1).ToArray(), () => ulong.MaxValue));
        var recipe = Hybrid.Draw(new Rng(9001));
        Assert.Throws<ArgumentException>(() => new NativeLabelTape(recipe, neowCondition: condition));
        var unentered = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe, neowCondition: condition);
        Assert.Throws<InvalidOperationException>(() => unentered.ValidateProposalCompletion());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task OpeningForcedAliasesFailWhileUnrequiredCoinAliasesKeepTheirOracleCell(int visitedDraw, bool fails)
    {
        NaturalSourceCollector.InitializeNativeModels();
        Assert.True(NativeNeowCondition.TryCreate(Root(Evidence(second: nameof(NeowsTalisman), curse: nameof(SilkenTress))),
            Hybrid, out var condition, out _));
        var recipe = Hybrid.Draw(new Rng(9001, "nosl-native-tape-source-draw-v1")) with { CombatIndex = 0, DecisionIndex = 0 };
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, recipe, neowCondition: condition);
        var earlier = new Rng(new RunRngSet(recipe.IndependentRunSeed).Seed, ModelDb.Event<Neow>().Id.Entry);
        for (int i = 0; i < visitedDraw; i++) earlier.NextUnsignedLong();
        using (tape.EnterScope()) earlier.NextUnsignedLong();
        if (fails)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe, tape));
            Assert.Contains("Neow opening revisited an earlier tape cell", error.Message);
        }
        else
        {
            await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe, tape);
            Assert.NotNull(world); tape.ValidateProposalCompletion();
            var options = Assert.IsType<PublicOptionsObserved>(world.Observe().PublicEvidence!.Events[2].Payload);
            Assert.Equal([nameof(WingedBoots), nameof(NeowsTalisman), nameof(SilkenTress)], options.Options.Select(o => o.Key));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(9, true)]
    public void FiniteOpeningAndShuffleCorrectionPreservesEveryLatentCoinPath(int curseIndex, bool requireTwoCoins)
    {
        const int bits = 4, domain = 1 << bits;
        string first = nameof(NutritiousOyster), second = requireTwoCoins ? nameof(Pomander) : nameof(WingedBoots);
        Assert.True(NativeNeowCondition.TryCreate(Root(Evidence(first: first, second: second, curse: NativeCurses()[curseIndex].Name)),
            Hybrid, out var condition, out _));
        var opening = condition!.CreateOpeningProposal(NativeCurses(), () => ulong.MaxValue, bits);
        int coinCount = opening.RawWords.Count - 1;
        var nativeOpenings = new Dictionary<int, int>();
        int openingSpace = 1 << (bits * opening.RawWords.Count);
        // Exhaust raw opening words independently of the proposal's bucket math.
        for (int encoded = 0; encoded < openingSpace; encoded++)
        {
            int value = encoded;
            int curse = (int)((double)(value % domain) / domain * 10); value /= domain;
            int coins = 0;
            for (int coin = 0; coin < coinCount; coin++)
            {
                coins |= (int)((double)(value % domain) / domain * 2) << coin; value /= domain;
            }
            int oysterIndex = curseIndex == 3 ? 0 : 1;
            if (curse != curseIndex || ((coins >> oysterIndex) & 1) != 0
                || (requireTwoCoins && ((coins >> (oysterIndex + 1)) & 1) != 1)) continue;
            nativeOpenings[coins] = nativeOpenings.GetValueOrDefault(coins) + 1;
        }
        int forcedCoins = requireTwoCoins ? 2 : 1;
        Assert.Equal(1 << (coinCount - forcedCoins), nativeOpenings.Count);
        var shuffleEnvelope = NativeNeowCondition.MaximumEnvelope([3, 4, 5], bits, prefixLength: 2);
        var envelope = shuffleEnvelope.Multiply(opening.NativeToProposalRatio.Numerator, opening.NativeToProposalRatio.Denominator);
        foreach (var latent in nativeOpenings)
        {
            // Different native coin paths can alias the same pool, or change its size.
            int count = 3 + latent.Key % 3;
            string[] ids = Enumerable.Range(0, count).Select(i => i.ToString()).ToArray();
            var permutations = new Dictionary<string, int>();
            int shuffleSpace = 1 << (bits * (count - 1));
            for (int encoded = 0; encoded < shuffleSpace; encoded++)
            {
                int value = encoded; int[] order = Enumerable.Range(0, count).ToArray();
                for (int bound = count; bound > 1; bound--)
                {
                    int index = (int)((double)(value % domain) / domain * bound); value /= domain;
                    (order[bound - 1], order[index]) = (order[index], order[bound - 1]);
                }
                if (order[0] != 0 || order[1] != 1) continue;
                string key = string.Join(",", order); permutations[key] = permutations.GetValueOrDefault(key) + 1;
            }
            foreach (var permutation in permutations)
            {
                int[] order = permutation.Key.Split(',').Select(int.Parse).ToArray();
                var remaining = Enumerable.Range(2, count - 2).ToList();
                var choices = new Queue<ulong>();
                foreach (int item in order.Skip(2))
                {
                    if (remaining.Count > 1) choices.Enqueue((ulong)(remaining.Count + remaining.IndexOf(item)));
                    remaining.Remove(item);
                }
                var plan = ConditionalShuffleProposal.Create(ids, ["0", "1"], () => choices.Count > 0 ? choices.Dequeue() : ulong.MaxValue, bits)!;
                var joint = new NativeNeowProposal(plan, envelope, opening.NativeToProposalRatio);
                int tails = Enumerable.Range(1, count - 2).Aggregate(1, (a, b) => a * b);
                var accepted = joint.AcceptanceProbability.Multiply(1, nativeOpenings.Count * tails);
                var native = new ShuffleRational(latent.Value, openingSpace).Multiply(permutation.Value, shuffleSpace);
                Assert.Equal(native.Multiply(envelope.Denominator, envelope.Numerator), accepted);
                Assert.Equal(order, plan.PhysicalPermutation);
            }
        }
    }

    private static Type[] NativeCurses() => [typeof(CursedPearl), typeof(DowsingRod), typeof(HeftyTablet), typeof(LargeCapsule),
        typeof(LeafyPoultice), typeof(NeowsBones), typeof(NeowsSacrifice), typeof(PrecariousShears), typeof(SilkenTress), typeof(SilverCrucible)];

    private static Type[] NativePool() => [typeof(ArcaneScroll), typeof(BoomingConch), typeof(FishingRod), typeof(GoldenPearl),
        typeof(Kaleidoscope), typeof(LeadPaperweight), typeof(LostCoffer), typeof(NeowsTorment), typeof(NewLeaf),
        typeof(PhialHolster), typeof(PreciseScissors), typeof(ScrollBoxes), typeof(WingedBoots), typeof(LavaRock),
        typeof(NutritiousOyster), typeof(NeowsTalisman)];
    private static ShuffleRational Add(ShuffleRational a, ShuffleRational b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    private static ShuffleRational Ratio(ShuffleRational a, ShuffleRational b) =>
        new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
}
