using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

/// <summary>Whole-policy targets at their exact anchor, never action-value labels.</summary>
public static class FiniteHuntDataset
{
    public const string Version = "nosl.dataset.finite-hunt.v2";
    private const string RulesVersion = "0.111.0";
    private const string LabelEndpoint = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION";

    public static object Record(HuntEvaluationResult evaluation, string sourceRun, string sourceCombat,
        string branchFamily, DecisionPacket? current = null, HuntPublicController? controller = null)
    {
        if (new[] { sourceRun, sourceCombat, branchFamily }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Source grouping identifiers required");
        var seeds = evaluation.Audit.IndependentEvaluationSeeds;
        // Each independent draw allocates one baseline and one plan execution,
        // including draws for which sampling failed before either could start.
        if (seeds.Length == 0 || seeds.Distinct().Count() != seeds.Length
            || evaluation.Worlds.Length != seeds.Length
            || !evaluation.Worlds.Select(x => x.WorldIndex).Order().SequenceEqual(Enumerable.Range(0, seeds.Length)))
            throw new ArgumentException("Every unique independent evaluation seed requires exactly one paired world");
        var anchor = evaluation.Anchor;
        if (evaluation.Worlds.Any(x => x.Baseline.Outcome.ContinuationPolicyId != anchor.BaselinePolicyId
            || x.Plan.Outcome.ContinuationPolicyId != anchor.TemplateId))
            throw new ArgumentException("Paired outcomes must retain their original continuation identities");
        var baselineCounts = CountWorlds(evaluation.Worlds.Select(x => x.Baseline.Outcome).ToArray());
        var planCounts = CountWorlds(evaluation.Worlds.Select(x => x.Plan.Outcome).ToArray());
        current ??= PublicJson.Read<DecisionPacket>(anchor.PublicSummary);
        var input = HuntStudentContext.Input(anchor, current, controller);
        bool atAnchor = PublicJson.Serialize(current) == anchor.PublicSummary
            && (controller is null || controller.Status == "active");
        bool success = atAnchor && evaluation.Masks.MeanSpecifiedSuccess;
        bool cost = atAnchor && evaluation.Masks.MeanExtraNetHpLoss;
        var actions = current.Actions.Select((_, index) => new
        {
            action_index = index, value = (double?)null, win_probability = (double?)null,
            death_probability = (double?)null, expected_final_hp = (double?)null,
            hp_distribution = (object?)null, potion_net_change = (double?)null, quality = "unresolved",
            masks = new { value = false, win_probability = false, death_probability = false,
                expected_final_hp = false, hp_distribution = false, potion_net_change = false },
            allocated_worlds = 0, completed_worlds = 0, truncated_worlds = 0, error_worlds = 0, other_worlds = 0,
        }).ToArray();
        return new
        {
            public_input = input,
            targets = new
            {
                actions, pairwise = Array.Empty<object>(), equivalent_action_set = Array.Empty<int>(),
                plan = new
                {
                    label_scope = atAnchor ? "whole_plan_from_anchor" : "unavailable",
                    specified_success_probability = success ? evaluation.MeanSpecifiedSuccess : null,
                    extra_net_hp_loss = cost ? evaluation.MeanExtraNetHpLoss : null,
                    masks = new { specified_success_probability = success, extra_net_hp_loss = cost },
                    allocated_worlds = seeds.Length,
                    success_completed_worlds = planCounts.Completed,
                    paired_completed_worlds = evaluation.Worlds.Count(x => x.Plan.Outcome.IsTrueTerminal && x.Plan.Outcome.SettlementComplete
                        && x.Baseline.Outcome.IsTrueTerminal && x.Baseline.Outcome.SettlementComplete),
                },
            },
            audit_only = new
            {
                dataset_version = Version, source_run_group = sourceRun, source_combat_id = sourceCombat,
                branch_family = branchFamily, source_kind = "constructed", engineering_smoke = true,
                public_state_digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(input)))).ToLowerInvariant(),
                simulator_commit = ContentAudit.UpstreamCommit,
                supermodel_base_commit = "d01c5f8ec56d799a80cf7a62a0f9296b3ac8d218",
                rules_version = RulesVersion, label_endpoint = LabelEndpoint,
                teacher_version = evaluation.Audit.EvaluatorVersion, objective_version = evaluation.Audit.ObjectiveProfileId,
                continuation_version = anchor.TemplateId, baseline_version = anchor.BaselinePolicyId,
                controller_version = HuntStudentContext.ContextSchema, sampler_version = BeliefSampler.ImplementationVersion,
                posterior_implementation = BeliefSampler.ImplementationVersion, sampling_method = evaluation.Audit.SamplingMethod,
                formal_labels_allowed = false, objective_calibrated = false,
                conditional_budget_management = false, eligibility_is_not_a_target = true,
                versions = new { dataset = Version, public_schema = HuntStudentContext.PublicSchema,
                    observation_schema = current.Observation!.Schema,
                    controller = HuntStudentContext.ContextSchema, teacher = evaluation.Audit.EvaluatorVersion,
                    continuation = anchor.TemplateId.Split(':')[0], baseline = anchor.BaselinePolicyId,
                    objective = evaluation.Audit.ObjectiveProfileId, sampler = BeliefSampler.ImplementationVersion,
                    simulator = ContentAudit.UpstreamCommit, rules = RulesVersion, endpoint = LabelEndpoint },
                independent_final_evaluation = true, candidate_evaluation_performed = false,
                n_exploration = 0, n_exploration_sampling_failures = 0, n_independent_eval = seeds.Length,
                n_unresolved = baselineCounts.Truncated + baselineCounts.Other + planCounts.Truncated + planCounts.Other,
                n_error = baselineCounts.Errors + planCounts.Errors,
                sampler_seeds = seeds.ToArray(), exploration_seeds = Array.Empty<ulong>(),
                plan_counts = new { baseline = baselineCounts, plan = planCounts },
                costs = new
                {
                    cost_unit = "policy_world_execution", root_candidates = current.Actions.Length,
                    worlds_allocated = baselineCounts.Allocated + planCounts.Allocated,
                    worlds_completed = baselineCounts.Completed + planCounts.Completed,
                    rollout_decisions = evaluation.Worlds.Sum(x => (long)x.Baseline.PublicTrace.Length + x.Plan.PublicTrace.Length),
                    // These measurements were not collected by the paired evaluator.
                    elapsed_seconds = (double?)null, clone_seconds = (double?)null,
                    settlement_seconds = (double?)null, peak_worker_memory_bytes = (long?)null,
                },
                paired_evidence = evaluation,
            },
        };
    }

    private sealed record WorldCounts(
        [property: JsonPropertyName("allocated_worlds")] int Allocated,
        [property: JsonPropertyName("completed_worlds")] int Completed,
        [property: JsonPropertyName("truncated_worlds")] int Truncated,
        [property: JsonPropertyName("error_worlds")] int Errors,
        [property: JsonPropertyName("other_worlds")] int Other);

    private static WorldCounts CountWorlds(RolloutOutcome[] outcomes)
    {
        int completed = outcomes.Count(x => x.IsTrueTerminal && x.SettlementComplete);
        int truncated = outcomes.Count(x => x.TerminalKind == TerminalKind.ComputeTruncated);
        int errors = outcomes.Count(x => x.TerminalKind == TerminalKind.EngineError);
        // Unsettled terminal claims and policy nontermination retain unknown mass.
        return new(outcomes.Length, completed, truncated, errors, outcomes.Length - completed - truncated - errors);
    }
}
