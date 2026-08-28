using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace STS2BestChoice.Core.Semantics;

public enum CardSemanticKind
{
    Damage,
    DynamicDamage,
    Outbreak,
    LoseEnemyHp,
    Block,
    DynamicBlock,
    Draw,
    GainEnergy,
    ModifyMaxHp,
    ApplyStatus,
    MultiplyStatus,
    DiscardCards,
    ExhaustCards,
    RandomExhaustCards,
    ExhaustNonAttacksAndBlock,
    DiscardHandThenDrawSame,
    DiscardHandAndGenerate,
    Reboot,
    GenerateCard,
    AutoPlayCard,
    SelectCard,
    CopySelectedCard,
    LoseHp,
    Heal,
    ChannelOrb,
    EvokeOrb,
    TriggerOrbPassive,
    ModifyOrbCapacity,
    Summon,
    Forge,
    MoveCard,
    ModifyCost,
    ModifyCardDamage,
    ModifyCardBlock,
    ModifyHandCosts,
    ClearEnemyBlockAndArtifact,
    PlayRestriction,
    KillAllDoomedEnemies,
    CompanionDamage,
    UpgradeCard,
    TransformCards,
    Keyword
}

public enum SemanticTarget
{
    None,
    Self,
    SelectedEnemy,
    AllEnemies,
    RandomEnemy,
    Hand,
    DrawPile,
    DiscardPile,
    ExhaustPile,
    Companion
}

public sealed record CardSemanticOperation(
    CardSemanticKind Kind,
    decimal Amount,
    SemanticTarget Target,
    string SourceClause,
    string? Id = null,
    int Repeat = 1,
    bool All = false,
    string Timing = "immediate",
    string? Trigger = null,
    string? Condition = null,
    bool RepeatByEnergySpent = false,
    bool RepeatByOrbCount = false,
    int XBonus = 0,
    bool AmountByEnergySpent = false,
    bool AmountByAliveEnemyCount = false,
    bool AmountByDistinctOrbTypes = false,
    bool AmountByHandAttackCount = false,
    bool AmountByCardsDrawnThisTurn = false,
    bool RepeatByHistoryCounter = false,
    bool RepeatByKillCount = false,
    bool AmountByTargetVulnerableStacks = false,
    bool RepeatByExhaustedCount = false,
    bool RepeatByStarsGained = false,
    string? RandomSource = null,
    string? Duration = null,
    string? DynamicAmountId = null);

public sealed record CardSemanticCompilation(
    ImmutableArray<CardSemanticOperation> Operations,
    ImmutableArray<string> UnparsedClauses,
    bool IsFullyStructured,
    bool IsImmediatelyExecutable,
    bool IsSimulatorExecutable);

public static partial class CardTextSemanticCompiler
{
    private static readonly HashSet<string> ExecutableKeywords = new(StringComparer.Ordinal)
    {
        "消耗", "固有", "保留", "虚无", "永恒", "不能被打出", "奇巧", "（抽0张牌）"
    };

    public static CardSemanticCompilation Compile(string? description)
    {
        var operations = ImmutableArray.CreateBuilder<CardSemanticOperation>();
        var unparsed = ImmutableArray.CreateBuilder<string>();
        foreach (var clause in SplitClauses(description))
        {
            var normalized = NormalizeClause(clause);
            if (TryCompileClause(normalized, out var operation)) operations.Add(operation! with { SourceClause = clause });
            else if (TryCompileCompoundClause(normalized, out var compound))
                operations.AddRange(compound.Select(item => item with { SourceClause = clause }));
            else unparsed.Add(clause);
        }

        var result = FuseSelectedHandModifiers(FuseDamageModifiers(operations.ToImmutable()));
        var fullyStructured = unparsed.Count == 0;
        var executable = fullyStructured && result.All(IsImmediateExecutable);
        var simulatorExecutable = fullyStructured && result.All(IsSimulatorExecutable);
        return new CardSemanticCompilation(result, unparsed.ToImmutable(), fullyStructured, executable, simulatorExecutable);
    }

    private static ImmutableArray<CardSemanticOperation> FuseDamageModifiers(
        ImmutableArray<CardSemanticOperation> operations)
    {
        var fused = ImmutableArray.CreateBuilder<CardSemanticOperation>(operations.Length);
        foreach (var operation in operations)
        {
            if (operation is
                {
                    Kind: CardSemanticKind.DynamicDamage,
                    Id: "OSTY_CURRENT_HP_DAMAGE" or "OSTY_MAX_HP_DAMAGE" or "OSTY_ATTACK_CARDS_IN_DECK_DAMAGE"
                } dynamicCompanion &&
                fused.Count > 0 && fused[^1] is { Kind: CardSemanticKind.CompanionDamage } companionDamage)
            {
                fused[^1] = companionDamage with
                {
                    DynamicAmountId = dynamicCompanion.Id,
                    XBonus = (int)dynamicCompanion.Amount,
                    SourceClause = $"{companionDamage.SourceClause}；{dynamicCompanion.SourceClause}"
                };
                continue;
            }
            if (operation is { Kind: CardSemanticKind.Damage, Id: "DOUBLE_X_AT_LEAST_4" } &&
                fused.Count > 0 && fused[^1] is
                {
                    Kind: CardSemanticKind.Damage,
                    RepeatByEnergySpent: true
                } xDamage)
            {
                fused[^1] = xDamage with
                {
                    Condition = "ENERGY_SPENT_AT_LEAST:4",
                    SourceClause = $"{xDamage.SourceClause}；{operation.SourceClause}"
                };
                continue;
            }
            if (operation is { Kind: CardSemanticKind.DynamicDamage, Id: { } id } &&
                id.StartsWith("BONUS_", StringComparison.Ordinal) && fused.Count > 0 &&
                fused[^1] is { Kind: CardSemanticKind.Damage } damage)
            {
                fused[^1] = operation with
                {
                    Amount = damage.Amount,
                    Id = id[6..],
                    XBonus = (int)operation.Amount,
                    SourceClause = $"{damage.SourceClause}；{operation.SourceClause}"
                };
                continue;
            }
            if (operation is
                {
                    Kind: CardSemanticKind.DynamicDamage,
                    Id: "CARDS_DRAWN_THIS_TURN",
                    XBonus: > 0
                } drawnBonus && fused.Count > 0 && fused[^1] is { Kind: CardSemanticKind.Damage } drawnDamage)
            {
                fused[^1] = drawnDamage with
                {
                    XBonus = drawnBonus.XBonus,
                    AmountByCardsDrawnThisTurn = true,
                    SourceClause = $"{drawnDamage.SourceClause}；{drawnBonus.SourceClause}"
                };
                continue;
            }
            if (operation is { Kind: CardSemanticKind.Damage, Id: "COPY_PREVIOUS_HIT" } &&
                fused.Count > 0 && fused[^1] is { Kind: CardSemanticKind.Damage or CardSemanticKind.DynamicDamage } previous)
            {
                fused.Add(previous with
                {
                    Repeat = 1,
                    Condition = operation.Condition,
                    SourceClause = operation.SourceClause
                });
                continue;
            }
            if (operation is { Kind: CardSemanticKind.Damage, Id: "REPEAT_PREVIOUS_ATTACK" } &&
                fused.Count > 0 && fused[^1] is { Kind: CardSemanticKind.Damage } repeatedAttack)
            {
                fused[^1] = repeatedAttack with
                {
                    Repeat = Math.Max(1, operation.Repeat),
                    Condition = operation.Condition,
                    SourceClause = $"{repeatedAttack.SourceClause}；{operation.SourceClause}"
                };
                continue;
            }
            if (operation is { Kind: CardSemanticKind.Damage, Id: "REPEAT_ON_KILL" } &&
                fused.Count > 0 && fused[^1] is { Kind: CardSemanticKind.Damage } killRepeatDamage)
            {
                fused[^1] = killRepeatDamage with
                {
                    RepeatByKillCount = true,
                    SourceClause = $"{killRepeatDamage.SourceClause}；{operation.SourceClause}"
                };
                continue;
            }
            fused.Add(operation);
        }
        return fused.ToImmutable();
    }

    private static ImmutableArray<CardSemanticOperation> FuseSelectedHandModifiers(
        ImmutableArray<CardSemanticOperation> operations)
    {
        var fused = ImmutableArray.CreateBuilder<CardSemanticOperation>(operations.Length);
        foreach (var operation in operations)
        {
            if (operation is
                {
                    Kind: CardSemanticKind.ModifyCost,
                    Id: "SELECTED_HAND_COST_DELTA"
                } && fused.Count > 0 && fused[^1] is
                {
                    Kind: CardSemanticKind.SelectCard,
                    Id: "HAND_ANY_ADD_REPLAY"
                } selected)
            {
                fused[^1] = selected with
                {
                    XBonus = (int)operation.Amount,
                    SourceClause = $"{selected.SourceClause}；{operation.SourceClause}"
                };
                continue;
            }
            if (operation is { Kind: CardSemanticKind.CopySelectedCard, Id: "SELECTED_TO_HAND" } &&
                fused.Count > 0 && fused[^1] is
                {
                    Kind: CardSemanticKind.SelectCard,
                    Id: "HAND_ATTACK_OR_POWER"
                } copySelection)
            {
                fused[^1] = copySelection with
                {
                    Amount = operation.Amount,
                    Id = "HAND_ATTACK_OR_POWER_COPY",
                    SourceClause = $"{copySelection.SourceClause}；{operation.SourceClause}"
                };
                continue;
            }
            fused.Add(operation);
        }
        return fused.ToImmutable();
    }

    private static IEnumerable<string> SplitClauses(string? description) =>
        (description ?? string.Empty)
        .Replace("<br>", "。", StringComparison.OrdinalIgnoreCase)
        .Replace("<br/>", "。", StringComparison.OrdinalIgnoreCase)
        .Split(['。', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeClause(string clause)
    {
        var normalized = clause.Trim().TrimStart('并', '且').Trim()
            .Replace("， ", "，", StringComparison.Ordinal)
            .Replace("[能量]", "能量", StringComparison.Ordinal)
            .Replace("16px|link=能量", "能量", StringComparison.Ordinal)
            .Replace("16px|link=", "能量", StringComparison.Ordinal);
        var bareEnergy = HistoricalBareEnergyGain().Match(normalized);
        if (bareEnergy.Success)
        {
            var count = bareEnergy.Groups["icons"].Value.Length / "16px|link=".Length;
            normalized = "获得" + string.Concat(Enumerable.Repeat("能量", count));
        }
        var numericEnergy = HistoricalNumericEnergyGain().Match(normalized);
        if (numericEnergy.Success)
            normalized = $"获得{numericEnergy.Groups["amount"].Value}能量";
        return normalized;
    }

    private static bool TryCompileClause(string clause, out CardSemanticOperation? operation)
    {
        operation = null;
        Match wrapper;
        if (clause == "本回合你获得的格挡值翻倍")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "DOUBLE_BLOCK_GAINED", Timing: "this_turn");
            return true;
        }
        if (clause is "所有的攻击伤害翻倍" or "所有攻击伤害翻倍")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "NEXT_TURN_DOUBLE_DAMAGE", Timing: "next_turn");
            return true;
        }
        if (clause is "在下个回合，你所有的攻击伤害翻倍" or "在下个回合，你所有攻击伤害翻倍")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "NEXT_TURN_DOUBLE_DAMAGE", Timing: "next_turn");
            return true;
        }
        if (clause == "每当你的攻击造成伤害时，同时给予等量的灾厄")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "TRIGGER_ATTACK_DAMAGE_DOOM", Timing: "persistent_combat");
            return true;
        }
        if (clause == "在本回合中，有易伤状态的敌人对你造成的伤害降低50%")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "REDUCE_VULNERABLE_ATTACK_DAMAGE", Timing: "this_turn");
            return true;
        }
        if (clause == "在本回合将你格挡掉的攻击伤害反弹给攻击者")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "REFLECT_BLOCKED_ATTACK_DAMAGE", Timing: "this_turn");
            return true;
        }
        if (clause is "对虚弱状态的敌人，你所造成的攻击伤害翻倍" or
            "对虚弱状态的敌人，造成的攻击伤害翻倍" or
            "有虚弱状态的敌人，所受的攻击伤害翻倍")
        {
            operation = new(CardSemanticKind.ApplyStatus, 50, SemanticTarget.Self, clause,
                Id: "BONUS_WEAK_TARGET_POWERED_ATTACK_DAMAGE_PERCENT", Timing: "persistent_combat");
            return true;
        }
        if (clause == "翻倍你每回合第一次从卡牌中获得的格挡")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "UNMOVABLE", Timing: "persistent_combat");
            return true;
        }
        var shivDamageBonus = ShivDamageBonus().Match(clause);
        if (shivDamageBonus.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(shivDamageBonus), SemanticTarget.Self, clause,
                Id: "SHIV_DAMAGE_BONUS", Timing: "persistent_combat");
            return true;
        }
        if (clause == "小刀现在会攻击所有敌人")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "SHIV_ALL_ENEMIES", Timing: "persistent_combat");
            return true;
        }
        if (clause == "小刀获得保留")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "SHIV_RETAIN", Timing: "persistent_combat");
            return true;
        }
        var accelerant = AccelerantExtraTriggers().Match(clause);
        if (accelerant.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(accelerant), SemanticTarget.Self, clause,
                Id: "ACCELERANT", Timing: "persistent_combat");
            return true;
        }
        var outbreak = OutbreakClause().Match(clause);
        if (outbreak.Success)
        {
            operation = new(
                CardSemanticKind.Outbreak,
                Value(outbreak),
                SemanticTarget.AllEnemies,
                clause,
                Id: "OUTBREAK",
                Repeat: int.Parse(outbreak.Groups["repeat"].Value));
            return true;
        }
        if (JugglingThirdAttackCopy().IsMatch(clause))
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "JUGGLING", Timing: "persistent_combat", Trigger: "THIRD_ATTACK_PLAYED");
            return true;
        }
        var firstShivBonus = FirstShivDamageBonus().Match(clause);
        if (firstShivBonus.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(firstShivBonus), SemanticTarget.Self, clause,
                Id: "FIRST_SHIV_DAMAGE_BONUS", Timing: "persistent_combat");
            return true;
        }
        var returnAfterSkills = ReturnSelfAfterSkills().Match(clause);
        if (returnAfterSkills.Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(returnAfterSkills), SemanticTarget.None, clause,
                Id: "RETURN_SELF_TO_HAND_AFTER_SKILLS", Timing: "persistent_combat", Trigger: "SKILL_PLAYED");
            return true;
        }
        var crueltyBonus = CrueltyBonus().Match(clause);
        if (crueltyBonus.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(crueltyBonus), SemanticTarget.AllEnemies, clause,
                Id: "BONUS_VULNERABLE_POWERED_ATTACK_DAMAGE_PERCENT", Timing: "persistent_combat");
            return true;
        }
        if (clause == "让所有“吊杀”牌对这名敌人造成的伤害翻倍")
        {
            operation = new(CardSemanticKind.ApplyStatus, 2, SemanticTarget.SelectedEnemy, clause,
                Id: "HANG_DAMAGE_MULTIPLIER", Timing: "persistent_combat");
            return true;
        }
        if (clause == "丢弃抽到的牌中耗能不为0能量的牌")
        {
            operation = new(CardSemanticKind.Keyword, 0, SemanticTarget.Hand, clause,
                Id: "DISCARD_DRAWN_NONZERO_COST");
            return true;
        }
        var noBlockFromCards = NoBlockFromCards().Match(clause);
        if (noBlockFromCards.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(noBlockFromCards), SemanticTarget.Self, clause,
                Id: "NO_BLOCK_FROM_CARDS");
            return true;
        }
        if (clause == "只有在手牌中每一张牌都是攻击牌时才能被打出")
        {
            operation = new(CardSemanticKind.PlayRestriction, 1, SemanticTarget.Hand, clause,
                Id: "HAND_ALL_ATTACKS");
            return true;
        }
        if (clause == "只有当你的抽牌堆中没有牌时才能打出")
        {
            operation = new(CardSemanticKind.PlayRestriction, 1, SemanticTarget.DrawPile, clause,
                Id: "DRAW_PILE_EMPTY");
            return true;
        }
        var handPlayLimit = HandCardPlayLimit().Match(clause);
        if (handPlayLimit.Success)
        {
            operation = new(CardSemanticKind.PlayRestriction, Value(handPlayLimit), SemanticTarget.Hand, clause,
                Id: "GLOBAL_CARD_PLAY_LIMIT_WHILE_IN_HAND", Timing: "while_in_hand");
            return true;
        }
        var persistentPlayLimit = PersistentCardPlayLimit().Match(clause);
        if (persistentPlayLimit.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(persistentPlayLimit), SemanticTarget.Self, clause,
                Id: "CARD_PLAY_LIMIT", Timing: "persistent_combat");
            return true;
        }
        var everyFiveCardsDamage = EveryFiveCardsAllDamage().Match(clause);
        if (everyFiveCardsDamage.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(everyFiveCardsDamage), SemanticTarget.Self, clause,
                Id: "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE", Timing: "persistent_combat", XBonus: 5);
            return true;
        }
        var soulPlayedRandomHpLoss = SoulPlayedRandomEnemyHpLoss().Match(clause);
        if (soulPlayedRandomHpLoss.Success)
        {
            operation = new(CardSemanticKind.LoseEnemyHp, Value(soulPlayedRandomHpLoss), SemanticTarget.RandomEnemy, clause,
                Timing: "persistent_combat", Trigger: "SOUL_PLAYED", RandomSource: "CombatTargets");
            return true;
        }
        if (clause == "每当你抽到名字中有“打击”的牌时，对一名随机敌人打出这张牌" ||
            clause == "每当你抽到名字中有\"打击\"的牌时，对一名随机敌人打出这张牌")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "HELLRAISER", Timing: "persistent_combat");
            return true;
        }
        var listenerCostReduction = ExhaustListenerSelfCostReduction().Match(clause);
        if (listenerCostReduction.Success)
        {
            operation = new(CardSemanticKind.ModifyCost, -Value(listenerCostReduction), SemanticTarget.None, clause,
                Id: "SELF_LISTENER_COST_DELTA", Timing: "persistent_combat", Trigger: "CARD_EXHAUSTED");
            return true;
        }
        if (CreatureDeathSelfCostReduction().IsMatch(clause))
        {
            operation = new(CardSemanticKind.ModifyCost, -1, SemanticTarget.None, clause,
                Id: "SELF_DEATH_COST_DELTA", Timing: "persistent_combat", Trigger: "CREATURE_DIED");
            return true;
        }
        var selfCombatDamageGrowth = SelfCombatDamageGrowth().Match(clause);
        if (selfCombatDamageGrowth.Success)
        {
            operation = new(CardSemanticKind.ModifyCardDamage, Value(selfCombatDamageGrowth), SemanticTarget.None, clause,
                Id: "SELF", Duration: "combat");
            return true;
        }
        var selfDrawDamageGrowth = SelfDrawDamageGrowth().Match(clause);
        if (selfDrawDamageGrowth.Success)
        {
            operation = new(CardSemanticKind.ModifyCardDamage, Value(selfDrawDamageGrowth), SemanticTarget.None, clause,
                Id: "SELF", Trigger: "SELF_DRAWN", Duration: "combat");
            return true;
        }
        var deathMarchDrawDamage = DeathMarchDrawDamage().Match(clause);
        if (deathMarchDrawDamage.Success)
        {
            operation = new(CardSemanticKind.DynamicDamage, 0, SemanticTarget.SelectedEnemy, clause,
                Id: "CARDS_DRAWN_THIS_TURN", XBonus: (int)Value(deathMarchDrawDamage));
            return true;
        }
        var allNamedCombatDamageGrowth = AllNamedCombatDamageGrowth().Match(clause);
        if (allNamedCombatDamageGrowth.Success)
        {
            operation = new(CardSemanticKind.ModifyCardDamage, Value(allNamedCombatDamageGrowth), SemanticTarget.None, clause,
                Id: "MODEL_ALL", Duration: "combat");
            return true;
        }
        var selfCombatBlockGrowth = SelfCombatBlockGrowth().Match(clause);
        if (selfCombatBlockGrowth.Success)
        {
            operation = new(CardSemanticKind.ModifyCardBlock, Value(selfCombatBlockGrowth), SemanticTarget.None, clause,
                Id: "SELF", Duration: "combat");
            return true;
        }
        if (clause == "打出抽牌堆顶部的牌并将其消耗")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.DrawPile, clause,
                Id: "TOP_FORCE_EXHAUST");
            return true;
        }
        if (clause is "打出你抽牌堆顶部的X张牌" or "打出你抽牌堆顶部的X+1张牌")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.DrawPile, clause,
                Id: "TOP", RepeatByEnergySpent: true, XBonus: clause.Contains("X+1", StringComparison.Ordinal) ? 1 : 0);
            return true;
        }
        var randomDrawAttackAutoPlay = RandomDrawAttackAutoPlay().Match(clause);
        if (randomDrawAttackAutoPlay.Success)
        {
            operation = new(CardSemanticKind.AutoPlayCard, Value(randomDrawAttackAutoPlay), SemanticTarget.DrawPile, clause,
                Id: "RANDOM_ATTACK", RandomSource: "Shuffle");
            return true;
        }
        var randomDrawAutoPlay = RandomDrawAutoPlay().Match(clause);
        if (randomDrawAutoPlay.Success)
        {
            operation = new(CardSemanticKind.AutoPlayCard, Value(randomDrawAutoPlay), SemanticTarget.DrawPile, clause,
                Id: "RANDOM_ANY", RandomSource: "Shuffle");
            return true;
        }
        if (clause == "在你的回合开始时，打出你抽牌堆顶部的牌")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.DrawPile, clause,
                Id: "TOP", Timing: "persistent_turn_start");
            return true;
        }
        var randomHandAttackAutoPlay = RandomHandAttackAutoPlay().Match(clause);
        if (randomHandAttackAutoPlay.Success)
        {
            operation = new(CardSemanticKind.AutoPlayCard, Value(randomHandAttackAutoPlay), SemanticTarget.Hand, clause,
                Id: "RANDOM_HAND_ATTACK", Timing: "turn_end", Condition: "RANDOM_ATTACK_HAND", RandomSource: "Shuffle");
            return true;
        }
        if (clause == "在你的回合结束时，如果这张牌位于抽牌堆顶部，则将其打出")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.DrawPile, clause,
                Id: "SELF", Timing: "turn_end", Condition: "SELF_DRAW_TOP");
            return true;
        }
        if (clause == "在你的回合结束时，如果这张牌在你的消耗牌堆中，则将其打出")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.ExhaustPile, clause,
                Id: "SELF", Timing: "turn_end", Condition: "SELF_IN_EXHAUST");
            return true;
        }
        if (clause == "你的回合开始时，如果这张牌在你的消耗牌堆中，则将其打出")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.ExhaustPile, clause,
                Id: "SELF", Timing: "turn_start", Condition: "SELF_IN_EXHAUST");
            return true;
        }
        if (clause == "消耗你所有的状态牌")
        {
            operation = new(CardSemanticKind.ExhaustCards, 0, SemanticTarget.None, clause,
                Id: "STATUS_ALL_PILES", All: true);
            return true;
        }
        var randomDamagePerExhaust = RandomDamagePerExhaustedCard().Match(clause);
        if (randomDamagePerExhaust.Success)
        {
            operation = new(CardSemanticKind.Damage, Value(randomDamagePerExhaust), SemanticTarget.RandomEnemy, clause,
                RepeatByExhaustedCount: true, RandomSource: "CombatTargets");
            return true;
        }
        if (clause is "杀死所有灾厄大于等于当前生命值的敌人" or
            "杀死所有灾厄大于等于自身生命值的敌人")
        {
            operation = new(CardSemanticKind.KillAllDoomedEnemies, 1, SemanticTarget.AllEnemies, clause,
                Id: "DOOM_AT_LEAST_HP");
            return true;
        }
        if (clause == "在每个回合开始时获得能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "TURN_START_ENERGY", Timing: "persistent_turn_start");
            return true;
        }
        var turnStartRandomDoom = TurnStartRandomEnemyDoom().Match(clause);
        if (turnStartRandomDoom.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(turnStartRandomDoom), SemanticTarget.Self, clause,
                Id: "TURN_START_RANDOM_DOOM", Timing: "persistent_turn_start");
            return true;
        }
        if (clause == "你每抽10张牌，获得能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "AUTOMATION_DRAW_ENERGY", Timing: "persistent_combat", XBonus: 10);
            return true;
        }
        if (clause == "你每花费4能量，就获得能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "ORBIT_ENERGY_REBATE", Timing: "persistent_combat", XBonus: 4);
            return true;
        }
        if (clause == "每回合失去1点能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, -1, SemanticTarget.Self, clause,
                Id: "MAX_ENERGY_DELTA", Timing: "persistent_combat");
            return true;
        }
        if (clause == "每回合少抽1张牌")
        {
            operation = new(CardSemanticKind.ApplyStatus, -1, SemanticTarget.Self, clause,
                Id: "HAND_DRAW_DELTA", Timing: "persistent_combat");
            return true;
        }
        var iterationMatch = IterationDraw().Match(clause);
        if (iterationMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(iterationMatch), SemanticTarget.Self, clause,
                Id: "ITERATION_DRAW", Timing: "persistent_combat");
            return true;
        }
        var pagestormMatch = PagestormDraw().Match(clause);
        if (pagestormMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(pagestormMatch), SemanticTarget.Self, clause,
                Id: "PAGESTORM_DRAW", Timing: "persistent_combat");
            return true;
        }
        var rageMatch = RageAttackBlock().Match(clause);
        if (rageMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(rageMatch), SemanticTarget.Self, clause,
                Id: "TRIGGER_ATTACK_PLAYED_BLOCK", Timing: "this_turn");
            return true;
        }
        var juggernautMatch = BlockGainedRandomEnemyDamage().Match(clause);
        if (juggernautMatch.Success)
        {
            operation = new(CardSemanticKind.Damage, Value(juggernautMatch), SemanticTarget.RandomEnemy, clause,
                Timing: "persistent_combat", Trigger: "BLOCK_GAINED",
                RandomSource: "CombatTargets", Duration: "combat");
            return true;
        }
        var serpentFormMatch = CardPlayedRandomEnemyDamage().Match(clause);
        if (serpentFormMatch.Success)
        {
            operation = new(CardSemanticKind.Damage, Value(serpentFormMatch), SemanticTarget.RandomEnemy, clause,
                Timing: "persistent_combat", Trigger: "CARD_PLAYED",
                RandomSource: "CombatTargets", Duration: "combat");
            return true;
        }
        var strangleMatch = CardPlayedTargetHpLoss().Match(clause);
        if (strangleMatch.Success)
        {
            operation = new(CardSemanticKind.LoseEnemyHp, Value(strangleMatch), SemanticTarget.SelectedEnemy, clause,
                Timing: "this_turn", Trigger: "CARD_PLAYED", Duration: "this_turn");
            return true;
        }
        if (clause == "打出你消耗牌堆中的所有虚无牌")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.ExhaustPile, clause,
                Id: "ALL_ETHEREAL");
            return true;
        }
        if (clause is "将你消耗牌堆中的所有小刀对一名敌人打出" or
            "将你消耗牌堆中的所有小刀对一名敌人打出。")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.ExhaustPile, clause,
                Id: "ALL_SHIV");
            return true;
        }
        if (clause is "将你消耗牌堆中的所有小刀升级然后对一名敌人打出" or
            "将你消耗牌堆中的所有小刀升级然后对一名敌人打出。")
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.ExhaustPile, clause,
                Id: "ALL_SHIV_UPGRADE");
            return true;
        }
        var envenomMatch = UnblockedPoweredAttackPoison().Match(clause);
        if (envenomMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(envenomMatch), SemanticTarget.SelectedEnemy, clause,
                Id: "POISON", Timing: "persistent_combat", Trigger: "POWERED_ATTACK_UNBLOCKED_DAMAGE",
                Duration: "persistent");
            return true;
        }
        var monarchsGazeMatch = PoweredAttackTemporaryStrengthLoss().Match(clause);
        if (monarchsGazeMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(monarchsGazeMatch), SemanticTarget.SelectedEnemy, clause,
                Id: "TEMP_STRENGTH_LOSS", Timing: "persistent_combat", Trigger: "POWERED_ATTACK",
                Duration: "this_turn");
            return true;
        }
        var monologueMatch = MonologueCardPlayedStrength().Match(clause);
        if (monologueMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(monologueMatch), SemanticTarget.Self, clause,
                Id: "STRENGTH", Timing: "this_turn", Trigger: "CARD_PLAYED");
            return true;
        }
        var oblivionMatch = OblivionCardPlayedDoom().Match(clause);
        if (oblivionMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(oblivionMatch), SemanticTarget.SelectedEnemy, clause,
                Id: "DOOM", Timing: "this_turn", Trigger: "CARD_PLAYED");
            return true;
        }
        if (clause == "给予等量于所造成伤害的灾厄")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.SelectedEnemy, clause,
                Id: "DOOM_EQUAL_DAMAGE");
            return true;
        }
        var corrosiveWaveMatch = CorrosiveWaveDrawPoison().Match(clause);
        if (corrosiveWaveMatch.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(corrosiveWaveMatch), SemanticTarget.AllEnemies, clause,
                Id: "POISON", Timing: "this_turn", Trigger: "CARD_DRAWN");
            return true;
        }
        var speedsterMatch = SpeedsterNonHandDrawDamage().Match(clause);
        if (speedsterMatch.Success)
        {
            operation = new(CardSemanticKind.Damage, Value(speedsterMatch), SemanticTarget.AllEnemies, clause,
                Trigger: "NON_HAND_CARD_DRAWN");
            return true;
        }
        var flameBarrierMatch = FlameBarrierRetaliation().Match(clause);
        if (flameBarrierMatch.Success)
        {
            operation = new(CardSemanticKind.Damage, Value(flameBarrierMatch), SemanticTarget.SelectedEnemy, clause,
                Timing: "this_turn", Trigger: "POWERED_ATTACK_RECEIVED");
            return true;
        }
        var persistentRetaliation = PersistentAttackRetaliation().Match(clause);
        if (persistentRetaliation.Success)
        {
            operation = new(CardSemanticKind.Damage, Value(persistentRetaliation), SemanticTarget.SelectedEnemy, clause,
                Timing: "persistent_combat", Trigger: "POWERED_ATTACK_RECEIVED");
            return true;
        }
        var buffer = PreventHpLoss().Match(clause);
        if (buffer.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(buffer), SemanticTarget.Self, clause,
                Id: "BUFFER", Timing: "persistent_until_consumed");
            return true;
        }
        if (clause is
            "如果你在本场战斗中受到未被格挡的攻击伤害，则立刻死亡" or
            "如果你在本场战斗中受到未被格挡的伤害，则立刻死亡")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "THE_GAMBIT", Timing: "persistent_combat",
                Trigger: "POWERED_ATTACK_UNBLOCKED_DAMAGE_RECEIVED");
            return true;
        }
        var lethality = LethalityFirstAttackBonus().Match(clause);
        if (lethality.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(lethality), SemanticTarget.Self, clause,
                Id: "LETHALITY", Timing: "persistent_combat",
                Trigger: "FIRST_ATTACK_PLAYED", Duration: "combat");
            return true;
        }
        var paleBlueDot = PaleBlueDotThresholdDraw().Match(clause);
        if (paleBlueDot.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(paleBlueDot), SemanticTarget.Self, clause,
                Id: "PALE_BLUE_DOT", Timing: "persistent_combat",
                Trigger: "ATTACK_COUNT_REACHED_5", Duration: "combat");
            return true;
        }
        var oneForAll = OneForAllZeroCostAttackBonus().Match(clause);
        if (oneForAll.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(oneForAll), SemanticTarget.Self, clause,
                Id: "ONE_FOR_ALL", Timing: "persistent_combat", Duration: "combat");
            return true;
        }
        var fasten = FastenDefendBonus().Match(clause);
        if (fasten.Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(fasten), SemanticTarget.Self, clause,
                Id: "FASTEN", Timing: "persistent_combat", Duration: "combat");
            return true;
        }
        if (clause == "在你的回合开始时，额外抽1张牌")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "TURN_START_DRAW", Timing: "persistent_turn_start");
            return true;
        }
        if (clause == "技能牌消耗变为0能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "SKILLS_COST_ZERO", Timing: "persistent_combat");
            return true;
        }
        if (clause == "每当你打出一张技能牌时，将其消耗")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "SKILLS_EXHAUST_ON_PLAY", Timing: "persistent_combat");
            return true;
        }
        if (clause is
            "你每回合打出的第一张耗能为0能量的攻击牌，会放回你的手牌" or
            "你每回合打出的第一张 耗能为0能量的攻击牌，会放回你的手牌" or
            "The first time you play a 0能量 Attack each turn, return it to your Hand." or
            "The first time you play a 016px|link= Attack each turn, return it to your Hand.")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "FERAL_ZERO_COST_ATTACK_RETURN", Timing: "persistent_combat");
            return true;
        }
        if (clause is
            "每回合首次打出攻击或技能牌时，将其置于你的抽牌堆顶端" or
            "将你每回合打出第一张攻击或技能牌，置于你的抽牌堆顶端")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "NOSTALGIA_ATTACK_SKILL_TOPDECK", Timing: "persistent_combat");
            return true;
        }
        if (clause == "你每回合打出的第一张牌会被打出两次")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "ECHO_FORM_REPLAY_FIRST_CARDS", Timing: "persistent_combat");
            return true;
        }
        if (clause == "你的下一张能力牌会额外打出一次")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "NEXT_POWER_REPLAY", Timing: "persistent_until_consumed");
            return true;
        }
        if (clause == "格挡不再在你的回合开始时消失")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "BARRICADE", Timing: "persistent_combat");
            return true;
        }
        if (clause is "卡牌在本回合耗能增加能量" or "所有卡牌在本回合耗能增加能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "ALL_CARD_COST_DELTA", Timing: "this_turn");
            return true;
        }
        if (AdaptiveStrikeCopy().IsMatch(clause))
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.DiscardPile, clause,
                Id: "SELF_COPY_ZERO_COST");
            return true;
        }
        if (clause == "选择你手牌中的一张无色牌")
        {
            operation = new(CardSemanticKind.SelectCard, 1, SemanticTarget.Hand, clause,
                Id: "HAND_COLORLESS");
            return true;
        }
        if (clause == "选择一张攻击牌或能力牌")
        {
            operation = new(CardSemanticKind.SelectCard, 1, SemanticTarget.Hand, clause,
                Id: "HAND_ATTACK_OR_POWER");
            return true;
        }
        if ((wrapper = SelectedCardCopiesToHand().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.CopySelectedCard, Value(wrapper), SemanticTarget.Hand, clause,
                Id: "SELECTED_TO_HAND");
            return true;
        }
        if ((wrapper = ExhaustUpToHandCards().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ExhaustCards, Value(wrapper), SemanticTarget.Hand, clause,
                Id: "UP_TO");
            return true;
        }
        if (clause is "为一张手牌添加虚无" or "将虚无添加至一张手牌")
        {
            operation = new(CardSemanticKind.SelectCard, 1, SemanticTarget.Hand, clause,
                Id: "HAND_ANY_ADD_ETHEREAL");
            return true;
        }
        if (clause == "给手牌中的一张牌添加保留")
        {
            operation = new(CardSemanticKind.SelectCard, 1, SemanticTarget.Hand, clause,
                Id: "HAND_ANY_ADD_RETAIN");
            return true;
        }
        if (clause == "给一张手牌添加重放")
        {
            operation = new(CardSemanticKind.SelectCard, 1, SemanticTarget.Hand, clause,
                Id: "HAND_ANY_ADD_REPLAY");
            return true;
        }
        if (clause == "将这张牌的一张复制品放入你的手牌")
        {
            operation = new(CardSemanticKind.CopySelectedCard, 1, SemanticTarget.Hand, clause,
                Id: "SELECTED_TO_HAND");
            return true;
        }
        if (clause is
            "在这个回合，你打出的下1张攻击牌会被额外打出一次" or
            "在这个回合，你打出的下2张攻击牌会被额外打出一次")
        {
            operation = new(CardSemanticKind.ApplyStatus, clause.Contains("下2张", StringComparison.Ordinal) ? 2 : 1,
                SemanticTarget.Self, clause, Id: "NEXT_ATTACK_REPLAY", Timing: "this_turn");
            return true;
        }
        if (clause is
            "在这个回合，你打出的下一张技能牌会被额外打出一次" or
            "在这个回合，你打出的下张技能牌会被额外打出一次" or
            "在这个回合，你打出的下2张技能牌会被额外打出一次" or
            "This turn, your next Skill is played an extra time." or
            "This turn, your next 2 Skills are played an extra time.")
        {
            operation = new(CardSemanticKind.ApplyStatus,
                clause.Contains("下2张", StringComparison.Ordinal) || clause.Contains("next 2 Skills", StringComparison.Ordinal) ? 2 : 1,
                SemanticTarget.Self, clause, Id: "NEXT_SKILL_REPLAY", Timing: "this_turn");
            return true;
        }
        if ((wrapper = HighCostCardPlayedBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Block, Value(wrapper), SemanticTarget.Self, clause,
                Id: $"ENERGY_AT_LEAST_{Math.Max(1, wrapper.Groups["icons"].Value.Length / 2)}",
                Trigger: "ENERGY_THRESHOLD_PLAYED");
            return true;
        }
        if ((wrapper = TurnEndInHandDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(wrapper), SemanticTarget.Self, clause,
                Timing: "turn_end", Condition: "SELF_IN_HAND");
            return true;
        }
        if ((wrapper = TurnEndInHandHpLoss().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.LoseHp, Value(wrapper), SemanticTarget.Self, clause,
                Timing: "turn_end", Condition: "SELF_IN_HAND");
            return true;
        }
        if (TurnEndInHandRegret().IsMatch(clause))
        {
            operation = new(CardSemanticKind.LoseHp, 1, SemanticTarget.Self, clause,
                Id: "HAND_COUNT", Timing: "turn_end", Condition: "SELF_IN_HAND");
            return true;
        }
        if ((wrapper = TurnEndInHandStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: NormalizeStatus(wrapper.Groups["status"].Value), Timing: "turn_end", Condition: "SELF_IN_HAND");
            return true;
        }
        if ((wrapper = NextTurnsChannelOrb().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ChannelOrb, Value(wrapper), SemanticTarget.Self, clause,
                Id: wrapper.Groups["orb"].Value,
                Timing: $"next_{wrapper.Groups["turns"].Value}_turn_starts");
            return true;
        }
        if ((wrapper = NextTurnsGainBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "TORIC_TOUGHNESS",
                Duration: wrapper.Groups["turns"].Value,
                Timing: "turn_start");
            return true;
        }
        if ((wrapper = DelayedAllEnemyDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.AllEnemies, clause,
                Id: "THE_BOMB",
                Duration: wrapper.Groups["turns"].Value,
                Timing: "turn_end");
            return true;
        }
        if ((wrapper = PowerCostReduction().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "POWERS_COST_DELTA",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = RetainUpToCardsTurnEnd().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, wrapper.Groups["amount"].Success ? Value(wrapper) : 1, SemanticTarget.Self, clause,
                Id: "WELL_LAID_PLANS",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = TurnStartDrawDiscard().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "TOOLS_OF_THE_TRADE",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = BonusDamagePerStarCostCard().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCardDamage, Value(wrapper), SemanticTarget.Self, clause,
                Id: "BONUS_DAMAGE_PER_STAR_COST_CARD",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = DebilitateEffect().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, 2, SemanticTarget.SelectedEnemy, clause,
                Id: "DEBILITATE",
                Duration: wrapper.Groups["turns"].Value,
                Timing: "turn_end");
            return true;
        }
        if (clause is "君王之剑在本回合对敌人造成双倍伤害" or "君王之剑在本回合造成双倍伤害")
        {
            operation = new(CardSemanticKind.ApplyStatus, 2, SemanticTarget.Self, clause,
                Id: "KINGS_BLADE_DOUBLE_DAMAGE",
                Timing: "this_turn");
            return true;
        }
        if (clause == "升级你手牌中的一张牌")
        {
            operation = new(CardSemanticKind.UpgradeCard, 1, SemanticTarget.Hand, clause, Id: "HAND_ONE");
            return true;
        }
        if (clause == "升级你手牌中的所有牌")
        {
            operation = new(CardSemanticKind.UpgradeCard, -1, SemanticTarget.Hand, clause, Id: "HAND_ALL");
            return true;
        }
        if (clause == "升级你的全部卡牌")
        {
            operation = new(CardSemanticKind.UpgradeCard, -1, SemanticTarget.Self, clause, Id: "ALL_COMBAT_CARDS");
            return true;
        }
        if (clause == "获得一瓶随机药水")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause, Id: "RANDOM_POTION_REWARD");
            return true;
        }
        if (clause is "对所有其他敌人造成等量的伤害" or "对所有其他敌人造成等量伤害")
        {
            operation = new(CardSemanticKind.Damage, 0, SemanticTarget.AllEnemies, clause,
                Id: "ALL_OTHER_ENEMIES_EQUAL_DAMAGE",
                Timing: "immediate");
            return true;
        }
        if (clause is "不论何处，将君王之剑放入你的手牌" or "不论何处将君王之剑放入你的手牌")
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause,
                Id: "KINGS_BLADE_TO_HAND",
                Timing: "immediate");
            return true;
        }
        if (clause == "君王之剑现在会对所有敌人造成伤害")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "KINGS_BLADE_ALL_ENEMIES",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = KingsBladeBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "KINGS_BLADE_BLOCK",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = KingsBladeReplay().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, wrapper.Groups["amount"].Success ? Value(wrapper) : 1, SemanticTarget.Self, clause,
                Id: "KINGS_BLADE_REPLAY",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = KingsBladeCostIncrease().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "KINGS_BLADE_COST_DELTA",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = CombatEndGoldReward().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(wrapper), SemanticTarget.Self, clause,
                Id: "GOLD_REWARD",
                Timing: "combat_end");
            return true;
        }
        if ((wrapper = BlackHoleTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "BLACK_HOLE",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = ChildOfTheStarsTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "CHILD_OF_THE_STARS",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = UpgradeRandomDiscardCards().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.UpgradeCard, Value(wrapper), SemanticTarget.DiscardPile, clause,
                Id: "DISCARD_RANDOM",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = TransformHandStatusCards().Match(clause)).Success)
        {
            var isUpgraded = wrapper.Groups["target"].Value.Contains('+');
            operation = new(CardSemanticKind.TransformCards, -1, SemanticTarget.Hand, clause,
                Id: isUpgraded ? "HAND_STATUS_TO_FUEL_PLUS" : "HAND_STATUS_TO_FUEL",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = HelixDrillDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(wrapper), SemanticTarget.SelectedEnemy, clause,
                Timing: "immediate",
                RepeatByEnergySpent: true);
            return true;
        }
        if (clause is "当你打出技能牌时，该牌获得奇巧" or "当你打出技能牌时该牌获得奇巧")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "MASTER_PLANNER",
                Timing: "persistent_combat");
            return true;
        }
        if (clause is "每当你生成状态牌的时候，随机生成一个充能球" or "每当你生成状态牌的时候随机生成一个充能球")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "TRASH_TO_TREASURE",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = ShroudBlockTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "SHROUD",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = SleightOfFleshTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "SLEIGHT_OF_FLESH",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = ThunderOrbTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "THUNDER",
                Timing: "persistent_combat");
            return true;
        }
        if (clause is "在你的回合开始时，将你弃牌堆的一张随机攻击牌放入你的手牌并将其升级" or "在你的回合开始时将你弃牌堆的一张随机攻击牌放入你的手牌并将其升级")
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause,
                Id: "DISCARD_ATTACK_TO_HAND_UPGRADED",
                Timing: "turn_start",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = CalcifyDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "CALCIFY",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = MadScienceDeckUpgrade().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.UpgradeCard,
                wrapper.Groups["amount"].Success ? Value(wrapper) : 1,
                SemanticTarget.DrawPile, clause,
                Id: "DECK_RANDOM",
                Timing: "combat_end");
            return true;
        }
        if (clause is "给予其他敌人该名敌人身上的所有负面效果" or "给予其他敌人该敌人身上的所有负面效果")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.AllEnemies, clause,
                Id: "TRANSFER_TARGET_DEBUFFS_TO_OTHERS",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = NecroMasteryTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "NECRO_MASTERY",
                Timing: "persistent_combat");
            return true;
        }
        if (clause is "在本回合给你手牌中的一张技能牌添加奇巧" or "在本回合给你手牌中的1张技能牌添加奇巧")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Hand, clause,
                Id: "HAND_SKILL_FINESSE",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = AutoPlayDiscardRandomAttacks().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.AutoPlayCard, Value(wrapper), SemanticTarget.DiscardPile, clause,
                Id: "DISCARD_RANDOM_ATTACK",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = TransformHandAttacksToGiantRock().Match(clause)).Success)
        {
            var isUpgraded = wrapper.Groups["target"].Value.Contains('+');
            operation = new(CardSemanticKind.TransformCards, -1, SemanticTarget.Hand, clause,
                Id: isUpgraded ? "HAND_ATTACKS_TO_GIANT_ROCK_PLUS" : "HAND_ATTACKS_TO_GIANT_ROCK",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = DrawCardReplayGain().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.DrawPile, clause,
                Id: "DRAW_CARD_REPLAY",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = RadiateDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(wrapper), SemanticTarget.AllEnemies, clause,
                Timing: "immediate",
                RepeatByStarsGained: true);
            return true;
        }
        if ((wrapper = TurnStartAddRandomCardToHand().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(wrapper), SemanticTarget.Hand, clause,
                Id: "RANDOM_ANY",
                Timing: "turn_start");
            return true;
        }
        if (clause is "添加的牌会获得虚无" or "添加的牌获得虚无")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Hand, clause,
                Id: "虚无",
                Timing: "turn_start");
            return true;
        }
        if ((wrapper = RollingBoulderTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.Self, clause,
                Id: "ROLLING_BOULDER",
                Timing: "persistent_combat");
            return true;
        }
        if ((wrapper = ForegoneConclusionChooseDraw().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.MoveCard, Value(wrapper), SemanticTarget.Hand, clause,
                Id: "DRAW_CHOOSE_TO_HAND",
                Timing: "next_turn");
            return true;
        }
        if (clause is "在战斗结束时，你可以从你的牌组中选一张牌移除" or "在战斗结束时你可以从你的牌组中选一张牌移除")
        {
            operation = new(CardSemanticKind.ExhaustCards, 1, SemanticTarget.DrawPile, clause,
                Id: "COMBAT_END_REMOVE_CARD",
                Timing: "combat_end");
            return true;
        }
        if ((wrapper = TurnStartTransformHandCards().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.TransformCards, Value(wrapper), SemanticTarget.Hand, clause,
                Id: "TURN_START_TRANSFORM_RANDOM",
                Timing: "turn_start");
            return true;
        }
        if ((wrapper = SicEmTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(wrapper), SemanticTarget.SelectedEnemy, clause,
                Id: "SIC_EM",
                Timing: "this_turn");
            return true;
        }
        if ((wrapper = BeatIntoShapeExtraForge().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Forge, Value(wrapper), SemanticTarget.Self, clause,
                Timing: "immediate",
                RepeatByHistoryCounter: true);
            return true;
        }
        if ((wrapper = TransformHandCardToOneMinionCard().Match(clause)).Success)
        {
            var isDive = wrapper.Groups["target"].Value.Contains("俯冲");
            var isPlus = wrapper.Groups["target"].Value.Contains('+');
            var id = isDive
                ? (isPlus ? "HAND_ONE_TO_MINION_DIVE_BOMB_PLUS" : "HAND_ONE_TO_MINION_DIVE_BOMB")
                : (isPlus ? "HAND_ONE_TO_MINION_STRIKE_PLUS" : "HAND_ONE_TO_MINION_STRIKE");
            operation = new(CardSemanticKind.TransformCards, 1, SemanticTarget.Hand, clause,
                Id: id,
                Timing: "immediate");
            return true;
        }
        if ((wrapper = DiscoverAnyCard().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause,
                Id: clause.Contains("升级") ? "DISCOVERY_UPGRADED" : "DISCOVERY",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = DiscoverColorlessCard().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause,
                Id: clause.Contains("升级") ? "QUASAR_COLORLESS_UPGRADED" : "QUASAR_COLORLESS",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = DiscoverOtherAttackCard().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause,
                Id: clause.Contains("升级") ? "SPLASH_OTHER_ATTACK_UPGRADED" : "SPLASH_OTHER_ATTACK",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = SeekerStrikeDrawChoose().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause,
                Id: "SEEKER_STRIKE_DRAW_CHOOSE",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if (clause is "选择一张牌" or "选择1张牌")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Hand, clause,
                Id: "NIGHTMARE_CHOOSE",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = NightmareCopiesNextTurn().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(wrapper), SemanticTarget.Hand, clause,
                Id: "NIGHTMARE_COPIES",
                Timing: "next_turn");
            return true;
        }
        if ((wrapper = DecisionsDecisionsReplaySkill().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.AutoPlayCard, 1, SemanticTarget.Hand, clause,
                Id: "HAND_SKILL_REPLAY_3",
                Repeat: int.Parse(wrapper.Groups["repeat"].Value),
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = TransformDrawCardsToMinionCard().Match(clause)).Success)
        {
            var isDive = wrapper.Groups["target"].Value.Contains("俯冲");
            var isPlus = wrapper.Groups["target"].Value.Contains('+');
            var id = isDive
                ? (isPlus ? "DRAW_TWO_TO_MINION_DIVE_BOMB_PLUS" : "DRAW_TWO_TO_MINION_DIVE_BOMB")
                : (isPlus ? "DRAW_TWO_TO_MINION_STRIKE_PLUS" : "DRAW_TWO_TO_MINION_STRIKE");
            operation = new(CardSemanticKind.TransformCards, Value(wrapper), SemanticTarget.DrawPile, clause,
                Id: id,
                Timing: "immediate");
            return true;
        }
        if ((wrapper = TransformHandAnyToMinionSacrifice().Match(clause)).Success)
        {
            var isPlus = wrapper.Groups["target"].Value.Contains('+');
            operation = new(CardSemanticKind.TransformCards, -1, SemanticTarget.Hand, clause,
                Id: isPlus ? "HAND_ANY_TO_MINION_SACRIFICE_PLUS" : "HAND_ANY_TO_MINION_SACRIFICE",
                Timing: "immediate");
            return true;
        }
        if (clause is "结束你的回合" or "结束回合")
        {
            operation = new(CardSemanticKind.PlayRestriction, 1, SemanticTarget.Self, clause,
                Id: "END_TURN",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = FirstCardsFreeEachTurn().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(wrapper), SemanticTarget.Self, clause,
                Id: "FIRST_CARDS_FREE_EACH_TURN",
                Timing: "persistent_combat");
            return true;
        }
        if (clause is "生成等量于你在这场战斗中生成过的闪电充能球数量的闪电充能球")
        {
            operation = new(CardSemanticKind.ChannelOrb, 1, SemanticTarget.Self, clause,
                Id: "LIGHTNING",
                Timing: "immediate",
                RepeatByHistoryCounter: true);
            return true;
        }
        if ((wrapper = StratagemShuffleChoose().Match(clause)).Success)
        {
            var count = wrapper.Groups["num"].Success ? int.Parse(wrapper.Groups["num"].Value) : 1;
            operation = new(CardSemanticKind.MoveCard, count, SemanticTarget.Hand, clause,
                Id: "SHUFFLE_CHOOSE_TO_HAND",
                Timing: "persistent_combat",
                Trigger: "ON_SHUFFLE");
            return true;
        }
        if (clause is "这张牌额外造成等量于奥斯提当前生命值的伤害" or "额外造成等量于奥斯提当前生命值的伤害" or "此牌额外造成等量于奥斯提当前生命值的伤害")
        {
            operation = new(CardSemanticKind.DynamicDamage, 0, SemanticTarget.SelectedEnemy, clause,
                Id: "OSTY_CURRENT_HP_DAMAGE",
                Timing: "immediate");
            return true;
        }
        if (clause is "额外造成等量于奥斯提最大生命值的伤害" or "这张牌额外造成等量于奥斯提最大生命值的伤害" or "此牌额外造成等量于奥斯提最大生命值的伤害")
        {
            operation = new(CardSemanticKind.DynamicDamage, 0, SemanticTarget.SelectedEnemy, clause,
                Id: "OSTY_MAX_HP_DAMAGE",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = OstyAttackedThisTurnCostZero().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCost, 0, SemanticTarget.Self, clause,
                Id: "OSTY_ATTACKED_THIS_TURN_ZERO_COST",
                Timing: "immediate",
                Condition: "OSTY_ATTACKED_THIS_TURN");
            return true;
        }
        if ((wrapper = FirstPlayedThisTurnDraw().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Draw, Value(wrapper), SemanticTarget.Self, clause,
                Id: "FIRST_PLAYED_THIS_TURN_DRAW",
                Timing: "immediate",
                Condition: "FIRST_PLAYED_THIS_TURN");
            return true;
        }
        if (clause is "他在本回合每攻击过一次，此牌就额外造成一次伤害" or "本回合每使用过一次奥斯提攻击牌，此牌就额外造成一次伤害")
        {
            operation = new(CardSemanticKind.CompanionDamage, 0, SemanticTarget.SelectedEnemy, clause,
                Id: "OSTY_ATTACK_COUNT_REPEAT",
                Timing: "immediate",
                RepeatByHistoryCounter: true);
            return true;
        }
        if ((wrapper = SqueezeAttackCardsBonusDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicDamage, Value(wrapper), SemanticTarget.SelectedEnemy, clause,
                Id: "OSTY_ATTACK_CARDS_IN_DECK_DAMAGE",
                Timing: "immediate");
            return true;
        }
        if (clause is "然后奥斯提死去" or "奥斯提死去")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause,
                Id: "KILL_OSTY",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = HealOstyHp().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Heal, Value(wrapper), SemanticTarget.Companion, clause,
                Id: "HEAL_OSTY",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = TransformDrawOneToSoul().Match(clause)).Success)
        {
            var isPlus = wrapper.Groups["target"].Value.Contains('+');
            operation = new(CardSemanticKind.TransformCards, 1, SemanticTarget.DrawPile, clause,
                Id: isPlus ? "DRAW_ONE_TO_SOUL_PLUS" : "DRAW_ONE_TO_SOUL",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = SummonXTimes().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Summon, decimal.Parse(wrapper.Groups["mult"].Value), SemanticTarget.Self, clause,
                Id: "SUMMON_X",
                RepeatByEnergySpent: true,
                Timing: "immediate");
            return true;
        }
        if ((wrapper = AddXSoulToDrawPile().Match(clause)).Success)
        {
            var isPlus = wrapper.Groups["plus"].Success;
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.DrawPile, clause,
                Id: isPlus ? "SOUL_PLUS" : "SOUL",
                AmountByEnergySpent: true,
                Timing: "immediate");
            return true;
        }
        if ((wrapper = OstyRandomDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.CompanionDamage, decimal.Parse(wrapper.Groups["damage"].Value), SemanticTarget.RandomEnemy, clause,
                Id: "OSTY_RANDOM_DAMAGE",
                RandomSource: "CombatTargets",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = DiscoverPowerCard().Match(clause)).Success)
        {
            var isUpgraded = clause.Contains("升级", StringComparison.Ordinal);
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause,
                Id: isUpgraded ? "ABUNDANCE_POWER_CHOICE_UPGRADED" : "ABUNDANCE_POWER_CHOICE",
                Timing: "immediate",
                RandomSource: "CombatCardSelection");
            return true;
        }
        if ((wrapper = DowsingTransformToAbundance().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.TransformCards, 1, SemanticTarget.Hand, clause,
                Id: "TRANSFORM_TO_ABUNDANCE",
                Timing: "out_of_combat");
            return true;
        }
        if ((wrapper = RightHandHandReturnTrigger().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause,
                Id: "DISCARD_SELF_TO_HAND_ON_HIGH_COST",
                Timing: "persistent_combat",
                Trigger: "HIGH_COST_CARD_PLAYED");
            return true;
        }
        if ((wrapper = GrappleBlockDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(wrapper), SemanticTarget.SelectedEnemy, clause,
                Id: "GAIN_BLOCK_DAMAGE",
                Timing: "this_turn",
                Trigger: "BLOCK_GAINED");
            return true;
        }
        if (clause is "远离")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause,
                Id: "FLEE",
                Timing: "immediate");
            return true;
        }
        if (clause is "将沙坑的计数加1")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause,
                Id: "QUICKSAND_COUNTER",
                Timing: "immediate");
            return true;
        }
        if (clause is "这张牌的耗能加1" or "此牌的耗能加1")
        {
            operation = new(CardSemanticKind.ModifyCost, 1, SemanticTarget.Self, clause,
                Id: "INCREASE_COST",
                Timing: "immediate");
            return true;
        }
        if ((wrapper = DebtLoseGold().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(wrapper), SemanticTarget.Self, clause,
                Id: "LOSE_GOLD",
                Timing: "turn_end",
                Condition: "SELF_IN_HAND");
            return true;
        }
        if (clause is "如果这张牌在你的手牌中，你必须优先打出这张牌" or "如果此牌在你的手牌中，你必须优先打出此牌")
        {
            operation = new(CardSemanticKind.PlayRestriction, 1, SemanticTarget.Self, clause,
                Id: "MUST_PLAY_FIRST",
                Timing: "immediate",
                Condition: "SELF_IN_HAND");
            return true;
        }
        if ((wrapper = GuiltyRemoveCombats().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(wrapper), SemanticTarget.Self, clause,
                Id: "REMOVE_FROM_DECK_AFTER_COMBATS",
                Timing: "out_of_combat");
            return true;
        }
        if (clause is "能在休息处被孵化")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause,
                Id: "HATCH_AT_REST_SITE",
                Timing: "out_of_combat");
            return true;
        }
        if (clause is "在下一阶段解锁一个特殊事件")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause,
                Id: "UNLOCK_SPECIAL_EVENT",
                Timing: "out_of_combat");
            return true;
        }
        if ((wrapper = SpoilsMapGoldBonus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(wrapper), SemanticTarget.Self, clause,
                Id: "MAP_BONUS_GOLD_LOCATION",
                Timing: "out_of_combat");
            return true;
        }
        if ((wrapper = TurnStartRightmostOrbPassive().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.TriggerOrbPassive,
                wrapper.Groups["repeat"].Success ? int.Parse(wrapper.Groups["repeat"].Value) : 1,
                SemanticTarget.Self, clause, Id: "RIGHTMOST_ANY", Timing: "turn_start");
            return true;
        }
        if ((wrapper = ConditionalExtraHit().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, 0, SemanticTarget.SelectedEnemy, clause,
                Id: "COPY_PREVIOUS_HIT",
                Condition: wrapper.Groups["condition"].Value.Contains("易伤", StringComparison.Ordinal)
                    ? "TARGET_HAS_VULNERABLE"
                    : "HAND_AT_LEAST_5");
            return true;
        }
        if ((wrapper = ConditionalAttackRepeat().Match(clause)).Success &&
            TryNormalizeCondition(wrapper.Groups["condition"].Value, out var repeatCondition))
        {
            operation = new(CardSemanticKind.Damage, 0, SemanticTarget.SelectedEnemy, clause,
                Id: "REPEAT_PREVIOUS_ATTACK",
                Repeat: int.Parse(wrapper.Groups["repeat"].Value),
                Condition: repeatCondition);
            return true;
        }
        if (clause == "每有一名敌人被击杀，就重复此效果")
        {
            operation = new(CardSemanticKind.Damage, 0, SemanticTarget.AllEnemies, clause,
                Id: "REPEAT_ON_KILL");
            return true;
        }
        if ((wrapper = ConditionalClause().Match(clause)).Success &&
            TryNormalizeCondition(wrapper.Groups["condition"].Value, out var condition) &&
            TryCompileClause(wrapper.Groups["action"].Value, out var conditionalOperation))
        {
            operation = conditionalOperation! with { SourceClause = clause, Condition = condition };
            return true;
        }
        if ((wrapper = TurnStartEnergyWithoutOwner().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus,
                Math.Max(1, wrapper.Groups["icons"].Value.Length / 2m),
                SemanticTarget.Self, clause, Id: "TURN_START_ENERGY", Timing: "persistent_turn_start");
            return true;
        }
        if ((wrapper = TimedClause().Match(clause)).Success &&
            TryCompileClause(wrapper.Groups["action"].Value.TrimStart('都'), out var timedOperation))
        {
            operation = timedOperation! with
            {
                SourceClause = clause,
                Timing = NormalizeTiming(wrapper.Groups["timing"].Value)
            };
            return true;
        }
        if ((wrapper = RecurringCardPlayedClause().Match(clause)).Success &&
            TryCompileClause(wrapper.Groups["action"].Value.TrimStart('都'), out var recurringOperation))
        {
            operation = recurringOperation! with { SourceClause = clause, Trigger = "CARD_PLAYED" };
            return true;
        }
        if ((wrapper = SelfExhaustedClause().Match(clause)).Success &&
            TryCompileClause(wrapper.Groups["action"].Value, out var selfExhaustedOperation))
        {
            operation = selfExhaustedOperation! with { SourceClause = clause, Trigger = "SELF_EXHAUSTED" };
            return true;
        }
        if ((wrapper = FirstCardGeneratedClause().Match(clause)).Success &&
            TryCompileClause(wrapper.Groups["action"].Value.Trim().TrimStart('都', '就'), out var firstGeneratedOperation))
        {
            operation = firstGeneratedOperation! with
            {
                SourceClause = clause,
                Trigger = "CARD_GENERATED",
                Condition = "FIRST_CARD_GENERATED_THIS_TURN"
            };
            return true;
        }
        if ((wrapper = ColorlessCardPlayedClause().Match(clause)).Success &&
            TryCompileClause(wrapper.Groups["action"].Value.Trim().TrimStart('都', '就'), out var colorlessPlayedOperation))
        {
            operation = colorlessPlayedOperation! with { SourceClause = clause, Trigger = "COLORLESS_CARD_PLAYED" };
            return true;
        }
        if ((wrapper = TriggeredClause().Match(clause)).Success &&
            TryNormalizeTrigger(wrapper.Groups["trigger"].Value, out var trigger) &&
            TryCompileClause(wrapper.Groups["action"].Value.Trim().TrimStart('都', '就'), out var triggeredOperation))
        {
            operation = triggeredOperation! with
            {
                SourceClause = clause,
                Trigger = trigger,
                Condition = trigger == "CARD_GENERATED" && clause.Contains("每回合第一次", StringComparison.Ordinal)
                    ? "FIRST_CARD_GENERATED_THIS_TURN"
                    : triggeredOperation.Condition
            };
            return true;
        }
        if (ExecutableKeywords.Contains(clause))
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.None, clause, Id: clause);
            return true;
        }

        Match match;
        if ((match = AllEnemyDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.AllEnemies, clause,
                Repeat: Repeat(match));
            return true;
        }
        if ((match = AllEnemyDamageX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.AllEnemies, clause,
                RepeatByEnergySpent: true, XBonus: XBonus(match));
            return true;
        }
        if ((match = Damage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.SelectedEnemy, clause,
                Repeat: Repeat(match));
            return true;
        }
        if ((match = ExhaustedCardDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.SelectedEnemy, clause,
                RepeatByExhaustedCount: true);
            return true;
        }
        if ((match = DamageX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.SelectedEnemy, clause,
                RepeatByEnergySpent: true, XBonus: XBonus(match));
            return true;
        }
        if (DoubleXAtLeastFour().IsMatch(clause))
        {
            operation = new(CardSemanticKind.Damage, 0, SemanticTarget.None, clause,
                Id: "DOUBLE_X_AT_LEAST_4");
            return true;
        }
        if ((match = DamagePerOrb().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.SelectedEnemy, clause,
                RepeatByOrbCount: true);
            return true;
        }
        if ((match = DynamicDamage().Match(clause)).Success)
        {
            var source = match.Groups["source"].Value switch
            {
                "你当前格挡值" => "PLAYER_BLOCK",
                "你抽牌堆中剩余牌数" => "DRAW_PILE_COUNT",
                "该敌人身上的灾厄层数" => "TARGET_STATUS:DOOM",
                _ => string.Empty
            };
            if (source.Length > 0)
            {
                operation = new(CardSemanticKind.DynamicDamage, 1, SemanticTarget.SelectedEnemy, clause, Id: source);
                return true;
            }
        }
        if ((match = RepeatedDamageByCounter().Match(clause)).Success)
        {
            var source = match.Groups["source"].Value switch
            {
                "手牌中每有一张技能牌" => "HAND_SKILL_COUNT",
                "在这个回合内每打出过一张攻击牌" => "ATTACKS_PLAYED_THIS_TURN",
                "本回合中每打出过一张技能牌" => "SKILLS_PLAYED_THIS_TURN",
                "本场战斗中每打出过一张虚无牌" => "ETHEREAL_PLAYED_THIS_COMBAT",
                _ => string.Empty
            };
            if (source.Length > 0)
            {
                operation = new(CardSemanticKind.DynamicDamage, Value(match), SemanticTarget.SelectedEnemy,
                    clause, Id: source, RepeatByHistoryCounter: true);
                return true;
            }
        }
        if (DamagePerReceivedEvent().IsMatch(clause))
        {
            operation = new(CardSemanticKind.DynamicDamage, 1, SemanticTarget.SelectedEnemy,
                clause, Id: "PLAYER_DAMAGE_RECEIVED_THIS_COMBAT", RepeatByHistoryCounter: true);
            return true;
        }
        if (clause == "造成本场战斗中所打出牌数的伤害")
        {
            operation = new(CardSemanticKind.DynamicDamage, 1, SemanticTarget.SelectedEnemy, clause,
                Id: "CARDS_PLAYED_THIS_COMBAT");
            return true;
        }
        if ((match = AdditiveDamageModifier().Match(clause)).Success)
        {
            var source = match.Groups["source"].Value switch
            {
                "你每有一张名字中含有“打击”的牌" => "STRIKE_CARD_COUNT",
                "你的消耗牌堆中每有一张牌" => "EXHAUST_PILE_COUNT",
                "该敌人身上每有一层易伤" => "TARGET_STATUS:VULNERABLE",
                _ => string.Empty
            };
            if (source.Length > 0)
            {
                operation = new(CardSemanticKind.DynamicDamage, Value(match), SemanticTarget.SelectedEnemy,
                    clause, Id: $"BONUS_{source}");
                return true;
            }
        }
        if ((match = HandCountDamagePenalty().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicDamage, -Value(match), SemanticTarget.SelectedEnemy,
                clause, Id: "BONUS_HAND_CARD_COUNT");
            return true;
        }
        if ((match = CombatCounterDamageBonus().Match(clause)).Success)
        {
            var source = match.Groups["source"].Value.Contains("抽过", StringComparison.Ordinal)
                ? "CARDS_DRAWN_THIS_COMBAT"
                : "CARDS_GENERATED_THIS_COMBAT";
            operation = new(CardSemanticKind.DynamicDamage, Value(match), SemanticTarget.SelectedEnemy,
                clause, Id: $"BONUS_{source}");
            return true;
        }
        if ((match = DiscardCounterDamageBonus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicDamage, Value(match), SemanticTarget.SelectedEnemy,
                clause, Id: "BONUS_CARDS_DISCARDED_THIS_TURN");
            return true;
        }
        if ((match = CompanionDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.CompanionDamage, Value(match), SemanticTarget.SelectedEnemy, clause,
                Id: match.Groups["actor"].Value, Repeat: Repeat(match));
            return true;
        }
        if ((match = Block().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Block, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = ExtraBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Block, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = DynamicBlock().Match(clause)).Success)
        {
            var source = match.Groups["source"].Value switch
            {
                "与你当前弃牌堆中牌数" or "你当前弃牌堆中牌数" => "DISCARD_PILE_COUNT",
                "所有敌人中毒层数" => "ALL_ENEMY_STATUS:POISON",
                "所造成伤害" => "DAMAGE_DEALT_THIS_CARD",
                _ => string.Empty
            };
            if (source.Length > 0)
            {
                operation = new(CardSemanticKind.DynamicBlock, 1, SemanticTarget.Self, clause,
                    Id: source, XBonus: match.Groups["bonus"].Success ? int.Parse(match.Groups["bonus"].Value) : 0);
                return true;
            }
        }
        if ((match = StrengthScaledBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicBlock, Value(match), SemanticTarget.Self, clause,
                Id: "PLAYER_STRENGTH");
            return true;
        }
        if ((match = DistinctOrbBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicBlock, Value(match), SemanticTarget.Self, clause,
                Id: "DISTINCT_ORB_TYPES", AmountByDistinctOrbTypes: true,
                Timing: match.Groups["timing"].Success ? "turn_start" : "immediate");
            return true;
        }
        if ((match = Draw().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Draw, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = DrawToHandSize().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Draw, Value(match), SemanticTarget.Self, clause,
                Id: "TO_HAND_SIZE_RETAIN_DRAWN");
            return true;
        }
        if (clause == "抽牌直到抽满手牌")
        {
            operation = new(CardSemanticKind.Draw, 10, SemanticTarget.Self, clause,
                Id: "TO_HAND_SIZE");
            return true;
        }
        if (clause == "抽牌直到你抽到一张非攻击牌")
        {
            operation = new(CardSemanticKind.Draw, 1, SemanticTarget.Self, clause,
                Id: "UNTIL_NON_ATTACK");
            return true;
        }
        if ((match = DrawnSkillBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Block, Value(match), SemanticTarget.Self, clause,
                Condition: "LAST_DRAWN_CARD_SKILL");
            return true;
        }
        if (DistinctOrbDraw().IsMatch(clause))
        {
            operation = new(CardSemanticKind.Draw, 1, SemanticTarget.Self, clause,
                AmountByDistinctOrbTypes: true);
            return true;
        }
        if ((match = EnemyStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.SelectedEnemy, clause,
                Id: NormalizeStatus(match.Groups["status"].Value));
            return true;
        }
        if ((match = RandomEnemyStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.RandomEnemy, clause,
                Id: NormalizeStatus(match.Groups["status"].Value),
                Repeat: Repeat(match), RandomSource: "CombatTargets");
            return true;
        }
        if ((match = EnemyStatusX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.SelectedEnemy, clause,
                Id: NormalizeStatus(match.Groups["status"].Value),
                XBonus: XBonus(match), AmountByEnergySpent: true);
            return true;
        }
        if ((match = AllEnemyStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.AllEnemies, clause,
                Id: NormalizeStatus(match.Groups["status"].Value));
            return true;
        }
        if ((match = DoubleEnemyStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.MultiplyStatus, 2, SemanticTarget.SelectedEnemy, clause,
                Id: NormalizeStatus(match.Groups["status"].Value));
            return true;
        }
        if ((match = RandomDamage().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.RandomEnemy, clause,
                Repeat: Repeat(match), RandomSource: "CombatTargets");
            return true;
        }
        if ((match = RandomDamageX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Damage, Value(match), SemanticTarget.RandomEnemy, clause,
                RepeatByEnergySpent: true, RandomSource: "CombatTargets");
            return true;
        }
        if ((match = SelfStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self, clause,
                Id: NormalizeStatus(match.Groups["status"].Value));
            return true;
        }
        if ((match = NumericSelfStatusNoUnit().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self, clause,
                Id: NormalizeStatus(match.Groups["status"].Value));
            return true;
        }
        if ((match = SelfDoom().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self, clause, Id: "DOOM");
            return true;
        }
        if ((match = TemporarySelfStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self, clause,
                Id: NormalizeStatus(match.Groups["status"].Value), Timing: "this_turn");
            return true;
        }
        if ((match = DistinctOrbFocus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self, clause,
                Id: "FOCUS", Timing: "this_turn", AmountByDistinctOrbTypes: true);
            return true;
        }
        if ((match = TemporaryLoseStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, -Value(match), SemanticTarget.Self, clause,
                Id: NormalizeStatus(match.Groups["status"].Value), Timing: "this_turn");
            return true;
        }
        if ((match = LoseStatus().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, -Value(match), SemanticTarget.Self, clause,
                Id: NormalizeStatus(match.Groups["status"].Value));
            return true;
        }
        if ((match = EnemyLoseStrength().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, -Value(match),
                match.Groups["all"].Success ? SemanticTarget.AllEnemies : SemanticTarget.SelectedEnemy,
                clause, Id: "STRENGTH");
            return true;
        }
        if ((match = TemporaryEnemyStrengthLoss().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match),
                match.Groups["all"].Success ? SemanticTarget.AllEnemies : SemanticTarget.SelectedEnemy,
                clause, Id: "TEMP_STRENGTH_LOSS", Timing: "this_turn");
            return true;
        }
        if ((match = EnemyLoseStrengthX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, -1, SemanticTarget.SelectedEnemy, clause,
                Id: "STRENGTH", XBonus: XBonus(match), AmountByEnergySpent: true);
            return true;
        }
        if ((match = EnemyGainStrength().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.SelectedEnemy, clause,
                Id: "STRENGTH");
            return true;
        }
        if ((match = StrengthFromVulnerable().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self, clause,
                Id: "STRENGTH", AmountByTargetVulnerableStacks: true);
            return true;
        }
        if ((match = Radiance().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Math.Max(1, match.Groups["icons"].Value.Length / 2m),
                SemanticTarget.Self, clause, Id: "RADIANCE");
            return true;
        }
        if ((match = NumericRadiance().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ApplyStatus, Value(match), SemanticTarget.Self,
                clause, Id: "RADIANCE");
            return true;
        }
        if ((match = NumericEnergy().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GainEnergy, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = IconEnergy().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GainEnergy, match.Groups["icons"].Value.Length / 2m, SemanticTarget.Self, clause);
            return true;
        }
        if ((match = LoseEnergy().Match(clause)).Success)
        {
            var amount = match.Groups["amount"].Success
                ? Value(match)
                : Math.Max(1, match.Groups["icons"].Value.Length / 2m);
            operation = new(CardSemanticKind.GainEnergy, -amount, SemanticTarget.Self, clause);
            return true;
        }
        if ((match = NextTurnEnergy().Match(clause)).Success)
        {
            var amount = match.Groups["amount"].Success
                ? decimal.Parse(match.Groups["amount"].Value)
                : Math.Max(1, match.Groups["icons"].Value.Length / 2m);
            operation = new(CardSemanticKind.GainEnergy, amount, SemanticTarget.Self, clause, Timing: "next_turn");
            return true;
        }
        if ((match = NextTurnDraw().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Draw, Value(match), SemanticTarget.Self, clause, Timing: "next_turn");
            return true;
        }
        if ((match = NextTurnDrawEnergy().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Draw, Value(match), SemanticTarget.Self, clause,
                Id: match.Groups["icons"].Value, Timing: "next_turn");
            return true;
        }
        if (clause is "你在本回合内不能再抽牌" or "你在本回合内不能再抽任何牌")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "CANNOT_DRAW", Timing: "this_turn");
            return true;
        }
        if (clause == "你在本回合内不能再获得能量")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "CANNOT_GAIN_ENERGY", Timing: "this_turn");
            return true;
        }
        if (clause == "你手牌中的所有牌在本回合免费打出")
        {
            operation = new(CardSemanticKind.ModifyHandCosts, 0, SemanticTarget.Hand, clause,
                Timing: "this_turn");
            return true;
        }
        if (clause == "在这个回合，你当前手牌中所有牌的耗能降低至1")
        {
            operation = new(CardSemanticKind.ModifyHandCosts, 1, SemanticTarget.Hand, clause,
                Id: "CAP_AT_1", Timing: "this_turn");
            return true;
        }
        if (clause == "在这场战斗，你当前手牌中所有牌的耗能降低至1")
        {
            operation = new(CardSemanticKind.ModifyHandCosts, 1, SemanticTarget.Hand, clause,
                Id: "CAP_AT_1", Timing: "persistent_combat");
            return true;
        }
        if ((match = NextCardFree().Match(clause)).Success)
        {
            var type = match.Groups["type"].Value switch
            {
                "攻击" => "ATTACK",
                "技能" => "SKILL",
                "能力" => "POWER",
                "虚无" => "ETHEREAL",
                _ => string.Empty
            };
            if (type.Length > 0)
            {
                operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                    Id: $"NEXT_FREE_{type}", Timing: "this_turn");
                return true;
            }
        }
        if (clause == "将你的能量翻倍")
        {
            operation = new(CardSemanticKind.GainEnergy, 1, SemanticTarget.Self, clause,
                Id: "CURRENT_ENERGY");
            return true;
        }
        if (clause == "你的手牌中每有一张攻击牌，就获得能量")
        {
            operation = new(CardSemanticKind.GainEnergy, 1, SemanticTarget.Self, clause,
                AmountByHandAttackCount: true);
            return true;
        }
        if (clause == "将你当前的格挡翻倍")
        {
            operation = new(CardSemanticKind.DynamicBlock, 1, SemanticTarget.Self, clause,
                Id: "PLAYER_BLOCK");
            return true;
        }
        if ((match = LoseMaxHp().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyMaxHp, -Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = FeedMaxHp().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyMaxHp, Value(match), SemanticTarget.Self, clause,
                Condition: "SOURCE_CARD_KILLED");
            return true;
        }
        if ((match = FatalExtraCardReward().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause,
                Id: "EXTRA_CARD_REWARD", Condition: "SOURCE_CARD_KILLED");
            return true;
        }
        if ((match = FatalGoldReward().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Keyword, Value(match), SemanticTarget.Self, clause,
                Id: "GOLD_REWARD", Condition: "SOURCE_CARD_KILLED");
            return true;
        }
        if (clause == "去除敌人身上的所有格挡值和人工制品")
        {
            operation = new(CardSemanticKind.ClearEnemyBlockAndArtifact, 1,
                SemanticTarget.SelectedEnemy, clause);
            return true;
        }
        if (clause == "击晕该敌人")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.SelectedEnemy, clause,
                Id: "STUN", Timing: "this_turn");
            return true;
        }
        if ((match = StatusGeneratedCostReduction().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCost, -1, SemanticTarget.None, clause,
                Id: "SELF_BY_STATUS_GENERATED", Trigger: "STATUS_CARD_GENERATED");
            return true;
        }
        if ((match = StatusGeneratedCostZero().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCost, 0, SemanticTarget.None, clause,
                Id: match.Groups["lifetime"].Value == "本回合"
                    ? "SELF_BY_STATUS_GENERATED_TURN_ZERO"
                    : "SELF_BY_STATUS_GENERATED_UNTIL_PLAYED_ZERO",
                Trigger: "STATUS_CARD_GENERATED");
            return true;
        }
        if (SelfCostIncrease().IsMatch(clause))
        {
            operation = new(CardSemanticKind.ModifyCost, 1, SemanticTarget.None, clause, Id: "SELF");
            return true;
        }
        if (clause == "这张牌的耗能降为0能量")
        {
            operation = new(CardSemanticKind.ModifyCost, 0, SemanticTarget.None, clause,
                Id: "SELF_SET_ZERO");
            return true;
        }
        if ((match = SelfCostReduction().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCost, -Value(match), SemanticTarget.None, clause,
                Id: "SELF");
            return true;
        }
        if ((match = SelectedHandCostIncrease().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCost, Value(match), SemanticTarget.None, clause,
                Id: "SELECTED_HAND_COST_DELTA");
            return true;
        }
        if ((match = BonusDamagePerTargetDebuffType().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicDamage, Value(match), SemanticTarget.SelectedEnemy, clause,
                Id: "BONUS_TARGET_DEBUFF_TYPE_COUNT");
            return true;
        }
        if ((match = BonusDamagePerExhaustedSoul().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DynamicDamage, Value(match), SemanticTarget.SelectedEnemy, clause,
                Id: "BONUS_EXHAUST_SOUL_COUNT");
            return true;
        }
        if ((match = CostReducedByPlayedType().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyCost, -1, SemanticTarget.None, clause,
                Id: match.Groups["type"].Value == "攻击" ? "SELF_BY_ATTACKS_PLAYED" : "SELF_BY_SKILLS_PLAYED");
            return true;
        }
        if ((match = CostReducedByEtherealPlayed().Match(clause)).Success)
        {
            var reduction = Math.Max(1, match.Groups["icons"].Value.Length / "能量".Length);
            operation = new(CardSemanticKind.ModifyCost, -reduction, SemanticTarget.None, clause,
                Id: "SELF_BY_ETHEREAL_PLAYED");
            return true;
        }
        if (clause == "你的下一回合开始时格挡不会消失")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "BLUR", Timing: "this_turn");
            return true;
        }
        if (clause == "在下个回合获得等量于你当前格挡值的格挡")
        {
            operation = new(CardSemanticKind.DynamicBlock, 1, SemanticTarget.Self, clause,
                Id: "PLAYER_BLOCK", Timing: "next_turn_snapshot");
            return true;
        }
        if ((match = NextTurnBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Block, Value(match), SemanticTarget.Self, clause,
                Timing: "next_turn");
            return true;
        }
        if (clause is
            "在弃牌堆放入一张此牌的复制品" or
            "在所有人的弃牌堆中加入一张此牌的复制品")
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.DiscardPile, clause, Id: "SELF_COPY");
            return true;
        }
        var randomAttacksToDraw = RandomAttacksToDrawPile().Match(clause);
        if (randomAttacksToDraw.Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(randomAttacksToDraw), SemanticTarget.DrawPile, clause,
                Id: "随机攻击牌", RandomSource: "CombatCardGeneration");
            return true;
        }
        if (clause == "将你在本回合打出的下一张牌放置到你的抽牌堆顶部")
        {
            operation = new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                Id: "NEXT_CARD_TO_DRAW_TOP", Timing: "this_turn");
            return true;
        }
        if ((match = GenerateCardToDrawPile().Match(clause)).Success)
        {
            var amount = match.Groups["amount"].Success ? Value(match) : 1m;
            operation = new(CardSemanticKind.GenerateCard, amount, SemanticTarget.DrawPile, clause,
                Id: NormalizeGeneratedCard(match.Groups["card"].Value));
            return true;
        }
        if (clause == "用碎屑填满你的手牌")
        {
            operation = new(CardSemanticKind.GenerateCard, 0, SemanticTarget.Hand, clause,
                Id: "碎屑", All: true);
            return true;
        }
        if ((match = CardMovement().Match(clause)).Success)
        {
            var discard = match.Groups["verb"].Value is "丢弃" or "弃";
            operation = new(discard ? CardSemanticKind.DiscardCards : CardSemanticKind.ExhaustCards,
                Value(match), SemanticTarget.Hand, clause);
            return true;
        }
        var stokeGenerated = StokeGeneratedCards().Match(clause);
        if (stokeGenerated.Success)
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause,
                Id: stokeGenerated.Groups["upgraded"].Value == "随机已升级的牌" ? "随机已升级牌" : "随机牌",
                RepeatByExhaustedCount: true,
                RandomSource: "CombatCardGeneration");
            return true;
        }
        if ((match = RandomExhaust().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.RandomExhaustCards, Value(match), SemanticTarget.Hand, clause);
            return true;
        }
        if ((match = AllCardMovement().Match(clause)).Success)
        {
            var discard = match.Groups["verb"].Value == "丢弃";
            operation = new(discard ? CardSemanticKind.DiscardCards : CardSemanticKind.ExhaustCards,
                0, SemanticTarget.Hand, clause, All: true);
            return true;
        }
        if (DiscardAndRedraw().IsMatch(clause))
        {
            operation = new(CardSemanticKind.DiscardHandThenDrawSame, 0, SemanticTarget.Hand, clause, All: true);
            return true;
        }
        if ((match = DiscardHandAndGenerate().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.DiscardHandAndGenerate, 0, SemanticTarget.Hand, clause,
                Id: NormalizeGeneratedCard(match.Groups["card"].Value), All: true);
            return true;
        }
        if ((match = Reboot().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Reboot, 0, SemanticTarget.Hand, clause, All: true,
                RandomSource: "Shuffle");
            return true;
        }
        if (clause == "消耗你的手牌中随机一张攻击牌，并将它的伤害添加给这张牌")
        {
            operation = new(CardSemanticKind.RandomExhaustCards, 1, SemanticTarget.Hand, clause,
                Id: "ATTACK_AND_GROW_SELF", RandomSource: "CombatCardSelection");
            return true;
        }
        if ((match = ExhaustNonAttacksForBlock().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ExhaustNonAttacksAndBlock, Value(match), SemanticTarget.Hand, clause);
            return true;
        }
        if ((match = DiscardPileSelection().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.MoveCard, Value(match), SemanticTarget.DiscardPile, clause,
                Id: "CHOOSE_DISCARD_ANY_TO_HAND");
            return true;
        }
        if (DrawPileDirectSelection().IsMatch(clause))
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.DrawPile, clause,
                Id: "CHOOSE_DRAW_ANY_TO_HAND");
            return true;
        }
        if ((match = DiscardPileUpToSelection().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.MoveCard, Value(match), SemanticTarget.DiscardPile, clause,
                Id: "CHOOSE_DISCARD_UP_TO_TO_HAND");
            return true;
        }
        if (DiscardPileToDrawTop().IsMatch(clause))
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.DiscardPile, clause,
                Id: "CHOOSE_DISCARD_ANY_TO_DRAW_TOP");
            return true;
        }
        if (HandToDrawTop().IsMatch(clause))
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause,
                Id: "CHOOSE_HAND_ANY_TO_DRAW_TOP");
            return true;
        }
        if (clause == "将你弃牌堆中的所有0能量牌放入你的手牌")
        {
            operation = new(CardSemanticKind.MoveCard, 0, SemanticTarget.DiscardPile, clause,
                Id: "ALL_ZERO_COST_DISCARD_TO_HAND", All: true);
            return true;
        }
        if (clause == "将你抽牌堆中的所有稀有牌放入你的手牌")
        {
            operation = new(CardSemanticKind.MoveCard, 0, SemanticTarget.DrawPile, clause,
                Id: "RANDOM_RARE_TO_HAND", All: true, RandomSource: "CombatCardSelection");
            return true;
        }
        if (DrawTopToExhaust().IsMatch(clause))
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.DrawPile, clause,
                Id: "DRAW_TOP_TO_EXHAUST");
            return true;
        }
        if (clause == "将一张随机牌放入你的手牌")
        {
            operation = new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause,
                Id: "随机牌");
            return true;
        }
        if ((match = GenerateCard().Match(clause)).Success)
        {
            var target = match.Groups["pile"].Value == "弃牌堆"
                ? SemanticTarget.DiscardPile
                : SemanticTarget.Hand;
            operation = new(CardSemanticKind.GenerateCard, Value(match), target, clause,
                Id: NormalizeGeneratedCard(match.Groups["card"].Value));
            return true;
        }
        if ((match = GenerateCardInHand().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(match), SemanticTarget.Hand, clause,
                Id: NormalizeGeneratedCard(match.Groups["card"].Value));
            return true;
        }
        if ((match = AddShivsToHand().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(match), SemanticTarget.Hand, clause,
                Id: "SHIV");
            return true;
        }
        if ((match = AddInkyShivsToHand().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(match), SemanticTarget.Hand, clause,
                Id: "INKY_SHIV");
            return true;
        }
        if ((match = GenerateCardInDiscardPile().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.GenerateCard, Value(match), SemanticTarget.DiscardPile, clause,
                Id: NormalizeGeneratedCard(match.Groups["card"].Value));
            return true;
        }
        if ((match = DrawPileSelection().Match(clause)).Success)
        {
            var filter = match.Groups["filter"].Value switch
            {
                "攻击" => "ATTACK",
                "技能" => "SKILL",
                _ => "ANY"
            };
            var destination = match.Groups["destination"].Value.Contains("消耗", StringComparison.Ordinal)
                ? "EXHAUST"
                : "HAND";
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.DrawPile, clause,
                Id: $"CHOOSE_DRAW_{filter}_TO_{destination}");
            return true;
        }
        if ((match = LoseHp().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.LoseHp, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = EnemyLoseHp().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.LoseEnemyHp, Value(match), SemanticTarget.SelectedEnemy, clause);
            return true;
        }
        if ((match = Heal().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Heal, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = ChannelOrb().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ChannelOrb, Value(match), SemanticTarget.Self, clause,
                Id: match.Groups["orb"].Value);
            return true;
        }
        if ((match = ChannelRandomOrb().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ChannelOrb, Value(match), SemanticTarget.Self, clause, Id: "RANDOM");
            return true;
        }
        if ((match = ChannelOrbCompact().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ChannelOrb, Value(match), SemanticTarget.Self, clause,
                Id: match.Groups["orb"].Value);
            return true;
        }
        if ((match = ChannelOrbX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ChannelOrb, 1, SemanticTarget.Self, clause,
                Id: match.Groups["orb"].Value, AmountByEnergySpent: true, XBonus: XBonus(match));
            return true;
        }
        if ((match = ChannelOrbPerEnemy().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ChannelOrb, Value(match), SemanticTarget.Self, clause,
                Id: match.Groups["orb"].Value, AmountByAliveEnemyCount: true);
            return true;
        }
        if ((match = EvokeOrb().Match(clause)).Success)
        {
            var repeat = match.Groups["repeat"].Success
                ? int.Parse(match.Groups["repeat"].Value)
                : match.Groups["repeatWord"].Value == "两" ? 2 : 1;
            operation = new(CardSemanticKind.EvokeOrb, repeat, SemanticTarget.Self, clause,
                Id: match.Groups["position"].Value switch
                {
                    "所有" => "ALL",
                    "最左侧" => "LEFTMOST",
                    _ => "RIGHTMOST"
                });
            return true;
        }
        if ((match = EvokeOrbX().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.EvokeOrb, 1, SemanticTarget.Self, clause,
                Id: "RIGHTMOST", AmountByEnergySpent: true, XBonus: XBonus(match));
            return true;
        }
        if ((match = ModifyOrbCapacity().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.ModifyOrbCapacity,
                match.Groups["verb"].Value == "失去" ? -Value(match) : Value(match),
                SemanticTarget.Self, clause);
            return true;
        }
        if ((match = TriggerOrbPassive().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.TriggerOrbPassive,
                match.Groups["repeatWord"].Value == "两" ? 2 : 1,
                match.Groups["target"].Success ? SemanticTarget.SelectedEnemy : SemanticTarget.Self,
                clause,
                Id: match.Groups["orb"].Value);
            return true;
        }
        if ((match = Summon().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Summon, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if ((match = Forge().Match(clause)).Success)
        {
            operation = new(CardSemanticKind.Forge, Value(match), SemanticTarget.Self, clause);
            return true;
        }
        if (clause == "在本回合保留你的手牌")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.Hand, clause, Id: "RETAIN_HAND", Timing: "this_turn");
            return true;
        }
        if (clause == "在你的下个回合开始时，将此卡返回你的手牌")
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause, Id: "SELF", Timing: "next_turn_start");
            return true;
        }
        if (clause == "将这张牌放置于你的抽牌堆顶部")
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.DrawPile, clause,
                Id: "SELF_TO_DRAW_TOP");
            return true;
        }
        if (clause == "将此牌返回你的手牌")
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.Hand, clause,
                Id: "SELF_TO_HAND");
            return true;
        }
        if (clause == "将弃牌堆中的一张牌放入你的手牌")
        {
            operation = new(CardSemanticKind.MoveCard, 1, SemanticTarget.DiscardPile, clause, Id: "CHOOSE_DISCARD_ANY_TO_HAND");
            return true;
        }
        if (clause is "这张牌在本回合可以免费打出" or
            "这张牌在本回合免费打出" or
            "这张牌在本回合内可以免费打出" or
            "这张牌在本回合内免费打出" or
            "那张牌在本回合内可以免费打出")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.None, clause,
                Id: "GENERATED_CARD_FREE_THIS_TURN", Timing: "this_turn");
            return true;
        }
        if (clause == "它们在本场战斗中可以被免费打出")
        {
            operation = new(CardSemanticKind.Keyword, 1, SemanticTarget.None, clause,
                Id: "GENERATED_CARDS_FREE_THIS_COMBAT", Timing: "persistent_combat");
            return true;
        }
        return false;
    }

    private static bool TryCompileCompoundClause(
        string clause,
        out ImmutableArray<CardSemanticOperation> operations)
    {
        operations = [];
        var ftl = ConditionalDrawByCardPlayCount().Match(clause);
        if (ftl.Success)
        {
            operations =
            [
                new(CardSemanticKind.Draw, decimal.Parse(ftl.Groups["draw"].Value), SemanticTarget.Self, clause,
                    Condition: $"CARD_PLAYS_FINISHED_LT:{ftl.Groups["maximum"].Value}")
            ];
            return true;
        }
        var handEmpty = HandEmptyDrawEnergy().Match(clause);
        if (handEmpty.Success)
        {
            var energy = Math.Max(1, handEmpty.Groups["icons"].Value.Length / "能量".Length);
            operations =
            [
                new(CardSemanticKind.Draw, decimal.Parse(handEmpty.Groups["draw"].Value), SemanticTarget.Self, clause,
                    Condition: "HAND_EMPTY"),
                new(CardSemanticKind.GainEnergy, energy, SemanticTarget.Self, clause,
                    Condition: "HAND_EMPTY")
            ];
            return true;
        }
        var pairedStatuses = AllEnemyPairedStatuses().Match(clause);
        if (pairedStatuses.Success)
        {
            var firstAmount = Value(pairedStatuses);
            var secondAmount = pairedStatuses.Groups["secondAmount"].Success
                ? int.Parse(pairedStatuses.Groups["secondAmount"].Value)
                : firstAmount;
            operations =
            [
                new(CardSemanticKind.ApplyStatus, firstAmount, SemanticTarget.AllEnemies, clause,
                    Id: NormalizeStatus(pairedStatuses.Groups["first"].Value)),
                new(CardSemanticKind.ApplyStatus, secondAmount, SemanticTarget.AllEnemies, clause,
                    Id: NormalizeStatus(pairedStatuses.Groups["second"].Value))
            ];
            return true;
        }
        var convergenceNextTurn = NextTurnEnergyAndStars().Match(clause);
        if (convergenceNextTurn.Success)
        {
            var energyText = convergenceNextTurn.Groups["energy"].Value;
            var starText = convergenceNextTurn.Groups["stars"].Value;
            var energyCount = Math.Max(1, Regex.Matches(energyText, @"(?:\[能量\]|16px\|link=能量|16px\|link=|能量)").Count);
            var starMatches = Regex.Matches(starText, @"(?:\[STAR\]|16px\|link=辉星|16px\|link=|辉星)");
            var starCount = starMatches.Count > 0 ? starMatches.Count : 1;
            operations =
            [
                new(CardSemanticKind.GainEnergy, energyCount, SemanticTarget.Self, clause, Timing: "next_turn"),
                new(CardSemanticKind.ApplyStatus, starCount, SemanticTarget.Self, clause, Id: "STARS", Timing: "next_turn")
            ];
            return true;
        }
        var invokeNextTurn = NextTurnSummonAndEnergy().Match(clause);
        if (invokeNextTurn.Success)
        {
            var summonCount = int.Parse(invokeNextTurn.Groups["summon"].Value);
            var energyText = invokeNextTurn.Groups["energy"].Value;
            var energyCount = Math.Max(1, Regex.Matches(energyText, @"(?:\[能量\]|16px\|link=能量|16px\|link=|能量)").Count);
            operations =
            [
                new(CardSemanticKind.Summon, summonCount, SemanticTarget.Self, clause, Timing: "next_turn"),
                new(CardSemanticKind.GainEnergy, energyCount, SemanticTarget.Self, clause, Timing: "next_turn")
            ];
            return true;
        }
        var highFive = HighFiveDamageAndVulnerable().Match(clause);
        if (highFive.Success)
        {
            operations =
            [
                new(CardSemanticKind.CompanionDamage, decimal.Parse(highFive.Groups["damage"].Value), SemanticTarget.AllEnemies, clause),
                new(CardSemanticKind.ApplyStatus, decimal.Parse(highFive.Groups["vuln"].Value), SemanticTarget.AllEnemies, clause, Id: "VULNERABLE")
            ];
            return true;
        }
        var boneShards = BoneShardsLivingOsty().Match(clause);
        if (boneShards.Success)
        {
            operations =
            [
                new(CardSemanticKind.CompanionDamage, decimal.Parse(boneShards.Groups["damage"].Value), SemanticTarget.AllEnemies, clause, Condition: "OSTY_ALIVE", Timing: "immediate"),
                new(CardSemanticKind.Block, decimal.Parse(boneShards.Groups["block"].Value), SemanticTarget.Self, clause, Condition: "OSTY_ALIVE", Timing: "immediate")
            ];
            return true;
        }
        var sacrifice = SacrificeLivingOsty().Match(clause);
        if (sacrifice.Success)
        {
            var mult = sacrifice.Groups["mult"].Value == "三倍" ? 3 : 2;
            operations =
            [
                new(CardSemanticKind.Keyword, 1, SemanticTarget.Self, clause, Id: "KILL_OSTY", Condition: "OSTY_ALIVE", Timing: "immediate"),
                new(CardSemanticKind.DynamicBlock, mult, SemanticTarget.Self, clause, Id: "OSTY_MAX_HP_BLOCK", Condition: "OSTY_ALIVE", Timing: "immediate")
            ];
            return true;
        }
        var noEscape = NoEscapeDoomThreshold().Match(clause);
        if (noEscape.Success)
        {
            operations =
            [
                new(CardSemanticKind.ApplyStatus, decimal.Parse(noEscape.Groups["base"].Value), SemanticTarget.SelectedEnemy, clause, Id: "DOOM", Timing: "immediate"),
                new(CardSemanticKind.ApplyStatus, decimal.Parse(noEscape.Groups["bonus"].Value), SemanticTarget.SelectedEnemy, clause, Id: "DOOM_PER_TEN_DOOM", Condition: "TARGET_DOOM_THRESHOLD", Timing: "immediate")
            ];
            return true;
        }
        var tyranny = TyrannyTurnStart().Match(clause);
        if (tyranny.Success)
        {
            var drawCount = tyranny.Groups["drawNum"].Success ? int.Parse(tyranny.Groups["drawNum"].Value) : 1;
            var exhaustCount = int.Parse(tyranny.Groups["exhaust"].Value);
            operations =
            [
                new(CardSemanticKind.Draw, drawCount, SemanticTarget.Self, clause, Id: "TYRANNY_DRAW", Timing: "turn_start"),
                new(CardSemanticKind.ExhaustCards, exhaustCount, SemanticTarget.Hand, clause, Id: "TYRANNY_EXHAUST", Timing: "turn_start")
            ];
            return true;
        }
        var recurringHpBlock = TurnStartLoseHpGainBlock().Match(clause);
        if (recurringHpBlock.Success)
        {
            operations =
            [
                new(CardSemanticKind.LoseHp,
                    decimal.Parse(recurringHpBlock.Groups["hp"].Value),
                    SemanticTarget.Self,
                    clause,
                    Timing: "turn_start"),
                new(CardSemanticKind.Block,
                    decimal.Parse(recurringHpBlock.Groups["block"].Value),
                    SemanticTarget.Self,
                    clause,
                    Timing: "turn_start")
            ];
            return true;
        }
        if (clause == "将一张灵魂分别加入你的抽牌堆，手牌和弃牌堆中")
        {
            operations =
            [
                new(CardSemanticKind.GenerateCard, 1, SemanticTarget.DrawPile, clause, Id: "SOUL"),
                new(CardSemanticKind.GenerateCard, 1, SemanticTarget.Hand, clause, Id: "SOUL"),
                new(CardSemanticKind.GenerateCard, 1, SemanticTarget.DiscardPile, clause, Id: "SOUL")
            ];
            return true;
        }
        if (clause == "在你的回合开始时，获得能量并额外多抽1张牌")
        {
            operations =
            [
                new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                    Id: "TURN_START_ENERGY", Timing: "persistent_turn_start"),
                new(CardSemanticKind.ApplyStatus, 1, SemanticTarget.Self, clause,
                    Id: "TURN_START_DRAW", Timing: "persistent_turn_start")
            ];
            return true;
        }
        var parts = clause.Split(['，', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        var builder = ImmutableArray.CreateBuilder<CardSemanticOperation>(parts.Length);
        foreach (var part in parts)
        {
            var normalized = part.TrimStart().TrimStart('并').TrimStart('且');
            if (!TryCompileClause(normalized, out var operation)) return false;
            builder.Add(operation! with { SourceClause = part });
        }
        operations = builder.ToImmutable();
        return true;
    }

    private static bool IsImmediateExecutable(CardSemanticOperation operation)
    {
        if (operation.Timing != "immediate" || operation.Trigger is not null || operation.Condition is not null) return false;
        if (operation.Target == SemanticTarget.RandomEnemy) return false;
        return operation.Kind switch
        {
            CardSemanticKind.Damage or CardSemanticKind.Outbreak or CardSemanticKind.Block or CardSemanticKind.Draw or
            CardSemanticKind.DynamicDamage or CardSemanticKind.DynamicBlock or CardSemanticKind.MultiplyStatus or CardSemanticKind.AutoPlayCard or
            CardSemanticKind.GainEnergy or CardSemanticKind.ApplyStatus or CardSemanticKind.LoseHp or CardSemanticKind.LoseEnemyHp or
            CardSemanticKind.Heal or CardSemanticKind.ModifyMaxHp or CardSemanticKind.ModifyHandCosts or
            CardSemanticKind.ModifyCost or CardSemanticKind.ModifyCardDamage or CardSemanticKind.ClearEnemyBlockAndArtifact or
            CardSemanticKind.ModifyCardBlock or CardSemanticKind.UpgradeCard or CardSemanticKind.Forge => true,
            CardSemanticKind.CompanionDamage => operation.Target != SemanticTarget.RandomEnemy,
            CardSemanticKind.Summon => operation.Id is "SUMMON_X" or null,
            CardSemanticKind.TransformCards => operation.Id is "HAND_STATUS_TO_FUEL" or "HAND_STATUS_TO_FUEL_PLUS" or
                "HAND_ATTACKS_TO_GIANT_ROCK" or "HAND_ATTACKS_TO_GIANT_ROCK_PLUS" or
                "HAND_ONE_TO_MINION_STRIKE" or "HAND_ONE_TO_MINION_STRIKE_PLUS" or
                "DRAW_TWO_TO_MINION_DIVE_BOMB" or "DRAW_TWO_TO_MINION_DIVE_BOMB_PLUS" or
                "HAND_ANY_TO_MINION_SACRIFICE" or "HAND_ANY_TO_MINION_SACRIFICE_PLUS",
            CardSemanticKind.PlayRestriction or CardSemanticKind.KillAllDoomedEnemies or CardSemanticKind.Reboot or
            CardSemanticKind.DiscardHandAndGenerate => true,
            CardSemanticKind.MoveCard => operation.Id is "KINGS_BLADE_TO_HAND" or "SELF_TO_HAND" or "SELF_TO_DRAW_TOP" or "ALL_ZERO_COST_DISCARD_TO_HAND" or "RANDOM_RARE_TO_HAND",
            CardSemanticKind.Keyword => operation.Id is "消耗" or "固有" or "保留" or "虚无" or "永恒" or "不能被打出" or "（抽0张牌）" or "EXTRA_CARD_REWARD" or "GOLD_REWARD" or "RANDOM_POTION_REWARD",
            CardSemanticKind.DiscardCards or CardSemanticKind.ExhaustCards => operation.All,
            _ => false
        };
    }

    private static bool IsSimulatorExecutable(CardSemanticOperation operation)
    {
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Outbreak,
                Target: SemanticTarget.AllEnemies,
                Id: "OUTBREAK"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null or "OSTY_ALIVE",
                Kind: CardSemanticKind.CompanionDamage,
                Target: SemanticTarget.SelectedEnemy or SemanticTarget.AllEnemies or SemanticTarget.RandomEnemy,
                RandomSource: null or "CombatTargets"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.DynamicDamage,
                Id: "OSTY_CURRENT_HP_DAMAGE" or "OSTY_MAX_HP_DAMAGE" or "OSTY_ATTACK_CARDS_IN_DECK_DAMAGE"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "OSTY_ALIVE",
                Kind: CardSemanticKind.DynamicBlock,
                Id: "OSTY_MAX_HP_BLOCK"
            })
            return true;
        if (operation is
            {
                Timing: "immediate" or "next_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Summon
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "SOUL_PLAYED",
                Condition: null,
                Kind: CardSemanticKind.Summon
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "OSTY_ALIVE" or null,
                Kind: CardSemanticKind.Heal,
                Target: SemanticTarget.Companion
            } or
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "OSTY_ALIVE" or null,
                Kind: CardSemanticKind.Keyword,
                Id: "KILL_OSTY"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "NECRO_MASTERY" or "DEVOUR_LIFE"
            } or
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "SIC_EM"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.AutoPlayCard,
                Target: SemanticTarget.DrawPile or SemanticTarget.DiscardPile or SemanticTarget.Hand,
                Id: "TOP" or "TOP_FORCE_EXHAUST" or "RANDOM_ATTACK" or "RANDOM_ANY" or "DISCARD_RANDOM_ATTACK" or "HAND_SKILL_REPLAY_3"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "GENERATED_CARDS_FREE_THIS_COMBAT"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.RandomEnemy,
                RandomSource: "CombatTargets"
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: "RANDOM_ATTACK_HAND",
                Kind: CardSemanticKind.AutoPlayCard,
                Target: SemanticTarget.Hand,
                Id: "RANDOM_HAND_ATTACK",
                RandomSource: "Shuffle"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.AutoPlayCard,
                Target: SemanticTarget.ExhaustPile,
                Id: "ALL_ETHEREAL" or "ALL_SHIV" or "ALL_SHIV_UPGRADE"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "GENERATED_CARD_FREE_THIS_TURN"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "STRENGTH" or "FOCUS"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.AllEnemies,
                Id: "POISON"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ModifyCost,
                Id: "SELF_SET_ZERO"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "OSTY_ATTACKED_THIS_TURN",
                Kind: CardSemanticKind.ModifyCost,
                Id: "OSTY_ATTACKED_THIS_TURN_ZERO_COST"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.AutoPlayCard,
                Target: SemanticTarget.DrawPile,
                Id: "TOP"
            })
            return true;
        if (operation is { Trigger: null, Kind: CardSemanticKind.AutoPlayCard, Id: "SELF" } &&
            ((operation.Timing == "turn_end" && operation.Condition is "SELF_DRAW_TOP" or "SELF_IN_EXHAUST") ||
             (operation.Timing == "turn_start" && operation.Condition == "SELF_IN_EXHAUST")))
            return true;
        if (operation is
            {
                Timing: "immediate",
                Condition: null,
                Kind: CardSemanticKind.ModifyCardDamage,
                Id: "SELF" or "MODEL_ALL" or "BONUS_DAMAGE_PER_STAR_COST_CARD"
            })
            return operation.Trigger is null or "SELF_DRAWN";
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ModifyCardBlock,
                Id: "SELF"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Damage,
                AmountByCardsDrawnThisTurn: true
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.RandomEnemy,
                RepeatByExhaustedCount: true,
                RandomSource: "CombatTargets"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "CARD_EXHAUSTED",
                Kind: CardSemanticKind.ModifyCost,
                Id: "SELF_LISTENER_COST_DELTA"
            } or
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "CREATURE_DIED",
                Kind: CardSemanticKind.ModifyCost,
                Id: "SELF_DEATH_COST_DELTA"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "SOUL_PLAYED",
                Kind: CardSemanticKind.LoseEnemyHp,
                Target: SemanticTarget.RandomEnemy,
                RandomSource: "CombatTargets"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "SKILL_PLAYED",
                Kind: CardSemanticKind.Keyword,
                Id: "RETURN_SELF_TO_HAND_AFTER_SKILLS"
            })
            return true;
        if (operation is
            {
                Timing: "next_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "NEXT_TURN_DOUBLE_DAMAGE" or "STARS"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "SKILLS_COST_ZERO" or "SKILLS_EXHAUST_ON_PLAY" or
                    "FERAL_ZERO_COST_ATTACK_RETURN" or "NOSTALGIA_ATTACK_SKILL_TOPDECK" or
                    "ECHO_FORM_REPLAY_FIRST_CARDS" or "BARRICADE" or
                    "AUTOMATION_DRAW_ENERGY" or "ORBIT_ENERGY_REBATE" or
                    "MAX_ENERGY_DELTA" or "HAND_DRAW_DELTA" or "ITERATION_DRAW" or "PAGESTORM_DRAW"
                    or "TRIGGER_ATTACK_DAMAGE_DOOM" or "BONUS_WEAK_TARGET_POWERED_ATTACK_DAMAGE_PERCENT" or "UNMOVABLE" or
                    "SHIV_DAMAGE_BONUS" or "SHIV_ALL_ENEMIES" or "SHIV_RETAIN" or "FIRST_SHIV_DAMAGE_BONUS" or
                    "JUGGLING" or "ACCELERANT" or "BONUS_VULNERABLE_POWERED_ATTACK_DAMAGE_PERCENT" or
                    "HANG_DAMAGE_MULTIPLIER"
                    or "THE_GAMBIT" or "LETHALITY" or "PALE_BLUE_DOT" or "ONE_FOR_ALL" or "FASTEN" or "HELLRAISER"
                    or "POWERS_COST_DELTA" or "WELL_LAID_PLANS" or "TOOLS_OF_THE_TRADE"
                    or "KINGS_BLADE_ALL_ENEMIES" or "KINGS_BLADE_BLOCK" or "KINGS_BLADE_REPLAY" or "KINGS_BLADE_COST_DELTA"
                    or "BLACK_HOLE" or "CHILD_OF_THE_STARS"
                    or "MASTER_PLANNER" or "SHROUD" or "SLEIGHT_OF_FLESH" or "THUNDER" or "TRASH_TO_TREASURE"
                    or "CALCIFY" or "NECRO_MASTERY" or "ROLLING_BOULDER"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "THIRD_ATTACK_PLAYED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "JUGGLING"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "POWERED_ATTACK_UNBLOCKED_DAMAGE_RECEIVED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "THE_GAMBIT"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "FIRST_ATTACK_PLAYED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "LETHALITY"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "ATTACK_COUNT_REACHED_5",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "PALE_BLUE_DOT"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "BLOCK_GAINED",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.RandomEnemy,
                RandomSource: "CombatTargets"
            } or
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "CARD_PLAYED",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.RandomEnemy,
                RandomSource: "CombatTargets"
            } or
            {
                Timing: "this_turn",
                Condition: null,
                Trigger: "CARD_PLAYED",
                Kind: CardSemanticKind.LoseEnemyHp,
                Target: SemanticTarget.SelectedEnemy
            } or
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "POWERED_ATTACK_UNBLOCKED_DAMAGE",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.SelectedEnemy,
                Id: "POISON"
            } or
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "POWERED_ATTACK",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.SelectedEnemy,
                Id: "TEMP_STRENGTH_LOSS"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_until_consumed",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "NEXT_POWER_REPLAY" or "BUFFER"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "CARD_PLAY_LIMIT" or "TRIGGER_EVERY_FIVE_CARDS_ALL_DAMAGE"
            })
            return true;
        if (operation is
            {
                Timing: "while_in_hand",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.PlayRestriction,
                Id: "GLOBAL_CARD_PLAY_LIMIT_WHILE_IN_HAND"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "SELF_DRAWN",
                Condition: null,
                Kind: CardSemanticKind.GainEnergy
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "SELF_DRAWN",
                Condition: null,
                Kind: CardSemanticKind.ModifyCost,
                Id: "SELF"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "SELF_TO_DRAW_TOP" or "SELF_TO_HAND" or "ALL_ZERO_COST_DISCARD_TO_HAND"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.DiscardHandAndGenerate,
                Target: SemanticTarget.Hand,
                Id: "SHIV" or "SHIV_UPGRADE"
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: "SELF_IN_HAND",
                Kind: CardSemanticKind.Damage or CardSemanticKind.LoseHp,
                Target: SemanticTarget.Self
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: "SELF_IN_HAND",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "WEAK" or "FRAIL"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Condition: null,
                Trigger: "CARD_PLAYED",
                Kind: CardSemanticKind.Block
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "CARD_GENERATED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "STRENGTH",
                Condition: null or "FIRST_CARD_GENERATED_THIS_TURN"
            } or
            {
                Timing: "immediate",
                Trigger: "CARD_GENERATED",
                Kind: CardSemanticKind.Block,
                Condition: null or "FIRST_CARD_GENERATED_THIS_TURN"
            } or
            {
                Timing: "immediate",
                Trigger: "COLORLESS_CARD_PLAYED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "STRENGTH"
            } or
            {
                Timing: "immediate",
                Trigger: "STATUS_CARD_GENERATED",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.AllEnemies
            } or
            {
                Timing: "immediate",
                Trigger: "STATUS_CARD_GENERATED",
                Kind: CardSemanticKind.ModifyCost,
                Id: "SELF_BY_STATUS_GENERATED" or "SELF_BY_STATUS_GENERATED_TURN_ZERO" or
                    "SELF_BY_STATUS_GENERATED_UNTIL_PLAYED_ZERO"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Condition: null,
                Trigger: "CARD_PLAYED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "STRENGTH"
            } or
            {
                Timing: "this_turn",
                Condition: null,
                Trigger: "CARD_PLAYED",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.SelectedEnemy,
                Id: "DOOM"
            } or
            {
                Timing: "this_turn",
                Condition: null,
                Trigger: "CARD_DRAWN",
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.AllEnemies,
                Id: "POISON"
            } or
            {
                Timing: "immediate",
                Condition: null,
                Trigger: "NON_HAND_CARD_DRAWN",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.AllEnemies
            } or
            {
                Timing: "this_turn",
                Condition: null,
                Trigger: "POWERED_ATTACK_RECEIVED",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.SelectedEnemy
            } or
            {
                Timing: "persistent_combat",
                Condition: null,
                Trigger: "POWERED_ATTACK_RECEIVED",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.SelectedEnemy
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Condition: null,
                Trigger: "POWER_PLAYED",
                Kind: CardSemanticKind.GainEnergy or CardSemanticKind.ChannelOrb
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Condition: null,
                Trigger: "ETHEREAL_PLAYED",
                Kind: CardSemanticKind.Block
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Condition: null,
                Trigger: "ENERGY_THRESHOLD_PLAYED",
                Kind: CardSemanticKind.Block,
                Id: not null
            })
            return true;
        if (operation is
            {
                Timing: "persistent_turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "TURN_START_ENERGY" or "TURN_START_DRAW" or "TURN_START_RANDOM_DOOM"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "DOOM" or "VIGOR" or "DEXTERITY" or "FOCUS" or "TORIC_TOUGHNESS"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.AllEnemies,
                Id: "POISON"
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "THE_BOMB" or "DEBILITATE"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "KINGS_BLADE_DOUBLE_DAMAGE"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.UpgradeCard,
                Id: "HAND_ONE" or "HAND_ALL" or "ALL_COMBAT_CARDS" or "DISCARD_RANDOM"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.TransformCards,
                Id: "HAND_STATUS_TO_FUEL" or "HAND_STATUS_TO_FUEL_PLUS" or "HAND_ATTACKS_TO_GIANT_ROCK" or "HAND_ATTACKS_TO_GIANT_ROCK_PLUS"
                    or "DRAW_ONE_TO_SOUL" or "DRAW_ONE_TO_SOUL_PLUS"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.TransformCards,
                Id: "HAND_ONE_TO_MINION_STRIKE" or "HAND_ONE_TO_MINION_STRIKE_PLUS" or
                    "DRAW_TWO_TO_MINION_DIVE_BOMB" or "DRAW_TWO_TO_MINION_DIVE_BOMB_PLUS" or
                    "HAND_ANY_TO_MINION_SACRIFICE" or "HAND_ANY_TO_MINION_SACRIFICE_PLUS"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: "HIGH_COST_CARD_PLAYED",
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "DISCARD_SELF_TO_HAND_ON_HIGH_COST"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: "BLOCK_GAINED",
                Condition: null,
                Kind: CardSemanticKind.Damage,
                Id: "GAIN_BLOCK_DAMAGE"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "FLEE" or "QUICKSAND_COUNTER"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ModifyCost,
                Id: "INCREASE_COST"
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: "SELF_IN_HAND",
                Kind: CardSemanticKind.Keyword,
                Id: "LOSE_GOLD"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "奇巧"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "SELF_IN_HAND",
                Kind: CardSemanticKind.PlayRestriction,
                Id: "MUST_PLAY_FIRST"
            })
            return true;
        if (operation is
            {
                Timing: "out_of_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "REMOVE_FROM_DECK_AFTER_COMBATS" or "HATCH_AT_REST_SITE" or "UNLOCK_SPECIAL_EVENT" or "MAP_BONUS_GOLD_LOCATION"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                Id: "ABUNDANCE_POWER_CHOICE" or "ABUNDANCE_POWER_CHOICE_UPGRADED"
            })
            return true;
        if (operation is
            {
                Timing: "out_of_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.TransformCards,
                Id: "TRANSFORM_TO_ABUNDANCE"
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.EvokeOrb,
                Id: "LEFTMOST"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                Id: "扫荡凝视" or "SWEEPING_GAZE" or "随机能力牌" or "随机普通牌" or "随机无色牌"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "ATTACK_PLAYED",
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                Id: "随机攻击牌"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                AmountByEnergySpent: true,
                Id: "SOUL" or "SOUL_PLUS"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "TARGET_DOOM_THRESHOLD",
                Kind: CardSemanticKind.ApplyStatus,
                Id: "DOOM_PER_TEN_DOOM"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.PlayRestriction,
                Id: "END_TURN"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "FIRST_CARDS_FREE_EACH_TURN"
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: "ON_SHUFFLE",
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "SHUFFLE_CHOOSE_TO_HAND"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ChannelOrb,
                RepeatByHistoryCounter: true
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Draw,
                Id: "TYRANNY_DRAW"
            } or
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ExhaustCards,
                Id: "TYRANNY_EXHAUST"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "FIRST_PLAYED_THIS_TURN",
                Kind: CardSemanticKind.Draw,
                Id: "FIRST_PLAYED_THIS_TURN_DRAW"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.TransformCards,
                Id: "TURN_START_TRANSFORM_RANDOM"
            })
            return true;
        if (operation is
            {
                Timing: "next_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "DRAW_CHOOSE_TO_HAND"
            })
            return true;
        if (operation is
            {
                Timing: "combat_end",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ExhaustCards,
                Id: "COMBAT_END_REMOVE_CARD"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "SIC_EM"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Damage,
                Id: "ALL_OTHER_ENEMIES_EQUAL_DAMAGE"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "KINGS_BLADE_TO_HAND" or "SEEKER_STRIKE_DRAW_CHOOSE"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                Id: "DISCOVERY" or "DISCOVERY_UPGRADED" or "QUASAR_COLORLESS" or "QUASAR_COLORLESS_UPGRADED"
                    or "SPLASH_OTHER_ATTACK" or "SPLASH_OTHER_ATTACK_UPGRADED"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "NIGHTMARE_CHOOSE"
            })
            return true;
        if (operation is
            {
                Timing: "next_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                Id: "NIGHTMARE_COPIES"
            })
            return true;
        if (operation is
            {
                Timing: "combat_end",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "GOLD_REWARD"
            })
            return true;
        if (operation is
            {
                Timing: "combat_end",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.UpgradeCard,
                Id: "DECK_RANDOM"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "DISCARD_ATTACK_TO_HAND_UPGRADED"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "TRANSFER_TARGET_DEBUFFS_TO_OTHERS" or "HAND_SKILL_FINESSE" or "DRAW_CARD_REPLAY"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Keyword,
                Id: "RETAIN_HAND"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.GenerateCard,
                Id: "SHIV" or "RANDOM_ANY"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "DRAW_TOP_TO_EXHAUST"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.LoseHp or CardSemanticKind.Block
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: "OSTY_ALIVE",
                Kind: CardSemanticKind.Block
            })
            return true;
        if (operation is
            {
                Kind: CardSemanticKind.Keyword,
                Id: "DISCARD_DRAWN_NONZERO_COST"
            } or
            {
                Timing: "turn_start",
                Kind: CardSemanticKind.Keyword,
                Id: "虚无"
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.DynamicBlock,
                AmountByDistinctOrbTypes: true
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                AmountByDistinctOrbTypes: true
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                AmountByTargetVulnerableStacks: true
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                 Kind: CardSemanticKind.ApplyStatus,
                 Id: "CANNOT_DRAW" or "CANNOT_GAIN_ENERGY" or "STUN" or
                       "NEXT_FREE_ATTACK" or "NEXT_FREE_SKILL" or "NEXT_FREE_POWER" or "NEXT_FREE_ETHEREAL" or "BLUR"
                       or "NEXT_CARD_TO_DRAW_TOP" or "NEXT_ATTACK_REPLAY" or "NEXT_SKILL_REPLAY" or
                       "ALL_CARD_COST_DELTA" or "DOUBLE_BLOCK_GAINED" or "TEMP_STRENGTH_LOSS" or "FOCUS" or
                       "STRENGTH" or "DEXTERITY" or "TRIGGER_ATTACK_PLAYED_BLOCK" or "REDUCE_VULNERABLE_ATTACK_DAMAGE" or
                       "REFLECT_BLOCKED_ATTACK_DAMAGE"
            })
            return true;
        if (operation is
            {
                Timing: "next_turn_snapshot",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.DynamicBlock,
                Id: "PLAYER_BLOCK"
            })
            return true;
        if (operation is
            {
                Timing: "this_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ModifyHandCosts
            })
            return true;
        if (operation is
            {
                Timing: "persistent_combat",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ModifyHandCosts,
                Id: "CAP_AT_1"
            })
            return true;
        if (operation is
            {
                Timing: "turn_end",
                Trigger: null,
                Condition: "HAS_FROST_ORB",
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.AllEnemies
            })
            return true;
        if (operation is
            {
                Timing: "turn_start",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.TriggerOrbPassive,
                Id: "RIGHTMOST_ANY"
            })
            return true;
        if (operation is
            {
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.ChannelOrb
            } && operation.Timing.StartsWith("next_", StringComparison.Ordinal) &&
                 operation.Timing.EndsWith("_turn_starts", StringComparison.Ordinal))
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "CARD_EXHAUSTED",
                Condition: null,
                Kind: CardSemanticKind.Draw or CardSemanticKind.Block
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "SELF_EXHAUSTED",
                Condition: null,
                Kind: CardSemanticKind.GainEnergy
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "VULNERABLE_APPLIED",
                Condition: null,
                Kind: CardSemanticKind.Draw
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "PLAYER_HP_LOST",
                Condition: null,
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.AllEnemies
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: "PLAYER_HP_LOST",
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Target: SemanticTarget.Self,
                Id: "STRENGTH"
            })
            return true;
        if (operation.Condition is not null)
            return operation.Trigger is null &&
                   operation.Timing == "immediate" &&
                   IsSupportedCondition(operation.Condition) &&
                   operation.Kind is CardSemanticKind.Damage or CardSemanticKind.Block or CardSemanticKind.Draw or CardSemanticKind.GainEnergy or
                       CardSemanticKind.ModifyMaxHp or CardSemanticKind.ApplyStatus or CardSemanticKind.Keyword;
        if (IsImmediateExecutable(operation)) return true;
        if (operation is { Timing: "immediate", Trigger: null, Condition: null, Kind: CardSemanticKind.GenerateCard })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.SelectCard,
                Id: "HAND_COLORLESS" or "HAND_ANY_ADD_ETHEREAL" or "HAND_ANY_ADD_RETAIN" or "HAND_ANY_ADD_REPLAY" or
                    "HAND_ATTACK_OR_POWER_COPY"
            } or
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.CopySelectedCard,
                Id: "SELECTED_TO_HAND"
            })
            return true;
        if (operation is { Timing: "immediate" or "turn_start", Trigger: null, Condition: null, Kind: CardSemanticKind.ChannelOrb })
            return true;
        if (operation is { Timing: "turn_start", Trigger: null, Condition: null, Kind: CardSemanticKind.Forge })
            return true;
        if (operation is
            {
                Timing: "turn_start" or "next_turn" or "immediate",
                Trigger: null or "CARD_PLAYED",
                Condition: null,
                Kind: CardSemanticKind.ApplyStatus,
                Id: "RADIANCE"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.EvokeOrb or CardSemanticKind.ModifyOrbCapacity
            })
            return true;
        if (operation is { Timing: "immediate", Trigger: null, Condition: null, Kind: CardSemanticKind.TriggerOrbPassive })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.Damage,
                Target: SemanticTarget.RandomEnemy
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.RandomExhaustCards
            })
            return true;
        if (operation is { Timing: "immediate", Trigger: null, Condition: null, Kind: CardSemanticKind.DiscardHandThenDrawSame })
            return true;
        if (operation is { Timing: "immediate", Trigger: null, Condition: null, Kind: CardSemanticKind.Reboot })
            return true;
        if (operation is { Timing: "immediate", Trigger: null, Condition: null, Kind: CardSemanticKind.ExhaustNonAttacksAndBlock })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Id: "CHOOSE_DRAW_ANY_TO_HAND" or "CHOOSE_DRAW_ATTACK_TO_HAND" or
                     "CHOOSE_DRAW_SKILL_TO_HAND" or "CHOOSE_DRAW_ANY_TO_EXHAUST" or
                     "CHOOSE_DISCARD_ANY_TO_HAND" or "CHOOSE_DISCARD_UP_TO_TO_HAND" or
                     "CHOOSE_DISCARD_ANY_TO_DRAW_TOP" or "CHOOSE_HAND_ANY_TO_DRAW_TOP" or
                     "RANDOM_RARE_TO_HAND"
            })
            return true;
        if (operation is
            {
                Timing: "immediate",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.DiscardCards or CardSemanticKind.ExhaustCards,
                Amount: >= 1
            })
            return true;
        if (operation is
            {
                Timing: "next_turn_start" or "next_turn",
                Trigger: null,
                Condition: null,
                Kind: CardSemanticKind.MoveCard,
                Target: SemanticTarget.Hand,
                Id: "SELF"
            })
            return true;
        return operation is
        {
            Trigger: null,
            Condition: null,
            Timing: "next_turn",
            Kind: CardSemanticKind.GainEnergy or CardSemanticKind.Draw or CardSemanticKind.Block
        };
    }

    private static decimal Value(Match match) => match.Groups["amount"].Value switch
    {
        "一" => 1m,
        "两" => 2m,
        var value => decimal.Parse(value)
    };
    private static int Repeat(Match match)
    {
        if (match.Groups["repeat"].Success) return int.Parse(match.Groups["repeat"].Value);
        return match.Groups["repeatWord"].Value == "两" ? 2 : 1;
    }
    private static int XBonus(Match match) =>
        match.Groups["bonus"].Success ? int.Parse(match.Groups["bonus"].Value) : 0;

    private static string NormalizeStatus(string status) => status switch
    {
        "虚弱" => "WEAK",
        "易伤" => "VULNERABLE",
        "脆弱" => "FRAIL",
        "力量" => "STRENGTH",
        "敏捷" => "DEXTERITY",
        "集中" => "FOCUS",
        "无实体" => "INTANGIBLE",
        "荆棘" => "THORNS",
        "覆甲" => "PLATED_ARMOR",
        "活力" => "VIGOR",
        "毒" or "中毒" => "POISON",
        "灾厄" => "DOOM",
        _ => status
    };

    private static string NormalizeGeneratedCard(string card)
    {
        var upgraded = card.TrimEnd().EndsWith('+');
        var name = card.TrimEnd().TrimEnd('+');
        var id = name switch
        {
            "小刀" => "SHIV",
            "此牌的复制品" => "SELF_COPY",
            _ => name
        };
        return upgraded && id.All(static character => character <= 0x7f)
            ? id + "_UPGRADE"
            : upgraded ? name + "+" : id;
    }

    private static string NormalizeTiming(string timing) => timing switch
    {
        "在你的回合开始时" => "turn_start",
        "在你的回合结束时" => "turn_end",
        "在下个回合" or "在下回合" or "下一回合" or "下回合" => "next_turn",
        "在你的下个回合开始时" => "next_turn_start",
        "打出此牌后" => "after_play",
        "在战斗结束时" => "combat_end",
        _ => timing
    };

    private static bool TryNormalizeTrigger(string value, out string trigger)
    {
        var text = value.Trim().TrimEnd('时');
        trigger = text switch
        {
            "你打出一张牌" => "CARD_PLAYED",
            "你打出一张攻击牌" => "ATTACK_PLAYED",
            "你打出一张技能牌" => "SKILL_PLAYED",
            "你打出一张能力牌" => "POWER_PLAYED",
            "你打出一张虚无牌" => "ETHEREAL_PLAYED",
            "你打出一张灵魂" => "SOUL_PLAYED",
            "你生成一张牌" or "你生成一张卡牌" => "CARD_GENERATED",
            "你生成状态牌" or "你生成一张状态牌" or "有一张状态被生成" => "STATUS_CARD_GENERATED",
            "你抽到这张牌" => "SELF_DRAWN",
            "你获得格挡" => "BLOCK_GAINED",
            "你铸造" => "FORGED",
            "有一张牌被消耗" => "CARD_EXHAUSTED",
            "这张牌被消耗" => "SELF_EXHAUSTED",
            "你给予易伤" => "VULNERABLE_APPLIED",
            "你在你的回合内失去生命" or
                "你在你的回合内失去生命值" or
                "你在你的回合失去生命" or
                "你在你的回合失去生命值" => "PLAYER_HP_LOST",
            _ => string.Empty
        };
        return trigger.Length > 0;
    }

    private static bool TryNormalizeCondition(string value, out string condition)
    {
        condition = value.Trim() switch
        {
            "你的手牌中没有攻击牌" => "HAND_NO_ATTACKS",
            "你的消耗牌堆拥有大于等于3张牌" => "EXHAUST_AT_LEAST_3",
            "敌方拥有中毒" => "TARGET_HAS_POISON",
            "敌人的意图是攻击" => "TARGET_INTENDS_ATTACK",
            "该敌人有易伤状态" => "TARGET_HAS_VULNERABLE",
            "你有冰霜充能球" => "HAS_FROST_ORB",
            "这张牌击杀了敌人" or "此牌击杀了敌人" => "SOURCE_CARD_KILLED",
            "你在本回合失去过生命值" => "PLAYER_HP_LOST_THIS_TURN",
            "你在本回合中曾给予过灾厄" => "DOOM_APPLIED_THIS_TURN",
            "你在本回合消耗过卡牌" or "你在本回合获得消耗过卡牌" => "CARD_EXHAUSTED_THIS_TURN",
            _ => string.Empty
        };
        return condition.Length > 0;
    }

    private static bool IsSupportedCondition(string condition) => condition is
        "HAND_NO_ATTACKS" or "HAND_AT_LEAST_5" or "EXHAUST_AT_LEAST_3" or "TARGET_HAS_POISON" or
        "TARGET_HAS_VULNERABLE" or "TARGET_INTENDS_ATTACK" or "HAS_FROST_ORB" or "SOURCE_CARD_KILLED" or
        "PLAYER_HP_LOST_THIS_TURN" or "DOOM_APPLIED_THIS_TURN" or "CARD_EXHAUSTED_THIS_TURN" or "LAST_DRAWN_CARD_SKILL" or "HAND_EMPTY" ||
        condition.StartsWith("CARD_PLAYS_FINISHED_LT:", StringComparison.Ordinal) ||
        condition == "ENERGY_SPENT_AT_LEAST:4";

    [GeneratedRegex(@"^对所有敌人造成(?<amount>\d+)点伤害(?:[，,]?共?(?<repeat>\d+)次|(?<repeatWord>两)次)?$")]
    private static partial Regex AllEnemyDamage();
    [GeneratedRegex(@"^对所有敌人造成(?<amount>\d+)点伤害X(?:\+(?<bonus>\d+))?次$")]
    private static partial Regex AllEnemyDamageX();
    [GeneratedRegex(@"^造成(?<amount>\d+)点伤害(?:[，,]?共?(?<repeat>\d+)次|(?<repeat>\d+)次|(?<repeatWord>两)次)?$")]
    private static partial Regex Damage();
    [GeneratedRegex(@"^每张被消耗的牌造成(?<amount>\d+)点伤害$")]
    private static partial Regex ExhaustedCardDamage();
    [GeneratedRegex(@"^造成(?<amount>\d+)点伤害X(?:\+(?<bonus>\d+))?次$")]
    private static partial Regex DamageX();
    [GeneratedRegex(@"^如果X的最终数值为4或以上，则将X的数值翻倍$")]
    private static partial Regex DoubleXAtLeastFour();
    [GeneratedRegex(@"^当前每有一个充能球[，,]造成(?<amount>\d+)点伤害$")]
    private static partial Regex DamagePerOrb();
    [GeneratedRegex(@"^造成(?:等量于)?(?<source>你当前格挡值|你抽牌堆中剩余牌数|该敌人身上的灾厄层数)的伤害$")]
    private static partial Regex DynamicDamage();
    [GeneratedRegex(@"^(?<source>你每有一张名字中含有“打击”的牌|你的消耗牌堆中每有一张牌|该敌人身上每有一层易伤)[，,]?(?:就)?(?:伤害\+|伤害增加|额外造成)(?<amount>\d+)(?:点伤害)?$")]
    private static partial Regex AdditiveDamageModifier();
    [GeneratedRegex(@"^你的手牌中每有一张牌[，,]此牌的伤害就降低(?<amount>\d+)点$")]
    private static partial Regex HandCountDamagePenalty();
    [GeneratedRegex(@"^(?<source>手牌中每有一张技能牌|在这个回合内每打出过一张攻击牌|本回合中每打出过一张技能牌|本场战斗中每打出过一张虚无牌)[，,](?:造成|就造成|此牌(?:就)?(?:额外)?造成)(?<amount>\d+)点伤害(?:一次)?$")]
    private static partial Regex RepeatedDamageByCounter();
    [GeneratedRegex(@"^在本场战斗中[，,]?你每失去过一次生命值[，,](?:此牌|这张牌)就额外造成一次伤害$")]
    private static partial Regex DamagePerReceivedEvent();
    [GeneratedRegex(@"^(?<source>你在本场战斗中每抽过一张牌|你在本场战斗中每生成过一张牌)[，,](?:此牌|这张牌)就额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex CombatCounterDamageBonus();
    [GeneratedRegex(@"^本回合每丢弃过一张牌[，,](?:则|(?:此牌|这张牌)?(?:就)?)增加(?<amount>\d+)点额外伤害$")]
    private static partial Regex DiscardCounterDamageBonus();
    [GeneratedRegex(@"^(?<actor>奥斯提)造成(?<amount>\d+)点伤害(?:(?<repeat>\d+)次|(?<repeatWord>两)次)?$")]
    private static partial Regex CompanionDamage();
    [GeneratedRegex(@"^(?:额外)?获得(?<amount>\d+)点格挡$")]
    private static partial Regex Block();
    [GeneratedRegex(@"^额外获得(?<amount>\d+)次格挡$")]
    private static partial Regex ExtraBlock();
    [GeneratedRegex(@"^你在接下来的(?<amount>\d+)回合内无法再从卡牌中获得格挡$")]
    private static partial Regex NoBlockFromCards();
    [GeneratedRegex(@"^获得等(?:量于|同于)(?<source>与你当前弃牌堆中牌数|你当前弃牌堆中牌数|所有敌人中毒层数|所造成伤害)(?:\+(?<bonus>\d+))?的格挡(?:值)?$")]
    private static partial Regex DynamicBlock();
    [GeneratedRegex(@"^你每拥有(?:一|1)点力量[，,](?:这张牌|此牌)就额外获得(?<amount>\d+)点格挡$")]
    private static partial Regex StrengthScaledBlock();
    [GeneratedRegex(@"^(?<timing>在你的回合开始时[，,]?)?你每有一种不同的充能球[，,]就获得(?<amount>\d+)点格挡$")]
    private static partial Regex DistinctOrbBlock();
    [GeneratedRegex(@"^抽(?<amount>\d+)张牌$")]
    private static partial Regex Draw();
    [GeneratedRegex(@"^抽牌直至你的手牌有(?<amount>\d+)张牌$")]
    private static partial Regex DrawToHandSize();
    [GeneratedRegex(@"^如果抽到的是技能牌[，,]则获得(?<amount>\d+)点格挡$")]
    private static partial Regex DrawnSkillBlock();
    [GeneratedRegex(@"^每回合你第一次抽到状态牌时，抽(?<amount>\d+)张牌$")]
    private static partial Regex IterationDraw();
    [GeneratedRegex(@"^每当你抽到一张虚无牌时[，,]\s*抽(?<amount>\d+)张牌$")]
    private static partial Regex PagestormDraw();
    [GeneratedRegex(@"^打出此牌后，你在这个回合内每打出一张攻击牌，获得(?<amount>\d+)点格挡$")]
    private static partial Regex RageAttackBlock();
    [GeneratedRegex(@"^每当你获得格挡时[，,]\s*对随机敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex BlockGainedRandomEnemyDamage();
    [GeneratedRegex(@"^将你在每回合打出的第三张攻击牌的复制品加入你的手牌$")]
    private static partial Regex JugglingThirdAttackCopy();
    [GeneratedRegex(@"^你每打出一张牌[，,]\s*就对随机一名敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex CardPlayedRandomEnemyDamage();
    [GeneratedRegex(@"^(?:你在这个回合内每出一张牌[，,]\s*该名敌人都会失去|本回合[，,]\s*你每打出一张牌[，,]\s*该敌人失去)(?<amount>\d+)点生命$")]
    private static partial Regex CardPlayedTargetHpLoss();
    [GeneratedRegex(@"^每有一次攻击造成未被格挡的伤害[，,]\s*就给予(?<amount>\d+)层中毒$")]
    private static partial Regex UnblockedPoweredAttackPoison();
    [GeneratedRegex(@"^每当你攻击敌人的时候[，,]\s*这名敌人在本回合失去(?<amount>\d+)点力量$")]
    private static partial Regex PoweredAttackTemporaryStrengthLoss();
    [GeneratedRegex(@"^每当你在本回合打出卡牌时[，,]\s*在本回合获得(?<amount>\d+)点力量$")]
    private static partial Regex MonologueCardPlayedStrength();
    [GeneratedRegex(@"^(?:打出此牌后[，,]\s*)?你在本回合内?每打出一张牌[，,]\s*就给予该敌人(?<amount>\d+)层灾厄$")]
    private static partial Regex OblivionCardPlayedDoom();
    [GeneratedRegex(@"^打出此牌后[，,]\s*你在本回合内?每抽到一张牌[，,]\s*就给予所有敌人(?<amount>\d+)层中毒$")]
    private static partial Regex CorrosiveWaveDrawPoison();
    [GeneratedRegex(@"^每当你在回合进行中抽到一张牌时[，,]\s*对所有敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex SpeedsterNonHandDrawDamage();
    [GeneratedRegex(@"^你在这个回合每受到一次攻击[，,]\s*都会对攻击者造成(?<amount>\d+)点伤害$")]
    private static partial Regex FlameBarrierRetaliation();
    [GeneratedRegex(@"^每当你被攻击时[，,]\s*对攻击者造成(?<amount>\d+)点伤害$")]
    private static partial Regex PersistentAttackRetaliation();
    [GeneratedRegex(@"^阻止下(?<amount>\d+)次你受到的生命值损伤$")]
    private static partial Regex PreventHpLoss();
    [GeneratedRegex(@"^每回合的第一张攻击牌会造成(?<amount>\d+)%额外伤害$")]
    private static partial Regex LethalityFirstAttackBonus();
    [GeneratedRegex(@"^如果你在一回合内打出了大于等于5张牌，在下个回合开始时抽(?<amount>[12])张牌$")]
    private static partial Regex PaleBlueDotThresholdDraw();
    [GeneratedRegex(@"^所有人的0能量费攻击牌额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex OneForAllZeroCostAttackBonus();
    [GeneratedRegex(@"^从“防御”牌中额外获得(?<amount>\d+)点格挡$")]
    private static partial Regex FastenDefendBonus();
    [GeneratedRegex(@"^你在本回合不能打出超过(?<amount>\d+)张牌$")]
    private static partial Regex HandCardPlayLimit();
    [GeneratedRegex(@"^你在每个回合不能打出超过(?<amount>\d+)张牌$")]
    private static partial Regex PersistentCardPlayLimit();
    [GeneratedRegex(@"^每当你在一回合内打出五张牌时[，,]\s*对所有敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex EveryFiveCardsAllDamage();
    [GeneratedRegex(@"^每当你打出一张灵魂时[，,]\s*随机一名敌人失去(?<amount>\d+)点生命$")]
    private static partial Regex SoulPlayedRandomEnemyHpLoss();
    [GeneratedRegex(@"^中毒会额外触发(?<amount>\d+)次$")]
    private static partial Regex AccelerantExtraTriggers();
    [GeneratedRegex(@"^每当你给予(?<repeat>\d+)次中毒[，,]就对所有敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex OutbreakClause();
    [GeneratedRegex(@"^本场战斗[，,]\s*任何人每消耗过一张牌[，,]\s*这张牌的能量费用减少(?<amount>\d+)$")]
    private static partial Regex ExhaustListenerSelfCostReduction();
    [GeneratedRegex(@"^每当有任何生物死亡时[，,]?\s*这张牌的耗能减少能量$")]
    private static partial Regex CreatureDeathSelfCostReduction();
    [GeneratedRegex(@"^(?:将)?这张牌在(?:本场战斗|本局游戏)中的伤害(?:永久性)?增加(?<amount>\d+)$")]
    private static partial Regex SelfCombatDamageGrowth();
    [GeneratedRegex(@"^每当你抽到这张牌时[，,]\s*在这场战斗中其伤害增加(?<amount>\d+)$")]
    private static partial Regex SelfDrawDamageGrowth();
    [GeneratedRegex(@"^你在回合进行中每抽到一张牌[，,]?\s*都会使其额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex DeathMarchDrawDamage();
    [GeneratedRegex(@"^(?:本场战斗中所有爪击卡牌|在这场战斗中[，,]?\s*将所有[“""]?撕咬[”""]?牌)的伤害增加(?<amount>\d+)点?$")]
    private static partial Regex AllNamedCombatDamageGrowth();
    [GeneratedRegex(@"^每打出一次[，,]?\s*这张牌在(?:本局游戏|本场战斗)中的格挡值永久增加(?<amount>\d+)点?$")]
    private static partial Regex SelfCombatBlockGrowth();
    [GeneratedRegex(@"^随机打出你的抽牌堆中的(?<amount>\d+)张攻击牌$")]
    private static partial Regex RandomDrawAttackAutoPlay();
    [GeneratedRegex(@"^在你的回合结束时，随机打出你手牌中的(?<amount>\d+)张攻击牌攻击随机敌人$")]
    private static partial Regex RandomHandAttackAutoPlay();
    [GeneratedRegex(@"^从你的抽牌堆中随机打出(?<amount>\d+)张牌$")]
    private static partial Regex RandomDrawAutoPlay();
    [GeneratedRegex(@"^每有一张被消耗的牌[，,]\s*就随机对敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex RandomDamagePerExhaustedCard();
    [GeneratedRegex(@"^你每有一种不同的充能球[，,]就抽一张牌$")]
    private static partial Regex DistinctOrbDraw();
    [GeneratedRegex(@"^给予(?<amount>\d+)层(?<status>虚弱|易伤|毒|中毒|灾厄)$")]
    private static partial Regex EnemyStatus();
    [GeneratedRegex(@"^随机给予敌人(?<amount>\d+)层(?<status>虚弱|易伤|毒|中毒|灾厄)(?:(?<repeat>\d+)次|(?<repeatWord>两)次)$")]
    private static partial Regex RandomEnemyStatus();
    [GeneratedRegex(@"^在你的回合开始时[，,]?\s*给予随机敌人(?<amount>\d+)层灾厄$")]
    private static partial Regex TurnStartRandomEnemyDoom();
    [GeneratedRegex(@"^给予X(?:\+(?<bonus>\d+))?层(?<status>虚弱|易伤|毒|中毒|灾厄)$")]
    private static partial Regex EnemyStatusX();
    [GeneratedRegex(@"^给予所有敌人(?<amount>\d+)层(?<status>虚弱|易伤|毒|中毒|灾厄)$")]
    private static partial Regex AllEnemyStatus();
    [GeneratedRegex(@"^给予所有敌人(?<amount>\d+)层(?<first>虚弱|易伤|毒|中毒|灾厄)和(?:(?<secondAmount>\d+)层)?(?<second>虚弱|易伤|毒|中毒|灾厄)$")]
    private static partial Regex AllEnemyPairedStatuses();
    [GeneratedRegex(@"^将该敌人身上的(?<status>虚弱|易伤|毒|中毒|灾厄)层数翻倍$")]
    private static partial Regex DoubleEnemyStatus();
    [GeneratedRegex(@"^随机对敌人造成(?<amount>\d+)点伤害(?:(?<repeat>\d+)次|(?<repeatWord>两)次)?$")]
    private static partial Regex RandomDamage();
    [GeneratedRegex(@"^随机对敌人造成(?<amount>\d+)点伤害X次$")]
    private static partial Regex RandomDamageX();
    [GeneratedRegex(@"^获得(?<amount>\d+)(?:点|层)(?<status>力量|敏捷|集中|无实体|荆棘|覆甲|活力)$")]
    private static partial Regex SelfStatus();
    [GeneratedRegex(@"^获得(?<amount>\d+)(?<status>无实体)$")]
    private static partial Regex NumericSelfStatusNoUnit();
    [GeneratedRegex(@"^给予自身(?<amount>\d+)层灾厄$")]
    private static partial Regex SelfDoom();
    [GeneratedRegex(@"^在本回合(?:内)?获得(?<amount>\d+)点(?<status>力量|敏捷|集中)$")]
    private static partial Regex TemporarySelfStatus();
    [GeneratedRegex(@"^你每有一种不同的充能球[，,]就在本回合获得(?<amount>\d+)点集中$")]
    private static partial Regex DistinctOrbFocus();
    [GeneratedRegex(@"^失去(?<amount>\d+)点(?<status>力量|敏捷|集中)$")]
    private static partial Regex LoseStatus();
    [GeneratedRegex(@"^在本回合(?:内)?失去(?<amount>\d+)点(?<status>力量|敏捷|集中)$")]
    private static partial Regex TemporaryLoseStatus();
    [GeneratedRegex(@"^(?:(?<all>所有敌人)|敌人)失去(?<amount>\d+)点力量$")]
    private static partial Regex EnemyLoseStrength();
    [GeneratedRegex(@"^(?:(?<all>所有敌人)(?:在本回合中?|在本回合内?)失去|(?:使|让)?一名敌人(?:在本回合中?|在本回合内?)失去|敌人(?:在本回合中?|在本回合内?)失去)(?<amount>\d+)点力量$")]
    private static partial Regex TemporaryEnemyStrengthLoss();
    [GeneratedRegex(@"^敌人失去X(?:\+(?<bonus>\d+))?点力量$")]
    private static partial Regex EnemyLoseStrengthX();
    [GeneratedRegex(@"^该敌人获得(?<amount>\d+)点力量$")]
    private static partial Regex EnemyGainStrength();
    [GeneratedRegex(@"^敌人身上每有一层易伤[，,]就获得(?<amount>\d+)点力量$")]
    private static partial Regex StrengthFromVulnerable();
    [GeneratedRegex(@"^获得(?<icons>(?:辉星)+)$")]
    private static partial Regex Radiance();
    [GeneratedRegex(@"^获得(?<amount>\d+)辉星$")]
    private static partial Regex NumericRadiance();
    [GeneratedRegex(@"^获得(?<amount>\d+)能量$")]
    private static partial Regex NumericEnergy();
    [GeneratedRegex(@"^获得(?<icons>(?:能量)+)$")]
    private static partial Regex IconEnergy();
    [GeneratedRegex(@"^获得(?<icons>(?:16px\|link=)+)$")]
    private static partial Regex HistoricalBareEnergyGain();
    [GeneratedRegex(@"^获得(?<amount>\d+)16px\|link=$")]
    private static partial Regex HistoricalNumericEnergyGain();
    [GeneratedRegex(@"^失去(?:(?<amount>\d+)能量|(?<icons>(?:能量)+))$")]
    private static partial Regex LoseEnergy();
    [GeneratedRegex(@"^在下个回合获得(?:(?<amount>\d+)能量|(?<icons>(?:能量)+))$")]
    private static partial Regex NextTurnEnergy();
    [GeneratedRegex(@"^(?:在下个回合|下一回合)抽(?<amount>\d+)张牌$")]
    private static partial Regex NextTurnDraw();
    [GeneratedRegex(@"^在下一回合获得(?<amount>\d+)点格挡$")]
    private static partial Regex NextTurnBlock();
    [GeneratedRegex(@"^(?:下个回合|下一回合)，抽(?<amount>\d+)张牌并获得(?<icons>(?:能量)+)$")]
    private static partial Regex NextTurnDrawEnergy();
    [GeneratedRegex(@"^(?<verb>丢弃|弃|消耗)(?<amount>\d+|一|两)张牌$")]
    private static partial Regex CardMovement();
    [GeneratedRegex(@"^随机消耗(?<amount>\d+)张牌$")]
    private static partial Regex RandomExhaust();
    [GeneratedRegex(@"^每消耗一张牌[，,]将1张(?<upgraded>随机已升级的牌|随机牌)加入你的手牌$")]
    private static partial Regex StokeGeneratedCards();
    [GeneratedRegex(@"^(?<verb>丢弃|消耗)(?:你的)?(?:所有)?手牌$")]
    private static partial Regex AllCardMovement();
    [GeneratedRegex(@"^丢弃你的所有手牌[，,]然后抽相同数量的牌$")]
    private static partial Regex DiscardAndRedraw();
    [GeneratedRegex(@"^将你的所有未消耗的卡牌重新洗牌放入抽牌堆$")]
    private static partial Regex Reboot();
    [GeneratedRegex(@"^每丢弃一张牌[，,]?就将一张(?<card>小刀\+?)添加至你的手牌$")]
    private static partial Regex DiscardHandAndGenerate();
    [GeneratedRegex(@"^消耗手牌中所有非攻击牌[，,]每张获得(?<amount>\d+)点格挡$")]
    private static partial Regex ExhaustNonAttacksForBlock();
    [GeneratedRegex(@"^将(?<amount>\d+|一|两)张牌从弃牌堆加入你的手牌$")]
    private static partial Regex DiscardPileSelection();
    [GeneratedRegex(@"^将你抽牌堆中的一张牌放入你的手牌$")]
    private static partial Regex DrawPileDirectSelection();
    [GeneratedRegex(@"^将你弃牌堆中的至多(?<amount>\d+)张牌放入你的手牌$")]
    private static partial Regex DiscardPileUpToSelection();
    [GeneratedRegex(@"^将(?:你)?弃牌堆中的一张牌放到(?:你的)?抽牌堆(?:的)?顶部$")]
    private static partial Regex DiscardPileToDrawTop();
    [GeneratedRegex(@"^将(?:你)?手牌中的(?:1|一)张牌放到(?:你的)?抽牌堆(?:的)?(?:顶部|顶端)$")]
    private static partial Regex HandToDrawTop();
    [GeneratedRegex(@"^将这张牌的一张0(?:能量|\[能量\]|16px\|link=(?:能量)?)复制品添加到你的弃牌堆$")]
    private static partial Regex AdaptiveStrikeCopy();
    [GeneratedRegex(@"^将(?<amount>\d+|一|两)张(?<card>[^张]+?)(?:加入|添加到|添加至)你的(?<pile>手牌|弃牌堆)(?:中)?$")]
    private static partial Regex GenerateCard();
    [GeneratedRegex(@"^在你的手牌中加入(?<amount>\d+|一|两)张(?<card>[^张]+?)$")]
    private static partial Regex GenerateCardInHand();
    [GeneratedRegex(@"^添加(?<amount>\d+|一|两)张小刀到你的手牌$")]
    private static partial Regex AddShivsToHand();
    [GeneratedRegex(@"^添加(?<amount>\d+|一|两)张墨影小刀到你的手牌$")]
    private static partial Regex AddInkyShivsToHand();
    [GeneratedRegex(@"^在你的弃牌堆中加入(?<amount>\d+|一|两)张(?<card>[^张]+?)$")]
    private static partial Regex GenerateCardInDiscardPile();
    [GeneratedRegex(@"^将(?:(?<amount>\d+|一|两)张|一张)(?<card>灵魂\+?|小刀)(?:放入|加入)你的抽牌堆(?:中)?$")]
    private static partial Regex GenerateCardToDrawPile();
    [GeneratedRegex(@"^在你的抽牌堆中加入(?<amount>\d+)张随机攻击牌$")]
    private static partial Regex RandomAttacksToDrawPile();
    [GeneratedRegex(@"^从(?:你的)?抽牌堆中选择一张(?<filter>攻击|技能)?牌(?<destination>放入你的手牌|将其消耗)$")]
    private static partial Regex DrawPileSelection();
    [GeneratedRegex(@"^失去(?<amount>\d+)点生命$")]
    private static partial Regex LoseHp();
    [GeneratedRegex(@"^敌人失去(?<amount>\d+)点生命$")]
    private static partial Regex EnemyLoseHp();
    [GeneratedRegex(@"^(?:回复|恢复)(?<amount>\d+)点生命$")]
    private static partial Regex Heal();
    [GeneratedRegex(@"^失去(?<amount>\d+)点最大生命$")]
    private static partial Regex LoseMaxHp();
    [GeneratedRegex(@"^斩杀时[，,]?永久获得(?<amount>\d+)点最大生命值$")]
    private static partial Regex FeedMaxHp();
    [GeneratedRegex(@"^(?:这张牌的耗能加1|这张牌的耗能增加1能量)$")]
    private static partial Regex SelfCostIncrease();
    [GeneratedRegex(@"^这张牌的耗能减少(?<amount>\d+)$")]
    private static partial Regex SelfCostReduction();
    [GeneratedRegex(@"^其耗能增加(?<amount>\d+)能量$")]
    private static partial Regex SelectedHandCostIncrease();
    [GeneratedRegex(@"^你在本回合中每打出过一张(?<type>攻击|技能)牌[，,]其耗能减少1能量$")]
    private static partial Regex CostReducedByPlayedType();
    [GeneratedRegex(@"^本场战斗中每打出过一张虚无牌[，,]此牌(?:的耗能|费用)就减少(?<icons>(?:能量)+)$")]
    private static partial Regex CostReducedByEtherealPlayed();
    [GeneratedRegex(@"^每当你生成状态牌时[，,]此牌的耗能将在下一次打出前减少1能量$")]
    private static partial Regex StatusGeneratedCostReduction();
    [GeneratedRegex(@"^(?:每当你生成状态牌时|当有一张状态被生成时)[，,](?:将此牌的耗能在|此牌的耗能将在)(?<lifetime>本回合|下一次打出前)降为0能量$")]
    private static partial Regex StatusGeneratedCostZero();
    [GeneratedRegex(@"^你(?:的|打出的)下一张(?<type>攻击|技能|能力|虚无)牌耗能变为0能量$")]
    private static partial Regex NextCardFree();
    [GeneratedRegex(@"^生成(?<amount>\d+)个(?<orb>闪电|冰霜|黑暗|等离子|玻璃)充能球$")]
    private static partial Regex ChannelOrb();
    [GeneratedRegex(@"^生成(?<amount>\d+)个随机充能球$")]
    private static partial Regex ChannelRandomOrb();
    [GeneratedRegex(@"^生成(?<amount>\d+)(?<orb>闪电|冰霜|黑暗|等离子|玻璃)充能球$")]
    private static partial Regex ChannelOrbCompact();
    [GeneratedRegex(@"^生成X(?:\+(?<bonus>\d+))?个(?<orb>闪电|冰霜|黑暗|等离子)充能球$")]
    private static partial Regex ChannelOrbX();
    [GeneratedRegex(@"^当前每有一名敌人[，,]就生成(?<amount>\d+)个(?<orb>闪电|冰霜|黑暗|等离子)充能球$")]
    private static partial Regex ChannelOrbPerEnemy();
    [GeneratedRegex(@"^激发你(?:的)?(?<position>所有|最左侧|最右侧)的?(?:一个)?充能球(?:(?<repeat>\d+)次|(?<repeatWord>两)次)?$")]
    private static partial Regex EvokeOrb();
    [GeneratedRegex(@"^激发你最右侧的充能球X(?:\+(?<bonus>\d+))?次$")]
    private static partial Regex EvokeOrbX();
    [GeneratedRegex(@"^(?<verb>获得|失去)(?<amount>\d+|一|两)个充能球栏位$")]
    private static partial Regex ModifyOrbCapacity();
    [GeneratedRegex(@"^(?:(?<target>对该敌人)触发你的|触发)所有(?<orb>闪电|冰霜|黑暗|等离子)充能球的被动\s*(?<repeatWord>两)?次?$")]
    private static partial Regex TriggerOrbPassive();
    [GeneratedRegex(@"^召唤(?<amount>\d+)$")]
    private static partial Regex Summon();
    [GeneratedRegex(@"^铸造(?<amount>\d+)$")]
    private static partial Regex Forge();
    [GeneratedRegex(@"^(?<timing>在你的回合开始时|在你的回合结束时|在你的下个回合开始时|在下个回合|在下回合|下一回合|下回合|打出此牌后|在战斗结束时)[，,]?(?<action>.+)$")]
    private static partial Regex TimedClause();
    [GeneratedRegex(@"^(?:每当|当)(?<trigger>.+?)[，,](?<action>.+)$")]
    private static partial Regex TriggeredClause();
    [GeneratedRegex(@"^你每回合第一次生成一张卡牌时[，,](?<action>.+)$")]
    private static partial Regex FirstCardGeneratedClause();
    [GeneratedRegex(@"^你?每打出一张无色牌[，,](?<action>.+)$")]
    private static partial Regex ColorlessCardPlayedClause();
    [GeneratedRegex(@"^你每打出一张牌[，,](?<action>.+)$")]
    private static partial Regex RecurringCardPlayedClause();
    [GeneratedRegex(@"^消耗你的抽牌堆顶部的牌$")]
    private static partial Regex DrawTopToExhaust();
    [GeneratedRegex(@"^这张牌被消耗时[，,](?<action>.+)$")]
    private static partial Regex SelfExhaustedClause();
    [GeneratedRegex(@"^每当你打出(?:一张)?耗能(?:大于等于(?<icons>(?:能量)+)|为(?<icons>(?:能量)+)或以上)的牌(?:时)?[，,]获得(?<amount>\d+)点格挡$")]
    private static partial Regex HighCostCardPlayedBlock();
    [GeneratedRegex(@"^如果(?<condition>.+?)[，,](?:则)?(?<action>.+)$")]
    private static partial Regex ConditionalClause();
    [GeneratedRegex(@"^如果(?<condition>该敌人有易伤状态|你的手牌中还有至少5张其他牌)[，,](?:则)?(?:攻击两次|额外命中一次)$")]
    private static partial Regex ConditionalExtraHit();
    [GeneratedRegex(@"^如果(?<condition>你在本回合失去过生命值)[，,](?:则)?攻击(?<repeat>[23])次$")]
    private static partial Regex ConditionalAttackRepeat();
    [GeneratedRegex(@"^在你的回合开始时[，,]失去(?<hp>\d+)点生命并获得(?<block>\d+)点格挡$")]
    private static partial Regex TurnStartLoseHpGainBlock();
    [GeneratedRegex(@"^在回合开始时[，,]获得(?<icons>(?:能量)+)$")]
    private static partial Regex TurnStartEnergyWithoutOwner();
    [GeneratedRegex(@"^在下(?<turns>\d+)个回合开始时[，,]生成(?<amount>\d+)个(?<orb>闪电|冰霜|黑暗|等离子)充能球$")]
    private static partial Regex NextTurnsChannelOrb();
    [GeneratedRegex(@"^在接下来的(?<turns>\d+)个回合开始时[，,]获得(?<amount>\d+)点格挡$")]
    private static partial Regex NextTurnsGainBlock();
    [GeneratedRegex(@"^在(?<turns>\d+)回合结束后[，,]对所有敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex DelayedAllEnemyDamage();
    [GeneratedRegex(@"^能力牌的耗能减少(?<amount>\d+)能量$")]
    private static partial Regex PowerCostReduction();
    [GeneratedRegex(@"^在你的回合开始时[，,]触发你最右侧的一个充能球的被动能力(?<repeat>\d+)?次?$")]
    private static partial Regex TurnStartRightmostOrbPassive();
    [GeneratedRegex(@"^在你的回合结束时[，,]?\s*(?:如果这张牌在你的手牌中[，,]?\s*)?(?:你)?受到(?<amount>\d+)点伤害$")]
    private static partial Regex TurnEndInHandDamage();
    [GeneratedRegex(@"^在你的回合结束时[，,]?\s*如果这张牌在你的手牌中[，,]?\s*(?:你)?失去(?<amount>\d+)点生命$")]
    private static partial Regex TurnEndInHandHpLoss();
    [GeneratedRegex(@"^在你的回合结束时[，,]?\s*如果这张牌在你的手牌中[，,]?\s*失去相当于手牌数量的生命$")]
    private static partial Regex TurnEndInHandRegret();
    [GeneratedRegex(@"^在你的回合结束时[，,]?\s*如果这张牌在你的手牌中[，,]?\s*(?:则)?获得(?<amount>\d+)层(?<status>虚弱|脆弱)$")]
    private static partial Regex TurnEndInHandStatus();
    [GeneratedRegex(@"^将(?:(?<amount>一|两|\d+)张(?:此牌|该牌)的复制品|(?:此牌|该牌)的(?<amount>一|两|\d+)张复制品)加入你的手牌$")]
    private static partial Regex SelectedCardCopiesToHand();
    [GeneratedRegex(@"^从手牌中选择最多(?<amount>\d+)张牌消耗$")]
    private static partial Regex ExhaustUpToHandCards();
    [GeneratedRegex(@"^该名敌人身上每有一种负面效果[，,]就额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex BonusDamagePerTargetDebuffType();
    [GeneratedRegex(@"^你的消耗牌堆中每有一张灵魂[，,]伤害增加(?<amount>\d+)$")]
    private static partial Regex BonusDamagePerExhaustedSoul();
    [GeneratedRegex(@"^如果你在这回合打出的牌数小于(?<maximum>\d+)张[，,]抽(?<draw>\d+)张牌$")]
    private static partial Regex ConditionalDrawByCardPlayCount();
    [GeneratedRegex(@"^如果你的手牌为空[，,]则抽(?<draw>\d+)张牌并获得(?<icons>(?:能量)+)$")]
    private static partial Regex HandEmptyDrawEnergy();
    [GeneratedRegex(@"^小刀额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex ShivDamageBonus();
    [GeneratedRegex(@"^你在每回合打出的第一张小刀额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex FirstShivDamageBonus();
    [GeneratedRegex(@"^你每在一回合内打出(?<amount>\d+)张技能牌[，,]就将这张牌放入你的手牌$")]
    private static partial Regex ReturnSelfAfterSkills();
    [GeneratedRegex(@"^有易伤状态的敌人额外受到(?<amount>\d+)%的伤害$")]
    private static partial Regex CrueltyBonus();
    [GeneratedRegex(@"^(?:在你的回合结束时[，,]保留最多(?<amount>\d+)张牌|在你的回合结束时[，,](?:你)?不再丢弃(?:你的)?手牌)$")]
    private static partial Regex RetainUpToCardsTurnEnd();
    [GeneratedRegex(@"^在你的回合开始时[，,]抽(?<amount>\d+)张牌[，,]丢弃(?<discard>\d+)张牌$")]
    private static partial Regex TurnStartDrawDiscard();
    [GeneratedRegex(@"^你每有一张拥有(?:\[STAR\]|16px\|link=)?(?:辉星)?耗能的卡牌[，,]这张牌就额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex BonusDamagePerStarCostCard();
    [GeneratedRegex(@"^斩杀时[，,]?额外获得一次卡牌奖励$")]
    private static partial Regex FatalExtraCardReward();
    [GeneratedRegex(@"^斩杀时[，,]?获得(?<amount>\d+)金币$")]
    private static partial Regex FatalGoldReward();
    [GeneratedRegex(@"^在接下来的(?<turns>\d+)回合内[，,]该敌人身上的易伤与虚弱(?:效果|效率)翻倍$")]
    private static partial Regex DebilitateEffect();
    [GeneratedRegex(@"^在下个回合[，,]?\s*获得(?<energy>.*)与(?<stars>.*)$")]
    private static partial Regex NextTurnEnergyAndStars();
    [GeneratedRegex(@"^(?:君王之剑现在能让你获得|每当你打出君王之剑时[，,]获得)(?<amount>\d+)点格挡$")]
    private static partial Regex KingsBladeBlock();
    [GeneratedRegex(@"^(?:君王之剑获得重放(?<amount>\d+)|君王之剑现在会额外命中一次)$")]
    private static partial Regex KingsBladeReplay();
    [GeneratedRegex(@"^君王之剑的耗能加(?<amount>\d+)$")]
    private static partial Regex KingsBladeCostIncrease();
    [GeneratedRegex(@"^在战斗结束时[，,]获得(?<amount>\d+)金币$")]
    private static partial Regex CombatEndGoldReward();
    [GeneratedRegex(@"^每当你(?:花费|使用)或获得(?:\[STAR\]|16px\|link=(?:辉星)?|辉星)?时[，,]?\s*对所有敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex BlackHoleTrigger();
    [GeneratedRegex(@"^每当你花费(?:\[STAR\]|16px\|link=(?:辉星)?|辉星)?时[，,]?\s*每花费一点(?:\[STAR\]|16px\|link=(?:辉星)?|辉星)?[，,]?\s*获得(?<amount>\d+)点格挡$")]
    private static partial Regex ChildOfTheStarsTrigger();
    [GeneratedRegex(@"^随机升级你弃牌堆中的(?<amount>\d+)张牌$")]
    private static partial Regex UpgradeRandomDiscardCards();
    [GeneratedRegex(@"^将你手牌中的全部状态牌变化为(?<target>燃料\+?|FUEL)$")]
    private static partial Regex TransformHandStatusCards();
    [GeneratedRegex(@"^在本回合中[，,]?\s*每个被使用的(?:\[能量\]|16px\|link=(?:能量)?|能量)?[，,]?\s*都会使此牌造成(?<amount>\d+)点伤害一次$")]
    private static partial Regex HelixDrillDamage();
    [GeneratedRegex(@"^在下个回合[，,]?\s*召唤(?<summon>\d+)并获得(?<energy>(?:\[能量\]|16px\|link=能量|16px\|link=|能量)+)$")]
    private static partial Regex NextTurnSummonAndEnergy();
    [GeneratedRegex(@"^每当你给予灾厄时[，,]获得(?<amount>\d+)点格挡$")]
    private static partial Regex ShroudBlockTrigger();
    [GeneratedRegex(@"^每当你给予一个敌人负面状态时[，,]使其受到(?<amount>\d+)点伤害$")]
    private static partial Regex SleightOfFleshTrigger();
    [GeneratedRegex(@"^每当你激发闪电充能球时[，,]对被命中的敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex ThunderOrbTrigger();
    [GeneratedRegex(@"^奥斯提的攻击额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex CalcifyDamage();
    [GeneratedRegex(@"^在战斗结束时[，,]升级你牌组中的(?:一张|(?<amount>\d+)张)随机牌$")]
    private static partial Regex MadScienceDeckUpgrade();
    [GeneratedRegex(@"^每当奥斯提失去生命值时[，,]?\s*所有敌人失去等量生命值$")]
    private static partial Regex NecroMasteryTrigger();
    [GeneratedRegex(@"^打出你弃牌堆中的(?<amount>\d+)张随机攻击牌$")]
    private static partial Regex AutoPlayDiscardRandomAttacks();
    [GeneratedRegex(@"^将手牌中的所有攻击牌变化为(?<target>巨石\+?|GIANT_ROCK)$")]
    private static partial Regex TransformHandAttacksToGiantRock();
    [GeneratedRegex(@"^你抽牌堆中的一张(?:没有重放的)?随机牌获得(?<amount>\d+)层重放$")]
    private static partial Regex DrawCardReplayGain();
    [GeneratedRegex(@"^你在本回合每获得一点(?:\[STAR\]|16px\|link=(?:辉星)?|辉星)?(?:[，,]?\s*则?此牌就(?:对)?所有敌人造成(?<amount>\d+)点伤害一次)$")]
    private static partial Regex RadiateDamage();
    [GeneratedRegex(@"^在你的回合开始时[，,]将(?<amount>\d+)张随机牌添加到你的手牌中$")]
    private static partial Regex TurnStartAddRandomCardToHand();
    [GeneratedRegex(@"^在你的回合开始时[，,]对所有敌人造成(?<amount>\d+)点伤害[，,]然后将该伤害增加(?<inc>\d+)点$")]
    private static partial Regex RollingBoulderTrigger();
    [GeneratedRegex(@"^(?:奥斯提对所有敌人\s*造成|奥斯提造成)(?<damage>\d+)点伤害\s*(?:并且给予所有敌人|并给予它们)\s*(?<vuln>\d+)层易伤$")]
    private static partial Regex HighFiveDamageAndVulnerable();
    [GeneratedRegex(@"^在下个回合[，,]?\s*从你的抽牌堆中选择(?<amount>\d+)张牌放入你的手牌$")]
    private static partial Regex ForegoneConclusionChooseDraw();
    [GeneratedRegex(@"^在你的回合开始时[，,]变化你手牌中的(?<amount>\d+)张牌$")]
    private static partial Regex TurnStartTransformHandCards();
    [GeneratedRegex(@"^在本回合内[，,]?\s*每当奥斯提攻击这名敌人时[，,]召唤(?<amount>\d+)$")]
    private static partial Regex SicEmTrigger();
    [GeneratedRegex(@"^(?:本回合内[，,]?\s*你此前每击中过该敌人一次[，,]这张牌就额外铸造(?<amount>\d+)点|本回合此前你每击中过该敌人一次[，,]铸造值就上升(?<amount>\d+)|本回合此前你击中过该敌人几次[，,]就额外铸造(?<amount>\d+))$")]
    private static partial Regex BeatIntoShapeExtraForge();
    [GeneratedRegex(@"^选择你手牌中的一张牌[，,]将其变化为(?<target>仆从打击\+?|仆从俯冲\+?|MINION_STRIKE|MINION_DIVE_BOMB)$")]
    private static partial Regex TransformHandCardToOneMinionCard();
    [GeneratedRegex(@"^从(?<pool>\d+)张随机牌中选择1张(?:升级过的)?牌?加入你的手牌$")]
    private static partial Regex DiscoverAnyCard();
    [GeneratedRegex(@"^从(?<pool>\d+)张随机(?:升级过的)?无色牌中选择1张(?:升级过的)?牌?加入你的手牌$")]
    private static partial Regex DiscoverColorlessCard();
    [GeneratedRegex(@"^从(?<pool>\d+)张其他角色的(?:升级过的)?攻击牌中选择1张(?:升级过的)?牌?加入你的手牌$")]
    private static partial Regex DiscoverOtherAttackCard();
    [GeneratedRegex(@"^从抽牌堆的随机(?<pool>\d+)张牌中选择一张(?:升级过的)?牌?加入你的手牌$")]
    private static partial Regex SeekerStrikeDrawChoose();
    [GeneratedRegex(@"^在下个回合将这张牌的(?<amount>\d+)张复制品加入你的手牌$")]
    private static partial Regex NightmareCopiesNextTurn();
    [GeneratedRegex(@"^选择你手牌中的一张技能牌[，,]并将其打出(?<repeat>\d+)次$")]
    private static partial Regex DecisionsDecisionsReplaySkill();
    [GeneratedRegex(@"^选择你抽牌堆中的(?<amount>\d+)张\s*牌[，,]将其变化为\s*(?<target>仆从俯冲\+?|仆从打击\+?|MINION_DIVE_BOMB|MINION_STRIKE)$")]
    private static partial Regex TransformDrawCardsToMinionCard();
    [GeneratedRegex(@"^将你手牌中的任意张牌变化为(?<target>仆从捐躯\+?|MINION_SACRIFICE)$")]
    private static partial Regex TransformHandAnyToMinionSacrifice();
    [GeneratedRegex(@"^你可以免费打出每回合的前(?<amount>\d+)张牌$")]
    private static partial Regex FirstCardsFreeEachTurn();
    [GeneratedRegex(@"^每当你的抽牌堆打乱洗牌时[，,]选择(?<amount>一张|(?<num>\d+)张)牌放入你的手牌$")]
    private static partial Regex StratagemShuffleChoose();
    [GeneratedRegex(@"^在你的回合开始时[，,]抽(?<draw>一张|(?<drawNum>\d+)张)牌[，,]并从你的手牌中消耗(?<exhaust>\d+)张牌$")]
    private static partial Regex TyrannyTurnStart();
    [GeneratedRegex(@"^如果奥斯提本回合攻击过[，,]则这张牌的(?:耗能|费用)变为0(?:\[能量\]|16px\|link=能量|16px\|link=|能量)?$")]
    private static partial Regex OstyAttackedThisTurnCostZero();
    [GeneratedRegex(@"^如果这是(?:这张牌|此牌)第一次在本回合被打出[，,]则抽(?<amount>\d+)张牌$")]
    private static partial Regex FirstPlayedThisTurnDraw();
    [GeneratedRegex(@"^你每有一张奥斯提的攻击牌[，,]这张牌就额外造成(?<amount>\d+)点伤害$")]
    private static partial Regex SqueezeAttackCardsBonusDamage();
    [GeneratedRegex(@"^奥斯提回复(?<amount>\d+)点生命$")]
    private static partial Regex HealOstyHp();
    [GeneratedRegex(@"^将你抽牌堆中的一张牌变化为(?<target>灵魂\+?|SOUL)$")]
    private static partial Regex TransformDrawOneToSoul();
    [GeneratedRegex(@"^召唤(?<mult>\d+)X次$")]
    private static partial Regex SummonXTimes();
    [GeneratedRegex(@"^将X张灵魂(?<plus>\+)?添加到你的抽牌堆中$")]
    private static partial Regex AddXSoulToDrawPile();
    [GeneratedRegex(@"^如果奥斯提存活[，,]则他?对所有敌人造成(?<damage>\d+)点伤害并且你获得(?<block>\d+)点格挡$")]
    private static partial Regex BoneShardsLivingOsty();
    [GeneratedRegex(@"^如果奥斯提存活[，,]则他死去[，,]然后你获得等量于其(?<mult>双倍|三倍)最大生命值的格挡$")]
    private static partial Regex SacrificeLivingOsty();
    [GeneratedRegex(@"^给予(?<base>\d+)层灾厄[，,]敌人身上每有(?<threshold>\d+)层灾厄[，,]则额外给予这名敌人(?<bonus>\d+)层灾厄$")]
    private static partial Regex NoEscapeDoomThreshold();
    [GeneratedRegex(@"^奥斯提对随机一名敌人造成(?<damage>\d+)点伤害$")]
    private static partial Regex OstyRandomDamage();
    [GeneratedRegex(@"^每当你打出耗能为(?:\[能量\]|16px\|link=能量|16px\|link=|能量)+(?:或以上)?的牌[，,]将此牌从弃牌堆放回你的手牌$")]
    private static partial Regex RightHandHandReturnTrigger();
    [GeneratedRegex(@"^当你在本回合获得格挡时[，,]对该敌人造成(?<amount>\d+)点伤害$")]
    private static partial Regex GrappleBlockDamage();
    [GeneratedRegex(@"^在你的回合结束时[，,]如果这张牌在你的手牌中[，,](?:你|则)失去(?<amount>\d+)金币$")]
    private static partial Regex DebtLoseGold();
    [GeneratedRegex(@"^在(?<amount>\d+)场战斗后从你的牌组中移除$")]
    private static partial Regex GuiltyRemoveCombats();
    [GeneratedRegex(@"^在下一阶段的地图上[，,]标记一个有(?<amount>\d+)额外金币的地点$")]
    private static partial Regex SpoilsMapGoldBonus();
    [GeneratedRegex(@"^从(?<pool>\d+)张(?:升级过的)?能力牌中选择1张(?:升级过的)?牌?加入你的手牌$")]
    private static partial Regex DiscoverPowerCard();
    [GeneratedRegex(@"^在进入(?<amount>\d+)个\?房间后[，,]这(?:张|副)牌变化为富足$")]
    private static partial Regex DowsingTransformToAbundance();
}
