using System.Reflection;
using System.Text.Json.Nodes;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativePublicOpeningEncounterTests
{
    private static NativeTapePrior Prior => new()
    {
        EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    public async Task EveryNativeWeakRosterCertifiesSlotZeroAndReplaysIndependentNativeGeneration(bool overgrowth, int target)
    {
        var fixture = await Opening(overgrowth, target);
        var packet = fixture.Packet;
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(packet, Prior, out var condition, out var reason), reason);
        var entry = NativeOpeningEncounterCatalog.Entries.Single(item => item.ActType == condition!.TargetActType && item.Index == target);
        Assert.Equal(overgrowth ? typeof(Overgrowth) : typeof(Underdocks), condition!.TargetActType);
        Assert.Equal(target, condition.TargetEncounterIndex); Assert.Equal(entry.EncounterId, condition.TargetEncounterId);
        Assert.Equal(new ShuffleRational(1, 4), condition.Envelope);
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet)),
            Prior with { SchemaVersion = NativeTapePrior.RewardsVersion }, out var hybrid, out reason), reason);
        Assert.Equal(condition.TargetEncounterId, hybrid!.TargetEncounterId);

        // A separate primitive-tape replay, independent of the observed formation,
        // HP, Neow and all other source cells. Only the one weak-bag word is forced.
        NativePublicOpeningEncounterProposal? first = null;
        var proposed = await Opening(overgrowth, target, condition, proposalSeed: 556677, onProposal: p => first = p);
        Assert.NotNull(first); first.ValidateCompletion(); Assert.True(first.Applied);
        Assert.Equal(target, first.FirstEncounterIndex);
        Assert.True(first.AcceptCorrection(() => throw new InvalidOperationException("No correction word is required")));
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(proposed.Packet, Prior, out var replayed, out reason), reason);
        Assert.Equal(condition.TargetEncounterId, replayed!.TargetEncounterId);
        Assert.Equal(1, proposed.ConditionedCells);
        // Replay of these hypothetical recipe/auxiliary coordinates is exact; no
        // observed source recipe is an input to the condition or the proposal.
        var repeated = await Opening(overgrowth, target, condition, proposalSeed: 556677,
            replay: proposed.Tape.ReplayCopy(), replayRecipe: proposed.Recipe);
        Assert.Equal(PublicJson.Serialize(proposed.Packet), PublicJson.Serialize(repeated.Packet));
        Assert.NotEqual(PublicJson.Serialize(packet), PublicJson.Serialize(proposed.Packet));

        // The current root may be much later. Its observation and current enemy
        // array are not substitutes for the first owner's retained typed evidence.
        var unrelatedCurrent = packet with { Observation = packet.Observation! with { Enemies = [] } };
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(unrelatedCurrent, Prior, out var retained, out reason), reason);
        Assert.Equal(condition.TargetEncounterId, retained!.TargetEncounterId);
    }

    [Fact]
    public async Task GapsForcedOwnersAndNonMonsterMapMovesFallBackWithoutChangingPrior()
    {
        var fixture = await Opening(true, 0);
        var root = fixture.Packet;
        var noChannel = Prior with { Execution = Prior.Execution with { PublicEvidenceProfile = null } };
        Assert.False(NativePublicOpeningEncounterCondition.TryCreate(root, noChannel, out _, out _));
        Assert.False(NativePublicOpeningEncounterCondition.TryCreate(root with { PublicEvidence = null }, Prior, out _, out _));
        var forcedEvidence = new PublicRunEvidence(PublicRunEvidence.Version, true,
        [
            new(0, null, root.PublicEvidence!.Events[0].Payload),
            new(1, 0, new PublicOwnerStarted(PublicEvidenceOwnerKind.Event, 0, 2, null, true)),
            new(2, 1, new PublicOwnerStarted(PublicEvidenceOwnerKind.Combat, 0, 2, 0, true)),
            new(3, 1, new PublicCombatFact(PublicCombatFactKind.Started)),
            new(4, 1, new PublicCombatFact(PublicCombatFactKind.PlayerTurnStarted, turn: 1)),
            new(5, 1, new PublicCombatFact(PublicCombatFactKind.IntentPublished, targetSlot: 0, model: "FuzzyWurmCrawler")),
        ]);
        Assert.False(NativePublicOpeningEncounterCondition.TryCreate(root with { PublicEvidence = forcedEvidence },
            Prior, out _, out var forcedReason));
        Assert.Equal("first_normal_combat_owner_required", forcedReason);
        var json = JsonNode.Parse(PublicJson.Serialize(root))!;
        var events = json["publicEvidence"]!["events"]!.AsArray();
        var combat = events.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "owner_started"
            && e["payload"]!["ownerKind"]!.GetValue<string>() == "combat")!;
        combat["payload"]!["floor"] = 3;
        Assert.False(NativePublicOpeningEncounterCondition.TryCreate(PublicJson.Read<DecisionPacket>(json.ToJsonString()), Prior, out _, out _));
        combat["payload"]!["floor"] = 2;
        var map = events.First(e => e!["payload"]!["kind"]!.GetValue<string>() == "map")!["payload"]!;
        foreach (var node in map["nodes"]!.AsArray().Where(n => n!["coordinate"]!["row"]!.GetValue<int>() == 1))
            node!["nodeType"] = "unknown";
        Assert.False(NativePublicOpeningEncounterCondition.TryCreate(PublicJson.Read<DecisionPacket>(json.ToJsonString()), Prior, out _, out _));

        // Append a later global gap: it does not invalidate facts already read at
        // the opening; completion of the complete root is still checked by replay.
        var evidence = root.PublicEvidence!;
        var laterGap = evidence.Append(new(evidence.Events.Length, null, new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)));
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(root with { PublicEvidence = laterGap }, Prior, out _, out var reason), reason);
        var missingStart = JsonNode.Parse(PublicJson.Serialize(root))!;
        missingStart["publicEvidence"]!["completeFromRunStart"] = false;
        missingStart["publicEvidence"]!["events"]![0]!["payload"] = new JsonObject
            { ["kind"] = "gap", ["reason"] = "run_start_not_observed" };
        Assert.False(NativePublicOpeningEncounterCondition.TryCreate(PublicJson.Read<DecisionPacket>(missingStart.ToJsonString()), Prior, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExactFiniteWordLawAndNativeBucketEndpoints(int target)
    {
        const int bits = 4, domain = 1 << bits, lowBits = 64 - bits;
        var factor = ConditionalShuffleProposal.Factor(4, target, bits);
        int compatible = 0;
        for (int high = 0; high < domain; high++)
        {
            bool native = (int)((double)high / domain * 4) == target;
            bool proposed = (ulong)high >= factor.BucketStart && (ulong)high < factor.BucketStart + factor.BucketSize;
            Assert.Equal(native, proposed); if (native) compatible++;
        }
        Assert.Equal(new ShuffleRational(compatible, domain), new ShuffleRational(1, 4));
        // Every low bit remains uniform. Enumerate every retained high value and
        // both low-word endpoints without invoking a numerical approximation.
        for (ulong offset = 0; offset < factor.BucketSize; offset++)
        foreach (ulong low in new[] { 0UL, (1UL << lowBits) - 1 })
        {
            int draw = 0;
            var plan = NativeOpeningEncounterPlan.Create(target, () => draw++ == 0 ? offset : low, bits);
            Assert.Equal(((factor.BucketStart + offset) << lowBits) | low, Assert.Single(plan.RawWords));
            Assert.Equal(new ShuffleRational(1, 4), plan.NativeToProposalRatio);
        }
        foreach (bool upper in new[] { false, true })
        {
            var nativeFactor = ConditionalShuffleProposal.Factor(4, target);
            ulong word = ((nativeFactor.BucketStart + (upper ? nativeFactor.BucketSize - 1 : 0)) << 11) | (upper ? 2047UL : 0UL);
            foreach (var act in new Sts2Sim.Core.Content.ActDefinition[] { new Overgrowth(), new Underdocks() })
            {
                int draws = 0;
                using var scope = LabelRandomScope.Enter(_ => { draws++; return word; });
                var picked = act.PickEncounter(Sts2Sim.Core.Rooms.RoomType.Monster, new Rng(123));
                Assert.Same(act.MonsterEncounterCandidates.Where(e => e.IsWeak).ElementAt(target), picked);
                Assert.Equal(1, draws);
            }
        }
        // Exact conditioning leaves any independent latent formation variable
        // unchanged. Four equally likely slime paths remain four, not one.
        Assert.Equal(4, NativeOpeningEncounterCatalog.Entries.Single(e => e.EncounterId == "SLIMES_WEAK").Rosters.Count);
        Assert.Single(NativeOpeningEncounterCatalog.Entries.Single(e => e.EncounterId == "CORPSE_SLUGS_WEAK").Rosters);
    }

    [Fact]
    public async Task OwnedAdapterKeepsTapeAliasPrimitiveStateAndCompletionGuards()
    {
        var conditionRoot = await Opening(true, 0);
        Assert.True(NativePublicOpeningEncounterCondition.TryCreate(conditionRoot.Packet, Prior, out var condition, out _));
        NativePublicOpeningEncounterProposal New(NativeLabelTape tape) => new(condition!, () => 0, Force(tape));
        var run = FreshRun();
        var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var proposal = New(tape);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(run, run.Act, run.Rng.UpFront)));
        proposal.AttachHypotheticalRun(run);
        Assert.Throws<InvalidOperationException>(() => proposal.AttachHypotheticalRun(run));
        Assert.Throws<InvalidOperationException>(() => proposal.BeginGeneration(new(run, run.Act, run.Rng.Shuffle)));
        var snap = run.Rng.UpFront.ToSerializable();
        Word(tape)(new(snap.state0, snap.state1, snap.state2, snap.state3));
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var forced = proposal.BeginGeneration(new(run, run.Act, run.Rng.UpFront));
            _ = run.Rng.UpFront.NextUnsignedLong();
        });
        Assert.Equal(0, tape.ConditionedCells);

        var fresh = FreshRun();
        var freshTape = new NativeLabelTape(new(12, 23, 34, 0, 0));
        var skipped = New(freshTape); skipped.AttachHypotheticalRun(fresh);
        Assert.Throws<InvalidOperationException>(() => skipped.BeginGeneration(new(fresh, fresh.Act, fresh.Rng.UpFront)).Dispose());
        Assert.Throws<InvalidOperationException>(skipped.ValidateCompletion);
        var foreign = New(new(new(13, 24, 35, 0, 0))); foreign.AttachHypotheticalRun(fresh);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var forced = foreign.BeginGeneration(new(fresh, fresh.Act, fresh.Rng.UpFront));
            _ = fresh.Rng.Shuffle.NextUnsignedLong();
        });
    }

    private sealed record Fixture(DecisionPacket Packet, int ConditionedCells, NativeLabelTape Tape, NativeTapeRecipe Recipe);
    private static async Task<Fixture> Opening(bool overgrowth, int target,
        NativePublicOpeningEncounterCondition? condition = null, ulong proposalSeed = 12345,
        Action<NativePublicOpeningEncounterProposal>? onProposal = null,
        NativeLabelTape? replay = null, NativeTapeRecipe? replayRecipe = null)
    {
        for (ulong attempt = 0; attempt < (replay is null ? 32UL : 1UL); attempt++)
        {
            var recipe = replayRecipe ?? new NativeTapeRecipe(400 + attempt, 700 + attempt + proposalSeed, proposalSeed, 0, 0);
            var tape = replay ?? new NativeLabelTape(recipe);
            NativePublicOpeningEncounterProposal? proposal = null;
            using var scope = LabelRandomScope.Enter(Word(tape), beginNormalEncounter: context =>
            {
                if ((context.Act is Overgrowth) != overgrowth)
                    throw new NativePublicConstraintMismatchException("Independent fixture act does not match requested test case");
                if (condition is null)
                {
                    var plan = NativeOpeningEncounterPlan.Create(target, new Rng(proposalSeed).NextUnsignedLong);
                    return Force(tape)(plan.RawWords, context.Rng, "controlled opening fixture");
                }
                proposal = new(condition, new Rng(proposalSeed).NextUnsignedLong, Force(tape));
                proposal.AttachHypotheticalRun(context.Run);
                return proposal.BeginGeneration(context);
            });
            try
            {
                await using var world = await NativeRunWorld.OpenAsync(Prior.Execution, recipe.IndependentRunSeed, 0);
                Assert.NotNull(world);
                if (proposal is not null) { proposal.ValidateCompletion(); onProposal?.Invoke(proposal); }
                return new(world.Observe(), tape.ConditionedCells, tape, recipe);
            }
            catch (NativePublicConstraintMismatchException) { }
        }
        throw new InvalidOperationException("No native fixture act found");
    }

    // Exercise the production tape's guard implementation without changing the
    // shared integration file solely to connect this detached optional proposal.
    private static Func<LabelRandomState, ulong> Word(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<LabelRandomState, ulong>>(tape);
    private static Func<IReadOnlyList<ulong>, Rng, string, IDisposable> Force(NativeLabelTape tape) => typeof(NativeLabelTape)
        .GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
    private static RunState FreshRun()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("public-opening-guards", [new Overgrowth(), new Hive(), new Glory()], ascensionLevel: 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        run.AddVisitedMapCoord(run.Map.StartingMapPoint.coord); return run;
    }
}
