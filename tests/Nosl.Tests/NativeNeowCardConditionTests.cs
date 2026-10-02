using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;

namespace Nosl.Tests;

public sealed class NativeNeowCardConditionTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 1, EligibleDecisionsPerCombat = 1,
        Execution = new(MaxFloors: 3, SourceDecisionHorizon: 32,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Theory]
    [InlineData(nameof(ArcaneScroll), 1)]
    [InlineData(nameof(ScrollBoxes), 6)]
    [InlineData(nameof(Kaleidoscope), 6)]
    [InlineData(nameof(Kaleidoscope), 6, 1UL << 48)] // Native rare base-odds arm, nonzero upgrade roll.
    [InlineData(nameof(Kaleidoscope), 6, 1UL << 62)] // Native uncommon base-odds arm.
    public async Task ActualNativeOpeningConditionsEveryDisplayedCardAndReplaysContinuation(string relic, int count,
        ulong ordinaryWord = 1UL << 63)
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed;
        using (Enter(relic, _ => ordinaryWord))
        await using (var source = await NativeRunWorld.OpenAsync(Prior.Execution, "public-neow-cards-source:" + relic, 0))
        { Assert.NotNull(source); observed = source.Observe(); }
        // Only detached public values cross this boundary; neither source seed nor native graph is an input.
        observed = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(observed));
        Assert.True(NativeNeowCardCondition.TryCreate(observed, Prior, out var condition, out var reason), reason);
        Assert.Equal(count, condition!.BaseCardIds.Count);
        if (relic == nameof(ScrollBoxes))
        {
            var choice = Assert.IsType<PublicCardsObserved>(observed.PublicEvidence!.Events[5].Payload).Choice;
            Assert.Equal(choice.Bundles!.SelectMany(b => b).Select(c => c.Id), condition.BaseCardIds);
        }
        var oracle = new NativeRewardsOracle(927511);
        var consumed = new List<(LabelRandomAddressV1 Address, ulong Word)>();
        ulong RewardWord(LabelRandomAddressV1 address)
        {
            ulong word = oracle.Word(address); consumed.Add((address, word)); return word;
        }
        var proposalRandom = new Rng(11793);
        var proposal = new NativeNeowCardProposal(condition, oracle.WasVisited, oracle.ForceFresh, proposalRandom.NextUnsignedLong, (words, _) => Force(words));
        using (Enter(relic, RewardWord, proposal))
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "independent-neow-cards-proposal:" + relic, 0))
        {
            Assert.NotNull(world); proposal.ValidateCompletion();
            Assert.Equal(count, proposal.ConditionedCardCount); Assert.Equal(relic == nameof(Kaleidoscope) ? 12 : count, oracle.ConditionedCells);
            Assert.Equal(relic == nameof(Kaleidoscope) ? 2 : 0, proposal.ConditionedPoolShuffleCount);
            Assert.Equal(relic == nameof(Kaleidoscope) ? 18 : count, oracle.DistinctCells);
            if (relic == nameof(Kaleidoscope))
            {
                var unconditioned = new NativeRewardsOracle(927511);
                var upgrades = consumed.Where((_, i) => i % 3 == 2).ToArray();
                Assert.Equal(6, upgrades.Length);
                Assert.All(upgrades, item => Assert.Equal(unconditioned.Word(item.Address), item.Word));
                Assert.Equal(-0.05f, world.NativeRun.Players.Single().Odds.CardRarity.CurrentValue);
                var groups = Assert.IsType<PublicOffersObserved>(observed.PublicEvidence!.Events[5].Payload).Groups;
                Assert.Equal(groups.SelectMany(g => g.Offers).Where(o => o.OfferKind == PublicOfferKind.Card)
                    .Select(o => o.Card!.Id), condition.BaseCardIds);
            }
            Assert.Equal(condition.Envelope, proposal.NativeToProposalRatio);
            Assert.True(proposal.AcceptCorrection(() => throw new InvalidOperationException("Constant correction needs no random word")));
            // The opening choices and complete grant/bundle evidence match even though all ordinary cells differ.
            int end = observed.PublicEvidence!.Events.TakeWhile(e => e is not
                { OwnerOrdinal: 0, Payload: PublicOwnerEnded }).Count() + 1;
            Assert.Equal(PublicJson.Serialize(observed.PublicEvidence!.Events.Take(end)),
                PublicJson.Serialize(world.Observe().PublicEvidence!.Events.Take(end)));
            var replayOracle = oracle.ReplayCopy(); var replayRandom = new Rng(11793);
            var replay = new NativeNeowCardProposal(condition, replayOracle.WasVisited, replayOracle.ForceFresh, replayRandom.NextUnsignedLong, (words, _) => Force(words));
            using (Enter(relic, replayOracle.Word, replay))
            await using (var fork = await world.ForkForContinuationAsync())
            {
                replay.ValidateCompletion();
                Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
                var action = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId).Choose(world.Observe());
                await world.StepAsync(action); await fork.StepAsync(action);
                Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
                Assert.Equal(relic == nameof(Kaleidoscope) ? 12 : count, replayOracle.ConditionedCells);
            }
        }
        Assert.False(NativeNeowCardCondition.TryCreate(observed, Prior with { SchemaVersion = NativeTapePrior.Version }, out _, out reason));
        Assert.Equal("neow_cards_require_rewards_hybrid_prior", reason);
    }

    [Fact]
    public async Task MissingUnselectedBundleCannotBecomeASelectedOnlyCondition()
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed;
        using (Enter(nameof(ScrollBoxes), _ => 1UL << 63))
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "public-neow-cards-missing-bundle", 0))
        { Assert.NotNull(world); observed = world.Observe(); }
        var entries = observed.PublicEvidence!.Events;
        var choice = Assert.IsType<PublicCardsObserved>(entries[5].Payload).Choice;
        // A plain candidate reveal supplies no evidence of all three cards in either bundle.
        entries = entries.SetItem(5, new(5, 1, new PublicCardsObserved(choice with { Bundles = null })));
        var changed = observed with { PublicEvidence = new(PublicRunEvidence.Version, true, entries) };
        Assert.False(NativeNeowCardCondition.TryCreate(changed, Prior, out _, out var reason));
        Assert.Equal("scroll_boxes_requires_both_complete_displayed_bundles", reason);
    }

    [Theory]
    [InlineData(nameof(ArcaneScroll))]
    [InlineData(nameof(ScrollBoxes))]
    [InlineData(nameof(Kaleidoscope))]
    public async Task AliasedRewardsCellsFailBeforeAnyForcedWrites(string relic)
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed;
        using (Enter(relic, _ => 1UL << 63))
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "neow-alias-source:" + relic, 0))
        { Assert.NotNull(world); observed = world.Observe(); }
        Assert.True(NativeNeowCardCondition.TryCreate(observed, Prior, out var condition, out _));
        int writes = 0;
        var proposal = new NativeNeowCardProposal(condition!, _ => true, (_, _) => writes++, new Rng(777).NextUnsignedLong, (words, _) => Force(words));
        using (Enter(relic, _ => 1UL << 63, proposal))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeRunWorld.OpenAsync(
                Prior.Execution, "neow-alias-proposal:" + relic, 0));
            Assert.Contains("fresh Rewards cells", error.Message);
        }
        Assert.Equal(0, writes); Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FiniteUniformBucketMassIncludesDuplicatePositionsAndUnpickedConstraints(int bits)
    {
        NaturalSourceCollector.InitializeNativeModels();
        CardModel[] pool = [ModelDb.Card<Backflip>(), ModelDb.Card<DeadlyPoison>(), ModelDb.Card<Backflip>()];
        int domain = 1 << bits;
        int one = Enumerable.Range(0, domain).Count(word => pool[(int)((double)word / domain * pool.Length)] is Backflip);
        var oneMass = NativeNeowCardCondition.UniformMass(pool, nameof(Backflip), bits);
        Assert.Equal(new ShuffleRational(one, domain), oneMass);
        int joint = 0;
        for (int selectedWord = 0; selectedWord < domain; selectedWord++)
        for (int unpickedWord = 0; unpickedWord < domain; unpickedWord++)
            if (pool[(int)((double)selectedWord / domain * 3)] is Backflip
                && pool[(int)((double)unpickedWord / domain * 3)] is DeadlyPoison) joint++;
        var unpickedMass = NativeNeowCardCondition.UniformMass(pool, nameof(DeadlyPoison), bits);
        Assert.Equal(new ShuffleRational(joint, domain * domain), oneMass.Multiply(unpickedMass.Numerator, unpickedMass.Denominator));
        Assert.True(joint < one * domain);
        // q(w | displayed identities) * Z / B = p(w) / B, with root-fixed B = Z.
        var z = new ShuffleRational(joint, domain * domain);
        var q = new ShuffleRational(1, joint);
        Assert.Equal(new ShuffleRational(1, domain * domain), q.Multiply(z.Numerator, z.Denominator));
    }

    [Fact]
    public void FiniteKaleidoscopePermutationAndRarityLawLeavesUpgradeIndependent()
    {
        NaturalSourceCollector.InitializeNativeModels();
        const int bits = 3, domain = 1 << bits;
        string[] poolIds = ["a", "b", "c", "d"], prefix = ["c", "a", "d"];
        var plan = ConditionalShuffleProposal.Create(poolIds, prefix, new Rng(119).NextUnsignedLong, bits)!;
        int nativePermutations = 0;
        for (int first = 0; first < domain; first++)
        for (int second = 0; second < domain; second++)
        for (int third = 0; third < domain; third++)
        {
            string[] actual = poolIds.ToArray(); int[] words = [first, second, third];
            for (int n = 4, step = 0; n > 1; n--, step++)
            {
                int index = (int)((double)words[step] / domain * n);
                (actual[n - 1], actual[index]) = (actual[index], actual[n - 1]);
            }
            if (actual.Take(3).SequenceEqual(prefix)) nativePermutations++;
        }
        Assert.Equal(new ShuffleRational(nativePermutations, domain * domain * domain), plan.NativeToProposalRatio);
        CardModel[] pool = [ModelDb.Card<Backflip>(), ModelDb.Card<DeadlyPoison>(), ModelDb.Card<Backflip>(),
            ModelDb.Card<Accelerant>(), ModelDb.Card<SerpentForm>()];
        var thresholds = new LabelCardRarityThresholds(0.27f, 0.63f);
        int identityWords = 0, identityAndUpgradeWords = 0;
        for (int rarityWord = 0; rarityWord < domain; rarityWord++)
        for (int indexWord = 0; indexWord < domain; indexWord++)
        {
            float roll = (float)((double)rarityWord / domain);
            CardRarity rarity = roll < thresholds.RareUpperExclusive ? CardRarity.Rare
                : roll < thresholds.UncommonUpperExclusive ? CardRarity.Uncommon : CardRarity.Common;
            var candidates = pool.Where(c => c.Rarity == rarity).ToArray();
            if (candidates[(int)((double)indexWord / domain * candidates.Length)] is not Backflip) continue;
            identityWords++;
            // A later public upgrade fact can constrain this untouched third cell.
            for (int upgradeWord = 0; upgradeWord < domain; upgradeWord++)
                if (upgradeWord < 2) identityAndUpgradeWords++;
        }
        var identityMass = NativeNeowCardCondition.RarityMass(pool, nameof(Backflip), thresholds, bits);
        Assert.Equal(new ShuffleRational(identityWords, domain * domain), identityMass);
        var z = plan.NativeToProposalRatio.Multiply(identityMass.Numerator, identityMass.Denominator);
        Assert.Equal(new ShuffleRational(nativePermutations * identityAndUpgradeWords, BigInteger.Pow(domain, 6)),
            z.Multiply(2, domain));
        var q = new ShuffleRational(1, nativePermutations * identityWords * domain);
        Assert.Equal(new ShuffleRational(1, BigInteger.Pow(domain, 6)), q.Multiply(z.Numerator, z.Denominator));
    }

    [Fact]
    public async Task KaleidoscopeMissingSecondOfferCannotUseChosenDeckCards()
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed;
        using (Enter(nameof(Kaleidoscope), _ => 1UL << 63))
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "kaleidoscope-missing-offer", 0))
        { Assert.NotNull(world); observed = world.Observe(); }
        var entries = observed.PublicEvidence!.Events;
        var offers = Assert.IsType<PublicOffersObserved>(entries[5].Payload);
        entries = entries.SetItem(5, new(5, 1, new PublicOffersObserved(offers.Groups.RemoveAt(1))));
        var changed = observed with { PublicEvidence = new(PublicRunEvidence.Version, true, entries) };
        Assert.False(NativeNeowCardCondition.TryCreate(changed, Prior, out _, out var reason));
        Assert.Equal("kaleidoscope_requires_both_displayed_offers", reason);
    }

    private static IDisposable Enter(string relic, Func<LabelRandomAddressV1, ulong> rewards,
        NativeNeowCardProposal? proposal = null) => LabelRandomScope.EnterRewardProvenance(rewards, StateWord,
        beginShuffle: (rng, items) =>
        {
            if (items.Count == 0 || items.Any(item => item is not Type)) return proposal?.BeginShuffle(rng, items);
            var ids = items.Cast<Type>().Select(t => t.Name).ToArray();
            if (!ids.Contains(relic)) return null;
            var plan = ConditionalShuffleProposal.Create(ids, [relic, nameof(WingedBoots)], new Rng(719).NextUnsignedLong)!;
            return Force(plan.RawWords);
        }, beginRewardCardSelection: proposal is null ? null : proposal.BeginSelection,
        beginNeowInitialOptions: context =>
        {
            proposal?.AttachHypotheticalRun((Sts2Sim.Core.Runs.RunState)context.Neow.Owner.RunState);
            int index = context.AllowedCurses.ToList().IndexOf(typeof(DowsingRod));
            return Force([ConditionalShuffleProposal.Factor(context.AllowedCurses.Count, index).BucketStart << 11, 0, 0, 0]);
        }, beginScrollBoxes: proposal is null ? null : proposal.BeginScrollBoxes);

    private static IDisposable Force(IReadOnlyList<ulong> words)
    {
        int index = 0;
        return LabelRandomScope.Enter(_ => words[index++]);
    }

    private static ulong StateWord(LabelRandomState state)
    {
        byte[] bytes = new[] { state.State0, state.State1, state.State2, state.State3 }.SelectMany(BitConverter.GetBytes).ToArray();
        return BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}
