using System.Collections.Immutable;
using System.Text;
using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Simulation;

internal sealed class MutableCombatState
{
    public required PlayerState Player { get; set; }
    public required List<CreatureState> Enemies { get; init; }
    public required List<CardState> Hand { get; init; }
    public required List<CardState> DrawPile { get; init; }
    public required List<CardState> DiscardPile { get; init; }
    public required List<CardState> ExhaustPile { get; init; }
    public required List<PotionState> Potions { get; init; }
    public required List<OrbState> Orbs { get; init; }
    public required List<RelicState> Relics { get; init; }
    public required List<PowerState> Powers { get; init; }
    public required int OrbCapacity { get; set; }
    public required ulong RngState { get; set; }
    public required RngSnapshotSet RngStreams { get; set; }
    public required List<RestrictionReason> Restrictions { get; init; }
    public required List<RiskEvent> Risks { get; init; }
    public decimal PotionCostSpent { get; set; }
    public decimal DamageDealt { get; set; }
    public int EnemiesKilled { get; set; }
    public decimal HpLostSinceSnapshot { get; set; }
    public int AttacksPlayedSinceSnapshot { get; set; }
    public int AttacksPlayedBeforeTurn { get; set; }
    public int SkillsPlayedSinceSnapshot { get; set; }
    public int CardsPlayedSinceSnapshot { get; set; }
    public int CardPlaysFinishedSinceSnapshot { get; set; }
    public int ShivsPlayedSinceSnapshot { get; set; }
    public int EtherealCardsPlayedSinceSnapshot { get; set; }
    public int CardsDrawnSinceSnapshot { get; set; }
    public int CardsDrawnThisTurn { get; set; }
    public int CardsGeneratedSinceSnapshot { get; set; }
    public int StatusCardsDrawnSinceSnapshot { get; set; }
    public int CardsExhaustedSinceSnapshot { get; set; }
    public int CardsDiscardedSinceSnapshot { get; set; }
    public int DamageReceivedEventsSinceSnapshot { get; set; }
    public int UnmovableBlockGainsThisTurn { get; set; }
    public List<string> PendingTurnStartReturnCardInstanceIds { get; init; } = [];
    public List<CardState> PendingTurnStartCopies { get; init; } = [];
    public required int StatusCardsDrawnBeforeTurn { get; set; }
    public required int CardsExhaustedBeforeTurn { get; set; }
    public required int CardsDiscardedBeforeTurn { get; set; }
    public required int ShivsPlayedBeforeTurn { get; set; }
    public required CombatHistoryCounters HistoryBeforeSnapshot { get; init; }

    public PredictionConfidence Confidence => Restrictions.Any(static reason => reason.Code.StartsWith("uncalculable", StringComparison.Ordinal)) ||
                                              Risks.Any(static risk => risk.Severity == PredictionRiskSeverity.Uncalculable)
            ? PredictionConfidence.Uncalculable
            : Restrictions.Count > 0 || Risks.Any(static risk => risk.Severity == PredictionRiskSeverity.Estimated)
                ? PredictionConfidence.Estimated
                : PredictionConfidence.Reliable;

    public static MutableCombatState FromSnapshot(CombatSnapshot snapshot)
    {
        var history = snapshot.HistoryCounters ?? new CombatHistoryCounters();
        var shivsHitAllEnemies = snapshot.Player.Statuses.TryGetValue("SHIV_ALL_ENEMIES", out var fanOfKnives) &&
                                 fanOfKnives.Amount > 0;
        var shivsRetained = snapshot.Player.Statuses.TryGetValue("SHIV_RETAIN", out var phantomBlades) &&
                            phantomBlades.Amount > 0;
        CardState NormalizeCard(CardState card)
        {
            if (!card.ModelId.Equals("SHIV", StringComparison.OrdinalIgnoreCase) &&
                !card.ModelId.Equals("SHIV_UPGRADE", StringComparison.OrdinalIgnoreCase) &&
                !card.ModelId.Equals("INKY_SHIV", StringComparison.OrdinalIgnoreCase) &&
                !card.ModelId.Equals("INKY_SHIV_UPGRADE", StringComparison.OrdinalIgnoreCase))
                return card;
            if (shivsHitAllEnemies) card = card with { Target = TargetKind.AllEnemies };
            if (shivsRetained) card = card with { RetainAtTurnEnd = true };
            return card;
        }
        return new MutableCombatState
        {
            Player = snapshot.Player,
            Enemies = snapshot.Enemies.ToList(),
            Hand = snapshot.Hand.Select(NormalizeCard).ToList(),
            DrawPile = snapshot.DrawPile.Select(NormalizeCard).ToList(),
            DiscardPile = snapshot.DiscardPile.Select(NormalizeCard).ToList(),
            ExhaustPile = snapshot.ExhaustPile.Select(NormalizeCard).ToList(),
            Potions = snapshot.Potions.ToList(),
            Orbs = snapshot.Orbs.IsDefault ? [] : snapshot.Orbs.ToList(),
            Relics = snapshot.SafeRelics.ToList(),
            Powers = snapshot.SafePowers.ToList(),
            OrbCapacity = snapshot.OrbCapacity,
            RngState = snapshot.RngState,
            RngStreams = (snapshot.RngStreams ?? RngSnapshotSet.Empty).Copy(),
            Restrictions = snapshot.GlobalRestrictions
                .Select(static reason => new RestrictionReason("snapshot_restriction", reason))
                .ToList(),
            Risks = snapshot.GlobalRestrictions
                .Select(static reason => new RiskEvent(
                    PredictionRiskReason.StateCaptureIncomplete,
                    PredictionRiskSeverity.Estimated,
                    reason))
                .ToList(),
            UnmovableBlockGainsThisTurn = history.BlockGainedThisTurn,
            HistoryBeforeSnapshot = history,
            StatusCardsDrawnBeforeTurn = history.StatusCardsDrawnThisTurn,
            CardsDrawnThisTurn = history.CardsDrawnThisTurn,
            CardsExhaustedBeforeTurn = history.CardsExhaustedThisTurn,
            CardsDiscardedBeforeTurn = history.CardsDiscardedThisTurn,
            ShivsPlayedBeforeTurn = history.ShivsPlayedThisTurn,
            AttacksPlayedBeforeTurn = history.AttacksPlayedThisTurn
        };
    }

    public MutableCombatState Clone() => new()
    {
        Player = Player,
        Enemies = [.. Enemies],
        Hand = [.. Hand],
        DrawPile = [.. DrawPile],
        DiscardPile = [.. DiscardPile],
        ExhaustPile = [.. ExhaustPile],
        Potions = [.. Potions],
        Orbs = [.. Orbs],
        Relics = [.. Relics],
        Powers = [.. Powers],
        OrbCapacity = OrbCapacity,
        RngState = RngState,
        RngStreams = RngStreams.Copy(),
        Restrictions = [.. Restrictions],
        Risks = [.. Risks],
        PotionCostSpent = PotionCostSpent,
        DamageDealt = DamageDealt,
        EnemiesKilled = EnemiesKilled,
        HpLostSinceSnapshot = HpLostSinceSnapshot,
        AttacksPlayedSinceSnapshot = AttacksPlayedSinceSnapshot,
        AttacksPlayedBeforeTurn = AttacksPlayedBeforeTurn,
        SkillsPlayedSinceSnapshot = SkillsPlayedSinceSnapshot,
        CardsPlayedSinceSnapshot = CardsPlayedSinceSnapshot,
            CardPlaysFinishedSinceSnapshot = CardPlaysFinishedSinceSnapshot,
            ShivsPlayedSinceSnapshot = ShivsPlayedSinceSnapshot,
        EtherealCardsPlayedSinceSnapshot = EtherealCardsPlayedSinceSnapshot,
        CardsDrawnSinceSnapshot = CardsDrawnSinceSnapshot,
        CardsDrawnThisTurn = CardsDrawnThisTurn,
        CardsGeneratedSinceSnapshot = CardsGeneratedSinceSnapshot,
        StatusCardsDrawnSinceSnapshot = StatusCardsDrawnSinceSnapshot,
        CardsExhaustedSinceSnapshot = CardsExhaustedSinceSnapshot,
        CardsDiscardedSinceSnapshot = CardsDiscardedSinceSnapshot,
        DamageReceivedEventsSinceSnapshot = DamageReceivedEventsSinceSnapshot,
        UnmovableBlockGainsThisTurn = UnmovableBlockGainsThisTurn,
        StatusCardsDrawnBeforeTurn = StatusCardsDrawnBeforeTurn,
        CardsExhaustedBeforeTurn = CardsExhaustedBeforeTurn,
            CardsDiscardedBeforeTurn = CardsDiscardedBeforeTurn,
        ShivsPlayedBeforeTurn = ShivsPlayedBeforeTurn,
            PendingTurnStartReturnCardInstanceIds = [.. PendingTurnStartReturnCardInstanceIds],
            PendingTurnStartCopies = [.. PendingTurnStartCopies],
        HistoryBeforeSnapshot = HistoryBeforeSnapshot
    };

    public string ExactKey()
    {
        var builder = new StringBuilder();
        if (PendingTurnStartReturnCardInstanceIds.Count > 0)
            builder.Append("|RET:").Append(string.Join(',', PendingTurnStartReturnCardInstanceIds));
        if (PendingTurnStartCopies.Count > 0)
            AppendCards(builder, "N", PendingTurnStartCopies);
        builder.Append(Player.Hp).Append('|').Append(Player.Block).Append('|').Append(Player.Energy).Append("|M:").Append(Player.MaxHp)
            .Append("|CP:").Append(AttacksPlayedSinceSnapshot).Append(':').Append(SkillsPlayedSinceSnapshot).Append(':').Append(RelevantRootCardPlays())
            .Append(":HPLOSS:").Append(HpLostSinceSnapshot)
            .Append("|HC:").Append(HistoryBeforeSnapshot.AttacksPlayedThisTurn).Append(':').Append(HistoryBeforeSnapshot.SkillsPlayedThisTurn)
            .Append(':').Append(HistoryBeforeSnapshot.CardsPlayedThisCombat).Append(':').Append(HistoryBeforeSnapshot.EtherealCardsPlayedThisCombat)
            .Append(':').Append(HistoryBeforeSnapshot.CardsDrawnThisCombat).Append(':').Append(HistoryBeforeSnapshot.CardsGeneratedThisCombat)
            .Append(':').Append(HistoryBeforeSnapshot.CardsPlayedThisTurn)
            .Append(':').Append(HistoryBeforeSnapshot.ShivsPlayedThisTurn)
            .Append(':').Append(HistoryBeforeSnapshot.DamageReceivedEventsThisCombat)
            .Append(':').Append(DamageReceivedEventsSinceSnapshot)
            .Append(':').Append(CardPlaysFinishedSinceSnapshot).Append(':').Append(EtherealCardsPlayedSinceSnapshot)
            .Append(':').Append(ShivsPlayedBeforeTurn).Append(':').Append(ShivsPlayedSinceSnapshot)
            .Append(':').Append(AttacksPlayedBeforeTurn)
            .Append(':').Append(CardsDrawnSinceSnapshot).Append(':').Append(CardsDrawnThisTurn)
            .Append(':').Append(CardsGeneratedSinceSnapshot);
        builder.Append(':').Append(StatusCardsDrawnBeforeTurn).Append(':').Append(StatusCardsDrawnSinceSnapshot);
        builder.Append(':').Append(CardsExhaustedBeforeTurn).Append(':').Append(CardsExhaustedSinceSnapshot)
            .Append(':').Append(CardsDiscardedBeforeTurn).Append(':').Append(CardsDiscardedSinceSnapshot);
        builder.Append(":UNMOVABLE:").Append(UnmovableBlockGainsThisTurn);
        AppendStatuses(builder, Player.Statuses);
        foreach (var enemy in Enemies)
        {
            builder.Append("|E:").Append(enemy.Id).Append(':').Append(enemy.Hp).Append(':').Append(enemy.Block);
            AppendStatuses(builder, enemy.Statuses);
            AppendIntents(builder, enemy.Intents);
        }
        AppendCards(builder, "H", Hand);
        AppendCards(builder, "D", DrawPile);
        AppendCards(builder, "C", DiscardPile);
        AppendCards(builder, "X", ExhaustPile);
        AppendOrbs(builder, Orbs, OrbCapacity);
        AppendRelics(builder, Relics);
        AppendPowers(builder, Powers);
        foreach (var potion in Potions) builder.Append("|P:").Append(potion.InstanceId);
        builder.Append("|R:").Append(RngState);
        AppendRngStreams(builder, RngStreams);
        return builder.ToString();
    }

    public string CycleKeyWithoutProgress()
    {
        var builder = new StringBuilder();
        builder.Append(Player.Hp).Append('|').Append(Player.Block).Append('|').Append(Player.Energy).Append("|M:").Append(Player.MaxHp)
            .Append("|CP:").Append(AttacksPlayedSinceSnapshot).Append(':').Append(SkillsPlayedSinceSnapshot).Append(':').Append(RelevantRootCardPlays());
        builder.Append(":HPLOSS:").Append(HpLostSinceSnapshot);
        builder.Append(":DAMAGE_RECEIVED:").Append(HistoryBeforeSnapshot.DamageReceivedEventsThisCombat).Append(':').Append(DamageReceivedEventsSinceSnapshot);
        builder.Append(":EXHAUST:").Append(CardsExhaustedBeforeTurn).Append(':').Append(CardsExhaustedSinceSnapshot)
            .Append(":DISCARD:").Append(CardsDiscardedBeforeTurn).Append(':').Append(CardsDiscardedSinceSnapshot);
        builder.Append(":SHIVS:").Append(ShivsPlayedBeforeTurn).Append(':').Append(ShivsPlayedSinceSnapshot);
        builder.Append(":ATTACKS:").Append(AttacksPlayedBeforeTurn);
        builder.Append(":UNMOVABLE:").Append(UnmovableBlockGainsThisTurn);
        AppendStatuses(builder, Player.Statuses);
        foreach (var enemy in Enemies)
        {
            builder.Append("|E:").Append(enemy.Id).Append(':').Append(enemy.Block);
            AppendStatuses(builder, enemy.Statuses);
            AppendIntents(builder, enemy.Intents);
        }
        AppendCards(builder, "H", Hand);
        AppendCards(builder, "D", DrawPile);
        AppendCards(builder, "C", DiscardPile);
        AppendCards(builder, "X", ExhaustPile);
        AppendOrbs(builder, Orbs, OrbCapacity);
        AppendRelics(builder, Relics);
        AppendPowers(builder, Powers);
        foreach (var potion in Potions) builder.Append("|P:").Append(potion.InstanceId);
        builder.Append("|R:").Append(RngState);
        AppendRngStreams(builder, RngStreams);
        return builder.ToString();
    }

    private static void AppendRelics(StringBuilder builder, IEnumerable<RelicState> relics)
    {
        foreach (var relic in relics.OrderBy(static r => r.Id, StringComparer.Ordinal))
        {
            builder.Append("|RL:").Append(relic.Id).Append(':')
                .Append(relic.Counter?.ToString() ?? "null").Append(':')
                .Append(relic.IsEnabled ? '1' : '0').Append(':')
                .Append(relic.IsUsedUp ? '1' : '0').Append(':')
                .Append(relic.UsesThisTurn).Append(':')
                .Append(relic.UsesThisCombat).Append(':')
                .Append(relic.UnknownStatePresent ? '1' : '0');
            if (relic.DynamicVars is { Count: > 0 } vars)
            {
                foreach (var pair in vars.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    builder.Append(':').Append(pair.Key).Append('=').Append(pair.Value);
                }
            }
        }
    }

    private static void AppendPowers(StringBuilder builder, IEnumerable<PowerState> powers)
    {
        foreach (var power in powers.OrderBy(static p => p.OwnerId, StringComparer.Ordinal)
                     .ThenBy(static p => p.Id, StringComparer.Ordinal)
                     .ThenBy(static p => p.ApplierId, StringComparer.Ordinal))
        {
            builder.Append("|PW:").Append(power.OwnerId).Append(':').Append(power.Id).Append(':')
                .Append(power.ApplierId ?? "null").Append(':').Append(power.Amount).Append(':')
                .Append(power.Support).Append(':').Append(power.Evidence);
            foreach (var pair in power.SafeDynamicVars.OrderBy(static p => p.Key, StringComparer.Ordinal))
                builder.Append(':').Append(pair.Key).Append('=').Append(pair.Value);
            foreach (var pair in power.SafeCounters.OrderBy(static p => p.Key, StringComparer.Ordinal))
                builder.Append(':').Append(pair.Key).Append('=').Append(pair.Value);
            foreach (var phase in power.SafeTriggerPhases.OrderBy(static p => p, StringComparer.Ordinal))
                builder.Append(":PH=").Append(phase);
        }
    }

    private static void AppendRngStreams(StringBuilder builder, RngSnapshotSet streams)
    {
        foreach (var pair in streams.Streams.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;
            builder.Append("|RS:").Append(pair.Key).Append(':')
                .Append(value.State0).Append(':').Append(value.State1).Append(':')
                .Append(value.State2).Append(':').Append(value.State3).Append(':')
                .Append(value.Counter).Append(':').Append(value.IsKnown);
        }
    }

    private int RelevantRootCardPlays()
    {
        var maximumRelevant = Player.Statuses.TryGetValue("ECHO_FORM_REPLAY_FIRST_CARDS", out var active)
            ? Math.Max(0, active.Amount)
            : 0;
        maximumRelevant += Hand.Concat(DrawPile).Concat(DiscardPile).Concat(ExhaustPile)
            .SelectMany(static card => card.Effects)
            .Where(static effect => effect.Kind == EffectKind.ApplyStatus && effect.StatusId == "ECHO_FORM_REPLAY_FIRST_CARDS")
            .Sum(static effect => Math.Max(0, (int)effect.Amount));
        return maximumRelevant <= 0 ? 0 : Math.Min(CardsPlayedSinceSnapshot, maximumRelevant);
    }

    private static void AppendCards(StringBuilder builder, string prefix, IEnumerable<CardState> cards)
    {
        builder.Append('|').Append(prefix).Append(':');
        foreach (var card in cards)
        builder.Append(card.InstanceId).Append("@C").Append(card.EnergyCost)
            .Append("@T").Append(card.Target)
            .Append("@Y").Append(card.Rarity)
            .Append("@DMG+").Append(card.CombatDamageBonus)
            .Append("@BLK+").Append(card.CombatBlockBonus)
            .Append(card.CostsX ? "X" : string.Empty)
            .Append("@R").Append(card.ReplayCount)
            .Append(card.IsDupe ? "D" : string.Empty)
            .Append(card.RetainAtTurnEnd ? "P" : string.Empty)
            .Append(card.TemporaryRetainAtTurnEnd ? "T" : string.Empty)
            .Append(card.TemporaryEnergyCostBeforeCap is { } previousCost ? $"K{previousCost}" : string.Empty)
            .Append(card.TemporaryCostsXBeforeOverride is { } previousCostsX ? $"Q{previousCostsX}" : string.Empty)
            .Append(card.ExhaustAtTurnEnd ? "E" : string.Empty).Append(',');
    }

    private static void AppendOrbs(StringBuilder builder, IEnumerable<OrbState> orbs, int capacity)
    {
        builder.Append("|O:").Append(capacity).Append(':');
        foreach (var orb in orbs)
            builder.Append(orb.Id).Append('@').Append(orb.PassiveValue).Append('/').Append(orb.EvokeValue)
                .Append('~').Append(orb.FocusAdjustment).Append(',');
    }

    private static void AppendStatuses(StringBuilder builder, ImmutableDictionary<string, StatusState> statuses)
    {
        foreach (var status in statuses.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            builder.Append(':').Append(status.Key).Append('=').Append(status.Value.Amount).Append('@').Append(status.Value.Duration)
                .Append('#').Append(status.Value.GeneratedCard?.ModelId)
                .Append('%').Append(status.Value.RandomSource);
    }

    private static void AppendIntents(StringBuilder builder, ImmutableArray<IntentState> intents)
    {
        builder.Append("|I:");
        foreach (var intent in intents)
        {
            builder.Append(intent.Type).Append('@').Append(intent.DamagePerHit).Append('#').Append(intent.Hits)
                .Append('%').Append(intent.RestrictionReason).Append('[');
            foreach (var effect in intent.SafeEffects)
            {
                builder.Append(effect.Kind).Append('@').Append(effect.Amount).Append(':').Append(effect.StatusId)
                    .Append(':').Append(effect.Duration).Append(':').Append(effect.IsDebuff)
                    .Append(':').Append(effect.FutureValuePerTurn).Append(':').Append(effect.SourceId)
                    .Append(':').Append(effect.TargetOverride).Append(':').Append(effect.Condition)
                    .Append(':').Append(effect.GeneratedDestination).Append(':').Append(effect.Repeat)
                    .Append(':').Append(effect.RepeatByEnergySpent).Append(':').Append(effect.RepeatByOrbCount)
                    .Append(':').Append(effect.XBonus).Append(':').Append(effect.AmountByEnergySpent)
                    .Append(':').Append(effect.AmountByAliveEnemyCount).Append(':').Append(effect.AmountByDistinctOrbTypes)
                    .Append(':').Append(effect.AmountByHandAttackCount).Append(':').Append(effect.AmountByCardsDrawnThisTurn)
                    .Append(':').Append(effect.RepeatByHistoryCounter).Append(':').Append(effect.RepeatByKillCount)
                    .Append(':').Append(effect.AmountByTargetVulnerableStacks).Append(':').Append(effect.RepeatByExhaustedCount)
                    .Append(':').Append(effect.RandomSource).Append(';');
                if (effect.GeneratedCard is { } generated)
                    builder.Append("G:").Append(generated.ModelId).Append('@').Append(generated.EnergyCost).Append(';');
                if (!effect.GeneratedCardPool.IsDefaultOrEmpty)
                    builder.Append("P:").Append(string.Join(',', effect.GeneratedCardPool.Select(static card => card.ModelId))).Append(';');
            }
            builder.Append(']');
        }
    }
}
