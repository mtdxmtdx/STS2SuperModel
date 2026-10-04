using Nosl.Contracts;

namespace Nosl.Worker;

/// <summary>Inspectable engineering evidence, not action supervision or Hunt-head labels.</summary>
public static class FiniteRegenDataset
{
    public static object Record(RegenEvaluationResult evaluation)
    {
        var packet = PublicJson.Read<DecisionPacket>(evaluation.Anchor.PublicSummary);
        return new
        {
            public_input = RegenStudentContext.Input(evaluation.Anchor, packet),
            targets = new
            {
                actions = packet.Actions.Select((_, index) => new
                {
                    action_index = index, value = (double?)null, win_probability = (double?)null, death_probability = (double?)null,
                    expected_final_hp = (double?)null, hp_distribution = (object?)null, potion_net_change = (double?)null, quality = "unresolved",
                    masks = new { value = false, win_probability = false, death_probability = false, expected_final_hp = false,
                        hp_distribution = false, potion_net_change = false },
                    allocated_worlds = 0, completed_worlds = 0, truncated_worlds = 0, error_worlds = 0, other_worlds = 0,
                }).ToArray(), pairwise = Array.Empty<object>(), equivalent_action_set = Array.Empty<int>(),
                plan = new
                {
                    label_scope = "unavailable", specified_success_probability = (double?)null, extra_net_hp_loss = (double?)null,
                    masks = new { specified_success_probability = false, extra_net_hp_loss = false },
                    allocated_worlds = evaluation.Worlds.Length,
                    success_completed_worlds = evaluation.Worlds.Count(x => x.Plan.Outcome.IsTrueTerminal && x.Plan.Outcome.SettlementComplete),
                    paired_completed_worlds = evaluation.Worlds.Count(x => x.Plan.Outcome.IsTrueTerminal && x.Plan.Outcome.SettlementComplete
                        && x.Baseline.Outcome.IsTrueTerminal && x.Baseline.Outcome.SettlementComplete),
                },
            },
            audit_only = new
            {
                dataset_version = "nosl.finite-regen-evidence.v1", trainable = false, engineering_smoke = true,
                candidate_evaluation_performed = false, formal_labels_allowed = false,
                paired_evidence = evaluation,
            },
        };
    }
}
