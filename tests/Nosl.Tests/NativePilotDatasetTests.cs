using Nosl.Worker;

namespace Nosl.Tests;

public sealed class NativePilotDatasetTests
{
    private static readonly NativePilotCollection Collection = new("fresh-regression", new string('a', 64));

    [Fact]
    public async Task LiveCollectionCannotReuseInspectedPrototypeNamespaceOrAuthorizeFormalLabels()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NativePilotDataset.CollectAsync(
            new(MaxRoots: 1, SeedPrefix: "nosl-m5-natural-proof-20261001", SourceRunPrefix: "fresh-regression"), new(), Collection));
        await Assert.ThrowsAsync<ArgumentException>(() => NativePilotDataset.CollectAsync(
            new(MaxRoots: 1, SeedPrefix: "nosl-native-pilot/fresh-regression"), new(), Collection));
        await Assert.ThrowsAsync<ArgumentException>(() => NativePilotDataset.CollectAsync(
            new(MaxRoots: 1, SeedPrefix: "nosl-native-pilot/fresh-regression", SourceRunPrefix: "fresh-regression"), new() { FormalLabels = true }, Collection));
        await Assert.ThrowsAsync<ArgumentException>(() => NativePilotDataset.CollectAsync(
            new(MaxRoots: 201, SeedPrefix: "nosl-native-pilot/fresh-regression", SourceRunPrefix: "fresh-regression"), new(), Collection));
        await Assert.ThrowsAsync<ArgumentException>(() => NativePilotDataset.CollectAsync(
            new(MaxRoots: 1, SeedPrefix: "nosl-native-pilot/fresh-regression", SourceRunPrefix: "fresh-regression"), new(), Collection with { ProtectionRegistrySha256 = "missing" }));
    }

    [Fact]
    public void SourceAndBattleIdentityHavePortableLengthDelimitedStableContracts()
    {
        Assert.Equal("9580a553957b566cac42acb7fab2bc62aff139ade59cb3e7b61e3d114aca44af",
            NativePilotDataset.SourceIdentity("nosl-native-pilot/identity:0"));
        Assert.NotEqual(NativePilotDataset.SourceIdentity("a:1"), NativePilotDataset.SourceIdentity("a:11"));
        Assert.NotEqual(NativePilotDataset.BattleIdentity("seed", 1, 2, "same"), NativePilotDataset.BattleIdentity("seed", 1, 3, "same"));
    }
}
