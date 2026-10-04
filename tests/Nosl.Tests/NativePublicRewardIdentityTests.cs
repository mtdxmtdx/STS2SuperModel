using System.Collections.Immutable;
using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Factories;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.CardPools;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicRewardIdentityTests
{
    private sealed class FixedPool(params CardModel[] cards) : CardPoolModel
    { public override IReadOnlyList<CardModel> AllCards => cards; }

    private static (RunState Run, Player Player) Player(string seed)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState(seed, ascensionLevel: 10);
        var player = Sts2Sim.Core.Entities.Players.Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        return (run, player);
    }

    private static PublicRunEvidence Evidence(Player player, RewardsSet rewards, int floor = 0)
    {
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, NativePublicRunEvidence.Assets(player)));
        long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, floor);
        recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory, NativePublicRunEvidence.Assets(player)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, floor);
        recorder.Record(owner, new PublicOffersObserved([new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.ChooseOne,
            rewards.Card.Options.Select((c, i) => new PublicOffer("card:card:" + i, PublicOfferKind.Card, card: PublicViews.Card(c)))
                .Append(new("card:skip", PublicOfferKind.Skip)).ToImmutableArray())]));
        return PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(recorder.Capture()));
    }

    private static string Offer(RewardsSet rewards) => PublicJson.Serialize(new
    {
        cards = rewards.Card.Options.Select(PublicViews.Card).ToArray(), gold = rewards.Gold.Amount,
        potion = rewards.Potion?.Potion?.GetType().Name,
        extra = rewards.ExtraRewards.OfType<CardReward>().Select(r => r.Options.Select(PublicViews.Card).ToArray()).ToArray(),
    });

    private static async Task Relics(Player player, string variant)
    {
        Type[] types = variant switch
        {
            "rug" => [typeof(DingyRug)], "gem" => [typeof(PrismaticGem)],
            "both" => [typeof(DingyRug), typeof(PrismaticGem)],
            "late" => [typeof(MoltenEgg), typeof(ToxicEgg), typeof(FrozenEgg), typeof(WingCharm)],
            "candy" => [typeof(LastingCandy)], "extras" => [typeof(PrayerWheel)], _ => [],
        };
        foreach (var type in types) await RelicCmd.Obtain((RelicModel)ModelDb.Get(type), player);
        if (variant == "candy")
        {
            // The real native counter is advanced by the real before-offer hook; no private state write.
            var previous = RewardsSet.GenerateFor(player, RoomType.Monster, player.RunState);
            await player.Relics.OfType<LastingCandy>().Single().BeforeCombatRewardOffered(previous, null!);
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("rug")]
    [InlineData("gem")]
    [InlineData("both")]
    [InlineData("late")]
    [InlineData("candy")]
    [InlineData("extras")]
    public async Task ActualNativePrimaryOffersPreserveEveryIdentityAndAllUnconditionedContent(string variant)
    {
        const ulong ordinary = 1UL << 63;
        PublicRunEvidence evidence; string expected;
        using (LabelRandomScope.EnterRewardProvenance(_ => ordinary, StateWord))
        {
            var (run, player) = Player("observed-native-reward:" + variant);
            await Relics(player, variant);
            var rewards = RewardsSet.GenerateFor(player, RoomType.Monster, run);
            evidence = Evidence(player, rewards); expected = Offer(rewards);
            Assert.Equal(variant == "candy" ? 4 : 3, rewards.Card.Options.Count);
        }
        var condition = NativePublicRewardCondition.Create(evidence);
        Assert.Single(condition.Targets);
        Assert.True(condition.Envelope.Numerator * 1000 < condition.Envelope.Denominator);
        var visited = new HashSet<LabelRandomAddressV1>();
        var forced = new Dictionary<LabelRandomAddressV1, ulong>();
        var random = new Rng(7281);
        var proposal = new NativePublicRewardProposal(condition, visited.Contains, (address, word) =>
        {
            Assert.DoesNotContain(address, visited); forced.Add(address, word);
        }, random.NextUnsignedLong);
        bool conditionNow = false;
        using (LabelRandomScope.EnterRewardProvenance(address =>
        {
            visited.Add(address); return forced.GetValueOrDefault(address, ordinary);
        }, StateWord, beginCombatReward: context => conditionNow ? proposal.BeginCombatReward(context, 0) : null,
            beginRewardCardSelection: proposal.BeginSelection))
        {
            var (run, player) = Player("independent-hypothetical-reward:" + variant);
            await Relics(player, variant);
            conditionNow = true;
            var rewards = RewardsSet.GenerateFor(player, RoomType.Monster, run);
            Assert.Equal(expected, Offer(rewards));
            proposal.ValidateCompletion();
            Assert.Equal(3, proposal.ConditionedCardCount); Assert.Equal(6, forced.Count);
            Assert.True(proposal.NativeToProposalRatio.Numerator * proposal.Envelope.Denominator
                <= proposal.NativeToProposalRatio.Denominator * proposal.Envelope.Numerator);
            Assert.True(proposal.NativeToProposalRatio.Numerator < proposal.NativeToProposalRatio.Denominator);
        }
    }

    [Fact]
    public async Task CompleteRealNativePublicPrefixMapsOneOfferDespiteRefreshesAndIncludesUnpickedCards()
    {
        var result = await NaturalSourceCollector.CollectAsync(new NaturalSourceOptions(MaxFloors: 8,
            MaxRoots: 2, MaxRootsPerCombat: 1, SeedPrefix: "owned-native-opening",
            ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version));
        Assert.Null(Assert.Single(result.Runs).Error);
        var evidence = result.Roots[1].PublicRoot.PublicEvidence!;
        var condition = NativePublicRewardCondition.Create(evidence);
        var target = Assert.Single(condition.Targets).Value;
        var observed = Assert.IsType<PublicOffersObserved>(evidence.Events[(int)target.OfferEventOrdinal].Payload);
        Assert.Equal(observed.Groups.SelectMany(g => g.Offers).Where(o => o.OfferKind == PublicOfferKind.Card)
            .Take(3).Select(o => o.Card!.Id), target.BaseCardIds);
        Assert.Equal(0, target.CombatIndex); Assert.Equal(2, target.Floor);
        Assert.True(evidence.Events.Count(e => e.Payload is PublicOffersObserved) > condition.Targets.Count);
        Assert.Equal(3, target.BaseCardIds.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FiniteHiddenRarityFallbackAndDuplicateIndexMassRetainPityPosterior(int bits)
    {
        var (_, player) = Player("toy-fallback");
        CardModel a = ModelDb.Card<DeadlyPoison>(), b = ModelDb.Card<Backflip>(), u = ModelDb.Card<Accelerant>();
        int domain = 1 << bits;
        var branches = new[] { new LabelRewardCardBranch(CardRarity.Rare, new[] { a, b }),
            new LabelRewardCardBranch(CardRarity.Uncommon, new[] { u }),
            new LabelRewardCardBranch(CardRarity.Common, new[] { a, b }) };
        var contexts = new[] { Context(player, new(0.25f, 0.5f), branches), Context(player, new(0.25f, 0.75f), branches) };
        var masses = contexts.Select(context => NativeRewardIdentityMath.Arms(context, nameof(DeadlyPoison), bits)
            .Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass)).ToArray();
        Assert.Equal(new ShuffleRational(3, 8), new ShuffleRational(masses[0], domain * domain));
        Assert.Equal(new ShuffleRational(1, 4), new ShuffleRational(masses[1], domain * domain));
        Assert.Equal(new ShuffleRational(3, 5), new ShuffleRational(masses[0], masses[0] + masses[1]));
        var noFallback = contexts[0] with { Kind = LabelRewardCardSelectionKind.LegacyCombat,
            Branches = new[] { new LabelRewardCardBranch(CardRarity.Rare, Array.Empty<CardModel>()), branches[1], branches[2] } };
        Assert.Equal(new ShuffleRational(1, 4), new ShuffleRational(NativeRewardIdentityMath.Arms(noFallback,
            nameof(DeadlyPoison), bits).Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass), domain * domain));
        // Exact per-path q * Z / B = p / B, including hidden reset-versus-increment branches.
        var laterMasses = new int[2];
        for (int h = 0; h < 2; h++)
        {
            var arms = NativeRewardIdentityMath.Arms(contexts[h], nameof(DeadlyPoison), bits);
            int accepted = 0, rareAccepted = 0;
            for (int rarityWord = 0; rarityWord < domain; rarityWord++)
            for (int indexWord = 0; indexWord < domain; indexWord++)
            {
                float roll = (float)((double)rarityWord / domain);
                int armIndex = roll < 0.25f ? 0 : roll < (h == 0 ? 0.5f : 0.75f) ? 1 : 2;
                var candidates = branches[armIndex].Candidates;
                bool match = candidates[(int)((double)indexWord / domain * candidates.Count)] == a;
                var arm = arms[armIndex];
                bool proposed = (ulong)rarityWord >= arm.RarityStart && (ulong)rarityWord < arm.RarityStart + arm.RaritySize
                    && arm.MatchingIndices.Any(i => (ulong)indexWord >= i.BucketStart && (ulong)indexWord < i.BucketStart + i.BucketSize);
                Assert.Equal(match, proposed);
                if (!match) continue;
                accepted++; if (armIndex == 0) rareAccepted++;
                // A later public outcome has mass 1/4 after a rare reset, 1/2 after a common increment.
                for (int next = 0; next < domain; next++)
                    if (next < (armIndex == 0 ? domain / 4 : domain / 2)) laterMasses[h]++;
                var q = new ShuffleRational(1, masses[h]);
                var zOverB = new ShuffleRational(2 * masses[h], domain * domain);
                Assert.Equal(new ShuffleRational(2, domain * domain), q.Multiply(zOverB.Numerator, zOverB.Denominator));
            }
            Assert.Equal(masses[h], new BigInteger(accepted));
            Assert.Equal(domain * domain / 8, rareAccepted); // This latent mass resets later rarity odds.
        }
        Assert.Equal(new ShuffleRational(5, 32), new ShuffleRational(laterMasses[0], domain * domain * domain));
        Assert.Equal(new ShuffleRational(3, 32), new ShuffleRational(laterMasses[1], domain * domain * domain));
        Assert.Equal(new ShuffleRational(5, 8), new ShuffleRational(laterMasses[0], laterMasses.Sum()));
        var duplicate = Context(player, null, [new(null, new[] { a, b, a })]);
        BigInteger actual = Enumerable.Range(0, domain).Count(word => (int)((double)word / domain * 3) != 1);
        var plan = NativeRewardIdentityMath.Create(duplicate, nameof(DeadlyPoison), new Rng(117).NextUnsignedLong, bits);
        Assert.Single(plan.RawWords); Assert.Equal(new ShuffleRational(actual, domain), plan.NativeToProposalRatio);
        var certificate = NativeRewardPoolCertificate.FromPools([new[] { a, b, a }], [nameof(DeadlyPoison)], bits);
        Assert.Equal(new ShuffleRational(actual, domain), certificate.Envelope);
    }

    [Fact]
    public void NativeCreationFallbackPreservesRolledRarityAndItsActualFutureOdds()
    {
        var (_, player) = Player("actual-fallback-pity");
        var a = ModelDb.Card<DeadlyPoison>(); var b = ModelDb.Card<Backflip>();
        float before = player.Odds.CardRarity.CurrentValue;
        LabelRewardCardSelectionContext? observed = null;
        using (LabelRandomScope.Enter(_ => 0, beginRewardCardSelection: context =>
        {
            observed = context;
            Assert.Equal(LabelRewardCardSelectionKind.CreationOptions, context.Kind);
            Assert.Equal(new CardModel[] { a, b }, context.Branches[0].Candidates);
            Assert.Equal(new CardModel[] { a, b }, context.RemainingCards);
            return null;
        }))
        {
            var options = new CardCreationOptions([new FixedPool(a, a, b)], CardCreationSource.Other, CardRarityOddsType.BossEncounter)
                .WithFlags(CardCreationFlags.ForceRarityOddsChange | CardCreationFlags.NoUpgradeRoll);
            var chosen = Assert.Single(CardFactory.CreateForReward(player, 1, options));
            Assert.Equal(a.Id, chosen.Id);
        }
        Assert.NotNull(observed); Assert.Equal(CardRarity.Common, a.Rarity);
        // Boss rare was hidden, fallback displayed common, and native rare reset retained.
        Assert.Equal(-0.05f, player.Odds.CardRarity.CurrentValue);
        using (LabelRandomScope.Enter(_ => ulong.MaxValue))
            _ = CardFactory.CreateForReward(player, 1, new CardCreationOptions([new FixedPool(a, b)],
                CardCreationSource.Encounter, CardRarityOddsType.RegularEncounter).WithFlags(CardCreationFlags.NoUpgradeRoll));
        Assert.Equal(before + player.Odds.CardRarity.RarityGrowth, player.Odds.CardRarity.CurrentValue);
    }

    [Fact]
    public void FloatPreimagesIncludeRoundedOneAndExactlyMatchNativeBoundaryNeighbors()
    {
        foreach (float threshold in new[] { -0.01f, 0f, 0.0149f, 0.3349f, 0.5f, 1f, float.PositiveInfinity })
        {
            ulong first = NativeRewardIdentityMath.FloatLowerBound(threshold);
            const ulong domain = 1UL << 53;
            foreach (ulong high in new[] { first == 0 ? 0 : first - 1, Math.Min(first, domain - 1), domain - 1 })
            {
                using var scope = LabelRandomScope.Enter(_ => high << 11);
                Assert.Equal(high < first, new Rng(17).NextFloat() < threshold);
            }
        }
        Assert.True(NativeRewardIdentityMath.FloatLowerBound(1f) < (1UL << 53));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyReadRewardsAndFullStateRngAreExplicitProofFailures(bool wrongPartition)
    {
        const ulong word = 1UL << 63;
        PublicRunEvidence evidence;
        using (LabelRandomScope.EnterRewardProvenance(_ => word, StateWord))
        {
            var (run, player) = Player("alias-observation");
            evidence = Evidence(player, RewardsSet.GenerateFor(player, RoomType.Monster, run));
        }
        var condition = NativePublicRewardCondition.Create(evidence);
        var proposal = new NativePublicRewardProposal(condition, _ => true, (_, _) => throw new Exception("Must not force"), () => word);
        using var scope = wrongPartition
            ? LabelRandomScope.Enter(StateWord, beginCombatReward: c => proposal.BeginCombatReward(c, 0), beginRewardCardSelection: proposal.BeginSelection)
            : LabelRandomScope.EnterRewardProvenance(_ => word, StateWord,
                beginCombatReward: c => proposal.BeginCombatReward(c, 0), beginRewardCardSelection: proposal.BeginSelection);
        var (native, p) = Player("alias-world");
        Assert.Throws<InvalidOperationException>(() => proposal.BeginCombatReward(
            new LabelCombatRewardContext(p, new Rng(42), RoomType.Monster, null, null, 1f), 0));
        var error = Assert.Throws<InvalidOperationException>(() => RewardsSet.GenerateFor(p, RoomType.Monster, native));
        Assert.Contains(wrongPartition ? "provenance" : "fresh", error.Message);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    private static ulong StateWord(LabelRandomState state)
    {
        byte[] bytes = new[] { state.State0, state.State1, state.State2, state.State3 }.SelectMany(BitConverter.GetBytes).ToArray();
        return BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static LabelRewardCardSelectionContext Context(Player player, LabelCardRarityThresholds? thresholds,
        IReadOnlyList<LabelRewardCardBranch> branches) => new(player, player.PlayerRng.Rewards,
        LabelRewardCardSelectionKind.CreationOptions, 0, 1, CardCreationSource.Other, CardCreationFlags.NoUpgradeRoll,
        CardRarityOddsType.RegularEncounter, thresholds, false, [], branches);
}
