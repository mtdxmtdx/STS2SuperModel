using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class PublicCompleteMapObservationTests
{
    private const string Profile = PublicMapObservationProfiles.CompleteGraphV1;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeCaptureIncludesSpecialNodesUnknownIconsAndAllOrdinaryPaths(bool overgrowth)
    {
        ActDefinition act = overgrowth ? new Overgrowth() : new Underdocks();
        var rng = new Rng(89102, "public-map-capture-test");
        var map = StandardActMap.CreateFor(act, rng, new AscensionManager(10));
        var beforeRng = rng.ToSerializable();
        string shape = NativeMapTravelConditionTests.MapShape(map);
        var capture = NativePublicCurrentMapCapture.Observe(map, 0);
        Assert.Equal(PublicMapCaptureStatus.Complete, capture.Status);
        Assert.Equal(map.GetAllMapPoints().Count() + 2, capture.Nodes.Length);
        Assert.Equal(new(3, 0), capture.StartingNode);
        Assert.Equal(new(3, 16), Assert.Single(capture.BossNodes));
        Assert.Equal(PublicMapNodeType.Ancient, capture.Nodes[0].NodeType);
        Assert.Equal(PublicMapNodeType.Boss, capture.Nodes[^1].NodeType);
        Assert.Equal(map.GetAllMapPoints().Count(p => p.PointType == MapPointType.Unknown),
            capture.Nodes.Count(n => n.NodeType == PublicMapNodeType.Unknown));
        Assert.True(capture.Nodes.Any(n => n.NodeType == PublicMapNodeType.Unknown));
        var points = map.GetAllMapPoints().Append(map.StartingMapPoint).Append(map.BossMapPoint).ToArray();
        Assert.Equal(points.Sum(p => p.Children.Count), capture.Edges.Length);
        Assert.All(points, p => Assert.All(p.Children, child => Assert.Contains(capture.Edges,
            edge => edge.From == new PublicMapCoordinate(p.coord.col, p.coord.row)
                && edge.To == new PublicMapCoordinate(child.coord.col, child.coord.row))));
        Assert.Equal(beforeRng, rng.ToSerializable());
        Assert.Equal(shape, NativeMapTravelConditionTests.MapShape(map));

        // Reorder native hash sets and alter private mutability bookkeeping.
        // Neither changes a displayed icon, a path, or the projection's bytes.
        foreach (var p in points)
        {
            var reversed = p.Children.Reverse().ToArray();
            p.Children.Clear(); foreach (var child in reversed) p.Children.Add(child);
            p.CanBeModified = !p.CanBeModified;
        }
        string expected = PublicJson.Serialize(capture);
        Assert.Equal(expected, PublicJson.Serialize(NativePublicCurrentMapCapture.Observe(map, 0)));
        using var wire = JsonDocument.Parse(expected);
        Assert.Equal(["status", "nodes", "edges", "startingNode", "bossNodes"],
            wire.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.All(wire.RootElement.GetProperty("nodes").EnumerateArray(), node =>
            Assert.Equal(["coordinate", "nodeType"], node.EnumerateObject().Select(p => p.Name)));
        Assert.All(wire.RootElement.GetProperty("edges").EnumerateArray(), edge =>
            Assert.Equal(["from", "to"], edge.EnumerateObject().Select(p => p.Name)));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void UnsupportedNativeViewIsExplicitlyMissing(int actIndex, bool secondBoss)
    {
        var map = StandardActMap.CreateFor(new Overgrowth(), new Rng(41, "missing-capture-test"),
            new AscensionManager(10), secondBoss);
        var missing = NativePublicCurrentMapCapture.Observe(map, actIndex);
        Assert.Equal(PublicMapCaptureStatus.Missing, missing.Status);
        Assert.Empty(missing.Nodes); Assert.Empty(missing.Edges);
        Assert.Null(missing.StartingNode); Assert.Empty(missing.BossNodes);
        var slice = NativePublicMapSlice.Observe(map, map.StartingMapPoint,
            map.StartingMapPoint.Children.ToArray(), Profile, missing);
        PublicMapObservationProfiles.ValidateEvidence(Profile, Evidence(slice));
        Assert.Equal(PublicMapCaptureStatus.Missing, NativePublicMapSlice.Observe(map,
            map.StartingMapPoint, map.StartingMapPoint.Children.ToArray(), Profile).CurrentMap!.Status);
    }

    [Fact]
    public void CompleteCaptureRejectsPartialNoncanonicalAndConflictingGraphClaims()
    {
        var capture = SmallGraph();
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Missing,
            capture.Nodes, [], null, []));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes, capture.Edges, null, capture.BossNodes));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes, capture.Edges, capture.StartingNode, []));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes.Reverse().ToImmutableArray(), capture.Edges, capture.StartingNode, capture.BossNodes));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes, capture.Edges.Reverse().ToImmutableArray(), capture.StartingNode, capture.BossNodes));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes, capture.Edges.RemoveAt(0), capture.StartingNode, capture.BossNodes));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes.Add(capture.Nodes[0]), capture.Edges, capture.StartingNode, capture.BossNodes));
        Assert.Throws<ArgumentException>(() => new PublicCurrentMapCapture(PublicMapCaptureStatus.Complete,
            capture.Nodes, capture.Edges.Add(new(new(0, 1), new(6, 2))), capture.StartingNode, capture.BossNodes));
        Assert.Throws<ArgumentException>(() => new PublicMapObserved(new(0, 0),
            [capture.Nodes[0], new(new(0, 1), PublicMapNodeType.Elite)], [capture.Edges[0]], [new(new(0, 1), true)], capture));
        Assert.Throws<ArgumentException>(() => new PublicMapObserved(new(0, 0),
            capture.Nodes.Take(2).ToImmutableArray(), [], [new(new(0, 1), false)], capture));
    }

    [Theory]
    [InlineData("self_edge")]
    [InlineData("unobserved_endpoint")]
    [InlineData("unsupported_icon")]
    public void NativeCaptureCannotAttestAnIncompleteOrMalformedView(string mutation)
    {
        var map = StandardActMap.CreateFor(new Overgrowth(), new Rng(81, "malformed-capture-test"), new AscensionManager(10));
        var point = map.GetPointsInRow(1).First();
        if (mutation == "self_edge") point.AddChildPoint(point);
        else if (mutation == "unobserved_endpoint") point.AddChildPoint(new(0, 99) { PointType = MapPointType.Unknown });
        else point.PointType = MapPointType.Unassigned;
        Assert.Equal(PublicMapCaptureStatus.Missing, NativePublicCurrentMapCapture.Observe(map, 0).Status);
    }

    [Fact]
    public void ProfileFieldAndStrictCapturePresenceAreEnforcedWithoutChangingLegacyBytes()
    {
        var capture = SmallGraph();
        var legacy = new PublicMapObserved(new(0, 0), capture.Nodes.Take(2).ToImmutableArray(),
            [capture.Edges[0]], [new(new(0, 1), true)]);
        const string legacyBytes = "{\"current\":{\"col\":0,\"row\":0},\"nodes\":[{\"coordinate\":{\"col\":0,\"row\":0},\"nodeType\":9},{\"coordinate\":{\"col\":0,\"row\":1},\"nodeType\":1}],\"edges\":[{\"from\":{\"col\":0,\"row\":0},\"to\":{\"col\":0,\"row\":1}}],\"options\":[{\"coordinate\":{\"col\":0,\"row\":1},\"isOrdinaryConnection\":true}]}";
        Assert.Equal(legacyBytes, PublicJson.Serialize(legacy));
        Assert.Equal(legacyBytes, PublicJson.Serialize(PublicJson.Read<PublicMapObserved>(legacyBytes)));
        Assert.DoesNotContain("currentMap", PublicRunEvidenceJson.Serialize(Evidence(legacy)));
        var complete = new PublicMapObserved(legacy.Current, legacy.Nodes, legacy.Edges, legacy.Options, capture);
        var evidence = Evidence(complete);
        PublicMapObservationProfiles.ValidateEvidence(Profile, evidence);
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateEvidence(null, evidence));
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateEvidence(PublicMapObservationProfiles.CoordinateOrderV1, evidence));
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateEvidence(Profile, Evidence(legacy)));
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateChannel(Profile, null, null));
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateChannel(Profile,
            PublicRunEvidence.Version, PublicRunContext.Version));
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateChannel(null,
            PublicRunEvidence.CompleteMapVersion, PublicRunContext.Version));
        Assert.Throws<ArgumentException>(() => new PublicRunEvidence(PublicRunEvidence.Version, false, evidence.Events));
        Assert.Throws<ArgumentException>(() => new PublicRunEvidence(PublicRunEvidence.CompleteMapVersion, false, Evidence(legacy).Events));
        var nullField = JsonNode.Parse(PublicRunEvidenceJson.Serialize(Evidence(legacy)))!;
        nullField["events"]![2]!["payload"]!["currentMap"] = null;
        Assert.Throws<JsonException>(() => PublicRunEvidenceJson.Read(nullField.ToJsonString()));
        var backwards = new PublicMapObserved(complete.Current, complete.Nodes.Reverse().ToImmutableArray(),
            complete.Edges, complete.Options, capture);
        Assert.Throws<ArgumentException>(() => PublicMapObservationProfiles.ValidateEvidence(Profile, Evidence(backwards)));
        string wire = PublicRunEvidenceJson.Serialize(evidence);
        Assert.Contains("\"status\":\"complete\"", wire);
        Assert.Contains("\"nodeType\":\"unknown\"", wire);
        Assert.Equal(wire, PublicRunEvidenceJson.Serialize(PublicRunEvidenceJson.Read(wire)));
        foreach (string field in new[] { "status", "nodes", "edges", "startingNode", "bossNodes" })
        {
            var altered = JsonNode.Parse(wire)!;
            altered["events"]![2]!["payload"]!["currentMap"]!.AsObject().Remove(field);
            Assert.Throws<JsonException>(() => PublicRunEvidenceJson.Read(altered.ToJsonString()));
        }
    }

    [Fact]
    public async Task NativeProducerAddsCurrentMapOnlyForDeclaredNewChannelAndPreservesLegacyRun()
    {
        var options = new NaturalSourceOptions(MaxFloors: 8, MaxRoots: 2, MaxRootsPerCombat: 1,
            SeedPrefix: "owned-native-opening", ContinuationPolicyId: PublicContinuationPolicies.ReviewedId,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version,
            PublicMapObservationProfile: PublicMapObservationProfiles.CoordinateOrderV1);
        var legacy = await NaturalSourceCollector.CollectAsync(options);
        var complete = await NaturalSourceCollector.CollectAsync(options with
            { PublicMapObservationProfile = Profile, PublicEvidenceProfile = PublicRunEvidence.CompleteMapVersion });
        Assert.Null(Assert.Single(complete.Runs).Error);
        Assert.Equal(PublicJson.Serialize(legacy.Runs), PublicJson.Serialize(complete.Runs));
        Assert.Equal(2, complete.Roots.Length);
        foreach (var pair in legacy.Roots.Zip(complete.Roots))
        {
            var actual = pair.Second.PublicRoot;
            PublicEvidenceInput.ValidateProfile(new(MaxFloors: 8, OutsideCombatScript: options.OutsideCombatScript,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
                PublicMapObservationProfile: Profile), actual);
            Assert.All(actual.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicMapObserved>(),
                map => Assert.Equal(PublicMapCaptureStatus.Complete, map.CurrentMap!.Status));
            var stripped = new PublicRunEvidence(PublicRunEvidence.Version, actual.PublicEvidence.CompleteFromRunStart,
                actual.PublicEvidence.Events.Select(e => e.Payload is PublicMapObserved map
                    ? new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal, new PublicMapObserved(map.Current, map.Nodes, map.Edges, map.Options)) : e).ToImmutableArray());
            Assert.Equal(PublicJson.Serialize(pair.First.PublicRoot), PublicJson.Serialize(actual with { PublicEvidence = stripped }));
            Assert.Equal(PublicJson.Serialize(pair.First.SourceTrace), PublicJson.Serialize(pair.Second.SourceTrace));
        }
    }

    private static PublicCurrentMapCapture SmallGraph() => new(PublicMapCaptureStatus.Complete,
        [new(new(0, 0), PublicMapNodeType.Ancient), new(new(0, 1), PublicMapNodeType.Unknown), new(new(0, 2), PublicMapNodeType.Boss)],
        [new(new(0, 0), new(0, 1)), new(new(0, 1), new(0, 2))], new(0, 0), [new(0, 2)]);

    private static PublicRunEvidence Evidence(PublicMapObserved map)
    {
        var recorder = new PublicRunEvidenceRecorder(null, null,
            map.CurrentMap is not null ? PublicRunEvidence.CompleteMapVersion : PublicRunEvidence.Version);
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, 1);
        recorder.Record(owner, map);
        return recorder.Capture();
    }
}
