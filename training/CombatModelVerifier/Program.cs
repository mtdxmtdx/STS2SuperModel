using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

if (args.Length is < 2 or > 4)
{
    Console.Error.WriteLine("usage: CombatModelVerifier <model.onnx> <onnx-parity-fixture.json> [feature-parity-fixture.json] [inject-logit-perturbation]");
    return 2;
}

var modelPath = Path.GetFullPath(args[0]);
var fixturePath = Path.GetFullPath(args[1]);
var perturbation = args.Length == 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0.0;
using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
var root = document.RootElement;
var expectedHash = root.GetProperty("model_sha256").GetString();
var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)));
if (!StringComparer.OrdinalIgnoreCase.Equals(expectedHash, actualHash))
    throw new InvalidOperationException($"model hash mismatch: expected {expectedHash}, actual {actualHash}");

var inputValues = new List<NamedOnnxValue>();
foreach (var input in root.GetProperty("inputs").EnumerateObject())
{
    var shape = input.Value.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).ToArray();
    var values = input.Value.GetProperty("values");
    var dtype = input.Value.GetProperty("dtype").GetString();
    inputValues.Add(dtype switch
    {
        "float32" => NamedOnnxValue.CreateFromTensor(input.Name, new DenseTensor<float>(values.EnumerateArray().Select(value => value.GetSingle()).ToArray(), shape)),
        "int64" => NamedOnnxValue.CreateFromTensor(input.Name, new DenseTensor<long>(values.EnumerateArray().Select(value => value.GetInt64()).ToArray(), shape)),
        "bool" => NamedOnnxValue.CreateFromTensor(input.Name, new DenseTensor<bool>(values.EnumerateArray().Select(value => value.GetBoolean()).ToArray(), shape)),
        _ => throw new InvalidOperationException($"unsupported input dtype {dtype}")
    });
}

using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
using var session = new InferenceSession(modelPath, options);
using var results = session.Run(inputValues);
var referenceContainer = root.TryGetProperty("reference_outputs", out var referenceOutputs)
    ? referenceOutputs
    : root.GetProperty("outputs");
var maximumError = 0.0;
var actualByName = new Dictionary<string, double[]>(StringComparer.Ordinal);
foreach (var expected in referenceContainer.EnumerateObject())
{
    var actual = results.First(value => value.Name == expected.Name).AsTensor<float>().ToArray().Select(value => (double)value).ToArray();
    var wanted = expected.Value.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
    if (actual.Length != wanted.Length)
        throw new InvalidOperationException($"{expected.Name} length mismatch: {actual.Length} != {wanted.Length}");
    for (var index = 0; index < actual.Length; index++) maximumError = Math.Max(maximumError, Math.Abs(actual[index] - wanted[index]));
    actualByName[expected.Name] = actual;
}

var policyReferenceElement = referenceContainer.GetProperty("policy_logits");
var policyShape = policyReferenceElement.GetProperty("shape").EnumerateArray().Select(value => value.GetInt32()).ToArray();
var sampleCount = policyShape[0];
var actionCount = policyShape[1];
var referencePolicy = ToMatrix(policyReferenceElement.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray(), sampleCount, actionCount);
var actualPolicy = ToMatrix(actualByName["policy_logits"], sampleCount, actionCount);
var legalValues = root.GetProperty("inputs").GetProperty("legal_mask").GetProperty("values").EnumerateArray().Select(value => value.GetBoolean()).ToArray();
var legalMask = ToBoolMatrix(legalValues, sampleCount, actionCount);
var actionIds = root.GetProperty("action_ids").EnumerateArray()
    .Select(row => row.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()).ToArray();
int? injectedSample = null;
int? injectedAction = null;
if (perturbation != 0)
{
    for (var sample = 0; sample < sampleCount && injectedSample is null; sample++)
    {
        var ids = actionIds[sample];
        var legal = Enumerable.Range(0, actionCount).Select(index => legalMask[sample, index]).ToArray();
        var scores = Enumerable.Range(0, actionCount).Select(index => referencePolicy[sample, index]).ToArray();
        var paddedIds = ids.Concat(Enumerable.Range(ids.Length, actionCount - ids.Length).Select(index => $"<PAD:{index}>")).ToArray();
        var ranking = DecisionParity.CanonicalRanking(scores, paddedIds, legal);
        if (ranking.Length < 2 || scores[ranking[0]] - scores[ranking[1]] >= DecisionParity.NearTieThreshold) continue;
        actualPolicy[sample, ranking[1]] += perturbation;
        injectedSample = sample;
        injectedAction = ranking[1];
    }
    if (injectedSample is null) throw new InvalidOperationException("fixture contains no near-tie sample for perturbation");
    maximumError = Math.Max(maximumError, Math.Abs(perturbation));
}

var parity = DecisionParity.Compare(referencePolicy, actualPolicy, actionIds, legalMask);
var syntheticTiePass = VerifySyntheticTie(root.GetProperty("synthetic_tie_case"));
var legacyTolerance = root.GetProperty("legacy_absolute_tolerance").GetDouble();
var diagnosticThreshold = root.GetProperty("numeric_diagnostic_threshold").GetDouble();
var priorMaximumError = root.TryGetProperty("prior_reported_maximum_absolute_error", out var priorElement) &&
                        priorElement.ValueKind == JsonValueKind.Number ? priorElement.GetDouble() : (double?)null;
var measurementNote = root.TryGetProperty("measurement_difference_note", out var noteElement) &&
                      noteElement.ValueKind == JsonValueKind.String ? noteElement.GetString() : null;
var featureParity = args.Length >= 3 ? FeatureParity.Verify(Path.GetFullPath(args[2])) : null;
var verdict = parity.Passed && syntheticTiePass ? "pass" : "fail";
Console.WriteLine(JsonSerializer.Serialize(new
{
    verdict,
    runtime = "Microsoft.ML.OnnxRuntime",
    sample_count = parity.SampleCount,
    top1_agreement_rate = parity.Top1AgreementRate,
    top3_set_agreement_rate = parity.Top3SetAgreementRate,
    ranking_agreement = parity.RankingAgreement,
    tie_break_deterministic = parity.TieBreakDeterministic,
    synthetic_tie_case_pass = syntheticTiePass,
    near_tie_sample_count = parity.NearTieSampleCount,
    tie_sample_count = parity.TieSampleCount,
    discordant_pairs = parity.DiscordantPairs,
    compared_pairs = parity.ComparedPairs,
    maximum_absolute_error = maximumError,
    tolerance = legacyTolerance,
    legacy_absolute_tolerance = legacyTolerance,
    legacy_absolute_verdict = maximumError <= legacyTolerance ? "pass" : "fail",
    numeric_diagnostic_threshold = diagnosticThreshold,
    numeric_diagnostic_status = maximumError > diagnosticThreshold ? "warning" : "normal",
    prior_reported_maximum_absolute_error = priorMaximumError,
    measurement_difference_note = measurementNote,
    tie_tolerance = DecisionParity.TieTolerance,
    tie_break_rule = "ActionId Ordinal ascending within anchor-relative logit groups whose anchor delta is < 1e-4",
    injected_logit_perturbation = perturbation,
    injected_sample_index = injectedSample,
    injected_action_index = injectedAction,
    model_sha256 = actualHash,
    feature_parity = featureParity,
}));
return verdict == "pass" ? 0 : 1;

static double[,] ToMatrix(double[] values, int rows, int columns)
{
    var result = new double[rows, columns];
    for (var row = 0; row < rows; row++)
    for (var column = 0; column < columns; column++)
        result[row, column] = values[row * columns + column];
    return result;
}

static bool[,] ToBoolMatrix(bool[] values, int rows, int columns)
{
    var result = new bool[rows, columns];
    for (var row = 0; row < rows; row++)
    for (var column = 0; column < columns; column++)
        result[row, column] = values[row * columns + column];
    return result;
}

static bool VerifySyntheticTie(JsonElement value)
{
    var reference = value.GetProperty("reference_logits").EnumerateArray().Select(item => item.GetDouble()).ToArray();
    var candidate = value.GetProperty("candidate_logits").EnumerateArray().Select(item => item.GetDouble()).ToArray();
    var ids = value.GetProperty("action_ids").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    var legal = value.GetProperty("legal_mask").EnumerateArray().Select(item => item.GetBoolean()).ToArray();
    var expected = value.GetProperty("expected_order").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    var referenceOrder = DecisionParity.CanonicalRanking(reference, ids, legal).Select(index => ids[index]);
    var candidateOrder = DecisionParity.CanonicalRanking(candidate, ids, legal).Select(index => ids[index]);
    return referenceOrder.SequenceEqual(expected) && candidateOrder.SequenceEqual(expected);
}
