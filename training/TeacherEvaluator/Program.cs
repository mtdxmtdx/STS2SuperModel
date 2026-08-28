using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using STS2BestChoice.Core.Model;
using STS2BestChoice.Core.Search;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

string? input;
while ((input = Console.ReadLine()) is not null)
{
    if (string.IsNullOrWhiteSpace(input)) continue;
    try
    {
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (!root.TryGetProperty("protocol", out var protocol) ||
            protocol.GetString() != "sts2.teacher-evaluator.v1")
            throw new InvalidDataException("unsupported teacher evaluator protocol");
        if (!root.TryGetProperty("combat_snapshot", out var snapshotElement) ||
            snapshotElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidDataException("combat_snapshot is required; raw teacher_snapshot must be rebuilt first");

        var snapshot = snapshotElement.Deserialize<CombatSnapshot>(jsonOptions)
            ?? throw new InvalidDataException("combat_snapshot could not be deserialized");
        var searchElement = root.TryGetProperty("search", out var search) ? search : default;
        var topK = ReadInt(searchElement, "top_k", 5, 1, 100);
        var budgetMs = ReadInt(searchElement, "budget_ms", 500, 1, 60_000);
        var maxNodes = ReadInt(searchElement, "maximum_expanded_nodes", 2_000_000, 1, 20_000_000);
        var options = SearchOptions.Default with
        {
            InitialBudget = TimeSpan.FromMilliseconds(budgetMs),
            TopCount = topK,
            MaximumExpandedNodes = maxNodes
        };
        var result = new CombatSearchSession(snapshot, options).Continue(options.InitialBudget);
        var legalActions = root.TryGetProperty("legal_actions", out var legal) ? legal : default;

        var balanced = BuildObjective(result.Balanced, result.EstimatedBalanced, legalActions, topK,
            static line => line.Score?.BalancedScore ?? decimal.MinValue);
        var damage = BuildObjective(result.HighestDamage, result.EstimatedHighestDamage, legalActions, topK,
            static line => line.Score?.EffectiveDamage ?? decimal.MinValue);
        var loss = BuildObjective(result.MinimumLoss, result.EstimatedMinimumLoss, legalActions, topK,
            static line => -(line.Score?.PlayerHpLoss ?? decimal.MaxValue));
        var selectedLines = balanced.Lines;
        var confidence = selectedLines.Length == 0
            ? PredictionConfidence.Uncalculable
            : selectedLines.Any(static line => line.Confidence != PredictionConfidence.Reliable) || !result.Progress.IsComplete
                ? PredictionConfidence.Estimated
                : PredictionConfidence.Reliable;
        var stochastic = selectedLines.Any(static line => line.OutcomeKind == OutcomeKind.Stochastic);
        var labelQuality = confidence switch
        {
            PredictionConfidence.Uncalculable => "Uncalculable",
            PredictionConfidence.Estimated when !result.Progress.IsComplete => "BudgetBound",
            PredictionConfidence.Estimated => "EstimatedByHeuristic",
            _ when stochastic => "ExactWithKnownChance",
            _ => "ExactComplete"
        };
        var risks = selectedLines
            .SelectMany(static line => line.Restrictions)
            .Select(static restriction => restriction.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!result.Progress.IsComplete) risks.Add("budget_bound");

        var response = new Dictionary<string, object?>
        {
            ["objectives"] = new Dictionary<string, object?>
            {
                ["Balanced"] = balanced.Payload,
                ["HighestDamage"] = damage.Payload,
                ["MinimumLoss"] = loss.Payload
            },
            ["teacher_best_actions"] = balanced.BestActions,
            ["teacher_top_k"] = balanced.TopK,
            ["action_values"] = balanced.ActionValues,
            ["death_probability"] = balanced.DeathProbability,
            ["search_budget_ms"] = budgetMs,
            ["expanded_nodes"] = result.Progress.ExpandedNodes,
            ["chance_branch"] = new Dictionary<string, object?>
            {
                ["produced"] = stochastic,
                ["kind"] = stochastic ? "known_distribution" : "none",
                ["policy_count"] = result.Policies.IsDefault ? 0 : result.Policies.Length
            },
            ["confidence"] = confidence.ToString(),
            ["label_quality"] = labelQuality,
            ["search_complete"] = result.Progress.IsComplete,
            ["risk_events"] = risks
        };
        Console.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
    }
    catch (Exception exception)
    {
        var error = new Dictionary<string, object?>
        {
            ["objectives"] = EmptyObjectives(),
            ["teacher_best_actions"] = Array.Empty<string>(),
            ["teacher_top_k"] = Array.Empty<object>(),
            ["action_values"] = new Dictionary<string, decimal>(),
            ["death_probability"] = 1m,
            ["search_budget_ms"] = 0,
            ["expanded_nodes"] = 0,
            ["chance_branch"] = new Dictionary<string, object?> { ["produced"] = false, ["kind"] = "none" },
            ["confidence"] = PredictionConfidence.Uncalculable.ToString(),
            ["label_quality"] = "Uncalculable",
            ["search_complete"] = false,
            ["risk_events"] = new[] { "teacher_snapshot_rebuild_failed" },
            ["error"] = $"{exception.GetType().Name}: {exception.Message}"
        };
        Console.WriteLine(JsonSerializer.Serialize(error, jsonOptions));
    }
}

static int ReadInt(JsonElement element, string name, int fallback, int minimum, int maximum)
{
    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed))
        return Math.Clamp(parsed, minimum, maximum);
    return fallback;
}

static ObjectiveResult BuildObjective(
    ImmutableArray<ActionLine> reliable,
    ImmutableArray<ActionLine> estimated,
    JsonElement legalActions,
    int topK,
    Func<ActionLine, decimal> valueSelector)
{
    var source = reliable.IsDefaultOrEmpty ? estimated : reliable;
    var ranked = source
        .Where(static line => !line.Steps.IsDefaultOrEmpty)
        .Select(line => new RankedLine(line, ResolveActionId(line.Steps[0], legalActions), valueSelector(line)))
        .Where(static item => item.ActionId is not null && item.Value != decimal.MinValue)
        .GroupBy(static item => item.ActionId!, StringComparer.Ordinal)
        .Select(static group => group.OrderByDescending(item => item.Value).First())
        .OrderByDescending(static item => item.Value)
        .ThenBy(static item => item.ActionId, StringComparer.Ordinal)
        .Take(topK)
        .ToArray();
    var bestValue = ranked.Length == 0 ? 0m : ranked[0].Value;
    var bestActions = ranked.Where(item => item.Value == bestValue).Select(item => item.ActionId!).ToArray();
    var values = ranked.ToDictionary(item => item.ActionId!, item => item.Value, StringComparer.Ordinal);
    var top = ranked.Select((item, index) => new Dictionary<string, object?>
    {
        ["action_id"] = item.ActionId,
        ["value"] = item.Value,
        ["rank"] = index + 1,
        ["death_probability"] = item.Line.Terminal?.PlayerDied == true ? 1m : 0m
    }).ToArray();
    var payload = new Dictionary<string, object?>
    {
        ["best_actions"] = bestActions,
        ["value"] = bestValue,
        ["action_values"] = values
    };
    return new ObjectiveResult(
        payload,
        bestActions,
        top,
        values,
        ranked.Length == 0 ? 1m : ranked[0].Line.Terminal?.PlayerDied == true ? 1m : 0m,
        ranked.Select(static item => item.Line).ToImmutableArray());
}

static string? ResolveActionId(ActionStep step, JsonElement legalActions)
{
    if (legalActions.ValueKind != JsonValueKind.Array) return null;
    var expectedKind = step.Kind switch
    {
        ActionKind.PlayCard => "PlayCard",
        ActionKind.UsePotion => "UsePotion",
        ActionKind.Choose => "Choice",
        ActionKind.EndTurn => "EndTurn",
        _ => step.Kind.ToString()
    };
    foreach (var candidate in legalActions.EnumerateArray())
    {
        if (ReadString(candidate, "kind") != expectedKind) continue;
        if (step.Kind != ActionKind.EndTurn && ReadString(candidate, "source_instance_id") != step.SourceInstanceId) continue;
        if (step.TargetId is not null && ReadString(candidate, "target_id") != step.TargetId) continue;
        if (step.ChoiceId is not null && ReadString(candidate, "choice_id") != step.ChoiceId) continue;
        return ReadString(candidate, "action_id");
    }
    return null;
}

static string? ReadString(JsonElement element, string name) =>
    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static Dictionary<string, object?> EmptyObjectives() => new()
{
    ["Balanced"] = new { best_actions = Array.Empty<string>(), value = 0m, action_values = new Dictionary<string, decimal>() },
    ["HighestDamage"] = new { best_actions = Array.Empty<string>(), value = 0m, action_values = new Dictionary<string, decimal>() },
    ["MinimumLoss"] = new { best_actions = Array.Empty<string>(), value = 0m, action_values = new Dictionary<string, decimal>() }
};

internal sealed record RankedLine(ActionLine Line, string? ActionId, decimal Value);
internal sealed record ObjectiveResult(
    Dictionary<string, object?> Payload,
    string[] BestActions,
    Dictionary<string, object?>[] TopK,
    Dictionary<string, decimal> ActionValues,
    decimal DeathProbability,
    ImmutableArray<ActionLine> Lines);
