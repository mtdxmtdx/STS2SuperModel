using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Headless;

class Program
{
    private const string CliVersion = "0.2.0";
    private const string SupportedGameVersion = "v0.111.0";
    private const string SupportedGameCommit = "41cef1ea";
    private const string SupportedAssemblyHash = "0861BFA1DF347538D932F22D580E75420F08082792EB914E53B4882764ACDBE9";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static TraceSession? _trace;

    /// <summary>
    /// Locate the directory containing sts2.dll: STS2_LIB env, walk up from BaseDirectory, then BaseDirectory/lib.
    /// </summary>
    private static string ResolveLibDirectory()
    {
        var envLib = Environment.GetEnvironmentVariable("STS2_LIB");
        if (!string.IsNullOrWhiteSpace(envLib))
        {
            var p = Path.GetFullPath(envLib.Trim());
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "sts2.dll")))
                return p;
        }

        var dir = AppContext.BaseDirectory;
        for (var depth = 0; depth < 16 && !string.IsNullOrEmpty(dir); depth++)
        {
            var candidate = Path.Combine(dir, "lib");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "sts2.dll")))
                return Path.GetFullPath(candidate);
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lib"));
    }

    static void Main(string[] args)
    {
        // Prevent unhandled exceptions from crashing the process
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[FATAL] Unhandled: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[WARN] Unobserved task exception: {e.Exception?.Message}");
            e.SetObserved();
        };

        var libDir = ResolveLibDirectory();

        var compatibility = InspectCompatibility(libDir);
        WriteLine(new Dictionary<string, object?>
        {
            ["type"] = "ready",
            ["version"] = CliVersion,
            ["game_version"] = compatibility.GameVersion,
            ["game_commit"] = compatibility.GameCommit,
            ["assembly_sha256"] = compatibility.AssemblySha256,
            ["compatible"] = compatibility.IsCompatible,
            ["compatibility_error"] = compatibility.Error,
        });
        if (!compatibility.IsCompatible)
            return;

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(libDir, name.Name + ".dll");
            if (File.Exists(path))
                return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));

            // Also check game directory (via STS2_GAME_DIR env var)
            var gameDir = Environment.GetEnvironmentVariable("STS2_GAME_DIR") ?? "";
            if (!string.IsNullOrEmpty(gameDir))
            {
                path = Path.Combine(gameDir, name.Name + ".dll");
                if (File.Exists(path))
                    return ctx.LoadFromAssemblyPath(path);
            }

            return null;
        };

        var sim = new RunSimulator();

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            Dictionary<string, object?>? result;
            try
            {
                var cmd = JsonSerializer.Deserialize<JsonElement>(line);
                var commandName = cmd.TryGetProperty("cmd", out var commandElement)
                    ? commandElement.GetString() ?? string.Empty
                    : string.Empty;
                var rngBefore = _trace is not null && commandName == "action"
                    ? sim.GetTraceRngCounters()
                    : null;
                var normalizedActionId = commandName == "action" ? sim.NormalizeTraceAction(cmd) : null;
                result = HandleCommand(sim, cmd);
                if (commandName == "start_run")
                    _trace = TraceSession.Create(cmd);
                if (_trace is not null)
                {
                    var rngAfter = commandName == "action" ? sim.GetTraceRngCounters() : null;
                    result = _trace.Attach(cmd, result ?? new Dictionary<string, object?>
                    {
                        ["type"] = "error",
                        ["message"] = "command returned no response",
                    }, rngBefore, rngAfter, normalizedActionId);
                }
            }
            catch (JsonException ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Invalid JSON: {ex.Message}" };
            }
            catch (Exception ex)
            {
                result = new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"{ex.GetType().Name}: {ex.Message}" };
            }

            if (result != null)
            {
                WriteLine(result);
                if (result.TryGetValue("type", out var resultTypeObj) &&
                    string.Equals(resultTypeObj as string, "quit_result", StringComparison.Ordinal))
                {
                    _trace?.Flush();
                    _trace?.Dispose();
                    _trace = null;
                    break;
                }
            }
        }
    }

    private static CompatibilityInfo InspectCompatibility(string libDir)
    {
        var assemblyPath = Path.Combine(libDir, "sts2.dll");
        if (!File.Exists(assemblyPath))
            return new CompatibilityInfo(null, null, null, false, $"Missing sts2.dll under {libDir}");

        var originalPath = assemblyPath + ".original";
        var hashPath = File.Exists(originalPath) ? originalPath : assemblyPath;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hashPath)));
        string? gameVersion = null;
        string? gameCommit = null;
        var releaseInfoPath = Path.Combine(libDir, "release_info.json");
        if (File.Exists(releaseInfoPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(releaseInfoPath));
                var root = doc.RootElement;
                gameVersion = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                gameCommit = root.TryGetProperty("commit", out var c) ? c.GetString() : null;
            }
            catch (JsonException) { }
        }

        var errors = new List<string>();
        if (!string.Equals(gameVersion, SupportedGameVersion, StringComparison.OrdinalIgnoreCase))
            errors.Add($"expected game version {SupportedGameVersion}, got {gameVersion ?? "unknown"}");
        if (!string.Equals(gameCommit, SupportedGameCommit, StringComparison.OrdinalIgnoreCase))
            errors.Add($"expected game commit {SupportedGameCommit}, got {gameCommit ?? "unknown"}");
        if (!string.Equals(hash, SupportedAssemblyHash, StringComparison.OrdinalIgnoreCase))
            errors.Add($"expected sts2.dll SHA-256 {SupportedAssemblyHash}, got {hash}");
        return new CompatibilityInfo(gameVersion, gameCommit, hash, errors.Count == 0,
            errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private sealed record CompatibilityInfo(
        string? GameVersion,
        string? GameCommit,
        string? AssemblySha256,
        bool IsCompatible,
        string? Error);

    static Dictionary<string, object?>? HandleCommand(RunSimulator sim, JsonElement cmd)
    {
        var cmdType = cmd.GetProperty("cmd").GetString() ?? "";
        switch (cmdType)
        {
            case "start_run":
                return sim.StartRun(
                    cmd.TryGetProperty("character", out var ch) ? ch.GetString() ?? "Ironclad" : "Ironclad",
                    cmd.TryGetProperty("ascension", out var asc) ? asc.GetInt32() : 0,
                    cmd.TryGetProperty("seed", out var s) ? s.GetString() : null,
                    cmd.TryGetProperty("lang", out var lang) ? lang.GetString() ?? "en" : "en"
                );

            case "action":
            {
                var action = cmd.GetProperty("action").GetString() ?? "";
                Dictionary<string, object?>? actionArgs = null;
                if (cmd.TryGetProperty("args", out var argsElem))
                {
                    actionArgs = new Dictionary<string, object?>();
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        actionArgs[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Number => prop.Value.GetInt32(),
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString(),
                        };
                    }
                }
                return sim.ExecuteAction(action, actionArgs);
            }

            case "load_save":
            {
                var savePath = cmd.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                var saveJson = cmd.TryGetProperty("json", out var sj) ? sj.GetString() : null;
                if (saveJson == null && savePath != null)
                {
                    if (!File.Exists(savePath))
                        return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Save file not found: {savePath}" };
                    saveJson = File.ReadAllText(savePath);
                }
                if (saveJson == null)
                    return new Dictionary<string, object?> { ["type"] = "error", ["message"] = "Provide 'path' or 'json' for load_save" };
                var loadLang = cmd.TryGetProperty("lang", out var le) ? (le.GetString() ?? "en") : "en";
                return sim.LoadSave(saveJson, loadLang);
            }
            case "get_map":
                return sim.GetFullMap();

            case "get_combat_snapshot":
                return sim.GetCombatSnapshot(
                    cmd.TryGetProperty("view", out var view) ? view.GetString() ?? "public" : "public");

            case "set_player":
            {
                var args = new Dictionary<string, JsonElement>();
                foreach (var prop in cmd.EnumerateObject())
                    if (prop.Name != "cmd") args[prop.Name] = prop.Value;
                return sim.SetPlayer(args);
            }

            case "enter_room":
            {
                var roomType = cmd.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";
                var encounter = cmd.TryGetProperty("encounter", out var enc) ? enc.GetString() : null;
                var eventId = cmd.TryGetProperty("event", out var ev) ? ev.GetString() : null;
                return sim.EnterRoom(roomType, encounter, eventId);
            }

            case "set_draw_order":
            {
                var cards = new List<string>();
                if (cmd.TryGetProperty("cards", out var cardsArr))
                    foreach (var c in cardsArr.EnumerateArray())
                        cards.Add(c.GetString() ?? "");
                return sim.SetDrawOrder(cards);
            }

            case "write_continue_save":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                return sim.SaveCheckpoint(outputPath);
            }

            case "quit":
            {
                var outputPath = cmd.TryGetProperty("path", out var op) ? op.GetString() : null;
                if (!string.IsNullOrEmpty(outputPath))
                {
                    var saveResult = sim.SaveCheckpoint(outputPath);
                    bool saveOk = saveResult.TryGetValue("success", out var sObj) && sObj is bool b && b;
                    if (!saveOk)
                    {
                        // Save failed — do NOT clean up so the caller can retry with a different path.
                        return new Dictionary<string, object?>
                        {
                            ["type"] = "save_error",
                            ["save"] = saveResult,
                        };
                    }
                    sim.CleanUp();
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "quit_result",
                        ["success"] = true,
                        ["save"] = saveResult,
                    };
                }
                sim.CleanUp();
                return new Dictionary<string, object?>
                {
                    ["type"] = "quit_result",
                    ["success"] = true,
                    ["save"] = null,
                };
            }

            default:
                return new Dictionary<string, object?> { ["type"] = "error", ["message"] = $"Unknown command: {cmdType}" };
        }
    }

    static void WriteLine(Dictionary<string, object?> data)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(data, JsonOpts));
        Console.Out.Flush();
    }

    private sealed class TraceSession : IDisposable
    {
        private readonly string _traceId;
        private readonly string? _path;
        private readonly StreamWriter? _writer;
        private int _step;
        private string _lastHash = string.Empty;

        private TraceSession(string traceId, string? path)
        {
            _traceId = traceId;
            _path = path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                _writer = new StreamWriter(path, append: true) { AutoFlush = true };
            }
        }

        public static TraceSession Create(JsonElement command)
        {
            var seed = command.TryGetProperty("seed", out var seedElement) ? seedElement.GetString() : null;
            var traceId = $"trace-v0111-{seed ?? Guid.NewGuid().ToString("N")}";
            return new TraceSession(traceId, Environment.GetEnvironmentVariable("STS2_TRACE_PATH"));
        }

        public Dictionary<string, object?> Attach(
            JsonElement command,
            Dictionary<string, object?> result,
            object? rngBefore,
            object? rngAfter,
            string? normalizedActionId = null)
        {
            var postHash = RunSimulator.StableHash(result);
            normalizedActionId ??= NormalizeAction(command);
            var chanceBranch = BuildChanceBranch(rngBefore, rngAfter, postHash);
            var status = result.GetValueOrDefault("type") is "error" or "save_error" ? "failed" : "ok";
            var traceRecord = new Dictionary<string, object?>
            {
                ["trace_id"] = _traceId,
                ["trace_schema"] = 1,
                ["game_version"] = SupportedGameVersion,
                ["game_commit"] = SupportedGameCommit,
                ["assembly_sha256"] = SupportedAssemblyHash,
                ["cli_protocol_version"] = CliVersion,
                ["simulator_version"] = "cli-v0111-headless",
                ["scorer_version"] = "not-applicable",
                ["semantic_database_version"] = "game-runtime-v0111",
                ["feature_schema_version"] = "1",
                ["model_version"] = "none",
                ["step"] = _step,
                ["decision"] = result.TryGetValue("decision", out var decision) ? decision : result.GetValueOrDefault("type"),
                ["pre_state_hash"] = string.IsNullOrEmpty(_lastHash) ? null : _lastHash,
                ["post_state_hash"] = postHash,
                ["normalized_action_id"] = normalizedActionId,
                ["rng_before"] = rngBefore,
                ["rng_after"] = rngAfter,
                ["produced_chance_branch"] = chanceBranch["produced"],
                ["chance_branch"] = chanceBranch,
                ["status"] = status,
            };
            if (status == "failed")
            {
                traceRecord["failure"] = new Dictionary<string, object?>
                {
                    ["message"] = result.GetValueOrDefault("message")?.ToString() ?? "unknown failure",
                    ["failed_step"] = _step,
                    ["recovery_required"] = true,
                    ["recovery_status"] = "not_attempted",
                    ["prior_trace_hash"] = string.IsNullOrEmpty(_lastHash) ? null : _lastHash,
                };
            }
            if (result.TryGetValue("public_observation", out var publicObservation))
                traceRecord["public_observation"] = publicObservation;
            else if (result.TryGetValue("decision", out var resultDecision) &&
                     string.Equals(resultDecision as string, "combat_play", StringComparison.Ordinal))
                traceRecord["public_observation"] = new Dictionary<string, object?>(result);
            if (result.TryGetValue("teacher_snapshot", out var teacherSnapshot))
                traceRecord["teacher_snapshot"] = teacherSnapshot;
            if (result.TryGetValue("observation_view", out var observationView))
                traceRecord["observation_view"] = observationView;
            result["trace_id"] = _traceId;
            result["trace_schema"] = 1;
            result["trace_step"] = _step;
            result["pre_state_hash"] = traceRecord["pre_state_hash"];
            result["post_state_hash"] = postHash;
            result["normalized_action_id"] = normalizedActionId;
            result["trace_status"] = traceRecord["status"];
            result["trace_record"] = traceRecord;
            WriteTrace(traceRecord);
            _lastHash = postHash;
            _step++;
            return result;
        }

        private static Dictionary<string, object?> BuildChanceBranch(object? beforeValue, object? afterValue, string outcomeHash)
        {
            var before = ReadCounters(beforeValue);
            var after = ReadCounters(afterValue);
            var deltas = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var key in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var delta = after.GetValueOrDefault(key) - before.GetValueOrDefault(key);
                if (delta != 0) deltas[key] = delta;
            }
            var produced = deltas.Count > 0;
            return new Dictionary<string, object?>
            {
                ["produced"] = produced,
                ["kind"] = produced ? "realized_rng_consumption" : "none",
                ["rng_counters_available"] = beforeValue is not null && afterValue is not null,
                ["rng_deltas"] = deltas,
                ["streams_changed"] = deltas.Keys.ToArray(),
                ["realized_outcome_hash"] = outcomeHash,
                ["probability_known"] = false,
                ["probability"] = null,
                ["branch_enumerated"] = false,
            };
        }

        private static Dictionary<string, long> ReadCounters(object? value)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (value is null) return result;
            try
            {
                var element = JsonSerializer.SerializeToElement(value);
                if (element.ValueKind != JsonValueKind.Object) return result;
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.TryGetInt64(out var counter))
                        result[property.Name] = counter;
                }
            }
            catch { }
            return result;
        }

        public void Flush() => _writer?.Flush();

        private void WriteTrace(Dictionary<string, object?> record)
        {
            if (_writer is null) return;
            _writer.WriteLine(JsonSerializer.Serialize(record, JsonOpts));
            _writer.Flush();
        }

        private static string NormalizeAction(JsonElement command)
        {
            var cmd = command.TryGetProperty("cmd", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            var action = command.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
            if (!command.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
                return string.IsNullOrEmpty(action) ? cmd : $"{cmd}:{action}";
            var fields = args.EnumerateObject()
                .OrderBy(static p => p.Name, StringComparer.Ordinal)
                .Select(static p => $"{p.Name}={p.Value}");
            return $"{cmd}:{action}:{string.Join(',', fields)}";
        }

        public void Dispose() => _writer?.Dispose();
    }
}
