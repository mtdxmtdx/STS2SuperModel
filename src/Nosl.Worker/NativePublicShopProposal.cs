using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Relics;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Owned first-shop proposal. The player's whole initial relic bag, both relic
/// rarity draws and every public stock/price are conditioned. Shared bag order,
/// discarded price/upgrade words and future stream cells retain their native law.
/// </summary>
internal sealed class NativePublicShopProposal(NativePublicShopCondition condition,
    Func<LabelRandomAddressV1, bool> wasVisited, Action<LabelRandomAddressV1, ulong> forceFresh,
    Func<ulong> nextWord, Func<IReadOnlyList<ulong>, Rng, string, IDisposable> forcePrefixWords,
    Action<Exception>? captureFailure = null)
{
    private RunState? _constructionRun, _run;
    private bool _bagComplete, _stockBegun, _stockComplete, _failed;
    private readonly List<ConditionalShufflePlan> _bagPlans = [];
    private ShuffleRational _stockRatio = new(1, 1);
    internal int ConditionedOfferCount => _stockComplete ? 1 : 0;
    internal int ConditionedBagCount => _bagComplete ? 1 : 0;
    internal ShuffleRational NativeToProposalRatio => _bagPlans.Aggregate(_stockRatio,
        (p, b) => p.Multiply(b.NativeToProposalRatio.Numerator, b.NativeToProposalRatio.Denominator));
    internal ShuffleRational Envelope => _bagPlans.Aggregate(condition.Stock.Envelope,
        (p, b) => p.Multiply(b.Envelope.Numerator, b.Envelope.Denominator));
    internal IDisposable EnterScope() => LabelMerchantScope.Enter(BeginBag, BeginInventory);

    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null || !_bagComplete || !ReferenceEquals(_constructionRun, run))
            throw new InvalidOperationException("Public shop must own its completed native player-bag construction");
        _run = run;
    }

    internal IDisposable? BeginBag(LabelPlayerRelicBagContext context)
    {
        try
        {
            if (_constructionRun is not null || _run is not null || context.Player.RunState is not RunState run
                || context.Player.Character is not Silent || run.TotalFloor != 0 || run.Players.Count != 0
                || run.CurrentRoom is not null || run.Rng.UsesSemanticKeys || !ReferenceEquals(context.Rng, run.Rng.UpFront)
                || !context.Pool.SequenceEqual(condition.RelicPool))
                throw new InvalidOperationException("Public shop escaped its fresh native player-bag boundary");
            RequireFullState(context.Rng); _constructionRun = run;
            var words = new List<ulong>();
            foreach (var bucket in context.Pool.Where(r => r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop).GroupBy(r => r.Rarity))
            {
                if (!condition.BackDraws.TryGetValue(bucket.Key, out var targets))
                { for (int i = 1; i < bucket.Count(); i++) words.Add(nextWord()); continue; }
                string[] ids = bucket.Select(r => r.GetType().Name).ToArray();
                var eligible = ids.Where(condition.EligibleRelics.Contains).ToHashSet();
                var plan = NativeShopRelicPermutationMath.Create(ids, eligible, targets, nextWord);
                _bagPlans.Add(plan); words.AddRange(plan.RawWords);
            }
            int counter = context.Rng.Counter; var before = context.Rng.ToSerializable();
            var force = forcePrefixWords(words, context.Rng, "public first-shop whole player relic bag");
            return new Boundary(force, Failed, captureFailure, () =>
            {
                CheckAdvance(context.Rng, before, counter, words.Count);
                _bagComplete = true;
            });
        }
        catch (Exception e) { Failed(); captureFailure?.Invoke(e); throw; }
    }

    internal IDisposable? BeginInventory(LabelMerchantInventoryContext context)
    {
        if (_run?.CurrentActIndex != 0 || _run.TotalFloor != condition.Floor) return null;
        try
        {
            var player = context.Player;
            if (_failed || !_bagComplete || _stockBegun || _run.CurrentRoom is not MerchantRoom || _run.CurrentRoomCount != 1
                || _run.Players.Count != 1 || !ReferenceEquals(player, _run.Players[0])
                || !ReferenceEquals(player.RunState, _run) || player.Character is not Silent || player.PlayerRng.UsesSemanticKeys)
                throw new InvalidOperationException("Public shop escaped its owned initial merchant inventory boundary");
            if (PublicJson.Serialize(NativePublicRunEvidence.Assets(player)) != PublicJson.Serialize(condition.BeforeAssets))
                throw new NativePublicConstraintMismatchException("Native shop inventory differs from public before-assets");
            RequireFullState(player.PlayerRng.Shops);
            var start = RewardsAddress(player.PlayerRng.Rewards);
            var stock = condition.Stock;
            var shops = new List<ulong>(); var rewards = new Dictionary<int, ulong>();
            int discount = DrawRationalIndex(stock.DiscountMasses, nextWord);
            var discountBucket = ConditionalShuffleProposal.Factor(5, discount);
            shops.Add(NativeRewardResourceMath.SampleWord(discountBucket.BucketStart, discountBucket.BucketSize, 53, nextWord));
            // Native RollWithoutChangingFutureOdds uses CurrentValue. The read-only
            // accessor's true arm exposes that threshold without updating odds.
            var thresholds = player.Odds.CardRarity.GetLabelRollThresholds(CardRarityOddsType.Shop, changesFutureOdds: true);
            float oldOdds = player.Odds.CardRarity.CurrentValue;
            if (condition.CertifiedRarityOffset is { } certifiedOffset && oldOdds != certifiedOffset)
                throw new NativePublicConstraintMismatchException("Native shop rarity offset differs from its closed public reward history");
            var identities = new ShuffleRational(1, 1);
            for (int i = 0; i < 7; i++)
            {
                var selection = new LabelRewardCardSelectionContext(player, player.PlayerRng.Rewards,
                    LabelRewardCardSelectionKind.CreationOptions, i, 7, CardCreationSource.Other,
                    (CardCreationFlags)0, CardRarityOddsType.Shop, i < 5 ? thresholds : null, false,
                    stock.CardBranches[i].SelectMany(b => b.Candidates).ToArray(), stock.CardBranches[i]);
                var plan = NativeRewardIdentityMath.Create(selection, stock.Cards[i].GetType().Name, nextWord);
                identities = identities.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
                if (i < 5) rewards.Add(2 * i, plan.RawWords[0]);
                shops.Add(plan.RawWords[^1]);
                // SetOnSale recalculates after consuming an unused full-price word.
                if (i == discount) shops.Add(nextWord());
                shops.Add(NativeShopPriceMath.Sample(stock.CardBasePrices[i], .95f, 1.05f, i == discount, stock.Prices[i], nextWord));
            }
            for (int i = 0; i < 3; i++)
            {
                if (i < 2) { var r = NativeShopStockCertificate.RelicRarityRange(stock.Relics[i].Rarity); rewards.Add(12 + i, NativeRewardResourceMath.SampleWord(r.Start, r.Size, 53, nextWord)); }
                shops.Add(NativeShopPriceMath.Sample(stock.Relics[i].MerchantCost, .85f, 1.15f, false, stock.Prices[7 + i], nextWord));
            }
            // Native distinct potion identities all precede native potion prices.
            for (int i = 0; i < 3; i++) shops.AddRange(NativeRewardResourceMath.Potion(stock.PotionPools[i], stock.Potions[i].GetType().Name, nextWord).RawWords);
            for (int i = 0; i < 3; i++) shops.Add(NativeShopPriceMath.Sample(stock.PotionBasePrice(i), .95f, 1.05f, false, stock.Prices[10 + i], nextWord));
            if (shops.Count != 28 || rewards.Count != 7) throw new InvalidOperationException("Certified shop draw contract changed");
            foreach (int offset in rewards.Keys)
                if (wasVisited(start with { RawCursor = checked(start.RawCursor + (ulong)offset) }))
                    throw new InvalidOperationException("Shop requires fresh Rewards cells; alias invalidates its envelope");
            foreach (var (offset, word) in rewards) forceFresh(start with { RawCursor = checked(start.RawCursor + (ulong)offset) }, word);
            _stockBegun = true; _stockRatio = stock.FixedMass.Multiply(identities.Numerator, identities.Denominator);
            if (_stockRatio.Numerator * stock.Envelope.Denominator > stock.Envelope.Numerator * _stockRatio.Denominator)
                throw new InvalidOperationException("Shop identity mass exceeds its root-fixed envelope");
            int counter = player.PlayerRng.Shops.Counter; var before = player.PlayerRng.Shops.ToSerializable();
            var force = forcePrefixWords(shops, player.PlayerRng.Shops, "public initial merchant inventory");
            return new Boundary(force, Failed, captureFailure, () =>
            {
                CheckAdvance(player.PlayerRng.Shops, before, counter, 28);
                if (RewardsAddress(player.PlayerRng.Rewards) != start with { RawCursor = checked(start.RawCursor + 14) }
                    || rewards.Keys.Any(offset => !wasVisited(start with { RawCursor = checked(start.RawCursor + (ulong)offset) }))
                    || player.Odds.CardRarity.CurrentValue != oldOdds || context.CompletedInventory is not { } actual)
                    throw new InvalidOperationException("Native shop skipped, restored, added draws or changed future rarity odds");
                if (!actual.Cards.Select(e => e.Card.GetType().Name).SequenceEqual(stock.Cards.Select(c => c.GetType().Name))
                    || !actual.Relics.Select(e => e.Relic.GetType().Name).SequenceEqual(stock.Relics.Select(r => r.GetType().Name))
                    || !actual.Potions.Select(e => e.Potion.GetType().Name).SequenceEqual(stock.Potions.Select(p => p.GetType().Name))
                    || !actual.Cards.Cast<MerchantEntry>().Concat(actual.Relics).Concat(actual.Potions).Select(e => e.Price).SequenceEqual(stock.Prices))
                    throw new InvalidOperationException("Native shop departed from its certified complete inventory");
                _stockComplete = true;
            });
        }
        catch (Exception e) { Failed(); captureFailure?.Invoke(e); throw; }
    }

    internal void ValidateCompletion()
    {
        if (_failed || !_bagComplete || !_stockComplete || _bagPlans.Count != condition.BackDraws.Count)
            throw new InvalidOperationException("Public shop proposal did not complete its whole bag and inventory");
    }
    internal bool AcceptCorrection(Func<ulong> correction)
    {
        ValidateCompletion();
        foreach (var plan in _bagPlans) if (!plan.AcceptCorrection(correction)) return false;
        var bound = condition.Stock.Envelope;
        return NativeNeowProposal.ExactRationalBernoulli(new(_stockRatio.Numerator * bound.Denominator,
            _stockRatio.Denominator * bound.Numerator), correction);
    }
    private void Failed() => _failed = true;
    private static int DrawRationalIndex(IReadOnlyList<ShuffleRational> masses, Func<ulong> next)
    {
        BigInteger common = masses.Aggregate(BigInteger.One, (d, m) => d / BigInteger.GreatestCommonDivisor(d, m.Denominator) * m.Denominator);
        var weights = masses.Select(m => m.Numerator * (common / m.Denominator)).ToArray();
        var ticket = NativeRewardIdentityMath.UniformBelow(weights.Aggregate(BigInteger.Zero, (a, b) => a + b), next);
        for (int i = 0; i < weights.Length; i++) { if (ticket < weights[i]) return i; ticket -= weights[i]; }
        throw new InvalidOperationException("Incomplete discount partition");
    }
    private static LabelRandomAddressV1 RewardsAddress(Rng rng)
    {
        if (rng.ToSerializable().LabelProvenance is not { Law: LabelRandomProvenance.LawId or LabelRandomProvenance.MapLawId,
            Partition: LabelRandomProvenance.RewardsPartition, OriginFamily: LabelRandomProvenance.RewardsOrigin,
            InitialSeed: { } seed, RawCursor: { } cursor }) throw new InvalidOperationException("Shop requires tagged native Rewards provenance");
        return new(LabelRandomProvenance.RewardsOrigin, seed, cursor);
    }
    private static void RequireFullState(Rng rng)
    {
        if (rng.ToSerializable().LabelProvenance is not { Law: LabelRandomProvenance.LawId or LabelRandomProvenance.MapLawId,
            Partition: LabelRandomProvenance.FullStatePartition, OriginFamily: null, InitialSeed: null, RawCursor: null, ActIndex: null })
            throw new InvalidOperationException("Shop requires tagged full-state native stream provenance");
    }
    private static void CheckAdvance(Rng rng, Sts2Sim.Core.Saves.SerializableRng before, int counter, int draws)
    {
        var expected = new MegaRandom(before);
        for (int i = 0; i < draws; i++) expected.NextULong();
        expected.FillSerializableState(before); var after = rng.ToSerializable();
        if (rng.Counter != counter + draws || before.state0 != after.state0 || before.state1 != after.state1
            || before.state2 != after.state2 || before.state3 != after.state3)
            throw new InvalidOperationException("Native shop boundary skipped, added or restored full-state draws");
    }
    private sealed class Boundary(IDisposable force, Action fail, Action<Exception>? capture, Action complete) : IAbortableLabelRewardBoundary
    {
        private bool _disposed, _aborted;
        public void Abort() { _aborted = true; fail(); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_aborted) { try { force.Dispose(); } catch { } return; }
            try { force.Dispose(); complete(); }
            catch (Exception e) { fail(); capture?.Invoke(e); throw; }
        }
    }
}
