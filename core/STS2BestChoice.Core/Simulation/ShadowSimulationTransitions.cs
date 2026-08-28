using System.Collections.Immutable;
using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Simulation;

internal sealed partial class DeterministicSimulator
{
    public ImmutableArray<ChanceBranch<MutableCombatState>> ProjectToNextPlayerTurnOutcomes(
        MutableCombatState original,
        int maximumExactBranches = 32,
        int sampleCount = 32)
    {
        if (!NeedsUnknownTurnStartShuffle(original))
            return [new ChanceBranch<MutableCombatState>(
                "确定结果",
                1m,
                ProjectToNextPlayerTurn(original),
                OutcomeKind.Deterministic)];

        var pool = original.DiscardPile.ToArray();
        var exact = FactorialAtMost(pool.Length, maximumExactBranches) > 0;
        var permutations = exact
            ? EnumeratePermutations(pool).Take(maximumExactBranches).ToArray()
            : SamplePermutations(pool, Math.Max(1, sampleCount), original.ExactKey()).ToArray();
        if (permutations.Length == 0)
            return [new ChanceBranch<MutableCombatState>(
                "无可洗牌弃牌",
                1m,
                ProjectToNextPlayerTurn(original),
                OutcomeKind.Deterministic)];

        var probability = 1m / permutations.Length;
        return permutations.Select(permutation =>
        {
            var prepared = original.Clone();
            prepared.DrawPile.AddRange(permutation);
            prepared.DiscardPile.Clear();
            var state = ProjectToNextPlayerTurn(prepared);
            var message = "回合结束抽牌使用了未知 Shuffle RNG；当前按牌池排列展开，后续 RNG 状态标记为估算。";
            state.Risks.Add(new RiskEvent(
                PredictionRiskReason.UnsupportedRandomSource,
                PredictionRiskSeverity.Estimated,
                message,
                RngSnapshotSet.Shuffle));
            state.Restrictions.Add(new RestrictionReason(
                "unknown_shuffle_rng_state",
                message,
                RngSnapshotSet.Shuffle));
            if (!exact)
            {
                var sampleMessage = $"回合结束未知洗牌超过 {maximumExactBranches} 个排列，使用 {permutations.Length} 个样本估计。";
                state.Risks.Add(new RiskEvent(
                    PredictionRiskReason.ChanceBranchSampled,
                    PredictionRiskSeverity.Estimated,
                    sampleMessage,
                    RngSnapshotSet.Shuffle));
                state.Restrictions.Add(new RestrictionReason(
                    "sampled_end_turn_shuffle",
                    sampleMessage,
                    RngSnapshotSet.Shuffle));
            }

            return new ChanceBranch<MutableCombatState>(
                ShuffleLabel(permutation, Math.Min(permutation.Length, original.Player.CardsPerTurn)),
                probability,
                state,
                OutcomeKind.Stochastic);
        }).ToImmutableArray();
    }

    public ImmutableArray<ChanceBranch<MutableCombatState>> UsePotionOutcomes(
        MutableCombatState original,
        PotionState potion,
        string? targetId,
        int maximumExactBranches = 32,
        int sampleCount = 32)
    {
        var drawCount = DrawCount(potion.Effects);
        if (!NeedsUnknownShuffle(original, drawCount))
            return [new ChanceBranch<MutableCombatState>("确定结果", 1m, UsePotion(original, potion, targetId), OutcomeKind.Deterministic)];

        var baseState = UsePotion(original, potion with { Effects = WithoutDraw(potion.Effects) }, targetId);
        var knownDraws = Math.Min(drawCount, baseState.DrawPile.Count);
        Draw(baseState, knownDraws);
        var unresolvedDraws = drawCount - knownDraws;
        var pool = baseState.DiscardPile.ToArray();
        baseState.DiscardPile.Clear();
        var exact = FactorialAtMost(pool.Length, maximumExactBranches) > 0;
        var permutations = exact
            ? EnumeratePermutations(pool).Take(maximumExactBranches).ToArray()
            : SamplePermutations(pool, Math.Max(1, sampleCount), original.ExactKey()).ToArray();
        if (permutations.Length == 0)
            return [new ChanceBranch<MutableCombatState>("无可抽取卡牌", 1m, baseState, OutcomeKind.Deterministic)];

        var probability = 1m / permutations.Length;
        return permutations.Select(permutation =>
        {
            var state = baseState.Clone();
            state.DrawPile.AddRange(permutation);
            Draw(state, unresolvedDraws);
            if (!exact)
            {
                var message = $"药水触发的未知洗牌超过 {maximumExactBranches} 个排列，使用 {permutations.Length} 个样本估计。";
                state.Risks.Add(new RiskEvent(PredictionRiskReason.ChanceBranchSampled, PredictionRiskSeverity.Estimated, message, potion.ModelId));
                state.Restrictions.Add(new RestrictionReason("sampled_shuffle", message, potion.ModelId));
            }
            return new ChanceBranch<MutableCombatState>(
                ShuffleLabel(permutation, unresolvedDraws),
                probability,
                state);
        }).ToImmutableArray();
    }

    public ImmutableArray<ChanceBranch<MutableCombatState>> PlayCardOutcomes(
        MutableCombatState original,
        CardState card,
        string? targetId,
        ChoiceSpec? choice,
        int maximumExactBranches = 32,
        int sampleCount = 32)
    {
        var previewPlayCount = EffectivePlayCount(original, card, consumeModifiers: false);
        var hasReboot = card.Effects.Any(static effect => effect.Kind == EffectKind.Reboot) ||
                        choice is not null && choice.Effects.Any(static effect => effect.Kind == EffectKind.Reboot);
        if (hasReboot &&
            original.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } &&
            original.RngState == 0)
            return RebootOutcomes(
                original,
                card,
                targetId,
                choice,
                previewPlayCount,
                maximumExactBranches,
                sampleCount);
        var randomOrbEffects = card.Effects
            .Concat(choice?.Effects ?? [])
            .Where(static effect => effect.Kind == EffectKind.ChannelOrbs && effect.StatusId == "RANDOM")
            .ToImmutableArray();
        if (randomOrbEffects.Length > 0)
        {
            var randomOrbStrippedCard = card with
            {
                Effects = card.Effects.Where(static effect => !(effect.Kind == EffectKind.ChannelOrbs && effect.StatusId == "RANDOM")).ToImmutableArray()
            };
            var randomOrbStrippedChoice = choice is null ? null : choice with
            {
                Effects = choice.Effects.Where(static effect => !(effect.Kind == EffectKind.ChannelOrbs && effect.StatusId == "RANDOM")).ToImmutableArray()
            };
            var baseOutcomes = PlayCardOutcomes(original, randomOrbStrippedCard, targetId, randomOrbStrippedChoice, maximumExactBranches, sampleCount);
            var repeatedRandomOrbEffects = RepeatEffects(randomOrbEffects, previewPlayCount);
            return baseOutcomes.SelectMany(baseOutcome =>
                RandomOrbOutcomes(baseOutcome.State, repeatedRandomOrbEffects, card.ModelId, maximumExactBranches)
                    .Select(orbOutcome => new ChanceBranch<MutableCombatState>(
                        baseOutcome.Label == "确定结果" ? orbOutcome.Label : $"{baseOutcome.Label}；{orbOutcome.Label}",
                        baseOutcome.Probability * orbOutcome.Probability,
                        orbOutcome.State,
                        baseOutcome.Kind == OutcomeKind.Deterministic && orbOutcome.Kind == OutcomeKind.Deterministic
                            ? OutcomeKind.Deterministic
                            : OutcomeKind.Stochastic)))
                .ToImmutableArray();
        }
        var randomEffects = card.Effects
            .Concat(choice?.Effects ?? [])
            .Where(static effect => effect.Kind == EffectKind.RandomEnemyDamage && effect.UnsupportedReason is null)
            .ToImmutableArray();
        if (randomEffects.Length > 0)
        {
            var randomStrippedCard = card with
            {
                Effects = WithoutRandomTargetDamage(card.Effects),
                Choices = card.SafeChoices.Select(item => item with
                {
                    Effects = WithoutRandomTargetDamage(item.Effects)
                }).ToImmutableArray()
            };
            var randomStrippedChoice = choice is null
                ? null
                : randomStrippedCard.SafeChoices.FirstOrDefault(item => item.Id == choice.Id);
            var baseOutcomes = PlayCardOutcomes(
                original,
                randomStrippedCard,
                targetId,
                randomStrippedChoice,
                maximumExactBranches,
                sampleCount);
            var randomEnergySpent = card.CostsX
                ? original.Player.Energy
                : EnergyCostToPlay(original, card);
            var repeatedRandomEffects = ExpandRandomEffects(randomEffects, previewPlayCount, randomEnergySpent);
            return baseOutcomes.SelectMany(baseOutcome =>
                    RandomEnemyDamageOutcomes(
                        baseOutcome.State,
                        repeatedRandomEffects,
                        card.ModelId,
                        maximumExactBranches,
                        sampleCount)
                    .Select(randomOutcome => new ChanceBranch<MutableCombatState>(
                        baseOutcome.Label == "确定结果"
                            ? randomOutcome.Label
                            : $"{baseOutcome.Label}；{randomOutcome.Label}",
                        baseOutcome.Probability * randomOutcome.Probability,
                        randomOutcome.State,
                        baseOutcome.Kind == OutcomeKind.Deterministic && randomOutcome.Kind == OutcomeKind.Deterministic
                            ? OutcomeKind.Deterministic
                            : OutcomeKind.Stochastic)))
                .ToImmutableArray();
        }

        var randomExhaustEffects = card.Effects
            .Concat(choice?.Effects ?? [])
            .Where(static effect => effect.Kind == EffectKind.RandomExhaustCards && effect.UnsupportedReason is null)
            .ToImmutableArray();
        if (randomExhaustEffects.Length > 0)
        {
            var movementStrippedCard = card with
            {
                Effects = WithoutRandomExhaust(card.Effects),
                Choices = card.SafeChoices.Select(item => item with
                {
                    Effects = WithoutRandomExhaust(item.Effects)
                }).ToImmutableArray()
            };
            var movementStrippedChoice = choice is null
                ? null
                : movementStrippedCard.SafeChoices.FirstOrDefault(item => item.Id == choice.Id);
            var baseOutcomes = PlayCardOutcomes(
                original,
                movementStrippedCard,
                targetId,
                movementStrippedChoice,
                maximumExactBranches,
                sampleCount);
            return baseOutcomes.SelectMany(baseOutcome =>
                    RandomExhaustOutcomes(
                        baseOutcome.State,
                        randomExhaustEffects.Sum(static effect => Math.Max(0, (int)effect.Amount)) * previewPlayCount,
                        card.ModelId,
                        maximumExactBranches,
                        sampleCount)
                    .Select(randomOutcome => new ChanceBranch<MutableCombatState>(
                        baseOutcome.Label == "确定结果"
                            ? randomOutcome.Label
                            : $"{baseOutcome.Label}；{randomOutcome.Label}",
                        baseOutcome.Probability * randomOutcome.Probability,
                        randomOutcome.State,
                        baseOutcome.Kind == OutcomeKind.Deterministic && randomOutcome.Kind == OutcomeKind.Deterministic
                            ? OutcomeKind.Deterministic
                            : OutcomeKind.Stochastic)))
                .ToImmutableArray();
        }

        var orderedDraw = HasOrderedDrawEffects(card.Effects) ||
                          choice is not null && HasOrderedDrawEffects(choice.Effects);
        var drawCount = orderedDraw
            ? DrawCount(original, card.Effects.AddRange(choice?.Effects ?? []), cardLeavesHand: true, previewPlayCount)
            : (DrawCount(card.Effects) + (choice is null ? 0 : DrawCount(choice.Effects))) * previewPlayCount;
        if (!NeedsUnknownShuffle(original, drawCount))
            return [new ChanceBranch<MutableCombatState>("确定结果", 1m, PlayCard(original, card, targetId, choice), OutcomeKind.Deterministic)];
        if (orderedDraw)
            return OrderedDrawShuffleOutcomes(
                original, card, targetId, choice, drawCount, maximumExactBranches, sampleCount);

        // Resolve non-draw effects first, then reproduce the unknown shuffle as a
        // chance node. Cards whose draw is interleaved with other effects are kept
        // calculable but explicitly estimated rather than silently treated as exact.
        var energySpent = card.CostsX ? original.Player.Energy : EnergyCostToPlay(original, card);
        var destinationState = original.Clone();
        var finalDestination = EffectiveCardDestination(destinationState, card, energySpent, consumeModifiers: true);
        var playCount = EffectivePlayCount(destinationState, card, consumeModifiers: true);
        var strippedCard = card with
        {
            Effects = WithoutDraw(card.Effects),
            Destination = CardDestination.Remove,
            Choices = card.SafeChoices.Select(item => item with { Effects = WithoutDraw(item.Effects) }).ToImmutableArray()
        };
        var strippedChoice = choice is null
            ? null
            : strippedCard.SafeChoices.FirstOrDefault(item => item.Id == choice.Id);
        var baseState = PlayCard(destinationState, strippedCard, targetId, strippedChoice,
            finalizeDestination: false, forcedPlayCount: playCount);
        var knownDraws = Math.Min(drawCount, baseState.DrawPile.Count);
        Draw(baseState, knownDraws);
        var unresolvedDraws = drawCount - knownDraws;
        var pool = baseState.DiscardPile.ToArray();
        baseState.DiscardPile.Clear();

        var exactCount = FactorialAtMost(pool.Length, maximumExactBranches);
        var exact = exactCount > 0;
        var permutations = exact
            ? EnumeratePermutations(pool).Take(maximumExactBranches).ToArray()
            : SamplePermutations(pool, Math.Max(1, sampleCount), original.ExactKey()).ToArray();
        if (permutations.Length == 0)
            return [new ChanceBranch<MutableCombatState>("无可抽取卡牌", 1m, FinishCard(baseState, card, finalDestination), OutcomeKind.Deterministic)];

        var probability = 1m / permutations.Length;
        var branches = ImmutableArray.CreateBuilder<ChanceBranch<MutableCombatState>>(permutations.Length);
        foreach (var permutation in permutations)
        {
            var state = baseState.Clone();
            state.DrawPile.AddRange(permutation);
            Draw(state, unresolvedDraws);
            FinishCard(state, card, finalDestination);
            if (!exact)
            {
                var message = $"未知洗牌共有超过 {maximumExactBranches} 个排列，使用 {permutations.Length} 个确定性样本估计。";
                state.Risks.Add(new RiskEvent(
                    PredictionRiskReason.ChanceBranchSampled,
                    PredictionRiskSeverity.Estimated,
                    message,
                    card.ModelId));
                state.Restrictions.Add(new RestrictionReason("sampled_shuffle", message, card.ModelId));
            }
            else if (HasInterleavedDraw(card.Effects) || choice is not null && HasInterleavedDraw(choice.Effects))
            {
                var message = "抽牌效果与其他效果交错；当前镜像按效果总量结算抽牌。";
                state.Risks.Add(new RiskEvent(
                    PredictionRiskReason.MethodMirrorIncomplete,
                    PredictionRiskSeverity.Estimated,
                    message,
                    card.ModelId));
                state.Restrictions.Add(new RestrictionReason("interleaved_draw_order", message, card.ModelId));
            }
            var label = unresolvedDraws <= 0
                ? "确定结果"
                : ShuffleLabel(permutation, unresolvedDraws);
            branches.Add(new ChanceBranch<MutableCombatState>(label, probability, state));
        }
        return branches.MoveToImmutable();
    }

    private static ImmutableArray<EffectSpec> RepeatEffects(ImmutableArray<EffectSpec> effects, int count)
    {
        if (count <= 1) return effects;
        var result = ImmutableArray.CreateBuilder<EffectSpec>(effects.Length * count);
        for (var index = 0; index < count; index++)
            result.AddRange(effects);
        return result.ToImmutable();
    }

    private ImmutableArray<ChanceBranch<MutableCombatState>> RebootOutcomes(
        MutableCombatState original,
        CardState card,
        string? targetId,
        ChoiceSpec? choice,
        int playCount,
        int maximumExactBranches,
        int sampleCount)
    {
        if (playCount != 1)
        {
            var state = PlayCard(original, card, targetId, choice);
            var message = "重放重启会产生连续洗牌；当前未知 Shuffle 状态无法可靠展开多次洗牌。";
            state.Risks.Add(new RiskEvent(
                PredictionRiskReason.UnsupportedRandomSource,
                PredictionRiskSeverity.Uncalculable,
                message,
                card.ModelId));
            state.Restrictions.Add(new RestrictionReason("uncalculable_replayed_reboot", message, card.ModelId));
            return [new ChanceBranch<MutableCombatState>("连续洗牌不可计算", 1m, state, OutcomeKind.Stochastic)];
        }

        var pool = original.DiscardPile
            .Concat(original.DrawPile)
            .Concat(original.Hand.Where(item => item.InstanceId != card.InstanceId))
            .ToArray();
        var exact = FactorialAtMost(pool.Length, maximumExactBranches) > 0;
        var permutations = exact
            ? EnumeratePermutations(pool).Take(maximumExactBranches).ToArray()
            : SamplePermutations(pool, Math.Max(1, sampleCount), original.ExactKey()).ToArray();
        if (permutations.Length == 0)
            return [new ChanceBranch<MutableCombatState>(
                "没有可重新洗入的卡牌",
                1m,
                PlayCard(original, card, targetId, choice),
                OutcomeKind.Deterministic)];

        var strippedCard = card with
        {
            Effects = card.Effects.Where(static effect => effect.Kind != EffectKind.Reboot).ToImmutableArray(),
            Choices = card.SafeChoices.Select(item => item with
            {
                Effects = item.Effects.Where(static effect => effect.Kind != EffectKind.Reboot).ToImmutableArray()
            }).ToImmutableArray()
        };
        var strippedChoice = choice is null
            ? null
            : strippedCard.SafeChoices.FirstOrDefault(item => item.Id == choice.Id);
        var drawCount = DrawCount(strippedCard.Effects) +
                        (strippedChoice is null ? 0 : DrawCount(strippedChoice.Effects));
        var probability = 1m / permutations.Length;
        return permutations.Select(permutation =>
        {
            var prepared = original.Clone();
            prepared.Hand.RemoveAll(item => item.InstanceId != card.InstanceId);
            prepared.DrawPile.Clear();
            prepared.DrawPile.AddRange(permutation);
            prepared.DiscardPile.Clear();
            var state = PlayCard(prepared, strippedCard, targetId, strippedChoice);
            if (!exact)
            {
                var message = $"重启洗牌超过 {maximumExactBranches} 个排列，使用 {permutations.Length} 个确定性样本估计。";
                state.Risks.Add(new RiskEvent(
                    PredictionRiskReason.ChanceBranchSampled,
                    PredictionRiskSeverity.Estimated,
                    message,
                    card.ModelId));
                state.Restrictions.Add(new RestrictionReason("sampled_reboot_shuffle", message, card.ModelId));
            }
            return new ChanceBranch<MutableCombatState>(
                ShuffleLabel(permutation, Math.Min(drawCount, permutation.Length)),
                probability,
                state,
                exact ? OutcomeKind.Stochastic : OutcomeKind.Stochastic);
        }).ToImmutableArray();
    }

    private static ImmutableArray<EffectSpec> ExpandRandomEffects(
        ImmutableArray<EffectSpec> effects,
        int playCount,
        int energySpent)
    {
        var result = ImmutableArray.CreateBuilder<EffectSpec>();
        for (var play = 0; play < Math.Max(1, playCount); play++)
        {
            foreach (var effect in effects)
            {
                var count = effect.RepeatByEnergySpent
                    ? Math.Max(0, energySpent + effect.XBonus)
                    : Math.Max(1, effect.Repeat);
                for (var index = 0; index < count; index++)
                    result.Add(effect with { RepeatByEnergySpent = false, Repeat = 1 });
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<ChanceBranch<MutableCombatState>> RandomOrbOutcomes(
        MutableCombatState original,
        ImmutableArray<EffectSpec> effects,
        string sourceId,
        int maximumExactBranches)
    {
        var types = new[] { "LIGHTNING", "FROST", "DARK", "PLASMA" };
        var count = effects.Sum(static effect => Math.Max(0, (int)effect.Amount));
        if (count == 0)
            return [new ChanceBranch<MutableCombatState>("没有生成随机充能球", 1m, original.Clone(), OutcomeKind.Deterministic)];
        var stream = original.RngStreams.Get(RngSnapshotSet.CombatOrbGeneration);
        if (stream is { IsKnown: true })
        {
            var state = original.Clone();
            var labels = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var selected = stream.NextInt(types.Length, out stream);
                labels.Add(types[selected]);
                ChannelOrbs(state, new EffectSpec(EffectKind.ChannelOrbs, 1, types[selected]), 0, sourceId);
            }
            state.RngStreams = state.RngStreams.With(stream);
            return [new ChanceBranch<MutableCombatState>(
                $"随机充能球：{string.Join("、", labels)}",
                1m,
                state,
                OutcomeKind.Deterministic)];
        }

        var total = 1;
        for (var index = 0; index < count && total <= maximumExactBranches; index++) total *= types.Length;
        var exact = total <= maximumExactBranches;
        var sequences = exact
            ? EnumerateOrbSequences(types, count)
            : EnumerateOrbSequences(types, count).Take(maximumExactBranches);
        var sequenceArray = sequences.ToArray();
        var branches = new List<ChanceBranch<MutableCombatState>>(sequenceArray.Length);
        foreach (var sequence in sequenceArray)
        {
            var state = original.Clone();
            foreach (var type in sequence)
                ChannelOrbs(state, new EffectSpec(EffectKind.ChannelOrbs, 1, type), 0, sourceId);
            if (!exact)
            {
                var message = $"随机充能球序列共有 {total} 种，保留前 {sequenceArray.Length} 个确定序列作为估计。";
                state.Risks.Add(new RiskEvent(PredictionRiskReason.ChanceBranchSampled, PredictionRiskSeverity.Estimated, message, sourceId));
                state.Restrictions.Add(new RestrictionReason("sampled_random_orbs", message, sourceId));
            }
            branches.Add(new ChanceBranch<MutableCombatState>(
                $"随机充能球：{string.Join("、", sequence)}",
                1m / sequenceArray.Length,
                state,
                OutcomeKind.Stochastic));
        }
        return branches.ToImmutableArray();
    }

    private static IEnumerable<ImmutableArray<string>> EnumerateOrbSequences(string[] types, int count)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }
        foreach (var prefix in EnumerateOrbSequences(types, count - 1))
        foreach (var type in types)
            yield return prefix.Add(type);
    }

    private static bool NeedsUnknownShuffle(MutableCombatState state, int drawCount) =>
        drawCount > state.DrawPile.Count &&
        state.DiscardPile.Count > 0 &&
        state.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } &&
        state.RngState == 0;

    private static bool NeedsUnknownTurnStartShuffle(MutableCombatState state)
    {
        if (state.DiscardPile.Count == 0 ||
            state.RngStreams.Get(RngSnapshotSet.Shuffle) is { IsKnown: true } ||
            state.RngState != 0)
            return false;

        var drawDemand = state.Player.CardsPerTurn +
                         GetStatusAmount(state.Player.Statuses, "HAND_DRAW_DELTA") +
                         GetStatusAmount(state.Player.Statuses, "SCHEDULED_DRAW") +
                         GetStatusAmount(state.Player.Statuses, "TURN_START_DRAW");
        return state.DrawPile.Count < Math.Max(0, drawDemand) ||
               state.DrawPile.Count == 0 && GetStatusAmount(state.Player.Statuses, "TURN_START_AUTOPLAY_TOP") > 0;
    }

    private ImmutableArray<ChanceBranch<MutableCombatState>> OrderedDrawShuffleOutcomes(
        MutableCombatState original,
        CardState card,
        string? targetId,
        ChoiceSpec? choice,
        int drawCount,
        int maximumExactBranches,
        int sampleCount)
    {
        var pool = original.DiscardPile.ToArray();
        var exact = FactorialAtMost(pool.Length, maximumExactBranches) > 0;
        var permutations = exact
            ? EnumeratePermutations(pool).Take(maximumExactBranches).ToArray()
            : SamplePermutations(pool, Math.Max(1, sampleCount), original.ExactKey()).ToArray();
        if (permutations.Length == 0)
            return [new ChanceBranch<MutableCombatState>(
                "无可抽取卡牌", 1m, PlayCard(original, card, targetId, choice), OutcomeKind.Deterministic)];

        var unresolvedDraws = Math.Max(0, drawCount - original.DrawPile.Count);
        var probability = 1m / permutations.Length;
        return permutations.Select(permutation =>
        {
            var prepared = original.Clone();
            prepared.DrawPile.AddRange(permutation);
            prepared.DiscardPile.Clear();
            var state = PlayCard(prepared, card, targetId, choice);
            if (!exact)
            {
                var message = $"未知洗牌共有超过 {maximumExactBranches} 个排列，使用 {permutations.Length} 个确定性样本估计。";
                state.Risks.Add(new RiskEvent(
                    PredictionRiskReason.ChanceBranchSampled,
                    PredictionRiskSeverity.Estimated,
                    message,
                    card.ModelId));
                state.Restrictions.Add(new RestrictionReason("sampled_shuffle", message, card.ModelId));
            }
            return new ChanceBranch<MutableCombatState>(
                ShuffleLabel(permutation, unresolvedDraws),
                probability,
                state,
                OutcomeKind.Stochastic);
        }).ToImmutableArray();
    }

    private static bool HasOrderedDrawEffects(ImmutableArray<EffectSpec> effects) => effects.Any(static effect =>
        effect.Kind is EffectKind.DrawToHandSize or EffectKind.DrawUntilNonAttack ||
        effect.Condition == "LAST_DRAWN_CARD_SKILL");

    private static int DrawCount(
        MutableCombatState state,
        ImmutableArray<EffectSpec> effects,
        bool cardLeavesHand,
        int playCount)
    {
        var handCount = Math.Max(0, state.Hand.Count - (cardLeavesHand ? 1 : 0));
        var drawOffset = 0;
        var total = 0;
        foreach (var effect in effects)
        {
            int count;
            switch (effect.Kind)
            {
                case EffectKind.Draw:
                    count = Math.Max(0, (int)effect.Amount) * Math.Max(1, playCount);
                    break;
                case EffectKind.DrawToHandSize:
                    count = Math.Max(0, (int)effect.Amount - handCount);
                    break;
                case EffectKind.DrawUntilNonAttack:
                    var capacity = Math.Max(0, 10 - handCount);
                    var known = state.DrawPile.Skip(drawOffset).Take(capacity).ToArray();
                    var stop = Array.FindIndex(known, static card => !IsCardType(card, "Attack", "攻击"));
                    count = stop >= 0 ? stop + 1 : capacity;
                    break;
                default:
                    continue;
            }
            total += count;
            drawOffset += count;
            handCount = Math.Min(10, handCount + count);
        }
        return total;
    }

    private static int DrawCount(ImmutableArray<EffectSpec> effects) => effects
        .Where(static effect => effect.Kind == EffectKind.Draw && effect.UnsupportedReason is null)
        .Sum(static effect => Math.Max(0, (int)effect.Amount));

    private static ImmutableArray<EffectSpec> WithoutDraw(ImmutableArray<EffectSpec> effects) =>
        effects.Where(static effect => effect.Kind != EffectKind.Draw).ToImmutableArray();

    private static ImmutableArray<EffectSpec> WithoutRandomTargetDamage(ImmutableArray<EffectSpec> effects) =>
        effects.Where(static effect => effect.Kind != EffectKind.RandomEnemyDamage).ToImmutableArray();

    private static ImmutableArray<EffectSpec> WithoutRandomExhaust(ImmutableArray<EffectSpec> effects) =>
        effects.Where(static effect => effect.Kind != EffectKind.RandomExhaustCards).ToImmutableArray();

    private static ImmutableArray<ChanceBranch<MutableCombatState>> RandomExhaustOutcomes(
        MutableCombatState original,
        int count,
        string sourceId,
        int maximumExactBranches,
        int sampleCount)
    {
        count = Math.Min(Math.Max(0, count), original.Hand.Count);
        if (count == 0)
            return [new ChanceBranch<MutableCombatState>("没有可随机消耗的手牌", 1m, original.Clone(), OutcomeKind.Deterministic)];

        var knownStream = original.RngStreams.Get(RngSnapshotSet.CombatCardSelection);
        if (knownStream is { IsKnown: true })
        {
            var state = original.Clone();
            var labels = new List<string>();
            for (var index = 0; index < count && state.Hand.Count > 0; index++)
            {
                var selected = knownStream.NextInt(state.Hand.Count, out knownStream);
                var card = state.Hand[selected];
                state.Hand.RemoveAt(selected);
                state.ExhaustPile.Add(card);
                ApplyExhaustTriggers(state, [card]);
                labels.Add(card.Name);
            }
            state.RngStreams = state.RngStreams.With(knownStream);
            return [new ChanceBranch<MutableCombatState>(
                $"随机消耗：{string.Join("、", labels)}",
                1m,
                state,
                OutcomeKind.Deterministic)];
        }

        var combinations = EnumerateCombinations(original.Hand.Count, count, maximumExactBranches + 1).ToArray();
        if (combinations.Length <= maximumExactBranches)
        {
            var probability = 1m / combinations.Length;
            return combinations.Select(indices =>
            {
                var state = original.Clone();
                var selected = indices.Select(index => state.Hand[index]).ToArray();
                foreach (var index in indices.OrderDescending()) state.Hand.RemoveAt(index);
                state.ExhaustPile.AddRange(selected);
                ApplyExhaustTriggers(state, selected);
                return new ChanceBranch<MutableCombatState>(
                    $"随机消耗：{string.Join("、", selected.Select(static card => card.Name))}",
                    probability,
                    state);
            }).ToImmutableArray();
        }

        var seed = StableSeed(original.ExactKey(), sourceId);
        var samples = new List<ChanceBranch<MutableCombatState>>();
        for (var sample = 0; sample < Math.Max(1, sampleCount); sample++)
        {
            var state = original.Clone();
            var selected = new List<CardState>();
            for (var index = 0; index < count && state.Hand.Count > 0; index++)
            {
                seed = NextSample(seed);
                var position = (int)(seed % (ulong)state.Hand.Count);
                selected.Add(state.Hand[position]);
                state.Hand.RemoveAt(position);
            }
            state.ExhaustPile.AddRange(selected);
            ApplyExhaustTriggers(state, selected);
            var message = $"随机消耗组合超过 {maximumExactBranches}，使用 {sampleCount} 个确定性样本。";
            state.Risks.Add(new RiskEvent(PredictionRiskReason.ChanceBranchSampled, PredictionRiskSeverity.Estimated, message, sourceId));
            state.Restrictions.Add(new RestrictionReason("sampled_random_exhaust", message, sourceId));
            samples.Add(new ChanceBranch<MutableCombatState>(
                $"随机消耗样本：{string.Join("、", selected.Select(static card => card.Name))}",
                1m / Math.Max(1, sampleCount),
                state));
        }
        return samples.ToImmutableArray();
    }

    private static IEnumerable<int[]> EnumerateCombinations(int size, int count, int limit)
    {
        var selected = new int[count];
        var yielded = 0;
        return Enumerate(0, 0);

        IEnumerable<int[]> Enumerate(int depth, int start)
        {
            if (yielded >= limit) yield break;
            if (depth == count)
            {
                yielded++;
                yield return selected.ToArray();
                yield break;
            }
            for (var index = start; index <= size - (count - depth); index++)
            {
                selected[depth] = index;
                foreach (var combination in Enumerate(depth + 1, index + 1)) yield return combination;
                if (yielded >= limit) yield break;
            }
        }
    }

    private static ImmutableArray<ChanceBranch<MutableCombatState>> RandomEnemyDamageOutcomes(
        MutableCombatState original,
        ImmutableArray<EffectSpec> effects,
        string sourceId,
        int maximumExactBranches,
        int sampleCount)
    {
        var knownStream = original.RngStreams.Get(RngSnapshotSet.CombatTargets);
        if (knownStream is { IsKnown: true })
        {
            var state = original.Clone();
            var labels = new List<string>();
            foreach (var effect in effects)
            {
                var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
                if (alive.Length == 0) break;
                var selected = knownStream.NextInt(alive.Length, out knownStream);
                var enemy = alive[selected];
                DamageEnemy(state, enemy.Id, ModifyPlayerAttackDamage(state.Player, enemy, effect.Amount));
                labels.Add(enemy.Name);
            }
            if (effects.Length > 0)
                ConsumeVigor(state);
            state.RngStreams = state.RngStreams.With(knownStream);
            return [new ChanceBranch<MutableCombatState>(
                $"随机目标：{string.Join("、", labels)}",
                1m,
                state,
                OutcomeKind.Deterministic)];
        }

        var frontier = new List<(MutableCombatState State, decimal Probability, string Label)>
        {
            (original.Clone(), 1m, string.Empty)
        };
        var exact = true;
        foreach (var effect in effects)
        {
            var next = new List<(MutableCombatState State, decimal Probability, string Label)>();
            foreach (var branch in frontier)
            {
                var alive = branch.State.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
                if (alive.Length == 0)
                {
                    next.Add(branch);
                    continue;
                }
                foreach (var enemy in alive)
                {
                    var state = branch.State.Clone();
                    var current = state.Enemies.First(item => item.Id == enemy.Id);
                    DamageEnemy(state, current.Id, ModifyPlayerAttackDamage(state.Player, current, effect.Amount));
                    next.Add((
                        state,
                        branch.Probability / alive.Length,
                        branch.Label.Length == 0 ? enemy.Name : $"{branch.Label}、{enemy.Name}"));
                }
            }
            frontier = next;
            if (frontier.Count > maximumExactBranches)
            {
                exact = false;
                break;
            }
        }

        if (exact)
            return frontier.Select(branch =>
            {
                if (effects.Length > 0)
                    ConsumeVigor(branch.State);
                return new ChanceBranch<MutableCombatState>(
                    $"随机目标：{branch.Label}",
                    branch.Probability,
                    branch.State);
            }).ToImmutableArray();

        var sampled = new List<ChanceBranch<MutableCombatState>>();
        var seed = StableSeed(original.ExactKey(), sourceId);
        for (var sample = 0; sample < Math.Max(1, sampleCount); sample++)
        {
            var state = original.Clone();
            var labels = new List<string>();
            foreach (var effect in effects)
            {
                var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
                if (alive.Length == 0) break;
                seed = NextSample(seed);
                var enemy = alive[(int)(seed % (ulong)alive.Length)];
                DamageEnemy(state, enemy.Id, ModifyPlayerAttackDamage(state.Player, enemy, effect.Amount));
                labels.Add(enemy.Name);
            }
            if (effects.Length > 0)
                ConsumeVigor(state);
            var message = $"随机目标组合超过 {maximumExactBranches}，使用 {sampleCount} 个确定性样本。";
            state.Risks.Add(new RiskEvent(PredictionRiskReason.ChanceBranchSampled, PredictionRiskSeverity.Estimated, message, sourceId));
            state.Restrictions.Add(new RestrictionReason("sampled_random_targets", message, sourceId));
            sampled.Add(new ChanceBranch<MutableCombatState>(
                $"随机目标样本：{string.Join("、", labels)}",
                1m / Math.Max(1, sampleCount),
                state));
        }
        return sampled.ToImmutableArray();
    }

    private static ulong StableSeed(string stateKey, string sourceId)
    {
        var seed = 1469598103934665603UL;
        foreach (var character in stateKey + sourceId)
        {
            seed ^= character;
            seed *= 1099511628211UL;
        }
        return seed;
    }

    private static ulong NextSample(ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }

    private static bool HasInterleavedDraw(ImmutableArray<EffectSpec> effects)
    {
        var drawIndex = -1;
        for (var index = 0; index < effects.Length; index++)
        {
            if (effects[index].Kind != EffectKind.Draw) continue;
            drawIndex = index;
            break;
        }
        return drawIndex >= 0 && drawIndex < effects.Length - 1;
    }

    private static MutableCombatState FinishCard(MutableCombatState state, CardState card, CardDestination destination)
    {
        switch (destination)
        {
            case CardDestination.Discard:
                state.DiscardPile.Add(card);
                break;
            case CardDestination.Exhaust:
                state.ExhaustPile.Add(card);
                ApplyExhaustTriggers(state, [card]);
                break;
            case CardDestination.DrawPileTop:
                state.DrawPile.Insert(0, card);
                break;
            case CardDestination.Remove:
                break;
            case CardDestination.Hand:
                state.Hand.Add(card);
                break;
        }
        return state;
    }

    private static int FactorialAtMost(int count, int limit)
    {
        var value = 1;
        for (var factor = 2; factor <= count; factor++)
        {
            if (value > limit / factor) return 0;
            value *= factor;
        }
        return value;
    }

    private static IEnumerable<CardState[]> EnumeratePermutations(CardState[] cards)
    {
        var working = cards.ToArray();
        return Enumerate(0);

        IEnumerable<CardState[]> Enumerate(int index)
        {
            if (index >= working.Length)
            {
                yield return working.ToArray();
                yield break;
            }
            for (var swap = index; swap < working.Length; swap++)
            {
                (working[index], working[swap]) = (working[swap], working[index]);
                foreach (var permutation in Enumerate(index + 1)) yield return permutation;
                (working[index], working[swap]) = (working[swap], working[index]);
            }
        }
    }

    private static IEnumerable<CardState[]> SamplePermutations(CardState[] cards, int count, string seedText)
    {
        var seed = 1469598103934665603UL;
        foreach (var character in seedText)
        {
            seed ^= character;
            seed *= 1099511628211UL;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var sample = 0; sample < count * 4 && seen.Count < count; sample++)
        {
            var permutation = cards.ToArray();
            if (permutation.Length > 1)
            {
                var first = sample % permutation.Length;
                (permutation[0], permutation[first]) = (permutation[first], permutation[0]);
            }
            // Stratify the first drawn card, then randomize the tail. This keeps
            // small sample budgets from accidentally missing a whole outcome.
            for (var index = permutation.Length - 1; index > 1; index--)
            {
                seed ^= seed << 13;
                seed ^= seed >> 7;
                seed ^= seed << 17;
                var swap = 1 + (int)(seed % (ulong)index);
                (permutation[index], permutation[swap]) = (permutation[swap], permutation[index]);
            }
            if (seen.Add(string.Join('|', permutation.Select(static card => card.InstanceId))))
                yield return permutation;
        }
    }

    private static string ShuffleLabel(CardState[] permutation, int drawn) =>
        $"洗牌后先抽 {string.Join("、", permutation.Take(drawn).Select(static item => $"{item.Name}[{item.InstanceId}]"))}" +
        (permutation.Length <= drawn
            ? string.Empty
            : $"；余序 {string.Join("、", permutation.Skip(drawn).Select(static item => item.InstanceId))}");
}
