namespace Nosl.Objectives;

/// <summary>Fixed combat-start terminal utility. No action/turn rewards, discounting, or interim reanchoring.</summary>
public static class ObjectiveEvaluator
{
    public static OutcomeEvaluation Evaluate(RolloutOutcome outcome, ObjectiveProfile? profile = null, bool formalLabels = false)
    {
        profile ??= ObjectiveProfile.Candidate;
        profile.Validate();
        if (formalLabels) profile.AssertFormalLabelsAllowed();
        if (outcome is null) return Invalid("missing_outcome");
        if (!Enum.IsDefined(outcome.TerminalKind)) return Invalid("unknown_terminal_kind");
        if (!outcome.IsTrueTerminal)
            return new(EvaluationStatus.Incomplete, null, null, null, null, [outcome.TerminalKind.ToString()]);
        if (!outcome.SettlementComplete || string.IsNullOrWhiteSpace(outcome.SettlementProfileId)
            || string.IsNullOrWhiteSpace(outcome.ContinuationPolicyId))
            return Invalid("actual_terminal_settlement_required");
        if (outcome.HpAtCombatStart <= 0 || outcome.MaxHpStart < outcome.HpAtCombatStart
            || outcome.HpAfterSettlement is not int finalHp || outcome.MaxHpAfterSettlement is not int finalMax
            || finalMax <= 0 || finalHp < 0 || finalHp > finalMax || outcome.PlayerAlive is null
            || outcome.PlayerAlive != (finalHp > 0) || outcome.TerminalKind == TerminalKind.Win && !outcome.PlayerAlive.Value)
            return Invalid("invalid_terminal_hp_or_alive_facts");
        if (outcome.InventoryStart is null || outcome.InventoryEnd is null || outcome.ResourceEvents is null || outcome.PermanentChanges is null)
            return Invalid("missing_resource_ledger");
        if (outcome.PlayerTurnsElapsed < 0 || outcome.AtomicActionsExecuted < 0
            || outcome.HpEventDiagnosticsComplete && (outcome.CumulativeHpDamage is null || outcome.HealingReceived is null)
            || InvalidNonnegative(outcome.CumulativeHpDamage) || InvalidNonnegative(outcome.HealingReceived)
            || outcome.InventoryStart.Concat(outcome.InventoryEnd).Any(x => x is null || string.IsNullOrWhiteSpace(x.ResourceId) || x.Count < 0)
            || outcome.ResourceEvents.Any(x => x is null || x.Quantity < 0 || string.IsNullOrWhiteSpace(x.ResourceId))
            || outcome.PermanentChanges.Any(x => x is null || string.IsNullOrWhiteSpace(x.Kind) || !double.IsFinite(x.Amount)))
            return Invalid("invalid_outcome_diagnostics_or_resources");

        var unresolved = new List<string>();
        if (!outcome.InventorySnapshotsComplete) unresolved.Add("inventory_snapshots_incomplete");
        double inventory = 0, permanent = 0;
        var start = Quantities(outcome.InventoryStart);
        var end = Quantities(outcome.InventoryEnd);
        // A true loss has no future retained inventory value. Consumption events are not another penalty.
        foreach (string id in start.Keys.Union(end.Keys).Order(StringComparer.Ordinal))
        {
            double difference = start.GetValueOrDefault(id) - (outcome.TerminalKind == TerminalKind.Win ? end.GetValueOrDefault(id) : 0);
            if (difference == 0) continue; // Unknown prices cancel only when retention is actually equal.
            if (!profile.InventoryValues.TryGetValue(id, out var value)) unresolved.Add($"inventory_value_unresolved:{id}");
            else inventory += difference * value.HpEquivalent;
        }
        if (!outcome.PermanentChangesComplete) unresolved.Add("permanent_change_ledger_incomplete");
        double recordedMaxHp = outcome.PermanentChanges.Where(x => x.Kind == "max_hp").Sum(x => x.Amount);
        if (recordedMaxHp != finalMax - outcome.MaxHpStart) unresolved.Add("permanent_change_not_recorded:max_hp");
        if (outcome.TerminalKind == TerminalKind.Win)
            foreach (var change in outcome.PermanentChanges.GroupBy(x => x.Kind))
            {
                double amount = change.Sum(x => x.Amount);
                if (amount == 0) continue;
                if (!profile.PermanentFutureValues.TryGetValue(change.Key, out var value)) unresolved.Add($"permanent_future_value_unresolved:{change.Key}");
                else permanent += amount * value.HpEquivalent;
            }
        if (!double.IsFinite(inventory) || Math.Abs(inventory) > profile.MaximumAbsoluteInventoryAdjustment)
            unresolved.Add("inventory_adjustment_exceeds_profile_bound");
        if (!double.IsFinite(permanent) || Math.Abs(permanent) > profile.MaximumAbsolutePermanentAdjustment)
            unresolved.Add("permanent_adjustment_exceeds_profile_bound");
        double loss = outcome.HpAtCombatStart - finalHp;
        if (unresolved.Count > 0)
            return new(EvaluationStatus.ObjectiveValueUnresolved, null, loss, null, null, unresolved.ToArray());
        double downside = profile.DownsideCoefficient * Math.Pow(Math.Max(loss, 0), 2) / Math.Max(1, outcome.HpAtCombatStart);
        double cost = (outcome.TerminalKind == TerminalKind.Loss ? profile.DefeatCost : 0) + loss + downside + inventory - permanent;
        if (!double.IsFinite(cost)) return Invalid("nonfinite_terminal_cost");
        return new(EvaluationStatus.Scored, cost, loss, inventory, permanent, []);
    }

    public static BatchEvaluation EvaluateBatch(IEnumerable<RolloutOutcome> outcomes, int assignedWorlds,
        ObjectiveProfile? profile = null, bool formalLabels = false)
    {
        profile ??= ObjectiveProfile.Candidate;
        profile.Validate();
        if (formalLabels) profile.AssertFormalLabelsAllowed();
        var rows = outcomes.ToArray();
        if (assignedWorlds <= 0 || rows.Length != assignedWorlds)
            throw new ArgumentException("Assigned world ledger must equal all recorded outcomes; never drop or renormalize incomplete worlds");
        if (rows.Any(x => x is null)) throw new ArgumentException("Missing outcome record in allocated world ledger");
        if (rows.Select(x => (x.HpAtCombatStart, x.MaxHpStart)).Distinct().Count() != 1)
            throw new ArgumentException("A batch must share one fixed combat-start anchor");
        var scored = rows.Select(x => Evaluate(x, profile)).ToArray();
        int wins = rows.Where((r, i) => r.TerminalKind == TerminalKind.Win && scored[i].Status != EvaluationStatus.InvalidOutcome).Count();
        int losses = rows.Where((r, i) => r.TerminalKind == TerminalKind.Loss && scored[i].Status != EvaluationStatus.InvalidOutcome).Count();
        int completed = wins + losses;
        int dead = rows.Where((r, i) => r.IsTrueTerminal && r.PlayerAlive == false && scored[i].Status != EvaluationStatus.InvalidOutcome).Count();
        bool allScored = scored.All(x => x.Status == EvaluationStatus.Scored);
        bool profileReady;
        try { profile.AssertFormalLabelsAllowed(); profileReady = true; } catch (InvalidOperationException) { profileReady = false; }
        var reasons = scored.SelectMany(x => x.Reasons).ToList();
        if (!profileReady) reasons.Add("objective_profile_not_calibrated_for_formal_labels");
        if (completed != assignedWorlds) reasons.Add("incomplete_probability_mass_preserved");
        EstimateInterval Bounds(int known) => new((double)known / assignedWorlds, (double)(known + assignedWorlds - completed) / assignedWorlds);
        return new(assignedWorlds, wins, losses,
            rows.Count(x => x.TerminalKind == TerminalKind.ComputeTruncated),
            rows.Count(x => x.TerminalKind == TerminalKind.EngineError),
            rows.Count(x => x.TerminalKind == TerminalKind.PolicyNonterminating),
            scored.Count(x => x.Status == EvaluationStatus.ObjectiveValueUnresolved),
            scored.Count(x => x.Status == EvaluationStatus.InvalidOutcome),
            (double)completed / assignedWorlds,
            allScored ? scored.Average(x => x.Cost!.Value) : null,
            completed == assignedWorlds ? scored.Average(x => x.NetHpLoss!.Value) : null,
            Bounds(wins), Bounds(losses), Bounds(dead), allScored && profileReady,
            reasons.Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, double> Quantities(IEnumerable<InventoryQuantity> rows) =>
        rows.GroupBy(x => x.ResourceId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(q => (double)q.Count), StringComparer.Ordinal);
    private static bool InvalidNonnegative(double? value) => value is double x && (!double.IsFinite(x) || x < 0);
    private static OutcomeEvaluation Invalid(string reason) => new(EvaluationStatus.InvalidOutcome, null, null, null, null, [reason]);
}
