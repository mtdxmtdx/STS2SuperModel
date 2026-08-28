using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using STS2BestChoice.Core.Model;

namespace STS2BestChoice.Core.Search;

public interface IExpectimaxProblem<TState, TAction>
    where TState : notnull
    where TAction : notnull
{
    IEnumerable<TAction> Actions(TState state);
    IEnumerable<ChanceBranch<TState>> Apply(TState state, TAction action);
    bool IsTerminal(TState state);
    decimal Evaluate(TState state);
    string Key(TState state);
}

public sealed record ExpectimaxOptions(
    TimeSpan Budget,
    int MaximumDepth = 8,
    int MaximumChanceBranches = 32)
{
    public static ExpectimaxOptions Default { get; } = new(TimeSpan.FromMilliseconds(500));
}

public sealed record ExpectimaxResult<TAction>(
    decimal Value,
    ImmutableArray<TAction> PrincipalVariation,
    long ExpandedNodes,
    bool IsComplete,
    TimeSpan Elapsed,
    PredictionConfidence Confidence = PredictionConfidence.Reliable,
    ImmutableArray<ChanceSamplingStatistics> ChanceSampling = default);

public sealed record ChanceSamplingStatistics(
    string StateKey,
    string ActionLabel,
    int Depth,
    int BranchCount,
    int SampleCount,
    ulong SamplingSeed,
    decimal ProbabilityMassCovered,
    decimal Mean,
    decimal Variance,
    decimal ConfidenceInterval95Lower,
    decimal ConfidenceInterval95Upper,
    bool ProbabilityModelKnown,
    ImmutableArray<string> SampledBranchLabels);

/// <summary>
/// Pure max/chance search. Game-specific state, RNG and effect mirrors stay outside this class.
/// </summary>
public sealed class ExpectimaxEngine<TState, TAction>
    where TState : notnull
    where TAction : notnull
{
    private readonly IExpectimaxProblem<TState, TAction> _problem;
    private readonly ExpectimaxOptions _options;
    private readonly Dictionary<(string Key, int Depth), NodeValue<TAction>> _cache = new();
    private readonly Stopwatch _clock = new();
    private long _expanded;
    private bool _complete = true;
    private PredictionConfidence _confidence = PredictionConfidence.Reliable;
    private readonly List<ChanceSamplingStatistics> _sampling = new();

    public ExpectimaxEngine(IExpectimaxProblem<TState, TAction> problem, ExpectimaxOptions? options = null)
    {
        _problem = problem;
        _options = options ?? ExpectimaxOptions.Default;
    }

    public ExpectimaxResult<TAction> Search(TState initialState)
    {
        if (_options.Budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(_options.Budget));
        _cache.Clear();
        _expanded = 0;
        _complete = true;
        _confidence = PredictionConfidence.Reliable;
        _sampling.Clear();
        _clock.Restart();
        var value = EvaluateNode(initialState, _options.MaximumDepth);
        _clock.Stop();
        return new ExpectimaxResult<TAction>(
            value.Value,
            value.Actions,
            _expanded,
            _complete,
            _clock.Elapsed,
            _confidence,
            _sampling.ToImmutableArray());
    }

    private NodeValue<TAction> EvaluateNode(TState state, int depth)
    {
        _expanded++;
        if (_clock.Elapsed >= _options.Budget)
        {
            _complete = false;
            return new NodeValue<TAction>(_problem.Evaluate(state), ImmutableArray<TAction>.Empty);
        }

        var key = (_problem.Key(state), depth);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (depth <= 0 || _problem.IsTerminal(state))
        {
            var terminal = new NodeValue<TAction>(_problem.Evaluate(state), ImmutableArray<TAction>.Empty);
            _cache[key] = terminal;
            return terminal;
        }

        var best = default(NodeValue<TAction>?);
        foreach (var action in _problem.Actions(state))
        {
            var allBranches = _problem.Apply(state, action).ToArray();
            var branches = allBranches;
            if (branches.Length == 0) continue;
            var actionLabel = action.ToString() ?? typeof(TAction).Name;
            var probabilityModelKnown = branches.All(static branch => branch.ProbabilityKnown);
            if (branches.Any(static branch => branch.Probability < 0m))
            {
                _confidence = PredictionConfidence.Uncalculable;
                _complete = false;
                continue;
            }
            if (!probabilityModelKnown)
            {
                _confidence = PredictionConfidence.Estimated;
            }
            if (branches.Length > _options.MaximumChanceBranches)
            {
                _complete = false;
                _confidence = PredictionConfidence.Estimated;
                var totalMass = branches.Sum(static branch => Math.Max(0m, branch.Probability));
                if (totalMass <= 0m)
                {
                    _confidence = PredictionConfidence.Uncalculable;
                    continue;
                }
                if (totalMass < 0.999999m || totalMass > 1.000001m)
                    _confidence = PredictionConfidence.Estimated;

                var sampleCount = Math.Max(1, _options.MaximumChanceBranches);
                var seed = StableSeed(_problem.Key(state), actionLabel, depth);
                branches = StratifiedSample(branches, sampleCount, totalMass, seed);
                if (branches.Length == 0) continue;
                var selectedMass = branches
                    .Select(static branch => branch.Label)
                    .ToHashSet(StringComparer.Ordinal)
                    .Select(label => allBranches.First(branch => branch.Label == label))
                    .Sum(static branch => Math.Max(0m, branch.Probability));
                var values = new List<decimal>(branches.Length);
                var bestSampleValue = decimal.MinValue;
                var bestSampleActions = ImmutableArray<TAction>.Empty;
                foreach (var branch in branches)
                {
                    var child = EvaluateNode(branch.State, depth - 1);
                    values.Add(child.Value);
                    if (child.Value > bestSampleValue)
                    {
                        bestSampleValue = child.Value;
                        bestSampleActions = child.Actions;
                    }
                }

                var mean = values.Count == 0 ? 0m : values.Average();
                var expectedSample = mean;
                var variance = values.Count <= 1
                    ? 0m
                    : values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1);
                var margin = values.Count == 0
                    ? 0m
                    : 1.96m * (decimal)Math.Sqrt((double)variance / values.Count);
                _sampling.Add(new ChanceSamplingStatistics(
                    _problem.Key(state),
                    actionLabel,
                    depth,
                    allBranches.Length,
                    branches.Length,
                    seed,
                    selectedMass,
                    mean,
                    variance,
                    mean - margin,
                    mean + margin,
                    probabilityModelKnown,
                    branches.Select(static branch => branch.Label).ToImmutableArray()));
                var sampledCandidate = new NodeValue<TAction>(
                    expectedSample,
                    bestSampleActions.Insert(0, action));
                if (best is null || sampledCandidate.Value > best.Value) best = sampledCandidate;
                if (!_complete && _clock.Elapsed >= _options.Budget) break;
                continue;
            }

            var probabilityTotal = branches.Sum(static branch => branch.Probability);
            if (probabilityTotal <= 0m)
            {
                _confidence = PredictionConfidence.Uncalculable;
                _complete = false;
                continue;
            }
            if (probabilityTotal < 0.999999m || probabilityTotal > 1.000001m)
                _confidence = PredictionConfidence.Estimated;
            var expected = 0m;
            var bestChildActions = ImmutableArray<TAction>.Empty;
            var bestChildValue = decimal.MinValue;
            foreach (var branch in branches)
            {
                var child = EvaluateNode(branch.State, depth - 1);
                expected += branch.Probability / probabilityTotal * child.Value;
                if (child.Value > bestChildValue)
                {
                    bestChildValue = child.Value;
                    bestChildActions = child.Actions;
                }
            }

            var candidate = new NodeValue<TAction>(expected, bestChildActions.Insert(0, action));
            if (best is null || candidate.Value > best.Value) best = candidate;
            if (!_complete && _clock.Elapsed >= _options.Budget) break;
        }

        var result = best ?? new NodeValue<TAction>(_problem.Evaluate(state), ImmutableArray<TAction>.Empty);
        _cache[key] = result;
        return result;
    }

    private static ulong StableSeed(string stateKey, string actionLabel, int depth)
    {
        var payload = Encoding.UTF8.GetBytes($"{stateKey}\u001f{actionLabel}\u001f{depth}");
        var digest = SHA256.HashData(payload);
        return BitConverter.ToUInt64(digest, 0);
    }

    private static ChanceBranch<TState>[] StratifiedSample(
        ChanceBranch<TState>[] branches,
        int sampleCount,
        decimal totalMass,
        ulong seed)
    {
        var cumulative = new decimal[branches.Length];
        var running = 0m;
        for (var index = 0; index < branches.Length; index++)
        {
            running += Math.Max(0m, branches[index].Probability);
            cumulative[index] = running;
        }

        var result = new ChanceBranch<TState>[sampleCount];
        var rng = seed;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var jitter = NextUnit(ref rng);
            var target = totalMass * (sample + (decimal)jitter) / sampleCount;
            var index = Array.BinarySearch(cumulative, target);
            if (index < 0) index = ~index;
            if (index >= branches.Length) index = branches.Length - 1;
            result[sample] = branches[index];
        }
        return result;
    }

    private static double NextUnit(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    private sealed record NodeValue<T>(decimal Value, ImmutableArray<T> Actions);
}
