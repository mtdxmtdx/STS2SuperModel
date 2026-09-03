using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("usage: CombatModelVerifier <model.onnx> <onnx-parity-fixture.json> [feature-parity-fixture.json]");
    return 2;
}

var modelPath = Path.GetFullPath(args[0]);
var fixturePath = Path.GetFullPath(args[1]);
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

using var session = new InferenceSession(modelPath);
using var results = session.Run(inputValues);
var tolerance = root.GetProperty("tolerance").GetDouble();
var maximumError = 0.0;
foreach (var expected in root.GetProperty("outputs").EnumerateObject())
{
    var actual = results.First(value => value.Name == expected.Name).AsTensor<float>().ToArray();
    var wanted = expected.Value.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
    if (actual.Length != wanted.Length)
        throw new InvalidOperationException($"{expected.Name} length mismatch: {actual.Length} != {wanted.Length}");
    for (var index = 0; index < actual.Length; index++)
        maximumError = Math.Max(maximumError, Math.Abs(actual[index] - wanted[index]));
}

var verdict = maximumError <= tolerance ? "pass" : "fail";
var featureParity = args.Length == 3 ? FeatureParity.Verify(Path.GetFullPath(args[2])) : null;
Console.WriteLine(JsonSerializer.Serialize(new
{
    verdict,
    runtime = "Microsoft.ML.OnnxRuntime",
    maximum_absolute_error = maximumError,
    tolerance,
    model_sha256 = actualHash,
    feature_parity = featureParity,
}));
return verdict == "pass" ? 0 : 1;
