using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Relics;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.RelicPools;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeNeowConditionTests
{
    private static NativeTapePrior Prior => new()
    { EligibleCombats = 1, EligibleDecisionsPerCombat = 1, Execution = new(MaxFloors: 1, SourceDecisionHorizon: 64) };

    private static PublicRelic Relic(string id) => new(id, new Dictionary<string, int>
    { ["isWax"] = 0, ["isMelted"] = 0, ["stackCount"] = 1, ["isUsedUp"] = 0 });

    private static DecisionPacket Root(string relic = "WingedBoots")
    {
        PublicCard card = new("StrikeSilent", 0, 1, -1, "Attack", []);
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 56, 70, 99, [card],
            [Relic("RingOfTheSnake"), Relic(relic)], [null, null], 3, 2, 0, 0);
        PublicEvent[] history = [new("combat_started", "Silent:A10"),
            new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry))];
        return new("player_decision", new("nosl.public.v2", 56, 10, 1, 56, 70, 0, 3, 0,
            [card], [], [], [], [], 0, [null, null], ["RingOfTheSnake", relic], [], [], history, null), [new(0, "end_turn")]);
    }

    [Theory]
    [InlineData("WingedBoots")]
    [InlineData("NeowsTorment")]
    [InlineData("NutritiousOyster")]
    [InlineData("ScrollBoxes")]
    public void PublicEntryAndDeclaredFreshRunPriorCertifyOnlyTheRelicOrigin(string relic)
    {
        var root = Root(relic);
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.NotNull(condition);
        Assert.Equal(relic, condition.TargetRelicId);
        // A later packet in that same combat has the same carry-in
        // evidence. Current inventory/card hooks do not establish its origin.
        root = root with { Status = "card_choice", Observation = root.Observation! with
        { Turn = 4, Relics = ["unrelated-current-relic"], History = [.. root.Observation.History, new("action", "later")] } };
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var later, out reason), reason);
        Assert.Equal(relic, later!.TargetRelicId);
        root.Observation.History[1] = new("mutated", "mutated");
        Assert.Equal(relic, condition.TargetRelicId);
    }

    [Theory]
    [InlineData(NaturalSourceCollector.ScriptVersion)]
    [InlineData(NaturalSourceCollector.BoundedEventScriptVersion)]
    public void OtherEntryAssetsDoNotChangeTheRetainedPositiveOrigin(string script)
    {
        var prior = Prior with { EligibleCombats = 80,
            Execution = Prior.Execution with { MaxFloors = 60, OutsideCombatScript = script } };
        var root = Root();
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        entry = entry with
        {
            // Absence of the starter, extra Ancient relics and other relic states
            // cannot create this ordinary Neow type. They are matched by full replay.
            Relics = [Relic("WingedBoots"), Relic("GoldenCompass"),
                new("Circlet", new Dictionary<string, int> { ["stackCount"] = 4, ["isWax"] = 1,
                    ["isMelted"] = 1, ["isUsedUp"] = 1 })],
            Deck = [], OrbSlots = 2,
        };
        root.Observation.History[1] = root.Observation.History[1] with { Detail = PublicJson.Serialize(entry) };
        Assert.True(NativeNeowCondition.TryCreate(root, prior, out var condition, out var reason), reason);
        Assert.Equal("WingedBoots", condition!.TargetRelicId);
    }

    [Theory]
    [InlineData("other_policy", "fresh_native_run_prior_required")]
    [InlineData("wrong_character", "fresh_silent_entry_history_required")]
    [InlineData("duplicate_anchor", "fresh_silent_entry_history_required")]
    [InlineData("extra_positive", "unique_retained_neow_positive_required")]
    [InlineData("duplicate_positive", "unique_retained_neow_positive_required")]
    [InlineData("melted", "ordinary_retained_neow_positive_required")]
    [InlineData("wax", "ordinary_retained_neow_positive_required")]
    [InlineData("stacked", "ordinary_retained_neow_positive_required")]
    [InlineData("missing_flags", "ordinary_retained_neow_positive_required")]
    [InlineData("MassiveScroll", "unique_retained_neow_positive_required")]
    [InlineData("NeowsBones", "unique_retained_neow_positive_required")]
    [InlineData("GoldenCompass", "unique_retained_neow_positive_required")]
    [InlineData("unknown", "unique_retained_neow_positive_required")]
    public void UnprovedOriginsFallbackWithoutExcludingTheRoot(string change, string expected)
    {
        var root = Root(); var prior = Prior;
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        switch (change)
        {
            case "other_policy": prior = prior with { Execution = prior.Execution with { SourcePolicyId = "other" } }; break;
            case "wrong_character": root.Observation.History[0] = new("combat_started", "Ironclad:A10"); break;
            case "duplicate_anchor": root = root with { Observation = root.Observation with
                { History = [.. root.Observation.History, root.Observation.History[1]] } }; break;
            case "extra_positive": entry = entry with { Relics = [.. entry.Relics, Relic("SmallCapsule")] }; break;
            case "duplicate_positive": entry = entry with { Relics = [.. entry.Relics, Relic("WingedBoots")] }; break;
            case "melted" or "wax" or "stacked": entry = entry with { Relics = [Relic("RingOfTheSnake"),
                new("WingedBoots", new Dictionary<string, int> { ["isMelted"] = change == "melted" ? 1 : 0,
                    ["isWax"] = change == "wax" ? 1 : 0, ["stackCount"] = change == "stacked" ? 2 : 1 })] }; break;
            case "missing_flags": entry = entry with { Relics = [Relic("RingOfTheSnake"), new("WingedBoots", new Dictionary<string, int>())] }; break;
            default: entry = entry with { Relics = [Relic("RingOfTheSnake"), Relic(change)] }; break;
        }
        root.Observation.History[1] = root.Observation.History[1] with { Detail = PublicJson.Serialize(entry) };
        Assert.False(NativeNeowCondition.TryCreate(root, prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(NaturalSourceCollector.ScriptVersion)]
    [InlineData(NaturalSourceCollector.BoundedEventScriptVersion)]
    public async Task NativeGeneratedSecondCombatEntryRetainsTheCertifiedOrigin(string script)
    {
        var prior = Prior with { EligibleCombats = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1000, OutsideCombatScript: script) };
        // Fixed natural-run fixture with a declared later target; this is a unit
        // check of a native public entry, not a sampled dataset or posterior fit.
        var recipe = prior.Draw(new Rng(8001, "nosl-native-tape-source-draw-v1"))
            with { CombatIndex = 1, DecisionIndex = 0 };
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, new(recipe));
        Assert.NotNull(world);
        Assert.True(world.NativeRun.TotalFloor >= 3);
        var publicPacket = world.Observe();
        Assert.True(NativeNeowCondition.TryCreate(publicPacket, prior, out var condition, out var reason), reason);
        Assert.Contains(world.NativeRun.Players.Single().Relics,
            relic => relic.GetType().Name == condition!.TargetRelicId && !relic.IsWax && !relic.IsMelted);
    }

    [Fact]
    public void OfficialRelicAndLaterAncientPoolsCannotCreateANeowPositive()
    {
        NaturalSourceCollector.InitializeNativeModels();
        Type[] positiveTypes = [typeof(ArcaneScroll), typeof(BoomingConch), typeof(FishingRod), typeof(GoldenPearl),
            typeof(Kaleidoscope), typeof(LeadPaperweight), typeof(LostCoffer), typeof(NeowsTorment),
            typeof(NewLeaf), typeof(PhialHolster), typeof(PreciseScissors), typeof(ScrollBoxes),
            typeof(WingedBoots), typeof(LavaRock), typeof(SmallCapsule), typeof(NutritiousOyster),
            typeof(StoneHumidifier), typeof(NeowsTalisman), typeof(Pomander)];
        Assert.Equal(19, positiveTypes.Distinct().Count());
        foreach (Type type in positiveTypes)
        {
            Assert.True(NativeNeowCondition.TryCreate(Root(type.Name), Prior, out _, out var reason), reason);
            Assert.Equal(RelicRarity.Ancient, ((RelicModel)ModelDb.Get(type)).Rarity);
        }
        Type[] forbiddenOrigins = [.. positiveTypes, typeof(NeowsBones)];
        var officialPools = ModelDb.AllCharacters.Select(character => character.RelicPool)
            .Append(SharedRelicPool.Instance);
        foreach (var pool in officialPools)
            Assert.All(pool.AllRelics, relic => Assert.DoesNotContain(relic.GetType(), forbiddenOrigins));

        var laterAncients = ModelDb.All<AncientEventModel>().Where(ancient => ancient is not Neow).ToArray();
        Assert.Equal(7, laterAncients.Length);
        foreach (var ancient in laterAncients)
            Assert.All(ancient.AllPossibleOptions, relic => Assert.DoesNotContain(relic.GetType(), forbiddenOrigins));
        Assert.All(new Hive().AncientPool.Concat(new Glory().AncientPool).Concat(SharedAncientPool.All),
            type => Assert.NotEqual(typeof(Neow), type));
        // Shared pools legitimately include OTHER Ancient relics. Exclusion of
        // these specific types is the closure; a blanket rarity assertion is false.
        Assert.Contains(SharedRelicPool.Instance.AllRelics, relic => relic.Rarity == RelicRarity.Ancient);
    }

    [Fact]
    public async Task EveryNativeNeowLatentPoolHasTheReviewedShapeAndFirstPositive()
    {
        NaturalSourceCollector.InitializeNativeModels();
        Assert.True(NativeNeowCondition.TryCreate(Root(), Prior, out var condition, out var reason), reason);
        var counts = new HashSet<int>(); var shapes = new HashSet<string>();
        // Exhaust the ten curse branches and three Boolean choices directly at the
        // native option generator; no run seed search or success quota is involved.
        for (int curse = 0; curse < 10; curse++)
            for (int booleans = 0; booleans < 8; booleans++)
            {
                var run = new RunState("neow-shape-proof", ascensionLevel: 10);
                run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
                _ = run.CurrentAncientEventType; // Native room factory resolves this before entering Neow.
                var room = new EventRoom(() => (Neow)ModelDb.Event<Neow>().MutableClone());
                run.PushRoom(room);
                var factor = ConditionalShuffleProposal.Factor(10, curse);
                int draw = 0; NativeNeowProposal? proposal = null;
                using (LabelRandomScope.Enter(_ => draw++ == 0
                    ? factor.BucketStart << 11
                    : ((booleans >> (draw - 2)) & 1) == 0 ? 0UL : ulong.MaxValue,
                    (rng, items) =>
                    {
                        Assert.IsType<Neow>(room.Event);
                        Assert.Same(room.Event.Rng, rng);
                        Type[] pool = items.Cast<Type>().ToArray();
                        counts.Add(pool.Length); shapes.Add(string.Join(",", pool.Select(t => t.Name).Order()));
                        Assert.DoesNotContain(typeof(MassiveScroll), pool);
                        Assert.Contains(typeof(ScrollBoxes), pool);
                        proposal = condition!.CreateProposal(pool, () => ulong.MaxValue);
                        int word = 0;
                        return LabelRandomScope.Enter(_ => proposal.Plan.RawWords[word++]);
                    }))
                    await room.Enter(run);
                Assert.NotNull(proposal);
                Assert.Equal(nameof(WingedBoots), room.Event.CurrentOptions.First(option => !option.IsLocked).Key);
                Assert.Equal(NativeNeowCondition.RootEnvelope, proposal.RootEnvelope);
                await room.Exit(run); run.PopCurrentRoom();
            }
        Assert.Equal([14, 15, 16], counts.Order());
        Assert.Equal(52, shapes.Count);
    }

    [Fact]
    public void NativePoolDriftIsAnErrorWhileTargetAbsenceIsAProvedNonmatch()
    {
        Type[] pool = [typeof(ArcaneScroll), typeof(BoomingConch), typeof(FishingRod), typeof(GoldenPearl),
            typeof(Kaleidoscope), typeof(LeadPaperweight), typeof(LostCoffer), typeof(NeowsTorment),
            typeof(NewLeaf), typeof(PhialHolster), typeof(PreciseScissors), typeof(ScrollBoxes),
            typeof(WingedBoots), typeof(LavaRock), typeof(NutritiousOyster), typeof(NeowsTalisman)];
        Assert.True(NativeNeowCondition.TryCreate(Root("SmallCapsule"), Prior, out var condition, out var reason), reason);
        Assert.Throws<NativePublicConstraintMismatchException>(() => condition!.CreateProposal(pool, () => ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal(pool.Skip(2).ToArray(), () => ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal([.. pool, typeof(MassiveScroll)], () => ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal([typeof(MassiveScroll), .. pool.Skip(1)], () => ulong.MaxValue));
        Assert.Throws<InvalidOperationException>(() => condition!.CreateProposal([pool[1], .. pool.Skip(1)], () => ulong.MaxValue));
    }

    [Fact]
    public void VariableLatentPoolsRetainTheirExactNativeJointLawAfterRootCorrection()
    {
        const int bits = 3, domain = 1 << bits;
        var envelope = NativeNeowCondition.MaximumEnvelope([3, 4], bits);
        var correctContextMass = new List<ShuffleRational>();
        var wrongContextMass = new List<ShuffleRational>();
        foreach (int count in new[] { 3, 4 })
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
                if (order[0] != 0) continue;
                string key = string.Join(",", order); native[key] = native.GetValueOrDefault(key) + 1;
            }
            var acceptedMass = new ShuffleRational(0, 1); var wronglyAcceptedMass = acceptedMass;
            foreach (var pair in native)
            {
                int[] order = pair.Key.Split(',').Select(int.Parse).ToArray();
                var words = new Queue<ulong>(); var remaining = Enumerable.Range(1, count - 1).ToList();
                foreach (int item in order.Skip(1))
                {
                    if (remaining.Count > 1) words.Enqueue((ulong)(remaining.Count + remaining.IndexOf(item)));
                    remaining.Remove(item);
                }
                var plan = ConditionalShuffleProposal.Create(ids, ["0"],
                    () => words.Count > 0 ? words.Dequeue() : ulong.MaxValue, bits)!;
                Assert.Equal(order, plan.PhysicalPermutation);
                var proposal = new NativeNeowProposal(plan, envelope);
                int accepted = 0, correctionSpace = (int)proposal.AcceptanceProbability.Denominator;
                for (int draw = 0; draw < correctionSpace; draw++)
                    if (proposal.AcceptCorrection(() => (ulong)draw)) accepted++;
                var probability = new ShuffleRational(accepted, correctionSpace);
                Assert.Equal(proposal.AcceptanceProbability, probability);
                int permutations = Enumerable.Range(1, count - 1).Aggregate(1, (a, b) => a * b);
                // Both latent contexts have prior mass 1/2; proposal permutations
                // are uniform conditional on their first entry. Compare exact joint
                // mass against independently enumerated native raw-word worlds / E.
                var joint = probability.Multiply(1, 2 * permutations);
                Assert.Equal(new ShuffleRational(pair.Value * envelope.Denominator,
                    2 * space * envelope.Numerator), joint);
                acceptedMass = Add(acceptedMass, joint);
                var wrong = new ShuffleRational(plan.NativeToProposalRatio.Numerator * plan.Envelope.Denominator,
                    plan.NativeToProposalRatio.Denominator * plan.Envelope.Numerator).Multiply(1, 2 * permutations);
                wronglyAcceptedMass = Add(wronglyAcceptedMass, wrong);
            }
            correctContextMass.Add(acceptedMass); wrongContextMass.Add(wronglyAcceptedMass);
        }
        // Dividing each context by its own c_n silently changes its posterior odds.
        Assert.NotEqual(Ratio(correctContextMass[0], correctContextMass[1]),
            Ratio(wrongContextMass[0], wrongContextMass[1]));
    }

    [Fact]
    public void RationalBernoulliRejectsSurplusIntegersAcrossMultipleWords()
    {
        var probability = new ShuffleRational(1, (BigInteger.One << 64) + 1);
        var words = new Queue<ulong>([1, 1, 0, 0]);
        Assert.True(NativeNeowProposal.ExactRationalBernoulli(probability, () => words.Dequeue()));
        Assert.Empty(words);
        words = new([0, 1]);
        Assert.False(NativeNeowProposal.ExactRationalBernoulli(probability, () => words.Dequeue()));
        Assert.Empty(words);
        ulong Never() => throw new InvalidOperationException("Exact endpoints need no words");
        Assert.False(NativeNeowProposal.ExactRationalBernoulli(new(0, 1), Never));
        Assert.True(NativeNeowProposal.ExactRationalBernoulli(new(1, 1), Never));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeNeowProposal.ExactRationalBernoulli(new(2, 1), Never));
    }

    private static ShuffleRational Add(ShuffleRational a, ShuffleRational b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    private static ShuffleRational Ratio(ShuffleRational a, ShuffleRational b) =>
        new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
}
