using System.Collections.Immutable;
using Nosl.Contracts;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

/// <summary>
/// Complete semantic act-zero map under the explicitly independent Map law.
/// Eligibility relies on the declared native public producer, not on graph
/// plausibility as a purported proof of positive native support.
/// </summary>
internal sealed class NativePublicMapReconstructionCondition
{
    internal const string SupportContract = "native-standard-act0-complete-map-producer-v1";
    private readonly LabelMapNode[] _nodes;
    private readonly LabelMapEdge[] _edges;
    internal long MapEventOrdinal { get; }

    private NativePublicMapReconstructionCondition(PublicCurrentMapCapture capture, long ordinal)
    {
        _nodes = capture.Nodes.Select(n => new LabelMapNode(new(n.Coordinate.Col, n.Coordinate.Row),
            n.NodeType switch
            {
                PublicMapNodeType.Ancient => MapPointType.Ancient,
                PublicMapNodeType.Boss => MapPointType.Boss,
                PublicMapNodeType.Elite => MapPointType.Elite,
                PublicMapNodeType.Monster => MapPointType.Monster,
                PublicMapNodeType.Rest => MapPointType.RestSite,
                PublicMapNodeType.Shop => MapPointType.Shop,
                PublicMapNodeType.Treasure => MapPointType.Treasure,
                PublicMapNodeType.Unknown => MapPointType.Unknown,
                _ => throw new ArgumentException("Unsupported complete native map icon"),
            })).ToArray();
        _edges = capture.Edges.Select(e => new LabelMapEdge(new(e.From.Col, e.From.Row), new(e.To.Col, e.To.Row))).ToArray();
        MapEventOrdinal = ordinal;
    }

    internal static bool TryCreate(PublicRunEvidence? evidence, NativeTapePrior prior,
        out NativePublicMapReconstructionCondition? condition, out string? reason)
    {
        condition = null; reason = null;
        if (!prior.UsesMapProvenance
            || prior.Execution.PublicMapObservationProfile != PublicMapObservationProfiles.CompleteGraphV1
            || prior.Execution.PublicEvidenceProfile != PublicRunEvidence.CompleteMapVersion
            || evidence?.SchemaVersion != PublicRunEvidence.CompleteMapVersion)
        { reason = "independent_complete_map_prior_required"; return false; }
        if (prior.Execution.OutsideCombatScript is not (null or NaturalSourceCollector.ScriptVersion
                or NaturalSourceCollector.BoundedEventScriptVersion))
        { reason = "native_complete_map_producer_required"; return false; }
        // This validates the source-pinned native opening and pre-map history.
        // The detached slice-only projection is a necessary boundary check,
        // never a source of reconstructed nodes or an old-law probability plan.
        var boundaryEvidence = new PublicRunEvidence(PublicRunEvidence.Version, evidence.CompleteFromRunStart,
            evidence.Events.Select(e => new PublicRunEvidenceEvent(e.EventOrdinal, e.OwnerOrdinal,
                e.Payload is PublicMapObserved m ? new PublicMapObserved(m.Current, m.Nodes, m.Edges, m.Options) : e.Payload)).ToImmutableArray());
        var boundaryPrior = prior with { SchemaVersion = NativeTapePrior.RewardsVersion,
            Execution = prior.Execution with { PublicEvidenceProfile = PublicRunEvidence.Version,
                PublicMapObservationProfile = PublicMapObservationProfiles.CoordinateOrderV1 } };
        if (!NativePublicInitialMapCondition.TryCreate(boundaryEvidence, boundaryPrior, out var initial, out reason)) return false;
        var observed = evidence.Events.Single(e => e.EventOrdinal == initial!.MapEventOrdinal).Payload as PublicMapObserved;
        if (observed?.CurrentMap is not { Status: PublicMapCaptureStatus.Complete } capture
            || capture.StartingNode != new PublicMapCoordinate(3, 0)
            || capture.BossNodes.Length != 1 || capture.BossNodes[0] != new PublicMapCoordinate(3, 16)
            || capture.Nodes.Any(n => n.Coordinate.Col is < 0 or > 6 || n.Coordinate.Row is < 0 or > 16
                || n.Coordinate.Row == 1 && n.NodeType != PublicMapNodeType.Monster
                || n.Coordinate.Row == 9 && n.NodeType != PublicMapNodeType.Treasure
                || n.Coordinate.Row == 15 && n.NodeType != PublicMapNodeType.Rest))
        { reason = "complete_native_standard_act0_graph_required"; return false; }
        // Regeneration cannot occur, even transiently, in the reviewed natural
        // act-zero source. An observed contradiction disables this certificate.
        if (evidence.Events.TakeWhile(e => e.Payload is not PublicOwnerStarted { ActIndex: > 0 })
            .Any(e => e.Payload switch
            {
                PublicOwnerEnded { Assets: { } a } => a.Relics.Any(r => r.Id == "GoldenCompass"),
                PublicCombatFact { Assets: { } a } => a.Relics.Any(r => r.Id == "GoldenCompass"),
                PublicOffersObserved o => o.Groups.Any(g => g.Offers.Any(x => x.Relic?.Id == "GoldenCompass")),
                PublicOptionChosen o => o.Key == "GoldenCompass",
                _ => false,
            }))
        { reason = "unmodified_native_complete_map_required"; return false; }
        try
        {
            var candidate = new NativePublicMapReconstructionCondition(capture, initial!.MapEventOrdinal);
            // The structural guard catches malformed imports without a source
            // RNG, source object, hidden trace, or native map-generation retry.
            _ = candidate.Reconstruct(new Rng(0));
            condition = candidate;
            return true;
        }
        catch (ArgumentException)
        { reason = "invalid_complete_standard_map_structure"; return false; }
    }

    internal StandardActMap Reconstruct(Rng mapRng) =>
        StandardActMap.ReconstructForLabels(mapRng, 15, _nodes, _edges);
}
