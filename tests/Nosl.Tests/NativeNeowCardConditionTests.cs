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
    [InlineData(nameof(LostCoffer), 3)]
    [InlineData(nameof(LostCoffer), 3, 1UL << 48)]
    [InlineData(nameof(LostCoffer), 3, 1UL << 62)]
    [InlineData(nameof(Kaleidoscope), 6)]
    [InlineData(nameof(Kaleidoscope), 6, 1UL << 48)] // Native rare base-odds arm, nonzero upgrade roll.
    [InlineData(nameof(Kaleidoscope), 6, 1UL << 62)] // Native uncommon base-odds arm.
    [InlineData(nameof(NewLeaf), 1)]
    [InlineData(nameof(LeadPaperweight), 2)] // Common roll falls forward to uncommon.
    [InlineData(nameof(LeadPaperweight), 2, 1UL << 48)]
    [InlineData(nameof(LeadPaperweight), 2, 1UL << 62)]
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
            Assert.Equal(count, proposal.ConditionedCardCount);
            int expectedConditioned = relic == nameof(NewLeaf) ? 0 : relic == nameof(LeadPaperweight) ? 4
                : relic == nameof(Kaleidoscope) ? 12 : relic == nameof(LostCoffer) ? 8 : count;
            Assert.Equal(expectedConditioned, oracle.ConditionedCells);
            Assert.Equal(relic == nameof(LostCoffer) ? 1 : 0, proposal.ConditionedPotionCount);
            Assert.Equal(relic == nameof(Kaleidoscope) ? 2 : 0, proposal.ConditionedPoolShuffleCount);
            Assert.Equal(relic == nameof(NewLeaf) ? 0 : relic == nameof(LeadPaperweight) ? 6
                : relic == nameof(Kaleidoscope) ? 18 : relic == nameof(LostCoffer) ? 11 : count, oracle.DistinctCells);
            if (relic is nameof(Kaleidoscope) or nameof(LostCoffer) or nameof(LeadPaperweight))
            {
                var unconditioned = new NativeRewardsOracle(927511);
                var upgrades = consumed.Where((_, i) => i % 3 == 2).ToArray();
                Assert.Equal(relic == nameof(LeadPaperweight) ? 2 : relic == nameof(Kaleidoscope) ? 6 : 3, upgrades.Length);
                Assert.All(upgrades, item => Assert.Equal(unconditioned.Word(item.Address), item.Word));
                Assert.Equal(-0.05f, world.NativeRun.Players.Single().Odds.CardRarity.CurrentValue);
                if (relic == nameof(LeadPaperweight))
                {
                    var choice = Assert.IsType<PublicCardsObserved>(observed.PublicEvidence!.Events[5].Payload).Choice;
                    Assert.Equal(choice.Candidates.Select(c => c.Id), condition.BaseCardIds);
                    Assert.Empty(Assert.IsType<PublicCardsChosen>(observed.PublicEvidence.Events[6].Payload).Selection);
                }
                else
                {
                    var groups = Assert.IsType<PublicOffersObserved>(observed.PublicEvidence!.Events[5].Payload).Groups;
                    Assert.Equal(groups.SelectMany(g => g.Offers).Where(o => o.OfferKind == PublicOfferKind.Card)
                        .Select(o => o.Card!.Id), condition.BaseCardIds);
                }
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
                Assert.Equal(expectedConditioned, replayOracle.ConditionedCells);
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
    [InlineData(nameof(LostCoffer))]
    [InlineData(nameof(ArcaneScroll), true)]
    [InlineData(nameof(ScrollBoxes), true)]
    [InlineData(nameof(Kaleidoscope), true)]
    [InlineData(nameof(LostCoffer), true)]
    [InlineData(nameof(LeadPaperweight))]
    [InlineData(nameof(LeadPaperweight), true)]
    public async Task AliasedRewardsOrLegacyFullStateLawFailBeforeAnyForcedWrites(string relic, bool legacyLaw = false)
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed;
        using (Enter(relic, _ => 1UL << 63))
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "neow-alias-source:" + relic, 0))
        { Assert.NotNull(world); observed = world.Observe(); }
        Assert.True(NativeNeowCardCondition.TryCreate(observed, Prior, out var condition, out _));
        int writes = 0;
        var proposal = new NativeNeowCardProposal(condition!, _ => true, (_, _) => writes++, new Rng(777).NextUnsignedLong, (words, _) => Force(words));
        using (Enter(relic, _ => 1UL << 63, proposal, rewardsProvenance: !legacyLaw))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeRunWorld.OpenAsync(
                Prior.Execution, "neow-alias-proposal:" + relic, 0));
            Assert.Contains(legacyLaw ? "tagged native Rewards provenance" : "fresh Rewards cells", error.Message);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostCofferRequiresUnpickedCardsAndTheDisplayedPotion(bool removePotion)
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed;
        using (Enter(nameof(LostCoffer), _ => 1UL << 63))
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "lost-coffer-missing-public-offer", 0))
        { Assert.NotNull(world); observed = world.Observe(); }
        var entries = observed.PublicEvidence!.Events;
        var offers = Assert.IsType<PublicOffersObserved>(entries[5].Payload);
        var group = offers.Groups.Single(g => g.Offers.Any(o => o.OfferKind == (removePotion ? PublicOfferKind.Potion : PublicOfferKind.Card)));
        var groups = removePotion ? offers.Groups.Remove(group)
            : offers.Groups.SetItem(offers.Groups.IndexOf(group), new PublicOfferGroup(group.GroupKind, group.SelectionMode, group.Offers.RemoveAt(1), group.AlternativeToGroupIndex));
        entries = entries.SetItem(5, new(5, 1, new PublicOffersObserved(groups)));
        var changed = observed with { PublicEvidence = new(PublicRunEvidence.Version, true, entries) };
        Assert.False(NativeNeowCardCondition.TryCreate(changed, Prior, out _, out var reason));
        Assert.Equal("lost_coffer_requires_all_three_cards_and_potion", reason);
    }

    [Fact]
    public void FiniteLostCofferCompositionRetainsExclusionsUpgradeAndPotionMass()
    {
        NaturalSourceCollector.InitializeNativeModels();
        const int bits = 2, domain = 1 << bits;
        var rarities = new[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common };
        var cards = ModelDb.Character<Sts2Sim.Core.Models.Characters.Silent>().CardPool
            .GetUnlockedCards(Sts2Sim.Core.Entities.Players.PlayerUnlockState.AllUnlocked(), false);
        var pool = rarities.SelectMany(r => cards.Where(c => c.Rarity == r).Take(2)).ToArray();
        Assert.Equal(6, pool.Length);
        var targets = rarities.Select(r => pool.First(c => c.Rarity == r).GetType().Name).ToArray();
        var thresholds = new LabelCardRarityThresholds(.25f, .625f);
        long Count(CardModel[] remaining, int slot)
        {
            if (slot == 3) return 1;
            long count = 0;
            for (int rarityWord = 0; rarityWord < domain; rarityWord++)
            for (int indexWord = 0; indexWord < domain; indexWord++)
            {
                float roll = (float)((double)rarityWord / domain);
                var rarity = roll < thresholds.RareUpperExclusive ? CardRarity.Rare
                    : roll < thresholds.UncommonUpperExclusive ? CardRarity.Uncommon : CardRarity.Common;
                var candidates = remaining.Where(c => c.Rarity == rarity).ToArray();
                var picked = candidates[(int)((double)indexWord / domain * candidates.Length)];
                if (picked.GetType().Name == targets[slot])
                    count += Count(remaining.Where(c => c.Id != picked.Id).ToArray(), slot + 1);
            }
            return count;
        }
        var cardMass = new ShuffleRational(1, 1); var remaining = pool;
        foreach (string id in targets)
        {
            var factor = NativeNeowCardCondition.RarityMass(remaining, id, thresholds, bits);
            cardMass = cardMass.Multiply(factor.Numerator, factor.Denominator);
            remaining = remaining.Where(c => c.GetType().Name != id).ToArray();
        }
        long cardCount = Count(pool, 0);
        Assert.Equal(new ShuffleRational(cardCount, BigInteger.Pow(domain, 6)), cardMass);
        var potionPool = ModelDb.All<Sts2Sim.Core.Models.PotionModel>()
            .Where(p => p.Rarity is Sts2Sim.Core.Entities.Potions.PotionRarity.Common or Sts2Sim.Core.Entities.Potions.PotionRarity.Uncommon or Sts2Sim.Core.Entities.Potions.PotionRarity.Rare)
            .GroupBy(p => p.Rarity).SelectMany(g => g.Take(2)).ToArray();
        string potionId = potionPool.First(p => p.Rarity == Sts2Sim.Core.Entities.Potions.PotionRarity.Common).GetType().Name;
        int potionCount = 0;
        for (int rarityWord = 0; rarityWord < domain; rarityWord++)
        for (int indexWord = 0; indexWord < domain; indexWord++)
        {
            float roll = (float)((double)rarityWord / domain);
            var rarity = roll <= .1f ? Sts2Sim.Core.Entities.Potions.PotionRarity.Rare
                : roll <= .35f ? Sts2Sim.Core.Entities.Potions.PotionRarity.Uncommon : Sts2Sim.Core.Entities.Potions.PotionRarity.Common;
            var candidates = potionPool.Where(p => p.Rarity == rarity).ToArray();
            if (candidates[(int)((double)indexWord / domain * candidates.Length)].GetType().Name == potionId) potionCount++;
        }
        var potionMass = NativeRewardResourceMath.PotionMass(potionPool, potionId, bits);
        var joint = cardMass.Multiply(potionMass.Numerator, potionMass.Denominator);
        Assert.Equal(new ShuffleRational(cardCount * potionCount, BigInteger.Pow(domain, 8)), joint);
        // The three untouched upgrade words remain independent. Conditioning a
        // later public first-upgrade event (two of four words) has its native mass.
        Assert.Equal(new ShuffleRational(cardCount * potionCount * 2 * domain * domain, BigInteger.Pow(domain, 11)),
            joint.Multiply(2, domain));
        var q = new ShuffleRational(1, cardCount * potionCount * BigInteger.Pow(domain, 3));
        Assert.Equal(new ShuffleRational(1, BigInteger.Pow(domain, 11)), q.Multiply(joint.Numerator, joint.Denominator));
    }

    [Theory]
    [InlineData("foreign_rng")]
    [InlineData("foreign_owner")]
    [InlineData("changed_pool")]
    [InlineData("incomplete")]
    [InlineData("missing_native_marker")]
    [InlineData("repeat")]
    [InlineData("native_failure")]
    public async Task NewLeafOwnedBoundaryFailuresStayFatalAndPreserveNativeException(string failure)
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket observed; CardModel? foreign = null;
        using (Enter(nameof(NewLeaf), _ => 1UL << 63, transform: context => { foreign = context.Original; return null; }))
        await using (var source = await NativeRunWorld.OpenAsync(Prior.Execution, "new-leaf-guard-source", 0))
        { Assert.NotNull(source); observed = source.Observe(); }
        Assert.True(NativeNeowCardCondition.TryCreate(observed, Prior, out var condition, out var reason), reason);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, new(11, 22, 33, 0, 0));
        var force = typeof(NativeLabelTape).GetMethod("ForceWords", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, string, IDisposable>>(tape);
        var capture = typeof(NativeLabelTape).GetMethod("CaptureConditionedFailure", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<Exception>>(tape);
        var oracle = new NativeRewardsOracle(991);
        var proposal = new NativeNeowCardProposal(condition!, oracle.WasVisited, oracle.ForceFresh,
            new Rng(887).NextUnsignedLong, force, capture);
        var originalError = new FormatException("native transform failure before its index word");
        Rng? failRng = null;
        IDisposable? Transform(LabelCardTransformContext context)
        {
            if (failure == "foreign_rng") return proposal.BeginTransform(new(context.Original, false, new Rng(91), context.Candidates));
            if (failure == "foreign_owner") return proposal.BeginTransform(new(foreign!, false, context.Rng, context.Candidates));
            if (failure == "changed_pool") return proposal.BeginTransform(new(context.Original, false, context.Rng, context.Candidates.Skip(1).ToArray()));
            if (failure == "missing_native_marker") return proposal.BeginTransform(new(context.Original, false, context.Rng, context.Candidates));
            if (failure == "native_failure") failRng = context.Rng;
            var scope = proposal.BeginTransform(context)!;
            if (failure == "incomplete") { scope.Dispose(); return null; }
            return failure == "repeat" ? new ActionScope(() => { scope.Dispose(); proposal.BeginTransform(context); }) : scope;
        }
        try
        {
            if (failure == "native_failure") RngDiagnostics.DrawObserver = (rng, _, _, _, _) =>
                { if (ReferenceEquals(rng, failRng)) throw originalError; };
            using (Enter(nameof(NewLeaf), oracle.Word, proposal, transform: Transform))
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => NativeRunWorld.OpenAsync(Prior.Execution, "new-leaf-guard-hypothetical", 0));
                if (failure == "native_failure") Assert.Same(originalError, error);
                else Assert.IsType<InvalidOperationException>(error);
            }
        }
        finally { RngDiagnostics.DrawObserver = null; }
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
        Assert.True(tape.HasConditionedWordFailure);
        if (failure == "native_failure") Assert.Same(originalError, tape.ConditionedWordError!.InnerException);
    }

    [Fact]
    public async Task InertTransformHookPreservesOrdinaryNativeSelectionAndRngState()
    {
        NaturalSourceCollector.InitializeNativeModels();
        CardModel? original = null;
        using (Enter(nameof(NewLeaf), _ => 1UL << 63, transform: context => { original = context.Original; return null; }))
        await using (var source = await NativeRunWorld.OpenAsync(Prior.Execution, "new-leaf-inert-hook", 0))
            Assert.NotNull(source);
        Assert.NotNull(original);
        var sequential = new Rng(71234); var observed = new Rng(71234);
        var expected = Sts2Sim.Core.Factories.CardFactory.CreateRandomCardForTransform(original, false, sequential);
        LabelCardTransformContext? captured = null;
        CardModel actual;
        using (LabelCardTransformScope.Enter(context => { captured = context; return null; }))
            actual = Sts2Sim.Core.Factories.CardFactory.CreateRandomCardForTransform(original, false, observed);
        Assert.Equal(PublicJson.Serialize(PublicViews.Card(expected)), PublicJson.Serialize(PublicViews.Card(actual)));
        Assert.Equal(PublicJson.Serialize(sequential.ToSerializable()), PublicJson.Serialize(observed.ToSerializable()));
        Assert.Same(actual, captured!.CompletedCard); Assert.Null(captured.NativeFailure);
        Assert.Equal(1, observed.Counter);
    }

    [Theory]
    [InlineData(nameof(NewLeaf))]
    [InlineData(nameof(LeadPaperweight))]
    public async Task AdditionalNeowSourcesRequireTheirWholeImmediatePublicEvidence(string relic)
    {
        NaturalSourceCollector.InitializeNativeModels();
        DecisionPacket root;
        using (Enter(relic, _ => 1UL << 63))
        await using (var source = await NativeRunWorld.OpenAsync(Prior.Execution, "additional-neow-evidence:" + relic, 0))
        { Assert.NotNull(source); root = source.Observe(); }
        var events = root.PublicEvidence!.Events;
        var choice = Assert.IsType<PublicCardsObserved>(events[5].Payload).Choice;
        var partial = choice with { Candidates = choice.Candidates.Take(choice.Candidates.Length - 1).ToArray() };
        var changed = root with { PublicEvidence = new(PublicRunEvidence.Version, true,
            events.SetItem(5, new(5, 1, new PublicCardsObserved(partial)))) };
        Assert.False(NativeNeowCardCondition.TryCreate(changed, Prior, out _, out var reason));
        Assert.Equal(relic == nameof(NewLeaf) ? "new_leaf_complete_transform_candidates_required"
            : "lead_paperweight_requires_both_displayed_cards", reason);
        changed = root with { PublicEvidence = new(PublicRunEvidence.Version, true,
            events.SetItem(8, new(8, 0, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed)))) };
        Assert.False(NativeNeowCardCondition.TryCreate(changed, Prior, out _, out reason));
        Assert.Equal("neow_card_choice_settlement_required", reason);
    }

    [Fact]
    public void FiniteColorlessFallbackRetainsBothRolledArmsAndOrdinaryUpgradeWords()
    {
        NaturalSourceCollector.InitializeNativeModels();
        const int bits = 3, domain = 1 << bits;
        var pool = Sts2Sim.Core.Models.CardPools.ColorlessCardPool.Instance
            .GetUnlockedCards(Sts2Sim.Core.Entities.Players.PlayerUnlockState.AllUnlocked(), false)
            .GroupBy(c => c.Rarity).SelectMany(group => group.Take(2)).ToArray();
        Assert.DoesNotContain(pool, c => c.Rarity == CardRarity.Common);
        var targets = pool.Where(c => c.Rarity == CardRarity.Uncommon).Take(2).Select(c => c.GetType().Name).ToArray();
        Assert.Equal(2, targets.Length);
        var thresholds = new LabelCardRarityThresholds(.25f, .625f);
        long Count(CardModel[] remaining, int slot)
        {
            if (slot == 2) return 1;
            long count = 0;
            for (int rarityWord = 0; rarityWord < domain; rarityWord++)
            for (int indexWord = 0; indexWord < domain; indexWord++)
            {
                float roll = (float)((double)rarityWord / domain);
                var rarity = roll < thresholds.RareUpperExclusive ? CardRarity.Rare
                    : roll < thresholds.UncommonUpperExclusive ? CardRarity.Uncommon : CardRarity.Common;
                for (int i = 0; i < 3 && !remaining.Any(c => c.Rarity == rarity); i++)
                    rarity = rarity switch { CardRarity.Common => CardRarity.Uncommon,
                        CardRarity.Uncommon => CardRarity.Rare, _ => CardRarity.Common };
                var candidates = remaining.Where(c => c.Rarity == rarity).ToArray();
                var picked = candidates[(int)((double)indexWord / domain * candidates.Length)];
                if (picked.GetType().Name == targets[slot]) count += Count(remaining.Where(c => c.Id != picked.Id).ToArray(), slot + 1);
            }
            return count;
        }
        var firstArms = NativeRewardIdentityMath.Arms(NativeNeowCardCondition.CreationBranches(pool), thresholds, targets[0], bits);
        Assert.Equal(new CardRarity?[] { CardRarity.Uncommon, CardRarity.Common }, firstArms.Where(a => a.Mass > 0).Select(a => a.RolledRarity));
        var first = NativeNeowCardCondition.CreationRarityMass(pool, targets[0], thresholds, bits);
        var second = NativeNeowCardCondition.CreationRarityMass(pool.Where(c => c.GetType().Name != targets[0]).ToArray(), targets[1], thresholds, bits);
        var z = first.Multiply(second.Numerator, second.Denominator);
        long matched = Count(pool, 0);
        Assert.Equal(new ShuffleRational(matched, BigInteger.Pow(domain, 4)), z);
        // Both native upgrade words are outside the proposal. A later condition
        // on one of them keeps its original 2/domain conditional mass.
        Assert.Equal(new ShuffleRational(matched * 2 * domain, BigInteger.Pow(domain, 6)), z.Multiply(2, domain));
    }

    private sealed class ActionScope(Action action) : IDisposable
    { public void Dispose() => action(); }

    private static IDisposable Enter(string relic, Func<LabelRandomAddressV1, ulong> rewards,
        NativeNeowCardProposal? proposal = null, bool rewardsProvenance = true,
        Func<LabelCardTransformContext, IDisposable?>? transform = null)
    {
        IDisposable? Shuffle(Rng rng, IReadOnlyList<object?> items)
        {
            if (items.Count == 0 || items.Any(item => item is not Type)) return proposal?.BeginShuffle(rng, items);
            var ids = items.Cast<Type>().Select(t => t.Name).ToArray();
            if (!ids.Contains(relic)) return null;
            var plan = ConditionalShuffleProposal.Create(ids, [relic, nameof(WingedBoots)], new Rng(719).NextUnsignedLong)!;
            return Force(plan.RawWords);
        }
        IDisposable? NeowOptions(LabelNeowInitialOptionsContext context)
        {
            proposal?.AttachHypotheticalRun((Sts2Sim.Core.Runs.RunState)context.Neow.Owner.RunState);
            int index = context.AllowedCurses.ToList().IndexOf(typeof(DowsingRod));
            return Force([ConditionalShuffleProposal.Factor(context.AllowedCurses.Count, index).BucketStart << 11, 0, 0, 0]);
        }
        var labels = rewardsProvenance
            ? LabelRandomScope.EnterRewardProvenance(rewards, StateWord, beginShuffle: Shuffle,
                beginRewardCardSelection: proposal is null ? null : proposal.BeginSelection,
                beginNeowInitialOptions: NeowOptions, beginScrollBoxes: proposal is null ? null : proposal.BeginScrollBoxes)
            : LabelRandomScope.Enter(StateWord, beginShuffle: Shuffle,
                beginRewardCardSelection: proposal is null ? null : proposal.BeginSelection,
                beginNeowInitialOptions: NeowOptions, beginScrollBoxes: proposal is null ? null : proposal.BeginScrollBoxes);
        if (relic == nameof(NewLeaf) && (proposal is not null || transform is not null))
            return new Nested(labels, LabelCardTransformScope.Enter(transform ?? proposal!.BeginTransform));
        return relic == nameof(LostCoffer) && proposal is not null
            ? new Nested(labels, proposal.EnterResourceScope()) : labels;
    }

    private sealed class Nested(IDisposable outer, IDisposable inner) : IDisposable
    {
        public void Dispose() { try { inner.Dispose(); } finally { outer.Dispose(); } }
    }

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
