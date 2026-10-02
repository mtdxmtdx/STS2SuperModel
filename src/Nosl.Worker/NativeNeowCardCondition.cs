using System.Numerics;
using Nosl.Contracts;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.CardPools;
using Sts2Sim.Core.Odds;
using Sts2Sim.Core.Models.Events;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace Nosl.Worker;

/// <summary>
/// Detached initial-Neow evidence. ArcaneScroll's single grant is the initial
/// event's public deck delta. ScrollBoxes constrains both displayed bundles,
/// including the unselected bundle. Kaleidoscope fixes both displayed offers and
/// their uniquely identified character-pool order. Later acquisitions are never inferred here.
/// </summary>
internal sealed class NativeNeowCardCondition
{
    internal string RelicId { get; }
    internal IReadOnlyList<string> BaseCardIds { get; }
    internal IReadOnlyList<IReadOnlyList<CardModel>> SlotPools { get; }
    internal IReadOnlyList<CardModel> Commons { get; }
    internal IReadOnlyList<CardModel> Uncommons { get; }
    internal ShuffleRational Envelope { get; }
    internal IReadOnlyList<ShuffleRational> SlotMasses { get; }
    internal IReadOnlyList<CardPoolModel> OtherCharacterPools { get; } = [];
    internal IReadOnlyList<string> PoolPrefixIds { get; } = [];
    internal IReadOnlyList<ShuffleRational> PoolShuffleMasses { get; } = [];
    internal LabelCardRarityThresholds? RarityThresholds { get; }

    private NativeNeowCardCondition(string relicId, string[] ids)
    {
        RelicId = relicId; BaseCardIds = Array.AsReadOnly(ids);
        var cards = ModelDb.Character<Silent>().CardPool.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false).ToArray();
        Commons = Array.AsReadOnly(cards.Where(c => c.Rarity == CardRarity.Common).ToArray());
        Uncommons = Array.AsReadOnly(cards.Where(c => c.Rarity == CardRarity.Uncommon).ToArray());
        var pools = new List<IReadOnlyList<CardModel>>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var otherPools = PlayerUnlockState.AllUnlocked().CharacterCardPools
            .Where(pool => pool.GetType() != ModelDb.Character<Silent>().CardPool.GetType())
            .OrderBy(pool => ModelDb.GetEntry(pool.GetType()), StringComparer.Ordinal).ToArray();
        var prefixes = new List<string>();
        if (relicId == nameof(Kaleidoscope))
        {
            Require(otherPools.Length == 4, "kaleidoscope_native_four_pool_shape_changed");
            OtherCharacterPools = Array.AsReadOnly(otherPools);
            // Native base odds depend only on the public ascension. This catalog
            // odds object makes no draw and does not construct or inspect a source run.
            RarityThresholds = new CardRarityOdds(new Rng(0), new AscensionManager(10))
                .GetLabelRollThresholds(CardRarityOddsType.RegularEncounter, changesFutureOdds: false);
        }
        for (int slot = 0; slot < ids.Length; slot++)
        {
            CardModel[] pool;
            if (relicId == nameof(Kaleidoscope))
            {
                var matches = otherPools.Where(p => p.GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false)
                    .Any(c => c.GetType().Name == ids[slot])).ToArray();
                Require(matches.Length == 1, "kaleidoscope_public_identity_requires_unique_other_character_pool");
                prefixes.Add(matches[0].GetType().Name);
                pool = matches[0].GetUnlockedCards(PlayerUnlockState.AllUnlocked(), false).Distinct().ToArray();
                Require(new[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common }
                    .All(rarity => pool.Any(c => c.Rarity == rarity)), "kaleidoscope_native_rarity_support_changed");
            }
            else pool = relicId == nameof(ArcaneScroll) ? cards.Where(c => c.Rarity == CardRarity.Rare).ToArray()
                : (slot % 3 == 2 ? Uncommons : Commons).Where(c => !usedIds.Contains(c.GetType().Name)).ToArray();
            Require(pool.Select(c => c.Id).Distinct().Count() == pool.Length, "neow_native_pool_duplicate_ids_not_certified");
            Require(pool.Any(c => c.GetType().Name == ids[slot]), "neow_public_card_has_no_native_pool_support");
            pools.Add(Array.AsReadOnly(pool)); usedIds.Add(ids[slot]);
        }
        SlotPools = pools.AsReadOnly();
        SlotMasses = Array.AsReadOnly(pools.Select((pool, slot) => relicId == nameof(Kaleidoscope)
            ? RarityMass(pool, ids[slot], RarityThresholds!.Value) : UniformMass(pool, ids[slot])).ToArray());
        if (relicId == nameof(Kaleidoscope))
        {
            PoolPrefixIds = prefixes.AsReadOnly();
            var shuffles = new List<ShuffleRational>();
            for (int offer = 0; offer < 2; offer++)
            {
                var prefix = prefixes.Skip(3 * offer).Take(3).ToArray();
                Require(prefix.Distinct().Count() == 3, "kaleidoscope_distinct_pool_prefix_required");
                // Three unique positions of four pin the entire permutation. Its
                // native bucket mass is independent of proposal sampling words.
                var plan = ConditionalShuffleProposal.Create(otherPools.Select(p => p.GetType().Name).ToArray(),
                    prefix, () => ulong.MaxValue)!;
                shuffles.Add(plan.NativeToProposalRatio);
            }
            PoolShuffleMasses = shuffles.AsReadOnly();
        }
        Envelope = SlotMasses.Concat(PoolShuffleMasses)
            .Aggregate(new ShuffleRational(1, 1), (p, q) => p.Multiply(q.Numerator, q.Denominator));
    }

    internal static bool TryCreate(DecisionPacket root, NativeTapePrior prior,
        out NativeNeowCardCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        try
        {
            Require(prior.UsesRewardsProvenance, "neow_cards_require_rewards_hybrid_prior");
            Require(root.PublicEvidence is { CompleteFromRunStart: true }, "neow_cards_require_complete_public_evidence");
            Require(NativeNeowCondition.TryCreate(root, prior, out var neow, out _)
                && neow!.ObservedCurseId is not null, "neow_cards_require_typed_initial_choice");
            string relic = neow!.TargetRelicId;
            Require(relic is nameof(ArcaneScroll) or nameof(ScrollBoxes) or nameof(Kaleidoscope), "neow_card_source_not_certified");
            var events = root.PublicEvidence!.Events;
            var start = (PublicRunStarted)events[0].Payload;
            Require(start.Assets.Relics.Select(r => r.Id).SequenceEqual(new[] { nameof(RingOfTheSnake) }),
                "neow_cards_require_native_starter_inventory");
            string[] ids;
            if (relic == nameof(ArcaneScroll))
            {
                Require(events.Length > 4 && events[4] is { OwnerOrdinal: 0, Payload: PublicOwnerEnded
                    { Outcome: PublicEvidenceOwnerOutcome.Completed, Assets: not null } }, "arcane_scroll_public_settlement_missing");
                var after = ((PublicOwnerEnded)events[4].Payload).Assets!.Deck.ToList();
                foreach (var before in start.Assets.Deck)
                {
                    int index = after.FindIndex(card => PublicJson.Serialize(card) == PublicJson.Serialize(before));
                    Require(index >= 0, "arcane_scroll_public_deck_delta_not_certified");
                    after.RemoveAt(index);
                }
                Require(after.Count == 1, "arcane_scroll_public_single_grant_required");
                ids = [after[0].Id];
            }
            else if (relic == nameof(ScrollBoxes))
            {
                Require(events.Length > 5 && events[4] is { OwnerOrdinal: 1, Payload: PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.OutsideChoice, ActIndex: 0, Floor: 1,
                        ParentOwnerOrdinal: 0, CompleteFromOwnerStart: true } }
                    && events[5] is { OwnerOrdinal: 1, Payload: PublicCardsObserved }, "scroll_boxes_public_bundle_owner_missing");
                var choice = ((PublicCardsObserved)events[5].Payload).Choice;
                Require(choice is { Source: nameof(ScrollBoxes), Min: 1, Max: 1, Cancelable: false,
                    CandidateOrder: "public", Bundles.Length: 2 } && choice.Bundles.All(b => b.Length == 3),
                    "scroll_boxes_requires_both_complete_displayed_bundles");
                ids = choice.Bundles!.SelectMany(bundle => bundle).Select(card => card.Id).ToArray();
                Require(ids.Distinct(StringComparer.Ordinal).Count() == 6, "scroll_boxes_silent_unique_ids_required");
            }
            else
            {
                Require(events.Length > 5 && events[4] is { OwnerOrdinal: 1, Payload: PublicOwnerStarted
                    { OwnerKind: PublicEvidenceOwnerKind.Reward, ActIndex: 0, Floor: 1,
                        ParentOwnerOrdinal: 0, CompleteFromOwnerStart: true } }
                    && events[5] is { OwnerOrdinal: 1, Payload: PublicOffersObserved }, "kaleidoscope_initial_public_reward_owner_missing");
                var offers = (PublicOffersObserved)events[5].Payload;
                var groups = offers.Groups.Where(group => group.Offers.Any(o => o.OfferKind == PublicOfferKind.Card)).ToArray();
                Require(groups.Length == 2 && groups[0].GroupKind == PublicOfferGroupKind.Primary
                    && groups[1].GroupKind == PublicOfferGroupKind.Extra, "kaleidoscope_requires_both_displayed_offers");
                var cards = new List<string>();
                for (int offer = 0; offer < 2; offer++)
                {
                    var options = groups[offer].Offers.Where(o => o.OfferKind == PublicOfferKind.Card).ToArray();
                    string prefix = offer == 0 ? "card:card:" : "extra:0:card:";
                    Require(groups[offer].SelectionMode == PublicOfferSelectionMode.ChooseOne && options.Length == 3
                        && options.Select(o => o.Key).SequenceEqual(Enumerable.Range(0, 3).Select(i => prefix + i)),
                        "kaleidoscope_requires_three_ordered_candidates_per_offer");
                    cards.AddRange(options.Select(o => o.Card!.Id));
                }
                ids = cards.ToArray();
            }
            condition = new(relic, ids); return true;
        }
        catch (NotCertifiedException error) { reason = error.Message; return false; }
    }

    internal static ShuffleRational UniformMass(IReadOnlyList<CardModel> pool, string id, int bits = 53)
    {
        BigInteger mass = BigInteger.Zero;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].GetType().Name == id) mass += ConditionalShuffleProposal.Factor(pool.Count, i, bits).BucketSize;
        return new(mass, BigInteger.One << bits);
    }

    internal static ShuffleRational RarityMass(IReadOnlyList<CardModel> pool, string id,
        LabelCardRarityThresholds thresholds, int bits = 53)
    {
        var branches = new[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common }
            .Select(rarity => new LabelRewardCardBranch(rarity, pool.Where(c => c.Rarity == rarity).ToArray())).ToArray();
        var mass = NativeRewardIdentityMath.Arms(branches, thresholds, id, bits)
            .Aggregate(BigInteger.Zero, (sum, arm) => sum + arm.Mass);
        return new(mass, BigInteger.One << (2 * bits));
    }

    private static void Require(bool value, string reason) { if (!value) throw new NotCertifiedException(reason); }
    private sealed class NotCertifiedException(string reason) : Exception(reason);
}

/// <summary>
/// Owned hypothetical Neow grants under the existing Rewards partition. The
/// native generator consumes every draw, clones cards and applies normal hooks.
/// Both native/proposal factors and the envelope use exact raw-word buckets.
/// </summary>
internal sealed class NativeNeowCardProposal(NativeNeowCardCondition condition,
    Func<LabelRandomAddressV1, bool> wasVisited, Action<LabelRandomAddressV1, ulong> forceFresh,
    Func<ulong> nextProposalWord,
    Func<IReadOnlyList<ulong>, string, IDisposable>? forceStateWords = null)
{
    private RunState? _run;
    private bool _begun, _completed, _failed, _activeShuffle, _activeSelection;
    private int _shuffleCount;
    internal int ConditionedPoolShuffleCount => _shuffleCount;
    internal int ConditionedCardCount { get; private set; }
    internal ShuffleRational NativeToProposalRatio { get; private set; } = new(1, 1);
    internal ShuffleRational Envelope => condition.Envelope;

    internal void AttachHypotheticalRun(RunState run)
    {
        if (_run is not null) throw new InvalidOperationException("Neow card proposal already owns a native run");
        _run = run;
    }

    internal IDisposable? BeginSelection(LabelRewardCardSelectionContext context)
    {
        if (condition.RelicId == nameof(Kaleidoscope)) return BeginKaleidoscopeSelection(context);
        if (condition.RelicId != nameof(ArcaneScroll) || !IsTargetBoundary(context.Player)) return null;
        try
        {
            Begin(context.Player, context.Rng);
            if (context.Kind != LabelRewardCardSelectionKind.CreationOptions || context.SelectionIndex != 0
                || context.OptionCount != 1 || context.Source != CardCreationSource.Other
                || context.Flags != CardCreationFlags.NoUpgradeRoll || context.OddsType != CardRarityOddsType.Uniform
                || context.Thresholds is not null || context.ChangesFutureOdds || context.Branches.Count != 1
                || context.Branches[0].RolledRarity is not null
                || !context.RemainingCards.SequenceEqual(condition.SlotPools[0])
                || !context.Branches[0].Candidates.SequenceEqual(context.RemainingCards))
                throw new InvalidOperationException("ArcaneScroll departed from its certified native uniform rare pool");
            var plan = NativeRewardIdentityMath.Create(context, condition.BaseCardIds[0], nextProposalWord);
            return Force(context.Rng, plan.RawWords, plan.NativeToProposalRatio, 1, 1, condition.Envelope);
        }
        catch { _failed = true; throw; }
    }

    internal IDisposable? BeginScrollBoxes(LabelScrollBoxesContext context)
    {
        if (condition.RelicId != nameof(ScrollBoxes) || !IsTargetBoundary(context.Player)) return null;
        try
        {
            Begin(context.Player, context.Rng);
            if (!context.Commons.SequenceEqual(condition.Commons) || !context.Uncommons.SequenceEqual(condition.Uncommons))
                throw new InvalidOperationException("ScrollBoxes post-modifier pools differ from the root-wide startup certificate");
            var words = new List<ulong>();
            var ratio = new ShuffleRational(1, 1);
            for (int slot = 0; slot < 6; slot++)
            {
                var pool = condition.SlotPools[slot];
                // The direct NextItem overload has one uniform index word and no rarity draw.
                var selection = new LabelRewardCardSelectionContext(context.Player, context.Rng,
                    LabelRewardCardSelectionKind.CreationOptions, slot, 6, CardCreationSource.Other,
                    CardCreationFlags.NoUpgradeRoll | CardCreationFlags.NoRarityModification, CardRarityOddsType.Uniform,
                    null, false, pool, new[] { new LabelRewardCardBranch(null, pool) });
                var plan = NativeRewardIdentityMath.Create(selection, condition.BaseCardIds[slot], nextProposalWord);
                words.AddRange(plan.RawWords);
                ratio = ratio.Multiply(plan.NativeToProposalRatio.Numerator, plan.NativeToProposalRatio.Denominator);
            }
            return Force(context.Rng, words, ratio, 6, 6, condition.Envelope);
        }
        catch { _failed = true; throw; }
    }

    internal IDisposable? BeginShuffle(Rng rng, IReadOnlyList<object?> items)
    {
        if (condition.RelicId != nameof(Kaleidoscope) || _run is null
            || !IsTargetBoundary(_run.Players.Single()) || !ReferenceEquals(rng, _run.Rng.Niche)) return null;
        try
        {
            if (_shuffleCount >= 2 || _activeShuffle || _activeSelection || ConditionedCardCount != 3 * _shuffleCount)
                throw new InvalidOperationException("Kaleidoscope repeated or reordered its native pool shuffles");
            if (items.Count != 4 || !items.SequenceEqual(condition.OtherCharacterPools))
                throw new InvalidOperationException("Kaleidoscope departed from its four sorted native character pools");
            if (_shuffleCount == 0) Begin(_run.Players.Single(), _run.Players.Single().PlayerRng.Rewards);
            var prefix = condition.PoolPrefixIds.Skip(3 * _shuffleCount).Take(3).ToArray();
            var plan = ConditionalShuffleProposal.Create(condition.OtherCharacterPools.Select(p => p.GetType().Name).ToArray(),
                prefix, nextProposalWord) ?? throw new NativePublicConstraintMismatchException("Public Kaleidoscope pool order has zero native mass");
            if (plan.NativeToProposalRatio != condition.PoolShuffleMasses[_shuffleCount])
                throw new InvalidOperationException("Kaleidoscope pool likelihood escaped its fixed-root permutation");
            var force = forceStateWords ?? throw new InvalidOperationException("Kaleidoscope requires the owned fresh-cell state oracle");
            var inner = force(plan.RawWords, "Kaleidoscope pools");
            _activeShuffle = true;
            AddRatio(plan.NativeToProposalRatio);
            return new Completion(() =>
            {
                try { inner.Dispose(); _activeShuffle = false; _shuffleCount++; }
                catch { _failed = true; throw; }
            });
        }
        catch { _failed = true; throw; }
    }

    private IDisposable? BeginKaleidoscopeSelection(LabelRewardCardSelectionContext context)
    {
        if (!IsTargetBoundary(context.Player)) return null;
        try
        {
            int slot = ConditionedCardCount;
            if (!_begun || _activeShuffle || _activeSelection || slot >= 6 || _shuffleCount != slot / 3 + 1
                || !ReferenceEquals(context.Player.RunState, _run) || !ReferenceEquals(context.Rng, context.Player.PlayerRng.Rewards)
                || context.Kind != LabelRewardCardSelectionKind.CreationOptions || context.SelectionIndex != 0 || context.OptionCount != 1
                || context.Source != CardCreationSource.Other || context.Flags != CardCreationFlags.NoCardPoolModifications
                || context.OddsType != CardRarityOddsType.RegularEncounter || context.Thresholds != condition.RarityThresholds
                || context.ChangesFutureOdds || !context.RemainingCards.SequenceEqual(condition.SlotPools[slot])
                || !context.Branches.Select(branch => branch.RolledRarity)
                    .SequenceEqual(new CardRarity?[] { CardRarity.Rare, CardRarity.Uncommon, CardRarity.Common })
                || context.Branches.Any(branch => !context.RemainingCards.Where(c => c.Rarity == branch.RolledRarity)
                    .SequenceEqual(branch.Candidates)))
                throw new InvalidOperationException("Kaleidoscope departed from its native single-pool base-rarity selection");
            var plan = NativeRewardIdentityMath.Create(context, condition.BaseCardIds[slot], nextProposalWord);
            if (plan.RawWords.Count != 2) throw new InvalidOperationException("Kaleidoscope rarity/index draw contract changed");
            // The third native upgrade word remains an ordinary Rewards oracle cell.
            return Force(context.Rng, plan.RawWords, plan.NativeToProposalRatio, 1, 3, condition.SlotMasses[slot]);
        }
        catch { _failed = true; throw; }
    }

    private bool IsTargetBoundary(Player player) => _run?.CurrentRoom is EventRoom { Event: Neow }
        && player.Relics.Any(relic => relic.GetType().Name == condition.RelicId);

    private void Begin(Player player, Rng rng)
    {
        if (_begun) throw new InvalidOperationException("Initial Neow card source repeated its certified generation");
        if (_run is null || !ReferenceEquals(player.RunState, _run) || _run.Players.Count != 1
            || _run.CurrentActIndex != 0 || _run.TotalFloor != 1 || player.Character is not Silent
            || !ReferenceEquals(rng, player.PlayerRng.Rewards)
            || !player.Relics.Select(r => r.GetType().Name).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { nameof(RingOfTheSnake), condition.RelicId }.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Initial Neow cards escaped the owned native startup inventory");
        _begun = true;
    }

    private IDisposable Force(Rng rng, IReadOnlyList<ulong> words, ShuffleRational ratio, int cardCount,
        int expectedAdvance, ShuffleRational bound)
    {
        if (ratio != bound) throw new InvalidOperationException("Native Neow identity mass differs from its fixed-root envelope");
        var before = Address(rng);
        var addresses = Enumerable.Range(0, words.Count).Select(i => before with { RawCursor = checked(before.RawCursor + (ulong)i) }).ToArray();
        if (addresses.Any(wasVisited))
            throw new InvalidOperationException("Neow card proposal requires fresh Rewards cells; alias invalidates its envelope");
        for (int i = 0; i < words.Count; i++) forceFresh(addresses[i], words[i]);
        AddRatio(ratio); _activeSelection = true;
        return new Completion(() =>
        {
            try
            {
                if (Address(rng) != before with { RawCursor = checked(before.RawCursor + (ulong)expectedAdvance) }
                    || addresses.Any(address => !wasVisited(address)))
                    throw new InvalidOperationException("Native Neow card generation restored, skipped or added Rewards draws");
                ConditionedCardCount += cardCount; _activeSelection = false;
                _completed = ConditionedCardCount == condition.BaseCardIds.Count
                    && (condition.RelicId != nameof(Kaleidoscope) || _shuffleCount == 2);
            }
            catch { _failed = true; throw; }
        });
    }

    private void AddRatio(ShuffleRational factor) => NativeToProposalRatio = NativeToProposalRatio.Multiply(factor.Numerator, factor.Denominator);

    internal void ValidateCompletion()
    {
        if (!_completed || _failed || _activeShuffle || _activeSelection || NativeToProposalRatio != Envelope) throw new InvalidOperationException("Neow card proposal did not complete its recorded grant or bundles");
    }

    internal bool AcceptCorrection(Func<ulong> nextWord)
    {
        ValidateCompletion();
        return NativeNeowProposal.ExactRationalBernoulli(new(
            NativeToProposalRatio.Numerator * Envelope.Denominator,
            NativeToProposalRatio.Denominator * Envelope.Numerator), nextWord);
    }

    private static LabelRandomAddressV1 Address(Rng rng)
    {
        var p = rng.ToSerializable().LabelProvenance;
        if (p is not { Law: LabelRandomProvenance.LawId, Partition: LabelRandomProvenance.RewardsPartition,
            OriginFamily: LabelRandomProvenance.RewardsOrigin, InitialSeed: { } seed, RawCursor: { } cursor })
            throw new InvalidOperationException("Neow card identity conditioning requires tagged native Rewards provenance");
        return new(LabelRandomProvenance.RewardsOrigin, seed, cursor);
    }

    private sealed class Completion(Action action) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; action(); }
    }
}
