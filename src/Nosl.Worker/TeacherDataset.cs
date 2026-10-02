using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

public static class TeacherDataset
{
    public static object RecordFiniteHunt(HuntEvaluationResult result, string sourceRun, string sourceCombat,
        string branchFamily, DecisionPacket? current = null, HuntPublicController? controller = null) =>
        FiniteHuntDataset.Record(result, sourceRun, sourceCombat, branchFamily, current, controller);

    public static object Record(TeacherResult result, string sourceRun, string sourceCombat, string branchFamily,
        ulong[] evaluationSeeds, ulong[] explorationSeeds)
    {
        if (new[] { sourceRun, sourceCombat, branchFamily }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Source grouping identifiers required");
        bool forcedEvent = result.PublicRoot.Observation!.History.Any(e => e.Kind == "forced_event_context");
        string publicSchema = forcedEvent || result.PublicRoot.Observation.History.Any(e => e.Kind == "native_entry_assets")
            ? HuntStudentContext.PublicSchema : "nosl.student.public.v1";
        var publicInput = new
        {
            schema_version = publicSchema, observation = result.PublicRoot.Observation,
            history_complete = true, controller_context = new { status = "inactive" },
            candidate_actions = result.PublicRoot.Actions, legal_mask = result.PublicRoot.Actions.Select(_ => true).ToArray(),
        };
        var actions = result.Candidates.Select((candidate, index) =>
        {
            var o = candidate.Outcomes; var evaluation = candidate.Evaluation;
            bool complete = o.All(x => x.IsTrueTerminal && x.SettlementComplete) && evaluation.InvalidOutcomes == 0;
            bool value = complete && evaluation.ExpectedCost.HasValue;
            double? win = complete ? (double)o.Count(x => x.TerminalKind == TerminalKind.Win) / o.Length : null;
            double? death = complete ? (double)o.Count(x => x.PlayerAlive == false) / o.Length : null;
            double? hp = complete ? o.Average(x => x.HpAfterSettlement!.Value) : null;
            double? potion = complete ? o.Average(x => x.InventoryEnd.Sum(i => i.Count) - x.InventoryStart.Sum(i => i.Count)) : null;
            object? distribution = complete ? o.GroupBy(x => x.HpAfterSettlement!.Value).OrderBy(x => x.Key)
                .Select(g => new { hp = g.Key, probability = (double)g.Count() / o.Length }).ToArray() : null;
            return new
            {
                action_index = index, value = value ? -evaluation.ExpectedCost : null,
                win_probability = win, death_probability = death, expected_final_hp = hp,
                hp_distribution = distribution, potion_net_change = potion,
                quality = !complete ? "unresolved" : value ? "complete" : "objective_value_unresolved",
                masks = new { value, win_probability = complete, death_probability = complete, expected_final_hp = complete,
                    hp_distribution = complete, potion_net_change = complete },
                allocated_worlds = o.Length, completed_worlds = o.Count(x => x.IsTrueTerminal && x.SettlementComplete),
                truncated_worlds = o.Count(x => x.TerminalKind == TerminalKind.ComputeTruncated),
                error_worlds = o.Count(x => x.TerminalKind == TerminalKind.EngineError),
                other_worlds = o.Count(x => x.TerminalKind == TerminalKind.PolicyNonterminating),
            };
        }).ToArray();
        var record = new
        {
            public_input = publicInput,
            targets = new { actions, pairwise = result.Ranking.Pairs.Select(p => new { preferred = p.Preferred, other = p.Other, weight = p.Weight }).ToArray(), equivalent_action_set = Array.Empty<int>() },
            audit_only = new
            {
                source_run_group = sourceRun, source_combat_id = sourceCombat, branch_family = branchFamily,
                public_state_digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJson.Serialize(publicInput)))).ToLowerInvariant(),
                source_kind = "constructed", engineering_smoke = true,
                simulator_commit = "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0", supermodel_base_commit = "d01c5f8ec56d799a80cf7a62a0f9296b3ac8d218",
                rules_version = "0.111.0", label_endpoint = "AFTER_AUTOMATIC_SETTLEMENT_BEFORE_FIRST_POSTCOMBAT_DECISION",
                teacher_version = result.TeacherVersion, continuation_version = result.ContinuationVersion,
                posterior_profile = result.Scope, posterior_implementation = BeliefSampler.ImplementationVersion,
                teacher_warnings = result.Warnings,
                objective_version = result.ObjectiveVersion, objective_calibrated = false,
                versions = new { posterior_implementation = BeliefSampler.ImplementationVersion, teacher = result.TeacherVersion, continuation = result.ContinuationVersion.Split(':')[0], objective = result.ObjectiveVersion, public_schema = publicSchema, observation_schema = result.PublicRoot.Observation!.Schema, simulator = "5a9576b9cc7b4c4fe98bde6d73890c76c947a3d0" },
                independent_final_evaluation = true,
                n_exploration = result.ExplorationWorlds, n_exploration_sampling_failures=result.Costs.ExplorationSamplingFailures, n_independent_eval = result.EvaluationWorlds,
                n_unresolved = actions.Sum(a => a.truncated_worlds + a.other_worlds), n_error = actions.Sum(a => a.error_worlds),
                sampler_seeds = evaluationSeeds, exploration_seeds = explorationSeeds,
                outcome_samples = result.Candidates.Select((c, i) => new { action_index = i, outcomes = c.Outcomes }).ToArray(),
                ranking_evidence = result.Ranking.Method, ranking_intervals = result.Ranking.Intervals, ranking_limitations = result.Ranking.Limitations,
                costs = new { root_candidates = result.Costs.RootCandidates, worlds_allocated = result.Costs.WorldsAllocated,
                    worlds_completed = result.Costs.WorldsCompleted, rollout_decisions = result.Costs.RolloutDecisions,
                    elapsed_seconds = result.Costs.ElapsedSeconds, clone_seconds = result.Costs.CloneSeconds,
                    settlement_seconds = result.Costs.SettlementSeconds, peak_worker_memory_bytes = result.Costs.PeakWorkerMemoryBytes },
            },
        };
        // Preserve the legacy record shape; a new continuation gets a new
        // dataset lock, so prepare_dataset refuses to append it to a v1 corpus.
        if (publicSchema != HuntStudentContext.PublicSchema
            && PublicContinuationPolicies.DatasetVersion(result.ContinuationVersion) != PublicContinuationPolicies.ReviewedDatasetVersion)
            return record;
        var versioned = JsonNode.Parse(PublicJson.Serialize(record))!.AsObject();
        string datasetVersion = forcedEvent ? "nosl.dataset.forced-events.v2" : PublicContinuationPolicies.DatasetVersion(result.ContinuationVersion);
        versioned["audit_only"]!["dataset_version"] = datasetVersion;
        versioned["audit_only"]!["versions"]!["dataset"] = datasetVersion;
        if (forcedEvent) versioned["audit_only"]!["constructed_event_fixture"] = true;
        if (publicSchema == HuntStudentContext.PublicSchema)
        {
            // The ordinary v2 contract carries an inactive controller, not a
            // running finite Hunt policy. Leave every public-v1 audit unchanged.
            var audit = versioned["audit_only"]!.AsObject();
            var versions = audit["versions"]!.AsObject();
            audit["controller_version"] = "nosl.controller.inactive.v1";
            audit["sampler_version"] = BeliefSampler.ImplementationVersion;
            versions["rules"] = audit["rules_version"]!.DeepClone();
            versions["endpoint"] = audit["label_endpoint"]!.DeepClone();
            versions["controller"] = audit["controller_version"]!.DeepClone();
            versions["sampler"] = audit["sampler_version"]!.DeepClone();
        }
        return versioned;
    }
}
