using System.Reflection;
using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Merchant;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Relics;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicShopProposalTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Fact]
    public void PassThroughMerchantLabelsPreserveDefaultNativeInventoryBagsAndStreams()
    {
        NaturalSourceCollector.InitializeNativeModels();
        string Snapshot()
        {
            var run = new RunState("public-shop-default-equivalence", new Overgrowth());
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
            run.AddPlayer(player);
            var inventory = MerchantInventory.Generate(player);
            return PublicJson.Serialize(new
            {
                Cards = inventory.Cards.Select(e => new { Id = e.Card.Id, e.Card.CurrentUpgradeLevel, e.Price }),
                Relics = inventory.Relics.Select(e => new { Id = e.Relic.Id, e.Price }),
                Potions = inventory.Potions.Select(e => new { Id = e.Potion.Id, e.Price }),
                RemovalPrice = inventory.CardRemoval.Price,
                PlayerBag = Buckets(player.RelicGrabBag).ToDictionary(p => p.Key, p => p.Value.Select(r => r.Id)),
                SharedBag = Buckets(run.SharedRelicGrabBag!).ToDictionary(p => p.Key, p => p.Value.Select(r => r.Id)),
                RunRng = run.Rng.ToSerializable(), PlayerRng = player.PlayerRng.ToSerializable(),
            });
        }
        string ordinary = Snapshot();
        int bags = 0, inventories = 0;
        using (LabelMerchantScope.Enter(_ => { bags++; return null; }, _ => { inventories++; return null; }))
            Assert.Equal(ordinary, Snapshot());
        Assert.Equal(1, bags); Assert.Equal(1, inventories);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeExceptionsAbortMerchantBoundaryWithoutReplacingOriginalFailure(bool playerBag)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-shop-abort-equivalence", new Overgrowth());
        var error = new InvalidOperationException("original native merchant draw failure");
        var boundary = new AbortProbe();
        bool active = false;
        using var merchant = LabelMerchantScope.Enter(_ =>
        {
            if (!playerBag) return null;
            active = true; return boundary;
        }, _ =>
        {
            active = true; return boundary;
        });
        using var words = LabelRandomScope.Enter(_ => active ? throw error : 0UL);
        var actual = Assert.Throws<InvalidOperationException>(() =>
        {
            var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
            run.AddPlayer(player);
            MerchantInventory.Generate(player);
        });
        Assert.Same(error, actual);
        Assert.True(boundary.Aborted); Assert.True(boundary.Disposed);
    }

    [Fact]
    public async Task FirstShopConditionsWholeBagsEveryStockAndPricePreservingNativeFutureState()
    {
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe)))
        { Assert.NotNull(source); root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe())); }
        Assert.True(NativePublicShopCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        Assert.Equal(146, condition!.OfferEventOrdinal); Assert.Equal(4, condition.Floor);
        Assert.NotNull(condition.CertifiedRarityOffset);
        var fallback = new NativeShopStockCertificate(condition.Offers);
        Assert.True(condition.Stock.Envelope.Numerator * fallback.Envelope.Denominator
            < fallback.Envelope.Numerator * condition.Stock.Envelope.Denominator);
        Assert.Equal(7, condition.Stock.Cards.Count); Assert.Equal(3, condition.Stock.Relics.Count); Assert.Equal(3, condition.Stock.Potions.Count);
        string expected = PublicJson.Serialize(root);
        string? previousBag = null;
        for (ulong attempt = 101; attempt <= 102; attempt++)
        {
            // Same-recipe native lifecycle verification, not an independent
            // posterior acceptance experiment. Only public DTOs enter condition.
            var tape = NativeLabelTape.ForDeclaredPrior(Prior, recipe);
            var rewards = (NativeRewardsOracle)typeof(NativeLabelTape).GetField("_rewardsOracle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tape)!;
            var prefix = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
            Exception? failure = null;
            var proposal = new NativePublicShopProposal(condition, rewards.WasVisited, rewards.ForceFresh,
                new Rng(attempt, "first-shop-fixture").NextUnsignedLong, prefix, e => failure = e);
            bool attached = false;
            using var merchant = LabelMerchantScope.Enter(proposal.BeginBag, context =>
            {
                if (!attached) { proposal.AttachHypotheticalRun((RunState)context.Player.RunState); attached = true; }
                return proposal.BeginInventory(context);
            });
            await using var world = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, tape);
            Assert.NotNull(world); proposal.ValidateCompletion(); Assert.Null(failure);
            Assert.Equal(expected, PublicJson.Serialize(world.Observe()));
            Assert.Equal(1, proposal.ConditionedBagCount); Assert.Equal(1, proposal.ConditionedOfferCount);
            double acceptance = Math.Exp(System.Numerics.BigInteger.Log(proposal.NativeToProposalRatio.Numerator)
                - System.Numerics.BigInteger.Log(proposal.NativeToProposalRatio.Denominator)
                - System.Numerics.BigInteger.Log(proposal.Envelope.Numerator) + System.Numerics.BigInteger.Log(proposal.Envelope.Denominator));
            output.WriteLine("Shop total correction ratio: " + acceptance);
            Assert.InRange(acceptance, .999999999, 1.000000001);
            Assert.True(proposal.AcceptCorrection(new Rng(912).NextUnsignedLong));
            Assert.Equal(7, rewards.ConditionedCells); // Seven unused upgrade draws remain ordinary.
            var run = world.NativeRun; var player = run.Players.Single();
            var buckets = Buckets(player.RelicGrabBag);
            var remaining = buckets.Values.SelectMany(x => x).Select(r => r.GetType().Name).ToArray();
            Assert.Equal(condition.RelicPool.Count(r => r.Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop) - 3, remaining.Length);
            Assert.All(condition.Stock.Relics, relic => Assert.DoesNotContain(relic.GetType().Name, remaining));
            Assert.All(condition.Stock.Relics, relic => Assert.DoesNotContain(Buckets(run.SharedRelicGrabBag!).Values.SelectMany(x => x), r => r.Id == relic.Id));
            string bag = string.Join("|", buckets.Select(kv => kv.Key + ":" + string.Join(",", kv.Value.Select(r => r.GetType().Name))));
            if (previousBag is not null) Assert.NotEqual(previousBag, bag);
            previousBag = bag;
            var shopState = player.PlayerRng.Shops.ToSerializable();
            Assert.Equal(28, shopState.counter);
            // Future full-state cells remain ordinary under the unchanged law.
            ulong next;
            using (tape.EnterScope()) next = player.PlayerRng.Shops.NextUnsignedLong();
            var ordinary = NativeLabelTape.ForDeclaredPrior(Prior, recipe);
            ulong expectedNext;
            using (ordinary.EnterScope()) expectedNext = new Rng(shopState).NextUnsignedLong();
            Assert.Equal(expectedNext, next);
        }
    }

    [Fact]
    public async Task MissingUnpickedStockAndUnobservedUnknownRoomsDeclineWithoutNarrowingPrior()
    {
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        await using var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(Prior, recipe));
        var root = source!.Observe(); var events = root.PublicEvidence!.Events;
        var offers = (PublicOffersObserved)events[146].Payload; var group = offers.Groups.Single();
        var changed = new PublicOffersObserved([new(group.GroupKind, group.SelectionMode, group.Offers.RemoveAt(6))]);
        var missing = root with { PublicEvidence = new(root.PublicEvidence.SchemaVersion, true,
            events.SetItem(146, new(146, events[146].OwnerOrdinal, changed))) };
        Assert.False(NativePublicShopCondition.TryCreate(missing, Prior, out _, out string? reason));
        Assert.Equal("shop_requires_all_fifteen_public_slots", reason);
        Assert.False(NativePublicShopCondition.TryCreate(root, Prior with { SchemaVersion = NativeTapePrior.Version }, out _, out reason));
        Assert.Equal("shop_requires_explicit_hybrid_prior", reason);
        var unknown = events[131];
        Assert.IsType<PublicMapChosen>(unknown.Payload);
        Assert.True(NativePublicShopCondition.ReviewedRoomAfterMapChoice(events, unknown));
        // Constructed missing-room evidence: after '?' the next visible owner is
        // another Map, as an unobserved treasure resolution could appear. This
        // is not a claim that pinned native treasure has a skip-relic action.
        var absentRoom = events.SetItem(133, new(133, events[133].OwnerOrdinal,
            new PublicOwnerStarted(PublicEvidenceOwnerKind.Map, 0, 3, null, true)));
        Assert.False(NativePublicShopCondition.ReviewedRoomAfterMapChoice(absentRoom, unknown));
        // Remove the Event and its OutsideChoice owner together, then renumber
        // the following Map/Shop transcript so the public DTO remains valid.
        var withoutRoom = events.Take(133).Concat(events.Skip(141).Take(8).Select(e =>
            new PublicRunEvidenceEvent(e.EventOrdinal - 8, e.OwnerOrdinal - 2, e.Payload switch
            {
                PublicMapChosen c => new PublicMapChosen(c.OfferEventOrdinal - 8, c.Coordinate),
                PublicOptionChosen c => new PublicOptionChosen(c.OfferEventOrdinal - 8, c.Key),
                _ => e.Payload,
            }))).ToImmutableArray();
        var missingRoom = root with { PublicEvidence = new(root.PublicEvidence.SchemaVersion, true, withoutRoom) };
        Assert.False(NativePublicShopCondition.TryCreate(missingRoom, Prior, out _, out reason));
        Assert.Equal("shop_prefix_has_unreviewed_map_room", reason);
        Assert.False(NativePublicShopCondition.ReviewedRoomAfterMapChoice(events.Take(133).ToArray(), unknown));
        // A displayed but unchosen relic still consumes its bag position.
        var priorOffers = (PublicOffersObserved)events[120].Payload;
        var earlierRelic = new PublicOffersObserved(priorOffers.Groups.Add(new(PublicOfferGroupKind.Primary,
            PublicOfferSelectionMode.Independent, [new("extra_relic", PublicOfferKind.Relic,
                relic: new PublicRelic("Vajra", new Dictionary<string, int>()))])));
        var consumed = root with { PublicEvidence = new(root.PublicEvidence.SchemaVersion, true,
            events.SetItem(120, new(120, events[120].OwnerOrdinal, earlierRelic))) };
        Assert.False(NativePublicShopCondition.TryCreate(consumed, Prior, out _, out reason));
        Assert.Equal("shop_prefix_has_prior_relic_offer", reason);
    }

    [Theory]
    [InlineData("bag_dispose")]
    [InlineData("stock_dispose")]
    [InlineData("rewards_alias")]
    [InlineData("bag_restore")]
    [InlineData("stock_restore")]
    [InlineData("bag_missing")]
    [InlineData("stock_missing")]
    public async Task FatalBoundariesRemainStickyAndCannotPassCompletion(string failureKind)
    {
        var recipe = Prior.Draw(new Rng(24008, "nosl-native-tape-source-draw-v1"));
        DecisionPacket root;
        await using (var source = await NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Prior, recipe))) root = source!.Observe();
        Assert.True(NativePublicShopCondition.TryCreate(root, Prior, out var condition, out var reason), reason);
        var tape = NativeLabelTape.ForDeclaredPrior(Prior, recipe);
        var rewards = (NativeRewardsOracle)typeof(NativeLabelTape).GetField("_rewardsOracle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tape)!;
        var prefix = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
        Exception? captured = null;
        IDisposable Force(IReadOnlyList<ulong> words, Rng rng, string purpose)
        {
            var before = rng.ToSerializable();
            bool incomplete = failureKind == "bag_missing" && purpose.Contains("whole player")
                || failureKind == "stock_missing" && purpose.Contains("merchant inventory");
            var inner = prefix(incomplete ? words.Concat(new[] { 0UL }).ToArray() : words, rng, purpose);
            bool inject = failureKind == "bag_dispose" && purpose.Contains("whole player")
                || failureKind == "stock_dispose" && purpose.Contains("merchant inventory");
            bool restore = failureKind == "bag_restore" && purpose.Contains("whole player")
                || failureKind == "stock_restore" && purpose.Contains("merchant inventory");
            return inject ? new DisposalFailure(inner) : restore ? new DisposalAction(inner, () => rng.LoadFromSerializable(before)) : inner;
        }
        var proposal = new NativePublicShopProposal(condition!, a => failureKind == "rewards_alias" || rewards.WasVisited(a),
            rewards.ForceFresh, new Rng(514).NextUnsignedLong, Force, e => captured = e);
        bool attached = false;
        using var merchant = LabelMerchantScope.Enter(proposal.BeginBag, context =>
        {
            if (!attached) { proposal.AttachHypotheticalRun((RunState)context.Player.RunState); attached = true; }
            return proposal.BeginInventory(context);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeRunWorld.OpenLabelTapeAsync(Prior.Execution, recipe, tape));
        Assert.NotNull(captured);
        Assert.Contains(failureKind == "rewards_alias" ? "fresh Rewards cells"
            : failureKind.EndsWith("restore") ? "restored full-state draws"
            : failureKind.EndsWith("missing") ? "complete conditioned word sequence" : "injected shop disposal", error.Message);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
        Assert.Equal(0, proposal.ConditionedOfferCount);
        if (failureKind == "rewards_alias") Assert.Equal(0, rewards.ConditionedCells);
    }
    private sealed class DisposalFailure(IDisposable inner) : IDisposable
    {
        public void Dispose() { inner.Dispose(); throw new InvalidOperationException("injected shop disposal failure"); }
    }
    private sealed class DisposalAction(IDisposable inner, Action action) : IDisposable
    {
        public void Dispose() { inner.Dispose(); action(); }
    }
    private sealed class AbortProbe : IAbortableLabelRewardBoundary
    {
        internal bool Aborted { get; private set; }
        internal bool Disposed { get; private set; }
        public void Abort() => Aborted = true;
        public void Dispose()
        {
            Disposed = true;
            if (!Aborted) throw new InvalidOperationException("boundary validation must not replace a native failure");
        }
    }
    private static Dictionary<RelicRarity, List<RelicModel>> Buckets(RelicGrabBag bag) =>
        (Dictionary<RelicRarity, List<RelicModel>>)typeof(RelicGrabBag).GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bag)!;
}
