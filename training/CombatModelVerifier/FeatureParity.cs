using System.Text.Json;

internal static class FeatureParity
{
    public static object Verify(string fixturePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = document.RootElement;
        var row = root.GetProperty("row");
        var expected = root.GetProperty("expected");
        var tokens = root.GetProperty("vocabulary").GetProperty("tokens").EnumerateArray()
            .Select((value, index) => (Token: value.GetString()!, Index: index))
            .ToDictionary(item => item.Token, item => item.Index, StringComparer.Ordinal);
        var encoded = Encode(row, tokens);
        var maximumError = 0.0;
        Compare(expected.GetProperty("state_numeric"), encoded.StateNumeric, ref maximumError);
        Compare(expected.GetProperty("state_token_weights"), encoded.StateTokenWeights, ref maximumError);
        Compare(expected.GetProperty("state_token_ids"), encoded.StateTokenIds);
        Compare(expected.GetProperty("enemy_token_ids"), encoded.EnemyTokenIds);
        CompareNested(expected.GetProperty("enemy_numeric"), encoded.EnemyNumeric, ref maximumError);
        CompareNested(expected.GetProperty("candidate_token_ids"), encoded.CandidateTokenIds);
        CompareNested(expected.GetProperty("candidate_numeric"), encoded.CandidateNumeric, ref maximumError);
        var expectedActions = expected.GetProperty("action_ids").EnumerateArray().Select(value => value.GetString()).ToArray();
        if (!expectedActions.SequenceEqual(encoded.ActionIds))
            throw new InvalidOperationException("feature action IDs differ");
        if (maximumError > 1e-9)
            throw new InvalidOperationException($"feature numeric error {maximumError} exceeds 1e-9");
        return new { verdict = "pass", encoder = "csharp-independent", maximum_absolute_error = maximumError, tolerance = 1e-9 };
    }

    private static Encoded Encode(JsonElement row, IReadOnlyDictionary<string, int> vocabulary)
    {
        var state = row.GetProperty("public_state");
        var player = state.GetObject("player");
        var enemies = state.GetArray("enemies")
            .OrderBy(enemy => enemy.GetString("instance_id", enemy.GetString("name", "")), StringComparer.Ordinal)
            .ToArray();
        var maxHp = Math.Max(player.GetDouble("max_hp", 1), 1);
        var maxEnergy = Math.Max(state.GetDouble("max_energy", 1), 1);
        var incoming = enemies.Sum(IntentDamage);
        var totalEnemyRatio = enemies.Sum(enemy => enemy.GetDouble("hp") / Math.Max(enemy.GetDouble("max_hp", 1), 1));
        var hand = state.GetArray("hand").ToArray();
        var stateNumeric = new[]
        {
            player.GetDouble("hp") / maxHp,
            player.GetDouble("block") / maxHp,
            state.GetDouble("energy") / maxEnergy,
            state.GetDouble("energy") / 10.0,
            state.GetDouble("max_energy") / 10.0,
            state.GetDouble("round", row.GetDouble("round")) / 20.0,
            row.GetDouble("act") / 3.0,
            row.GetDouble("floor") / 60.0,
            row.GetDouble("ascension") / 20.0,
            hand.Length / 10.0,
            state.GetDouble("draw_pile_count") / 50.0,
            state.GetDouble("discard_pile_count") / 50.0,
            state.GetDouble("exhaust_pile_count") / 50.0,
            enemies.Length / 6.0,
            totalEnemyRatio / Math.Max(enemies.Length, 1),
            incoming / maxHp,
        };
        var weighted = new List<(string Token, double Weight)>
        {
            ($"character:{Id(row.GetString("character"))}", 1),
            ($"room:{Id(state.GetObject("context").GetString("room_type"))}", 1),
        };
        foreach (var card in hand)
            weighted.Add((CardToken(card.GetString("id"), card.GetBool("upgraded")), 1));
        foreach (var zone in new[] { "draw", "discard", "exhaust" })
            foreach (var card in state.GetArray($"{zone}_pile_multiset"))
                weighted.Add((CardToken(card.GetString("model_id"), card.GetBool("upgraded")), card.GetDouble("count")));
        foreach (var relic in player.GetArray("relics"))
            weighted.Add(($"relic:{Id(relic.GetString("id"))}", 1));
        foreach (var power in state.GetArray("player_powers"))
            weighted.Add(($"power:{Id(power.GetString("id"))}", Math.Max(Math.Abs(power.GetDouble("amount", 1)), 1)));
        weighted.Sort((left, right) =>
        {
            var token = StringComparer.Ordinal.Compare(left.Token, right.Token);
            return token != 0 ? token : left.Weight.CompareTo(right.Weight);
        });

        var enemyIds = new List<int>();
        var enemyNumeric = new List<double[]>();
        foreach (var enemy in enemies)
        {
            var enemyMaxHp = Math.Max(enemy.GetDouble("max_hp", 1), 1);
            enemyIds.Add(TokenId(vocabulary, $"enemy:{Id(enemy.GetString("name", enemy.GetString("instance_id")))}"));
            enemyNumeric.Add(new[]
            {
                enemy.GetDouble("hp") / enemyMaxHp,
                enemy.GetDouble("block") / enemyMaxHp,
                enemy.GetBool("intends_attack") ? 1.0 : 0.0,
                enemy.GetBool("is_hittable", true) ? 1.0 : 0.0,
                enemy.GetBool("is_minion") ? 1.0 : 0.0,
                enemy.GetBool("is_primary_enemy") ? 1.0 : 0.0,
                IntentDamage(enemy) / enemyMaxHp,
                enemy.GetArray("intents").Count() / 4.0,
            });
        }

        var handByInstance = hand.Where(card => card.Has("instance_id"))
            .ToDictionary(card => card.GetString("instance_id"), card => card, StringComparer.Ordinal);
        var candidateIds = new List<int[]>();
        var candidateNumeric = new List<double[]>();
        var actionIds = new List<string?>();
        foreach (var action in row.GetArray("legal_actions"))
        {
            var kind = action.GetString("kind", "Unknown");
            var sourceModel = action.GetString("source_model_id", "");
            var targetId = action.GetString("target_id", "");
            var sourceInstance = action.GetString("source_instance_id", "");
            var sourceCard = handByInstance.TryGetValue(sourceInstance, out var card) ? card : default;
            var stats = sourceCard.ValueKind == JsonValueKind.Object ? sourceCard.GetObject("stats") : default;
            var targetModel = targetId;
            var separator = targetModel.LastIndexOf(':');
            if (separator >= 0) targetModel = targetModel[..separator];
            candidateIds.Add(new[]
            {
                TokenId(vocabulary, $"action:{Id(kind)}"),
                sourceModel.Length > 0 ? TokenId(vocabulary, CardToken(sourceModel, false)) : 0,
                targetId.Length > 0 ? TokenId(vocabulary, $"target:{Id(targetModel)}") : 0,
            });
            candidateNumeric.Add(new[]
            {
                action.GetDouble("effective_energy_cost") / 10.0,
                kind == "PlayCard" ? 1.0 : 0.0,
                kind == "UsePotion" ? 1.0 : 0.0,
                kind == "EndTurn" ? 1.0 : 0.0,
                targetId.Length > 0 ? 1.0 : 0.0,
                stats.GetDouble("damage") / 100.0,
                stats.GetDouble("block") / 100.0,
                action.GetBool("legal", true) ? 1.0 : 0.0,
            });
            actionIds.Add(action.GetString("action_id", ""));
        }
        return new Encoded(
            stateNumeric,
            weighted.Select(item => TokenId(vocabulary, item.Token)).ToArray(),
            weighted.Select(item => item.Weight).ToArray(),
            enemyIds.ToArray(), enemyNumeric.ToArray(), candidateIds.ToArray(), candidateNumeric.ToArray(), actionIds.ToArray());
    }

    private static double IntentDamage(JsonElement enemy) => enemy.GetArray("intents").Sum(intent =>
        intent.GetDouble("damage") * (intent.Has("hits") ? intent.GetDouble("hits", 1) : intent.GetDouble("times", 1)));
    private static int TokenId(IReadOnlyDictionary<string, int> vocabulary, string token) => vocabulary.TryGetValue(token, out var value) ? value : 1;
    private static string Id(string? value) => (value ?? "UNKNOWN").ToUpperInvariant();
    private static string CardToken(string? modelId, bool upgraded) => $"card:{Id(modelId)}:{(upgraded ? 1 : 0)}";

    private static void Compare(JsonElement expected, IReadOnlyList<int> actual)
    {
        var values = expected.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (!values.SequenceEqual(actual)) throw new InvalidOperationException("integer feature vector differs");
    }
    private static void Compare(JsonElement expected, IReadOnlyList<double> actual, ref double maximumError)
    {
        var values = expected.EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (values.Length != actual.Count) throw new InvalidOperationException("numeric feature length differs");
        for (var index = 0; index < values.Length; index++) maximumError = Math.Max(maximumError, Math.Abs(values[index] - actual[index]));
    }
    private static void CompareNested(JsonElement expected, IReadOnlyList<int[]> actual)
    {
        var rows = expected.EnumerateArray().ToArray();
        if (rows.Length != actual.Count) throw new InvalidOperationException("nested integer feature length differs");
        for (var index = 0; index < rows.Length; index++) Compare(rows[index], actual[index]);
    }
    private static void CompareNested(JsonElement expected, IReadOnlyList<double[]> actual, ref double maximumError)
    {
        var rows = expected.EnumerateArray().ToArray();
        if (rows.Length != actual.Count) throw new InvalidOperationException("nested numeric feature length differs");
        for (var index = 0; index < rows.Length; index++) Compare(rows[index], actual[index], ref maximumError);
    }

    private sealed record Encoded(double[] StateNumeric, int[] StateTokenIds, double[] StateTokenWeights,
        int[] EnemyTokenIds, double[][] EnemyNumeric, int[][] CandidateTokenIds, double[][] CandidateNumeric, string?[] ActionIds);
}

internal static class JsonFeatureExtensions
{
    public static bool Has(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);
    public static JsonElement GetObject(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Object ? result : default;
    public static IEnumerable<JsonElement> GetArray(this JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().ToArray() : [];
    public static string GetString(this JsonElement value, string name, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() ?? fallback : fallback;
    public static double GetDouble(this JsonElement value, string name, double fallback = 0) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Number ? result.GetDouble() : fallback;
    public static bool GetBool(this JsonElement value, string name, bool fallback = false) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) && result.ValueKind is JsonValueKind.True or JsonValueKind.False ? result.GetBoolean() : fallback;
}
