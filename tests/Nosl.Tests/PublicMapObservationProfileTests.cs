using System.Collections.Immutable;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class PublicMapObservationProfileTests
{
    private const string Canonical = PublicMapObservationProfiles.CoordinateOrderV1;
    private static NativeRunExecutionOptions Execution => new(MaxFloors: 8,
        OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
        PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version);
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, Execution = Execution,
        EligibleCombats = 1, EligibleDecisionsPerCombat = 1,
    };

    [Fact]
    public void ProfileIsExplicitAndVersionedWithoutChangingLegacySerialization()
    {
        Assert.DoesNotContain("publicMapObservationProfile", PublicJson.Serialize(new NativeRunExecutionOptions()));
        Assert.DoesNotContain("publicMapObservationProfile", PublicJson.Serialize(new NaturalSourceOptions()));
        var selected = Execution with { PublicMapObservationProfile = Canonical };
        Assert.Equal(Canonical, PublicJson.Read<NativeRunExecutionOptions>(PublicJson.Serialize(selected)).PublicMapObservationProfile);
        Assert.NotEqual(Prior.Identity, (Prior with { Execution = selected }).Freeze().Identity);
        Assert.NotEqual(new NativeRunPrior { Execution = Execution }.Identity,
            new NativeRunPrior { Execution = selected }.Freeze().Identity);
        Assert.Throws<ArgumentException>(() => (Prior with { Execution = Execution with
            { PublicMapObservationProfile = "invented" } }).Freeze());
        Assert.Throws<ArgumentException>(() => new NativeRunPrior { Execution = new(PublicMapObservationProfile: Canonical) }.Freeze());
        Assert.Throws<ArgumentException>(() => new NaturalSourceOptions(PublicMapObservationProfile: Canonical).EmitsPublicEvidence);
    }

    [Fact]
    public void ProjectionForgetsOnlyEnumerationAndDoesNotMutateNativeMapsOrPolicyActions()
    {
        var run = new RunState("coordinate-profile-native-fixture", ascensionLevel: 10);
        var current = run.Map.StartingMapPoint;
        var choices = current.Children.OrderBy(p => p.coord.col).ToArray();
        Assert.True(choices.Length > 1);
        string shape = NativeMapTravelConditionTests.MapShape(run.Map);
        string random = RandomState(run);
        var forward = NativePublicMapSlice.Observe(run.Map, current, choices);
        var reverse = NativePublicMapSlice.Observe(run.Map, current, choices.Reverse().ToArray());
        Assert.NotEqual(PublicJson.Serialize(forward), PublicJson.Serialize(reverse));
        Assert.Equal(PublicJson.Serialize(forward.Nodes), PublicJson.Serialize(reverse.Nodes));
        Assert.Equal(PublicJson.Serialize(forward.Edges), PublicJson.Serialize(reverse.Edges));
        foreach (var permutation in Permutations(choices))
        {
            Assert.Equal(PublicJson.Serialize(forward), PublicJson.Serialize(
                NativePublicMapSlice.Observe(run.Map, current, permutation, Canonical)));
            foreach (int hp in new[] { 1, 70 })
                Assert.Same(NativeSourceMapChoice.Choose(choices, hp, 70), NativeSourceMapChoice.Choose(permutation, hp, 70));
        }
        Assert.Equal(shape, NativeMapTravelConditionTests.MapShape(run.Map));
        Assert.Equal(random, RandomState(run));

        // Multi-row / nonordinary travel still keeps every coordinate, edge,
        // Unknown icon and connection flag, sorting by row before column.
        var first = choices[0]; var deeper = first.Children.First();
        deeper.PointType = MapPointType.Unknown;
        var raw = NativePublicMapSlice.Observe(run.Map, current, [deeper, first]);
        var canonical = NativePublicMapSlice.Observe(run.Map, current, [deeper, first], Canonical);
        Assert.Equal([new(first.coord.col, first.coord.row), new(deeper.coord.col, deeper.coord.row)],
            canonical.Options.Select(o => o.Coordinate));
        Assert.Equal(PublicJson.Serialize(raw.Nodes), PublicJson.Serialize(canonical.Nodes));
        Assert.Equal(PublicJson.Serialize(raw.Edges), PublicJson.Serialize(canonical.Edges));
        Assert.False(canonical.Options[1].IsOrdinaryConnection);
        Assert.Equal(PublicMapNodeType.Unknown, canonical.Nodes.Single(n => n.Coordinate == canonical.Options[1].Coordinate).NodeType);
    }

    [Fact]
    public void FiniteCoarseningSumsFineEventMassAndCappedRejectionKeepsItsConditionalLaw()
    {
        var run = new RunState("coordinate-profile-finite-law", ascensionLevel: 10);
        var current = run.Map.StartingMapPoint;
        var choices = current.Children.OrderBy(p => p.coord.col).Take(2).ToArray();
        Assert.Equal(2, choices.Length);
        // Six equally likely latent states. Fine event forward: hidden masses
        // 2:1. Coarsened event either order: 2:3. The sixth state differs in an
        // actually retained coordinate and must remain a miss.
        (int Hidden, MapPoint[] Choices)[] tickets = [(0, choices), (0, choices), (1, choices),
            (1, choices.Reverse().ToArray()), (1, choices.Reverse().ToArray()), (0, choices.Take(1).ToArray())];
        string target = PublicJson.Serialize(NativePublicMapSlice.Observe(run.Map, current, choices, Canonical));
        bool Match(int ticket, string? profile) => target == PublicJson.Serialize(
            NativePublicMapSlice.Observe(run.Map, current, tickets[ticket].Choices, profile));
        Assert.Equal([2, 1], Enumerable.Range(0, 6).Where(i => Match(i, null))
            .GroupBy(i => tickets[i].Hidden).OrderBy(g => g.Key).Select(g => g.Count()));
        Assert.Equal([2, 3], Enumerable.Range(0, 6).Where(i => Match(i, Canonical))
            .GroupBy(i => tickets[i].Hidden).OrderBy(g => g.Key).Select(g => g.Count()));
        int[] emitted = [0, 0]; int exhausted = 0;
        for (int first = 0; first < 6; first++)
        for (int second = 0; second < 6; second++)
        {
            int cursor = 0; int[] sequence = [first, second];
            try
            {
                var result = NativeComponentRejection.Sample("finite_map_coarsening", 2,
                    () => new NativeComponentTrial<int>(sequence[cursor++], [], 0), i => Match(i, Canonical));
                emitted[tickets[result.Value].Hidden]++;
            }
            catch (NativeComponentBudgetExceededException) { exhausted++; }
        }
        Assert.Equal([14, 21], emitted); Assert.Equal(1, exhausted);
        Assert.Equal(new ShuffleRational(2, 3), new ShuffleRational(emitted[0], emitted[1]));
        Assert.Equal(new ShuffleRational(6, 7), new ShuffleRational(2 * 36, 6 * emitted[0]));
        Assert.Equal(new ShuffleRational(6, 7), new ShuffleRational(3 * 36, 6 * emitted[1]));
    }

    [Fact]
    public async Task NativeSourceReplayAndRngStayIdenticalWhileDeclaredChannelRejectsUnsortedInputs()
    {
        var options = new NaturalSourceOptions(MaxFloors: 8, MaxRoots: 2, MaxRootsPerCombat: 1,
            SeedPrefix: "owned-native-opening", ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: Execution.OutsideCombatScript, PublicContextProfile: Execution.PublicContextProfile,
            PublicEvidenceProfile: Execution.PublicEvidenceProfile);
        var legacy = await NaturalSourceCollector.CollectAsync(options);
        var canonical = await NaturalSourceCollector.CollectAsync(options with { PublicMapObservationProfile = Canonical });
        Assert.Equal(PublicJson.Serialize(legacy.Runs), PublicJson.Serialize(canonical.Runs));
        Assert.Null(Assert.Single(canonical.Runs).Error); Assert.Equal(2, canonical.Roots.Length);
        var execution = Execution with { PublicMapObservationProfile = Canonical };
        bool checkedLaterMap = false;
        foreach (var pair in legacy.Roots.Zip(canonical.Roots))
        {
            var packet = pair.Second.PublicRoot;
            Assert.Equal(PublicJson.Serialize(Coarsen(pair.First.PublicRoot)), PublicJson.Serialize(packet));
            Assert.Equal(PublicJson.Serialize(pair.First.PublicRoot with { PublicEvidence = null }),
                PublicJson.Serialize(packet with { PublicEvidence = null }));
            Assert.Equal(Canonical, pair.Second.PublicMapObservationProfile);
            using var record = JsonDocument.Parse(PublicJson.Serialize(pair.Second.ToSourceRecord()));
            Assert.Equal(Canonical, record.RootElement.GetProperty("audit_only").GetProperty("public_map_observation_profile").GetString());
            Assert.DoesNotContain("public_map_observation_profile", PublicJson.Serialize(pair.First.ToSourceRecord()));
            PublicEvidenceInput.ValidateProfile(execution, packet);
            int slot = pair.First.SourceTrace.Count(t => t.Kind == "combat_action");
            await using var oldWorld = await NativeRunWorld.OpenAsync(Execution, "owned-native-opening:0", slot);
            await using var newWorld = await NativeRunWorld.OpenAsync(execution, "owned-native-opening:0", slot);
            Assert.NotNull(oldWorld); Assert.NotNull(newWorld);
            Assert.Equal(RandomState(oldWorld.NativeRun), RandomState(newWorld.NativeRun));
            Assert.Equal(NativeMapTravelConditionTests.MapShape(oldWorld.NativeRun.Map), NativeMapTravelConditionTests.MapShape(newWorld.NativeRun.Map));
            Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(newWorld.Observe()));
            var action = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId).Choose(packet);
            await oldWorld.StepAsync(action); await newWorld.StepAsync(action);
            Assert.Equal(RandomState(oldWorld.NativeRun), RandomState(newWorld.NativeRun));
            Assert.Equal(PublicJson.Serialize(Coarsen(oldWorld.Observe())), PublicJson.Serialize(newWorld.Observe()));

            var bad = ReplaceMaps(packet, map => map.Options.Length > 1
                ? new(map.Current, map.Nodes, map.Edges, map.Options.Reverse().ToImmutableArray()) : map);
            Assert.NotEqual(PublicJson.Serialize(packet), PublicJson.Serialize(bad));
            PublicEvidenceInput.ValidateProfile(Execution, bad); // Original channel still accepts native enumeration.
            Assert.Throws<ArgumentException>(() => PublicEvidenceInput.ValidateProfile(execution, bad));
            Assert.Throws<ArgumentException>(() => new NativeTapeReplaySource(bad, Prior with { Execution = execution }));
            Assert.False(NativePublicInitialMapCondition.TryCreate(bad.PublicEvidence, Prior with { Execution = execution }, out _, out var reason));
            Assert.Equal("coordinate_order_map_options_required", reason);
            var laterMap = packet.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicMapObserved>()
                .Skip(1).LastOrDefault();
            if (laterMap is not null)
            {
                // This source fixture has a one-option later slice. Add one
                // structurally valid public option solely for the hostile-input
                // regression, preserving every original chosen-coordinate link.
                var extra = new PublicMapCoordinate(laterMap.Nodes.Max(n => n.Coordinate.Col) + 1,
                    laterMap.Options[0].Coordinate.Row);
                var laterOptions = laterMap.Options.Add(new(extra, true)).OrderBy(o => o.Coordinate.Row)
                    .ThenBy(o => o.Coordinate.Col).Reverse().ToImmutableArray();
                var laterOnly = ReplaceMaps(packet, map => ReferenceEquals(map, laterMap)
                    ? new(map.Current, map.Nodes.Add(new(extra, PublicMapNodeType.Unknown)),
                        map.Edges.Add(new(map.Current!, extra)), laterOptions) : map);
                PublicEvidenceInput.ValidateProfile(Execution, laterOnly);
                Assert.True(PublicMapObservationProfiles.IsCoordinateOrdered(
                    laterOnly.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicMapObserved>().First()));
                Assert.Throws<ArgumentException>(() => PublicEvidenceInput.ValidateProfile(execution, laterOnly));
                Assert.Throws<ArgumentException>(() => new NativeTapeReplaySource(laterOnly, Prior with { Execution = execution }));
                checkedLaterMap = true;
            }
        }
        Assert.True(checkedLaterMap, "The fixture must exercise a noncanonical later map with a canonical initial map");
    }

    internal static DecisionPacket Coarsen(DecisionPacket packet) => ReplaceMaps(packet, map => new(map.Current,
        map.Nodes, map.Edges, map.Options.OrderBy(o => o.Coordinate.Row).ThenBy(o => o.Coordinate.Col).ToImmutableArray()));
    private static DecisionPacket ReplaceMaps(DecisionPacket packet, Func<PublicMapObserved, PublicMapObserved> project)
    {
        var evidence = packet.PublicEvidence!;
        return packet with { PublicEvidence = new(evidence.SchemaVersion, evidence.CompleteFromRunStart,
            evidence.Events.Select(e => e.Payload is PublicMapObserved map
                ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, project(map)) : e).ToImmutableArray()) };
    }
    private static string RandomState(RunState run) => JsonSerializer.Serialize(new
        { run = run.Rng.ToSerializable(), players = run.Players.Select(p => p.PlayerRng.ToSerializable()).ToArray() },
        new JsonSerializerOptions { IncludeFields = true });
    private static IEnumerable<MapPoint[]> Permutations(MapPoint[] points)
    {
        if (points.Length == 0) { yield return []; yield break; }
        foreach (var point in points)
        foreach (var suffix in Permutations(points.Where(p => p != point).ToArray()))
            yield return new[] { point }.Concat(suffix).ToArray();
    }
}
