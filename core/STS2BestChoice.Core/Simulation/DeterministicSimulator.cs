using System.Collections.Immutable;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Scoring;

namespace STS2BestChoice.Core.Simulation;

internal sealed partial class DeterministicSimulator
{
    private static readonly HashSet<string> RetainBlockStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "BARRICADE", "BLUR", "CALIPERS"
    };

    private static readonly CardState SweepingGazeTemplate = new(
        "template:sweeping_gaze",
        "SWEEPING_GAZE",
        "扫荡凝视",
        0,
        TargetKind.None,
        [
            new EffectSpec(EffectKind.RandomEnemyDamage, 10, RandomSource: RngSnapshotSet.CombatTargets)
        ],
        Destination: CardDestination.Exhaust,
        CardType: "Attack",
        ExhaustAtTurnEnd: true);

    public MutableCombatState PlayCard(
        MutableCombatState original,
        CardState card,
        string? targetId,
        ChoiceSpec? choice,
        bool finalizeDestination = true,
        int? forcedPlayCount = null,
        bool isAutoPlay = false)
    {
        var state = original.Clone();
        var handIndex = state.Hand.FindIndex(item => item.InstanceId == card.InstanceId);
        if (handIndex < 0)
            return AddUncalculable(state, "uncalculable_card_missing", "Card is no longer in hand.", card.ModelId);
        var energyCost = EnergyCostToPlay(state, card);
        if (!IsCardPlayableNow(state, card) || (!isAutoPlay && energyCost > state.Player.Energy))
            return AddUncalculable(state, "uncalculable_card_unplayable", card.RestrictionReason ?? "Card is not playable.", card.ModelId);
        if (card.RestrictionReason is { Length: > 0 } restriction)
            AddEstimated(state, PredictionRiskReason.MethodMirrorIncomplete, "unsupported_card", restriction, card.ModelId);
        if (card.SafeChoices.Length > 0 && choice is null)
            return AddUncalculable(state, "uncalculable_choice_missing", "Card requires a deterministic sub-choice.", card.ModelId);
        if (choice?.RestrictionReason is { Length: > 0 } choiceRestriction)
            AddEstimated(state, PredictionRiskReason.UnresolvedPlayerChoice, "unsupported_choice", choiceRestriction, card.ModelId);

        var energyValue = card.CostsX ? state.Player.Energy : energyCost;
        var energySpent = isAutoPlay ? 0 : energyValue;
        state.Hand.RemoveAt(handIndex);
        var destination = finalizeDestination
            ? EffectiveCardDestination(state, card, energyValue, consumeModifiers: true)
            : CardDestination.Remove;
        var playCount = forcedPlayCount ?? EffectivePlayCount(state, card, consumeModifiers: true);
        state.Player = state.Player with { Energy = state.Player.Energy - energySpent };
        ApplyEnergySpentTriggers(state, energySpent);
        ConsumeNextFreeStatus(state, card);
        var playedCard = card;
        if (playCount > 1 && choice is not null)
            AddEstimated(state, PredictionRiskReason.UnresolvedPlayerChoice, "replayed_choice_reused",
                "牌被重复打出时，当前镜像在每次结算中复用同一选择。", card.ModelId);
        for (var playIndex = 0; playIndex < playCount; playIndex++)
        {
            var hpLossContext = new CardHpLossTriggerContext();
            var drawnByThisPlay = new List<CardState>();
            var damageDealtBeforeCard = state.DamageDealt;
            var monologueStrength = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_PLAYED_TEMP_STRENGTH");
            var serpentForm = state.Player.Statuses.TryGetValue("TRIGGER_CARD_PLAYED_RANDOM_DAMAGE", out var serpentStatus)
                ? serpentStatus
                : null;
            var panacheCardsLeft = state.Player.Statuses.ContainsKey("TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE")
                ? GetStatusAmount(state.Player.Statuses, "PANACHE_CARDS_LEFT")
                : 0;
            var haunt = card.ModelId.Equals("SOUL", StringComparison.OrdinalIgnoreCase) &&
                        state.Player.Statuses.TryGetValue("TRIGGER_SOUL_PLAYED_RANDOM_HP_LOSS", out var hauntStatus)
                ? hauntStatus
                : null;
            var oblivionDoom = state.Enemies
                .Where(static enemy => enemy.IsAlive)
                .Select(enemy => (
                    EnemyId: enemy.Id,
                    Amount: GetStatusAmount(enemy.Statuses, "TRIGGER_CARD_PLAYED_DOOM")))
                .Where(static trigger => trigger.Amount > 0)
                .ToArray();
            var strangleHpLoss = state.Enemies
                .Where(static enemy => enemy.IsAlive)
                .Select(enemy => (
                    EnemyId: enemy.Id,
                    Amount: GetStatusAmount(enemy.Statuses, "TRIGGER_CARD_PLAYED_HP_LOSS")))
                .Where(static trigger => trigger.Amount > 0)
                .ToArray();
            var afterimageBlock = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_PLAYED_BLOCK");
            var rageBlock = IsCardType(card, "Attack", "攻击")
                ? GetStatusAmount(state.Player.Statuses, "TRIGGER_ATTACK_PLAYED_BLOCK")
                : 0;
            var powerEnergy = IsCardType(card, "Power", "能力")
                ? GetStatusAmount(state.Player.Statuses, "TRIGGER_POWER_PLAYED_ENERGY")
                : 0;
            var powerLightning = IsCardType(card, "Power", "能力")
                ? GetStatusAmount(state.Player.Statuses, "TRIGGER_POWER_PLAYED_LIGHTNING")
                : 0;
            var etherealBlock = card.ExhaustAtTurnEnd
                ? GetStatusAmount(state.Player.Statuses, "TRIGGER_ETHEREAL_PLAYED_BLOCK")
                : 0;
            var energyThresholdBlock = GetEnergyThresholdBlock(state, energySpent);
            if (etherealBlock > 0)
                GainPlayerBlock(state, etherealBlock);
            if (energyThresholdBlock > 0)
                GainPlayerBlock(state, energyThresholdBlock);

            ApplyJugglingBeforeCardPlayed(state, card);

            var penNibIndex = state.Relics.FindIndex(static r => r.Id.Equals("PEN_NIB", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
            var penNibWasActive = false;
            if (penNibIndex >= 0 && IsCardType(card, "Attack", "攻击"))
            {
                var penNib = state.Relics[penNibIndex];
                var cur = penNib.Counter ?? 0;
                if (cur >= 9)
                {
                    penNibWasActive = true;
                    state.Relics[penNibIndex] = penNib with { Counter = 0 };
                    state.Player = state.Player with
                    {
                        Statuses = state.Player.Statuses.SetItem("PEN_NIB_ACTIVE", new StatusState("PEN_NIB_ACTIVE", 1, 1))
                    };
                }
                else
                {
                    state.Relics[penNibIndex] = penNib with { Counter = cur + 1 };
                }
            }

            ApplyEffects(state, card.Effects, card.Target, targetId, card.ModelId,
                hasExplicitCardChoice: choice is not null,
                energySpent: energyValue,
                cardDamageBonus: card.CombatDamageBonus,
                cardBlockBonus: card.CombatBlockBonus,
                damageDealtBeforeEffects: damageDealtBeforeCard,
                poweredAttack: IsCardType(card, "Attack", "攻击"),
                hpLossContext: hpLossContext,
                drawnCards: drawnByThisPlay,
                firstAttackLethalityPercent: IsCardType(card, "Attack", "攻击") &&
                    state.AttacksPlayedBeforeTurn + state.AttacksPlayedSinceSnapshot == 0
                    ? GetStatusAmount(state.Player.Statuses, "LETHALITY")
                    : 0m,
                zeroCostAttackBonus: IsCardType(card, "Attack", "攻击") && !card.CostsX && energyCost == 0
                    ? GetStatusAmount(state.Player.Statuses, "ONE_FOR_ALL")
                    : 0m);
            if (penNibWasActive)
            {
                state.Player = state.Player with
                {
                    Statuses = state.Player.Statuses.Remove("PEN_NIB_ACTIVE")
                };
            }
            if (state.Player.Hp > 0m && drawnByThisPlay.Count > 0)
                ApplyHellraiserDrawTriggers(state, drawnByThisPlay);
            foreach (var growthEffect in card.Effects.Where(static effect =>
                         effect.Kind == EffectKind.RandomExhaustAttackAndGrow))
                playedCard = playedCard with
                {
                    CombatDamageBonus = playedCard.CombatDamageBonus +
                        ExhaustRandomAttackAndGetDamage(state, growthEffect, card.ModelId)
                };
            if (IsCardType(card, "Attack", "攻击"))
            {
                state.AttacksPlayedSinceSnapshot++;
                ApplyRelicAttackTriggers(state);
            }
            else if (IsCardType(card, "Skill", "技能"))
                state.SkillsPlayedSinceSnapshot++;
            if (IsShiv(card))
                state.ShivsPlayedSinceSnapshot++;
            state.CardPlaysFinishedSinceSnapshot++;
            if (card.ExhaustAtTurnEnd)
                state.EtherealCardsPlayedSinceSnapshot++;

            foreach (var costEffect in card.Effects.Where(static effect => effect.Kind == EffectKind.ModifyPlayedCardCost && effect.StatusId is null))
                playedCard = playedCard with { EnergyCost = Math.Max(0, playedCard.EnergyCost + (int)costEffect.Amount) };
            foreach (var costEffect in card.Effects.Where(static effect =>
                         effect.Kind == EffectKind.ModifyPlayedCardCost && effect.StatusId == "SELF_SET_ZERO"))
                playedCard = playedCard with { EnergyCost = 0 };
            if (playedCard.TemporaryEnergyCostBeforeCap is { } originalCost)
                playedCard = playedCard with
                {
                    EnergyCost = originalCost,
                    CostsX = playedCard.TemporaryCostsXBeforeOverride ?? playedCard.CostsX,
                    TemporaryEnergyCostBeforeCap = null,
                    TemporaryCostsXBeforeOverride = null
                };
            foreach (var damageEffect in card.Effects.Where(static effect =>
                         effect.Kind == EffectKind.ModifyPlayedCardDamage && effect.StatusId is "SELF" or "MODEL_ALL"))
                playedCard = playedCard with { CombatDamageBonus = playedCard.CombatDamageBonus + damageEffect.Amount };
            foreach (var blockEffect in card.Effects.Where(static effect =>
                         effect.Kind == EffectKind.ModifyPlayedCardBlock && effect.StatusId == "SELF"))
                playedCard = playedCard with { CombatBlockBonus = playedCard.CombatBlockBonus + blockEffect.Amount };
            CopyPlayedCard(state, playedCard);
            if (choice is not null)
            {
                ApplyEffects(state, choice.Effects, card.Target, targetId, card.ModelId,
                    poweredAttack: IsCardType(card, "Attack", "攻击"),
                    hpLossContext: hpLossContext,
                    drawnCards: drawnByThisPlay);
                ApplyCardChoiceMovement(state, choice, card.Effects);
            }
            if (card.Effects.Any(static effect => effect.Kind == EffectKind.CompanionDamage))
            {
                state.Player = state.Player with
                {
                    Statuses = state.Player.Statuses.SetItem(
                        "OSTY_ATTACKS_THIS_TURN",
                        new StatusState(
                            "OSTY_ATTACKS_THIS_TURN",
                            GetStatusAmount(state.Player.Statuses, "OSTY_ATTACKS_THIS_TURN") + 1,
                            Duration: 1))
                };
            }
            FinalizePendingCompanionKill(state);
            if (state.Player.Hp > 0m)
            {
                ApplyCardPlayedTriggers(
                    state,
                    afterimageBlock,
                    monologueStrength,
                    oblivionDoom,
                    rageBlock,
                    serpentForm,
                    panacheCardsLeft,
                    haunt,
                    powerLightning,
                    strangleHpLoss,
                    powerEnergy,
                    attackPlayed: IsCardType(card, "Attack", "攻击"),
                    cardModelId: card.ModelId);
                if (IsCardType(card, "Skill", "技能"))
                    ApplyReturnToHandListeners(state);
                if (hpLossContext.PendingRuptureStrength > 0)
                    GainPlayerStrength(state, hpLossContext.PendingRuptureStrength);
            }
              if (playedCard.Effects.Any(static effect =>
                      effect.Kind == EffectKind.ModifyPlayedCardCost &&
                      effect.StatusId is "SELF_BY_STATUS_GENERATED" or "SELF_BY_STATUS_GENERATED_UNTIL_PLAYED_ZERO"))
                playedCard = playedCard with { EnergyCost = CanonicalEnergyCost(playedCard) };
        }
        state.CardsPlayedSinceSnapshot++;

        switch (destination)
        {
            case CardDestination.Discard:
                state.DiscardPile.Add(playedCard);
                break;
            case CardDestination.Exhaust:
                state.ExhaustPile.Add(playedCard);
                ApplyExhaustTriggers(state, [playedCard]);
                break;
            case CardDestination.Remove:
                break;
            case CardDestination.DrawPileTop:
                state.DrawPile.Insert(0, playedCard);
                break;
            case CardDestination.Hand:
                state.Hand.Add(playedCard);
                break;
        }
        if (card.Effects.Any(static effect => effect.Kind == EffectKind.DelayedReturnSelfToHand))
        {
            if (!state.PendingTurnStartReturnCardInstanceIds.Contains(playedCard.InstanceId, StringComparer.Ordinal))
                state.PendingTurnStartReturnCardInstanceIds.Add(playedCard.InstanceId);
        }
        return state;
    }

    internal static int EnergyCostToPlay(MutableCombatState state, CardState card)
    {
        if (card.CostsX) return Math.Max(0, state.Player.Energy);
        if (MatchingNextFreeStatus(state, card) is not null) return 0;
        if (card.Effects.Any(effect => effect.Kind == EffectKind.ModifyPlayedCardCost &&
                                       effect.StatusId == "OSTY_ATTACKED_THIS_TURN_ZERO_COST" &&
                                       ConditionMatches(state, effect, null, state.EnemiesKilled, null, state.Hand.Count == 0)))
            return 0;
        if (IsCardType(card, "Skill", "技能") && GetStatusAmount(state.Player.Statuses, "SKILLS_COST_ZERO") > 0) return 0;
        var reduction = card.Effects.Where(static effect => effect.Kind == EffectKind.ModifyPlayedCardCost)
            .Sum(effect => effect.StatusId switch
            {
                "BY_ATTACKS_PLAYED" => state.AttacksPlayedSinceSnapshot * -(int)effect.Amount,
                "BY_SKILLS_PLAYED" => state.SkillsPlayedSinceSnapshot * -(int)effect.Amount,
                "BY_ETHEREAL_PLAYED_SINCE_SNAPSHOT" => state.EtherealCardsPlayedSinceSnapshot * -(int)effect.Amount,
                _ => 0
            });
        if (IsCardType(card, "Power", "能力"))
            reduction += GetStatusAmount(state.Player.Statuses, "POWERS_COST_DELTA");
        var globalDelta = GetStatusAmount(state.Player.Statuses, "ALL_CARD_COST_DELTA");
        return Math.Max(0, card.EnergyCost + globalDelta - reduction);
    }

    internal static bool IsCardPlayableNow(MutableCombatState state, CardState card)
    {
        if (!card.IsPlayable) return false;
        var cardsPlayedThisTurn = state.HistoryBeforeSnapshot.CardsPlayedThisTurn + state.CardPlaysFinishedSinceSnapshot;
        var persistentLimit = GetStatusAmount(state.Player.Statuses, "CARD_PLAY_LIMIT");
        if (persistentLimit > 0 && cardsPlayedThisTurn >= persistentLimit)
            return false;
        var handLimit = state.Hand
            .SelectMany(static item => item.Effects)
            .Where(static effect => effect is
            {
                Kind: EffectKind.PlayRestriction,
                StatusId: "GLOBAL_CARD_PLAY_LIMIT_WHILE_IN_HAND"
            })
            .Select(static effect => (int)effect.Amount)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        if (cardsPlayedThisTurn >= handLimit)
            return false;
        foreach (var restriction in card.Effects.Where(static effect => effect.Kind == EffectKind.PlayRestriction))
        {
            if (restriction.StatusId == "HAND_ALL_ATTACKS" &&
                !state.Hand.All(static item => IsCardType(item, "Attack", "攻击")))
                return false;
            if (restriction.StatusId == "DRAW_PILE_EMPTY" && state.DrawPile.Count > 0)
                return false;
        }
        return true;
    }

    private static void ConsumeNextFreeStatus(MutableCombatState state, CardState card)
    {
        if (MatchingNextFreeStatus(state, card) is not { } statusId) return;
        state.Player = state.Player with { Statuses = state.Player.Statuses.Remove(statusId) };
    }

    private static string? MatchingNextFreeStatus(MutableCombatState state, CardState card)
    {
        var typeStatus = card.CardType?.ToUpperInvariant() switch
        {
            "ATTACK" or "攻击" => "NEXT_FREE_ATTACK",
            "SKILL" or "技能" => "NEXT_FREE_SKILL",
            "POWER" or "能力" => "NEXT_FREE_POWER",
            _ => null
        };
        if (typeStatus is not null && GetStatusAmount(state.Player.Statuses, typeStatus) > 0) return typeStatus;
        return card.ExhaustAtTurnEnd && GetStatusAmount(state.Player.Statuses, "NEXT_FREE_ETHEREAL") > 0
            ? "NEXT_FREE_ETHEREAL"
            : null;
    }

    private static bool IsCardType(CardState card, string english, string chinese) =>
        string.Equals(card.CardType, english, StringComparison.OrdinalIgnoreCase) || card.CardType == chinese;

    internal static int EffectivePlayCount(MutableCombatState state, CardState card, bool consumeModifiers)
    {
        var playCount = Math.Max(1, card.ReplayCount + 1);
        var echoLimit = GetStatusAmount(state.Player.Statuses, "ECHO_FORM_REPLAY_FIRST_CARDS");
        var echoUsed = GetStatusAmount(state.Player.Statuses, "ECHO_FORM_ROOT_PLAYS_BEFORE_SNAPSHOT") +
                       state.CardsPlayedSinceSnapshot;
        if (echoLimit > echoUsed)
            playCount++;

        var statusId = IsCardType(card, "Attack", "攻击") && GetStatusAmount(state.Player.Statuses, "NEXT_ATTACK_REPLAY") > 0
            ? "NEXT_ATTACK_REPLAY"
            : IsCardType(card, "Skill", "技能") && GetStatusAmount(state.Player.Statuses, "NEXT_SKILL_REPLAY") > 0
                ? "NEXT_SKILL_REPLAY"
                : IsCardType(card, "Power", "能力") && GetStatusAmount(state.Player.Statuses, "NEXT_POWER_REPLAY") > 0
                    ? "NEXT_POWER_REPLAY"
                    : GetStatusAmount(state.Player.Statuses, "NEXT_CARD_REPLAY") > 0 ? "NEXT_CARD_REPLAY" : null;
        if (statusId is null) return playCount;
        playCount++;
        if (consumeModifiers)
            DecrementStatus(state, statusId);
        return playCount;
    }

    internal static (ImmutableArray<CardState> Candidates, bool Exact) PreviewHandChoiceCandidates(
        MutableCombatState original,
        CardState card)
    {
        var preview = original.Clone();
        var handIndex = preview.Hand.FindIndex(candidate => candidate.InstanceId == card.InstanceId);
        if (handIndex >= 0) preview.Hand.RemoveAt(handIndex);
        var drawCount = DrawCount(card.Effects);
        if (NeedsUnknownShuffle(preview, drawCount))
            return (preview.Hand.ToImmutableArray(), false);
        Draw(preview, drawCount);
        return (preview.Hand.ToImmutableArray(), true);
    }

    private static void DecrementStatus(MutableCombatState state, string statusId)
    {
        if (!state.Player.Statuses.TryGetValue(statusId, out var status)) return;
        state.Player = state.Player with
        {
            Statuses = status.Amount <= 1
                ? state.Player.Statuses.Remove(statusId)
                : state.Player.Statuses.SetItem(statusId, status with { Amount = status.Amount - 1 })
        };
    }

    private static CardDestination EffectiveCardDestination(
        MutableCombatState state,
        CardState card,
        int energySpent,
        bool consumeModifiers)
    {
        if (IsCardType(card, "Skill", "技能") &&
            GetStatusAmount(state.Player.Statuses, "SKILLS_EXHAUST_ON_PLAY") > 0)
            return CardDestination.Exhaust;

        var destination = card.IsDupe ? CardDestination.Remove : card.Destination;
        var feralLimit = GetStatusAmount(state.Player.Statuses, "FERAL_ZERO_COST_ATTACK_RETURN");
        var feralUsed = GetStatusAmount(state.Player.Statuses, "FERAL_ZERO_COST_ATTACK_RETURN_USED");
        if (feralLimit > feralUsed &&
            IsCardType(card, "Attack", "攻击") &&
            energySpent == 0 &&
            !card.IsDupe &&
            destination != CardDestination.Hand)
        {
            if (consumeModifiers)
            {
                var used = new StatusState("FERAL_ZERO_COST_ATTACK_RETURN_USED", 1);
                state.Player = state.Player with { Statuses = AddStatus(state.Player.Statuses, used) };
            }
            return CardDestination.Hand;
        }

        var nostalgiaLimit = GetStatusAmount(state.Player.Statuses, "NOSTALGIA_ATTACK_SKILL_TOPDECK");
        var priorAttackOrSkillPlays = GetStatusAmount(state.Player.Statuses, "NOSTALGIA_ATTACK_SKILL_PLAYS_BEFORE_SNAPSHOT") +
                                      state.AttacksPlayedSinceSnapshot + state.SkillsPlayedSinceSnapshot;
        if (destination == CardDestination.Discard &&
            nostalgiaLimit > priorAttackOrSkillPlays &&
            (IsCardType(card, "Attack", "攻击") || IsCardType(card, "Skill", "技能")))
            return CardDestination.DrawPileTop;

        if (destination != CardDestination.Discard ||
            GetStatusAmount(state.Player.Statuses, "NEXT_CARD_TO_DRAW_TOP") <= 0)
            return destination;
        if (consumeModifiers)
            state.Player = state.Player with { Statuses = state.Player.Statuses.Remove("NEXT_CARD_TO_DRAW_TOP") };
        return CardDestination.DrawPileTop;
    }

    private static int GetEnergyThresholdBlock(MutableCombatState state, int energySpent)
    {
        const string prefix = "TRIGGER_ENERGY_AT_LEAST_";
        const string suffix = "_BLOCK";
        var total = 0;
        foreach (var pair in state.Player.Statuses)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                !pair.Key.EndsWith(suffix, StringComparison.Ordinal) ||
                !int.TryParse(pair.Key[prefix.Length..^suffix.Length], out var threshold) ||
                energySpent < threshold)
                continue;
            total += pair.Value.Amount;
        }
        return total;
    }

    private static void CopyPlayedCard(MutableCombatState state, CardState card)
    {
        foreach (var effect in card.Effects.Where(static effect => effect.Kind == EffectKind.CopyPlayedCard))
        {
            var count = Math.Max(0, (int)effect.Amount);
            var existing = state.Hand.Concat(state.DrawPile).Concat(state.DiscardPile).Concat(state.ExhaustPile)
                .Count(candidate => candidate.ModelId.Equals(card.ModelId, StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < count; index++)
            {
                var copy = card with
                {
                    InstanceId = $"copy:{card.ModelId.ToLowerInvariant()}:{existing + index + 1}",
                    EnergyCost = effect.StatusId == "ZERO_COST" ? 0 : card.EnergyCost,
                    CostsX = effect.StatusId == "ZERO_COST" ? false : card.CostsX
                };
                switch (effect.GeneratedDestination)
                {
                    case GeneratedCardDestination.DrawPile:
                        AddGeneratedCard(state, copy, GeneratedCardDestination.DrawPile, card.ModelId);
                        break;
                    case GeneratedCardDestination.DiscardPile:
                        AddGeneratedCard(state, copy, GeneratedCardDestination.DiscardPile, card.ModelId);
                        break;
                    default:
                        AddGeneratedCard(state, copy, GeneratedCardDestination.Hand, card.ModelId);
                        break;
                }
            }
        }
    }

    private static void ApplyJugglingBeforeCardPlayed(MutableCombatState state, CardState card)
    {
        if (!IsCardType(card, "Attack", "攻击") ||
            GetStatusAmount(state.Player.Statuses, "JUGGLING") <= 0)
            return;

        var attacksPlayed = state.HistoryBeforeSnapshot.AttacksPlayedThisTurn +
                            state.AttacksPlayedSinceSnapshot;
        if (attacksPlayed + 1 != 3) return;

        var copy = PrepareGeneratedCard(
            state,
            card,
            $"juggling:{card.InstanceId}:{state.CardsGeneratedSinceSnapshot + 1}");
        AddGeneratedCard(state, copy, GeneratedCardDestination.Hand, card.ModelId);
    }

    public MutableCombatState UsePotion(MutableCombatState original, PotionState potion, string? targetId)
    {
        var state = original.Clone();
        var index = state.Potions.FindIndex(item => item.InstanceId == potion.InstanceId);
        if (index < 0)
            return AddUncalculable(state, "uncalculable_potion_missing", "Potion is no longer available.", potion.ModelId);
        if (!potion.IsUsable)
            return AddUncalculable(state, "uncalculable_potion_unusable", potion.RestrictionReason ?? "Potion is not usable.", potion.ModelId);
        if (potion.RestrictionReason is { Length: > 0 } restriction)
            AddEstimated(state, PredictionRiskReason.MethodMirrorIncomplete, "unsupported_potion", restriction, potion.ModelId);

        state.Potions.RemoveAt(index);
        state.PotionCostSpent += potion.OpportunityCost;
        ApplyEffects(state, potion.Effects, potion.Target, targetId, potion.ModelId,
            canUseVigor: false, cardSource: false);
        return state;
    }

    public MutableCombatState ProjectToNextPlayerTurn(MutableCombatState original)
    {
        var state = original.Clone();
        state.StatusCardsDrawnBeforeTurn = 0;
        state.StatusCardsDrawnSinceSnapshot = 0;
        state.CardsDrawnThisTurn = 0;
        AutoPlayMarkedCardsFromPile(state, state.ExhaustPile, "TURN_END_SELF_IN_EXHAUST");
        if (state.Player.Hp > 0m && state.DrawPile.FirstOrDefault() is { } drawTop &&
            drawTop.Effects.Any(static effect =>
                effect.Kind == EffectKind.AutoPlaySelfFromPile && effect.StatusId == "TURN_END_SELF_DRAW_TOP"))
            AutoPlayExistingCardFromPile(state, state.DrawPile, drawTop.InstanceId, drawTop.ModelId);
        var randomHandAttackCount = GetStatusAmount(state.Player.Statuses, "TURN_END_RANDOM_HAND_ATTACK");
        for (var index = 0; index < randomHandAttackCount && state.Player.Hp > 0m; index++)
            if (!AutoPlayRandomHandAttack(state, "TURN_END_RANDOM_HAND_ATTACK"))
                break;
        var turnEndHand = state.Hand.ToArray();
        var turnEndHandCount = turnEndHand.Length;
        foreach (var card in turnEndHand)
        {
            foreach (var effect in card.SafeTurnEndInHandEffects)
            {
                var amount = effect.Condition == "HAND_COUNT"
                    ? effect.Amount * turnEndHandCount
                    : effect.Amount;
                switch (effect.Kind)
                {
                    case EffectKind.Damage:
                        if (amount > 0m) DamagePlayer(state, amount);
                        break;
                    case EffectKind.LoseHp:
                        ApplyTurnEndHpLoss(state, amount);
                        break;
                    case EffectKind.ApplyStatus:
                        if (amount > 0m)
                            ApplyStatus(state, effect with { Amount = amount }, TargetKind.Self, null, card.ModelId, false);
                        break;
                }
                if (state.Player.Hp <= 0m) break;
            }

            if (state.Player.Hp <= 0m) break;
            if (card.HpLossAtTurnEnd > 0m)
                ApplyTurnEndHpLoss(state, card.HpLossAtTurnEnd);
            if (state.Player.Hp <= 0m) break;
        }
        RemoveTemporaryMonologueStrength(state);
        if (state.Player.Statuses.TryGetValue("THE_BOMB", out var bombStatus) && bombStatus.Duration <= 1)
        {
            state.Player = state.Player with { Statuses = state.Player.Statuses.Remove("THE_BOMB") };
            foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                DamageEnemy(state, enemy.Id, ModifyPlayerAttackDamage(state.Player, enemy, bombStatus.Amount, includeVigor: false, poweredAttack: false));
        }
        var platedArmor = GetStatusAmount(state.Player.Statuses, "PLATED_ARMOR");
        if (platedArmor > 0)
            GainPlayerBlock(state, platedArmor);
        var plating = GetStatusAmount(state.Player.Statuses, "PLATING");
        if (plating > 0)
            GainPlayerBlock(state, plating);
        var frostConditionalDamage = GetStatusAmount(state.Player.Statuses, "TURN_END_HAS_FROST_DAMAGE");
        if (frostConditionalDamage > 0 && state.Orbs.Any(static orb => orb.Id == "FROST"))
            foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                DamageEnemy(state, enemy.Id, ModifyPlayerAttackDamage(state.Player, enemy, frostConditionalDamage, includeVigor: false));
        ApplyScheduledStatuses(state);
        TriggerTurnEndOrbPassives(state);
        ApplyRelicTurnEndTriggers(state);
        ResolvePlayerDoom(state);

        // The live engine discards the end-of-turn hand before the enemy side
        // acts, so HP-loss triggers that draw (e.g. Centennial Puzzle) keep the
        // drawn cards in hand instead of discarding them with the old hand.
        static CardState ClearTemporaryCostCap(CardState card) =>
            card.TemporaryEnergyCostBeforeCap is { } originalCost
                ? card with
                {
                    EnergyCost = originalCost,
                    CostsX = card.TemporaryCostsXBeforeOverride ?? card.CostsX,
                    TemporaryEnergyCostBeforeCap = null,
                    TemporaryCostsXBeforeOverride = null
                }
                : card;
        var retained = state.Hand
            .Where(static card => card.RetainAtTurnEnd && !card.ExhaustAtTurnEnd)
            .Select(static card => card.TemporaryRetainAtTurnEnd
                ? card with { RetainAtTurnEnd = false, TemporaryRetainAtTurnEnd = false }
                : card)
            .Select(ClearTemporaryCostCap)
            .ToArray();
        var ethereal = state.Hand.Where(static card => card.ExhaustAtTurnEnd).Select(ClearTemporaryCostCap).ToArray();
        state.DiscardPile.AddRange(state.Hand
            .Where(static card => !card.RetainAtTurnEnd && !card.ExhaustAtTurnEnd)
            .Select(ClearTemporaryCostCap));
        state.ExhaustPile.AddRange(ethereal);
        ApplyExhaustTriggers(state, ethereal);
        state.Hand.Clear();
        state.Hand.AddRange(retained);

        foreach (var enemy in state.Player.Hp > 0m
                     ? state.Enemies.Where(static enemy => enemy.IsAlive).ToArray()
                     : [])
        {
            if (GetStatusAmount(enemy.Statuses, "STUN") > 0) continue;
            foreach (var intent in enemy.Intents)
            {
                if (intent.RestrictionReason is { Length: > 0 } restriction)
                    AddEstimated(state, PredictionRiskReason.MethodNotMirrored, "unsupported_enemy_intent", restriction, enemy.Id);

                if (intent.DamagePerHit > 0m && intent.Hits > 0)
                {
                    for (var hit = 0;
                         hit < intent.Hits && state.Player.Hp > 0m && IsEnemyAlive(state, enemy.Id);
                         hit++)
                    {
                        DamagePlayer(
                            state,
                            ModifyEnemyAttackDamage(enemy, state.Player, intent.DamagePerHit, poweredAttack: true),
                            enemy.Id,
                            poweredAttack: true);
                    }
                }
                if (!IsEnemyAlive(state, enemy.Id)) break;
                ApplyEffects(state, intent.SafeEffects, TargetKind.Self, null, enemy.Id, enemyActs: true);
            }
        }
        if (state.Player.Hp > 0m)
            ResolveEnemyDoom(state);

        var artOfWarBonus = false;
        var artOfWarIndex = state.Relics.FindIndex(static r => r.Id.Equals("ART_OF_WAR", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (artOfWarIndex >= 0 && state.AttacksPlayedSinceSnapshot + state.AttacksPlayedBeforeTurn == 0)
        {
            artOfWarBonus = true;
        }

        // The previous player turn is complete after enemy actions. Start a fresh
        // local Shiv history before turn-start auto-play and the new hand draw.
        state.ShivsPlayedBeforeTurn = 0;
        state.ShivsPlayedSinceSnapshot = 0;
        state.AttacksPlayedBeforeTurn = 0;
        state.AttacksPlayedSinceSnapshot = 0;

        var scheduledEnergy = GetStatusAmount(state.Player.Statuses, "SCHEDULED_ENERGY");
        var scheduledDraw = GetStatusAmount(state.Player.Statuses, "SCHEDULED_DRAW");
        var scheduledSummon = GetStatusAmount(state.Player.Statuses, "SCHEDULED_SUMMON");
        var recurringEnergy = GetStatusAmount(state.Player.Statuses, "TURN_START_ENERGY");
        if (artOfWarBonus)
            recurringEnergy += 1;

        var happyFlowerIndex = state.Relics.FindIndex(static r => r.Id.Equals("HAPPY_FLOWER", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (happyFlowerIndex >= 0)
        {
            var hf = state.Relics[happyFlowerIndex];
            var cur = ((hf.Counter ?? 0) + 1) % 3;
            if (cur == 0)
            {
                recurringEnergy += 1;
            }
            state.Relics[happyFlowerIndex] = hf with { Counter = cur };
        }
        var recurringDraw = GetStatusAmount(state.Player.Statuses, "TURN_START_DRAW");
        var recurringRandomDoom = GetStatusAmount(state.Player.Statuses, "TURN_START_RANDOM_DOOM");
        var scheduledBlock = GetStatusAmount(state.Player.Statuses, "SCHEDULED_BLOCK");
        var distinctOrbBlock = GetStatusAmount(state.Player.Statuses, "TURN_START_DISTINCT_ORB_BLOCK");
        var rightmostPassive = GetStatusAmount(state.Player.Statuses, "TURN_START_RIGHTMOST_ORB_PASSIVE");
        var scheduledLightning = GetStatusAmount(state.Player.Statuses, "TURN_START_CHANNEL_LIGHTNING");
        var recurringSelfDoom = GetStatusAmount(state.Player.Statuses, "TURN_START_SELF_DOOM");
        var recurringStrength = GetStatusAmount(state.Player.Statuses, "TURN_START_STRENGTH");
        var recurringVigor = GetStatusAmount(state.Player.Statuses, "TURN_START_VIGOR");
        var recurringDexterityLoss = GetStatusAmount(state.Player.Statuses, "TURN_START_DEXTERITY_LOSS");
        var recurringFocusLoss = GetStatusAmount(state.Player.Statuses, "TURN_START_FOCUS_LOSS");
        var recurringHpLoss = GetStatusAmount(state.Player.Statuses, "TURN_START_SELF_HP_LOSS");
        var recurringBlock = GetStatusAmount(state.Player.Statuses, "TURN_START_BLOCK");
        var recurringAllEnemyPoison = GetStatusAmount(state.Player.Statuses, "TURN_START_ALL_ENEMY_POISON");
        var toricBlock = state.Player.Statuses.TryGetValue("TORIC_TOUGHNESS", out var toricStatus) && toricStatus.Duration != 0
            ? toricStatus.Amount
            : 0;
        var exhaustDrawTop = GetStatusAmount(state.Player.Statuses, "TURN_START_EXHAUST_DRAW_TOP");
        var autoPlayDrawTop = GetStatusAmount(state.Player.Statuses, "TURN_START_AUTOPLAY_TOP");
        var expiringTemporaryFocus = state.Player.Statuses.TryGetValue("TEMP_FOCUS", out var temporaryFocus) &&
                                     temporaryFocus.Duration is >= 0 and <= 1
            ? temporaryFocus.Amount
            : 0;
        state.Player.Statuses.TryGetValue("TURN_START_GENERATE_SHIV", out var recurringShiv);

        var retainBlock = state.Player.Statuses.Keys.Any(RetainBlockStatuses.Contains);
        var playerStatusesBeforeTick = state.Player.Statuses.Keys.ToArray();
        state.Player = state.Player with
        {
            Block = retainBlock ? state.Player.Block : 0m,
            Energy = state.Player.MaxEnergy + scheduledEnergy + recurringEnergy,
            Statuses = TickDurations(state.Player.Statuses).Remove("HELLRAISER_AUTOPLAYS_THIS_TURN")
        };
        // A modeled player status that expired on the tick must also disappear
        // from the structured PowerState rows; unchanged statuses are left alone
        // so persistent trigger statuses never spawn duplicate power entries.
        foreach (var statusId in playerStatusesBeforeTick)
            if (!state.Player.Statuses.ContainsKey(statusId))
                SyncPowerState(state, statusId, "player", null, "TURN_TICK");
        if (scheduledSummon > 0)
            SummonCompanion(state, scheduledSummon);
        state.UnmovableBlockGainsThisTurn = 0;
        if (state.Player.Statuses.TryGetValue("SHADOW_STEP_PENDING", out var shadowStepPending))
        {
            state.Player = state.Player with
            {
                Statuses = state.Player.Statuses
                    .Remove("SHADOW_STEP_PENDING")
                    .SetItem("DOUBLE_DAMAGE", new StatusState(
                        "DOUBLE_DAMAGE",
                        1,
                        Duration: 1,
                        FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DOUBLE_DAMAGE", 1)))
            };
        }
        ResetTurnScopedRocketPunchCosts(state);
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses.Remove("CARD_GENERATED_BLOCK_USED_THIS_TURN")
        };
        if (state.Player.Statuses.TryGetValue("PLATING", out var platingStatus))
        {
            var nextPlating = platingStatus.Amount - 1;
            state.Player = state.Player with
            {
                Statuses = nextPlating <= 0
                    ? state.Player.Statuses.Remove("PLATING")
                    : state.Player.Statuses.SetItem("PLATING", platingStatus with { Amount = nextPlating })
            };
            SyncPowerState(state, "PLATING", "player", "player", "TURN_TICK");
        }

        var incenseIndex = state.Relics.FindIndex(static r => r.Id.Equals("INCENSE_BURNER", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (incenseIndex >= 0)
        {
            var inc = state.Relics[incenseIndex];
            var cur = ((inc.Counter ?? 0) + 1) % 6;
            if (cur == 0)
            {
                ApplyStatus(state, new EffectSpec(EffectKind.ApplyStatus, 1, "INTANGIBLE", Duration: 1), TargetKind.Self, null, "INCENSE_BURNER", false);
            }
            state.Relics[incenseIndex] = inc with { Counter = cur };
        }
        if (expiringTemporaryFocus != 0)
            AdjustOrbFocus(state, -expiringTemporaryFocus);
        if (state.Player.Hp > 0m && recurringFocusLoss > 0)
            ApplyStatus(
                state,
                new EffectSpec(
                    EffectKind.ApplyStatus,
                    -recurringFocusLoss,
                    "FOCUS",
                    FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("FOCUS", -recurringFocusLoss)),
                TargetKind.Self,
                null,
                "TURN_START_FOCUS_LOSS",
                enemyActs: false);
        if (scheduledBlock > 0)
            state.Player = state.Player with { Block = state.Player.Block + scheduledBlock };
        if (state.Player.Hp > 0m && recurringSelfDoom > 0)
        {
            var doom = new StatusState(
                "DOOM", recurringSelfDoom, FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DOOM", recurringSelfDoom));
            state.Player = state.Player with { Statuses = AddStatus(state.Player.Statuses, doom) };
        }
        if (state.Player.Hp > 0m && recurringStrength > 0)
        {
            var strength = new StatusState(
                "STRENGTH", recurringStrength,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("STRENGTH", recurringStrength));
            state.Player = state.Player with { Statuses = AddStatus(state.Player.Statuses, strength) };
            // Keep the structured PowerState list in sync with the granted status
            // (e.g. Demon Form accruing Strength at every turn start).
            SyncPowerState(state, "TURN_START_STRENGTH", "player", "player", "TURN_START_STRENGTH");
            SyncPowerState(state, "STRENGTH", "player", "player", "TURN_START_STRENGTH_APPLIED");
        }
        if (state.Player.Hp > 0m && recurringVigor > 0)
        {
            var vigor = new StatusState(
                "VIGOR", recurringVigor, FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("VIGOR", recurringVigor));
            state.Player = state.Player with { Statuses = AddStatus(state.Player.Statuses, vigor) };
        }
        if (state.Player.Hp > 0m && recurringDexterityLoss > 0)
        {
            var dexterityLoss = new StatusState(
                "DEXTERITY", -recurringDexterityLoss,
                FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DEXTERITY", -recurringDexterityLoss));
            state.Player = state.Player with { Statuses = AddStatus(state.Player.Statuses, dexterityLoss) };
        }
        if (state.Player.Hp > 0m && recurringHpLoss > 0)
        {
            var actualLoss = ResolvePlayerHpLoss(state, recurringHpLoss);
            state.Player = state.Player with { Hp = state.Player.Hp - actualLoss };
            state.HpLostSinceSnapshot += actualLoss;
            if (actualLoss > 0m) ApplyPlayerHpLossTriggers(state);
        }
        if (state.Player.Hp > 0m && recurringBlock > 0)
            GainPlayerBlock(state, recurringBlock);
        if (state.Player.Hp > 0m && toricBlock > 0)
            GainPlayerBlock(state, toricBlock);
        for (var index = 0; index < state.Enemies.Count; index++)
        {
            var enemyStatusesBeforeTick = state.Enemies[index].Statuses.Keys.ToArray();
            state.Enemies[index] = state.Enemies[index] with { Statuses = TickDurations(state.Enemies[index].Statuses) };
            // A modeled enemy status that expired on the tick (e.g. Weak after
            // the enemy turn) must also disappear from the structured PowerState
            // rows; unchanged statuses are left alone.
            foreach (var statusId in enemyStatusesBeforeTick)
                if (!state.Enemies[index].Statuses.ContainsKey(statusId))
                    SyncPowerState(state, statusId, state.Enemies[index].Id, null, "TURN_TICK");
        }
        if (state.Player.Hp > 0m && recurringAllEnemyPoison > 0)
            ApplyStatus(
                state,
                new EffectSpec(
                    EffectKind.ApplyStatus,
                    recurringAllEnemyPoison,
                    "POISON",
                    IsDebuff: true,
                    FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("POISON", recurringAllEnemyPoison)),
                TargetKind.AllEnemies,
                null,
                "TURN_START_ALL_ENEMY_POISON",
                enemyActs: false);
        if (state.Player.Hp > 0m && recurringRandomDoom > 0)
            ApplyRandomEnemyStatus(
                state,
                new EffectSpec(
                    EffectKind.ApplyStatus,
                    recurringRandomDoom,
                    "DOOM",
                    RandomSource: RngSnapshotSet.CombatTargets),
                "TURN_START_RANDOM_DOOM",
                markLocalDoom: true);
        if (distinctOrbBlock > 0)
        {
            var orbTypes = state.Orbs.Select(static orb => orb.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            state.Player = state.Player with { Block = state.Player.Block + distinctOrbBlock * orbTypes };
        }
        if (scheduledLightning > 0)
            ChannelOrbs(state, new EffectSpec(EffectKind.ChannelOrbs, scheduledLightning, "LIGHTNING"), 0, "TURN_START_CHANNEL_LIGHTNING");
        if (rightmostPassive > 0)
            TriggerOrbPassives(state, new EffectSpec(EffectKind.TriggerOrbPassives, rightmostPassive, "RIGHTMOST_ANY"), null, "TURN_START_RIGHTMOST_ORB_PASSIVE");
        TriggerTurnStartOrbPassives(state);
        if (state.Player.Hp > 0m && recurringShiv is { Amount: > 0, GeneratedCard: not null })
            AddGeneratedCards(state, new EffectSpec(
                EffectKind.GenerateCards,
                recurringShiv.Amount,
                GeneratedCard: recurringShiv.GeneratedCard), "TURN_START_GENERATE_SHIV");
        if (state.Player.Hp > 0m && state.Player.Statuses.TryGetValue("SENTRY_MODE", out var sentryMode) && sentryMode.Amount > 0)
        {
            var template = sentryMode.GeneratedCard ?? SweepingGazeTemplate;
            AddGeneratedCards(state, new EffectSpec(
                EffectKind.GenerateCards,
                sentryMode.Amount,
                GeneratedCard: template), "TURN_START_GENERATE_SWEEPING_GAZE");
        }
        if (state.Player.Hp > 0m)
            AutoPlayMarkedCardsFromPile(state, state.ExhaustPile, "TURN_START_SELF_IN_EXHAUST");
        if (state.Player.Hp > 0m && autoPlayDrawTop > 0)
        {
            for (var index = 0; index < autoPlayDrawTop && state.Player.Hp > 0m; index++)
                if (!AutoPlayTopDrawCard(state, forceExhaust: false, "TURN_START_AUTOPLAY_TOP")) break;
        }
        if (state.Player.Hp > 0m && exhaustDrawTop > 0)
        {
            var exhausted = new List<CardState>();
            for (var index = 0; index < exhaustDrawTop && state.DrawPile.Count > 0; index++)
            {
                exhausted.Add(state.DrawPile[0]);
                state.DrawPile.RemoveAt(0);
            }
            if (exhausted.Count > 0)
            {
                state.ExhaustPile.AddRange(exhausted);
                ApplyExhaustTriggers(state, exhausted);
            }
        }
        if (state.Player.Hp > 0m && state.PendingTurnStartReturnCardInstanceIds.Count > 0)
        {
            var returningIds = state.PendingTurnStartReturnCardInstanceIds.ToArray();
            state.PendingTurnStartReturnCardInstanceIds.Clear();
            foreach (var instanceId in returningIds)
            {
                var card = RemoveCardFromPiles(state, instanceId);
                if (card is not null) state.Hand.Add(card);
            }
        }
        if (state.Player.Hp > 0m && state.PendingTurnStartCopies.Count > 0)
        {
            var copies = state.PendingTurnStartCopies.ToArray();
            state.PendingTurnStartCopies.Clear();
            for (var index = 0; index < copies.Length && state.Hand.Count < 10; index++)
            {
                var copy = copies[index] with
                {
                    InstanceId = $"copy:nightmare:{copies[index].ModelId.ToLowerInvariant()}:{index + 1}",
                    IsPlayable = true,
                    RestrictionReason = null
                };
                state.Hand.Add(copy);
            }
        }
        var handDrawDelta = GetStatusAmount(state.Player.Statuses, "HAND_DRAW_DELTA");
        var turnStartDrawn = new List<CardState>();
        Draw(
            state,
            Math.Max(0, state.Player.CardsPerTurn + handDrawDelta + scheduledDraw + recurringDraw),
            fromHandDraw: true,
            drawnCards: turnStartDrawn);
        if (state.Player.Hp > 0m)
            ApplyHellraiserDrawTriggers(state, turnStartDrawn);
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses
                .Remove("FERAL_ZERO_COST_ATTACK_RETURN_USED")
                .Remove("NOSTALGIA_ATTACK_SKILL_PLAYS_BEFORE_SNAPSHOT")
                .Remove("ECHO_FORM_ROOT_PLAYS_BEFORE_SNAPSHOT")
        };
        state.HpLostSinceSnapshot = 0m;
        state.CardsExhaustedBeforeTurn = 0;
        state.CardsExhaustedSinceSnapshot = 0;
        state.CardsDiscardedBeforeTurn = 0;
        state.CardsDiscardedSinceSnapshot = 0;
        return state;
    }

    private static void ResolvePlayerDoom(MutableCombatState state)
    {
        var doom = GetStatusAmount(state.Player.Statuses, "DOOM");
        if (state.Player.Hp > 0m && doom > 0 && state.Player.Hp <= doom)
            state.Player = state.Player with { Hp = 0m };
    }

    private static void ResolveEnemyDoom(MutableCombatState state)
    {
        for (var index = 0; index < state.Enemies.Count; index++)
        {
            var enemy = state.Enemies[index];
            var doom = GetStatusAmount(enemy.Statuses, "DOOM");
            if (!enemy.IsAlive || doom <= 0 || enemy.Hp > doom) continue;
            state.Enemies[index] = enemy with { Hp = 0m };
            state.EnemiesKilled++;
            break;
        }
    }

    private void ApplyEffects(
        MutableCombatState state,
        ImmutableArray<EffectSpec> effects,
        TargetKind targetKind,
        string? targetId,
        string sourceId,
        bool enemyActs = false,
        bool cardSource = true,
        bool hasExplicitCardChoice = false,
        int energySpent = 0,
        bool canUseVigor = true,
        decimal damageDealtBeforeEffects = 0m,
        bool poweredAttack = false,
        CardHpLossTriggerContext? hpLossContext = null,
        List<CardState>? drawnCards = null,
        decimal cardDamageBonus = 0m,
        decimal cardBlockBonus = 0m,
        decimal firstAttackLethalityPercent = 0m,
        decimal zeroCostAttackBonus = 0m)
    {
        var killsBeforeEffects = state.EnemiesKilled;
        var exhaustedBeforeEffects = state.CardsExhaustedSinceSnapshot;
        var handWasEmptyBeforeEffects = state.Hand.Count == 0;
        var sourceDamageBonus = cardDamageBonus;
        var sourceBlockBonus = cardBlockBonus;
        var totalDamageDealtByEffects = 0m;
        decimal? previousCompanionDamageAmount = null;
        if (poweredAttack && zeroCostAttackBonus > 0m)
            sourceDamageBonus += zeroCostAttackBonus;
        if (poweredAttack && IsShiv(sourceId))
        {
            sourceDamageBonus += GetStatusAmount(state.Player.Statuses, "SHIV_DAMAGE_BONUS");
            if (state.ShivsPlayedBeforeTurn + state.ShivsPlayedSinceSnapshot == 0)
                sourceDamageBonus += GetStatusAmount(state.Player.Statuses, "FIRST_SHIV_DAMAGE_BONUS");
        }
        foreach (var effect in effects)
        {
            if (effect.UnsupportedReason is { Length: > 0 } restriction)
            {
                AddEstimated(state, PredictionRiskReason.MethodMirrorIncomplete, "unsupported_effect", restriction, effect.SourceId ?? sourceId);
                continue;
            }

            if (!ConditionMatches(
                    state,
                    effect,
                    targetId,
                    killsBeforeEffects,
                    drawnCards,
                    handWasEmptyBeforeEffects)) continue;

            var effectTarget = effect.TargetOverride ?? targetKind;
            switch (effect.Kind)
            {
                case EffectKind.Damage:
                    var drawnCardDamageBonus = effect.AmountByCardsDrawnThisTurn
                        ? state.CardsDrawnThisTurn * effect.XBonus
                        : 0m;
                    var repeat = effect.RepeatByEnergySpent
                        ? Math.Max(0, energySpent + effect.XBonus)
                        : effect.RepeatByOrbCount
                            ? state.Orbs.Count
                        : effect.RepeatByExhaustedCount
                            ? Math.Max(0, state.CardsExhaustedSinceSnapshot - exhaustedBeforeEffects)
                        : Math.Max(0, effect.Repeat);
                    if (effect.Condition == "ENERGY_SPENT_AT_LEAST:4" && energySpent >= 4)
                        repeat *= 2;
                    if (effect.RepeatByKillCount)
                    {
                        var pendingRepeats = 1;
                        while (pendingRepeats-- > 0)
                        {
                            var killsBeforeRepeat = state.EnemiesKilled;
                            if (enemyActs)
                                DamagePlayer(state, effect.Amount, sourceId);
                            else
                                ForTargets(state, effectTarget, targetId, enemy => DealPlayerDamage(
                                    state,
                                    enemy.Id,
                                    ModifyPlayerAttackDamage(state.Player, enemy, effect.Amount + sourceDamageBonus + drawnCardDamageBonus, includeVigor: canUseVigor, poweredAttack: poweredAttack, lethalityPercent: firstAttackLethalityPercent, sourceId: sourceId),
                                    poweredAttack,
                                    cardSource,
                                    totalDamage => totalDamageDealtByEffects += totalDamage));
                            pendingRepeats += state.EnemiesKilled - killsBeforeRepeat;
                        }
                    }
                    else
                    {
                        for (var hit = 0; hit < repeat; hit++)
                        {
                            if (enemyActs)
                                DamagePlayer(state, effect.Amount, sourceId);
                            else
                                ForTargets(state, effectTarget, targetId, enemy => DealPlayerDamage(
                                    state,
                                    enemy.Id,
                                    ModifyPlayerAttackDamage(state.Player, enemy, effect.Amount + sourceDamageBonus + drawnCardDamageBonus, includeVigor: canUseVigor, poweredAttack: poweredAttack, lethalityPercent: firstAttackLethalityPercent, sourceId: sourceId),
                                    poweredAttack,
                                    cardSource,
                                    totalDamage => totalDamageDealtByEffects += totalDamage));
                        }
                    }
                    if (effect.StatusId == "ALL_OTHER_ENEMIES_EQUAL_DAMAGE")
                    {
                        var baseDmg = effects.FirstOrDefault(e => e.Kind == EffectKind.Damage && e.StatusId != "ALL_OTHER_ENEMIES_EQUAL_DAMAGE")?.Amount ?? effect.Amount;
                        foreach (var other in state.Enemies.Where(e => e.Id != targetId && e.Hp > 0).ToArray())
                        {
                            DealPlayerDamage(
                                state,
                                other.Id,
                                ModifyPlayerAttackDamage(state.Player, other, baseDmg + sourceDamageBonus, includeVigor: false, poweredAttack: poweredAttack, lethalityPercent: 0, sourceId: sourceId),
                                poweredAttack,
                                cardSource,
                                totalDamage => totalDamageDealtByEffects += totalDamage);
                        }
                    }
                    if (!enemyActs && canUseVigor && (repeat > 0 || effect.RepeatByKillCount))
                        ConsumeVigor(state);
                    break;
                case EffectKind.DynamicDamage:
                    var dynamicDamage = (effect.RepeatByHistoryCounter
                        ? effect.Amount
                        : ResolveDynamicAmount(state, effect, targetId)) + sourceDamageBonus;
                    var dynamicRepeat = effect.RepeatByHistoryCounter
                        ? ResolveHistoryCounter(state, effect.StatusId)
                        : 1;
                    for (var hit = 0; hit < dynamicRepeat; hit++)
                        ForTargets(state, effectTarget, targetId, enemy =>
                            DealPlayerDamage(
                                state,
                                enemy.Id,
                                ModifyPlayerAttackDamage(
                                    state.Player,
                                    enemy,
                                    dynamicDamage,
                                    includeVigor: canUseVigor,
                                    poweredAttack: poweredAttack,
                                    lethalityPercent: firstAttackLethalityPercent,
                                    sourceId: sourceId),
                                poweredAttack,
                                cardSource,
                                totalDamage => totalDamageDealtByEffects += totalDamage));
                    if (!enemyActs && canUseVigor && dynamicRepeat > 0)
                        ConsumeVigor(state);
                    break;
                case EffectKind.CompanionDamage:
                    var companionEffect = effect;
                    if (effect.StatusId == "OSTY_ATTACK_COUNT_REPEAT" &&
                        effect.Amount <= 0m &&
                        previousCompanionDamageAmount is { } priorCompanionDamage)
                    {
                        companionEffect = effect with { Amount = priorCompanionDamage };
                    }
                    ApplyCompanionDamage(
                        state,
                        companionEffect,
                        effectTarget,
                        targetId,
                        effect.SourceId ?? sourceId,
                        energySpent);
                    if (effect.Amount > 0m)
                        previousCompanionDamageAmount = effect.Amount;
                    break;
                case EffectKind.LoseEnemyHp:
                    ForTargets(state, effectTarget, targetId, enemy => LoseEnemyHp(state, enemy.Id, effect.Amount));
                    break;
                case EffectKind.RandomEnemyDamage:
                    var randomHits = effect.RepeatByEnergySpent
                        ? Math.Max(0, energySpent + effect.XBonus)
                        : Math.Max(0, effect.Repeat);
                    for (var hit = 0; hit < randomHits; hit++)
                        AttackRandomEnemy(
                            state,
                            effect.Amount,
                            effect.SourceId ?? sourceId,
                            effect.RandomSource ?? RngSnapshotSet.CombatTargets,
                            includeVigor: canUseVigor);
                    if (canUseVigor && randomHits > 0) ConsumeVigor(state);
                    break;
                case EffectKind.RandomEnemyStatus:
                    for (var hit = 0; hit < Math.Max(0, effect.Repeat); hit++)
                        ApplyRandomEnemyStatus(state, effect, effect.SourceId ?? sourceId, markLocalDoom: !enemyActs);
                    break;
                case EffectKind.RandomEnemyAttackByExhaustedCount:
                    var randomAttackHits = Math.Max(0, state.CardsExhaustedSinceSnapshot - exhaustedBeforeEffects);
                    for (var hit = 0; hit < randomAttackHits; hit++)
                        AttackRandomEnemy(
                            state,
                            effect.Amount,
                            effect.SourceId ?? sourceId,
                            effect.RandomSource,
                            includeVigor: canUseVigor);
                    if (canUseVigor && randomAttackHits > 0)
                        ConsumeVigor(state);
                    break;
                case EffectKind.RandomExhaustCards:
                    AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
                        "uncalculable_random_card_selection", "Random hand-card selection requires a chance transition.", effect.SourceId ?? sourceId);
                    break;
                case EffectKind.RandomExhaustAttackAndGrow:
                    break;
                case EffectKind.LoseHp:
                    if (!enemyActs)
                    {
                        var hpLoss = ResolvePlayerHpLoss(state, effect.Amount);
                        state.Player = state.Player with { Hp = state.Player.Hp - hpLoss };
                        state.HpLostSinceSnapshot += hpLoss;
                        if (hpLoss > 0m) ApplyPlayerHpLossTriggers(state, hpLossContext);
                    }
                    break;
                case EffectKind.Block:
                    if (enemyActs)
                        ForTargets(state, TargetKind.Enemy, sourceId, enemy => SetEnemy(state, enemy with { Block = enemy.Block + effect.Amount }));
                    else
                    {
                        var fastenBonus = sourceId.StartsWith("DEFEND", StringComparison.OrdinalIgnoreCase)
                            ? GetStatusAmount(state.Player.Statuses, "FASTEN")
                            : 0;
                        GainPlayerBlock(state, effect.Amount + sourceBlockBonus + fastenBonus, applyDexterity: true, fromCard: cardSource);
                    }
                    break;
                case EffectKind.DynamicBlock:
                    var dynamicBlock = effect.AmountByDistinctOrbTypes
                        ? state.Orbs.Select(static orb => orb.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() * effect.Amount
                        : ResolveDynamicAmount(state, effect, targetId, damageDealtBeforeEffects);
                    GainPlayerBlock(state, dynamicBlock + sourceBlockBonus, applyDexterity: true, fromCard: cardSource);
                    break;
                case EffectKind.Heal:
                    if (enemyActs)
                        ForTargets(state, TargetKind.Enemy, sourceId, enemy => SetEnemy(state, enemy with { Hp = Math.Min(enemy.MaxHp, enemy.Hp + effect.Amount) }));
                    else if (effectTarget == TargetKind.Companion)
                        HealCompanion(state, effect.Amount);
                    else
                        state.Player = state.Player with { Hp = Math.Min(state.Player.MaxHp, state.Player.Hp + effect.Amount) };
                    break;
                case EffectKind.GainEnergy:
                    if (!enemyActs && GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0)
                    {
                        var energy = effect.AmountByHandAttackCount
                            ? state.Hand.Count(static card => card.CardType?.Equals("Attack", StringComparison.OrdinalIgnoreCase) == true ||
                                                               card.CardType == "攻击") * (int)effect.Amount
                            : effect.StatusId == "CURRENT_ENERGY"
                                ? state.Player.Energy * (int)effect.Amount
                                : (int)effect.Amount;
                        state.Player = state.Player with { Energy = state.Player.Energy + energy };
                    }
                    break;
                case EffectKind.ModifyMaxHp:
                    if (!enemyActs)
                    {
                        var maximum = Math.Max(1m, state.Player.MaxHp + effect.Amount);
                        state.Player = state.Player with { MaxHp = maximum, Hp = Math.Min(state.Player.Hp, maximum) };
                    }
                    break;
                case EffectKind.Summon:
                    var summonAmount = effect.RepeatByEnergySpent
                        ? Math.Max(0m, effect.Amount * (energySpent + effect.XBonus))
                        : effect.Amount;
                    if (summonAmount > 0m) SummonCompanion(state, summonAmount);
                    break;
                case EffectKind.KillCompanion:
                    if (GetStatusAmount(state.Player.Statuses, "OSTY_ALIVE") > 0)
                        KillCompanion(state);
                    break;
                case EffectKind.ScheduleCurrentBlock:
                    if (!enemyActs)
                        ApplyStatus(state, new EffectSpec(
                            EffectKind.ApplyStatus,
                            state.Player.Block * effect.Amount,
                            "SCHEDULED_BLOCK",
                            Duration: 1), TargetKind.Self, null, sourceId, false);
                    break;
                case EffectKind.Draw:
                    if (!enemyActs)
                    {
                        var drawAmount = effect.AmountByDistinctOrbTypes
                            ? state.Orbs.Select(static orb => orb.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() * (int)effect.Amount
                            : (int)effect.Amount;
                        Draw(state, drawAmount, drawnCards: drawnCards);
                    }
                    break;
                case EffectKind.DrawToHandSize:
                    if (!enemyActs)
                        DrawToHandSize(
                            state,
                            (int)effect.Amount,
                            effect.StatusId == "RETAIN_DRAWN_THIS_TURN",
                            drawnCards);
                    break;
                case EffectKind.DrawUntilNonAttack:
                    if (!enemyActs)
                        DrawUntilNonAttack(state, drawnCards);
                    break;
                case EffectKind.DiscardDrawnNonZeroCost:
                    if (drawnCards is null) break;
                    foreach (var drawn in drawnCards.Where(static card => card.CostsX || card.EnergyCost != 0).ToArray())
                    {
                        var handIndex = state.Hand.FindIndex(card => card.InstanceId == drawn.InstanceId);
                        if (handIndex < 0) continue;
                        state.Hand.RemoveAt(handIndex);
                        state.DiscardPile.Add(drawn);
                        state.CardsDiscardedSinceSnapshot++;
                    }
                    break;
                case EffectKind.ApplyStatus:
                    var statusEffect = effect.StatusId == "DOOM_EQUAL_DAMAGE"
                        ? effect with
                        {
                            StatusId = "DOOM",
                            Amount = totalDamageDealtByEffects,
                            FutureValuePerTurn = StatusValuation.IntrinsicFutureValue("DOOM", totalDamageDealtByEffects),
                            IsDebuff = true
                        }
                        : effect.AmountByTargetVulnerableStacks
                        ? effect with
                        {
                            Amount = effect.Amount * (targetId is { Length: > 0 }
                                ? GetStatusAmount(
                                    state.Enemies.FirstOrDefault(enemy => enemy.Id == targetId)?.Statuses ??
                                    ImmutableDictionary<string, StatusState>.Empty,
                                    "VULNERABLE")
                                : 0)
                        }
                        : effect.AmountByDistinctOrbTypes
                        ? effect with
                        {
                            Amount = effect.Amount * state.Orbs.Select(static orb => orb.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                        }
                        : effect.AmountByEnergySpent
                        ? effect with
                        {
                            Amount = effect.Amount * Math.Max(0, energySpent + effect.XBonus),
                            Duration = effect.StatusId is "WEAK" or "VULNERABLE"
                                ? (int)Math.Abs(effect.Amount * Math.Max(0, energySpent + effect.XBonus))
                                : effect.Duration
                        }
                        : effect;
                    if ((effect.AmountByTargetVulnerableStacks || effect.AmountByDistinctOrbTypes || effect.AmountByEnergySpent) &&
                        statusEffect.StatusId is { Length: > 0 } resolvedStatusId)
                    {
                        statusEffect = statusEffect with
                        {
                            FutureValuePerTurn = StatusValuation.IntrinsicFutureValue(resolvedStatusId, statusEffect.Amount),
                            IsDebuff = StatusValuation.IntrinsicFutureValue(resolvedStatusId, statusEffect.Amount) < 0m
                        };
                    }
                    ApplyStatus(state, statusEffect, effectTarget, targetId, sourceId, enemyActs);
                    break;
                case EffectKind.MultiplyStatus:
                    MultiplyStatus(state, effect, effectTarget, targetId);
                    break;
                case EffectKind.RemoveStatus:
                    RemoveStatus(state, effect, effectTarget, targetId, sourceId, enemyActs);
                    break;
                case EffectKind.DiscardCards:
                case EffectKind.ExhaustCards:
                case EffectKind.ChooseDrawToHand:
                case EffectKind.ChooseDrawToExhaust:
                case EffectKind.ChooseDiscardToHand:
                case EffectKind.ChooseDiscardToDrawTop:
                case EffectKind.ChooseHandToDrawTop:
                case EffectKind.CopyChosenHandCard:
                case EffectKind.ModifySelectedHandCard:
                    if (hasExplicitCardChoice)
                        break;
                    if (effect.Kind == EffectKind.CopyChosenHandCard &&
                        !state.Hand.Any(static card => card.IsColorless))
                        break;
                    AddUncalculable(
                        state,
                        PredictionRiskReason.UnresolvedPlayerChoice,
                        "uncalculable_selection_effect",
                        "Card movement requires an explicit sub-choice.",
                        effect.SourceId ?? sourceId);
                    break;
                case EffectKind.MoveAllZeroCostDiscardToHand:
                    var zeroCostCards = state.DiscardPile.Where(static card => !card.CostsX && card.EnergyCost == 0).ToArray();
                    state.DiscardPile.RemoveAll(card => zeroCostCards.Any(candidate => candidate.InstanceId == card.InstanceId));
                    state.Hand.AddRange(zeroCostCards);
                    break;
                case EffectKind.MoveRandomRareDrawToHand:
                    MoveRandomRareDrawToHand(state, effect, sourceId);
                    break;
                case EffectKind.MoveKingsBladeToHand:
                    MoveKingsBladeToHand(state);
                    break;
                case EffectKind.RetainHand:
                    for (var i = 0; i < state.Hand.Count; i++)
                    {
                        state.Hand[i] = state.Hand[i] with { RetainAtTurnEnd = true, TemporaryRetainAtTurnEnd = true };
                    }
                    break;
                case EffectKind.DiscardHandAndGenerate:
                    ReplaceHandWithGeneratedCards(state, effect, sourceId);
                    break;
                case EffectKind.DiscardHand:
                    state.CardsDiscardedSinceSnapshot += state.Hand.Count;
                    state.DiscardPile.AddRange(state.Hand);
                    state.Hand.Clear();
                    break;
                case EffectKind.ExhaustHand:
                    var exhaustedCards = state.Hand.ToArray();
                    state.ExhaustPile.AddRange(exhaustedCards);
                    state.Hand.Clear();
                    ApplyExhaustTriggers(state, exhaustedCards);
                    break;
                case EffectKind.ExhaustStatusCards:
                    ExhaustAllStatusCards(state);
                    break;
                case EffectKind.ExhaustNonAttacksAndBlock:
                    var nonAttacks = state.Hand.Where(static card =>
                        !string.Equals(card.CardType, "Attack", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(card.CardType, "攻击", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (nonAttacks.Length > 0)
                    {
                        state.Hand.RemoveAll(card => nonAttacks.Any(candidate => candidate.InstanceId == card.InstanceId));
                        state.ExhaustPile.AddRange(nonAttacks);
                        for (var index = 0; index < nonAttacks.Length; index++)
                            GainPlayerBlock(state, effect.Amount, applyDexterity: true, fromCard: cardSource);
                        ApplyExhaustTriggers(state, nonAttacks);
                    }
                    break;
                case EffectKind.GenerateCards:
                    AddGeneratedCards(state, effect, sourceId);
                    break;
                case EffectKind.GenerateRandomCards:
                    AddRandomGeneratedCards(
                        state,
                        effect.RepeatByExhaustedCount
                            ? effect with
                            {
                                Amount = Math.Max(0, state.CardsExhaustedSinceSnapshot - exhaustedBeforeEffects),
                                RepeatByExhaustedCount = false
                            }
                            : effect,
                        sourceId);
                    break;
                case EffectKind.Outbreak:
                    ApplyOutbreak(state, effect, sourceId);
                    break;
                case EffectKind.AutoPlayFromDrawPile:
                    var autoPlayCount = effect.RepeatByEnergySpent
                        ? Math.Max(0, energySpent + effect.XBonus)
                        : Math.Max(0, (int)effect.Amount);
                    for (var index = 0; index < autoPlayCount && state.Player.Hp > 0m; index++)
                    {
                        var played = effect.StatusId switch
                        {
                            "RANDOM_ATTACK" => AutoPlayRandomDrawCard(state, attackOnly: true, sourceId),
                            "RANDOM_ANY" => AutoPlayRandomDrawCard(state, attackOnly: false, sourceId),
                            _ => AutoPlayTopDrawCard(state, effect.StatusId == "TOP_FORCE_EXHAUST", sourceId)
                        };
                        if (!played)
                            break;
                    }
                    break;
                case EffectKind.AutoPlayEtherealFromExhaust:
                    AutoPlayEtherealFromExhaust(state, sourceId);
                    break;
                case EffectKind.AutoPlayShivsFromExhaust:
                    AutoPlayShivsFromExhaust(
                        state,
                        targetId,
                        effect.StatusId == "ALL_SHIV_UPGRADE",
                        sourceId);
                    break;
                case EffectKind.AutoPlaySelfFromPile:
                    break;
                case EffectKind.ChannelOrbs:
                    ChannelOrbs(state, effect, energySpent, sourceId);
                    break;
                case EffectKind.EvokeOrbs:
                    EvokeOrbs(state, effect, energySpent, sourceId);
                    break;
                case EffectKind.TriggerOrbPassives:
                    TriggerOrbPassives(state, effect, targetId, sourceId);
                    break;
                case EffectKind.ModifyOrbCapacity:
                    ModifyOrbCapacity(state, effect, sourceId);
                    break;
                case EffectKind.ModifyHandCosts:
                    for (var index = 0; index < state.Hand.Count; index++)
                        state.Hand[index] = state.Hand[index] with { EnergyCost = Math.Max(0, (int)effect.Amount), CostsX = false };
                    break;
                case EffectKind.CapHandCosts:
                    for (var index = 0; index < state.Hand.Count; index++)
                    {
                        var handCard = state.Hand[index];
                        if (handCard.CostsX || handCard.EnergyCost <= effect.Amount) continue;
                        state.Hand[index] = handCard with
                        {
                            EnergyCost = (int)effect.Amount,
                            TemporaryEnergyCostBeforeCap = effect.StatusId == "UNTIL_TURN_END"
                                ? handCard.EnergyCost
                                : null
                        };
                    }
                    break;
                case EffectKind.ModifyPlayedCardCost:
                    break;
                case EffectKind.ModifyPlayedCardDamage:
                    if (effect.StatusId == "MODEL_ALL")
                        ModifyModelDamageAcrossPiles(state, effect.SourceId ?? sourceId, effect.Amount);
                    break;
                case EffectKind.ModifyPlayedCardBlock:
                    break;
                case EffectKind.Forge:
                    ApplyForge(state, effect.Amount, sourceId);
                    break;
                case EffectKind.ClearEnemyBlockAndArtifact:
                    ForTargets(state, effectTarget, targetId, enemy => SetEnemy(state, enemy with
                    {
                        Block = 0m,
                        Statuses = enemy.Statuses.Remove("ARTIFACT")
                    }));
                    break;
                case EffectKind.DiscardHandThenDrawSame:
                    var redrawCount = state.Hand.Count;
                    state.CardsDiscardedSinceSnapshot += redrawCount;
                    state.DiscardPile.AddRange(state.Hand);
                    state.Hand.Clear();
                    Draw(state, redrawCount);
                    break;
                case EffectKind.Reboot:
                    // Reboot returns every non-exhausted card to the draw pile, then
                    // performs the normal draw effect that follows in card text.
                    state.DrawPile.AddRange(state.Hand);
                    state.Hand.Clear();
                    ShuffleReboot(state);
                    break;
                case EffectKind.KillAllDoomedEnemies:
                    KillAllDoomedEnemies(state);
                    break;
                case EffectKind.UpgradeCards:
                    if (effect.StatusId == "HAND_ONE")
                    {
                        for (var i = 0; i < state.Hand.Count; i++)
                        {
                            if (!state.Hand[i].IsUpgraded)
                            {
                                state.Hand[i] = state.Hand[i] with { IsUpgraded = true };
                                break;
                            }
                        }
                    }
                    else if (effect.StatusId == "HAND_ALL")
                    {
                        for (var i = 0; i < state.Hand.Count; i++)
                        {
                            state.Hand[i] = state.Hand[i] with { IsUpgraded = true };
                        }
                    }
                    else if (effect.StatusId == "DISCARD_RANDOM")
                    {
                        var remaining = (int)effect.Amount;
                        for (var i = 0; i < state.DiscardPile.Count && remaining > 0; i++)
                        {
                            if (!state.DiscardPile[i].IsUpgraded)
                            {
                                state.DiscardPile[i] = state.DiscardPile[i] with { IsUpgraded = true };
                                remaining--;
                            }
                        }
                    }
                    else if (effect.StatusId == "ALL_COMBAT_CARDS")
                    {
                        for (var i = 0; i < state.Hand.Count; i++) state.Hand[i] = state.Hand[i] with { IsUpgraded = true };
                        for (var i = 0; i < state.DrawPile.Count; i++) state.DrawPile[i] = state.DrawPile[i] with { IsUpgraded = true };
                        for (var i = 0; i < state.DiscardPile.Count; i++) state.DiscardPile[i] = state.DiscardPile[i] with { IsUpgraded = true };
                    }
                    break;
                case EffectKind.TransformCards when effect.StatusId is "HAND_STATUS_TO_FUEL" or "HAND_STATUS_TO_FUEL_PLUS":
                    var isFuelPlus = effect.StatusId == "HAND_STATUS_TO_FUEL_PLUS";
                    for (var i = 0; i < state.Hand.Count; i++)
                    {
                        var card = state.Hand[i];
                        if (string.Equals(card.CardType, "Status", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(card.CardType, "状态", StringComparison.OrdinalIgnoreCase))
                        {
                            state.Hand[i] = new CardState(
                                Guid.NewGuid().ToString("N"),
                                "FUEL",
                                isFuelPlus ? "燃料+" : "燃料",
                                0,
                                TargetKind.Self,
                                [],
                                CardType: "Skill",
                                IsUpgraded: isFuelPlus);
                        }
                    }
                    break;
                case EffectKind.TransformCards when effect.StatusId is "HAND_ATTACKS_TO_GIANT_ROCK" or "HAND_ATTACKS_TO_GIANT_ROCK_PLUS":
                    var isRockPlus = effect.StatusId == "HAND_ATTACKS_TO_GIANT_ROCK_PLUS";
                    for (var i = 0; i < state.Hand.Count; i++)
                    {
                        var card = state.Hand[i];
                        if (string.Equals(card.CardType, "Attack", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(card.CardType, "攻击", StringComparison.OrdinalIgnoreCase))
                        {
                            state.Hand[i] = new CardState(
                                Guid.NewGuid().ToString("N"),
                                "GIANT_ROCK",
                                isRockPlus ? "巨石+" : "巨石",
                                2,
                                TargetKind.Enemy,
                                [new EffectSpec(EffectKind.Damage, isRockPlus ? 26 : 20)],
                                CardType: "Attack",
                                IsUpgraded: isRockPlus);
                        }
                    }
                    break;
                case EffectKind.PlayRestriction:
                    break;
            }
        }
    }

    private static decimal ResolveDynamicAmount(
        MutableCombatState state,
        EffectSpec effect,
        string? targetId,
        decimal damageDealtBeforeEffects = 0m)
    {
        if (effect.StatusId == "PLAYER_BLOCK") return state.Player.Block * effect.Amount;
        if (effect.StatusId == "OSTY_MAX_HP_BLOCK")
            return Math.Max(0, GetStatusAmount(state.Player.Statuses, "OSTY_MAX_HP")) * effect.Amount;
        if (effect.StatusId == "PLAYER_STRENGTH")
            return Math.Max(0, GetStatusAmount(state.Player.Statuses, "STRENGTH")) * effect.Amount;
        if (effect.StatusId == "DAMAGE_DEALT_THIS_CARD")
            return Math.Max(0m, state.DamageDealt - damageDealtBeforeEffects) * effect.Amount;
        if (effect.StatusId == "DRAW_PILE_COUNT") return state.DrawPile.Count * effect.Amount;
        if (effect.StatusId == "DISCARD_PILE_COUNT") return state.DiscardPile.Count * effect.Amount + effect.XBonus;
        if (effect.StatusId == "EXHAUST_PILE_COUNT") return effect.Amount + state.ExhaustPile.Count * effect.XBonus;
        if (effect.StatusId == "HAND_CARD_COUNT") return effect.Amount + state.Hand.Count * effect.XBonus;
        if (effect.StatusId == "CARDS_PLAYED_THIS_COMBAT")
            return (state.HistoryBeforeSnapshot.CardsPlayedThisCombat + state.CardPlaysFinishedSinceSnapshot) * effect.Amount + effect.XBonus;
        if (effect.StatusId == "CARDS_DRAWN_THIS_COMBAT")
            return effect.Amount + (state.HistoryBeforeSnapshot.CardsDrawnThisCombat + state.CardsDrawnSinceSnapshot) * effect.XBonus;
        if (effect.StatusId == "CARDS_GENERATED_THIS_COMBAT")
            return effect.Amount + (state.HistoryBeforeSnapshot.CardsGeneratedThisCombat + state.CardsGeneratedSinceSnapshot) * effect.XBonus;
        if (effect.StatusId == "CARDS_DISCARDED_THIS_TURN")
            return effect.Amount +
                   (state.CardsDiscardedBeforeTurn + state.CardsDiscardedSinceSnapshot) * effect.XBonus;
        if (effect.StatusId == "STRIKE_CARD_COUNT")
            return effect.Amount + state.Hand.Concat(state.DrawPile).Concat(state.DiscardPile).Concat(state.ExhaustPile)
                .Count(static card => card.Name.Contains("打击", StringComparison.Ordinal) ||
                                      card.ModelId.Contains("STRIKE", StringComparison.OrdinalIgnoreCase)) * effect.XBonus;
        if (effect.StatusId == "ALL_ENEMY_STATUS:POISON")
            return state.Enemies.Where(static enemy => enemy.IsAlive)
                .Sum(enemy => GetStatusAmount(enemy.Statuses, "POISON")) * effect.Amount + effect.XBonus;
        if (effect.StatusId == "TARGET_DEBUFF_TYPE_COUNT")
        {
            var target = state.Enemies.FirstOrDefault(enemy => enemy.Id == targetId && enemy.IsAlive);
            return effect.Amount + (target?.Statuses.Values.Count(static status => status.IsDebuff && status.Amount > 0) ?? 0) * effect.XBonus;
        }
        if (effect.StatusId == "EXHAUST_SOUL_COUNT")
            return effect.Amount + state.ExhaustPile.Count(static card =>
                card.ModelId.Equals("SOUL", StringComparison.OrdinalIgnoreCase) ||
                card.Name.Equals("灵魂", StringComparison.Ordinal)) * effect.XBonus;
        const string prefix = "TARGET_STATUS:";
        if (effect.StatusId?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            var status = effect.StatusId[prefix.Length..];
            var target = state.Enemies.FirstOrDefault(enemy => enemy.Id == targetId && enemy.IsAlive);
            return target is null ? 0m : effect.XBonus == 0
                ? GetStatusAmount(target.Statuses, status) * effect.Amount
                : effect.Amount + GetStatusAmount(target.Statuses, status) * effect.XBonus;
        }
        return 0m;
    }

    private static int ResolveHistoryCounter(MutableCombatState state, string? counter) => counter switch
    {
        "HAND_SKILL_COUNT" => state.Hand.Count(static card => IsCardType(card, "Skill", "技能")),
        "ATTACKS_PLAYED_THIS_TURN" => state.AttacksPlayedBeforeTurn + state.AttacksPlayedSinceSnapshot,
        "SKILLS_PLAYED_THIS_TURN" => state.HistoryBeforeSnapshot.SkillsPlayedThisTurn + state.SkillsPlayedSinceSnapshot,
        "ETHEREAL_PLAYED_THIS_COMBAT" => state.HistoryBeforeSnapshot.EtherealCardsPlayedThisCombat + state.EtherealCardsPlayedSinceSnapshot,
        "PLAYER_DAMAGE_RECEIVED_THIS_COMBAT" => state.HistoryBeforeSnapshot.DamageReceivedEventsThisCombat + state.DamageReceivedEventsSinceSnapshot,
        _ => 0
    };

    private static void MultiplyStatus(MutableCombatState state, EffectSpec effect, TargetKind targetKind, string? targetId)
    {
        if (effect.StatusId is null) return;
        ForTargets(state, targetKind, targetId, enemy =>
        {
            if (!enemy.Statuses.TryGetValue(effect.StatusId, out var status)) return;
            SetEnemy(state, enemy with
            {
                Statuses = enemy.Statuses.SetItem(effect.StatusId, status with
                {
                    Amount = (int)Math.Max(0m, status.Amount * effect.Amount)
                })
            });
        });
    }

    private static bool ConditionMatches(
        MutableCombatState state,
        EffectSpec effect,
        string? targetId,
        int killsBeforeEffects,
        List<CardState>? drawnCards,
        bool handWasEmptyBeforeEffects)
    {
        if (effect.Condition is null) return true;
        return effect.Condition switch
        {
            "HAND_NO_ATTACKS" => state.Hand.All(card =>
                !string.Equals(card.CardType, "Attack", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(card.CardType, "攻击", StringComparison.OrdinalIgnoreCase)),
            "HAND_EMPTY" => handWasEmptyBeforeEffects,
            "OSTY_ALIVE" => GetStatusAmount(state.Player.Statuses, "OSTY_ALIVE") > 0,
            "OSTY_ATTACKED_THIS_TURN" => GetStatusAmount(state.Player.Statuses, "OSTY_ATTACKS_THIS_TURN") > 0,
            "FIRST_PLAYED_THIS_TURN" => state.HistoryBeforeSnapshot.CardsPlayedThisTurn + state.CardPlaysFinishedSinceSnapshot == 0,
            "HAND_AT_LEAST_5" => state.Hand.Count >= 5,
            "EXHAUST_AT_LEAST_3" => state.ExhaustPile.Count >= 3,
            "TARGET_HAS_POISON" => TargetMatches(state, targetId, enemy => GetStatusAmount(enemy.Statuses, "POISON") > 0),
            "TARGET_HAS_VULNERABLE" => TargetMatches(state, targetId, enemy => GetStatusAmount(enemy.Statuses, "VULNERABLE") > 0),
            "TARGET_INTENDS_ATTACK" => TargetMatches(state, targetId, enemy => enemy.Intents.Any(intent => intent.DamagePerHit > 0 && intent.Hits > 0)),
            "HAS_FROST_ORB" => state.Orbs.Any(static orb => orb.Id == "FROST"),
            "SOURCE_CARD_KILLED" => state.EnemiesKilled > killsBeforeEffects,
            "PLAYER_HP_LOST_THIS_TURN" => state.HpLostSinceSnapshot > 0m,
            "DOOM_APPLIED_THIS_TURN" => GetStatusAmount(state.Player.Statuses, "DOOM_APPLIED_THIS_TURN") > 0,
            "CARD_EXHAUSTED_THIS_TURN" => state.CardsExhaustedBeforeTurn + state.CardsExhaustedSinceSnapshot > 0,
            "LAST_DRAWN_CARD_SKILL" => drawnCards is { Count: > 0 } &&
                IsCardType(drawnCards[^1], "Skill", "技能"),
            var condition when condition.StartsWith("CARD_PLAYS_FINISHED_LT:", StringComparison.Ordinal) &&
                               int.TryParse(condition["CARD_PLAYS_FINISHED_LT:".Length..], out var maximum) =>
                state.HistoryBeforeSnapshot.CardsPlayedThisTurn + state.CardPlaysFinishedSinceSnapshot < maximum,
            "ENERGY_SPENT_AT_LEAST:4" => true,
            _ => false
        };
    }

    private static bool TargetMatches(MutableCombatState state, string? targetId, Func<CreatureState, bool> predicate)
    {
        var target = state.Enemies.FirstOrDefault(enemy => enemy.Id == targetId && enemy.IsAlive);
        return target is not null && predicate(target);
    }

    private static void ReplaceHandWithGeneratedCards(
        MutableCombatState state,
        EffectSpec effect,
        string sourceId)
    {
        if (effect.GeneratedCard is null)
        {
            AddUncalculable(state, PredictionRiskReason.MethodNotMirrored,
                "uncalculable_generated_card", "Generated-card template is missing.", effect.SourceId ?? sourceId);
            return;
        }

        var discarded = state.Hand.ToArray();
        state.Hand.Clear();
        state.CardsDiscardedSinceSnapshot += discarded.Length;
        state.DiscardPile.AddRange(discarded);
        for (var index = 0; index < discarded.Length; index++)
        {
            var template = effect.GeneratedCard;
            AddGeneratedCard(
                state,
                PrepareGeneratedCard(state, template, $"{template.InstanceId}:{index + 1}"),
                GeneratedCardDestination.Hand,
                effect.SourceId ?? sourceId);
        }
    }

    private static void AddGeneratedCards(MutableCombatState state, EffectSpec effect, string sourceId)
    {
        if (effect.GeneratedCard is null)
        {
            AddUncalculable(state, PredictionRiskReason.MethodNotMirrored,
                "uncalculable_generated_card", "Generated-card template is missing.", effect.SourceId ?? sourceId);
            return;
        }
        var amount = effect.StatusId == "FILL_HAND" && effect.GeneratedDestination == GeneratedCardDestination.Hand
            ? Math.Max(0, 10 - state.Hand.Count)
            : Math.Max(0, (int)effect.Amount);
        var existing = state.Hand.Concat(state.DrawPile).Concat(state.DiscardPile).Concat(state.ExhaustPile)
            .Count(card => card.ModelId.Equals(effect.GeneratedCard.ModelId, StringComparison.OrdinalIgnoreCase));
        for (var index = 0; index < amount; index++)
        {
            var template = effect.GeneratedCard;
            var generated = PrepareGeneratedCard(
                state,
                template,
                $"{template.InstanceId}:{existing + index + 1}");
            AddGeneratedCard(state, generated, effect.GeneratedDestination, effect.SourceId ?? sourceId);
        }
    }

    private static void AddRandomGeneratedCards(MutableCombatState state, EffectSpec effect, string sourceId)
    {
        if (!string.Equals(effect.RandomSource, RngSnapshotSet.CombatCardGeneration, StringComparison.Ordinal))
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_card_generation_source",
                $"Random card generation requires the {RngSnapshotSet.CombatCardGeneration} RNG stream.",
                effect.SourceId ?? sourceId);
            return;
        }
        if (effect.GeneratedCardPool.IsDefaultOrEmpty) return;
        if (state.RngStreams.Get(RngSnapshotSet.CombatCardGeneration) is not { IsKnown: true } stream)
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_card_generation",
                $"Random card generation requires the {RngSnapshotSet.CombatCardGeneration} RNG stream.",
                effect.SourceId ?? sourceId);
            return;
        }
        RngStreamSnapshot? positionStream = null;
        if (effect.RandomizeGeneratedPosition)
        {
            positionStream = state.RngStreams.Get(RngSnapshotSet.Shuffle);
            if (positionStream is not { IsKnown: true })
            {
                AddUncalculable(
                    state,
                    PredictionRiskReason.UnsupportedRandomSource,
                    "uncalculable_random_generated_position",
                    $"Random generated-card placement requires the {RngSnapshotSet.Shuffle} RNG stream.",
                    effect.SourceId ?? sourceId);
                return;
            }
        }

        var candidates = effect.GeneratedCardPool.ToList();
        var amount = effect.RandomSelectionWithReplacement
            ? Math.Max(0, (int)effect.Amount)
            : Math.Min(Math.Max(0, (int)effect.Amount), candidates.Count);
        for (var index = 0; index < amount; index++)
        {
            var selectedIndex = stream.NextInt(candidates.Count, out stream);
            var selected = candidates[selectedIndex];
            if (!effect.RandomSelectionWithReplacement) candidates.RemoveAt(selectedIndex);
            var existing = state.Hand.Concat(state.DrawPile).Concat(state.DiscardPile).Concat(state.ExhaustPile)
                .Count(card => card.ModelId.Equals(selected.ModelId, StringComparison.OrdinalIgnoreCase));
            var generated = PrepareGeneratedCard(
                state,
                selected,
                $"{selected.InstanceId}:{existing + 1}");
            if (effect.StatusId == "FREE_THIS_TURN")
                generated = generated with
                {
                    EnergyCost = 0,
                    CostsX = false,
                    TemporaryEnergyCostBeforeCap = selected.EnergyCost,
                    TemporaryCostsXBeforeOverride = selected.CostsX
                };
            else if (effect.StatusId == "FREE_THIS_COMBAT")
                generated = generated with { EnergyCost = 0, CostsX = false, FreeThisCombat = true };
            int? drawPosition = null;
            if (positionStream is { IsKnown: true } placement)
            {
                drawPosition = placement.NextInt(state.DrawPile.Count + 1, out placement);
                positionStream = placement;
            }
            AddGeneratedCard(
                state,
                generated,
                effect.GeneratedDestination,
                effect.SourceId ?? sourceId,
                drawPosition);
        }
        state.RngStreams = state.RngStreams.With(stream);
        if (positionStream is { IsKnown: true } positioned)
            state.RngStreams = state.RngStreams.With(positioned);
    }

    private static decimal ExhaustRandomAttackAndGetDamage(
        MutableCombatState state,
        EffectSpec effect,
        string sourceId)
    {
        var candidates = state.Hand
            .Where(card => IsCardType(card, "Attack", "攻击"))
            .ToArray();
        if (candidates.Length == 0) return 0m;
        if (!string.Equals(effect.RandomSource, RngSnapshotSet.CombatCardSelection, StringComparison.Ordinal) ||
            state.RngStreams.Get(RngSnapshotSet.CombatCardSelection) is not { IsKnown: true } stream)
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_attack_exhaust",
                $"Random Attack exhaustion requires the {RngSnapshotSet.CombatCardSelection} RNG stream.",
                effect.SourceId ?? sourceId);
            return 0m;
        }

        var selected = candidates[stream.NextInt(candidates.Length, out stream)];
        state.RngStreams = state.RngStreams.With(stream);
        var index = state.Hand.FindIndex(card => card.InstanceId == selected.InstanceId);
        if (index < 0) return 0m;
        state.Hand.RemoveAt(index);
        state.ExhaustPile.Add(selected);
        ApplyExhaustTriggers(state, [selected]);

        var baseDamage = selected.Effects
            .Where(static candidate => candidate.Kind is EffectKind.Damage or EffectKind.DynamicDamage)
            .Select(static candidate => candidate.Amount)
            .FirstOrDefault();
        if (baseDamage <= 0m)
            AddEstimated(
                state,
                PredictionRiskReason.MethodMirrorIncomplete,
                "random_exhaust_damage_unknown",
                "The exhausted Attack has no modeled single-hit damage value.",
                effect.SourceId ?? sourceId);
        return Math.Max(0m, baseDamage + selected.CombatDamageBonus);
    }

    private static void AddGeneratedCard(
        MutableCombatState state,
        CardState generated,
        GeneratedCardDestination destination,
        string sourceId,
        int? drawPosition = null)
    {
        switch (destination)
        {
            case GeneratedCardDestination.DrawPile:
                if (drawPosition is { } position)
                    state.DrawPile.Insert(Math.Clamp(position, 0, state.DrawPile.Count), generated);
                else
                    state.DrawPile.Add(generated);
                break;
            case GeneratedCardDestination.DiscardPile:
                state.DiscardPile.Add(generated);
                break;
            default:
                if (state.Hand.Count < 10)
                    state.Hand.Add(generated);
                else
                    state.DiscardPile.Add(generated);
                break;
        }

        state.CardsGeneratedSinceSnapshot++;
        ApplyGeneratedCardTriggers(state, generated, sourceId);
    }

    private static void ApplyGeneratedCardTriggers(
        MutableCombatState state,
        CardState generated,
        string sourceId)
    {
        var colorlessStrength = generated.IsColorless
            ? GetStatusAmount(state.Player.Statuses, "TRIGGER_COLORLESS_CARD_PLAYED_STRENGTH")
            : 0;
        var generatedStrength = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_GENERATED_STRENGTH");
        if (colorlessStrength > 0)
            GainPlayerStrength(state, colorlessStrength);
        if (generatedStrength > 0)
            GainPlayerStrength(state, generatedStrength);

        var block = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_GENERATED_BLOCK");
        if (block > 0)
            GainPlayerBlock(state, block, applyDexterity: false);

        var firstBlock = GetStatusAmount(state.Player.Statuses, "TRIGGER_FIRST_CARD_GENERATED_EACH_TURN_BLOCK");
        if (firstBlock > 0 && GetStatusAmount(state.Player.Statuses, "CARD_GENERATED_BLOCK_USED_THIS_TURN") <= 0)
        {
            GainPlayerBlock(state, firstBlock, applyDexterity: false);
            state.Player = state.Player with
            {
                Statuses = state.Player.Statuses.SetItem(
                    "CARD_GENERATED_BLOCK_USED_THIS_TURN",
                    new StatusState("CARD_GENERATED_BLOCK_USED_THIS_TURN", 1))
            };
        }

        if (generated.CardType is "Status" or "状态")
        {
            var statusDamage = GetStatusAmount(state.Player.Statuses, "TRIGGER_STATUS_CARD_GENERATED_ALL_DAMAGE");
            if (statusDamage > 0)
                foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                    DamageEnemy(state, enemy.Id, statusDamage);
        }

        if (generated.CardType is "Status" or "状态")
            MutateRocketPunchCosts(state);
    }

    private static void MutateRocketPunchCosts(MutableCombatState state)
    {
        static CardState Apply(CardState card)
        {
            var effect = card.Effects.FirstOrDefault(static item =>
                item.Kind == EffectKind.ModifyPlayedCardCost &&
                item.StatusId is "SELF_BY_STATUS_GENERATED" or
                    "SELF_BY_STATUS_GENERATED_TURN_ZERO" or
                    "SELF_BY_STATUS_GENERATED_UNTIL_PLAYED_ZERO");
            if (effect is null) return card;
            return effect.StatusId switch
            {
                "SELF_BY_STATUS_GENERATED" => card with { EnergyCost = Math.Max(0, card.EnergyCost - 1) },
                "SELF_BY_STATUS_GENERATED_TURN_ZERO" => card with { EnergyCost = 0 },
                _ => card with { EnergyCost = 0 }
            };
        }

        for (var index = 0; index < state.Hand.Count; index++) state.Hand[index] = Apply(state.Hand[index]);
        for (var index = 0; index < state.DrawPile.Count; index++) state.DrawPile[index] = Apply(state.DrawPile[index]);
        for (var index = 0; index < state.DiscardPile.Count; index++) state.DiscardPile[index] = Apply(state.DiscardPile[index]);
        for (var index = 0; index < state.ExhaustPile.Count; index++) state.ExhaustPile[index] = Apply(state.ExhaustPile[index]);
    }

    private static void ModifyModelDamageAcrossPiles(MutableCombatState state, string modelId, decimal amount)
    {
        var normalizedModelId = modelId.EndsWith("_UPGRADE", StringComparison.OrdinalIgnoreCase)
            ? modelId[..^"_UPGRADE".Length]
            : modelId;
        CardState Apply(CardState card) =>
            (card.ModelId.EndsWith("_UPGRADE", StringComparison.OrdinalIgnoreCase)
                ? card.ModelId[..^"_UPGRADE".Length]
                : card.ModelId).Equals(normalizedModelId, StringComparison.OrdinalIgnoreCase) ||
            normalizedModelId.Equals("SOVEREIGN_BLADE", StringComparison.OrdinalIgnoreCase) && IsSovereignBlade(card.ModelId)
                ? card with { CombatDamageBonus = card.CombatDamageBonus + amount }
                : card;

        for (var index = 0; index < state.Hand.Count; index++) state.Hand[index] = Apply(state.Hand[index]);
        for (var index = 0; index < state.DrawPile.Count; index++) state.DrawPile[index] = Apply(state.DrawPile[index]);
        for (var index = 0; index < state.DiscardPile.Count; index++) state.DiscardPile[index] = Apply(state.DiscardPile[index]);
        for (var index = 0; index < state.ExhaustPile.Count; index++) state.ExhaustPile[index] = Apply(state.ExhaustPile[index]);
    }

    private static void ApplyForge(MutableCombatState state, decimal amount, string sourceId)
    {
        var forge = Math.Max(0, (int)amount);
        if (forge == 0) return;

        var currentForge = GetStatusAmount(state.Player.Statuses, "FORGE");
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses.SetItem(
                "FORGE",
                new StatusState("FORGE", currentForge + forge, FutureValuePerTurn: (currentForge + forge) * 0.35m))
        };

        var bladeExists = state.Hand.Concat(state.DrawPile).Concat(state.DiscardPile)
            .Any(static card => IsSovereignBlade(card.ModelId));
        ModifyModelDamageAcrossPiles(state, "SOVEREIGN_BLADE", forge);
        if (bladeExists) return;

        var generated = new CardState(
            $"generated:sovereign_blade:{currentForge + forge}",
            "SOVEREIGN_BLADE",
            "君王之剑",
            2,
            TargetKind.Enemy,
            [new EffectSpec(EffectKind.Damage, 10, SourceId: "SOVEREIGN_BLADE")],
            RetainAtTurnEnd: true,
            CardType: "Attack",
            BaseEnergyCost: 2,
            CombatDamageBonus: forge);
        AddGeneratedCard(state, generated, GeneratedCardDestination.Hand, sourceId);
    }

    private static bool IsSovereignBlade(string modelId) =>
        modelId.Equals("SOVEREIGN_BLADE", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("SOVEREIGN_BLADE_UPGRADE", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("KINGS_BLADE", StringComparison.OrdinalIgnoreCase);

    private static void ResetTurnScopedRocketPunchCosts(MutableCombatState state)
    {
        static CardState Apply(CardState card) => card.Effects.Any(static item =>
                item.Kind == EffectKind.ModifyPlayedCardCost &&
                item.StatusId == "SELF_BY_STATUS_GENERATED_TURN_ZERO")
            ? card with { EnergyCost = CanonicalEnergyCost(card) }
            : card;

        for (var index = 0; index < state.Hand.Count; index++) state.Hand[index] = Apply(state.Hand[index]);
        for (var index = 0; index < state.DrawPile.Count; index++) state.DrawPile[index] = Apply(state.DrawPile[index]);
        for (var index = 0; index < state.DiscardPile.Count; index++) state.DiscardPile[index] = Apply(state.DiscardPile[index]);
        for (var index = 0; index < state.ExhaustPile.Count; index++) state.ExhaustPile[index] = Apply(state.ExhaustPile[index]);
    }

    private static int CanonicalEnergyCost(CardState card) =>
        card.BaseEnergyCost > 0 ? card.BaseEnergyCost :
        card.ModelId.Equals("ROCKET_PUNCH", StringComparison.OrdinalIgnoreCase) ? 2 : card.EnergyCost;

    private static CardState PrepareGeneratedCard(
        MutableCombatState state,
        CardState template,
        string instanceId)
    {
        var historicalEtherealReduction = template.Effects
            .Where(static effect => effect is
            {
                Kind: EffectKind.ModifyPlayedCardCost,
                StatusId: "BY_ETHEREAL_PLAYED_SINCE_SNAPSHOT"
            })
            .Sum(effect => state.HistoryBeforeSnapshot.EtherealCardsPlayedThisCombat * -(int)effect.Amount);
        return ApplyShivModifiers(state, template with
        {
            InstanceId = instanceId,
            EnergyCost = Math.Max(0, template.EnergyCost - historicalEtherealReduction),
            IsPlayable = true,
            RestrictionReason = null
        });
    }

    private static void ApplyShivModifiers(MutableCombatState state)
    {
        for (var index = 0; index < state.Hand.Count; index++)
            state.Hand[index] = ApplyShivModifiers(state, state.Hand[index]);
        for (var index = 0; index < state.DrawPile.Count; index++)
            state.DrawPile[index] = ApplyShivModifiers(state, state.DrawPile[index]);
        for (var index = 0; index < state.DiscardPile.Count; index++)
            state.DiscardPile[index] = ApplyShivModifiers(state, state.DiscardPile[index]);
        for (var index = 0; index < state.ExhaustPile.Count; index++)
            state.ExhaustPile[index] = ApplyShivModifiers(state, state.ExhaustPile[index]);
    }

    private static CardState ApplyShivModifiers(MutableCombatState state, CardState card)
    {
        if (!IsShiv(card)) return card;
        if (GetStatusAmount(state.Player.Statuses, "SHIV_ALL_ENEMIES") > 0)
            card = card with { Target = TargetKind.AllEnemies };
        if (GetStatusAmount(state.Player.Statuses, "SHIV_RETAIN") > 0)
            card = card with { RetainAtTurnEnd = true };
        return card;
    }

    private static bool IsShiv(CardState card) =>
        card.ModelId.Equals("SHIV", StringComparison.OrdinalIgnoreCase) ||
        card.ModelId.Equals("SHIV_UPGRADE", StringComparison.OrdinalIgnoreCase) ||
        card.ModelId.Equals("INKY_SHIV", StringComparison.OrdinalIgnoreCase) ||
        card.ModelId.Equals("INKY_SHIV_UPGRADE", StringComparison.OrdinalIgnoreCase);

    private static bool IsShiv(string modelId) =>
        modelId.Equals("SHIV", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("SHIV_UPGRADE", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("INKY_SHIV", StringComparison.OrdinalIgnoreCase) ||
        modelId.Equals("INKY_SHIV_UPGRADE", StringComparison.OrdinalIgnoreCase);

    private static void ApplyCardChoiceMovement(
        MutableCombatState state,
        ChoiceSpec choice,
        ImmutableArray<EffectSpec> cardEffects)
    {
        var cardIds = choice.SelectedCardInstanceIds;
        var transform = cardEffects.FirstOrDefault(static effect =>
            effect.Kind == EffectKind.TransformCards &&
            effect.StatusId is "HAND_ONE_TO_MINION_STRIKE" or "HAND_ONE_TO_MINION_STRIKE_PLUS" or
                "HAND_ANY_TO_MINION_SACRIFICE" or "HAND_ANY_TO_MINION_SACRIFICE_PLUS" or
                "DRAW_TWO_TO_MINION_DIVE_BOMB" or "DRAW_TWO_TO_MINION_DIVE_BOMB_PLUS");
        if (transform is not null)
        {
            TransformSelectedCardsToMinion(state, cardIds, transform.StatusId!, choice.Id);
            return;
        }
        if (cardIds.IsDefaultOrEmpty) return;
        var movement = cardEffects.FirstOrDefault(static effect =>
            effect.Kind is EffectKind.DiscardCards or EffectKind.ExhaustCards or
                EffectKind.ChooseDrawToHand or EffectKind.ChooseDrawToExhaust or EffectKind.ChooseDiscardToHand or
                EffectKind.ChooseDiscardToDrawTop or EffectKind.ChooseHandToDrawTop or
                EffectKind.ModifySelectedHandCard);
        movement ??= cardEffects.FirstOrDefault(static effect => effect.Kind == EffectKind.CopyChosenHandCard);
        if (movement is null) return;
        var requiredSelectionCount = movement.Kind == EffectKind.CopyChosenHandCard
            ? 1
            : (int)movement.Amount;
        if (movement.StatusId != "UP_TO" && requiredSelectionCount != cardIds.Length)
        {
            AddUncalculable(state, PredictionRiskReason.UnresolvedPlayerChoice,
                "uncalculable_card_choice_count", "The selected-card count does not match the movement effect.", choice.Id);
            return;
        }
        var source = movement.Kind switch
        {
            EffectKind.ChooseDrawToHand or EffectKind.ChooseDrawToExhaust => state.DrawPile,
            EffectKind.ChooseDiscardToHand or EffectKind.ChooseDiscardToDrawTop => state.DiscardPile,
            _ => state.Hand
        };
        if (movement.Kind == EffectKind.CopyChosenHandCard)
        {
            foreach (var cardId in cardIds)
            {
                var selectedCard = source.FirstOrDefault(card => card.InstanceId == cardId);
                if (selectedCard is null ||
                    movement.StatusId == "COLORLESS" && !selectedCard.IsColorless ||
                    movement.StatusId == "ATTACK_OR_POWER" &&
                    !IsCardType(selectedCard, "Attack", "攻击") &&
                    !IsCardType(selectedCard, "Power", "能力"))
                {
                    AddUncalculable(state, PredictionRiskReason.UnresolvedPlayerChoice,
                        "uncalculable_choice_card_missing", "Chosen card is no longer an eligible hand card.", choice.Id);
                    return;
                }
                var existing = state.Hand.Concat(state.DrawPile).Concat(state.DiscardPile).Concat(state.ExhaustPile)
                    .Count(candidate => candidate.ModelId.Equals(selectedCard.ModelId, StringComparison.OrdinalIgnoreCase));
                if (movement.StatusId == "NEXT_TURN")
                {
                    for (var copyIndex = 0; copyIndex < Math.Max(0, (int)movement.Amount); copyIndex++)
                        state.PendingTurnStartCopies.Add(selectedCard with
                        {
                            InstanceId = $"pending:nightmare:{selectedCard.ModelId.ToLowerInvariant()}:{existing + copyIndex + 1}"
                        });
                    state.CardsGeneratedSinceSnapshot += Math.Max(0, (int)movement.Amount);
                    continue;
                }
                for (var copyIndex = 0; copyIndex < Math.Max(0, (int)movement.Amount); copyIndex++)
                    state.Hand.Add(selectedCard with
                    {
                        InstanceId = $"copy:selected:{selectedCard.ModelId.ToLowerInvariant()}:{existing + copyIndex + 1}"
                    });
                state.CardsGeneratedSinceSnapshot += Math.Max(0, (int)movement.Amount);
            }
            return;
        }
        if (movement.Kind == EffectKind.ModifySelectedHandCard)
        {
            foreach (var cardId in cardIds)
            {
                var index = state.Hand.FindIndex(card => card.InstanceId == cardId);
                if (index < 0)
                {
                    AddUncalculable(state, PredictionRiskReason.UnresolvedPlayerChoice,
                        "uncalculable_choice_card_missing", "Chosen hand card is no longer available.", choice.Id);
                    return;
                }
                var selectedCard = state.Hand[index];
                state.Hand[index] = movement.StatusId switch
                {
                    "ADD_REPLAY" => selectedCard with
                    {
                        ReplayCount = selectedCard.ReplayCount + Math.Max(0, (int)movement.Amount)
                    },
                    "ADD_REPLAY_AND_COST" => selectedCard with
                    {
                        ReplayCount = selectedCard.ReplayCount + Math.Max(0, (int)movement.Amount),
                        EnergyCost = selectedCard.CostsX
                            ? selectedCard.EnergyCost
                            : Math.Max(0, selectedCard.EnergyCost + (int)movement.XBonus)
                    },
                    "ADD_RETAIN" => selectedCard with { RetainAtTurnEnd = true },
                    _ => selectedCard with { ExhaustAtTurnEnd = true }
                };
            }
            return;
        }
        var selected = new List<CardState>(cardIds.Length);
        foreach (var cardId in cardIds)
        {
            var index = source.FindIndex(card => card.InstanceId == cardId);
            if (index < 0)
            {
                AddUncalculable(state, PredictionRiskReason.UnresolvedPlayerChoice, "uncalculable_choice_card_missing", "Chosen card is no longer available.", choice.Id);
                return;
            }
            selected.Add(source[index]);
            source.RemoveAt(index);
        }
        if (movement.Kind == EffectKind.DiscardCards)
        {
            state.CardsDiscardedSinceSnapshot += selected.Count;
            state.DiscardPile.AddRange(selected);
        }
        else if (movement.Kind is EffectKind.ExhaustCards or EffectKind.ChooseDrawToExhaust)
        {
            state.ExhaustPile.AddRange(selected);
            ApplyExhaustTriggers(state, selected);
        }
        else if (movement.Kind is EffectKind.ChooseDiscardToDrawTop or EffectKind.ChooseHandToDrawTop)
            state.DrawPile.InsertRange(0, selected);
        else
            state.Hand.AddRange(selected);
    }

    private static void TransformSelectedCardsToMinion(
        MutableCombatState state,
        ImmutableArray<string> cardIds,
        string transformId,
        string choiceId)
    {
        var fromDraw = transformId.StartsWith("DRAW_TWO_", StringComparison.Ordinal);
        var source = fromDraw ? state.DrawPile : state.Hand;
        var expected = fromDraw ? 2 : transformId.StartsWith("HAND_ONE_", StringComparison.Ordinal) ? 1 : -1;
        if (expected >= 0 && cardIds.Length != expected)
        {
            AddUncalculable(state, PredictionRiskReason.UnresolvedPlayerChoice,
                "uncalculable_card_choice_count", "Minion transformation selection count is invalid.", choiceId);
            return;
        }
        if (cardIds.Any(id => source.All(card => card.InstanceId != id)))
        {
            AddUncalculable(state, PredictionRiskReason.UnresolvedPlayerChoice,
                "uncalculable_choice_card_missing", "A selected card is no longer available for minion transformation.", choiceId);
            return;
        }
        var isStrike = transformId.Contains("STRIKE", StringComparison.Ordinal);
        var isDive = transformId.Contains("DIVE_BOMB", StringComparison.Ordinal);
        var isPlus = transformId.EndsWith("_PLUS", StringComparison.Ordinal);
        for (var index = 0; index < source.Count; index++)
        {
            var selected = source[index];
            if (!cardIds.Contains(selected.InstanceId, StringComparer.Ordinal)) continue;
            source[index] = BuildMinionCard(selected.InstanceId, isStrike, isDive, isPlus);
        }
    }

    private static CardState BuildMinionCard(string instanceId, bool isStrike, bool isDive, bool isPlus)
    {
        if (isStrike)
        {
            var damage = isPlus ? 9m : 6m;
            return new CardState(
                $"minion:{instanceId}",
                isPlus ? "MINION_STRIKE_UPGRADE" : "MINION_STRIKE",
                isPlus ? "仆从打击+" : "仆从打击",
                0,
                TargetKind.Enemy,
                [new EffectSpec(EffectKind.Damage, damage), new EffectSpec(EffectKind.Draw, 1)],
                CardDestination.Exhaust,
                CardType: "Attack",
                IsUpgraded: isPlus);
        }
        if (isDive)
        {
            return new CardState(
                $"minion:{instanceId}",
                isPlus ? "MINION_DIVE_BOMB_UPGRADE" : "MINION_DIVE_BOMB",
                isPlus ? "仆从俯冲+" : "仆从俯冲",
                0,
                TargetKind.Enemy,
                [new EffectSpec(EffectKind.Damage, isPlus ? 16m : 13m)],
                CardDestination.Exhaust,
                CardType: "Attack",
                IsUpgraded: isPlus);
        }
        return new CardState(
            $"minion:{instanceId}",
            isPlus ? "MINION_SACRIFICE_UPGRADE" : "MINION_SACRIFICE",
            isPlus ? "仆从捐躯+" : "仆从捐躯",
            0,
            TargetKind.Self,
            [new EffectSpec(EffectKind.Block, isPlus ? 11m : 8m)],
            CardDestination.Exhaust,
            CardType: "Skill",
            IsUpgraded: isPlus);
    }

    private static void ApplyExhaustTriggers(MutableCombatState state, IReadOnlyList<CardState> exhaustedCards)
    {
        var exhaustedCount = exhaustedCards.Count;
        if (exhaustedCount <= 0) return;
        state.CardsExhaustedSinceSnapshot += exhaustedCount;
        ApplyListenerCostDelta(state.Hand, exhaustedCount);
        ApplyListenerCostDelta(state.DrawPile, exhaustedCount);
        ApplyListenerCostDelta(state.DiscardPile, exhaustedCount);
        ApplyListenerCostDelta(state.ExhaustPile, exhaustedCount);
        var block = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_EXHAUSTED_BLOCK");
        if (block > 0)
            for (var index = 0; index < exhaustedCount; index++) GainPlayerBlock(state, block);
        var draw = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_EXHAUSTED_DRAW");
        if (draw > 0) Draw(state, draw * exhaustedCount);
        foreach (var card in exhaustedCards)
        foreach (var effect in card.Effects.Where(static effect => effect.Condition == "SELF_EXHAUSTED"))
            if (effect.Kind == EffectKind.GainEnergy && GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0)
                state.Player = state.Player with { Energy = state.Player.Energy + (int)effect.Amount };
    }

    private static void ExhaustAllStatusCards(MutableCombatState state)
    {
        var pending = state.Hand
            .Concat(state.DrawPile)
            .Concat(state.DiscardPile)
            .Where(static card => IsCardType(card, "Status", "状态"))
            .Select(static card => card.InstanceId)
            .ToArray();
        foreach (var instanceId in pending)
        {
            CardState? card = null;
            foreach (var pile in new[] { state.Hand, state.DrawPile, state.DiscardPile })
            {
                var index = pile.FindIndex(candidate => candidate.InstanceId == instanceId);
                if (index < 0) continue;
                card = pile[index];
                pile.RemoveAt(index);
                break;
            }
            if (card is null) continue;
            state.ExhaustPile.Add(card);
            ApplyExhaustTriggers(state, [card]);
        }
    }

    private static void MoveRandomRareDrawToHand(
        MutableCombatState state,
        EffectSpec effect,
        string sourceId)
    {
        var slots = Math.Max(0, 10 - state.Hand.Count);
        if (slots == 0) return;
        if (!string.Equals(effect.RandomSource, RngSnapshotSet.CombatCardSelection, StringComparison.Ordinal) ||
            state.RngStreams.Get(RngSnapshotSet.CombatCardSelection) is not { IsKnown: true } stream)
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_rare_selection",
                $"Rare-card retrieval requires the {RngSnapshotSet.CombatCardSelection} RNG stream.",
                effect.SourceId ?? sourceId);
            return;
        }
        var candidates = state.DrawPile
            .Where(static card => string.Equals(card.Rarity, "稀有", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(card.Rarity, "Rare", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var count = Math.Min(slots, candidates.Count);
        for (var index = 0; index < count; index++)
        {
            var selectedIndex = stream.NextInt(candidates.Count, out stream);
            var selected = candidates[selectedIndex];
            candidates.RemoveAt(selectedIndex);
            var pileIndex = state.DrawPile.FindIndex(card => card.InstanceId == selected.InstanceId);
            if (pileIndex < 0) continue;
            state.DrawPile.RemoveAt(pileIndex);
            state.Hand.Add(selected);
        }
        state.RngStreams = state.RngStreams.With(stream);
    }

    private static void MoveKingsBladeToHand(MutableCombatState state)
    {
        if (state.Hand.Count >= 10) return;
        var kbInDraw = state.DrawPile.FirstOrDefault(static card => IsSovereignBlade(card.ModelId) || card.Name == "君王之剑");
        if (kbInDraw is not null)
        {
            state.DrawPile.Remove(kbInDraw);
            state.Hand.Add(kbInDraw);
            return;
        }
        var kbInDiscard = state.DiscardPile.FirstOrDefault(static card => IsSovereignBlade(card.ModelId) || card.Name == "君王之剑");
        if (kbInDiscard is not null)
        {
            state.DiscardPile.Remove(kbInDiscard);
            state.Hand.Add(kbInDiscard);
            return;
        }
        var kbInExhaust = state.ExhaustPile.FirstOrDefault(static card => IsSovereignBlade(card.ModelId) || card.Name == "君王之剑");
        if (kbInExhaust is not null)
        {
            state.ExhaustPile.Remove(kbInExhaust);
            state.Hand.Add(kbInExhaust);
        }
    }

    private static void ApplyListenerCostDelta(
        List<CardState> cards,
        int triggerCount,
        string statusId = "SELF_LISTENER_COST_DELTA")
    {
        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            var delta = card.Effects
                .Where(effect => effect.Kind == EffectKind.ModifyPlayedCardCost && effect.StatusId == statusId)
                .Sum(static effect => effect.Amount);
            if (delta == 0m) continue;
            cards[index] = card with { EnergyCost = Math.Max(0, card.EnergyCost + (int)(delta * triggerCount)) };
        }
    }

    private static void ApplyCreatureDeathCostListeners(MutableCombatState state)
    {
        ApplyListenerCostDelta(state.Hand, 1, "SELF_DEATH_COST_DELTA");
        ApplyListenerCostDelta(state.DrawPile, 1, "SELF_DEATH_COST_DELTA");
        ApplyListenerCostDelta(state.DiscardPile, 1, "SELF_DEATH_COST_DELTA");
        ApplyListenerCostDelta(state.ExhaustPile, 1, "SELF_DEATH_COST_DELTA");
    }

    private static void ChannelOrbs(MutableCombatState state, EffectSpec effect, int energySpent, string sourceId)
    {
        if (effect.StatusId is not { Length: > 0 } orbId)
        {
            AddUncalculable(state, PredictionRiskReason.MethodMirrorIncomplete,
                "uncalculable_orb_id", "Channel effect has no orb id.", sourceId);
            return;
        }
        if (state.OrbCapacity <= 0)
        {
            AddUncalculable(state, PredictionRiskReason.StateCaptureIncomplete,
                "uncalculable_orb_capacity", "No live orb-slot capacity was captured.", sourceId);
            return;
        }

        var amount = effect.AmountByEnergySpent
            ? Math.Max(0, energySpent + effect.XBonus)
            : effect.AmountByAliveEnemyCount
                ? Math.Max(0, state.Enemies.Count(static enemy => enemy.IsAlive) * (int)effect.Amount)
                : Math.Max(0, (int)effect.Amount);
        for (var index = 0; index < amount; index++)
        {
            if (state.Orbs.Count >= state.OrbCapacity)
            {
                var evoked = state.Orbs[0];
                state.Orbs.RemoveAt(0);
                EvokeOrb(state, evoked, sourceId);
            }
            state.Orbs.Add(CreateOrb(orbId, state.Player.Statuses));
        }
    }

    private static OrbState CreateOrb(string id, ImmutableDictionary<string, StatusState> statuses)
    {
        var permanentFocus = GetStatusAmount(statuses, "FOCUS");
        var temporaryFocus = GetStatusAmount(statuses, "TEMP_FOCUS");
        return id.ToUpperInvariant() switch
        {
            "LIGHTNING" => new OrbState("LIGHTNING", 3m + permanentFocus, 8m + permanentFocus, temporaryFocus),
            "FROST" => new OrbState("FROST", 2m + permanentFocus, 5m + permanentFocus, temporaryFocus),
            "DARK" => new OrbState("DARK", 6m + permanentFocus, 6m, temporaryFocus),
            "PLASMA" => new OrbState("PLASMA", 1m, 2m),
            _ => new OrbState(id.ToUpperInvariant(), 0m, 0m)
        };
    }

    private static void AdjustOrbFocus(MutableCombatState state, decimal delta)
    {
        for (var index = 0; index < state.Orbs.Count; index++)
        {
            var orb = state.Orbs[index];
            state.Orbs[index] = orb.Id.ToUpperInvariant() switch
            {
                "LIGHTNING" or "FROST" or "DARK" => orb with
                {
                    FocusAdjustment = orb.FocusAdjustment + delta
                },
                _ => orb
            };
        }
    }

    private static void EvokeOrb(MutableCombatState state, OrbState orb, string sourceId)
    {
        switch (orb.Id.ToUpperInvariant())
        {
            case "LIGHTNING":
            case "DARK":
                DamageRandomEnemy(state, orb.EffectiveEvokeValue, sourceId);
                break;
            case "FROST":
                GainPlayerBlock(state, orb.EffectiveEvokeValue);
                break;
            case "PLASMA":
                state.Player = state.Player with { Energy = state.Player.Energy + (int)orb.EvokeValue };
                break;
            default:
                AddUncalculable(state, PredictionRiskReason.MethodMirrorIncomplete,
                    "uncalculable_orb_type", $"Orb {orb.Id} has no verified shadow handler.", sourceId);
                break;
        }
    }

    private static void EvokeOrbs(MutableCombatState state, EffectSpec effect, int energySpent, string sourceId)
    {
        var repeat = effect.AmountByEnergySpent
            ? Math.Max(0, energySpent + effect.XBonus)
            : Math.Max(0, (int)effect.Amount);
        for (var iteration = 0; iteration < repeat && state.Orbs.Count > 0; iteration++)
        {
            if (effect.StatusId == "ALL")
            {
                var all = state.Orbs.ToArray();
                state.Orbs.Clear();
                foreach (var queuedOrb in all) EvokeOrb(state, queuedOrb, sourceId);
                continue;
            }
            var index = effect.StatusId == "LEFTMOST" ? 0 : state.Orbs.Count - 1;
            var selectedOrb = state.Orbs[index];
            state.Orbs.RemoveAt(index);
            EvokeOrb(state, selectedOrb, sourceId);
        }
    }

    private static void ModifyOrbCapacity(MutableCombatState state, EffectSpec effect, string sourceId)
    {
        state.OrbCapacity = Math.Max(0, state.OrbCapacity + (int)effect.Amount);
        while (state.Orbs.Count > state.OrbCapacity)
        {
            var evoked = state.Orbs[0];
            state.Orbs.RemoveAt(0);
            EvokeOrb(state, evoked, sourceId);
        }
    }

    private static void TriggerOrbPassives(MutableCombatState state, EffectSpec effect, string? targetId, string sourceId)
    {
        if (effect.StatusId is not { Length: > 0 } orbId) return;
        var repeat = Math.Max(0, (int)effect.Amount);
        for (var iteration = 0; iteration < repeat; iteration++)
        foreach (var orb in (orbId == "RIGHTMOST_ANY"
                     ? state.Orbs.TakeLast(1)
                     : state.Orbs.Where(orb => orb.Id.Equals(orbId, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            switch (orb.Id.ToUpperInvariant())
            {
                case "LIGHTNING":
                    if (targetId is { Length: > 0 } && state.Enemies.Any(enemy => enemy.Id == targetId && enemy.IsAlive))
                        DamageEnemy(state, targetId, orb.EffectivePassiveValue);
                    else
                        DamageRandomEnemy(state, orb.EffectivePassiveValue, sourceId);
                    break;
                case "FROST":
                    GainPlayerBlock(state, orb.EffectivePassiveValue);
                    break;
                case "DARK":
                    var index = state.Orbs.IndexOf(orb);
                    if (index >= 0) state.Orbs[index] = state.Orbs[index] with
                    {
                        EvokeValue = state.Orbs[index].EvokeValue + orb.EffectivePassiveValue
                    };
                    break;
                case "PLASMA":
                    state.Player = state.Player with { Energy = state.Player.Energy + (int)orb.PassiveValue };
                    break;
            }
        }
    }

    private static void TriggerTurnEndOrbPassives(MutableCombatState state)
    {
        for (var index = 0; index < state.Orbs.Count; index++)
        {
            var orb = state.Orbs[index];
            switch (orb.Id.ToUpperInvariant())
            {
                case "LIGHTNING":
                    DamageRandomEnemy(state, orb.EffectivePassiveValue, orb.Id);
                    break;
                case "FROST":
                    GainPlayerBlock(state, orb.EffectivePassiveValue);
                    break;
                case "DARK":
                    state.Orbs[index] = orb with { EvokeValue = orb.EvokeValue + orb.EffectivePassiveValue };
                    break;
                case "PLASMA":
                    break;
                default:
                    AddUncalculable(state, PredictionRiskReason.MethodMirrorIncomplete,
                        "uncalculable_orb_type", $"Orb {orb.Id} has no verified turn-end handler.", orb.Id);
                    break;
            }
        }
        var consumingShadow = GetStatusAmount(state.Player.Statuses, "CONSUMING_SHADOW");
        if (consumingShadow > 0)
            EvokeOrbs(state, new EffectSpec(EffectKind.EvokeOrbs, consumingShadow, "LEFTMOST"), 0, "CONSUMING_SHADOW");
    }

    private static void TriggerTurnStartOrbPassives(MutableCombatState state)
    {
        foreach (var orb in state.Orbs)
            if (orb.Id.Equals("PLASMA", StringComparison.OrdinalIgnoreCase))
                state.Player = state.Player with { Energy = state.Player.Energy + (int)orb.PassiveValue };
    }

    private static void DamageRandomEnemy(
        MutableCombatState state,
        decimal damage,
        string sourceId,
        string? randomSource = RngSnapshotSet.CombatTargets)
    {
        var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
        if (alive.Length == 0 || damage <= 0m) return;
        if (!string.Equals(randomSource, RngSnapshotSet.CombatTargets, StringComparison.Ordinal))
        {
            AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_target_source",
                $"Random enemy damage requires the {RngSnapshotSet.CombatTargets} RNG stream; source was {randomSource ?? "unknown"}.",
                sourceId);
            return;
        }
        if (state.RngStreams.Get(RngSnapshotSet.CombatTargets) is { IsKnown: true } stream)
        {
            var chosen = stream.NextInt(alive.Length, out stream);
            state.RngStreams = state.RngStreams.With(stream);
            DamageEnemy(state, alive[chosen].Id, damage);
            return;
        }
        if (alive.Length == 1)
            DamageEnemy(state, alive[0].Id, damage);
        AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
            "uncalculable_random_target", $"Random enemy damage needs the {randomSource} RNG stream.", sourceId);
    }

    private static void ApplyCompanionDamage(
        MutableCombatState state,
        EffectSpec effect,
        TargetKind targetKind,
        string? targetId,
        string sourceId,
        int energySpent)
    {
        if (GetStatusAmount(state.Player.Statuses, "OSTY_ALIVE") <= 0) return;
        var attackCount = effect.RepeatByHistoryCounter
            ? Math.Max(0, GetStatusAmount(state.Player.Statuses, "OSTY_ATTACKS_THIS_TURN"))
            : Math.Max(1, effect.Repeat);
        if (attackCount <= 0) return;
        var amount = effect.StatusId switch
        {
            "OSTY_CURRENT_HP_DAMAGE" => effect.Amount + GetStatusAmount(state.Player.Statuses, "OSTY_CURRENT_HP"),
            "OSTY_MAX_HP_DAMAGE" => effect.Amount + GetStatusAmount(state.Player.Statuses, "OSTY_MAX_HP"),
            "OSTY_ATTACK_CARDS_IN_DECK_DAMAGE" => effect.Amount +
                effect.XBonus * GetStatusAmount(state.Player.Statuses, "OSTY_ATTACK_CARD_COUNT"),
            _ => effect.Amount
        };
        if (effect.StatusId == "OSTY_ATTACK_COUNT_REPEAT" && amount <= 0)
            amount = 0;
        if (amount <= 0m) return;

        for (var hit = 0; hit < attackCount && state.Player.Hp > 0m; hit++)
        {
            if (targetKind == TargetKind.AllEnemies)
            {
                foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                    ApplyCompanionAttackToEnemy(state, enemy.Id, amount, sourceId);
                continue;
            }
            if (effect.RandomSource == RngSnapshotSet.CombatTargets)
            {
                var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
                if (alive.Length == 0) break;
                if (state.RngStreams.Get(RngSnapshotSet.CombatTargets) is { IsKnown: true } stream)
                {
                    var selected = alive[stream.NextInt(alive.Length, out stream)];
                    state.RngStreams = state.RngStreams.With(stream);
                    ApplyCompanionAttackToEnemy(state, selected.Id, amount, sourceId);
                }
                else
                {
                    if (alive.Length == 1)
                        ApplyCompanionAttackToEnemy(state, alive[0].Id, amount, sourceId);
                    AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
                        "uncalculable_random_target", "Osty random attack needs the CombatTargets RNG stream.", sourceId);
                }
                continue;
            }
            if (targetKind == TargetKind.Enemy && targetId is { Length: > 0 })
            {
                if (IsEnemyAlive(state, targetId)) ApplyCompanionAttackToEnemy(state, targetId, amount, sourceId);
                else AddUncalculable(state, PredictionRiskReason.StateCaptureIncomplete,
                    "uncalculable_target", "Required enemy target is unavailable for Osty attack.", targetId);
            }
        }
    }

    private static void ApplyCompanionAttackToEnemy(
        MutableCombatState state,
        string enemyId,
        decimal amount,
        string sourceId)
    {
        var calcify = GetStatusAmount(state.Player.Statuses, "CALCIFY");
        var damage = amount + calcify;
        var enemy = state.Enemies.FirstOrDefault(candidate => candidate.Id == enemyId && candidate.IsAlive);
        if (enemy is not null)
            damage = ModifyCompanionAttackDamage(enemy, damage);
        DamageEnemy(state, enemyId, damage);
        enemy = state.Enemies.FirstOrDefault(candidate => candidate.Id == enemyId && candidate.IsAlive);
        if (enemy is not null && enemy.Statuses.TryGetValue("SIC_EM", out var sicEm) && sicEm.Amount > 0)
            SummonCompanion(state, sicEm.Amount);
    }

    private static decimal ModifyCompanionAttackDamage(CreatureState enemy, decimal amount)
    {
        if (GetStatusAmount(enemy.Statuses, "VULNERABLE") <= 0)
            return Math.Max(0m, amount);
        var multiplier = GetStatusAmount(enemy.Statuses, "DEBILITATE") > 0 ? 2m : 1.5m;
        return Math.Max(0m, decimal.Floor(amount * multiplier));
    }

    private static void SummonCompanion(MutableCombatState state, decimal amount)
    {
        if (amount <= 0m) return;
        var summon = (int)Math.Truncate(amount);
        if (summon <= 0) return;
        var statuses = state.Player.Statuses;
        var maxHp = GetStatusAmount(statuses, "OSTY_MAX_HP");
        var currentHp = GetStatusAmount(statuses, "OSTY_CURRENT_HP");
        var alive = GetStatusAmount(statuses, "OSTY_ALIVE") > 0;
        if (alive)
        {
            statuses = statuses
                .SetItem("OSTY_MAX_HP", new StatusState("OSTY_MAX_HP", maxHp + summon))
                .SetItem("OSTY_CURRENT_HP", new StatusState("OSTY_CURRENT_HP", currentHp + summon));
        }
        else
        {
            statuses = statuses
                .SetItem("OSTY_ALIVE", new StatusState("OSTY_ALIVE", 1))
                .SetItem("OSTY_MAX_HP", new StatusState("OSTY_MAX_HP", summon))
                .SetItem("OSTY_CURRENT_HP", new StatusState("OSTY_CURRENT_HP", summon));
        }
        state.Player = state.Player with { Statuses = statuses };
    }

    private static void HealCompanion(MutableCombatState state, decimal amount)
    {
        if (amount <= 0m || GetStatusAmount(state.Player.Statuses, "OSTY_ALIVE") <= 0) return;
        var current = GetStatusAmount(state.Player.Statuses, "OSTY_CURRENT_HP");
        var maximum = GetStatusAmount(state.Player.Statuses, "OSTY_MAX_HP");
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses.SetItem(
                "OSTY_CURRENT_HP",
                new StatusState("OSTY_CURRENT_HP", Math.Min(maximum, current + (int)amount)))
        };
    }

    private static void KillCompanion(MutableCombatState state)
    {
        if (GetStatusAmount(state.Player.Statuses, "OSTY_KILL_PENDING") > 0) return;
        var lost = GetStatusAmount(state.Player.Statuses, "OSTY_CURRENT_HP");
        var statuses = state.Player.Statuses
            .SetItem("OSTY_KILL_PENDING", new StatusState("OSTY_KILL_PENDING", 1))
            .SetItem("OSTY_CURRENT_HP", new StatusState("OSTY_CURRENT_HP", 0));
        state.Player = state.Player with { Statuses = statuses };
        if (lost <= 0 || GetStatusAmount(statuses, "NECRO_MASTERY") <= 0) return;
        foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
            LoseEnemyHp(state, enemy.Id, lost);
    }

    private static void FinalizePendingCompanionKill(MutableCombatState state)
    {
        if (GetStatusAmount(state.Player.Statuses, "OSTY_KILL_PENDING") <= 0) return;
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses
                .SetItem("OSTY_ALIVE", new StatusState("OSTY_ALIVE", 0))
                .Remove("OSTY_KILL_PENDING")
        };
    }

    private static void LoseHpRandomEnemy(
        MutableCombatState state,
        decimal amount,
        string sourceId,
        string? randomSource = RngSnapshotSet.CombatTargets)
    {
        var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
        if (alive.Length == 0 || amount <= 0m) return;
        if (!string.Equals(randomSource, RngSnapshotSet.CombatTargets, StringComparison.Ordinal))
        {
            AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_target_source",
                $"Random enemy HP loss requires the {RngSnapshotSet.CombatTargets} RNG stream; source was {randomSource ?? "unknown"}.",
                sourceId);
            return;
        }
        if (state.RngStreams.Get(RngSnapshotSet.CombatTargets) is { IsKnown: true } stream)
        {
            var chosen = stream.NextInt(alive.Length, out stream);
            state.RngStreams = state.RngStreams.With(stream);
            LoseEnemyHp(state, alive[chosen].Id, amount);
            return;
        }
        if (alive.Length == 1)
            LoseEnemyHp(state, alive[0].Id, amount);
        AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
            "uncalculable_random_target", $"Random enemy HP loss needs the {randomSource} RNG stream.", sourceId);
    }

    private static void AttackRandomEnemy(
        MutableCombatState state,
        decimal baseDamage,
        string sourceId,
        string? randomSource,
        bool includeVigor)
    {
        var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
        if (alive.Length == 0 || baseDamage <= 0m) return;
        if (!string.Equals(randomSource, RngSnapshotSet.CombatTargets, StringComparison.Ordinal))
        {
            AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_target_source",
                $"Random attack requires the {RngSnapshotSet.CombatTargets} RNG stream; source was {randomSource ?? "unknown"}.",
                sourceId);
            return;
        }
        if (state.RngStreams.Get(RngSnapshotSet.CombatTargets) is { IsKnown: true } stream)
        {
            var chosen = stream.NextInt(alive.Length, out stream);
            state.RngStreams = state.RngStreams.With(stream);
            var enemy = alive[chosen];
            DealPlayerDamage(
                state,
                enemy.Id,
                ModifyPlayerAttackDamage(state.Player, enemy, baseDamage, includeVigor: includeVigor, poweredAttack: true),
                poweredAttack: true,
                cardSource: true);
            return;
        }
        if (alive.Length == 1)
        {
            var enemy = alive[0];
            DealPlayerDamage(
                state,
                enemy.Id,
                ModifyPlayerAttackDamage(state.Player, enemy, baseDamage, includeVigor: includeVigor, poweredAttack: true),
                poweredAttack: true,
                cardSource: true);
        }
        AddUncalculable(state, PredictionRiskReason.UnsupportedRandomSource,
            "uncalculable_random_target", $"Random attack needs the {randomSource} RNG stream.", sourceId);
    }

    private static void ApplyRandomEnemyStatus(
        MutableCombatState state,
        EffectSpec effect,
        string sourceId,
        bool markLocalDoom)
    {
        var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
        if (alive.Length == 0 || effect.StatusId is not { Length: > 0 } statusId) return;
        if (!string.Equals(effect.RandomSource, RngSnapshotSet.CombatTargets, StringComparison.Ordinal) ||
            state.RngStreams.Get(RngSnapshotSet.CombatTargets) is not { IsKnown: true } stream)
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_random_status_target",
                $"Random enemy status requires the {RngSnapshotSet.CombatTargets} RNG stream.",
                sourceId);
            return;
        }
        var selected = alive[stream.NextInt(alive.Length, out stream)];
        state.RngStreams = state.RngStreams.With(stream);
        var applied = ApplyEnemyStatus(state, selected.Id, new StatusState(
            statusId,
            (int)effect.Amount,
            effect.Duration,
            effect.IsDebuff,
            effect.FutureValuePerTurn));
        if (markLocalDoom && applied && statusId == "DOOM")
            MarkDoomAppliedThisTurn(state);
    }

    private bool AutoPlayTopDrawCard(MutableCombatState state, bool forceExhaust, string sourceId)
    {
        if (state.DrawPile.Count == 0)
        {
            if (state.DiscardPile.Count == 0) return false;
            if (state.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } && state.RngState == 0)
            {
                AddUncalculable(
                    state,
                    PredictionRiskReason.UnsupportedRandomSource,
                    "uncalculable_autoplay_shuffle",
                    "Auto-play from the draw pile requires a shuffle whose live RNG state is not available.",
                    sourceId);
                return false;
            }
            ShuffleDiscardIntoDraw(state);
        }

        var card = state.DrawPile[0];
        state.DrawPile.RemoveAt(0);
        if (forceExhaust)
            card = card with { Destination = CardDestination.Exhaust };

        if (!IsCardPlayableNow(state, card))
        {
            MoveAutoPlayedCardToResultPile(state, card);
            return true;
        }

        string? targetId = null;
        if (card.Target == TargetKind.Enemy)
            targetId = ResolveAutoPlayEnemyTarget(state, card.ModelId);

        state.Hand.Add(card);
        var result = PlayCard(state, card, targetId, null, isAutoPlay: true);
        CopyStateInto(state, result);
        return true;
    }

    private bool AutoPlayRandomDrawCard(MutableCombatState state, bool attackOnly, string sourceId)
    {
        var candidates = state.DrawPile
            .Where(card => !attackOnly || IsCardType(card, "Attack", "攻击"))
            .ToList();
        if (candidates.Count == 0) return false;

        var playable = candidates.Where(card => IsCardPlayableNow(state, card)).ToList();
        var pool = playable.Count > 0 ? playable : candidates;
        if (!TryStableShuffleFirst(state, pool, sourceId, out var selected) || selected is null)
            return false;
        return AutoPlayExistingCardFromPile(state, state.DrawPile, selected.InstanceId, sourceId);
    }

    private bool AutoPlayRandomHandAttack(MutableCombatState state, string sourceId)
    {
        var candidates = state.Hand
            .Where(card => IsCardType(card, "Attack", "攻击") && !card.ExhaustAtTurnEnd)
            .ToList();
        if (candidates.Count == 0) return false;
        if (state.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } stream)
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_autoplay_selection",
                "Random hand attack auto-play requires the Shuffle RNG stream.",
                sourceId);
            return false;
        }

        var selected = candidates[stream.NextInt(candidates.Count, out stream)];
        state.RngStreams = state.RngStreams.With(stream);
        return AutoPlayExistingCardFromPile(state, state.Hand, selected.InstanceId, sourceId);
    }

    private static bool TryStableShuffleFirst(
        MutableCombatState state,
        List<CardState> cards,
        string sourceId,
        out CardState? selected)
    {
        selected = null;
        if (cards.Count == 0) return true;
        if (state.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } stream)
        {
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_autoplay_selection",
                "Random draw-pile auto-play requires the Shuffle RNG stream.",
                sourceId);
            return false;
        }

        for (var index = cards.Count - 1; index > 0; index--)
        {
            var swap = stream.NextInt(index + 1, out stream);
            (cards[index], cards[swap]) = (cards[swap], cards[index]);
        }
        state.RngStreams = state.RngStreams.With(stream);
        selected = cards[0];
        return true;
    }

    private void AutoPlayMarkedCardsFromPile(MutableCombatState state, List<CardState> pile, string marker)
    {
        var instanceIds = pile
            .Where(card => card.Effects.Any(effect =>
                effect.Kind == EffectKind.AutoPlaySelfFromPile && effect.StatusId == marker))
            .Select(static card => card.InstanceId)
            .ToArray();
        foreach (var instanceId in instanceIds)
        {
            if (state.Player.Hp <= 0m) break;
            AutoPlayExistingCardFromPile(state, pile, instanceId, marker);
        }
    }

    private void AutoPlayEtherealFromExhaust(MutableCombatState state, string sourceId)
    {
        var instanceIds = state.ExhaustPile
            .Where(static card => card.ExhaustAtTurnEnd)
            .Select(static card => card.InstanceId)
            .ToArray();
        foreach (var instanceId in instanceIds)
        {
            if (state.Player.Hp <= 0m) break;
            AutoPlayExistingCardFromPile(state, state.ExhaustPile, instanceId, sourceId);
        }
    }

    private void AutoPlayShivsFromExhaust(
        MutableCombatState state,
        string? targetId,
        bool upgrade,
        string sourceId)
    {
        var instanceIds = state.ExhaustPile
            .Where(IsShiv)
            .Select(static card => card.InstanceId)
            .ToArray();
        foreach (var instanceId in instanceIds)
        {
            if (state.Player.Hp <= 0m) break;
            var index = state.ExhaustPile.FindIndex(card => card.InstanceId == instanceId);
            if (index < 0) continue;
            if (upgrade)
                state.ExhaustPile[index] = UpgradeShiv(state.ExhaustPile[index]);
            AutoPlayExistingCardFromPile(state, state.ExhaustPile, instanceId, sourceId, targetId);
        }
    }

    private static CardState UpgradeShiv(CardState card)
    {
        if (card.ModelId.EndsWith("_UPGRADE", StringComparison.OrdinalIgnoreCase))
            return card;
        var effects = card.Effects
            .Select(static effect => effect.Kind == EffectKind.Damage
                ? effect with { Amount = effect.Amount + 2m }
                : effect)
            .ToImmutableArray();
        return card with
        {
            ModelId = card.ModelId + "_UPGRADE",
            Name = card.Name.EndsWith("+", StringComparison.Ordinal) ? card.Name : card.Name + "+",
            Effects = effects
        };
    }

    private bool AutoPlayExistingCardFromPile(
        MutableCombatState state,
        List<CardState> pile,
        string instanceId,
        string sourceId,
        string? forcedTargetId = null)
    {
        var index = pile.FindIndex(card => card.InstanceId == instanceId);
        if (index < 0) return false;
        var card = pile[index];
        pile.RemoveAt(index);
        if (!IsCardPlayableNow(state, card))
        {
            MoveAutoPlayedCardToResultPile(state, card);
            return true;
        }

        string? targetId = null;
        if (card.Target == TargetKind.Enemy)
            targetId = forcedTargetId ?? ResolveAutoPlayEnemyTarget(state, card.ModelId);
        state.Hand.Add(card);
        var result = PlayCard(state, card, targetId, null, isAutoPlay: true);
        CopyStateInto(state, result);
        return true;
    }

    private static string? ResolveAutoPlayEnemyTarget(MutableCombatState state, string sourceId)
    {
        var alive = state.Enemies.Where(static enemy => enemy.IsAlive).ToArray();
        if (alive.Length == 0) return null;
        if (state.RngStreams.Get(RngSnapshotSet.CombatTargets) is { IsKnown: true } stream)
        {
            var index = stream.NextInt(alive.Length, out stream);
            state.RngStreams = state.RngStreams.With(stream);
            return alive[index].Id;
        }
        if (alive.Length == 1) return alive[0].Id;
        AddUncalculable(
            state,
            PredictionRiskReason.UnsupportedRandomSource,
            "uncalculable_autoplay_target",
            "Auto-play needs the CombatTargets RNG stream to resolve an enemy target.",
            sourceId);
        return null;
    }

    private static void MoveAutoPlayedCardToResultPile(MutableCombatState state, CardState card)
    {
        switch (card.Destination)
        {
            case CardDestination.Exhaust:
                state.ExhaustPile.Add(card);
                ApplyExhaustTriggers(state, [card]);
                break;
            case CardDestination.DrawPileTop:
                state.DrawPile.Insert(0, card);
                break;
            case CardDestination.Hand:
                state.Hand.Add(card);
                break;
            case CardDestination.Discard:
                state.DiscardPile.Add(card);
                break;
        }
    }

    private static void CopyStateInto(MutableCombatState target, MutableCombatState source)
    {
        target.Player = source.Player;
        Replace(target.Enemies, source.Enemies);
        Replace(target.Hand, source.Hand);
        Replace(target.DrawPile, source.DrawPile);
        Replace(target.DiscardPile, source.DiscardPile);
        Replace(target.ExhaustPile, source.ExhaustPile);
        Replace(target.Potions, source.Potions);
        Replace(target.Orbs, source.Orbs);
        Replace(target.Restrictions, source.Restrictions);
        Replace(target.Risks, source.Risks);
        target.OrbCapacity = source.OrbCapacity;
        target.RngState = source.RngState;
        target.RngStreams = source.RngStreams;
        target.PotionCostSpent = source.PotionCostSpent;
        target.DamageDealt = source.DamageDealt;
        target.EnemiesKilled = source.EnemiesKilled;
        target.HpLostSinceSnapshot = source.HpLostSinceSnapshot;
        target.AttacksPlayedSinceSnapshot = source.AttacksPlayedSinceSnapshot;
        target.AttacksPlayedBeforeTurn = source.AttacksPlayedBeforeTurn;
        target.SkillsPlayedSinceSnapshot = source.SkillsPlayedSinceSnapshot;
        target.CardsPlayedSinceSnapshot = source.CardsPlayedSinceSnapshot;
        target.CardPlaysFinishedSinceSnapshot = source.CardPlaysFinishedSinceSnapshot;
        target.ShivsPlayedSinceSnapshot = source.ShivsPlayedSinceSnapshot;
        target.EtherealCardsPlayedSinceSnapshot = source.EtherealCardsPlayedSinceSnapshot;
        target.CardsDrawnSinceSnapshot = source.CardsDrawnSinceSnapshot;
        target.CardsDrawnThisTurn = source.CardsDrawnThisTurn;
        target.CardsGeneratedSinceSnapshot = source.CardsGeneratedSinceSnapshot;
        target.StatusCardsDrawnSinceSnapshot = source.StatusCardsDrawnSinceSnapshot;
        target.CardsExhaustedSinceSnapshot = source.CardsExhaustedSinceSnapshot;
        target.CardsDiscardedSinceSnapshot = source.CardsDiscardedSinceSnapshot;
        target.DamageReceivedEventsSinceSnapshot = source.DamageReceivedEventsSinceSnapshot;
        target.UnmovableBlockGainsThisTurn = source.UnmovableBlockGainsThisTurn;
        target.StatusCardsDrawnBeforeTurn = source.StatusCardsDrawnBeforeTurn;
        target.CardsExhaustedBeforeTurn = source.CardsExhaustedBeforeTurn;
        target.CardsDiscardedBeforeTurn = source.CardsDiscardedBeforeTurn;
        target.ShivsPlayedBeforeTurn = source.ShivsPlayedBeforeTurn;

        static void Replace<T>(List<T> destination, List<T> replacement)
        {
            destination.Clear();
            destination.AddRange(replacement);
        }
    }

    private static void Draw(
        MutableCombatState state,
        int count,
        bool fromHandDraw = false,
        List<CardState>? drawnCards = null)
    {
        if (GetStatusAmount(state.Player.Statuses, "CANNOT_DRAW") > 0) return;
        for (var draw = 0; draw < count; draw++)
        {
            if (state.Hand.Count >= 10) return;
            if (state.DrawPile.Count == 0)
            {
                if (state.DiscardPile.Count == 0) return;
                if (state.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } && state.RngState == 0)
                    AddUncalculable(
                        state,
                        PredictionRiskReason.UnsupportedRandomSource,
                        "uncalculable_shuffle_order",
                        "Drawing requires a shuffle whose live RNG state is not available.",
                        RngSnapshotSet.Shuffle);
                ShuffleDiscardIntoDraw(state);
            }
            var card = state.DrawPile[0];
            state.DrawPile.RemoveAt(0);
            if (!card.CostsX && card.EnergyCostChangeOnDraw != 0)
                card = card with
                {
                    EnergyCost = Math.Max(0, card.EnergyCost + card.EnergyCostChangeOnDraw)
                };
            if (card.DamageChangeOnDraw != 0m)
                card = card with { CombatDamageBonus = card.CombatDamageBonus + card.DamageChangeOnDraw };
            state.Hand.Add(card);
            drawnCards?.Add(card);
            state.CardsDrawnSinceSnapshot++;
            if (!fromHandDraw)
                state.CardsDrawnThisTurn++;
            if (card.CardType is "Status" or "状态")
            {
                state.StatusCardsDrawnSinceSnapshot++;
                ApplyIterationTrigger(state);
            }
            if (card.ExhaustAtTurnEnd)
                ApplyPagestormTrigger(state);
            ApplyDrawTriggers(state, fromHandDraw);
            if (card.EnergyChangeOnDraw != 0)
                state.Player = state.Player with { Energy = Math.Max(0, state.Player.Energy + card.EnergyChangeOnDraw) };
        }
    }

    private static void DrawUntilNonAttack(MutableCombatState state, List<CardState>? drawnCards)
    {
        var captured = drawnCards ?? [];
        while (state.Hand.Count < 10)
        {
            var before = captured.Count;
            Draw(state, 1, drawnCards: captured);
            if (captured.Count == before) return;
            if (!IsCardType(captured[^1], "Attack", "攻击")) return;
        }
    }

    private static void DrawToHandSize(
        MutableCombatState state,
        int targetSize,
        bool retainDrawnThisTurn,
        List<CardState>? drawnCards)
    {
        var captured = drawnCards ?? [];
        var before = captured.Count;
        Draw(state, Math.Max(0, targetSize - state.Hand.Count), drawnCards: captured);
        if (!retainDrawnThisTurn) return;
        foreach (var drawn in captured.Skip(before))
        {
            var index = state.Hand.FindIndex(card => card.InstanceId == drawn.InstanceId);
            if (index < 0 || state.Hand[index].RetainAtTurnEnd) continue;
            state.Hand[index] = state.Hand[index] with
            {
                RetainAtTurnEnd = true,
                TemporaryRetainAtTurnEnd = true
            };
        }
    }

    private static void ApplyIterationTrigger(MutableCombatState state)
    {
        var amount = GetStatusAmount(state.Player.Statuses, "ITERATION_DRAW");
        if (amount <= 0) return;
        var statusCardsDrawn = state.StatusCardsDrawnBeforeTurn + state.StatusCardsDrawnSinceSnapshot;
        if (statusCardsDrawn != 1) return;
        Draw(state, amount);
    }

    private static void ApplyPagestormTrigger(MutableCombatState state)
    {
        var amount = GetStatusAmount(state.Player.Statuses, "PAGESTORM_DRAW");
        if (amount > 0) Draw(state, amount);
    }

    private void ApplyHellraiserDrawTriggers(MutableCombatState state, IReadOnlyList<CardState> drawnCards)
    {
        if (GetStatusAmount(state.Player.Statuses, "HELLRAISER") <= 0) return;
        var used = GetStatusAmount(state.Player.Statuses, "HELLRAISER_AUTOPLAYS_THIS_TURN");
        foreach (var drawn in drawnCards)
        {
            if (used >= 9 || state.Player.Hp <= 0m) break;
            if (!drawn.Name.Contains("打击", StringComparison.Ordinal) &&
                !drawn.ModelId.Contains("STRIKE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!state.Hand.Any(card => card.InstanceId == drawn.InstanceId)) continue;
            state.Player = state.Player with
            {
                Statuses = state.Player.Statuses.SetItem(
                    "HELLRAISER_AUTOPLAYS_THIS_TURN",
                    new StatusState("HELLRAISER_AUTOPLAYS_THIS_TURN", ++used))
            };
            AutoPlayExistingCardFromPile(state, state.Hand, drawn.InstanceId, "HELLRAISER");
        }
    }

    private static void ApplyDrawTriggers(MutableCombatState state, bool fromHandDraw)
    {
        var poison = GetStatusAmount(state.Player.Statuses, "TRIGGER_CARD_DRAWN_ALL_POISON");
        if (poison > 0)
        {
            foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
            {
                ApplyEnemyStatus(
                    state,
                    enemy.Id,
                    new StatusState(
                        "POISON",
                        poison,
                        IsDebuff: true,
                        FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("POISON", poison)));
            }
        }

        var speedsterDamage = fromHandDraw
            ? 0
            : GetStatusAmount(state.Player.Statuses, "TRIGGER_NON_HAND_DRAW_ALL_DAMAGE");
        if (speedsterDamage > 0)
            foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                DamageEnemy(state, enemy.Id, speedsterDamage);

        var reward = GetStatusAmount(state.Player.Statuses, "AUTOMATION_DRAW_ENERGY");
        if (reward <= 0) return;
        var cardsLeft = GetStatusAmount(state.Player.Statuses, "AUTOMATION_DRAWS_LEFT");
        cardsLeft = cardsLeft <= 0 ? 10 : cardsLeft;
        cardsLeft--;
        if (cardsLeft <= 0)
        {
            if (GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0)
                state.Player = state.Player with { Energy = state.Player.Energy + reward };
            cardsLeft = 10;
        }
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses.SetItem(
                "AUTOMATION_DRAWS_LEFT",
                new StatusState("AUTOMATION_DRAWS_LEFT", cardsLeft))
        };
    }

    private static void ApplyEnergySpentTriggers(MutableCombatState state, int energySpent)
    {
        if (energySpent <= 0) return;
        var reward = GetStatusAmount(state.Player.Statuses, "ORBIT_ENERGY_REBATE");
        if (reward <= 0) return;
        var remainder = Math.Max(0, GetStatusAmount(state.Player.Statuses, "ORBIT_ENERGY_REMAINDER"));
        var accumulated = remainder + energySpent;
        var triggerCount = accumulated / 4;
        remainder = accumulated % 4;
        state.Player = state.Player with
        {
            Energy = state.Player.Energy + (GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0
                ? triggerCount * reward
                : 0),
            Statuses = state.Player.Statuses.SetItem(
                "ORBIT_ENERGY_REMAINDER",
                new StatusState("ORBIT_ENERGY_REMAINDER", remainder))
        };
    }

    private static void ShuffleDiscardIntoDraw(MutableCombatState state)
    {
        state.DrawPile.AddRange(state.DiscardPile);
        state.DiscardPile.Clear();
        var stream = state.RngStreams.Get(RngSnapshotSet.Shuffle);
        for (var index = state.DrawPile.Count - 1; index > 0; index--)
        {
            int swap;
            if (stream is { IsKnown: true })
            {
                swap = stream.NextInt(index + 1, out stream);
                state.RngStreams = state.RngStreams.With(stream);
            }
            else
            {
                var rngState = state.RngState;
                swap = (int)(NextRandom(ref rngState) % (ulong)(index + 1));
                state.RngState = rngState;
            }
            (state.DrawPile[index], state.DrawPile[swap]) = (state.DrawPile[swap], state.DrawPile[index]);
        }
        ApplyRelicShuffleTriggers(state);
    }

    private static void ShuffleReboot(MutableCombatState state)
    {
        state.DrawPile.AddRange(state.DiscardPile);
        state.DiscardPile.Clear();
        if (state.RngStreams.Get(RngSnapshotSet.Shuffle) is not { IsKnown: true } && state.RngState == 0)
            AddUncalculable(
                state,
                PredictionRiskReason.UnsupportedRandomSource,
                "uncalculable_reboot_shuffle",
                "Reboot requires a full-pile shuffle whose live RNG state is not available.",
                RngSnapshotSet.Shuffle);
        var stream = state.RngStreams.Get(RngSnapshotSet.Shuffle);
        for (var index = state.DrawPile.Count - 1; index > 0; index--)
        {
            int swap;
            if (stream is { IsKnown: true })
            {
                swap = stream.NextInt(index + 1, out stream);
                state.RngStreams = state.RngStreams.With(stream);
            }
            else
            {
                var rngState = state.RngState;
                swap = (int)(NextRandom(ref rngState) % (ulong)(index + 1));
                state.RngState = rngState;
            }
            (state.DrawPile[index], state.DrawPile[swap]) = (state.DrawPile[swap], state.DrawPile[index]);
        }
        ApplyRelicShuffleTriggers(state);
    }

    private static ulong NextRandom(ref ulong state)
    {
        state = state == 0 ? 0x9E3779B97F4A7C15UL : state;
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }

    private static decimal ModifyPlayerAttackDamage(
        PlayerState player,
        CreatureState enemy,
        decimal damage,
        bool includeVigor = true,
        bool poweredAttack = false,
        decimal lethalityPercent = 0m,
        string? sourceId = null)
    {
        var strength = GetStatusAmount(player.Statuses, "STRENGTH");
        var vigor = includeVigor ? GetStatusAmount(player.Statuses, "VIGOR") : 0;
        var weak = GetStatusAmount(player.Statuses, "WEAK") > 0 ? 0.75m : 1m;
        var debilitate = GetStatusAmount(enemy.Statuses, "DEBILITATE") > 0;
        var vulnerableMultiplier = debilitate ? 2.0m : 1.5m;
        var vulnerable = GetStatusAmount(enemy.Statuses, "VULNERABLE") > 0
            ? vulnerableMultiplier + (poweredAttack
                ? GetStatusAmount(enemy.Statuses, "BONUS_VULNERABLE_POWERED_ATTACK_DAMAGE_PERCENT") / 100m
                : 0m)
            : 1m;
        var weakTargetBonus = poweredAttack && GetStatusAmount(enemy.Statuses, "WEAK") > 0
            ? 1m + GetStatusAmount(player.Statuses, "BONUS_WEAK_TARGET_POWERED_ATTACK_DAMAGE_PERCENT") / 100m
            : 1m;
        var lethality = poweredAttack && lethalityPercent > 0m ? 1m + lethalityPercent / 100m : 1m;
        var hangMultiplier = poweredAttack &&
                             sourceId is not null &&
                             (sourceId.Equals("HANG", StringComparison.OrdinalIgnoreCase) ||
                              sourceId.Equals("HANG_UPGRADE", StringComparison.OrdinalIgnoreCase))
            ? Math.Max(1m, GetStatusAmount(enemy.Statuses, "HANG_DAMAGE_MULTIPLIER"))
            : 1m;
        var modified = Math.Max(0m, decimal.Floor(
            (damage + strength + vigor) * weak * vulnerable * weakTargetBonus * lethality * hangMultiplier));
        var doubleDamage = poweredAttack && (GetStatusAmount(player.Statuses, "DOUBLE_DAMAGE") > 0 ||
                                             GetStatusAmount(player.Statuses, "KINGS_BLADE_DOUBLE_DAMAGE") > 0 ||
                                             GetStatusAmount(player.Statuses, "PEN_NIB_ACTIVE") > 0);
        return doubleDamage
            ? modified * 2m
            : modified;
    }

    private static void ConsumeVigor(MutableCombatState state)
    {
        if (GetStatusAmount(state.Player.Statuses, "VIGOR") <= 0) return;
        state.Player = state.Player with { Statuses = state.Player.Statuses.Remove("VIGOR") };
        // Vigor is fully consumed by the first attack; keep the structured
        // PowerState list in sync with the removed status.
        SyncPowerState(state, "VIGOR", "player", null, "VIGOR_CONSUMED");
    }

    private static decimal ModifyEnemyAttackDamage(
        CreatureState enemy,
        PlayerState player,
        decimal damage,
        bool poweredAttack = false)
    {
        var strength = GetStatusAmount(enemy.Statuses, "STRENGTH") -
                       GetStatusAmount(enemy.Statuses, "TEMP_STRENGTH_LOSS");
        var debilitate = GetStatusAmount(enemy.Statuses, "DEBILITATE") > 0;
        var weak = GetStatusAmount(enemy.Statuses, "WEAK") > 0 ? (debilitate ? 0.5m : 0.75m) : 1m;
        var vulnerable = GetStatusAmount(player.Statuses, "VULNERABLE") > 0 ? 1.5m : 1m;
        var colossus = poweredAttack &&
                       GetStatusAmount(enemy.Statuses, "VULNERABLE") > 0 &&
                       GetStatusAmount(player.Statuses, "REDUCE_VULNERABLE_ATTACK_DAMAGE") > 0
            ? 0.5m
            : 1m;
        return Math.Max(0m, decimal.Floor((damage + strength) * weak * vulnerable * colossus));
    }

    private static void GainPlayerBlock(
        MutableCombatState state,
        decimal amount,
        bool applyDexterity = false,
        bool fromCard = false)
    {
        if (fromCard && state.Player.Statuses.ContainsKey("NO_BLOCK_FROM_CARDS")) return;
        var adjusted = Math.Max(0m, amount + (applyDexterity
            ? GetStatusAmount(state.Player.Statuses, "DEXTERITY")
            : 0));
        if (fromCard && GetStatusAmount(state.Player.Statuses, "FRAIL") > 0)
            adjusted = decimal.Floor(adjusted * 0.75m);
        if (adjusted <= 0m) return;
        var doublings = Math.Max(0, GetStatusAmount(state.Player.Statuses, "DOUBLE_BLOCK_GAINED"));
        var multiplier = doublings == 0 ? 1m : (decimal)Math.Pow(2d, doublings);
        var unmovable = fromCard ? GetStatusAmount(state.Player.Statuses, "UNMOVABLE") : 0;
        if (state.UnmovableBlockGainsThisTurn < unmovable)
        {
            multiplier *= 2m;
            state.UnmovableBlockGainsThisTurn++;
        }
        state.Player = state.Player with { Block = state.Player.Block + adjusted * multiplier };
        if (state.Player.Statuses.TryGetValue("TRIGGER_BLOCK_GAINED_RANDOM_DAMAGE", out var juggernaut) &&
            juggernaut.Amount > 0)
            DamageRandomEnemy(
                state,
                juggernaut.Amount,
                "TRIGGER_BLOCK_GAINED_RANDOM_DAMAGE",
                juggernaut.RandomSource);
    }

    private static int GetStatusAmount(ImmutableDictionary<string, StatusState> statuses, string id) =>
        statuses.TryGetValue(id, out var status) ? status.Amount : 0;

    private static void ApplyPlayerHpLossTriggers(
        MutableCombatState state,
        CardHpLossTriggerContext? cardContext = null)
    {
        if (state.Player.Hp <= 0m) return;
        var damage = GetStatusAmount(state.Player.Statuses, "TRIGGER_PLAYER_HP_LOST_ALL_ENEMY_DAMAGE");
        if (damage > 0)
            foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                DamageEnemy(state, enemy.Id, damage);

        var strength = GetStatusAmount(state.Player.Statuses, "TRIGGER_PLAYER_HP_LOST_STRENGTH");
        if (strength <= 0) return;
        if (cardContext is not null)
            cardContext.PendingRuptureStrength += strength;
        else
            GainPlayerStrength(state, strength);
    }

    private static void ApplyCardPlayedTriggers(
        MutableCombatState state,
        int afterimageBlock,
        int monologueStrength,
        IReadOnlyList<(string EnemyId, int Amount)> oblivionDoom,
        int rageBlock,
        StatusState? serpentForm,
        int panacheCardsLeft,
        StatusState? haunt,
        int powerLightning,
        IReadOnlyList<(string EnemyId, int Amount)> strangleHpLoss,
        int powerEnergy,
        bool attackPlayed,
        string cardModelId)
    {
        if (afterimageBlock > 0)
            GainPlayerBlock(state, afterimageBlock);

        if (monologueStrength > 0)
        {
            GainPlayerStrength(state, monologueStrength);
            state.Player = state.Player with
            {
                Statuses = AddStatus(
                    state.Player.Statuses,
                    new StatusState("MONOLOGUE_STRENGTH_APPLIED", monologueStrength, Duration: 1))
            };
        }

        foreach (var trigger in oblivionDoom)
        {
            if (!IsEnemyAlive(state, trigger.EnemyId)) continue;
            ApplyLocalEnemyStatus(
                state,
                trigger.EnemyId,
                new StatusState(
                    "DOOM",
                    trigger.Amount,
                    IsDebuff: true,
                    FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DOOM", trigger.Amount)));
        }

        if (rageBlock > 0)
            GainPlayerBlock(state, rageBlock);

        if (serpentForm is { Amount: > 0 })
            DamageRandomEnemy(
                state,
                serpentForm.Amount,
                "TRIGGER_CARD_PLAYED_RANDOM_DAMAGE",
                serpentForm.RandomSource);

        if (panacheCardsLeft > 0)
        {
            var next = panacheCardsLeft - 1;
            if (next <= 0)
            {
                var panacheDamage = GetStatusAmount(state.Player.Statuses, "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE");
                if (panacheDamage > 0)
                    foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
                        DamageEnemy(state, enemy.Id, panacheDamage);
                next = 5;
            }
            state.Player = state.Player with
            {
                Statuses = state.Player.Statuses.SetItem("PANACHE_CARDS_LEFT", new StatusState("PANACHE_CARDS_LEFT", next))
            };
            SyncPowerState(state, "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE", "player", "player", "PANACHE");
        }

        if (haunt is { Amount: > 0 })
            LoseHpRandomEnemy(
                state,
                haunt.Amount,
                "TRIGGER_SOUL_PLAYED_RANDOM_HP_LOSS",
                haunt.RandomSource);

        if (powerLightning > 0)
            ChannelOrbs(
                state,
                new EffectSpec(EffectKind.ChannelOrbs, powerLightning, "LIGHTNING"),
                0,
                "TRIGGER_POWER_PLAYED_LIGHTNING");

        foreach (var trigger in strangleHpLoss)
        {
            if (IsEnemyAlive(state, trigger.EnemyId))
                LoseEnemyHp(state, trigger.EnemyId, trigger.Amount);
        }

        if (powerEnergy > 0 && GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0)
            state.Player = state.Player with { Energy = state.Player.Energy + powerEnergy };

        if (cardModelId.Equals("SOUL", StringComparison.OrdinalIgnoreCase))
        {
            var summon = GetStatusAmount(state.Player.Statuses, "DEVOUR_LIFE");
            if (summon > 0) SummonCompanion(state, summon);
        }

        if (attackPlayed &&
            state.AttacksPlayedBeforeTurn + state.AttacksPlayedSinceSnapshot >= 5 &&
            GetStatusAmount(state.Player.Statuses, "PALE_BLUE_DOT") > 0 &&
            GetStatusAmount(state.Player.Statuses, "PALE_BLUE_DOT_ACTIVATED_THIS_TURN") <= 0)
        {
            var drawAmount = GetStatusAmount(state.Player.Statuses, "PALE_BLUE_DOT");
            state.Player = state.Player with
            {
                Statuses = state.Player.Statuses
                    .SetItem("PALE_BLUE_DOT_ACTIVATED_THIS_TURN", new StatusState(
                        "PALE_BLUE_DOT_ACTIVATED_THIS_TURN", 1, Duration: 1))
                    .SetItem("SCHEDULED_DRAW", new StatusState(
                        "SCHEDULED_DRAW", drawAmount, Duration: 1, FutureValuePerTurn: drawAmount * 2m))
            };
        }
    }

    private static void ApplyReturnToHandListeners(MutableCombatState state)
    {
        var skillsPlayed = state.HistoryBeforeSnapshot.SkillsPlayedThisTurn + state.SkillsPlayedSinceSnapshot;
        if (skillsPlayed <= 0) return;
        var listeners = state.DiscardPile.Concat(state.DrawPile).Concat(state.ExhaustPile)
            .SelectMany(card => card.Effects
                .Where(static effect => effect.Kind == EffectKind.ReturnSelfToHandAfterSkills)
                .Select(effect => (Card: card, Interval: Math.Max(1, (int)effect.Amount)))
                .Where(item => skillsPlayed % item.Interval == 0))
            .Select(static item => item.Card.InstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var instanceId in listeners)
        {
            var card = RemoveCardFromPiles(state, instanceId);
            if (card is not null) state.Hand.Add(card);
        }
    }

    private static CardState? RemoveCardFromPiles(MutableCombatState state, string instanceId)
    {
        foreach (var pile in new[] { state.DiscardPile, state.DrawPile, state.ExhaustPile })
        {
            var index = pile.FindIndex(card => card.InstanceId == instanceId);
            if (index < 0) continue;
            var card = pile[index];
            pile.RemoveAt(index);
            return card;
        }
        return null;
    }

    private static void RemoveTemporaryMonologueStrength(MutableCombatState state)
    {
        var applied = GetStatusAmount(state.Player.Statuses, "MONOLOGUE_STRENGTH_APPLIED");
        var statuses = state.Player.Statuses
            .Remove("TRIGGER_CARD_PLAYED_TEMP_STRENGTH")
            .Remove("MONOLOGUE_STRENGTH_APPLIED");
        if (applied > 0 && statuses.TryGetValue("STRENGTH", out var strength))
        {
            var remaining = strength.Amount - applied;
            statuses = remaining == 0
                ? statuses.Remove("STRENGTH")
                : statuses.SetItem("STRENGTH", strength with
                {
                    Amount = remaining,
                    FutureValuePerTurn = StatusValuation.IntrinsicFutureValue("STRENGTH", remaining)
                });
        }
        state.Player = state.Player with { Statuses = statuses };
    }

    private static void GainPlayerStrength(MutableCombatState state, int amount)
    {
        if (amount <= 0) return;
        var strength = new StatusState(
            "STRENGTH",
            amount,
            FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("STRENGTH", amount));
        state.Player = state.Player with { Statuses = AddStatus(state.Player.Statuses, strength) };
        // Rupture-style triggers grant Strength outside ApplyStatus; keep the
        // structured PowerState list in sync with the granted status.
        SyncPowerState(state, "TRIGGER_PLAYER_HP_LOST_STRENGTH", "player", "player", "TRIGGER_PLAYER_HP_LOST_STRENGTH");
        SyncPowerState(state, "STRENGTH", "player", "player", "TRIGGER_PLAYER_HP_LOST_STRENGTH_APPLIED");
    }

    private sealed class CardHpLossTriggerContext
    {
        public int PendingRuptureStrength { get; set; }
    }

    private static void DamagePlayer(
        MutableCombatState state,
        decimal damage,
        string? attackerId = null,
        bool poweredAttack = false)
    {
        if (GetStatusAmount(state.Player.Statuses, "INTANGIBLE") > 0) damage = Math.Min(1m, damage);
        var blocked = Math.Min(state.Player.Block, damage);
        var unblocked = damage - blocked;
        var gambitTriggered = poweredAttack && attackerId is not null && unblocked > 0m &&
                              GetStatusAmount(state.Player.Statuses, "THE_GAMBIT") > 0m;
        if (unblocked > 0m)
        {
            state.DamageReceivedEventsSinceSnapshot++;
            ApplyRelicDamageReceivedTriggers(state);
        }
        unblocked = ResolvePlayerHpLoss(state, unblocked);
        state.Player = state.Player with
        {
            Block = state.Player.Block - blocked,
            Hp = Math.Max(0m, state.Player.Hp - unblocked)
        };
        if (gambitTriggered)
        {
            state.Player = state.Player with
            {
                Hp = 0m,
                Statuses = state.Player.Statuses.Remove("THE_GAMBIT")
            };
        }
        state.HpLostSinceSnapshot += unblocked;
        if (unblocked > 0m && state.Player.Statuses.TryGetValue("PLATED_ARMOR", out var plated))
        {
            state.Player = state.Player with
            {
                Statuses = plated.Amount <= 1
                    ? state.Player.Statuses.Remove("PLATED_ARMOR")
                    : state.Player.Statuses.SetItem("PLATED_ARMOR", plated with { Amount = plated.Amount - 1 })
            };
        }
        var reflect = GetStatusAmount(state.Player.Statuses, "REFLECT_BLOCKED_ATTACK_DAMAGE");
        if (poweredAttack && attackerId is not null && reflect > 0m && blocked > 0m && IsEnemyAlive(state, attackerId))
            DamageEnemy(state, attackerId, blocked);
        var thorns = GetStatusAmount(state.Player.Statuses, "THORNS");
        if (attackerId is not null && thorns > 0 && state.Enemies.Any(enemy => enemy.Id == attackerId && enemy.IsAlive))
            DamageEnemy(state, attackerId, thorns);
        var flameBarrier = GetStatusAmount(state.Player.Statuses, "TRIGGER_POWERED_ATTACK_RECEIVED_DAMAGE");
        if (poweredAttack && state.Player.Hp > 0m && attackerId is not null && flameBarrier > 0 && IsEnemyAlive(state, attackerId))
            DamageEnemy(state, attackerId, flameBarrier);
    }

    private static void ApplyRelicAttackTriggers(MutableCombatState state)
    {
        var nunchakuIndex = state.Relics.FindIndex(static r => r.Id.Equals("NUNCHAKU", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (nunchakuIndex >= 0)
        {
            var nunchaku = state.Relics[nunchakuIndex];
            var cur = (nunchaku.Counter ?? 0) + 1;
            if (cur >= 10)
            {
                if (GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0)
                    state.Player = state.Player with { Energy = state.Player.Energy + 1 };
                cur = 0;
            }
            state.Relics[nunchakuIndex] = nunchaku with { Counter = cur };
        }
    }

    private static void ApplyRelicTurnEndTriggers(MutableCombatState state)
    {
        var hasOrichalcum = state.Relics.Any(static r => r.Id.Equals("ORICHALCUM", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (hasOrichalcum && state.Player.Block == 0m)
        {
            GainPlayerBlock(state, 6);
        }
    }

    private static void ApplyRelicShuffleTriggers(MutableCombatState state)
    {
        var sundialIndex = state.Relics.FindIndex(static r => r.Id.Equals("SUNDIAL", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (sundialIndex >= 0)
        {
            var sd = state.Relics[sundialIndex];
            var cur = ((sd.Counter ?? 0) + 1) % 3;
            if (cur == 0)
            {
                if (GetStatusAmount(state.Player.Statuses, "CANNOT_GAIN_ENERGY") <= 0)
                    state.Player = state.Player with { Energy = state.Player.Energy + 2 };
            }
            state.Relics[sundialIndex] = sd with { Counter = cur };
        }
    }

    private static void ApplyRelicDamageReceivedTriggers(MutableCombatState state)
    {
        var puzzleIndex = state.Relics.FindIndex(static r => r.Id.Equals("CENTENNIAL_PUZZLE", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp);
        if (puzzleIndex >= 0)
        {
            var pz = state.Relics[puzzleIndex];
            if (pz.UsesThisCombat == 0)
            {
                state.Relics[puzzleIndex] = pz with { UsesThisCombat = 1 };
                Draw(state, 3);
            }
        }
    }

    private static bool IsEnemyAlive(MutableCombatState state, string enemyId) =>
        state.Enemies.Any(enemy => enemy.Id == enemyId && enemy.IsAlive);

    private static decimal ResolvePlayerHpLoss(MutableCombatState state, decimal requestedLoss)
    {
        var actualLoss = Math.Min(state.Player.Hp, Math.Max(0m, requestedLoss));
        if (actualLoss <= 0m) return 0m;
        if (state.Player.Statuses.TryGetValue("BUFFER", out var buffer) && buffer.Amount > 0)
        {
            state.Player = state.Player with
            {
                Statuses = buffer.Amount <= 1
                    ? state.Player.Statuses.Remove("BUFFER")
                    : state.Player.Statuses.SetItem("BUFFER", buffer with { Amount = buffer.Amount - 1 })
            };
            return 0m;
        }

        if (state.Relics.Any(static r => r.Id.Equals("TUNGSTEN_ROD", StringComparison.OrdinalIgnoreCase) && r.IsEnabled && !r.IsUsedUp))
        {
            actualLoss = Math.Max(0m, actualLoss - 1m);
        }

        return actualLoss;
    }

    private static void ApplyTurnEndHpLoss(MutableCombatState state, decimal requestedLoss)
    {
        var actualLoss = ResolvePlayerHpLoss(state, requestedLoss);
        if (actualLoss <= 0m) return;
        state.Player = state.Player with { Hp = state.Player.Hp - actualLoss };
        state.HpLostSinceSnapshot += actualLoss;
        ApplyPlayerHpLossTriggers(state);
    }

    private static decimal DealPlayerDamage(
        MutableCombatState state,
        string enemyId,
        decimal damage,
        bool poweredAttack,
        bool cardSource,
        Action<decimal>? onTotalDamage = null)
    {
        var result = DamageEnemyResult(state, enemyId, damage);
        var dealt = result.HpLoss;
        onTotalDamage?.Invoke(result.TotalDamage);
        if (poweredAttack && cardSource)
        {
            if (dealt > 0m)
            {
                var multiplier = GetStatusAmount(state.Player.Statuses, "TRIGGER_ATTACK_DAMAGE_DOOM");
                if (multiplier > 0)
                {
                    var doom = (int)Math.Floor(dealt * multiplier);
                    if (doom > 0)
                        ApplyLocalEnemyStatus(state, enemyId, new StatusState(
                            "DOOM",
                            doom,
                            IsDebuff: true,
                            FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("DOOM", doom)));
                }

                var poison = GetStatusAmount(state.Player.Statuses, "TRIGGER_ATTACK_UNBLOCKED_POISON");
                if (poison > 0)
                    ApplyEnemyStatus(state, enemyId, new StatusState(
                        "POISON",
                        poison,
                        IsDebuff: true,
                        FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("POISON", poison)));
            }

            var temporaryStrengthLoss = GetStatusAmount(state.Player.Statuses, "TRIGGER_ATTACK_TEMP_STRENGTH_LOSS");
            if (temporaryStrengthLoss > 0)
                ApplyEnemyStatus(state, enemyId, new StatusState(
                    "TEMP_STRENGTH_LOSS",
                    temporaryStrengthLoss,
                    Duration: 1,
                    IsDebuff: true,
                    FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("TEMP_STRENGTH_LOSS", temporaryStrengthLoss)));
        }
        return dealt;
    }

    private static decimal DamageEnemy(MutableCombatState state, string enemyId, decimal damage)
        => DamageEnemyResult(state, enemyId, damage).HpLoss;

    private static DamageResult DamageEnemyResult(MutableCombatState state, string enemyId, decimal damage)
    {
        var enemy = state.Enemies.First(item => item.Id == enemyId);
        var blocked = Math.Min(enemy.Block, damage);
        var hpLoss = Math.Min(enemy.Hp, damage - blocked);
        var updated = enemy with
        {
            Block = enemy.Block - blocked,
            Hp = Math.Max(0m, enemy.Hp - hpLoss)
        };
        state.DamageDealt += hpLoss;
        if (enemy.Hp > 0m && updated.Hp <= 0m)
        {
            state.EnemiesKilled++;
            ApplyCreatureDeathCostListeners(state);
        }
        SetEnemy(state, updated);
        return new DamageResult(hpLoss, blocked + hpLoss);
    }

    private readonly record struct DamageResult(decimal HpLoss, decimal TotalDamage);

    private static void LoseEnemyHp(MutableCombatState state, string enemyId, decimal amount)
    {
        var enemy = state.Enemies.First(item => item.Id == enemyId);
        var hpLoss = Math.Min(enemy.Hp, Math.Max(0m, amount));
        var updated = enemy with { Hp = Math.Max(0m, enemy.Hp - hpLoss) };
        state.DamageDealt += hpLoss;
        if (enemy.Hp > 0m && updated.Hp <= 0m)
        {
            state.EnemiesKilled++;
            ApplyCreatureDeathCostListeners(state);
        }
        SetEnemy(state, updated);
    }

    private static void KillAllDoomedEnemies(MutableCombatState state)
    {
        for (var index = 0; index < state.Enemies.Count; index++)
        {
            var enemy = state.Enemies[index];
            var doom = GetStatusAmount(enemy.Statuses, "DOOM");
            if (!enemy.IsAlive || doom <= 0 || enemy.Hp > doom) continue;
            state.Enemies[index] = enemy with { Hp = 0m };
            state.EnemiesKilled++;
            ApplyCreatureDeathCostListeners(state);
        }
    }

    private static void ForTargets(MutableCombatState state, TargetKind kind, string? targetId, Action<CreatureState> apply)
    {
        switch (kind)
        {
            case TargetKind.Enemy:
                var target = state.Enemies.FirstOrDefault(enemy => enemy.Id == targetId && enemy.IsAlive);
                if (target is not null) apply(target);
                else AddUncalculable(state, PredictionRiskReason.StateCaptureIncomplete, "uncalculable_target", "Required enemy target is unavailable.", targetId);
                break;
            case TargetKind.AllEnemies:
                foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray()) apply(enemy);
                break;
            case TargetKind.None:
            case TargetKind.Self:
            case TargetKind.Companion:
                break;
        }
    }

    private static void ApplyStatus(
        MutableCombatState state,
        EffectSpec effect,
        TargetKind targetKind,
        string? targetId,
        string sourceId,
        bool enemyActs)
    {
        if (effect.StatusId is not { Length: > 0 } statusId)
        {
            AddUncalculable(state, PredictionRiskReason.MethodMirrorIncomplete, "uncalculable_status_id", "Status effect has no status id.", sourceId);
            return;
        }
        var futureValue = effect.FutureValuePerTurn == 0m
            ? StatusValuation.IntrinsicFutureValue(effect.StatusId, effect.Amount)
            : effect.FutureValuePerTurn;
        var status = new StatusState(
            statusId,
            (int)effect.Amount,
            effect.Duration,
            effect.IsDebuff,
            futureValue,
            effect.GeneratedCard,
            effect.RandomSource);
        if (statusId == "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE" &&
            !state.Player.Statuses.ContainsKey("PANACHE_CARDS_LEFT"))
            state.Player = state.Player with
            {
                Statuses = state.Player.Statuses.SetItem("PANACHE_CARDS_LEFT", new StatusState("PANACHE_CARDS_LEFT", Math.Max(1, effect.XBonus)))
            };
        if (enemyActs || targetKind == TargetKind.Enemy || targetKind == TargetKind.AllEnemies)
        {
            var actualTarget = enemyActs ? sourceId : targetId;
            var actualKind = enemyActs ? TargetKind.Enemy : targetKind;
            var applications = 0;
            ForTargets(state, actualKind, actualTarget, enemy =>
            {
                if (ApplyEnemyStatus(state, enemy.Id, status))
                {
                    applications++;
                    SyncPowerState(state, status.Id, enemy.Id, enemyActs ? sourceId : "player", sourceId);
                }
            });
            if (!enemyActs && statusId == "VULNERABLE" && status.Amount > 0 && applications > 0)
            {
                var draw = GetStatusAmount(state.Player.Statuses, "TRIGGER_VULNERABLE_APPLIED_DRAW");
                if (draw > 0) Draw(state, draw * applications);
            }
            if (!enemyActs && statusId == "DOOM" && applications > 0)
                MarkDoomAppliedThisTurn(state);
        }
        else
        {
            if (!ApplyPlayerStatus(state, status)) return;
            SyncPowerState(state, status.Id, "player", enemyActs ? sourceId : "player", sourceId);
            if (statusId is "SHIV_ALL_ENEMIES" or "SHIV_RETAIN")
                ApplyShivModifiers(state);
            if (statusId is "FOCUS" or "TEMP_FOCUS")
                AdjustOrbFocus(state, status.Amount);
            if (statusId == "MAX_ENERGY_DELTA")
            {
                state.Player = state.Player with
                {
                    MaxEnergy = Math.Max(1, state.Player.MaxEnergy + status.Amount)
                };
            }
            if (statusId == "AUTOMATION_DRAW_ENERGY" &&
                !state.Player.Statuses.ContainsKey("AUTOMATION_DRAWS_LEFT"))
            {
                state.Player = state.Player with
                {
                    Statuses = state.Player.Statuses.Add(
                        "AUTOMATION_DRAWS_LEFT",
                        new StatusState("AUTOMATION_DRAWS_LEFT", 10))
                };
            }
        }
    }

    private static void SyncPowerState(
        MutableCombatState state,
        string statusId,
        string ownerId,
        string? applierId,
        string sourceId)
    {
        var livePowerId = LivePowerIdFor(statusId, sourceId);
        var statuses = ownerId == "player"
            ? state.Player.Statuses
            : state.Enemies.FirstOrDefault(enemy => enemy.Id == ownerId)?.Statuses;
        var index = state.Powers.FindIndex(power =>
            power.OwnerId == ownerId && CanonicalPowerId(power.Id) == CanonicalPowerId(livePowerId));
        if (statuses is null || !statuses.TryGetValue(statusId, out var status) || status.Amount <= 0)
        {
            if (index >= 0) state.Powers.RemoveAt(index);
            return;
        }

        if (index >= 0)
        {
            state.Powers[index] = state.Powers[index] with
            {
                Amount = status.Amount,
                ApplierId = applierId ?? state.Powers[index].ApplierId,
                DynamicVars = LivePowerDynamicVars(state, statusId, ownerId),
                SourceId = sourceId,
                Support = SemanticSupportStatus.SimulatorSupported,
            };
            return;
        }

        state.Powers.Add(new PowerState(
            livePowerId.EndsWith("_POWER", StringComparison.Ordinal) ? livePowerId : $"{livePowerId}_POWER",
            ownerId,
            applierId,
            status.Amount,
            DynamicVars: LivePowerDynamicVars(state, statusId, ownerId),
            TriggerPhases: KnownPowerTriggerPhases(statusId),
            SourceId: sourceId,
            Support: SemanticSupportStatus.SimulatorSupported,
            Evidence: EvidenceLevel.HeuristicInferred,
            SourceVersion: "v0.111.0"));
    }

    private static string LivePowerIdFor(string statusId, string sourceId)
    {
        var normalizedStatus = CanonicalPowerId(statusId);
        var normalizedSource = CanonicalPowerId(sourceId);
        if (normalizedSource == "TURN_START_STRENGTH" || normalizedStatus == "TURN_START_STRENGTH")
            return "DEMON_FORM_POWER";
        if (normalizedSource == "TRIGGER_PLAYER_HP_LOST_STRENGTH" || normalizedStatus == "TRIGGER_PLAYER_HP_LOST_STRENGTH")
            return "RUPTURE_POWER";
        if (normalizedSource == "AFTERIMAGE" || normalizedSource == "TRIGGER_CARD_PLAYED_BLOCK" || normalizedStatus == "TRIGGER_CARD_PLAYED_BLOCK")
            return "AFTERIMAGE_POWER";
        if (normalizedSource == "ACCURACY" || normalizedStatus == "SHIV_DAMAGE_BONUS")
            return "ACCURACY_POWER";
        if (normalizedSource == "PANACHE" || normalizedStatus == "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE")
            return "PANACHE_POWER";
        return normalizedStatus.EndsWith("_POWER", StringComparison.Ordinal)
            ? normalizedStatus
            : $"{normalizedStatus}_POWER";
    }

    private static string CanonicalPowerId(string id)
    {
        var normalized = id.ToUpperInvariant();
        return normalized.EndsWith("_POWER", StringComparison.Ordinal)
            ? normalized[..^6]
            : normalized;
    }

    private static ImmutableDictionary<string, string> LivePowerDynamicVars(MutableCombatState state, string statusId, string ownerId)
    {
        var canonical = CanonicalPowerId(statusId);
        if (canonical is "PANACHE" or "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE")
        {
            var left = GetStatusAmount(state.Player.Statuses, "PANACHE_CARDS_LEFT");
            return ImmutableDictionary<string, string>.Empty.Add("CardsLeft", left.ToString());
        }
        return KnownPowerDynamicVars(statusId);
    }

    private static ImmutableDictionary<string, string> KnownPowerDynamicVars(string statusId) =>
        CanonicalPowerId(statusId) switch
        {
            "VULNERABLE" => ImmutableDictionary<string, string>.Empty.Add("DamageIncrease", "1.5"),
            "WEAK" => ImmutableDictionary<string, string>.Empty.Add("DamageDecrease", "0.75"),
            "BARRICADE" => ImmutableDictionary<string, string>.Empty.Add("ApplierName", "0"),
            "PLATING" => ImmutableDictionary<string, string>.Empty.Add("Decrement", "1"),
            _ => ImmutableDictionary<string, string>.Empty,
        };

    private static ImmutableArray<string> KnownPowerTriggerPhases(string statusId) =>
        CanonicalPowerId(statusId) switch
        {
            "VULNERABLE" or "WEAK" or "STRENGTH" => ["Damage"],
            "POISON" or "DOOM" => ["TurnEnd"],
            _ => [],
        };

    private static bool ApplyPlayerStatus(MutableCombatState state, StatusState status)
    {
        var statuses = state.Player.Statuses;
        if (status.IsDebuff && statuses.TryGetValue("ARTIFACT", out var artifact) && artifact.Amount > 0)
        {
            statuses = artifact.Amount <= 1
                ? statuses.Remove("ARTIFACT")
                : statuses.SetItem("ARTIFACT", artifact with { Amount = artifact.Amount - 1 });
            state.Player = state.Player with { Statuses = statuses };
            return false;
        }

        state.Player = state.Player with { Statuses = AddStatus(statuses, status) };
        return true;
    }

    private static bool ApplyEnemyStatus(MutableCombatState state, string enemyId, StatusState status)
    {
        var enemy = state.Enemies.FirstOrDefault(candidate => candidate.Id == enemyId && candidate.IsAlive);
        if (enemy is null) return false;

        var statuses = enemy.Statuses;
        if (status.IsDebuff && statuses.TryGetValue("ARTIFACT", out var artifact) && artifact.Amount > 0)
        {
            statuses = artifact.Amount <= 1
                ? statuses.Remove("ARTIFACT")
                : statuses.SetItem("ARTIFACT", artifact with { Amount = artifact.Amount - 1 });
            SetEnemy(state, enemy with { Statuses = statuses });
            return false;
        }

        if (status.Id == "HANG_DAMAGE_MULTIPLIER")
        {
            var currentAmount = GetStatusAmount(statuses, status.Id);
            var nextAmount = currentAmount <= 0
                ? Math.Max(2, status.Amount)
                : Math.Min(999_999_999, currentAmount + Math.Max(2, currentAmount));
            status = status with
            {
                Amount = nextAmount,
                FutureValuePerTurn = StatusValuation.IntrinsicFutureValue(status.Id, nextAmount)
            };
            statuses = statuses.SetItem(status.Id, status);
        }
        else
        {
            statuses = AddStatus(statuses, status);
        }

        SetEnemy(state, enemy with { Statuses = statuses });
        return true;
    }

    private static bool ApplyLocalEnemyStatus(MutableCombatState state, string enemyId, StatusState status)
    {
        var applied = ApplyEnemyStatus(state, enemyId, status);
        if (applied && status.Id == "DOOM")
            MarkDoomAppliedThisTurn(state);
        return applied;
    }

    private static void MarkDoomAppliedThisTurn(MutableCombatState state)
    {
        state.Player = state.Player with
        {
            Statuses = state.Player.Statuses.SetItem(
                "DOOM_APPLIED_THIS_TURN",
                new StatusState("DOOM_APPLIED_THIS_TURN", 1, Duration: 1))
        };
    }

    private static void RemoveStatus(
        MutableCombatState state,
        EffectSpec effect,
        TargetKind targetKind,
        string? targetId,
        string sourceId,
        bool enemyActs)
    {
        if (effect.StatusId is not { Length: > 0 } statusId) return;
        if (enemyActs || targetKind == TargetKind.Enemy || targetKind == TargetKind.AllEnemies)
        {
            var actualTarget = enemyActs ? sourceId : targetId;
            var actualKind = enemyActs ? TargetKind.Enemy : targetKind;
            ForTargets(state, actualKind, actualTarget, enemy =>
                SetEnemy(state, enemy with { Statuses = enemy.Statuses.Remove(statusId) }));
        }
        else
        {
            state.Player = state.Player with { Statuses = state.Player.Statuses.Remove(statusId) };
        }
    }

    private static ImmutableDictionary<string, StatusState> AddStatus(
        ImmutableDictionary<string, StatusState> statuses,
        StatusState addition)
    {
        if (!statuses.TryGetValue(addition.Id, out var current)) return statuses.Add(addition.Id, addition);
        if (addition.Id == "NO_BLOCK_FROM_CARDS")
        {
            return statuses.SetItem(addition.Id, current with
            {
                Amount = 1,
                Duration = Math.Max(0, current.Duration) + Math.Max(0, addition.Duration),
                IsDebuff = true,
                FutureValuePerTurn = Math.Min(current.FutureValuePerTurn, addition.FutureValuePerTurn)
            });
        }
        if (addition.Id == "DOUBLE_DAMAGE")
        {
            return statuses.SetItem(addition.Id, current with
            {
                Amount = 1,
                Duration = Math.Max(0, current.Duration) + Math.Max(0, addition.Duration),
                FutureValuePerTurn = Math.Max(current.FutureValuePerTurn, addition.FutureValuePerTurn)
            });
        }
        if (addition.Id == "SHADOW_STEP_PENDING")
        {
            return statuses.SetItem(addition.Id, current with
            {
                Amount = 1,
                Duration = Math.Max(0, current.Duration) + Math.Max(0, addition.Duration),
                FutureValuePerTurn = Math.Max(current.FutureValuePerTurn, addition.FutureValuePerTurn)
            });
        }
        return statuses.SetItem(addition.Id, current with
        {
            Amount = current.Amount + addition.Amount,
            Duration = Math.Max(current.Duration, addition.Duration),
            FutureValuePerTurn = current.FutureValuePerTurn + addition.FutureValuePerTurn,
            GeneratedCard = current.GeneratedCard ?? addition.GeneratedCard,
            RandomSource = current.RandomSource ?? addition.RandomSource
        });
    }

    private static ImmutableDictionary<string, StatusState> TickDurations(
        ImmutableDictionary<string, StatusState> statuses)
    {
        var result = statuses;
        foreach (var pair in statuses)
        {
            if (pair.Value.Duration < 0) continue;
            if (pair.Value.Duration <= 1) result = result.Remove(pair.Key);
            else result = result.SetItem(pair.Key, pair.Value with { Duration = pair.Value.Duration - 1 });
        }
        return result;
    }

    private static void ApplyScheduledStatuses(MutableCombatState state)
    {
        foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
            TriggerEnemyPoison(state, enemy.Id);
    }

    private static void ApplyOutbreak(MutableCombatState state, EffectSpec effect, string sourceId)
    {
        var poison = Math.Max(0, (int)effect.Amount);
        if (poison <= 0)
        {
            AddUncalculable(state, PredictionRiskReason.MethodMirrorIncomplete,
                "uncalculable_outbreak_poison", "Outbreak has no positive Poison value.", sourceId);
            return;
        }

        foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
            ApplyEnemyStatus(
                state,
                enemy.Id,
                new StatusState(
                    "POISON",
                    poison,
                    IsDebuff: true,
                    FutureValuePerTurn: StatusValuation.IntrinsicFutureValue("POISON", poison)));
        foreach (var enemy in state.Enemies.Where(static enemy => enemy.IsAlive).ToArray())
            TriggerEnemyPoison(state, enemy.Id);
    }

    private static void TriggerEnemyPoison(MutableCombatState state, string enemyId)
    {
        var index = state.Enemies.FindIndex(enemy => enemy.Id == enemyId);
        if (index < 0 || !state.Enemies[index].IsAlive ||
            !state.Enemies[index].Statuses.TryGetValue("POISON", out var poison) || poison.Amount <= 0)
            return;

        var triggerCount = Math.Min(
            poison.Amount,
            1 + Math.Max(0, GetStatusAmount(state.Player.Statuses, "ACCELERANT")));
        for (var trigger = 0; trigger < triggerCount; trigger++)
        {
            if (!IsEnemyAlive(state, enemyId)) break;
            DamageEnemy(state, enemyId, poison.Amount);
            var refreshed = state.Enemies[index];
            state.Enemies[index] = refreshed with
            {
                Statuses = poison.Amount <= 1
                    ? refreshed.Statuses.Remove("POISON")
                    : refreshed.Statuses.SetItem("POISON", poison with { Amount = poison.Amount - 1 })
            };
            poison = poison with { Amount = poison.Amount - 1 };
            SyncPowerState(state, "POISON", enemyId, "player", "POISON_TICK");
        }
    }

    private static void SetEnemy(MutableCombatState state, CreatureState enemy)
    {
        var index = state.Enemies.FindIndex(item => item.Id == enemy.Id);
        if (index >= 0) state.Enemies[index] = enemy;
    }

    private static MutableCombatState AddUncalculable(MutableCombatState state, string code, string message, string? source)
    {
        AddUncalculable(state, PredictionRiskReason.MethodNotMirrored, code, message, source);
        return state;
    }

    private static void AddEstimated(
        MutableCombatState state,
        PredictionRiskReason reason,
        string code,
        string message,
        string? source)
    {
        state.Restrictions.Add(new RestrictionReason(code, message, source));
        state.Risks.Add(new RiskEvent(reason, PredictionRiskSeverity.Estimated, message, source));
    }

    private static void AddUncalculable(
        MutableCombatState state,
        PredictionRiskReason reason,
        string code,
        string message,
        string? source)
    {
        state.Restrictions.Add(new RestrictionReason(code, message, source));
        state.Risks.Add(new RiskEvent(reason, PredictionRiskSeverity.Uncalculable, message, source));
    }
}
