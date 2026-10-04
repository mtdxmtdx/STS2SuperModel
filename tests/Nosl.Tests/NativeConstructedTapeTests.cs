using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativeConstructedTapeTests(ITestOutputHelper output)
{
    private static NativeConstructedTapePrior Prior(string encounter = "SludgeSpinnerWeak",
        string selection = "opening", string[]? deck = null) => new()
    {
        Setup = new()
        {
            Encounter = encounter,
            Deck = deck ?? [nameof(GrandFinale), nameof(GrandFinale), nameof(GrandFinale)],
            Hp = 1000,
            MaxHp = 1000,
            Gold = 77,
        },
        RootSelection = selection,
        SourcePolicyId = PublicContinuationPolicies.ReviewedId,
        SourceDecisionHorizon = 64,
    };

    private static NativeTapeRecipe Recipe(NativeConstructedTapePrior prior, ulong seed = 8001) =>
        prior.Draw(new Rng(seed, NativeConstructedTapePrior.SourceDrawDomain));

    private static Task<NativeRunWorld?> Open(NativeConstructedTapePrior prior,
        NativeTapeRecipe recipe, CancellationToken token = default) =>
        NativeRunWorld.OpenConstructedLabelTapeAsync(prior, recipe,
            NativeLabelTape.ForConstructedPrior(prior, recipe), token);

    private static async Task<DecisionPacket> DetachedRoot(NativeConstructedTapePrior prior, ulong seed = 8001)
    {
        await using var world = await Open(prior, Recipe(prior, seed));
        Assert.NotNull(world);
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }

    [Fact]
    public void DeclaredSetupIsFrozenAndForcedOwnersAreRejectedExplicitly()
    {
        var prior = Prior() with { Setup = Prior().Setup with { Potions = ["FirePotion"], Relics = ["Anchor"] } };
        var frozen = prior.Freeze();
        string identity = frozen.Identity;
        var draw = Recipe(frozen);
        prior.Setup.Deck![0] = "mutated";
        prior.Setup.Potions![0] = "mutated";
        prior.Setup.Relics![0] = "mutated";
        Assert.Equal(identity, frozen.Identity);
        Assert.Equal(draw, Recipe(frozen));
        Assert.Equal(nameof(GrandFinale), frozen.Setup.Deck![0]);
        Assert.Equal("FirePotion", frozen.Setup.Potions![0]);
        Assert.Equal("Anchor", frozen.Setup.Relics![0]);
        Assert.NotEqual(identity, (frozen with { RootSelection = "first_player_turn_2" }).Identity);
        Assert.Equal(80, EncounterCoverage.AllEncounters.Count(e => !e.RequiresEventContext));
        var failure = Assert.Throws<NotSupportedException>(() => Prior("PunchOffEventEncounter").Freeze());
        Assert.Contains("event", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeRelicAcquisitionChangesBaseResourcesBeforeTheCombatEntryAnchor()
    {
        var prior = Prior();
        prior = prior with { Setup = prior.Setup with
        {
            Hp = 20, MaxHp = 40, Gold = 7, Relics = ["GoldenPearl", "NutritiousOyster"],
        } };
        DecisionPacket packet;
        await using (var world = await Open(prior, Recipe(prior)))
        {
            Assert.NotNull(world);
            packet = world.Observe();
            // GoldenPearl gains 150 gold; NutritiousOyster gains 11 max HP and
            // heals that actual gain through the native acquisition commands.
            Assert.Equal(31, world.StartHp);
            Assert.Equal(51, world.StartMaxHp);
            Assert.Equal(31, packet.Observation!.StartHp);
            Assert.Equal(31, packet.Observation.Hp);
            Assert.Equal(51, packet.Observation.MaxHp);
            Assert.Equal(157, packet.Observation.StartGold);
            Assert.Equal(157, packet.Observation.Gold);
            var entry = PublicJson.Read<NativeEntryAssets>(packet.Observation.History
                .Single(e => e.Kind == NativeEntryAssets.EventKind).Detail);
            Assert.Equal((31, 51, 157), (entry.Hp, entry.MaxHp, entry.Gold));
            Assert.Contains(entry.Relics, relic => relic.Id == "GoldenPearl");
            Assert.Contains(entry.Relics, relic => relic.Id == "NutritiousOyster");
            var outcome = await Finish(world);
            Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
            Assert.Equal(31, outcome.HpAtCombatStart);
            Assert.Equal(51, outcome.MaxHpStart);
            Assert.Equal(157, PublicJson.Read<CombatAssetSnapshot>(outcome.PersistentAssetsAtStartJson!).Gold);
        }
        var source = new NativeConstructedTapeSource(packet, prior);
        Assert.Equal(31, source.StartHp);
        Assert.Equal(51, source.StartMaxHp);
        Assert.Equal((20, 40, 7), (source.Prior.Setup.Hp, source.Prior.Setup.MaxHp, source.Prior.Setup.Gold));
    }

    [Fact]
    public async Task MixedUpgradeShuffleAndProposalsDependOnlyOnFrozenPublicInput()
    {
        var prior = Prior(deck:
        [
            "StrikeSilent", "StrikeSilent+", "StrikeSilent", "StrikeSilent+",
            "DefendSilent", "DefendSilent+", "DefendSilent", "DefendSilent+",
        ]);
        // The complete source graph is disposed before either sampler exists.
        var packet = await DetachedRoot(prior);
        AssertConstructedEvidence(packet, 0);
        var entry = PublicJson.Read<NativeEntryAssets>(packet.Observation!.History
            .Single(e => e.Kind == NativeEntryAssets.EventKind).Detail);
        Assert.Equal(4, entry.Deck.Count(card => card.Upgrade == 0));
        Assert.Equal(4, entry.Deck.Count(card => card.Upgrade == 1));
        Assert.Contains(packet.Observation.Hand, card => card.Upgrade == 0);
        Assert.Contains(packet.Observation.Hand, card => card.Upgrade == 1);
        Assert.True(NativeInitialShuffleCondition.TryCreatePublicCombatV5(packet, out var condition, out var reason), reason);
        Assert.Equal(8, condition!.DeckCount);

        var detached = PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet));
        var first = new NativeConstructedTapeSource(packet, prior);
        var second = new NativeConstructedTapeSource(detached, prior);
        Assert.True(first.UsesPrimitiveConditioning, first.ConditioningReason);
        packet.Actions[0] = new(999, "external_mutation");
        packet.Observation.History[0] = new("external_mutation", "not sampler input");
        prior.Setup.Deck![0] = "external_mutation";
        Assert.Equal(PublicJson.Serialize(detached), PublicJson.Serialize(first.Observe()));
        Assert.Equal(first.Prior.Identity, second.Prior.Identity);

        // This is one fixed proposal per sampler, including an inconclusive draw.
        // Neither source seeds nor successful-source selection enter inference.
        async Task<string?> Sample(NativeConstructedTapeSource source)
        {
            try
            {
                await using var sample = await source.SampleWorldAsync(101, 1);
                return PublicJson.Serialize(sample.Observe());
            }
            catch (PosteriorSamplingException) { return null; }
        }
        Assert.Equal(await Sample(first), await Sample(second));
        var firstAudit = Assert.Single(first.ProposalAudit);
        var secondAudit = Assert.Single(second.ProposalAudit);
        Assert.Equal(firstAudit.Recipe, secondAudit.Recipe);
        Assert.Equal(firstAudit.Status, secondAudit.Status);
        Assert.Equal(firstAudit.ConditionedTapeCells, secondAudit.ConditionedTapeCells);
        Assert.Equal(firstAudit.DistinctTapeCells, secondAudit.DistinctTapeCells);
        Assert.Equal(1, firstAudit.ConditionedShuffles);
        Assert.Equal(1, firstAudit.ConditionedHpCount);
        Assert.True(firstAudit.ConditionedTapeCells >= 8);
    }

    [Theory]
    [InlineData("NeowsBones", "constructed_relic_owner_required")]
    [InlineData("LostCoffer", "constructed_relic_owner_required")]
    [InlineData("CallingBell", "constructed_relic_owner_required")]
    [InlineData("ToyBox", "constructed_relic_owner_required")]
    [InlineData("SeaGlass", "constructed_relic_setup_required")]
    public void OwnerAndSelectedModelDependentAcquisitionIsExplicitlyUnsupported(string relic, string reason)
    {
        // Native acquisition requires more than a fresh inventory model. The
        // rejected setup must never be represented as successfully initialized.
        var prior = Prior() with { Setup = Prior().Setup with { Relics = [relic] } };
        var error = Assert.Throws<NotSupportedException>(() => prior.Freeze());
        Assert.StartsWith(reason + ": " + relic, error.Message);
    }

    [Fact]
    public async Task MixedNightmareFallbackRetainsAllFourPinnedUnmatchedProposals()
    {
        // A pinned bounded fallback/accounting diagnostic. These ordinary public
        // rejections do not establish impossible content or a semantic failure.
        var prior = Prior(selection: "first_pending_choice", deck:
        [
            nameof(Nightmare), nameof(Nightmare), nameof(GrandFinale), nameof(GrandFinale),
            nameof(GrandFinale), nameof(GrandFinale), nameof(GrandFinale), nameof(GrandFinale),
        ]);
        var packet = await DetachedRoot(prior, 8001);
        var source = new NativeConstructedTapeSource(packet, prior);
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var sample = await source.SampleWorldAsync(101, 4);
        });
        output.WriteLine("Fixed source draw 8001; evaluation seed 101; proposal budget 4; actual native opener.");
        output.WriteLine("Conditioning reason: " + source.ConditioningReason);
        output.WriteLine(PublicJson.Serialize(source.ProposalAudit));
        Assert.Equal(4, Assert.IsType<PosteriorSamplingException>(failure).Attempts);
        Assert.False(source.UsesPrimitiveConditioning);
        Assert.Equal("entry_hook_not_certified:GrandFinale.ShouldPlay", source.ConditioningReason);
        Assert.Equal(new[] { 1, 2, 3, 4 }, source.ProposalAudit.Select(audit => audit.Attempt));
        Assert.All(source.ProposalAudit, audit =>
        {
            Assert.Equal("public_constraint_mismatch", audit.Status);
            Assert.Contains("combat_fact:CardDrawn", audit.Detail);
            Assert.Equal(0, audit.ConditionedShuffles);
            Assert.Equal(0, audit.ConditionedHpCount);
            Assert.Equal(0, audit.ConditionedTapeCells);
            Assert.True(audit.DistinctTapeCells > 0);
        });
    }

    [Fact]
    public async Task NightmarePendingChoiceRetainsNativeCoroutineWithoutLegacyWhitelist()
    {
        Assert.DoesNotContain(nameof(Nightmare), NativeBeliefCertificate.Cards);
        var prior = Prior(selection: "first_pending_choice", deck: Enumerable.Repeat(nameof(Nightmare), 5).ToArray());
        prior = prior with { Setup = prior.Setup with { Hp = 30, MaxHp = 30 } };
        var packet = await DetachedRoot(prior);
        AssertConstructedEvidence(packet, 0);
        Assert.Equal("card_choice", packet.Status);
        Assert.Equal(nameof(Nightmare), packet.Observation!.Choice!.Source);
        var source = new NativeConstructedTapeSource(packet, prior);
        Assert.True(source.UsesPrimitiveConditioning, source.ConditioningReason);
        await using var world = Assert.IsType<NativeRunWorld>(await source.SampleWorldAsync(101, 4));
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(world.Observe()));
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        Assert.NotSame(world.NativeRun, fork.NativeRun);
        string unchanged = PublicJson.Serialize(world.Observe());
        var action = packet.Actions.First(a => a.Kind == "choose");
        await fork.StepAsync(action);
        Assert.Equal(unchanged, PublicJson.Serialize(world.Observe()));
        await world.StepAsync(action);
        await world.StepAsync(world.Observe().Actions.Single(a => a.Kind == "end_turn"));
        await fork.StepAsync(fork.Observe().Actions.Single(a => a.Kind == "end_turn"));
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(7, world.Observe().Observation!.Hand.Count(card => card.Id == nameof(Nightmare)));
        var outcome = await Finish(world, endTurnsOnly: true);
        Assert.Equal(TerminalKind.Loss, outcome.TerminalKind);
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(await Finish(fork, endTurnsOnly: true)));
    }

    [Theory]
    [InlineData("KnowledgeDemonBoss", "first_player_turn_2", 1, 2)]
    [InlineData("AeonglassBoss", "first_player_turn_3", 2, 3)]
    public async Task LaterBossRootsOwnTheirNativeActAndSettleThroughIndependentForks(
        string encounter, string selection, int act, int turn)
    {
        var prior = Prior(encounter, selection);
        var packet = await DetachedRoot(prior);
        AssertConstructedEvidence(packet, act);
        Assert.Equal(turn, packet.Observation!.Turn);
        var source = new NativeConstructedTapeSource(packet, prior);
        await using var world = Assert.IsType<NativeRunWorld>(await source.SampleWorldAsync(101, 4));
        Assert.Equal(encounter, world.Encounter);
        Assert.Equal(RoomType.Boss, world.NativeCombatRoom.RoomType);
        Assert.Equal(act, world.NativeRun.CurrentActIndex);
        Assert.IsType<Hive>(world.NativeRun.Acts[1]);
        Assert.IsType<Glory>(world.NativeRun.Acts[2]);
        Assert.Same(world.NativeRun, world.NativeCombatRoom.Engine.State.RunState);
        await using var fork = Assert.IsType<NativeRunWorld>(await world.ForkForContinuationAsync());
        Assert.NotSame(world.NativeRun, fork.NativeRun);
        Assert.NotSame(world.NativeCombatRoom, fork.NativeCombatRoom);
        if (act == 2)
        {
            var originalPower = Assert.Single(world.NativeCombatRoom.Engine.State.Enemies.Single().Powers
                .OfType<WitheringPresencePower>());
            var forkPower = Assert.Single(fork.NativeCombatRoom.Engine.State.Enemies.Single().Powers
                .OfType<WitheringPresencePower>());
            Assert.NotSame(originalPower, forkPower);
            Assert.Same(world.NativeRun.Players.Single().Creature, originalPower.Target);
            Assert.Same(fork.NativeRun.Players.Single().Creature, forkPower.Target);
        }
        string unchanged = PublicJson.Serialize(world.Observe());
        var forkOutcome = await Finish(fork);
        Assert.Equal(unchanged, PublicJson.Serialize(world.Observe()));
        var outcome = await Finish(world);
        Assert.Equal(PublicJson.Serialize(outcome), PublicJson.Serialize(forkOutcome));
        Assert.Equal(TerminalKind.Win, outcome.TerminalKind);
        Assert.True(outcome.SettlementComplete);
        Assert.True(outcome.HpEventDiagnosticsComplete);
        Assert.True(outcome.ResourceProvenanceComplete);
        Assert.Equal((double)outcome.HpAfterSettlement!, outcome.HpAtCombatStart
            - outcome.CumulativeHpDamage!.Value + outcome.HealingReceived!.Value + outcome.OtherHpAdjustment!.Value);
        Assert.DoesNotContain(world.SourceTrace, item => item.Kind == "reward_choice");
        if (act == 1)
        {
            Assert.NotEmpty(world.NativeCombatRoom.GeneratedRewards);
            Assert.NotEmpty(world.StandardRewardOpportunities);
        }
        else
        {
            Assert.Empty(world.NativeCombatRoom.GeneratedRewards);
            Assert.Empty(world.StandardRewardOpportunities);
        }
    }

    [Fact]
    public async Task MissingSelectedRootAndHorizonRemainAbsentButCancellationPropagates()
    {
        var opening = Prior();
        var recipe = Recipe(opening);
        await using var first = await Open(opening, recipe);
        await using var indexed = await Open(opening with { RootSelection = "decision_index", DecisionIndex = 0 }, recipe);
        Assert.NotNull(first);
        Assert.NotNull(indexed);
        Assert.Equal(PublicJson.Serialize(first.Observe()), PublicJson.Serialize(indexed.Observe()));
        var noChoice = opening with { RootSelection = "first_pending_choice" };
        Assert.Null(await Open(noChoice, Recipe(noChoice)));
        var beyondHorizon = Prior("KnowledgeDemonBoss", "first_player_turn_2") with { SourceDecisionHorizon = 1 };
        Assert.Null(await Open(beyondHorizon, Recipe(beyondHorizon)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(opening, recipe, cancelled.Token));
        using var during = new CancellationTokenSource();
        await using var active = await Open(opening, recipe, during.Token);
        Assert.NotNull(active);
        var action = active.Observe().Actions.Single(a => a.Kind == "end_turn");
        during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.StepAsync(action));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("cancelled")]
    [InlineData("engine_error")]
    public async Task SamplerDistinguishesAbsentMassFromFailedComputation(string failure)
    {
        var prior = Prior();
        var packet = await DetachedRoot(prior);
        int opened = 0;
        Exception exception = failure == "cancelled"
            ? new OperationCanceledException("constructed cancellation")
            : new InvalidOperationException("constructed engine failure");
        Task<NativeRunWorld?> OpenFailure(NativeConstructedTapePrior _, NativeTapeRecipe __,
            NativeLabelTape ___, CancellationToken ____)
        {
            opened++;
            return failure == "absent" ? Task.FromResult<NativeRunWorld?>(null)
                : Task.FromException<NativeRunWorld?>(exception);
        }
        var source = new NativeConstructedTapeSource(packet, prior, enableConditioning: false,
            nativeOpenerForTests: OpenFailure);
        if (failure == "absent")
        {
            await Assert.ThrowsAsync<PosteriorSamplingException>(() => source.SampleWorldAsync(101, 3));
            Assert.Equal(3, opened);
            Assert.Equal(3, source.ProposalAudit.Length);
            Assert.All(source.ProposalAudit, audit => Assert.Equal("absent_under_declared_source_horizon", audit.Status));
        }
        else
        {
            Assert.Same(exception, await Record.ExceptionAsync(() => source.SampleWorldAsync(101, 3)));
            Assert.Equal(1, opened);
            Assert.Equal(failure == "cancelled" ? "computation_cancelled" : "proposal_engine_error",
                Assert.Single(source.ProposalAudit).Status);
        }
    }

    [Fact]
    public async Task UnsupportedStartupFallsBackToWholeNativeReplayWithoutRejectingTheRoot()
    {
        // Aeonglass's native startup power owner is outside the shuffle certificate.
        var prior = Prior("AeonglassBoss");
        var packet = await DetachedRoot(prior);
        var independentlyGenerated = await DetachedRoot(prior, 8002);
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(independentlyGenerated));
        var source = new NativeConstructedTapeSource(packet, prior);
        var independentSource = new NativeConstructedTapeSource(independentlyGenerated, prior);
        Assert.False(source.UsesPrimitiveConditioning);
        Assert.False(string.IsNullOrWhiteSpace(source.ConditioningReason));
        await using var sample = await source.SampleWorldAsync(101, 1);
        Assert.Equal(PublicJson.Serialize(packet), PublicJson.Serialize(sample.Observe()));
        var audit = Assert.Single(source.ProposalAudit);
        Assert.Equal("accepted", audit.Status);
        Assert.Equal(0, audit.ConditionedShuffles);
        Assert.Equal(0, audit.ConditionedHpCount);
        Assert.Equal(0, audit.ConditionedTapeCells);
        await using var independentSample = await independentSource.SampleWorldAsync(101, 1);
        Assert.Equal(PublicJson.Serialize(sample.Observe()), PublicJson.Serialize(independentSample.Observe()));
        var independentAudit = Assert.Single(independentSource.ProposalAudit);
        Assert.Equal(audit.Recipe, independentAudit.Recipe);
        Assert.Equal(audit.Status, independentAudit.Status);
    }

    private static void AssertConstructedEvidence(DecisionPacket packet, int act)
    {
        PublicEvidenceInput.Validate(packet);
        Assert.Equal(PublicRunEvidence.CompleteMapVersion, packet.PublicEvidence!.SchemaVersion);
        Assert.False(packet.PublicEvidence.CompleteFromRunStart);
        Assert.Equal(PublicEvidenceGapReason.RunStartNotObserved,
            Assert.IsType<PublicEvidenceGap>(packet.PublicEvidence.Events[0].Payload).Reason);
        var context = packet.Observation!.RunContext;
        Assert.NotNull(context);
        Assert.Equal(act, context.ActIndex);
        Assert.False(context.CompleteFromRunStart);
        Assert.Null(context.CombatEntryIndex);
    }

    private static async Task<RolloutOutcome> Finish(ITeacherWorld world, bool endTurnsOnly = false)
    {
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        int lastTurn = 0;
        for (int i = 0; i < 96 && world.Observe().Status != "terminal_settled"; i++)
        {
            var packet = world.Observe();
            lastTurn = packet.Observation!.Turn;
            await world.StepAsync(endTurnsOnly ? packet.Actions.Single(a => a.Kind == "end_turn") : policy.Choose(packet));
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        return await world.RecordSettledAsync(endTurnsOnly ? "constructed-test-end-turn" : policy.Id, lastTurn);
    }
}
