using System.Text.Json;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativeProvenanceTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Fact]
    public void FiniteOracleEnumeration_HasIndependentPartitionsAndExactCloneResetAliases()
    {
        var joint = new Dictionary<(ulong, ulong, ulong, ulong), int>();
        int accepted = 0;
        for (ulong assignment = 0; assignment < 256; assignment++)
        {
            ulong a = assignment & 3, b = (assignment >> 2) & 3;
            ulong c = (assignment >> 4) & 3, d = (assignment >> 6) & 3;
            var rewards = new Dictionary<LabelRandomAddressV1, ulong>();
            var states = new Dictionary<LabelRandomState, ulong>();
            using var scope = LabelRandomScope.EnterRewardProvenance(address =>
            {
                if (!rewards.TryGetValue(address, out ulong value))
                    rewards.Add(address, value = address.InitialSeed == 42 ? (address.RawCursor == 0 ? a : b) : d);
                return value;
            }, state =>
            {
                if (!states.TryGetValue(state, out ulong value)) states.Add(state, value = c);
                return value;
            });
            var rng = new MegaRandom(42).WithLabelRewardsProvenance();
            var untagged = new MegaRandom(42);
            AssertStateEqual(Snapshot(rng), Snapshot(untagged));
            ulong first = rng.NextULong();
            MegaRandom clone = rng.Clone();
            ulong second = rng.NextULong();
            Assert.Equal(second, clone.NextULong());
            Assert.Equal(first, new MegaRandom(42).WithLabelRewardsProvenance().NextULong());
            rng.Reinitialise(42);
            Assert.Equal(first, rng.NextULong());
            ulong stateWord = untagged.NextULong();
            Assert.Equal(stateWord, new MegaRandom(42).NextULong());
            ulong otherSeed = new MegaRandom(43).WithLabelRewardsProvenance().NextULong();
            joint[(first, second, stateWord, otherSeed)] = joint.GetValueOrDefault((first, second, stateWord, otherSeed)) + 1;
            if (first < 2 && second == 2 && stateWord == 3) accepted++;
            Assert.Equal(3, rewards.Count);
            Assert.Single(states);
        }
        Assert.Equal(256, joint.Count);
        Assert.All(joint.Values, count => Assert.Equal(1, count));
        // Full two-option public evidence plus a state-partition observation has mass
        // (2/4)*(1/4)*(1/4)=1/32, with the other seed retaining all four values.
        Assert.Equal(8, accepted);
    }

    [Theory]
    [InlineData(0uL)]
    [InlineData(0x8000000000000000uL)]
    [InlineData(ulong.MaxValue)]
    public void PrimitiveConversionsAndNativeAdvancementRemainExact(ulong word)
    {
        var expected = new MegaRandom(91);
        var addresses = new List<LabelRandomAddressV1>();
        MegaRandom mega;
        Rng rng;
        using (LabelRandomScope.EnterRewardProvenance(address => { addresses.Add(address); return word; }, _ => 7))
        {
            mega = new MegaRandom(91).WithLabelRewardsProvenance();
            rng = new Rng(91).WithLabelRewardsProvenance();
            Assert.Equal((double)(word >> 11) * 1.1102230246251565E-16, mega.NextDouble());
            Assert.Equal((float)(word >> 40) * 5.9604645E-08f, mega.NextFloat());
            Assert.Equal((int)(word >> 33), mega.NextInt());
            Assert.Equal((uint)word, mega.NextUInt());
            Assert.Equal((word & (1UL << 63)) != 0, mega.NextBool());
            Assert.Equal(word, mega.NextULong());
            Assert.Equal((int)(((double)(word >> 11) * 1.1102230246251565E-16) * 10), rng.NextInt(10));
            Assert.Equal((float)((double)(word >> 11) * 1.1102230246251565E-16), rng.NextFloat());
            Assert.Equal(word, rng.NextUnsignedLong());
            Assert.Equal(3, rng.Counter);
            Assert.Equal(3UL, rng.LabelRewardAddress!.Value.RawCursor);
        }
        for (int i = 0; i < 6; i++) expected.NextULong();
        AssertStateEqual(Snapshot(expected), Snapshot(mega));
        Assert.Equal(new ulong[] { 0, 1, 2, 3, 4, 5, 0, 1, 2 }, addresses.Select(a => a.RawCursor));
        Assert.Equal(expected.NextULong(), mega.NextULong());
    }

    [Fact]
    public void RawCursorSurvivesCounterDivergenceCloneAndBothReseedOverloads()
    {
        using var scope = LabelRandomScope.EnterRewardProvenance(a => a.RawCursor + 100, _ => 9);
        var rng = new PlayerRngSet(7).Rewards;
        LabelRandomAddressV1 initial = rng.LabelRewardAddress!.Value;
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
        Assert.Equal(1, rng.Counter);
        Assert.Equal(initial, rng.LabelRewardAddress);
        Assert.Equal(100UL, rng.NextUnsignedLong());
        Assert.Equal(2, rng.Counter);
        Assert.Equal(1UL, rng.LabelRewardAddress!.Value.RawCursor);
        Rng clone = rng.CloneExact();
        Assert.Equal(rng.NextUnsignedLong(), clone.NextUnsignedLong());
        Assert.Equal(rng.LabelRewardAddress, clone.LabelRewardAddress);
        Rng reseeded = rng.CloneReseeded(19);
        Rng namedReseed = rng.CloneReseeded(19, "rewards");
        Assert.Equal(new LabelRandomAddressV1(LabelRandomProvenance.RewardsOrigin, 19, 0), reseeded.LabelRewardAddress);
        Assert.Equal(19 + StringHelper.GetDeterministicHashCode("rewards"), namedReseed.LabelRewardAddress!.Value.InitialSeed);
        Assert.Equal(0, reseeded.Counter);
        Assert.Equal(100UL, reseeded.NextUnsignedLong());
    }

    [Fact]
    public void JsonSnapshotsPreserveSourcePartitionAcrossGenericAndTypedRestores()
    {
        using var scope = LabelRandomScope.EnterRewardProvenance(a => a.InitialSeed + a.RawCursor, s => s.State0);
        var player = new PlayerRngSet(15);
        var run = new RunRngSet("hybrid-save");
        Assert.False(player.UsesSemanticKeys);
        Assert.False(run.UsesSemanticKeys);
        foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>()) player.GetRng(type).NextUnsignedLong();
        foreach (RunRngType type in Enum.GetValues<RunRngType>()) run.GetRng(type).NextUnsignedLong();
        SerializablePlayerRngSet playerSave = RoundTrip(player.ToSerializable());
        SerializableRunRngSet runSave = RoundTrip(run.ToSerializable());
        Assert.Equal(LabelRandomProvenance.RewardsPartition, playerSave.Rngs[PlayerRngType.Rewards].LabelProvenance!.Partition);
        Assert.Equal(LabelRandomProvenance.FullStatePartition, playerSave.Rngs[PlayerRngType.Shops].LabelProvenance!.Partition);
        Assert.All(runSave.Rngs.Values, s => Assert.Equal(LabelRandomProvenance.FullStatePartition, s.LabelProvenance!.Partition));
        PlayerRngSet restoredPlayer = PlayerRngSet.FromSerializable(playerSave);
        RunRngSet restoredRun = RunRngSet.FromSave(runSave);
        Assert.Equal(player.Rewards.NextUnsignedLong(), restoredPlayer.Rewards.NextUnsignedLong());
        Assert.Equal(run.Shuffle.NextUnsignedLong(), restoredRun.Shuffle.NextUnsignedLong());
        var destination = new Rng(999);
        destination.LoadFromSerializable(playerSave.Rngs[PlayerRngType.Rewards]);
        Assert.Equal(new Rng(playerSave.Rngs[PlayerRngType.Rewards]).NextUnsignedLong(), destination.NextUnsignedLong());
        // An explicit full-state source replaces the destination's Rewards partition.
        destination.LoadFromSerializable(playerSave.Rngs[PlayerRngType.Shops]);
        Assert.Null(destination.LabelRewardAddress);
        Assert.Equal(new Rng(playerSave.Rngs[PlayerRngType.Shops]).NextUnsignedLong(), destination.NextUnsignedLong());
        player.LoadFromSerializable(playerSave);
        run.LoadFromSerializable(runSave);
        Assert.Equal(JsonSerializer.Serialize(playerSave, Json), JsonSerializer.Serialize(player.ToSerializable(), Json));
        Assert.Equal(JsonSerializer.Serialize(runSave, Json), JsonSerializer.Serialize(run.ToSerializable(), Json));
    }

    [Fact]
    public void HybridRestoreRejectsMissingAndUnknownProvenanceWithoutStateMutation()
    {
        var legacy = new Rng(1).ToSerializable();
        using var scope = LabelRandomScope.EnterRewardProvenance(_ => 1, _ => 2);
        var player = new PlayerRngSet(3);
        var valid = player.ToSerializable();
        var snapshot = valid.Rngs[PlayerRngType.Rewards];
        string before = JsonSerializer.Serialize(player.Rewards.ToSerializable(), Json);
        Assert.Throws<InvalidOperationException>(() => new Rng(legacy));
        Assert.Throws<InvalidOperationException>(() => player.Rewards.LoadFromSerializable(legacy));
        Assert.Equal(before, JsonSerializer.Serialize(player.Rewards.ToSerializable(), Json));
        Assert.Throws<InvalidOperationException>(() => new Rng(snapshot with
        {
            LabelProvenance = snapshot.LabelProvenance! with { Law = "unreviewed-law" }
        }));
        valid.Rngs[PlayerRngType.Rewards] = snapshot with { LabelProvenance = null };
        Assert.Throws<InvalidOperationException>(() => PlayerRngSet.FromSerializable(valid));
        Assert.Throws<InvalidOperationException>(() => player.LoadFromSerializable(valid));
        valid.Rngs[PlayerRngType.Rewards] = snapshot with
        {
            LabelProvenance = new(LabelRandomProvenance.LawId, LabelRandomProvenance.FullStatePartition)
        };
        Assert.Null(PlayerRngSet.FromSerializable(valid).Rewards.LabelRewardAddress);
        var runSave = new RunRngSet("save").ToSerializable();
        runSave.Rngs.Remove(RunRngType.Shuffle);
        Assert.Throws<InvalidOperationException>(() => RunRngSet.FromSave(runSave));
        var loadedState = new MegaRandom(valid.Rngs[PlayerRngType.Rewards]);
        Assert.Throws<InvalidOperationException>(() => loadedState.WithLabelRewardsProvenance());
    }

    [Fact]
    public void LegacyScopeAndNormalSaveBytesRemainUnchangedAndNestedScopesKeepRawCursor()
    {
        var snapshot = new SerializableRng { counter = 5, state0 = 1, state1 = 2, state2 = 3, state3 = 4 };
        const string frozen = "{\"counter\":5,\"state0\":1,\"state1\":2,\"state2\":3,\"state3\":4}";
        Assert.Equal(frozen, JsonSerializer.Serialize(new Rng(snapshot).ToSerializable(), Json));
        Assert.Null(new PlayerRngSet(12).Rewards.LabelRewardAddress);
        using (LabelRandomScope.Enter(_ => 77))
        {
            Assert.Equal(77UL, new Rng(snapshot).NextUnsignedLong());
            Assert.Null(new PlayerRngSet(12).Rewards.ToSerializable().LabelProvenance);
        }
        using (LabelRandomScope.EnterRewardProvenance(a => a.RawCursor + 10, _ => 20))
        {
            var player = new PlayerRngSet(12);
            Assert.Equal(10UL, player.Rewards.NextUnsignedLong());
            using (LabelRandomScope.Enter(_ => 77))
            {
                Assert.Equal(77UL, player.Rewards.NextUnsignedLong());
                Assert.NotNull(new PlayerRngSet(13).Rewards.LabelRewardAddress);
            }
            Assert.Equal(12UL, player.Rewards.NextUnsignedLong());
            Assert.Equal(20UL, player.Shops.NextUnsignedLong());
        }
        Assert.Equal(frozen, JsonSerializer.Serialize(new Rng(snapshot).ToSerializable(), Json));
    }

    [Fact]
    public async Task CallbackSamplingDoesNotRecurseAndAsyncScopesRemainIndependent()
    {
        var sampler = new MegaRandom(19);
        var expected = new MegaRandom(19);
        ulong[] expectedWords = [expected.NextULong(), expected.NextULong(), expected.NextULong()];
        using (LabelRandomScope.EnterRewardProvenance(_ => sampler.NextULong(), _ => sampler.NextULong()))
        {
            Assert.Equal(expectedWords[0], new PlayerRngSet(1).Rewards.NextUnsignedLong());
            Assert.Equal(expectedWords[1], new Rng(1).NextUnsignedLong());
            await Task.WhenAll(Check(31), Check(37));
            Assert.Equal(expectedWords[2], new PlayerRngSet(1).Rewards.NextUnsignedLong());
        }
        async Task Check(ulong value)
        {
            using var nested = LabelRandomScope.EnterRewardProvenance(_ => value, _ => value + 1);
            var player = new PlayerRngSet(2);
            await Task.Yield();
            Assert.Equal(value, player.Rewards.NextUnsignedLong());
            Assert.Equal(value + 1, player.Shops.NextUnsignedLong());
        }
    }

    [Fact]
    public void EqualNumericStatesDoNotReplaceLineageAndDiagnosticsDoNotDefineIt()
    {
        var addresses = new List<LabelRandomAddressV1>();
        using var scope = LabelRandomScope.EnterRewardProvenance(a => { addresses.Add(a); return a.InitialSeed; }, _ => 0);
        var player = new PlayerRngSet(24);
        var snapshot = player.Rewards.ToSerializable();
        RngDrawObserver? previous = RngDiagnostics.DrawObserver;
        try
        {
            RngDiagnostics.DrawObserver = (_, _, _, _, _) => { };
            var withDiagnostics = new PlayerRngSet(24);
            Assert.Equal(player.Rewards.LabelRewardAddress, withDiagnostics.Rewards.LabelRewardAddress);
            Assert.Equal(player.Rewards.NextUnsignedLong(), withDiagnostics.Rewards.NextUnsignedLong());
        }
        finally { RngDiagnostics.DrawObserver = previous; }
        var changedLineage = snapshot with
        {
            LabelProvenance = snapshot.LabelProvenance! with
            {
                InitialSeed = snapshot.LabelProvenance.InitialSeed + 1,
                RawCursor = 7
            }
        };
        AssertStateEqual(snapshot, changedLineage);
        Assert.NotEqual(new Rng(snapshot).NextUnsignedLong(), new Rng(changedLineage).NextUnsignedLong());
        Assert.Equal(7UL, addresses[^1].RawCursor);
        var mega = new MegaRandom(snapshot);
        mega.Reinitialise(changedLineage);
        Assert.Equal(new MegaRandom(changedLineage).LabelRewardAddress, mega.LabelRewardAddress);
    }

    [Fact]
    public void SourcePartitionRoundTripsOutsideScopeIncludingTypedCrossPartitionLoads()
    {
        SerializablePlayerRngSet playerSave;
        SerializableRunRngSet runSave;
        var stateKeys = new List<LabelRandomState>();
        using (LabelRandomScope.EnterRewardProvenance(_ => 7, state => { stateKeys.Add(state); return 9; }))
        {
            var player = new PlayerRngSet(20);
            player.Rewards.NextUnsignedLong();
            playerSave = RoundTrip(player.ToSerializable());
            runSave = RoundTrip(new RunRngSet("source-partition").ToSerializable());
            Assert.Equal(new Rng(35, "event_content").NextUnsignedLong(),
                new Rng(35, "event_content").NextUnsignedLong());
            Assert.Equal(stateKeys[^1], stateKeys[^2]);
        }
        SerializableRng rewards = playerSave.Rngs[PlayerRngType.Rewards];
        SerializableRng fullState = playerSave.Rngs[PlayerRngType.Shops];
        playerSave.Rngs[PlayerRngType.Rewards] = fullState;
        playerSave.Rngs[PlayerRngType.Shops] = rewards;
        runSave.Rngs[RunRngType.Shuffle] = rewards;
        PlayerRngSet playerRestore = PlayerRngSet.FromSerializable(RoundTrip(playerSave));
        RunRngSet runRestore = RunRngSet.FromSave(RoundTrip(runSave));
        Assert.Null(playerRestore.Rewards.LabelRewardAddress);
        Assert.Equal(new Rng(rewards).LabelRewardAddress, playerRestore.Shops.LabelRewardAddress);
        Assert.Equal(playerRestore.Shops.LabelRewardAddress, runRestore.Shuffle.LabelRewardAddress);
        Assert.Equal(JsonSerializer.Serialize(playerSave, Json), JsonSerializer.Serialize(playerRestore.ToSerializable(), Json));
        Assert.Equal(new Rng(rewards).NextUnsignedLong(), runRestore.Shuffle.NextUnsignedLong());
        Assert.Throws<InvalidOperationException>(() => playerRestore.Shops.LoadFromSerializable(rewards with { LabelProvenance = null }));
        var mega = new MegaRandom(rewards with
        {
            LabelProvenance = rewards.LabelProvenance! with { RawCursor = ulong.MaxValue }
        });
        SerializableRng before = Snapshot(mega);
        Assert.Throws<OverflowException>(() => mega.NextULong());
        AssertStateEqual(before, Snapshot(mega));
    }

    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    private static SerializableRng Snapshot(MegaRandom rng)
    {
        var snapshot = new SerializableRng();
        rng.FillSerializableState(snapshot);
        return snapshot;
    }

    private static void AssertStateEqual(SerializableRng expected, SerializableRng actual)
    {
        Assert.Equal(expected.state0, actual.state0);
        Assert.Equal(expected.state1, actual.state1);
        Assert.Equal(expected.state2, actual.state2);
        Assert.Equal(expected.state3, actual.state3);
    }
}
