using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Relics;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Events;
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
    public void PublicEntryAndDeclaredFirstCombatPriorCertifyOnlyTheRelicOrigin(string relic)
    {
        var root = Root(relic);
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.NotNull(condition);
        Assert.Equal(relic, condition.TargetRelicId);
        // A later packet in that same declared first combat has the same carry-in
        // evidence. Current inventory/card hooks do not establish its origin.
        root = root with { Status = "card_choice", Observation = root.Observation! with
        { Turn = 4, Relics = ["unrelated-current-relic"], History = [.. root.Observation.History, new("action", "later")] } };
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var later, out reason), reason);
        Assert.Equal(relic, later!.TargetRelicId);
        root.Observation.History[1] = new("mutated", "mutated");
        Assert.Equal(relic, condition.TargetRelicId);
    }

    [Theory]
    [InlineData("wider_combat", "first_native_combat_prior_required")]
    [InlineData("wider_floor", "first_native_combat_prior_required")]
    [InlineData("other_policy", "first_native_combat_prior_required")]
    [InlineData("wrong_character", "fresh_silent_entry_history_required")]
    [InlineData("duplicate_anchor", "fresh_silent_entry_history_required")]
    [InlineData("extra_relic", "exact_neow_entry_inventory_required")]
    [InlineData("missing_ring", "ordinary_starter_and_positive_required")]
    [InlineData("melted", "ordinary_starter_and_positive_required")]
    [InlineData("missing_flags", "ordinary_starter_and_positive_required")]
    [InlineData("MassiveScroll", "neow_positive_origin_not_certified")]
    [InlineData("NeowsBones", "neow_positive_origin_not_certified")]
    [InlineData("GoldenCompass", "neow_positive_origin_not_certified")]
    [InlineData("unknown", "neow_positive_origin_not_certified")]
    public void UnprovedOriginsFallbackWithoutExcludingTheRoot(string change, string expected)
    {
        var root = Root(); var prior = Prior;
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        switch (change)
        {
            case "wider_combat": prior = prior with { EligibleCombats = 2 }; break;
            case "wider_floor": prior = prior with { Execution = prior.Execution with { MaxFloors = 2 } }; break;
            case "other_policy": prior = prior with { Execution = prior.Execution with { SourcePolicyId = "other" } }; break;
            case "wrong_character": root.Observation.History[0] = new("combat_started", "Ironclad:A10"); break;
            case "duplicate_anchor": root = root with { Observation = root.Observation with
                { History = [.. root.Observation.History, root.Observation.History[1]] } }; break;
            case "extra_relic": entry = entry with { Relics = [.. entry.Relics, Relic("SmallCapsule")] }; break;
            case "missing_ring": entry = entry with { Relics = [Relic("LavaRock"), Relic("WingedBoots")] }; break;
            case "melted": entry = entry with { Relics = [Relic("RingOfTheSnake"),
                new("WingedBoots", new Dictionary<string, int> { ["isMelted"] = 1, ["isWax"] = 0, ["stackCount"] = 1 })] }; break;
            case "missing_flags": entry = entry with { Relics = [Relic("RingOfTheSnake"), new("WingedBoots", new Dictionary<string, int>())] }; break;
            default: entry = entry with { Relics = [Relic("RingOfTheSnake"), Relic(change)] }; break;
        }
        root.Observation.History[1] = root.Observation.History[1] with { Detail = PublicJson.Serialize(entry) };
        Assert.False(NativeNeowCondition.TryCreate(root, prior, out var condition, out var reason));
        Assert.Null(condition); Assert.Equal(expected, reason);
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
