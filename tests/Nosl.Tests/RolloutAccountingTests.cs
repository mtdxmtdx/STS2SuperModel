using System.Text.Json;
using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public sealed class RolloutAccountingTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(5, 0)]
    public async Task FatalNativeDamageCountsActualHpLossWithoutSubtractingOverkill(int hp, int expectedOverkill)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "fatal-audit", Hp: hp));
        var packet = await session.StepAsync(session.Observe().Actions.Single(a => a.Kind == "end_turn"));
        Assert.Equal("terminal_settled", packet.Status);
        var facts = await session.SettleAsync();
        Assert.Equal("loss", facts.Result);
        Assert.Equal(0, facts.FinalHp);
        var damage = facts.Events.Where(e => e.Kind == "damage").Select(e => JsonDocument.Parse(e.Detail)).ToArray();
        try
        {
            var hit = Assert.Single(damage, d => d.RootElement.GetProperty("target").GetString() == "player").RootElement;
            Assert.Equal(hp, hit.GetProperty("unblocked").GetInt32());
            Assert.Equal(expectedOverkill, hit.GetProperty("overkill").GetInt32());
            var outcome = RolloutRecorder.Settled(session, facts, PublicContinuationPolicies.LegacyId, 1);
            Assert.Equal((double)hp, outcome.CumulativeHpDamage);
            Assert.True(outcome.HpEventDiagnosticsComplete);
            Assert.Equal(0, outcome.HealingReceived);
            Assert.Equal(0, outcome.OtherHpAdjustment);
            var cost = ObjectiveEvaluator.Evaluate(outcome);
            Assert.Equal(EvaluationStatus.Scored, cost.Status);
            Assert.Equal(1000 + hp * 1.2, cost.Cost!.Value, 10); // Fixed terminal HP, no extra damage charge.
        }
        finally { foreach (var item in damage) item.Dispose(); }
    }
}
