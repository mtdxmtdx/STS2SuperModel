using System.Collections.Immutable;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativePublicPoisonBlockDrawClosureTests(ITestOutputHelper output)
{
    private sealed class Recorder(CombatSession session)
    {
        internal NativeEntryAssets Entry { get; } = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        private PublicRunEvidenceRecorder? _recorder;
        private long _owner;
        internal DecisionPacket Record(DecisionPacket packet)
        {
            if (_recorder is null)
            {
                var assets = new PublicEvidenceAssets(Entry.Hp, Entry.MaxHp, Entry.Gold, Entry.Deck, Entry.Relics,
                    Entry.Potions.ToImmutableArray(), Entry.MaxEnergy, Entry.PotionSlots, Entry.OrbSlots, Entry.CardRemovalsUsed);
                _recorder = new(new("Silent", 10, assets));
                _owner = _recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 1);
            }
            packet = packet with { Observation = packet.Observation! with
            {
                History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(Entry)),
                    .. packet.Observation.History.Skip(1)],
            } };
            _recorder.ObserveCombatDecision(_owner, packet);
            return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet with { PublicEvidence = _recorder.Capture() }));
        }
    }

    private static async Task<(DecisionPacket Root, NativeEntryAssets Entry)> NativeHistory(string id, bool upgraded, string seed, bool verify)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: seed,
            Deck: [.. Enumerable.Repeat(id + (upgraded ? "+" : ""), 6),
                .. Enumerable.Range(0, 6).Select(i => "DefendSilent" + (i % 2 == 0 ? "+" : ""))],
            Enemy: "Nibbit", EnemyHp: 1000));
        var recorder = new Recorder(session); var current = recorder.Record(session.Observe());
        string shuffle = RngState(session.State.RunState.Rng.Shuffle);
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == id)));
        if (verify)
        {
            Assert.Equal(shuffle, RngState(session.State.RunState.Rng.Shuffle));
            Assert.Equal(5, current.Observation!.DrawCount);
            Assert.Equal(id == "LegSweep" ? 1 : 2, current.Observation.Energy);
            if (id == "LegSweep")
            {
                Assert.Equal(upgraded ? 14 : 11, current.Observation.Block);
                Assert.Contains(current.Observation.Enemies.Single().Powers, p => p.Id == "WeakPower" && p.Amount == (upgraded ? 3 : 2));
                Assert.Equal(id, Assert.Single(current.Observation.Discard).Id);
            }
            else
            {
                Assert.Contains(current.Observation.Powers, p => p.Id == "NoxiousFumesPower" && p.Amount == (upgraded ? 3 : 2));
                Assert.DoesNotContain(current.Observation.Enemies.Single().Powers, p => p.Id == "PoisonPower");
                Assert.Empty(current.Observation.Discard);
                Assert.Contains(current.PublicEvidence!.Events, e => e.Payload is PublicCombatFact
                    { FactKind: PublicCombatFactKind.CardPlayed, ResultPile: PublicCardPile.None } f && f.Cards.Single().Id == id);
            }
        }
        for (int turn = 0; turn < 2; turn++)
        {
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
            if (verify && id == "NoxiousFumes")
            {
                int amount = upgraded ? 3 : 2;
                Assert.Equal(turn == 0 ? 1000 : 1000 - amount, current.Observation!.Enemies.Single().Hp);
                Assert.Contains(current.Observation.Enemies.Single().Powers,
                    p => p.Id == "PoisonPower" && p.Amount == (turn == 0 ? amount : 2 * amount - 1));
            }
        }
        return (current, recorder.Entry);
    }

    private static string RngState(Rng rng) => JsonSerializer.Serialize(rng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });

    [Theory]
    [InlineData("LegSweep", false)]
    [InlineData("LegSweep", true)]
    [InlineData("NoxiousFumes", false)]
    [InlineData("NoxiousFumes", true)]
    public async Task NativeBlockPoisonAndPowerRemovalPreserveIndependentConditionalDrawProposals(string id, bool upgraded)
    {
        var fixture = await NativeHistory(id, upgraded, "poison-block-source:" + id, true);
        string before = PublicJson.Serialize(fixture.Root);
        var prefix = NativePublicCombatPrefixCondition.Create(fixture.Root);
        var reshuffles = NativePublicReshuffleCondition.Create(fixture.Root);
        Assert.Equal("nosl.public-first-draw-cycle.v11", prefix.Combats[0].DrawPrefix!.CertificateVersion);
        Assert.Equal(12, prefix.Combats[0].Shuffle!.DrawPrefixKeys.Length);
        Assert.Equal("first_reshuffle", prefix.Combats[0].DrawPrefix!.StopReason);
        Assert.Equal("observed_prefix_complete", reshuffles.CombatAudits[0].StopReason);
        var target = Assert.Single(reshuffles.Combats[0].Targets);
        Assert.Equal(5, target.DrawPrefixKeys.Count);
        Assert.Equal(id == "NoxiousFumes" ? 11 : 12, target.PoolKeys.Count);
        var facts = fixture.Root.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicCombatFact>().ToArray();
        Assert.Equal(facts.SkipWhile(f => f.FactKind != PublicCombatFactKind.Shuffled)
            .Where(f => f.FactKind == PublicCombatFactKind.CardDrawn).Select(f => NativePublicDrawKey.From(f.Cards.Single())), target.DrawPrefixKeys);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var random = new Rng((ulong)(85001 + attempt), "poison-block-independent-proposals");
            IDisposable Force(IReadOnlyList<ulong> words)
            {
                var queue = new Queue<ulong>(words);
                return LabelRandomScope.Enter(_ => queue.Dequeue());
            }
            var first = new NativePublicCombatPrefixProposal(prefix, random.NextUnsignedLong, (words, _) => Force(words));
            var later = new NativePublicReshuffleProposal(reshuffles, random.NextUnsignedLong, (words, _, _) => Force(words));
            bool attached = false;
            using var boundary = later.EnterScope();
            using var scope = LabelRandomScope.Enter(state => new MegaRandom(new SerializableRng
            { state0 = state.State0, state1 = state.State1, state2 = state.State2, state3 = state.State3 }).NextULong(),
                (rng, cards) => later.BeginShuffle(rng, cards) ?? first.BeginShuffle(rng, cards), context =>
                {
                    if (!attached)
                    {
                        var run = (RunState)context.Creature.CombatState!.RunState;
                        first.AttachHypotheticalRun(run); first.CombatEntering(0, fixture.Entry, run.Rng.Shuffle);
                        later.AttachHypotheticalRun(run); later.CombatEntering(0, fixture.Entry, run.Rng.Shuffle);
                        attached = true;
                    }
                    return first.BeginMonsterHp(context);
                });
            var replay = await NativeHistory(id, upgraded, $"poison-block-proposed:{id}:{attempt}", false);
            first.ValidateCompletion(); later.ValidateCompletion();
            Assert.True(first.NativeToProposalRatio.Numerator > 0);
            Assert.True(later.NativeToProposalRatio.Numerator > 0);
            _ = first.AcceptCorrection(random.NextUnsignedLong);
            _ = later.AcceptCorrection(random.NextUnsignedLong);
            Assert.Equal(before, PublicJson.Serialize(replay.Root));
            Assert.Equal(before, PublicJson.Serialize(fixture.Root));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StackedNoxiousFumesAppliesOnlyAtOwnerSideStartToEveryNativeEnemy(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "noxious-stacked-two-enemies",
            Deck: [.. Enumerable.Repeat("NoxiousFumes" + (upgraded ? "+" : ""), 8), .. Enumerable.Repeat("DefendSilent", 4)],
            Enemies: ["Nibbit", "Nibbit"], EnemyHp: 1000));
        var recorder = new Recorder(session); var current = recorder.Record(session.Observe());
        for (int i = 0; i < 2; i++)
            current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
                && current.Observation!.Hand[a.Slot].Id == "NoxiousFumes")));
        int amount = upgraded ? 6 : 4;
        Assert.Contains(current.Observation!.Powers, p => p.Id == "NoxiousFumesPower" && p.Amount == amount);
        Assert.All(current.Observation.Enemies, e => Assert.DoesNotContain(e.Powers, p => p.Id == "PoisonPower"));
        for (int turn = 0; turn < 2; turn++)
        {
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
            Assert.All(current.Observation!.Enemies, e =>
            {
                Assert.Equal(turn == 0 ? 1000 : 1000 - amount, e.Hp);
                Assert.Contains(e.Powers, p => p.Id == "PoisonPower" && p.Amount == (turn == 0 ? amount : amount * 2 - 1));
            });
        }
        var reshuffles = NativePublicReshuffleCondition.Create(current);
        Assert.Equal("observed_prefix_complete", reshuffles.CombatAudits[0].StopReason);
        Assert.Equal(10, Assert.Single(reshuffles.Combats[0].Targets).PoolKeys.Count);
    }

    [Fact]
    public async Task NoxiousDoesNotAdmitAnUnreviewedPoisonAmplifier()
    {
        var root = (await NativeHistory("NoxiousFumes", false, "noxious-amplifier-guard", true)).Root;
        var played = root.PublicEvidence!.Events.First(e => e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardPlayed });
        var modified = root with { PublicEvidence = new(root.PublicEvidence.SchemaVersion, root.PublicEvidence.CompleteFromRunStart,
            root.PublicEvidence.Events.SetItem((int)played.EventOrdinal, new(played.EventOrdinal, played.OwnerOrdinal,
                new PublicCombatFact(PublicCombatFactKind.PowerChanged, targetSlot: -2, model: "AccelerantPower", amount: 1)))) };
        string before = PublicJson.Serialize(modified);
        var first = NativePublicCombatPrefixCondition.Create(modified).Combats[0];
        Assert.Equal("draw_cycle_power_not_certified:AccelerantPower", first.DrawPrefix!.StopReason);
        Assert.Equal(7, first.Shuffle!.DrawPrefixKeys.Length);
        Assert.Equal(played.EventOrdinal - 1, first.DrawPrefix.ThroughEventOrdinal);
        Assert.Equal(0, NativePublicReshuffleCondition.Create(modified).EligibleShuffleCount);
        Assert.Equal(before, PublicJson.Serialize(modified));
    }

    [Fact]
    public async Task SameInspectedRootCrossesLegSweepAndNoxiousAndReplaysAllPublicEvidence()
    {
        var prior = new NativeTapePrior
        {
            SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
            Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
                OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
                PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
                PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
        };
        // Same already-inspected diagnostic recipe, never a seed used for inference.
        var recipe = prior.Draw(new Rng(24210, "nosl-native-tape-source-draw-v1"));
        await using var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(source);
        var root = source.Observe(); string before = PublicJson.Serialize(root);
        var prefix = NativePublicCombatPrefixCondition.Create(root);
        var reshuffles = NativePublicReshuffleCondition.Create(root);
        foreach (var (index, id) in new[] { (0, "LegSweep"), (1, "LegSweep"), (2, "NoxiousFumes") })
        {
            var input = prefix.Combats[index]; var audit = reshuffles.CombatAudits[index];
            var played = root.PublicEvidence!.Events.First(e => e.OwnerOrdinal == input.OwnerOrdinal
                && e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardPlayed } f && f.Cards.Single().Id == id);
            Assert.True(audit.ThroughEventOrdinal >= played.EventOrdinal,
                $"24210 C{index}: {audit.StopReason} at {audit.ThroughEventOrdinal} before {played.EventOrdinal}");
            output.WriteLine($"24210 C{index}: crossed {id}; initial={input.Shuffle!.DrawPrefixKeys.Length}; through={audit.ThroughEventOrdinal}; stop={audit.StopReason}");
        }
        // Native wiring proof with fresh proposal randomness, not an independent
        // posterior experiment: source recipe is intentionally retained only here.
        var proposed = recipe with { ProposalSeed = recipe.ProposalSeed ^ 0x2468aceUL };
        var tape = NativeLabelTape.ForDeclaredPrior(prior, proposed, publicCombatCondition: prefix,
            publicReshuffleCondition: reshuffles, expectedPublicEvidence: root.PublicEvidence);
        await using var replay = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, proposed, tape);
        Assert.NotNull(replay); tape.ValidateProposalCompletion();
        Assert.Equal(prefix.EligibleShuffleCount, tape.ConditionedPublicCombatShuffles);
        Assert.Equal(reshuffles.EligibleShuffleCount, tape.ConditionedReshuffles);
        Assert.Equal(before, PublicJson.Serialize(replay.Observe()));
        Assert.Equal(before, PublicJson.Serialize(source.Observe()));
    }
}
