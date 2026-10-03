using System.Reflection;
using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Content.Acts;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativePublicRubyFormationTests(ITestOutputHelper output)
{
    private static NativeConstructedTapePrior Prior(string selection = "opening") => new()
    {
        Setup = new()
        {
            Encounter = "RubyRaiders", Gold = 110, Potions = ["FirePotion", "BlockPotion"],
            Deck = ["StrikeSilent", "StrikeSilent+", "StrikeSilent", "DefendSilent", "DefendSilent+", "DefendSilent",
                "Neutralize+", "Survivor", "DeadlyPoison", "BladeDance+", "DaggerThrow", "Backflip", "Acrobatics",
                "DodgeAndRoll", "Dash", "PoisonedStab"],
        },
        SourcePolicyId = PublicContinuationPolicies.ReviewedId, SourceDecisionHorizon = 48, RootSelection = selection,
    };

    private static async Task<DecisionPacket> Root(NativeConstructedTapePrior prior)
    {
        var recipe = prior.Draw(new Rng(44101, NativeConstructedTapePrior.SourceDrawDomain));
        await using var source = await NativeRunWorld.OpenConstructedLabelTapeAsync(prior, recipe,
            NativeLabelTape.ForConstructedPrior(prior, recipe));
        Assert.NotNull(source);
        return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(source.Observe()));
    }

    [Fact]
    public void ExhaustiveFiniteNativeWorldsHaveExactUnequalMassAndCompleteConditionalSupport()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = new Overgrowth().MonsterEncounterCandidates.Single(item => item.IdEntry == "RUBY_RAIDERS_NORMAL");
        const int bits = 3, domain = 1 << bits, lowBits = 64 - bits;
        var masses = new Dictionary<string, int>(StringComparer.Ordinal);
        for (ulong a = 0; a < domain; a++)
        for (ulong b = 0; b < domain; b++)
        for (ulong c = 0; c < domain; c++)
        {
            var words = new Queue<ulong>([a << lowBits, b << lowBits, c << lowBits]);
            using var scope = LabelRandomScope.Enter(_ => words.Dequeue());
            string key = string.Join(",", encounter.CreateMonsters(new Rng(17)).Select(item => item.Monster.GetType().Name));
            masses[key] = masses.GetValueOrDefault(key) + 1; Assert.Empty(words);
        }
        Assert.Equal(60, masses.Count); Assert.Equal(512, masses.Values.Sum());
        Assert.True(masses.Values.Distinct().Count() > 1); // A uniform 1/60 shortcut is observably wrong.
        Assert.Equal(60, NativeRubyFormationCatalog.Tickets.Count);
        foreach (var ticket in NativeRubyFormationCatalog.Tickets)
        {
            var factors = ticket.Choices.Select((choice, i) => ConditionalShuffleProposal.Factor(5 - i, choice, bits)).ToArray();
            var expectedMass = new ShuffleRational(masses[string.Join(",", ticket.Roster)], domain * domain * domain);
            Assert.Equal(expectedMass, NativeRubyFormationPlan.Mass(ticket.Roster, bits));
            var seen = new HashSet<string>();
            for (ulong a = 0; a < factors[0].BucketSize; a++)
            for (ulong b = 0; b < factors[1].BucketSize; b++)
            for (ulong c = 0; c < factors[2].BucketSize; c++)
            {
                ulong[] offsets = [a, b, c]; var auxiliary = new Queue<ulong>();
                for (int i = 0; i < 3; i++)
                {
                    if (factors[i].BucketSize > 1)
                    {
                        ulong threshold = unchecked(0UL - factors[i].BucketSize) % factors[i].BucketSize;
                        ulong word = offsets[i];
                        while (word < threshold) word += factors[i].BucketSize;
                        auxiliary.Enqueue(word);
                    }
                    // Coarse finite native world has exactly these high bits; discarded
                    // low bits are tested separately against the actual 53-bit primitive.
                    auxiliary.Enqueue(0);
                }
                var plan = NativeRubyFormationPlan.Create(ticket.Roster, auxiliary.Dequeue, bits);
                Assert.Empty(auxiliary); Assert.True(seen.Add(string.Join(",", plan.RawWords)));
                Assert.Equal(expectedMass, plan.NativeToProposalRatio); Assert.Equal(expectedMass, plan.Envelope);
                Assert.True(plan.AcceptCorrection(() => throw new InvalidOperationException("Root-constant mass cancels")));
                var proposed = new Queue<ulong>(plan.RawWords);
                using var scope = LabelRandomScope.Enter(_ => proposed.Dequeue());
                var rng = new Rng(97);
                Assert.Equal(ticket.Roster, encounter.CreateMonsters(rng).Select(item => item.Monster.GetType().Name));
                Assert.Equal(3, rng.Counter); Assert.Empty(proposed);
            }
            Assert.Equal(masses[string.Join(",", ticket.Roster)], seen.Count);
            // Actual 53-bit endpoints with every combination of discarded low-bit endpoints.
            foreach (bool upper in new[] { false, true })
            for (int lowMask = 0; lowMask < 8; lowMask++)
            {
                var auxiliary = new Queue<ulong>();
                for (int i = 0; i < 3; i++)
                {
                    ulong width = ConditionalShuffleProposal.Factor(5 - i, ticket.Choices[i]).BucketSize;
                    ulong threshold = unchecked(0UL - width) % width;
                    ulong word = upper ? width - 1 : width;
                    while (word < threshold) word += width;
                    auxiliary.Enqueue(word); auxiliary.Enqueue((lowMask & (1 << i)) == 0 ? 0UL : 2047UL);
                }
                var plan = NativeRubyFormationPlan.Create(ticket.Roster, auxiliary.Dequeue);
                Assert.Empty(auxiliary);
                var proposed = new Queue<ulong>(plan.RawWords);
                using var scope = LabelRandomScope.Enter(_ => proposed.Dequeue());
                Assert.Equal(ticket.Roster, encounter.CreateMonsters(new Rng(77)).Select(item => item.Monster.GetType().Name));
                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(plan.Factors[i].BucketStart + (upper ? plan.Factors[i].BucketSize - 1 : 0), plan.RawWords[i] >> 11);
                    Assert.Equal((lowMask & (1 << i)) == 0 ? 0UL : 2047UL, plan.RawWords[i] & 2047);
                }
            }
        }
    }

    [Theory]
    [InlineData("opening")]
    [InlineData("first_player_turn_2")]
    public async Task MixedDeckNativeReplayUsesDetachedStartupAndIndependentProposalRecipes(string selection)
    {
        var prior = Prior(selection); var root = await Root(prior); string before = PublicJson.Serialize(root);
        Assert.True(NativePublicRubyFormationCondition.TryCreateConstructed(root, prior, out var condition, out var reason), reason);
        Assert.Equal(new[] { "BruteRubyRaider", "TrackerRubyRaider", "CrossbowRubyRaider" }, condition!.Roster);
        var changedCurrentSnapshot = root with { Observation = root.Observation! with { Enemies = [] } };
        Assert.True(NativePublicRubyFormationCondition.TryCreateConstructed(changedCurrentSnapshot, prior, out var retained, out reason), reason);
        Assert.Equal(condition.Roster, retained!.Roster);
        Assert.Equal(before, PublicJson.Serialize(root));
        var source = new NativeConstructedTapeSource(root, prior);
        Assert.Equal(NativeConstructedTapeSource.RubyImplementationVersion, NativeConstructedTapeSource.ImplementationFor(prior));
        Assert.True(source.UsesRubyFormationConditioning); Assert.Equal(NativeConstructedTapeSource.RubyProfile, source.PosteriorProfile);
        foreach (ulong seed in new ulong[] { 74101, 74102 })
        {
            try
            {
                await using var sample = await source.SampleWorldAsync(seed, 16);
                Assert.Equal(before, PublicJson.Serialize(sample.Observe()));
                await using var fork = await sample.ForkForContinuationAsync();
                Assert.Equal(before, PublicJson.Serialize(fork.Observe()));
            }
            catch (PosteriorSamplingException) when (selection != "opening") { }
        }
        output.WriteLine(PublicJson.Serialize(new { selection, prior = prior.Identity,
            attempts = source.ProposalAudit.Select(row => new { row.SampleCall, row.Attempt, row.Status, row.Detail, row.ConditionedTapeCells }) }));
        Assert.All(source.ProposalAudit, row =>
        {
            Assert.DoesNotContain("initial monster roster", row.Detail ?? "");
            Assert.DoesNotContain("event 12 (combat_fact:IntentPublished)", row.Detail ?? "");
            Assert.DoesNotContain("event 13 (combat_fact:IntentPublished)", row.Detail ?? "");
            Assert.DoesNotContain("event 14 (combat_fact:IntentPublished)", row.Detail ?? "");
        });
        Assert.False(new NativeConstructedTapeSource(root, prior, enableConditioning: false).UsesRubyFormationConditioning);
    }

    [Theory]
    [InlineData("missing_evidence")]
    [InlineData("wrong_encounter")]
    [InlineData("gap")]
    [InlineData("duplicate_type")]
    [InlineData("wrong_slot")]
    public async Task MissingCertificateDisablesAccelerationWithoutChangingPrior(string change)
    {
        var prior = Prior(); var root = await Root(prior);
        if (change == "missing_evidence") root = root with { PublicEvidence = null };
        else if (change == "wrong_encounter") prior = prior with { Setup = prior.Setup with { Encounter = "SlimesWeak" } };
        else
        {
            var evidence = root.PublicEvidence!; var events = evidence.Events.ToArray();
            int first = Array.FindIndex(events, e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.IntentPublished });
            var intent = (PublicCombatFact)events[first + 1].Payload;
            PublicEvidencePayload replacement = change == "gap" ? new PublicEvidenceGap(PublicEvidenceGapReason.Interrupted)
                : new PublicCombatFact(PublicCombatFactKind.IntentPublished,
                    targetSlot: change == "wrong_slot" ? 7 : intent.TargetSlot,
                    model: change == "duplicate_type" ? ((PublicCombatFact)events[first].Payload).Model : intent.Model,
                    intents: intent.Intents);
            events[first + 1] = new(events[first + 1].EventOrdinal, events[first + 1].OwnerOrdinal, replacement);
            if (change == "gap") events = events.TakeWhile(e => e.Payload is not PublicCombatDecision).ToArray();
            root = root with { PublicEvidence = new(evidence.SchemaVersion, evidence.CompleteFromRunStart, events.ToImmutableArray()) };
        }
        Assert.False(NativePublicRubyFormationCondition.TryCreateConstructed(root, prior, out var condition, out _));
        Assert.Null(condition);
    }

    [Theory]
    [InlineData("wrong_owner")]
    [InlineData("wrong_room")]
    [InlineData("wrong_stream")]
    [InlineData("used_stream")]
    [InlineData("same_named_encounter")]
    [InlineData("repeat")]
    public async Task NativeOwnerPredicatesRejectUncertifiedFactoriesBeforeDrawing(string change)
    {
        var root = await Root(Prior());
        Assert.True(NativePublicRubyFormationCondition.TryCreateConstructed(root, Prior(), out var condition, out _));
        var (run, room, context) = Context(); int sampled = 0;
        var proposal = new NativePublicRubyFormationProposal(condition!, () => { sampled++; return ulong.MaxValue; },
            (words, _, _) => { var queue = new Queue<ulong>(words); return LabelRandomScope.Enter(_ => queue.Dequeue()); });
        proposal.AttachHypotheticalRun(run);
        proposal.CombatEntering(0, PublicJson.Read<NativeEntryAssets>(condition!.EntryJson), run.Rng.Shuffle); run.PushRoom(room);
        switch (change)
        {
            case "wrong_owner": context = context with { Run = new RunState("foreign") }; break;
            case "wrong_room": context = context with { Room = new CombatRoom(() => Array.Empty<MonsterModel>()) }; break;
            case "wrong_stream": context = context with { Rng = run.Rng.Niche }; break;
            case "used_stream": context.Rng.NextInt(2); break;
            case "same_named_encounter": context = context with { Encounter = new Overgrowth().MonsterEncounterCandidates.Single(e => e.IdEntry == "RUBY_RAIDERS_NORMAL") }; break;
            case "repeat": using (proposal.BeginFormation(context)) { context.Rng.NextInt(5); context.Rng.NextInt(4); context.Rng.NextInt(3); } sampled = 0; break;
        }
        Assert.Throws<InvalidOperationException>(() => proposal.BeginFormation(context)); Assert.Equal(0, sampled);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("wrong_draw_stream")]
    [InlineData("missing_word")]
    [InlineData("extra_draw")]
    public async Task ExactTapeGuardsRetainFailureMassInsteadOfAllowingAnAcceptedIncompletePlan(string change)
    {
        var root = await Root(Prior());
        Assert.True(NativePublicRubyFormationCondition.TryCreateConstructed(root, Prior(), out var condition, out _));
        var (run, room, context) = Context(); var tape = new NativeLabelTape(new(11, 22, 33, 0, 0));
        var force = typeof(NativeLabelTape).GetMethod("ForcePrefixWords", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<IReadOnlyList<ulong>, Rng, string, IDisposable>>(tape);
        var proposal = new NativePublicRubyFormationProposal(condition!, new Rng(5454).NextUnsignedLong, force);
        proposal.AttachHypotheticalRun(run);
        proposal.CombatEntering(0, PublicJson.Read<NativeEntryAssets>(condition!.EntryJson), run.Rng.Shuffle); run.PushRoom(room);
        if (change == "alias")
        {
            var snap = context.Rng.ToSerializable();
            typeof(NativeLabelTape).GetMethod("Word", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<LabelRandomState, ulong>>(tape)(new(snap.state0, snap.state1, snap.state2, snap.state3));
        }
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var forced = proposal.BeginFormation(context);
            if (change == "wrong_draw_stream") run.Rng.Niche.NextInt(5);
            else
            {
                context.Rng.NextInt(5); context.Rng.NextInt(4);
                if (change != "missing_word") context.Rng.NextInt(3);
                if (change == "extra_draw") context.Rng.NextInt(2);
            }
        });
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
    }

    [Fact]
    public void DisabledObserverPreservesNativeFullStateAndCallbackDrawsStayOffTape()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var encounter = new Overgrowth().MonsterEncounterCandidates.Single(item => item.IdEntry == "RUBY_RAIDERS_NORMAL");
        foreach (ulong seed in new ulong[] { 0, 1, 1234, ulong.MaxValue })
        {
            var ordinary = new Rng(seed); var expected = encounter.CreateMonsters(ordinary); var disabled = new Rng(seed);
            using (LabelRubyRaidersScope.Enter(_ => null))
                Assert.Equal(expected.Select(item => item.Monster.GetType()), encounter.CreateMonsters(disabled).Select(item => item.Monster.GetType()));
            Assert.Equal(PublicJson.Serialize(ordinary.ToSerializable()), PublicJson.Serialize(disabled.ToSerializable()));
            Assert.Equal(3, ordinary.Counter);
        }
        int intercepted = 0, callbacks = 0;
        using var tape = LabelRandomScope.Enter(_ => { intercepted++; return 0; });
        using var callback = LabelRubyRaidersScope.Enter(context =>
        { callbacks++; Assert.Equal(0, context.Rng.Counter); new Rng(87).NextInt(5); return null; });
        encounter.CreateMonsters(new Rng(456)); Assert.Equal(1, callbacks); Assert.Equal(3, intercepted);
    }

    private static (RunState Run, CombatRoom Room, LabelRubyRaidersFormationContext Context) Context()
    {
        NaturalSourceCollector.InitializeNativeModels();
        var run = new RunState("owned-ruby-formation", new Overgrowth(), 10);
        run.AddPlayer(Player.CreateForNewRun(ModelDb.Character<Silent>(), run));
        var encounter = run.Act.MonsterEncounterCandidates.Single(item => item.IdEntry == "RUBY_RAIDERS_NORMAL");
        var rng = new Rng(7654);
        var room = new CombatRoom(() => (encounter, encounter.CreateMonsters(rng)), RoomType.Monster);
        return (run, room, new(run, room, encounter, rng, rng));
    }
}
