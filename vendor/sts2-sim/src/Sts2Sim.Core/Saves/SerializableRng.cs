using System.Text.Json.Serialization;
using Sts2Sim.Core.Random;

namespace Sts2Sim.Core.Saves;

public record SerializableRng
{
    public int counter;
    public ulong state0;
    public ulong state1;
    public ulong state2;
    public ulong state3;

    /// <summary>Present only for the explicitly opted-in hypothetical provenance law.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LabelRandomProvenance? LabelProvenance { get; set; }
}
