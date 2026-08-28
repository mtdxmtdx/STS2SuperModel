using System.Collections.Immutable;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Simulation;

namespace STS2BestChoice.Core.Scoring;

internal sealed class CombatScorer(CombatSnapshot snapshot, SearchOptions options)
{
    public int EstimatedFutureTurns { get; } = EstimateFutureTurns(snapshot, options);

    public ScoreBreakdown Score(MutableCombatState terminal)
    {
        var hpLoss = Math.Max(0m, snapshot.Player.Hp - terminal.Player.Hp);
        var effectiveDamage = snapshot.Enemies.Sum(static enemy => enemy.Hp) - terminal.Enemies.Sum(static enemy => enemy.Hp);
        var initialById = snapshot.Enemies.ToDictionary(static enemy => enemy.Id, StringComparer.Ordinal);
        decimal threatRemoval = 0m;
        decimal longTerm = StatusValue(terminal.Player.Statuses, playerOwned: true);
        longTerm -= StatusValue(snapshot.Player.Statuses, playerOwned: true);
        longTerm += OrbValue(terminal.Orbs) - OrbValue(snapshot.Orbs.IsDefault ? [] : snapshot.Orbs);

        foreach (var enemy in terminal.Enemies)
        {
            if (!initialById.TryGetValue(enemy.Id, out var initial)) continue;
            if (initial.IsAlive && !enemy.IsAlive)
                threatRemoval += initial.ThreatPerFutureTurn * EstimatedFutureTurns;
            longTerm += StatusValue(enemy.Statuses, playerOwned: false) - StatusValue(initial.Statuses, playerOwned: false);
        }
        longTerm *= EstimatedFutureTurns;

        var handValue = terminal.Hand.Count(card => DeterministicSimulator.IsCardPlayableNow(terminal, card)) * 0.25m
                        + Math.Max(0, terminal.Player.Energy - terminal.Player.MaxEnergy) * 0.5m;
        var weights = options.Weights;
        var balanced =
            effectiveDamage * weights.Damage
            - hpLoss * 4m * weights.Survival
            + threatRemoval * weights.ThreatRemoval
            + longTerm * weights.LongTerm
            + handValue * weights.HandAndResources
            - terminal.PotionCostSpent * weights.PotionConservation;

        return new ScoreBreakdown(
            effectiveDamage,
            hpLoss,
            threatRemoval,
            longTerm,
            handValue,
            terminal.PotionCostSpent,
            balanced,
            EstimatedFutureTurns);
    }

    public ProjectedTerminal Terminal(MutableCombatState terminal) => new(
        terminal.Player.Hp,
        terminal.Player.Block,
        terminal.Enemies.ToImmutableArray(),
        terminal.Hand.ToImmutableArray(),
        terminal.Orbs.ToImmutableArray(),
        terminal.Player.Hp <= 0m,
        terminal.Enemies.All(static enemy => !enemy.IsAlive),
        terminal.ExactKey());

    private static decimal StatusValue(ImmutableDictionary<string, StatusState> statuses, bool playerOwned)
    {
        decimal value = 0m;
        foreach (var status in statuses.Values)
        {
            var signed = status.FutureValuePerTurn;
            if (signed == 0m)
                signed = StatusValuation.IntrinsicFutureValue(status.Id, status.Amount);
            if (!playerOwned) signed = -signed;
            value += signed;
        }
        return value;
    }

    private static decimal OrbValue(IEnumerable<OrbState> orbs) => orbs.Sum(static orb => orb.Id.ToUpperInvariant() switch
    {
        "LIGHTNING" => orb.EffectivePassiveValue * 0.75m,
        "FROST" => orb.EffectivePassiveValue,
        "DARK" => orb.EffectiveEvokeValue * 0.5m,
        "PLASMA" => orb.EffectivePassiveValue * 1.5m,
        _ => 0m
    });

    private static int EstimateFutureTurns(CombatSnapshot snapshot, SearchOptions options)
    {
        var effectiveHp = snapshot.Enemies.Sum(static enemy => Math.Max(0m, enemy.Hp + enemy.Block));
        var handDamage = snapshot.Hand
            .SelectMany(static card => card.Effects)
            .Where(static effect => effect.Kind == EffectKind.Damage)
            .Sum(static effect => effect.Amount);
        var estimatedPerTurn = Math.Max(1m, handDamage);
        var turns = (int)decimal.Ceiling(effectiveHp / estimatedPerTurn);
        if (snapshot.IsBoss) turns = Math.Max(3, turns);
        return Math.Clamp(turns, options.MinimumFutureTurns, options.MaximumFutureTurns);
    }
}
