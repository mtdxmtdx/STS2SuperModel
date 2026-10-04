using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

/// <summary>
/// Fresh source-backed constructed engineering evidence. The Python boundary
/// validates exact full-v5 conditioning and produces the quarantined v5 envelope.
/// This is neither a natural-run prior nor an admission or training operation.
/// </summary>
public static class FiniteHuntDatasetV5
{
    public const string RawSchema = "nosl.dataset.finite-hunt.full-v5.raw.v1";
    public const string ProducerVersion = "nosl.finite-hunt.full-v5.producer.v1";
    internal const string EvaluatorVersion = "nosl-anchored-hunt-full-v5-evaluator-v1";

    public static async Task<object> GenerateAsync(Scenario scenario, HuntEvaluationOptions options,
        string sourceRun, string sourceCombat, string branchFamily)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.EvaluationSeeds.Length > 16 || options.MaxDecisionsPerPolicy > 200 || options.MaxPosteriorAttempts > 4096)
            throw new ArgumentException("Fresh v5 engineering generation requires at most 16 worlds, 200 decisions and 4096 proposals per world");
        if (new[] { sourceRun, sourceCombat, branchFamily }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Declared source grouping identifiers required");
        // Freeze caller-owned arrays before opening the new source. No old raw
        // record, label file, native carry-in or hidden source snapshot is accepted.
        scenario = PublicJson.Read<Scenario>(PublicJson.Serialize(scenario));
        options = options with { EvaluationSeeds = options.EvaluationSeeds.ToArray() };
        await using var source = await CombatSession.CreateAsync(scenario);
        if (BeliefSampler.PosteriorProfileFor(source) != BeliefSampler.WholeSetupReplayProfile)
            throw new NotSupportedException("Fresh Hunt v5 generation requires the existing whole-setup replay prior");
        var sampledRoots = new List<object>();
        var terminals = new List<object>();
        var execution = new HuntExecution
        {
            Observe = ConstructedHuntV5View.Observe,
            EvaluatorVersion = EvaluatorVersion,
            SampleAccepted = (index, packet) => sampledRoots.Add(new { worldIndex = index, publicRoot = packet }),
            TerminalObserved = (index, pursuing, packet) => terminals.Add(new
            {
                worldIndex = index, policyRole = pursuing ? "plan" : "baseline",
                publicEvidence = JsonNode.Parse(PublicRunEvidenceJson.Serialize(packet.PublicEvidence!)),
            }),
        };
        var evaluation = await AnchoredHuntEvaluator.EvaluateAsync(source, options, execution);
        return Record(evaluation, source.InitialScenario, options, sourceRun, sourceCombat, branchFamily,
            sampledRoots.ToArray(), terminals.ToArray());
    }

    internal static object Record(HuntEvaluationResult evaluation, Scenario scenario, HuntEvaluationOptions options,
        string sourceRun, string sourceCombat, string branchFamily, object[] sampledRoots, object[] terminalEvidence,
        DecisionPacket? current = null, HuntPublicController? controller = null)
    {
        if (evaluation.Audit.EvaluatorVersion != EvaluatorVersion
            || !evaluation.Audit.IndependentEvaluationSeeds.SequenceEqual(options.EvaluationSeeds)
            || evaluation.Worlds.Length != options.EvaluationSeeds.Length
            || !evaluation.Worlds.Select(w => w.WorldIndex).Order().SequenceEqual(Enumerable.Range(0, options.EvaluationSeeds.Length)))
            throw new ArgumentException("Every declared evaluation draw must retain one fresh full-v5 paired result");
        current ??= PublicJson.Read<DecisionPacket>(evaluation.Anchor.PublicSummary);
        object input = ConstructedHuntV5View.HuntInput(evaluation.Anchor, current, controller);
        string publicJson = PublicJson.Serialize(input);
        bool atAnchor = PublicJson.Serialize(current) == evaluation.Anchor.PublicSummary
            && (controller is null || controller.Status == "active");
        bool Complete(RolloutOutcome outcome) => outcome.IsTrueTerminal && outcome.SettlementComplete
            && ObjectiveEvaluator.Evaluate(outcome).Status != EvaluationStatus.InvalidOutcome;
        int completePlan = evaluation.Worlds.Count(w => Complete(w.Plan.Outcome));
        int completePairs = evaluation.Worlds.Count(w => Complete(w.Plan.Outcome) && Complete(w.Baseline.Outcome));
        bool success = atAnchor && completePlan == evaluation.Worlds.Length;
        bool cost = atAnchor && completePairs == evaluation.Worlds.Length;
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
            schema_version = RawSchema, record_kind = "constructed_finite_hunt_raw", public_input = input,
            targets = new
            {
                actions, pairwise = Array.Empty<object>(), equivalent_action_set = Array.Empty<int>(),
                plan = new
                {
                    label_scope = atAnchor ? "whole_plan_from_anchor" : "unavailable",
                    specified_success_probability = success ? evaluation.MeanSpecifiedSuccess : null,
                    extra_net_hp_loss = cost ? evaluation.MeanExtraNetHpLoss : null,
                    masks = new { specified_success_probability = success, extra_net_hp_loss = cost },
                    allocated_worlds = evaluation.Worlds.Length, success_completed_worlds = completePlan,
                    paired_completed_worlds = completePairs,
                },
            },
            audit_only = new
            {
                producer_version = ProducerVersion, trainable = false, formal_labels = false,
                source_kind = "constructed_declared_setup", native_run = false,
                source_run_group = sourceRun, source_combat_id = sourceCombat, branch_family = branchFamily,
                scenario, evaluation_options = options, public_input_json = publicJson,
                public_input_json_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicJson))).ToLowerInvariant(),
                paired_evidence = evaluation, sampled_public_roots = sampledRoots, terminal_public_evidence = terminalEvidence,
                public_conditioning = "full_v5_exact_sample_and_branch_equality",
                posterior_profile = BeliefSampler.WholeSetupReplayProfile, legacy_labels_read = false,
            },
        };
    }
}
