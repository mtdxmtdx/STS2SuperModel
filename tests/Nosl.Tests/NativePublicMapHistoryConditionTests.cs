using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicMapHistoryConditionTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    public static IEnumerable<object[]> NativeCases =>
        from overgrowth in new[] { false, true }
        from canonical in new[] { false, true }
        from uses in new[] { -1, 0, 3 }
        select new object[] { overgrowth, canonical, uses };

    [Theory]
    [MemberData(nameof(NativeCases))]
    public void LinkedSlicesMatchNativeTravelForBothActsProfilesAndBootsStates(bool overgrowth, bool canonical, int uses)
    {
        var run = Run(overgrowth);
        string? profile = canonical ? PublicMapObservationProfiles.CoordinateOrderV1 : null;
        var prior = Prior with { Execution = Prior.Execution with { PublicMapObservationProfile = profile } };
        var evidence = Evidence(run, uses, profile);
        var detached = PublicJson.Read<PublicRunEvidence>(PublicJson.Serialize(evidence));
        string before = PublicJson.Serialize(detached);
        Assert.True(NativePublicMapHistoryCondition.TryCreate(detached, prior, out var condition, out var why), why);
        Assert.Equal(6, condition!.CertifiedSliceCount); Assert.Null(condition.SuffixFallbackReason);
        Assert.True(condition.MatchesMap(run.Map)); Assert.Equal(6, condition.MatchingSlicePrefix(run.Map));
        Assert.True(NativeInitialPrefixCondition.TryCreate(new("fixture", null, [], detached), prior, out var initial, out why), why);
        Assert.Equal(condition.MapEventOrdinals, initial!.PublicMapHistory!.MapEventOrdinals);
        Assert.True(initial.MatchesMap(run.Map)); Assert.Equal(before, PublicJson.Serialize(detached));
        // A later visible icon is a real necessary predicate, even though the
        // first public slice still matches. Unknown stays an icon, with no roll.
        var later = detached.Events.Select(e => e.Payload).OfType<PublicMapObserved>().Last();
        var target = run.Map.GetPoint(later.Options[0].Coordinate.Col, later.Options[0].Coordinate.Row)!;
        target.PointType = target.PointType == MapPointType.Unknown ? MapPointType.Shop : MapPointType.Unknown;
        Assert.True(condition.Initial.MatchesMap(run.Map)); Assert.False(condition.MatchesMap(run.Map));
        Assert.Equal(5, condition.MatchingSlicePrefix(run.Map));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MeltedBootsWithUnusedChargesUseOrdinaryNativeTravel(bool canonical)
    {
        var run = Run(false);
        string? profile = canonical ? PublicMapObservationProfiles.CoordinateOrderV1 : null;
        var prior = Prior with { Execution = Prior.Execution with { PublicMapObservationProfile = profile } };
        var evidence = Evidence(run, 0, profile, laterMelted: true);
        Assert.True(NativePublicMapHistoryCondition.TryCreate(evidence, prior, out var condition, out var why), why);
        Assert.Equal(6, condition!.CertifiedSliceCount); Assert.True(condition.MatchesMap(run.Map));
        Assert.All(evidence.Events.Select(e => e.Payload).OfType<PublicMapObserved>(),
            map => Assert.All(map.Options, option => Assert.True(option.IsOrdinaryConnection)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChoiceTimeHpControlsRestPriorityAndUnknownStaysPublic(bool canonical)
    {
        var run = Run(true);
        string? profile = canonical ? PublicMapObservationProfiles.CoordinateOrderV1 : null;
        var prior = Prior with { Execution = Prior.Execution with { PublicMapObservationProfile = profile } };
        var first = NativeSourceMapChoice.Choose(run.Map.StartingMapPoint.Children, 70, 70);
        var row = run.Map.GetPointsInRow(2).ToArray(); Assert.True(row.Length >= 3);
        foreach (var point in first.Children.ToArray()) first.RemoveChildPoint(point);
        first.AddChildPoint(row[0]); first.AddChildPoint(row[1]); first.AddChildPoint(row[2]);
        row[0].PointType = MapPointType.Unknown; row[1].PointType = MapPointType.RestSite; row[2].PointType = MapPointType.Monster;
        foreach (int hp in new[] { 34, 35 })
        {
            var evidence = Evidence(run, -1, profile, count: 2, hp: hp);
            var maps = evidence.Events.Where(e => e.Payload is PublicMapObserved).ToArray();
            var observed = (PublicMapObserved)maps[1].Payload;
            Assert.Contains(observed.Nodes, n => n.NodeType == PublicMapNodeType.Unknown);
            var chosen = (PublicMapChosen)evidence.Events[(int)maps[1].EventOrdinal + 1].Payload;
            Assert.Equal(hp == 34 ? row[1].coord.col : row[2].coord.col, chosen.Coordinate.Col);
            Assert.True(NativePublicMapHistoryCondition.TryCreate(evidence, prior, out var condition, out var why), why);
            Assert.Equal(2, condition!.CertifiedSliceCount); Assert.True(condition.MatchesMap(run.Map));
            // Keeping the observed choice but changing its exact public HP across
            // the native threshold must change the map predicate, not the graph.
            int ended = (int)maps[1].EventOrdinal + 2;
            var changed = Replace(evidence, ended, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Assets("GoldenPearl", hp: hp == 34 ? 35 : 34)));
            Assert.True(NativePublicMapHistoryCondition.TryCreate(changed, prior, out var other, out why), why);
            Assert.False(other!.MatchesMap(run.Map));
        }
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("compass")]
    [InlineData("act")]
    [InlineData("owner_incomplete")]
    [InlineData("assets")]
    [InlineData("boot_counter")]
    [InlineData("link")]
    [InlineData("truncated")]
    [InlineData("canonical_order")]
    public void UnsupportedSuffixRetainsEarlierSlicesAndOriginalEvidence(string change)
    {
        var run = Run(false);
        var profile = PublicMapObservationProfiles.CoordinateOrderV1;
        var prior = Prior with { Execution = Prior.Execution with { PublicMapObservationProfile = profile } };
        var evidence = Evidence(run, change == "boot_counter" ? 0 : -1, profile, count: 3,
            gapBeforeThird: change == "gap", compassBeforeThird: change == "compass");
        var maps = evidence.Events.Where(e => e.Payload is PublicMapObserved).ToArray();
        int third = (int)maps[2].EventOrdinal;
        var owner = (PublicOwnerStarted)evidence.Events[third - 1].Payload;
        if (change is "act" or "owner_incomplete")
            evidence = Replace(evidence, third - 1, new PublicOwnerStarted(PublicEvidenceOwnerKind.Map,
                change == "act" ? 1 : 0, owner.Floor, null, change != "owner_incomplete"));
        if (change == "assets") evidence = Replace(evidence, third + 2, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed));
        if (change == "boot_counter") evidence = Replace(evidence, third + 2,
            new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Assets("WingedBoots", missingCounter: true)));
        if (change == "link")
        {
            var slice = (PublicMapObserved)maps[2].Payload;
            // A valid standalone public map with the wrong current node is not
            // linked to the preceding observed choice.
            var other = run.Map.GetPointsInRow(2).First(p => p.coord.col != slice.Current!.Col);
            var choices = other.Children.ToArray();
            var entries = evidence.Events
                .SetItem(third, new(third, maps[2].OwnerOrdinal, NativePublicMapSlice.Observe(run.Map, other, choices, profile)))
                .SetItem(third + 1, new(third + 1, maps[2].OwnerOrdinal, new PublicMapChosen(third,
                    new(choices[0].coord.col, choices[0].coord.row))));
            evidence = new(evidence.SchemaVersion, true, entries);
        }
        if (change == "truncated") evidence = new(evidence.SchemaVersion, true, evidence.Events.Take(third + 2).ToImmutableArray());
        if (change == "canonical_order")
        {
            // Use free travel for enough native options at every later row.
            evidence = Evidence(run, 0, profile, count: 3); maps = evidence.Events.Where(e => e.Payload is PublicMapObserved).ToArray();
            third = (int)maps[2].EventOrdinal; var slice = (PublicMapObserved)maps[2].Payload;
            Assert.True(slice.Options.Length > 1);
            evidence = Replace(evidence, third, new PublicMapObserved(slice.Current, slice.Nodes, slice.Edges,
                slice.Options.Reverse().ToImmutableArray()));
        }
        string before = PublicJson.Serialize(evidence);
        Assert.True(NativePublicMapHistoryCondition.TryCreate(evidence, prior, out var condition, out var why), why);
        Assert.Equal(2, condition!.CertifiedSliceCount); Assert.NotNull(condition.SuffixFallbackReason);
        Assert.True(condition.MatchesMap(run.Map)); Assert.Equal(before, PublicJson.Serialize(evidence));
        Assert.True(NativeInitialPrefixCondition.TryCreate(new("fixture", null, [], evidence), prior, out var integrated, out why), why);
        Assert.Equal(2, integrated!.PublicMapHistory!.CertifiedSliceCount);
    }

    [Theory]
    [InlineData(1)] [InlineData(5)]
    public void IncompleteInitialOwnerDisablesCertificateWithoutThrowing(int ownerStart)
    {
        var evidence = Evidence(Run(false), -1, null);
        var owner = (PublicOwnerStarted)evidence.Events[ownerStart].Payload;
        var changed = Replace(evidence, ownerStart, new PublicOwnerStarted(owner.OwnerKind,
            owner.ActIndex, owner.Floor, owner.ParentOwnerOrdinal, false));
        Assert.False(NativePublicMapHistoryCondition.TryCreate(changed, Prior, out var condition, out var reason));
        Assert.Null(condition); Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void CompleteJointCandidateKeepsNativeTraceAcrossAllCertifiedSlices(bool overgrowth, bool canonical)
    {
        NaturalSourceCollector.InitializeNativeModels();
        var recipe = new NativeTapeRecipe(17, 23, 29, 0, 0);
        var random = new Rng(31, "public-map-native-fixture"); int draw = 0;
        var native = NativeComponentRejection.Evaluate(() => new RunState(recipe.IndependentRunSeed, ascensionLevel: 10),
            () => draw++ == 0 ? overgrowth ? 0UL : ulong.MaxValue : random.NextUnsignedLong());
        string? profile = canonical ? PublicMapObservationProfiles.CoordinateOrderV1 : null;
        var prior = Prior with { Execution = Prior.Execution with { PublicMapObservationProfile = profile } };
        var evidence = PublicJson.Read<PublicRunEvidence>(PublicJson.Serialize(Evidence(native.Value, 0, profile)));
        Assert.True(NativeInitialPrefixCondition.TryCreate(new("fixture", null, [], evidence), prior, out var condition, out var why), why);
        Assert.Equal(6, condition!.PublicMapHistory!.CertifiedSliceCount);
        var words = new Queue<ulong>(new[] { recipe.RunSeed }.Concat(native.Trace.DistinctBy(w => w.State).Select(w => w.Word)));
        var plan = condition.Prepare(recipe with { RunSeed = 997 }, 1, () => words.Dequeue());
        Assert.Empty(words); Assert.Equal(recipe, plan.SelectedRecipe); Assert.Equal(native.Trace, plan.Trace);
        Assert.Equal(1, plan.RunSeedDraws); Assert.Equal(native.Trace.Count, plan.Stats.TotalWordDraws);
        var tape = NativeLabelTape.ForDeclaredPrior(prior, plan.SelectedRecipe, initialPrefixPlan: plan);
        using (tape.EnterScope())
        {
            var replay = new RunState(plan.SelectedRecipe.IndependentRunSeed, ascensionLevel: 10);
            tape.AttachHypotheticalRun(replay); tape.ValidateProposalCompletion();
            Assert.True(condition.MatchesMap(replay.Map));
        }
    }

    [Theory]
    [InlineData(11001UL, 1)] [InlineData(11002UL, 1)] [InlineData(11003UL, 1)] [InlineData(11004UL, 5)]
    [InlineData(11005UL, 1)] [InlineData(11006UL, 2)] [InlineData(11007UL, 4)] [InlineData(11008UL, 1)]
    public async Task ExistingEightRecordedRootsCertifyEveryAvailableMapSlice(ulong draw, int expectedSlices)
    {
        var prior = Prior.Freeze(); var recipe = prior.Draw(new Rng(draw, "nosl-native-tape-source-draw-v1"));
        await using var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(source);
        var root = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
        Assert.True(NativeInitialPrefixCondition.TryCreate(root, prior, out var condition, out var why), why);
        Assert.Equal(expectedSlices, root.PublicEvidence!.Events.Count(e => e.Payload is PublicMapObserved));
        Assert.Equal(expectedSlices, condition!.PublicMapHistory!.CertifiedSliceCount);
        Assert.Null(condition.PublicMapHistory.SuffixFallbackReason); Assert.True(condition.MatchesMap(source.NativeRun.Map));
    }

    private static RunState Run(bool overgrowth)
    {
        NaturalSourceCollector.InitializeNativeModels();
        return new RunState("map-history-native-fixture", overgrowth ? new Overgrowth() : new Underdocks(), ascensionLevel: 10);
    }
    private static PublicEvidenceAssets Assets(string neow, int uses = 0, int hp = 70, bool missingCounter = false, bool melted = false)
    {
        PublicRelic Relic(string id) => new(id, new Dictionary<string, int> { ["isWax"] = 0, ["isMelted"] = 0,
            ["stackCount"] = 1 });
        var relics = new List<PublicRelic> { Relic("RingOfTheSnake") };
        if (neow.Length != 0) relics.Add(neow == "WingedBoots" && !missingCounter
            ? new("WingedBoots", new Dictionary<string, int> { ["timesUsed"] = uses, ["isMelted"] = melted ? 1 : 0 }) : Relic(neow));
        return new(hp, 70, 99, [], relics.ToArray(), [null, null], 3, 2, 0, 0);
    }
    private static PublicRunEvidence Evidence(RunState run, int laterBootUses, string? profile, int count = 6,
        int hp = 70, bool gapBeforeThird = false, bool compassBeforeThird = false, bool laterMelted = false)
    {
        string neow = laterBootUses >= 0 ? "WingedBoots" : "GoldenPearl";
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets("")));
        long neowOwner = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, 1);
        long offered = recorder.Record(neowOwner, new PublicOptionsObserved([new(neow, false),
            new(neow == "GoldenPearl" ? "WingedBoots" : "GoldenPearl", false), new("LeafyPoultice", false)]));
        recorder.Record(neowOwner, new PublicOptionChosen(offered, neow));
        recorder.Record(neowOwner, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Assets(neow)));
        var player = run.Players.FirstOrDefault() ?? Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        if (run.Players.Count == 0) run.AddPlayer(player);
        WingedBoots? boots = null;
        if (laterBootUses >= 0) { boots = (WingedBoots)ModelDb.Relic<WingedBoots>().MutableClone(); boots.AssignOwner(player); player.AddRelicInternal(boots); }
        var current = run.Map.StartingMapPoint;
        for (int i = 0; i < count; i++)
        {
            if (i == 2 && gapBeforeThird) recorder.RecordGap(null, PublicEvidenceGapReason.Interrupted);
            if (i == 2 && compassBeforeThird)
            {
                long unknown = recorder.BeginOwner(PublicEvidenceOwnerKind.Event, 0, i + 1);
                recorder.Record(unknown, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Assets("GoldenCompass")));
            }
            int used = i == 0 ? 0 : laterBootUses;
            if (boots is not null)
            {
                typeof(WingedBoots).GetProperty(nameof(WingedBoots.TimesUsed))!.SetValue(boots, used);
                boots.IsMelted = i > 0 && laterMelted;
            }
            var choices = MapTravel.GetTravelablePointsFrom(run, current).ToArray();
            Assert.Equal(choices, NativePublicMapHistoryCondition.TravelablePoints(run.Map, current, boots is not null && !boots.IsMelted && used < 3));
            var choice = NativeSourceMapChoice.Choose(choices, hp, 70);
            long map = recorder.BeginOwner(PublicEvidenceOwnerKind.Map, 0, i + 1);
            long observed = recorder.Record(map, NativePublicMapSlice.Observe(run.Map, current, choices, profile));
            recorder.Record(map, new PublicMapChosen(observed, new(choice.coord.col, choice.coord.row)));
            recorder.Record(map, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed, Assets(neow, Math.Max(used, 0), hp, melted: i > 0 && laterMelted)));
            current = choice;
        }
        // The boss lies outside the map grid; even active Boots follows Children.
        var lastRow = run.Map.GetPointsInRow(run.Map.GetRowCount() - 1).First();
        Assert.Equal(MapTravel.GetTravelablePointsFrom(run, lastRow),
            NativePublicMapHistoryCondition.TravelablePoints(run.Map, lastRow, boots is not null && !boots.IsMelted && !boots.IsUsedUp));
        if (boots is not null) player.RemoveRelicInternal(boots);
        return recorder.Capture();
    }
    private static PublicRunEvidence Replace(PublicRunEvidence evidence, int index, PublicEvidencePayload payload)
    {
        var events = evidence.Events.SetItem(index, new(index, evidence.Events[index].OwnerOrdinal, payload));
        bool complete = events[0].Payload is PublicRunStarted && !events.Any(e => e.Payload is PublicEvidenceGap
            || e.Payload is PublicOwnerStarted { CompleteFromOwnerStart: false });
        return new(evidence.SchemaVersion, complete, events);
    }
}
