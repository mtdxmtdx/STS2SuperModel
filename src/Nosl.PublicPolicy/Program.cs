using Nosl.Contracts;
while (Console.ReadLine() is { } line)
{
    try { Console.WriteLine(PublicJson.Serialize(PublicDiagnosticPolicy.Choose(PublicJson.Read<DecisionPacket>(line)))); }
    catch (Exception e) { Console.WriteLine(PublicJson.Serialize(new { status = "invalid_public_input", message = e.Message })); }
}
