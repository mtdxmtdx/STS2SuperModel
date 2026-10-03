using System.Diagnostics;
using Nosl.Contracts;
using Nosl.Objectives;
using Sts2Sim.Core.Random;

namespace Nosl.Worker;

internal sealed record NativeConstructedTapeProposalAudit(int SampleCall, int Attempt, NativeTapeRecipe Recipe,
    string Status, double ElapsedSeconds, string? Detail, int ConditionedShuffles, int ConditionedHpCount,
    int DistinctTapeCells, int ConditionedTapeCells);

/// <summary>
/// Posterior for a declared fresh construction, not natural-run seed inference.
/// Inputs are only the detached complete public packet and immutable setup law.
/// Each proposal owns a freshly generated native lifecycle; every public history
/// event, including the missing-run-start declaration, is conditioned unchanged.
/// </summary>
internal sealed class NativeConstructedTapeSource : ITeacherSource
{
    internal const string Profile = "owned-constructed-native-tape-conditional-v1-public-evidence-v2";
    internal const string ImplementationVersion = "nosl-constructed-native-tape-conditional-v1-public-evidence-v2";
    internal const string ProposalDomain = "nosl-constructed-tape-independent-proposals-v1";
    private readonly string _serializedRoot;
    private readonly NativeConstructedTapePrior _prior;
    private readonly NativePublicCombatPrefixCondition? _combatCondition;
    private readonly PublicRunEvidence _evidence;
    private readonly string _entryJson;
    private readonly string?[] _potions;
    private readonly CancellationToken _cancellation;
    private readonly Func<NativeConstructedTapePrior, NativeTapeRecipe, NativeLabelTape,
        CancellationToken, Task<NativeRunWorld?>> _openWorld;
    private readonly List<NativeConstructedTapeProposalAudit> _attempts = [];
    private int _sampleCalls;

    internal NativeConstructedTapeSource(DecisionPacket publicRoot, NativeConstructedTapePrior prior,
        bool enableConditioning = true, CancellationToken cancellationToken = default,
        Func<NativeConstructedTapePrior, NativeTapeRecipe, NativeLabelTape, CancellationToken,
            Task<NativeRunWorld?>>? nativeOpenerForTests = null)
    {
        _prior = prior.Freeze(); _cancellation = cancellationToken;
        _openWorld = nativeOpenerForTests ?? NativeRunWorld.OpenConstructedLabelTapeAsync;
        _serializedRoot = PublicJson.Serialize(publicRoot);
        var root = PublicJson.Read<DecisionPacket>(_serializedRoot);
        ValidateRoot(root, _prior);
        _evidence = root.PublicEvidence!;
        _entryJson = root.Observation!.History.Single(e => e.Kind == NativeEntryAssets.EventKind).Detail;
        var entry = PublicJson.Read<NativeEntryAssets>(_entryJson);
        if (entry.SchemaVersion != "nosl.native-entry-assets.v1" || entry.Hp != root.Observation.StartHp
            || entry.Hp <= 0 || entry.Hp > entry.MaxHp || entry.Potions is null)
            throw new ArgumentException("Invalid constructed public entry anchor");
        StartHp = entry.Hp; StartMaxHp = entry.MaxHp; _potions = entry.Potions.ToArray();
        if (enableConditioning)
        {
            var condition = NativePublicCombatPrefixCondition.CreateConstructed(root, _prior);
            _combatCondition = condition.EligibleShuffleCount > 0 ? condition : null;
            ConditioningReason = _combatCondition is not null ? "certified_declared_constructed_startup"
                : condition.Reason ?? condition.Combats.Values.FirstOrDefault()?.ShuffleReason
                    ?? "uncertified_constructed_startup";
        }
        else ConditioningReason = "conditioning_disabled_for_reference";
    }

    private static void ValidateRoot(DecisionPacket root, NativeConstructedTapePrior prior)
    {
        if (root.Status is not ("player_decision" or "card_choice") || root.Observation is null || root.Actions.Length == 0)
            throw new ArgumentException("Active constructed public decision required");
        PublicEvidenceInput.ValidateProfile(prior.Execution, root);
        var evidence = root.PublicEvidence!;
        var context = root.Observation.RunContext!;
        context.Validate();
        if (evidence.SchemaVersion != PublicRunEvidence.CompleteMapVersion || evidence.CompleteFromRunStart
            || context.CompleteFromRunStart || context.CombatEntryIndex is not null
            || context.ActIndex != EncounterCoverage.Find(prior.Setup.Encounter).ActIndex || context.Floor != 0
            || evidence.Events[0] is not { OwnerOrdinal: null, Payload: PublicEvidenceGap
                { Reason: PublicEvidenceGapReason.RunStartNotObserved } }
            || evidence.Events.Any(e => e.Payload is PublicRunStarted or PublicMapObserved or PublicMapChosen))
            throw new ArgumentException("Declared construction must retain missing native run start and unobserved map history");
        var owners = evidence.Events.Where(e => e.Payload is PublicOwnerStarted
            { OwnerKind: PublicEvidenceOwnerKind.Combat }).ToArray();
        if (owners.Length != 1 || owners[0].Payload is not PublicOwnerStarted
                { CompleteFromOwnerStart: true, ParentOwnerOrdinal: null }
            || root.Observation.History.Count(e => e.Kind == NativeEntryAssets.EventKind) != 1)
            throw new ArgumentException("One complete declared combat and public entry anchor required");
        int decisionIndex = root.Actions[0].Revision;
        if (decisionIndex < 0 || root.Actions.Any(a => a.Revision != decisionIndex)
            || root.Observation.History.Count(e => e.Kind == "action") != decisionIndex)
            throw new ArgumentException("Constructed public local decision index is inconsistent");
        var decisions = evidence.Events.Where(e => e.OwnerOrdinal == owners[0].OwnerOrdinal
            && e.Payload is PublicCombatDecision).Select(e => (PublicCombatDecision)e.Payload).ToArray();
        if (decisions.Length != decisionIndex + 1 || decisions.Any(d => !d.HistoryCompleteFromCombatStart)
            || !decisions.Select((d, i) => d.Actions.All(a => a.Revision == i)).All(matches => matches))
            throw new ArgumentException("Constructed public history must retain every decision from combat entry");
        int selected = Array.FindIndex(decisions, decision => prior.Selects(
            new(decision.Status, decision.Observation, decision.Actions), decision.Actions[0].Revision));
        if (selected != decisionIndex)
            throw new ArgumentException("Public root is not the declared first stopping-rule match");
    }

    public int StartHp { get; }
    public int StartMaxHp { get; }
    public string?[] StartPotions => _potions.ToArray();
    public DecisionPacket Observe() => PublicJson.Read<DecisionPacket>(_serializedRoot);
    public string PosteriorProfile => Profile;
    public string PriorWarning => "Declared fresh constructed setup under independent Map/Rewards/native-state tapes; not natural reachability or the game's finite-seed law. Exact full-v5 public history conditions independent owned native replays. Uncertified mechanisms retain ordinary tape rejection; absence and computation failures remain unresolved.";
    public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile) => null;
    internal NativeConstructedTapePrior Prior => _prior.Freeze();
    internal NativeConstructedTapeProposalAudit[] ProposalAudit => _attempts.ToArray();
    internal bool UsesPrimitiveConditioning => _combatCondition is not null;
    internal string ConditioningReason { get; }

    public async Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        int call = ++_sampleCalls;
        var random = new Rng(seed, ProposalDomain);
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var recipe = _prior.Draw(random);
            var timer = Stopwatch.StartNew();
            var tape = NativeLabelTape.ForConstructedPrior(_prior, recipe, _combatCondition, _evidence, _entryJson);
            NativeRunWorld? world = null;
            bool accepted = false;
            Exception? operationFailure = null;
            NativeConstructedTapeProposalAudit AuditRow(string status, string? detail) => new(call, attempt,
                recipe, status, timer.Elapsed.TotalSeconds, detail, tape.ConditionedPublicCombatShuffles,
                tape.ConditionedPublicCombatHp, tape.DistinctCells, tape.ConditionedCells);
            void Audit(string status, string? detail = null) => _attempts.Add(AuditRow(status, detail));
            try
            {
                _cancellation.ThrowIfCancellationRequested();
                world = await _openWorld(_prior, recipe, tape, _cancellation);
                tape.RequireSuccessfulConditionedWords();
                if (world is null) Audit("absent_under_declared_source_horizon");
                else
                {
                    tape.ValidateProposalCompletion();
                    if (PublicJson.Serialize(world.Observe()) != _serializedRoot) Audit("public_packet_mismatch");
                    else if (!tape.AcceptCorrection(random.NextUnsignedLong)) Audit("exact_proposal_density_correction_rejected");
                    else { Audit("accepted"); accepted = true; return world; }
                }
            }
            catch (NativePublicConstraintMismatchException exception) when (!tape.HasConditionedWordFailure)
            { Audit("public_constraint_mismatch", exception.Message); }
            catch (Exception exception)
            {
                Exception failure = tape.ConditionedWordError ?? exception;
                operationFailure = failure;
                Audit(failure is OperationCanceledException ? "computation_cancelled" : "proposal_engine_error",
                    failure.GetType().Name + ": " + failure.Message);
                if (!ReferenceEquals(failure, exception)) throw failure;
                throw;
            }
            finally
            {
                if (!accepted && world is not null)
                {
                    try { await world.DisposeAsync(); }
                    catch (Exception failure)
                    {
                        int index = _attempts.FindLastIndex(a => a.SampleCall == call && a.Attempt == attempt);
                        var failed = AuditRow("proposal_cleanup_error", failure.GetType().Name + ": " + failure.Message);
                        if (index >= 0) _attempts[index] = failed; else _attempts.Add(failed);
                        if (operationFailure is not null)
                            throw new AggregateException("Constructed proposal execution and cleanup both failed", operationFailure, failure);
                        throw;
                    }
                }
            }
        }
        throw new PosteriorSamplingException(maxAttempts);
    }
}
