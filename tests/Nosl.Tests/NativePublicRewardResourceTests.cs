using System.Collections.Immutable;
using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Commands;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Factories;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rewards;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicRewardResourceTests
{
    private static (RunState Run, Player Player) CreateRun(string seed)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState(seed, ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run); run.AddPlayer(player);
        return (run, player);
    }

    private static PublicRunEvidence Evidence(Player player, RewardsSet rewards)
    {
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, NativePublicRunEvidence.Assets(player)));
        long combat = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 0);
        recorder.Record(combat, new PublicCombatFact(PublicCombatFactKind.Started));
        recorder.Record(combat, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Victory));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Reward, 0, 0);
        var groups = new List<PublicOfferGroup> { new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.ChooseOne,
            rewards.Card.Options.Select((c, i) => new PublicOffer("card:card:" + i, PublicOfferKind.Card, card: PublicViews.Card(c))).ToImmutableArray()) };
        if (rewards.Gold.Amount > 0) groups.Add(new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.Independent,
            [new("gold", PublicOfferKind.Gold, gold: rewards.Gold.Amount)]));
        if (rewards.Potion?.Potion is { } potion) groups.Add(new(PublicOfferGroupKind.Primary, PublicOfferSelectionMode.Independent,
            [new("potion", PublicOfferKind.Potion, potion: potion.GetType().Name)]));
        recorder.Record(owner, new PublicOffersObserved(groups.ToImmutableArray()));
        return PublicRunEvidenceJson.Read(PublicRunEvidenceJson.Serialize(recorder.Capture()));
    }

    private static string Offer(RewardsSet rewards) => PublicJson.Serialize(new
    { cards = rewards.Card.Options.Select(PublicViews.Card).ToArray(), gold = rewards.Gold.Amount,
        potion = rewards.Potion?.Potion?.GetType().Name, pity = rewards.Player.Odds.PotionReward.CurrentValue,
        rarity = rewards.Player.Odds.CardRarity.CurrentValue, counter = rewards.Player.PlayerRng.Rewards.Counter });

    [Theory]
    [InlineData("StrengthPotion", false, "normal")]
    [InlineData("ColorlessPotion", false, "range")]
    [InlineData("WeakPotion", true, "fixed")]
    [InlineData(null, false, "omitted")]
    [InlineData(null, false, "boss")]
    [InlineData("StrengthPotion", false, "normal", true)]
    [InlineData("WeakPotion", true, "fixed", true)]
    [InlineData(null, false, "omitted", true)]
    public async Task NativeResourcesComposeWithCardsAndReplayExactly(string? targetPotion, bool forced, string goldKind,
        bool mapLaw = false)
    {
        IDisposable Enter(Func<LabelRandomAddressV1, ulong> rewards,
            Func<LabelCombatRewardContext, IDisposable?>? beginCombatReward = null,
            Func<LabelRewardCardSelectionContext, IDisposable?>? beginRewardCardSelection = null) => mapLaw
            ? LabelRandomScope.EnterMapRewardProvenance(new NativeMapOracle(9173).Word, rewards, StateWord,
                beginCombatReward: beginCombatReward, beginRewardCardSelection: beginRewardCardSelection)
            : LabelRandomScope.EnterRewardProvenance(rewards, StateWord,
                beginCombatReward: beginCombatReward, beginRewardCardSelection: beginRewardCardSelection);
        const ulong ordinary = 1UL << 63;
        PublicRunEvidence evidence; string expected;
        var sourceWords = new Queue<ulong>();
        using (Enter(_ => sourceWords.Count > 0 ? sourceWords.Dequeue() : ordinary))
        {
            var (run, player) = CreateRun("resource-observation");
            if (forced) await RelicCmd.Obtain(ModelDb.Relic<WhiteBeastStatue>(), player);
            using var resources = LabelRewardResourceScope.Enter(c =>
            { if (!c.Forced) sourceWords.Enqueue(targetPotion is null ? ulong.MaxValue : 0); return null; }, _ => null, c =>
            {
                var common = c.Pool.Where(p => p.Rarity == PotionRarity.Common).ToArray();
                int index = Array.FindIndex(common, p => p.GetType().Name == targetPotion);
                Assert.True(index >= 0);
                sourceWords.Enqueue(ordinary); // Native common rarity arm.
                sourceWords.Enqueue((ulong)(((index + 0.5) / common.Length) * (1UL << 53)) << 11);
                return null;
            });
            var rewards = Generate(player, run, goldKind);
            evidence = Evidence(player, rewards); expected = Offer(rewards);
            Assert.Equal(targetPotion, rewards.Potion?.Potion?.GetType().Name);
            Assert.Empty(sourceWords);
        }
        var cards = NativePublicRewardCondition.Create(evidence);
        var condition = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.Null(Assert.Single(condition.Targets).Value.GoldCertificate.RoomType); // Synthetic owner has no map certificate.
        Dictionary<LabelRandomAddressV1, ulong> overrides = [];
        var visited = new HashSet<LabelRandomAddressV1>();
        NativePublicRewardResourceProposal? first = null;
        for (int replay = 0; replay < 2; replay++)
        {
            visited.Clear();
            var random = new Rng(81231);
            void Force(LabelRandomAddressV1 address, ulong word)
            {
                Assert.DoesNotContain(address, visited);
                if (overrides.TryGetValue(address, out ulong previous)) Assert.Equal(previous, word);
                overrides[address] = word;
            }
            var resourceProposal = new NativePublicRewardResourceProposal(condition, visited.Contains, Force, random.NextUnsignedLong);
            var cardRandom = new Rng(2727);
            var cardProposal = new NativePublicRewardProposal(cards, visited.Contains, Force, cardRandom.NextUnsignedLong);
            bool conditionNow = true;
            using var scope = Enter(address =>
            { visited.Add(address); return overrides.GetValueOrDefault(address, ordinary); },
                beginCombatReward: c => conditionNow ? new Together(cardProposal.BeginCombatReward(c, 0)!, resourceProposal.BeginCombatReward(c, 0)!) : null,
                beginRewardCardSelection: cardProposal.BeginSelection);
            var (run, player) = CreateRun("resource-independent-world");
            Assert.Equal(mapLaw ? LabelRandomProvenance.MapLawId : LabelRandomProvenance.LawId,
                player.PlayerRng.Rewards.ToSerializable().LabelProvenance!.Law);
            if (forced) await RelicCmd.Obtain(ModelDb.Relic<WhiteBeastStatue>(), player);
            Assert.Equal(expected, Offer(Generate(player, run, goldKind)));
            cardProposal.ValidateCompletion(); resourceProposal.ValidateCompletion();
            Assert.Equal(1, resourceProposal.ConditionedPresenceCount); Assert.Equal(1, resourceProposal.ConditionedGoldCount);
            Assert.Equal(targetPotion is null ? 0 : 1, resourceProposal.ConditionedPotionCount);
            Assert.Equal(3, cardProposal.ConditionedCardCount);
            Assert.True(resourceProposal.NativeToProposalRatio.Numerator * condition.Envelope.Denominator
                <= resourceProposal.NativeToProposalRatio.Denominator * condition.Envelope.Numerator);
            if (first is not null) Assert.Equal(first.NativeToProposalRatio, resourceProposal.NativeToProposalRatio);
            first = resourceProposal;
            // Later rewards run after both scopes are disposed; no extra owners are conditioned.
            conditionNow = false;
            _ = RewardsSet.GenerateFor(player, RoomType.Monster, run);
        }
    }

    private static RewardsSet Generate(Player player, RunState run, string kind) => kind switch
    {
        "fixed" => RewardsSet.GenerateFor(player, RoomType.Monster, run, fixedGoldAmount: 60),
        "omitted" => RewardsSet.GenerateFor(player, RoomType.Monster, run, goldProportion: 0f),
        "boss" => RewardsSet.GenerateFor(player, RoomType.Boss, run),
        "range" => RewardsSet.GenerateFor(player, RoomType.Monster, run, goldProportion: 0.5f,
            encounter: new EncounterDefinition((Func<MonsterModel>)(() => throw new NotSupportedException())) { MinGoldReward = 21, MaxGoldReward = 34 }),
        _ => RewardsSet.GenerateFor(player, RoomType.Monster, run),
    };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FinitePresenceHiddenPityAndEmptyRarityBranchesKeepTheirLikelihood(int bits)
    {
        _ = CreateRun("finite-resources");
        int domain = 1 << bits;
        var pool = new PotionModel[] { ModelDb.Potion<StrengthPotion>(), ModelDb.Potion<WeakPotion>() };
        var random = new Rng(7182);
        var totals = new int[2]; var later = new int[2];
        for (int h = 0; h < 2; h++)
        {
            float threshold = h == 0 ? 0.25f : 0.75f;
            for (int presence = 0; presence < domain; presence++)
            for (int rarity = 0; rarity < domain; rarity++)
            for (int index = 0; index < domain; index++)
            {
                bool present = (float)((double)presence / domain) < threshold;
                float rarityRoll = (float)((double)rarity / domain);
                bool empty = rarityRoll <= 0.1f || rarityRoll <= 0.35f;
                bool absent = !present || empty;
                if (!absent) continue;
                totals[h]++;
                // Pity falls on a present-but-null reward and rises on no reward.
                float nextThreshold = threshold + (present ? -0.1f : 0.1f);
                for (int next = 0; next < domain; next++)
                    if ((float)((double)next / domain) < nextThreshold) later[h]++;
            }
            var identity = NativeRewardResourceMath.PotionMass(pool, null, bits);
            var z = new ShuffleRational(totals[h], domain * domain * domain);
            for (int draw = 0; draw < 128; draw++)
            {
                var proposal = NativeRewardResourceMath.Presence(false, threshold, false, identity, random.NextUnsignedLong, bits);
                var ratio = proposal.Plan.NativeToProposalRatio;
                var nativeOdds = new PotionRewardOdds(threshold, new Rng(823), NullOddsHooks.Instance);
                using (LabelRandomScope.Enter(_ => Assert.Single(proposal.Plan.RawWords)))
                    Assert.Equal(proposal.Present, nativeOdds.Roll(RoomType.Monster));
                Assert.Equal(threshold + (proposal.Present ? -0.1f : 0.1f), nativeOdds.CurrentValue);
                if (proposal.Present)
                {
                    var potion = NativeRewardResourceMath.Potion(pool, null, random.NextUnsignedLong, bits);
                    Assert.Single(potion.RawWords); ratio = ratio.Multiply(potion.NativeToProposalRatio.Numerator, potion.NativeToProposalRatio.Denominator);
                }
                Assert.Equal(z, ratio); // q(path) * Z = p(path), with both latent pity branches retained.
            }
        }
        Assert.True(totals[0] > totals[1]);
        Assert.True(later[0] != later[1]);
        Assert.Equal(new ShuffleRational(totals[0], totals.Sum()), bits == 2 ? new ShuffleRational(7, 12) : new ShuffleRational(27, 44));
        var forced = NativeRewardResourceMath.Presence(true, 0.4f, false,
            NativeRewardResourceMath.PotionMass(pool, null, bits), random.NextUnsignedLong, bits);
        Assert.True(forced.Present); Assert.Empty(forced.Plan.RawWords);
        Assert.Throws<NativePublicConstraintMismatchException>(() => NativeRewardResourceMath.Presence(true, 1f, false, new(0, 1), random.NextUnsignedLong, bits));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void FiniteGoldPreimagesMatchNativeRangesAndOmissions(int bits)
    {
        int domain = 1 << bits;
        foreach (var (min, max) in new[] { (7, 15), (26, 33), (75, 75), (60, 60), (0, 0), (-2, 3), (10, 17) })
            foreach (int? target in Enumerable.Range(min, max - min + 1).Select(i => (int?)i).Append(null))
            {
                var bucket = NativeRewardResourceMath.GoldBucket(min, max, target, bits);
                for (int word = 0; word < domain; word++)
                {
                    using var scope = LabelRandomScope.Enter(_ => (ulong)word << (64 - bits));
                    int actual = new Rng(14).NextInt(min, max + 1);
                    Assert.Equal(target is null ? actual <= 0 : actual == target, (ulong)word >= bucket.Start && (ulong)word < bucket.Start + bucket.Size);
                }
            }
        var omitted = NativeRewardResourceMath.Gold(0, 0, true, null, () => throw new Exception("No draw"), bits);
        Assert.Empty(omitted.RawWords); Assert.Equal(new ShuffleRational(1, 1), omitted.NativeToProposalRatio);
        Assert.Throws<NativePublicConstraintMismatchException>(() => NativeRewardResourceMath.Gold(0, 0, true, 12, () => 0, bits));
    }

    [Fact]
    public void PotionInclusiveFloatEdgesAndResourceCallbacksPreserveNativeStreams()
    {
        var (run, player) = CreateRun("resource-hook-suppression");
        const ulong domain = 1UL << 53;
        foreach (float threshold in new[] { 0f, 0.1f, 0.35f, 1f })
        {
            ulong edge = NativeRewardResourceMath.FloatUpperBound(threshold);
            foreach (ulong word in new[] { edge == 0 ? 0 : edge - 1, Math.Min(edge, domain - 1), domain - 1 })
            {
                using var scope = LabelRandomScope.Enter(_ => word << 11);
                Assert.Equal(word < edge, new Rng(6).NextFloat() <= threshold);
            }
        }
        var emptyRng = new Rng(567);
        Assert.Null(PotionFactory.CreateRandomOutOfCombat(player, emptyRng, _ => false));
        Assert.Equal(1, emptyRng.Counter); // Empty native branch does not invent an index draw.
        Assert.True(NativeRewardResourceMath.FloatUpperBound(0.1f) > NativeRewardIdentityMath.FloatLowerBound(0.1f));
        int oracleCalls = 0, callbacks = 0;
        using var outer = LabelRandomScope.Enter(_ => { oracleCalls++; return 0; });
        IDisposable? Auxiliary()
        {
            callbacks++; int before = oracleCalls;
            _ = new Rng(77).NextUnsignedLong();
            _ = PotionFactory.CreateRandomOutOfCombat(player, new Rng(43));
            Assert.Equal(before, oracleCalls); return null;
        }
        using (LabelRewardResourceScope.Enter(_ => Auxiliary(), _ => Auxiliary(), _ => Auxiliary()))
            _ = RewardsSet.GenerateFor(player, RoomType.Monster, run);
        Assert.Equal(3, callbacks); Assert.True(oracleCalls >= 12);
        int oldCallbacks = callbacks;
        _ = RewardsSet.GenerateFor(player, RoomType.Monster, run);
        Assert.Equal(oldCallbacks, callbacks);
    }

    [Fact]
    public void AllFloatGoldProportionsCoverNativeRoundedRangesWithoutUsingOneWorldAsBound()
    {
        foreach (var (min, max) in new[] { (7, 15), (10, 20), (26, 33), (35, 45), (75, 75), (100, 100) })
        {
            var ranges = NativeGoldEnvelopeCertificate.AllProportionalRanges(min, max);
            Assert.Contains((0, 0), ranges); Assert.Contains((min, max), ranges);
            var random = new Rng(623);
            for (int i = 0; i < 1000; i++)
            {
                float p = random.NextFloat();
                Assert.Contains(((int)Math.Round(min * p), (int)Math.Round(max * p)), ranges);
            }
        }
    }

    [Fact]
    public async Task ActualPublicMapRewardUsesCertifiedGoldBoundAndOnlyFirstDisplayedOwner()
    {
        var result = await NaturalSourceCollector.CollectAsync(new NaturalSourceOptions(MaxFloors: 8,
            MaxRoots: 2, MaxRootsPerCombat: 1, SeedPrefix: "owned-native-opening",
            ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version));
        Assert.Null(Assert.Single(result.Runs).Error);
        var evidence = result.Roots[1].PublicRoot.PublicEvidence!;
        var cards = NativePublicRewardCondition.Create(evidence);
        var condition = NativePublicRewardResourceCondition.Create(evidence, cards);
        var target = Assert.Single(condition.Targets).Value;
        Assert.Equal(RoomType.Monster, target.GoldCertificate.RoomType);
        Assert.True(target.GoldCertificate.Envelope.Numerator < target.GoldCertificate.Envelope.Denominator);
        Assert.Contains((7, 15), target.GoldCertificate.Ranges);
        Assert.NotNull(target.Owner.PublicHistory);
        Assert.Equal(new[] { (7, 15) }, target.GoldCertificate.Ranges);
        var displayed = (PublicOffersObserved)evidence.Events[(int)target.Owner.OfferEventOrdinal].Payload;
        Assert.Equal(displayed.Groups.SelectMany(g => g.Offers).Single(o => o.Key == "gold").Gold, target.Gold);
        Assert.Equal(displayed.Groups.SelectMany(g => g.Offers).SingleOrDefault(o => o.Key == "potion")?.Potion, target.Potion);
        Assert.True(evidence.Events.Count(e => e.Payload is PublicOffersObserved) > condition.Targets.Count);
    }

    [Fact]
    public void CustomPotionOnlyOwnerContributesNoResourceFactor()
    {
        var (_, player) = CreateRun("resource-custom-owner");
        var custom = RewardsSet.CreateCustom(player,
            potion: new PotionReward((PotionModel)ModelDb.Potion<StrengthPotion>().MutableClone(), player));
        var evidence = Evidence(player, custom);
        var cards = NativePublicRewardCondition.Create(evidence);
        Assert.Empty(cards.Targets);
        var resources = NativePublicRewardResourceCondition.Create(evidence, cards);
        Assert.Empty(resources.Targets); Assert.Equal(new ShuffleRational(1, 1), resources.Envelope);
        Assert.Contains(evidence.Events.Select(e => e.Payload).OfType<PublicOffersObserved>()
            .SelectMany(o => o.Groups).SelectMany(g => g.Offers), o => o.Potion == nameof(StrengthPotion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceAliasOrMissingProvenanceIsAnExplicitProofFailure(bool wrongPartition)
    {
        var (sourceRun, sourcePlayer) = CreateRun("resource-alias-observation");
        var evidence = Evidence(sourcePlayer, RewardsSet.GenerateFor(sourcePlayer, RoomType.Monster, sourceRun));
        var condition = NativePublicRewardResourceCondition.Create(evidence, NativePublicRewardCondition.Create(evidence));
        var random = new Rng(714);
        var proposal = new NativePublicRewardResourceProposal(condition, _ => true,
            (_, _) => throw new Exception("Must not overwrite"), random.NextUnsignedLong);
        using var scope = wrongPartition
            ? LabelRandomScope.Enter(StateWord, beginCombatReward: c => proposal.BeginCombatReward(c, 0))
            : LabelRandomScope.EnterRewardProvenance(_ => 0, StateWord, beginCombatReward: c => proposal.BeginCombatReward(c, 0));
        var (run, player) = CreateRun("resource-alias-world");
        var error = Assert.Throws<InvalidOperationException>(() => RewardsSet.GenerateFor(player, RoomType.Monster, run));
        Assert.Contains(wrongPartition ? "provenance" : "fresh", error.Message);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Fact]
    public async Task ForcedPotionContradictionPreservesMismatchAndRestoresOuterResourceScope()
    {
        const ulong ordinary = 1UL << 63;
        PublicRunEvidence evidence;
        using (LabelRandomScope.EnterRewardProvenance(_ => ordinary, StateWord))
        {
            var (run, player) = CreateRun("resource-absence-observation");
            var rewards = RewardsSet.GenerateFor(player, RoomType.Monster, run);
            Assert.Null(rewards.Potion);
            evidence = Evidence(player, rewards);
        }
        var condition = NativePublicRewardResourceCondition.Create(evidence, NativePublicRewardCondition.Create(evidence));
        var oracle = new NativeRewardsOracle(237);
        var random = new Rng(73);
        var proposal = new NativePublicRewardResourceProposal(condition, oracle.WasVisited, oracle.ForceFresh, random.NextUnsignedLong);
        int outerPresenceCallbacks = 0;
        using var outer = LabelRewardResourceScope.Enter(_ => { outerPresenceCallbacks++; return null; }, _ => null, _ => null);
        bool conditionNow = true;
        using var scope = LabelRandomScope.EnterRewardProvenance(oracle.Word, StateWord,
            beginCombatReward: context => conditionNow ? proposal.BeginCombatReward(context, 0) : null);
        var (native, hypothetical) = CreateRun("resource-forced-hypothesis");
        await RelicCmd.Obtain(ModelDb.Relic<WhiteBeastStatue>(), hypothetical);
        int before = hypothetical.PlayerRng.Rewards.Counter;
        var error = Assert.Throws<NativePublicConstraintMismatchException>(() => RewardsSet.GenerateFor(hypothetical, RoomType.Monster, native));
        Assert.Contains("Public potion presence", error.Message);
        Assert.True(proposal.HasFailed);
        Assert.Equal(before, hypothetical.PlayerRng.Rewards.Counter);
        Assert.Equal(0, outerPresenceCallbacks);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        conditionNow = false;
        Assert.NotNull(RewardsSet.GenerateFor(hypothetical, RoomType.Monster, native).Potion);
        Assert.Equal(1, outerPresenceCallbacks); // Failed inner scope was actually removed.
        Assert.True(proposal.HasFailed); // Available to the owning composite after cleanup.
    }

    [Fact]
    public void CompletionCallbackFailureMarksBoundaryFailedWithoutReplacingException()
    {
        var (sourceRun, sourcePlayer) = CreateRun("resource-callback-observation");
        var evidence = Evidence(sourcePlayer, RewardsSet.GenerateFor(sourcePlayer, RoomType.Monster, sourceRun));
        var condition = NativePublicRewardResourceCondition.Create(evidence, NativePublicRewardCondition.Create(evidence));
        var oracle = new NativeRewardsOracle(912);
        var random = new Rng(27);
        var failure = new FormatException("visited-cell callback failure");
        int checks = 0;
        var proposal = new NativePublicRewardResourceProposal(condition, _ => ++checks == 1 ? false : throw failure,
            oracle.ForceFresh, random.NextUnsignedLong);
        using var scope = LabelRandomScope.EnterRewardProvenance(oracle.Word, StateWord,
            beginCombatReward: context => proposal.BeginCombatReward(context, 0));
        var (run, player) = CreateRun("resource-callback-hypothesis");
        Assert.Same(failure, Assert.Throws<FormatException>(() => RewardsSet.GenerateFor(player, RoomType.Monster, run)));
        Assert.True(proposal.HasFailed);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    private static ulong StateWord(LabelRandomState state)
    {
        byte[] bytes = new[] { state.State0, state.State1, state.State2, state.State3 }.SelectMany(BitConverter.GetBytes).ToArray();
        return BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private sealed class Together(IDisposable first, IDisposable second) : IDisposable
    { public void Dispose() { try { second.Dispose(); } finally { first.Dispose(); } } }
}
