namespace Nosl.Objectives;

/// <summary>Audit-only pairing facts. These are not policy/student inputs or evidence inferred from equal outcomes.</summary>
public sealed record RelativeOutcomeAudit(string WorldId, string PublicRootKey, string CombatStartAnchor,
    string ControllerContext, string SamplerProfile, bool SharedWorldForkVerified, string RootActionIdentity);

/// <summary>A separately reviewed mechanics proof, scoped to a public root and the settlement endpoint.</summary>
public sealed record EmptyInventoryClosure(string PublicRootKey, string SettlementProfileId, string Evidence);

public sealed record CancelledInventoryTerm(string ResourceId, double LossAwareCoefficient, bool ValueUnresolved);
public sealed record RelativeOutcomeEvaluation(string LabelProfile, bool RelativeValueMask, double? FirstMinusSecondCost,
    bool FirstAbsoluteValueMask, bool SecondAbsoluteValueMask, bool FormalLabelsAllowed,
    CancelledInventoryTerm[] CancelledInventory, string[] Reasons, string[] Provenance);
public sealed record AuditedOutcome(RolloutOutcome Outcome, RelativeOutcomeAudit Audit);

public sealed record RelativeDifferenceSupport(double Lower, double Upper, string Evidence);
public sealed record RelativeEvaluationPlan(string[] WorldIds, string Evidence, bool IndependentFinalEvaluation,
    int PlannedComparisons = 1, double FamilywiseAlpha = .05);
public sealed record RelativeBatchEvaluation(string LabelProfile, int AssignedWorlds, int ResolvedWorlds,
    bool RelativeValueMask, double? EmpiricalMeanFirstMinusSecondCost, EstimateInterval? CostDifferenceInterval,
    int? PreferredArm, bool FormalLabelsAllowed, string[] Reasons, string[] Provenance);

/// <summary>
/// Opt-in development evidence for the existing additive terminal objective. It never fills an absolute value,
/// cancels a permanent-change summary, or changes the ordinary teacher's ranking/label profile.
/// </summary>
public static class CommonResourceObjective
{
    public const string LabelProfile = "nosl-common-inventory-relative-development-v1";

    public static RelativeOutcomeEvaluation Evaluate(AuditedOutcome first, AuditedOutcome second,
        ObjectiveProfile? profile = null, EmptyInventoryClosure? closure = null)
    {
        profile ??= ObjectiveProfile.Candidate;
        profile.Validate();
        var a = first.Outcome; var b = second.Outcome;
        var ea = ObjectiveEvaluator.Evaluate(a, profile); var eb = ObjectiveEvaluator.Evaluate(b, profile);
        var reasons = new List<string>(); var provenance = new List<string>();
        RelativeOutcomeEvaluation Masked() => new(LabelProfile, false, null, ea.Cost.HasValue, eb.Cost.HasValue,
            false, [], reasons.Distinct().Order(StringComparer.Ordinal).ToArray(), provenance.ToArray());
        if (ea.Status is EvaluationStatus.InvalidOutcome or EvaluationStatus.Incomplete
            || eb.Status is EvaluationStatus.InvalidOutcome or EvaluationStatus.Incomplete)
        { reasons.Add("both_valid_actual_settled_outcomes_required"); return Masked(); }
        if (!CompatibleAudit(first.Audit, second.Audit)) reasons.Add("same_world_root_anchor_controller_and_sampler_required");
        if (a.HpAtCombatStart != b.HpAtCombatStart || a.MaxHpStart != b.MaxHpStart
            || !Equal(Quantities(a.InventoryStart), Quantities(b.InventoryStart))) reasons.Add("combat_start_facts_mismatch");
        if (a.ContinuationPolicyId != b.ContinuationPolicyId) reasons.Add("frozen_continuation_mismatch");
        if (a.SettlementProfileId != b.SettlementProfileId) reasons.Add("settlement_endpoint_mismatch");
        if (!a.InventorySnapshotsComplete || !b.InventorySnapshotsComplete) reasons.Add("inventory_snapshots_incomplete");
        // Matching aggregate counts do not establish matching future opportunity, deck, relic or nonlinear value.
        if (!a.PermanentChangesComplete || !b.PermanentChangesComplete) reasons.Add("permanent_change_ledger_incomplete");
        if (a.PermanentChanges.Length != 0 || b.PermanentChanges.Length != 0) reasons.Add("permanent_or_future_opportunity_changes_not_supported");
        if (a.EarnedBonus == true || b.EarnedBonus == true || a.EarnedBonus != b.EarnedBonus
            || a.SpecifiedFinishSuccess != b.SpecifiedFinishSuccess || a.DeadlineMet != b.DeadlineMet)
            reasons.Add("bonus_or_finish_opportunity_facts_not_supported");
        if (string.IsNullOrWhiteSpace(a.PersistentAssetsAtStartJson) || string.IsNullOrWhiteSpace(a.PersistentAssetsAfterSettlementJson)
            || a.PersistentAssetsAtStartJson != b.PersistentAssetsAtStartJson
            || a.PersistentAssetsAtStartJson != a.PersistentAssetsAfterSettlementJson
            || b.PersistentAssetsAtStartJson != b.PersistentAssetsAfterSettlementJson)
            reasons.Add("exact_unchanged_persistent_assets_required");
        if (a.MaxHpStart != a.MaxHpAfterSettlement || b.MaxHpStart != b.MaxHpAfterSettlement)
            reasons.Add("maximum_hp_change_not_supported");

        bool explicitClosure = closure is not null && !string.IsNullOrWhiteSpace(closure.Evidence)
            && closure.PublicRootKey == first.Audit.PublicRootKey && closure.PublicRootKey == second.Audit.PublicRootKey
            && closure.SettlementProfileId == a.SettlementProfileId && closure.SettlementProfileId == b.SettlementProfileId
            && Quantities(a.InventoryEnd).Values.All(x => x == 0) && Quantities(b.InventoryEnd).Values.All(x => x == 0);
        if (explicitClosure)
            provenance.Add("reviewed_empty_inventory_closure:" + closure!.Evidence);
        else
        {
            if (!CompleteLedger(a) || !CompleteLedger(b)) reasons.Add("complete_reconciled_resource_ledger_or_reviewed_closure_required");
            else provenance.Add("complete_reconciled_resource_ledgers");
        }
        // The only unresolved objective reasons admissible here are inventory prices.
        foreach (var reason in ea.Reasons.Concat(eb.Reasons))
            if (!reason.StartsWith("inventory_value_unresolved:", StringComparison.Ordinal)) reasons.Add(reason);
        var da = Coefficients(a); var db = Coefficients(b);
        if (!Equal(da, db)) reasons.Add("loss_aware_inventory_coefficients_differ");
        // Profile caps reject rather than clip. Certify the WHOLE component remains within its cap
        // for every allowed unresolved unit value; never cancel across a possibly invalid component.
        double bound = da.Sum(x => Math.Abs(x.Value) * (profile.InventoryValues.TryGetValue(x.Key, out var v)
            ? Math.Abs(v.HpEquivalent) : profile.MaximumAbsoluteUnitValue));
        if (!double.IsFinite(bound) || bound > profile.MaximumAbsoluteInventoryAdjustment)
            reasons.Add("common_inventory_component_bound_not_certified");
        if (reasons.Count > 0) return Masked();
        double difference = HpAndDefeatCost(a, profile) - HpAndDefeatCost(b, profile);
        if (!double.IsFinite(difference)) { reasons.Add("nonfinite_relative_cost"); return Masked(); }
        provenance.Add("exact_additive_inventory_cancellation_after_loss_retention_mask");
        provenance.Add("candidate_objective_and_relative_profile_are_development_only");
        return new(LabelProfile, true, difference, ea.Cost.HasValue, eb.Cost.HasValue, false,
            da.OrderBy(x => x.Key, StringComparer.Ordinal).Where(x => x.Value != 0)
                .Select(x => new CancelledInventoryTerm(x.Key, x.Value, !profile.InventoryValues.ContainsKey(x.Key))).ToArray(),
            [], provenance.ToArray());
    }

    public static RelativeBatchEvaluation EvaluateBatch(AuditedOutcome[] first, AuditedOutcome[] second,
        RelativeEvaluationPlan plan, ObjectiveProfile? profile = null, EmptyInventoryClosure? closure = null,
        RelativeDifferenceSupport? support = null)
    {
        if (plan.WorldIds.Length == 0 || plan.WorldIds.Any(string.IsNullOrWhiteSpace)
            || plan.WorldIds.Distinct(StringComparer.Ordinal).Count() != plan.WorldIds.Length
            || string.IsNullOrWhiteSpace(plan.Evidence) || !plan.IndependentFinalEvaluation
            || plan.PlannedComparisons <= 0 || !double.IsFinite(plan.FamilywiseAlpha)
            || plan.FamilywiseAlpha <= 0 || plan.FamilywiseAlpha >= 1
            || !first.Select(x => x.Audit.WorldId).SequenceEqual(plan.WorldIds)
            || !second.Select(x => x.Audit.WorldId).SequenceEqual(plan.WorldIds))
            throw new ArgumentException("Require the complete, predeclared, ordered independent paired-world ledger");
        if (first.Select(x => x.Audit.RootActionIdentity).Distinct(StringComparer.Ordinal).Count() != 1
            || second.Select(x => x.Audit.RootActionIdentity).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new ArgumentException("Each arm must keep the same frozen root action across every world");
        // Same decision/anchor/policy across all allocated worlds, not merely within each pair.
        var reference = first[0];
        if (first.Concat(second).Any(x => !CompatibleAudit(reference.Audit, x.Audit with { WorldId = reference.Audit.WorldId })
            || x.Outcome.ContinuationPolicyId != reference.Outcome.ContinuationPolicyId
            || x.Outcome.SettlementProfileId != reference.Outcome.SettlementProfileId
            || x.Outcome.HpAtCombatStart != reference.Outcome.HpAtCombatStart
            || x.Outcome.MaxHpStart != reference.Outcome.MaxHpStart
            || x.Outcome.PersistentAssetsAtStartJson != reference.Outcome.PersistentAssetsAtStartJson
            || !Equal(Quantities(x.Outcome.InventoryStart), Quantities(reference.Outcome.InventoryStart))))
            throw new ArgumentException("All allocated worlds must share one root, start anchor, controller and frozen continuation");
        var rows = first.Zip(second, (a, b) => Evaluate(a, b, profile, closure)).ToArray();
        int known = rows.Count(x => x.RelativeValueMask);
        var reasons = rows.SelectMany(x => x.Reasons).Distinct().ToList();
        double? mean = known == rows.Length ? rows.Average(x => x.FirstMinusSecondCost!.Value) : null;
        EstimateInterval? interval = null; int? preferred = null;
        var provenance = new List<string> { plan.Evidence, "no_absolute_value_imputation", "no_equivalence_inferred_from_overlapping_intervals" };
        if (known != rows.Length) reasons.Add("unresolved_allocated_worlds_preserved_no_complete_case_mean");
        if (support is null) reasons.Add("no_certified_full_distribution_support_no_preference");
        else
        {
            if (string.IsNullOrWhiteSpace(support.Evidence) || !double.IsFinite(support.Lower)
                || !double.IsFinite(support.Upper) || support.Lower > support.Upper)
                throw new ArgumentException("A finite independently proved full-distribution support is required");
            provenance.Add(support.Evidence);
            if (mean.HasValue)
            {
                interval = FixedSampleEstimates.Mean(rows.Select(x => x.FirstMinusSecondCost!.Value), plan.WorldIds.Length,
                    support.Lower, support.Upper, plan.FamilywiseAlpha / plan.PlannedComparisons);
                if (interval.Value.Upper < 0) preferred = 0;
                else if (interval.Value.Lower > 0) preferred = 1;
            }
        }
        return new(LabelProfile, rows.Length, known, mean.HasValue, mean, interval, preferred, false,
            reasons.ToArray(), provenance.ToArray());
    }

    private static bool CompatibleAudit(RelativeOutcomeAudit a, RelativeOutcomeAudit b) =>
        a.SharedWorldForkVerified && b.SharedWorldForkVerified
        && new[] { a.WorldId, a.PublicRootKey, a.CombatStartAnchor, a.ControllerContext, a.SamplerProfile,
            a.RootActionIdentity, b.RootActionIdentity }.All(x => !string.IsNullOrWhiteSpace(x))
        && a.WorldId == b.WorldId && a.PublicRootKey == b.PublicRootKey && a.CombatStartAnchor == b.CombatStartAnchor
        && a.ControllerContext == b.ControllerContext && a.SamplerProfile == b.SamplerProfile;
    private static Dictionary<string, double> Quantities(IEnumerable<InventoryQuantity> values) => values
        .GroupBy(x => x.ResourceId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(v => (double)v.Count), StringComparer.Ordinal);
    private static Dictionary<string, double> Coefficients(RolloutOutcome x)
    {
        var start = Quantities(x.InventoryStart); var end = Quantities(x.InventoryEnd);
        return start.Keys.Union(end.Keys).ToDictionary(id => id,
            id => start.GetValueOrDefault(id) - (x.TerminalKind == TerminalKind.Win ? end.GetValueOrDefault(id) : 0), StringComparer.Ordinal);
    }
    private static bool Equal(Dictionary<string, double> a, Dictionary<string, double> b) =>
        a.Keys.Union(b.Keys).All(key => a.GetValueOrDefault(key) == b.GetValueOrDefault(key));
    private static bool CompleteLedger(RolloutOutcome x)
    {
        if (!x.ResourceProvenanceComplete || x.ResourceEvents.Any(e => string.IsNullOrWhiteSpace(e.PublicSource)
            || e.Kind is not ("consumed" or "generated" or "discarded"))) return false;
        var expected = Quantities(x.InventoryStart);
        foreach (var e in x.ResourceEvents)
            expected[e.ResourceId] = expected.GetValueOrDefault(e.ResourceId) + (e.Kind == "generated" ? e.Quantity : -e.Quantity);
        return expected.Values.All(n => n >= 0) && Equal(expected, Quantities(x.InventoryEnd));
    }
    private static double HpAndDefeatCost(RolloutOutcome x, ObjectiveProfile p)
    {
        double loss = x.HpAtCombatStart - x.HpAfterSettlement!.Value;
        return (x.TerminalKind == TerminalKind.Loss ? p.DefeatCost : 0) + loss
            + p.DownsideCoefficient * Math.Pow(Math.Max(0, loss), 2) / Math.Max(1, x.HpAtCombatStart);
    }
}
