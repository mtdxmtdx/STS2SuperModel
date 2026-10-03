using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;

namespace Nosl.Tests;

public sealed class NativeMapProvenanceTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    [Fact]
    public void FiniteLawSeparatesMapActRewardsAndStateWithExactRecreationAliases()
    {
        var joint = new Dictionary<(ulong Map, ulong LaterAct, ulong Rewards, ulong State, ulong OtherSeed), int>();
        // Deliberately choose different run seeds whose two named Map seeds collide.
        ulong seed0 = 91;
        ulong seed1 = unchecked(seed0 + StringHelper.GetDeterministicHashCode("act_1_map")
            - StringHelper.GetDeterministicHashCode("act_2_map"));
        ulong firstEffectiveSeed = unchecked(seed0 + StringHelper.GetDeterministicHashCode("act_1_map"));
        for (ulong assignment = 0; assignment < 1024; assignment++)
        {
            using var scope = LabelRandomScope.EnterMapRewardProvenance(
                a => (assignment >> (a.ActIndex == 1 ? 2 : a.InitialSeed == firstEffectiveSeed ? 0 : 8)) & 3,
                _ => (assignment >> 4) & 3,
                _ => (assignment >> 6) & 3);
            Rng first = StandardActMap.CreateRng(seed0, 0);
            Rng later = StandardActMap.CreateRng(seed1, 1);
            ulong effectiveSeed = first.LabelMapAddress!.Value.InitialSeed;
            Assert.Equal(effectiveSeed, later.LabelMapAddress!.Value.InitialSeed);
            Assert.NotEqual(first.LabelMapAddress, later.LabelMapAddress);
            var other = new Rng(effectiveSeed);
            var rewards = new MegaRandom(effectiveSeed).WithLabelRewardsProvenance();
            AssertStateEqual(first.ToSerializable(), other.ToSerializable());
            var clone = first.CloneExact();
            ulong map = first.NextUnsignedLong();
            Assert.Equal(map, clone.NextUnsignedLong());
            Assert.Equal(map, StandardActMap.CreateRng(seed0, 0).NextUnsignedLong());
            var cell = (map, later.NextUnsignedLong(), rewards.NextULong(), other.NextUnsignedLong(),
                StandardActMap.CreateRng(seed0 + 1, 0).NextUnsignedLong());
            joint[cell] = joint.GetValueOrDefault(cell) + 1;
        }
        Assert.Equal(1024, joint.Count);
        Assert.All(joint.Values, count => Assert.Equal(1, count));
        // Conditioning on any one Map outcome leaves every other partition/act value.
        Assert.Equal(256, joint.Keys.Count(k => k.Map == 2));
    }

    [Fact]
    public void OldStateAndRewardsLawsRetainTheMapCollisionCounterexample()
    {
        for (int mode = 0; mode < 2; mode++)
        {
            using var scope = mode == 0 ? LabelRandomScope.Enter(s => s.State0)
                : LabelRandomScope.EnterRewardProvenance(_ => 77, s => s.State0);
            Rng map = StandardActMap.CreateRng(91, 0);
            var other = new Rng(unchecked(91UL + StringHelper.GetDeterministicHashCode("act_1_map")));
            Assert.Null(map.LabelMapAddress);
            AssertStateEqual(map.ToSerializable(), other.ToSerializable());
            Assert.Equal(map.NextUnsignedLong(), other.NextUnsignedLong());
        }
    }

    [Fact]
    public void RawCursorCloneReseedAndRestorePreserveActLineageAndRejectLostMarkers()
    {
        SerializableRng ordinary = StandardActMap.CreateRng(11, 0).ToSerializable();
        SerializableRng oldHybrid;
        using (LabelRandomScope.EnterRewardProvenance(_ => 1, _ => 2))
            oldHybrid = StandardActMap.CreateRng(11, 0).ToSerializable();
        SerializableRng saved;
        Rng map;
        using (LabelRandomScope.EnterMapRewardProvenance(a => a.RawCursor + 100, _ => 200, _ => 300))
        {
            map = StandardActMap.CreateRng(11, 2);
            Assert.Throws<ArgumentOutOfRangeException>(() => map.NextInt(0));
            Assert.Equal(1, map.Counter);
            Assert.Equal(0UL, map.LabelMapAddress!.Value.RawCursor);
            Assert.Equal(100UL, map.NextUnsignedLong());
            Assert.Equal(2, map.Counter);
            Assert.Equal(1UL, map.LabelMapAddress!.Value.RawCursor);
            saved = JsonSerializer.Deserialize<SerializableRng>(JsonSerializer.Serialize(map.ToSerializable(), Json), Json)!;
            Assert.Equal(map.LabelMapAddress, new Rng(saved).LabelMapAddress);
            Rng clone = map.CloneExact();
            Assert.Equal(map.NextUnsignedLong(), clone.NextUnsignedLong());
            Rng reseeded = map.CloneReseeded(13);
            Rng namedReseed = map.CloneReseeded(13, "again");
            Assert.Equal(new LabelMapRandomAddressV1(2, 13, 0), reseeded.LabelMapAddress);
            Assert.Equal(new LabelMapRandomAddressV1(2, unchecked(13UL + StringHelper.GetDeterministicHashCode("again")), 0), namedReseed.LabelMapAddress);
            Assert.Equal(0, reseeded.Counter);
            Assert.Equal(100UL, reseeded.NextUnsignedLong());
            using (LabelRandomScope.Enter(_ => 888))
            {
                Assert.Equal(888UL, map.NextUnsignedLong());
                Assert.NotNull(StandardActMap.CreateRng(12, 1).LabelMapAddress);
            }
            Assert.Equal(103UL, map.NextUnsignedLong());
            Assert.Throws<InvalidOperationException>(() => new Rng(ordinary));
            Assert.Throws<InvalidOperationException>(() => new Rng(oldHybrid));
            string before = JsonSerializer.Serialize(map.ToSerializable(), Json);
            foreach (SerializableRng invalid in new[]
            {
                saved with { LabelProvenance = null },
                saved with { LabelProvenance = saved.LabelProvenance! with { ActIndex = null } },
                saved with { LabelProvenance = saved.LabelProvenance! with { ActIndex = -1 } },
                saved with { LabelProvenance = saved.LabelProvenance! with { Law = LabelRandomProvenance.LawId } },
                saved with { LabelProvenance = saved.LabelProvenance! with { OriginFamily = LabelRandomProvenance.RewardsOrigin } },
            })
                Assert.Throws<InvalidOperationException>(() => map.LoadFromSerializable(invalid));
            Assert.Equal(before, JsonSerializer.Serialize(map.ToSerializable(), Json));
            map.LoadFromSerializable(saved);
            Assert.Equal(101UL, map.NextUnsignedLong());
        }
        Assert.Equal(saved.LabelProvenance, new Rng(saved).ToSerializable().LabelProvenance);
        Assert.Equal(2, map.CloneReseeded(17).LabelMapAddress!.Value.ActIndex);
        Assert.Throws<InvalidOperationException>(() => map.LoadFromSerializable(ordinary));
        var exhausted = new Rng(saved with { LabelProvenance = saved.LabelProvenance! with { RawCursor = ulong.MaxValue } });
        var beforeOverflow = exhausted.ToSerializable();
        Assert.Throws<OverflowException>(() => exhausted.NextUnsignedLong());
        AssertStateEqual(beforeOverflow, exhausted.ToSerializable());
    }

    [Fact]
    public void NativeMapTraceAndConversionsMatchOrdinaryExecutionAndBothActZeroKernels()
    {
        const ulong seed = 71;
        var nativeRng = StandardActMap.CreateRng(seed, 0);
        var native = StandardActMap.CreateFor(new Overgrowth(), nativeRng, new AscensionManager(0));
        var replayWords = new Rng(seed, "act_1_map");
        var addresses = new List<LabelMapRandomAddressV1>();
        using var scope = LabelRandomScope.EnterMapRewardProvenance(a =>
        {
            addresses.Add(a);
            return replayWords.NextUnsignedLong(); // Callback sampling must not recurse.
        }, _ => throw new InvalidOperationException("No Rewards draw in map generation"),
            _ => throw new InvalidOperationException("No full-state draw in map generation"));
        Rng mapRng = StandardActMap.CreateRng(seed, 0);
        var hybrid = StandardActMap.CreateFor(new Overgrowth(), mapRng, new AscensionManager(0));
        Assert.Equal(SemanticMap(native), SemanticMap(hybrid));
        Assert.Equal(nativeRng.Counter, mapRng.Counter);
        AssertStateEqual(nativeRng.ToSerializable(), mapRng.ToSerializable());
        Assert.Equal(Enumerable.Range(0, addresses.Count).Select(i => (ulong)i), addresses.Select(a => a.RawCursor));
        Assert.All(addresses, a => Assert.Equal(0, a.ActIndex));
        Assert.Equal((ulong)addresses.Count, mapRng.LabelMapAddress!.Value.RawCursor);
        int firstCount = addresses.Count;
        replayWords = new Rng(seed, "act_1_map");
        var alternative = StandardActMap.CreateFor(new Underdocks(), StandardActMap.CreateRng(seed, 0), new AscensionManager(0));
        Assert.Equal(SemanticMap(native), SemanticMap(alternative));
        Assert.Equal(addresses.Take(firstCount), addresses.Skip(firstCount));
    }

    [Fact]
    public void NestedAmbientLawsCannotLaunderAnExistingRestoreOrReseedLineage()
    {
        SerializableRng rewardsSave, mapSave;
        using (LabelRandomScope.EnterRewardProvenance(_ => 1, _ => 2))
            rewardsSave = new PlayerRngSet(3).Rewards.ToSerializable();
        using (LabelRandomScope.EnterMapRewardProvenance(_ => 3, _ => 4, _ => 5))
            mapSave = StandardActMap.CreateRng(3, 0).ToSerializable();
        Check(mapSave, rewardsSave, () => LabelRandomScope.EnterRewardProvenance(_ => 1, _ => 2));
        Check(rewardsSave, mapSave, () => LabelRandomScope.EnterMapRewardProvenance(_ => 3, _ => 4, _ => 5));

        static void Check(SerializableRng existingSave, SerializableRng ambientSave, Func<IDisposable> enter)
        {
            var existing = new Rng(existingSave);
            var mega = new MegaRandom(existingSave);
            using var scope = enter();
            Assert.Throws<InvalidOperationException>(() => existing.LoadFromSerializable(ambientSave));
            Assert.Throws<InvalidOperationException>(() => existing.CloneReseeded(7));
            Assert.Throws<InvalidOperationException>(() => existing.CloneReseeded(7, "again"));
            Assert.Throws<InvalidOperationException>(() => mega.Reinitialise(7));
            Assert.Equal(JsonSerializer.Serialize(existingSave, Json), JsonSerializer.Serialize(existing.ToSerializable(), Json));
            var after = new SerializableRng();
            mega.FillSerializableState(after);
            AssertStateEqual(existingSave, after);
            Assert.Equal(existingSave.LabelProvenance, after.LabelProvenance);
        }
    }

    [Fact]
    public void MapConstructionBindsOnlyItsNativeSourceAndRunsBeforePlayers()
    {
        var oracle = new NativeMapOracle(97);
        int calls = 0, mapWords = 0, stateWords = 0;
        using var scope = LabelRandomScope.EnterMapRewardProvenance(a => { mapWords++; return oracle.Word(a); },
            _ => 0, _ => { stateWords++; return 19; }, beginMapGeneration: context =>
            {
                calls++;
                Assert.Empty(context.Run.Players);
                Assert.Equal(0UL, context.Rng.LabelMapAddress!.Value.RawCursor);
                Assert.Equal(0, context.Rng.LabelMapAddress.Value.ActIndex);
                // Incidental sources in this callback are not dynamically tagged.
                Assert.Null(new Rng(context.Run.Rng.Seed, "act_1_map").LabelMapAddress);
                return null;
            });
        var run = new RunState("map-constructor-fixture", new Overgrowth());
        Assert.Equal(1, calls);
        Assert.True(mapWords > 0);
        Assert.Equal(0, stateWords);
        Assert.NotNull(run.Map);
        Assert.Null(new Rng(run.Rng.Seed, "act_1_map").LabelMapAddress);
        Assert.Equal(19UL, new Rng(run.Rng.Seed, "act_1_map").NextUnsignedLong());
        Assert.Equal(1, stateWords);
    }

    [Fact]
    public void ExplicitHybridScopesCannotMasqueradeAsEachOthersLawWhenNested()
    {
        using (LabelRandomScope.EnterRewardProvenance(_ => 11, _ => 12))
        {
            Assert.Throws<InvalidOperationException>(() =>
                LabelRandomScope.EnterMapRewardProvenance(_ => 21, _ => 22, _ => 23));
            Assert.Null(StandardActMap.CreateRng(5, 0).LabelMapAddress);
            Assert.Equal(12UL, StandardActMap.CreateRng(5, 0).NextUnsignedLong());
        }
        using (LabelRandomScope.EnterMapRewardProvenance(_ => 21, _ => 22, _ => 23))
        {
            Assert.Throws<InvalidOperationException>(() => LabelRandomScope.EnterRewardProvenance(_ => 11, _ => 12));
            using (LabelRandomScope.Enter(_ => 99))
            {
                Assert.Throws<InvalidOperationException>(() => LabelRandomScope.EnterRewardProvenance(_ => 11, _ => 12));
                Assert.NotNull(StandardActMap.CreateRng(5, 0).LabelMapAddress);
                Assert.Equal(99UL, StandardActMap.CreateRng(5, 0).NextUnsignedLong());
            }
            using (LabelRandomScope.EnterMapRewardProvenance(_ => 31, _ => 32, _ => 33))
                Assert.Equal(31UL, StandardActMap.CreateRng(5, 0).NextUnsignedLong());
            Assert.Equal(21UL, StandardActMap.CreateRng(5, 0).NextUnsignedLong());
        }
    }

    [Fact]
    public void RelabelingMapOriginsKeepsTheActZeroSemanticKernelForHeldSeedAndAct()
    {
        // A coupling of ideal IID tapes by raw cursor, not a claim about SHA seeds.
        // Every origin receives the same sequence here to check that only the words,
        // not the numerical native state or the held seed, enter the map kernel.
        var words = new List<ulong>();
        var sampler = new Rng(5588);
        string? expected = null;
        int expectedDraws = 0;
        using var scope = LabelRandomScope.EnterMapRewardProvenance(a =>
        {
            while ((ulong)words.Count <= a.RawCursor) words.Add(sampler.NextUnsignedLong());
            return words[(int)a.RawCursor];
        }, _ => throw new InvalidOperationException("Unexpected Rewards draw"),
            _ => throw new InvalidOperationException("Unexpected state draw"));
        foreach (ulong seed in new[] { 0UL, 938UL, ulong.MaxValue })
        foreach (bool alternateAct in new[] { false, true })
        {
            var rng = StandardActMap.CreateRng(seed, 0);
            var act = alternateAct ? (Sts2Sim.Core.Content.ActDefinition)new Underdocks() : new Overgrowth();
            var map = StandardActMap.CreateFor(act, rng, new AscensionManager(0));
            expected ??= SemanticMap(map);
            expectedDraws = expectedDraws == 0 ? rng.Counter : expectedDraws;
            Assert.Equal(expected, SemanticMap(map));
            Assert.Equal(expectedDraws, rng.Counter);
        }
    }

    [Fact]
    public void MapOracleReplaysOverridesAndDoesNotChangeTheOldRewardsPartition()
    {
        var map = new NativeMapOracle(41);
        var address = new LabelMapRandomAddressV1(0, 72, 3);
        map.ForceFresh(address, 8);
        Assert.False(map.WasVisited(address));
        Assert.Equal(8UL, map.Word(address));
        Assert.True(map.WasVisited(address));
        Assert.Throws<InvalidOperationException>(() => map.ForceFresh(address, 8));
        var copy = map.ReplayCopy();
        copy.ForceFresh(address, 8);
        Assert.Throws<InvalidOperationException>(() => copy.ForceFresh(address, 9));
        Assert.Equal(8UL, copy.Word(address));
        Assert.Equal(map.Word(address with { RawCursor = 4 }), copy.Word(address with { RawCursor = 4 }));
        Assert.Equal(2, copy.DistinctCells);
        Assert.Equal(1, copy.ConditionedCells);
        Assert.Throws<InvalidOperationException>(() => map.Word(address with { ActIndex = -1 }));
        var rewards = new NativeRewardsOracle(41);
        var oldRewards = new NativeRewardsOracle(41);
        using var scope = LabelRandomScope.EnterMapRewardProvenance(map.Word, rewards.Word, _ => 0);
        var rng = new PlayerRngSet(72).Rewards;
        var rewardAddress = rng.LabelRewardAddress!.Value;
        Assert.Equal(oldRewards.Word(rewardAddress), rng.NextUnsignedLong());
        Assert.Null(rng.LabelMapAddress);
    }

    [Fact]
    public void NewLawIsExplicitAndRequiresCompletePublicMapChannel()
    {
        var old = new NativeTapePrior { SchemaVersion = NativeTapePrior.RewardsVersion,
            Execution = new(PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version) };
        var map = old with { SchemaVersion = NativeTapePrior.MapVersion };
        Assert.True(old.UsesRewardsProvenance);
        Assert.False(old.UsesMapProvenance);
        Assert.True(map.UsesRewardsProvenance);
        Assert.True(map.UsesMapProvenance);
        Assert.NotEqual(old.PrimitiveLaw, map.PrimitiveLaw);
        Assert.NotEqual(old.Identity, map.Identity);
        Assert.Throws<ArgumentException>(() => map.Freeze());
        Assert.DoesNotContain("usesMapProvenance", PublicJson.Serialize(map));
        var complete = map with { Execution = map.Execution with
        {
            PublicMapObservationProfile = PublicMapObservationProfiles.CompleteGraphV1,
            PublicEvidenceProfile = PublicRunEvidence.CompleteMapVersion,
        } };
        Assert.True(complete.Freeze().UsesMapProvenance);
        foreach (string prior in new[] { NativeTapePrior.Version, NativeTapePrior.RewardsVersion })
            Assert.Throws<ArgumentException>(() => (complete with { SchemaVersion = prior }).Freeze());
        Assert.Throws<ArgumentException>(() => (complete with { Execution = complete.Execution with
            { PublicEvidenceProfile = PublicRunEvidence.Version } }).Freeze());
        Assert.Throws<ArgumentException>(() => (complete with { Execution = complete.Execution with
            { PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1 } }).Freeze());
        Assert.Throws<ArgumentException>(() => (complete with { Execution = complete.Execution with
            { PublicContextProfile = null } }).Freeze());
    }

    private static string SemanticMap(ActMap map) => string.Join(";", map.GetAllMapPoints()
        .Append(map.StartingMapPoint).Append(map.BossMapPoint)
        .Distinct().OrderBy(p => p.coord.row).ThenBy(p => p.coord.col)
        .Select(p => $"{p.coord.row},{p.coord.col}:{p.PointType}:" + string.Join(",", p.Children
            .OrderBy(c => c.coord.row).ThenBy(c => c.coord.col).Select(c => $"{c.coord.row}/{c.coord.col}"))));

    private static void AssertStateEqual(SerializableRng expected, SerializableRng actual)
    {
        Assert.Equal(expected.state0, actual.state0); Assert.Equal(expected.state1, actual.state1);
        Assert.Equal(expected.state2, actual.state2); Assert.Equal(expected.state3, actual.state3);
    }
}
