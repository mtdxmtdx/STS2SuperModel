using System.Collections.Immutable;
using System.Text.Json;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Saves;
using Xunit.Abstractions;

namespace Nosl.Tests;

public sealed class NativePublicFiveCardDrawClosureTests(ITestOutputHelper output)
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
            // Constructed fixture inventory is explicit; the actual certificate
            // receives only the detached public history below, never this session.
            packet = packet with { Observation = packet.Observation! with
            {
                History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(Entry)),
                    .. packet.Observation.History.Skip(1)],
            } };
            _recorder.ObserveCombatDecision(_owner, packet);
            return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(packet with { PublicEvidence = _recorder.Capture() }));
        }
    }

    private sealed record History(DecisionPacket Root, NativeEntryAssets Entry);

    private static async Task<History> NativeHistory(string id, bool upgraded, string seed, bool checkEffects)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: seed,
            Deck: [.. Enumerable.Repeat(id + (upgraded ? "+" : ""), 6),
                .. Enumerable.Range(0, 6).Select(i => "DefendSilent" + (i % 2 == 0 ? "+" : ""))],
            Enemy: "Nibbit", EnemyHp: 1000));
        var recorder = new Recorder(session);
        var current = recorder.Record(session.Observe());
        int beforeDraws = current.Observation!.History.Count(e => e.Kind == "draw");
        int beforeHp = current.Observation.Enemies.Single().Hp;
        string beforeShuffle = RngState(session.State.RunState.Rng.Shuffle);
        var action = current.Actions.First(a => a.Kind == "play" && current.Observation.Hand[a.Slot].Id == id);
        int cost = current.Observation.Hand[action.Slot].Cost;
        current = recorder.Record(await session.StepAsync(action));
        if (checkEffects)
        {
            Assert.Equal(3 - cost, current.Observation!.Energy);
            Assert.Equal(beforeShuffle, RngState(session.State.RunState.Rng.Shuffle));
            Assert.Equal(beforeDraws, current.Observation.History.Count(e => e.Kind == "draw"));
            Assert.Equal(5, current.Observation.DrawCount);
            Assert.Equal(id, Assert.Single(current.Observation.Discard).Id);
            if (id == "DeadlyPoison")
            {
                Assert.Equal(beforeHp, current.Observation.Enemies.Single().Hp);
                Assert.Contains(current.Observation.Enemies.Single().Powers,
                    p => p.Id == "PoisonPower" && p.Amount == (upgraded ? 7 : 5));
            }
            if (id == "FlickFlack") Assert.Equal(beforeHp - (upgraded ? 9 : 7), current.Observation.Enemies.Single().Hp);
            if (id == "Ricochet") Assert.Equal(beforeHp - 3 * (upgraded ? 5 : 4), current.Observation.Enemies.Single().Hp);
            if (id == "PreciseCut") Assert.Equal(beforeHp - ((upgraded ? 16 : 13) - 2 * 6), current.Observation.Enemies.Single().Hp);
            if (id == "Anticipate")
            {
                Assert.Contains(current.Observation.Powers, p => p.Id == "AnticipatePower" && p.Amount == (upgraded ? 4 : 2));
                Assert.Contains(current.Observation.Powers, p => p.Id == "DexterityPower" && p.Amount == (upgraded ? 4 : 2));
            }
        }
        if (id == "Anticipate")
        {
            var block = current.Actions.First(a => a.Kind == "play" && current.Observation!.Hand[a.Slot].Id == "DefendSilent");
            int baseBlock = current.Observation!.Hand[block.Slot].Upgrade > 0 ? 8 : 5;
            current = recorder.Record(await session.StepAsync(block));
            if (checkEffects) Assert.Equal(baseBlock + (upgraded ? 4 : 2), current.Observation!.Block);
        }
        for (int turn = 0; turn < 2; turn++)
        {
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
            if (checkEffects && id == "Anticipate")
                Assert.DoesNotContain(current.Observation!.Powers, p => p.Id is "AnticipatePower" or "DexterityPower");
        }
        if (checkEffects && id is "FlickFlack" or "Ricochet")
        {
            // Sly hand flush is ordinary Add, not selected-card Discard/autoplay.
            Assert.Single(current.PublicEvidence!.Events.Where(e => e.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.CardStarted } fact && fact.Cards.Single().Id == id));
        }
        return new(current, recorder.Entry);
    }

    [Theory]
    [InlineData("DeadlyPoison", false)]
    [InlineData("DeadlyPoison", true)]
    [InlineData("FlickFlack", false)]
    [InlineData("FlickFlack", true)]
    [InlineData("Anticipate", false)]
    [InlineData("Anticipate", true)]
    [InlineData("PreciseCut", false)]
    [InlineData("PreciseCut", true)]
    [InlineData("Ricochet", false)]
    [InlineData("Ricochet", true)]
    public async Task NativeEffectsAndIndependentConditionalProposalsPreserveTheWholePublicDrawHistory(string id, bool upgraded)
    {
        var fixture = await NativeHistory(id, upgraded, "five-card-source:" + id, true);
        string before = PublicJson.Serialize(fixture.Root);
        var prefix = NativePublicCombatPrefixCondition.Create(fixture.Root);
        var reshuffles = NativePublicReshuffleCondition.Create(fixture.Root);
        Assert.Equal("nosl.public-first-draw-cycle.v11", prefix.Combats[0].DrawPrefix!.CertificateVersion);
        Assert.Equal("first_reshuffle", prefix.Combats[0].DrawPrefix!.StopReason);
        Assert.Equal(12, prefix.Combats[0].Shuffle!.DrawPrefixKeys.Length);
        Assert.Equal("observed_prefix_complete", reshuffles.CombatAudits[0].StopReason);
        Assert.Equal(5, Assert.Single(reshuffles.Combats[0].Targets).DrawPrefixKeys.Count);
        AssertObservedDraws(fixture.Root, prefix, reshuffles);
        for (int proposal = 0; proposal < 3; proposal++)
        {
            // Independent proposal words and independent private native seed.
            // Nibbit's singleton cycle and single-target attacks are deterministic;
            // only the certified native shuffles are forced from public evidence.
            var random = new Rng((ulong)(84001 + proposal), "five-card-independent-proposal");
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
            var replay = await NativeHistory(id, upgraded, $"five-card-proposal:{id}:{proposal}", false);
            first.ValidateCompletion(); later.ValidateCompletion();
            Assert.True(first.NativeToProposalRatio.Numerator > 0);
            Assert.True(later.NativeToProposalRatio.Numerator > 0);
            _ = first.AcceptCorrection(random.NextUnsignedLong);
            _ = later.AcceptCorrection(random.NextUnsignedLong);
            Assert.Equal(before, PublicJson.Serialize(replay.Root));
            Assert.Equal(before, PublicJson.Serialize(fixture.Root));
        }
    }

    private static void AssertObservedDraws(DecisionPacket root, NativePublicCombatPrefixCondition first, NativePublicReshuffleCondition later)
    {
        var facts = root.PublicEvidence!.Events.Select(e => e.Payload).OfType<PublicCombatFact>().ToArray();
        Assert.Equal(facts.TakeWhile(f => f.FactKind != PublicCombatFactKind.Shuffled)
            .Where(f => f.FactKind == PublicCombatFactKind.CardDrawn).Select(f => NativePublicDrawKey.From(f.Cards.Single())),
            first.Combats[0].Shuffle!.DrawPrefixKeys);
        Assert.Equal(facts.SkipWhile(f => f.FactKind != PublicCombatFactKind.Shuffled)
            .Where(f => f.FactKind == PublicCombatFactKind.CardDrawn).Select(f => NativePublicDrawKey.From(f.Cards.Single())),
            Assert.Single(later.Combats[0].Targets).DrawPrefixKeys);
    }

    private static string RngState(Rng rng) => JsonSerializer.Serialize(rng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });

    [Theory]
    [InlineData("FlickFlack", false)]
    [InlineData("FlickFlack", true)]
    [InlineData("Ricochet", false)]
    [InlineData("Ricochet", true)]
    public async Task NativeMultiEnemyAttacksKeepShuffleStateAndUseOnlyTheirActualTargetLaw(string id, bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "five-card-multi-target:" + id,
            Deck: Enumerable.Repeat(id + (upgraded ? "+" : ""), 12).ToArray(),
            Enemies: ["Nibbit", "Nibbit"], EnemyHp: 1000));
        var recorder = new Recorder(session); var current = recorder.Record(session.Observe());
        string shuffle = RngState(session.State.RunState.Rng.Shuffle);
        string targets = RngState(session.State.RunState.Rng.CombatTargets);
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play")));
        Assert.Equal(shuffle, RngState(session.State.RunState.Rng.Shuffle));
        if (id == "Ricochet")
        {
            Assert.NotEqual(targets, RngState(session.State.RunState.Rng.CombatTargets));
            Assert.Equal(2000 - 3 * (upgraded ? 5 : 4), current.Observation!.Enemies.Sum(e => e.Hp));
        }
        else
        {
            Assert.Equal(targets, RngState(session.State.RunState.Rng.CombatTargets));
            Assert.All(current.Observation!.Enemies, e => Assert.Equal(1000 - (upgraded ? 9 : 7), e.Hp));
        }
        for (int turn = 0; turn < 2; turn++)
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.Equal("observed_prefix_complete", NativePublicReshuffleCondition.Create(current).CombatAudits[0].StopReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlyPoisonDeathLeavesRavenousLifecycleAndLaterDrawWitnessNative(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "deadly-poison-death",
            Deck: Enumerable.Repeat("DeadlyPoison" + (upgraded ? "+" : ""), 12).ToArray(),
            Enemies: ["CorpseSlug", "CorpseSlug"], EnemyHp: upgraded ? 7 : 5));
        var recorder = new Recorder(session); var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play" && a.Target == 0)));
        Assert.Equal(2, current.Observation!.Enemies.Length);
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        var survivor = Assert.Single(current.Observation!.Enemies);
        Assert.Equal(1, survivor.Slot);
        Assert.Contains(survivor.Powers, p => p.Id == "StrengthPower" && p.Amount == 5);
        Assert.Contains(current.PublicEvidence!.Events, e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.Damage, TargetSlot: 0, Damage.Killed: true });
        current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.Equal("observed_prefix_complete", NativePublicReshuffleCondition.Create(current).CombatAudits[0].StopReason);
        AssertObservedDraws(current, NativePublicCombatPrefixCondition.Create(current), NativePublicReshuffleCondition.Create(current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnticipateStackingAndExpiryPreserveNativeDexterityAndDraws(bool upgraded)
    {
        await using var session = await CombatSession.CreateAsync(new(Seed: "anticipate-stacking",
            Deck: [.. Enumerable.Repeat("Anticipate" + (upgraded ? "+" : ""), 8), .. Enumerable.Repeat("DefendSilent", 4)],
            Enemy: "Nibbit", EnemyHp: 1000));
        var recorder = new Recorder(session); var current = recorder.Record(session.Observe());
        // Every seven-card opening has at least three copies; no seed search.
        const int plays = 2;
        for (int i = 0; i < plays; i++)
            current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
                && current.Observation!.Hand[a.Slot].Id == "Anticipate")));
        Assert.Contains(current.Observation!.Powers, p => p.Id == "AnticipatePower" && p.Amount == plays * (upgraded ? 4 : 2));
        Assert.Contains(current.Observation.Powers, p => p.Id == "DexterityPower" && p.Amount == plays * (upgraded ? 4 : 2));
        for (int turn = 0; turn < 2; turn++)
            current = recorder.Record(await session.StepAsync(current.Actions.Single(a => a.Kind == "end_turn")));
        Assert.DoesNotContain(current.Observation!.Powers, p => p.Id is "AnticipatePower" or "DexterityPower");
        Assert.Equal("observed_prefix_complete", NativePublicReshuffleCondition.Create(current).CombatAudits[0].StopReason);
    }

    [Theory]
    [InlineData("FlickFlack", false, false)]
    [InlineData("FlickFlack", true, false)]
    [InlineData("Ricochet", false, false)]
    [InlineData("Ricochet", true, false)]
    [InlineData("FlickFlack", false, true)]
    [InlineData("FlickFlack", true, true)]
    [InlineData("Ricochet", false, true)]
    [InlineData("Ricochet", true, true)]
    public async Task NewlyReviewedSlyManualPlaysDoNotAdmitDiscardAutoplay(string id, bool automatic, bool upgraded)
    {
        string card = id + (upgraded ? "+" : "");
        await using var session = await CombatSession.CreateAsync(new(Seed: "five-card-sly:" + id,
            Deck: automatic ? ["DaggerThrow", card] : [.. Enumerable.Repeat("DaggerThrow", 4), .. Enumerable.Repeat(card, 4)],
            Enemy: "Nibbit", EnemyHp: 1000));
        var recorder = new Recorder(session); var current = recorder.Record(session.Observe());
        current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "play"
            && current.Observation!.Hand[a.Slot].Id == "DaggerThrow")));
        long boundary;
        if (automatic)
            boundary = current.PublicEvidence!.Events.Single(e => e.Payload is PublicCombatFact
                { FactKind: PublicCombatFactKind.AutomaticSelection, Model: "DaggerThrow" }).EventOrdinal - 1;
        else
        {
            Assert.Equal("card_choice", current.Status);
            boundary = current.PublicEvidence!.Events[^1].EventOrdinal;
            current = recorder.Record(await session.StepAsync(current.Actions.First(a => a.Kind == "choose"
                && current.Observation!.Choice!.Candidates[a.Selection![0]].Id == id)));
        }
        var audit = NativePublicCombatPrefixCondition.Create(current).Combats[0].DrawPrefix!;
        Assert.Equal("draw_cycle_sly_discard_not_certified", audit.StopReason);
        Assert.Equal(boundary, audit.ThroughEventOrdinal);
        Assert.Contains(current.PublicEvidence!.Events, e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.CardStarted } fact && fact.Cards.Single().Id == id);
    }

    [Theory]
    [InlineData("draw", "draw_cycle_draw_source_not_certified")]
    [InlineData("generated", "draw_cycle_generation_not_certified")]
    [InlineData("result", "draw_cycle_card_effect_not_certified")]
    [InlineData("modifier", "draw_cycle_card_effect_not_certified")]
    [InlineData("power", "draw_cycle_power_not_certified:ConfusionPower")]
    public async Task NewManualPlayDoesNotPermitUnreviewedEffects(string mutation, string stop)
    {
        var root = (await NativeHistory("FlickFlack", false, "five-card-negative", true)).Root;
        var played = root.PublicEvidence!.Events.First(e => e.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.CardPlayed } fact && fact.Cards.Single().Id == "FlickFlack");
        var fact = (PublicCombatFact)played.Payload;
        PublicEvidencePayload changed = mutation switch
        {
            "draw" => new PublicCombatFact(PublicCombatFactKind.CardDrawn, cards: fact.Cards),
            "generated" => new PublicCombatFact(PublicCombatFactKind.HiddenCardGenerated),
            "result" => new PublicCombatFact(PublicCombatFactKind.CardPlayed, cards: fact.Cards, resultPile: PublicCardPile.Draw),
            "modifier" => new PublicCombatFact(PublicCombatFactKind.CardPlayed,
                cards: [fact.Cards.Single() with { Affliction = new("Hexed", 1) }], resultPile: PublicCardPile.Discard),
            _ => new PublicCombatFact(PublicCombatFactKind.PowerChanged, targetSlot: -2, model: "ConfusionPower", amount: 1),
        };
        var changedRoot = root with { PublicEvidence = new(root.PublicEvidence.SchemaVersion, root.PublicEvidence.CompleteFromRunStart,
            root.PublicEvidence.Events.SetItem((int)played.EventOrdinal, new(played.EventOrdinal, played.OwnerOrdinal, changed))) };
        string before = PublicJson.Serialize(changedRoot);
        var first = NativePublicCombatPrefixCondition.Create(changedRoot).Combats[0];
        Assert.Equal(stop, first.DrawPrefix!.StopReason);
        Assert.Equal(7, first.Shuffle!.DrawPrefixKeys.Length);
        Assert.Equal(played.EventOrdinal - 1, first.DrawPrefix.ThroughEventOrdinal);
        Assert.Equal(0, NativePublicReshuffleCondition.Create(changedRoot).EligibleShuffleCount);
        Assert.Equal(before, PublicJson.Serialize(changedRoot));
    }

    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(24205UL, 1, "Anticipate")]
    [InlineData(24207UL, 1, "PreciseCut")]
    [InlineData(24210UL, 0, "DeadlyPoison")]
    [InlineData(24210UL, 1, "DeadlyPoison")]
    [InlineData(24210UL, 2, "Ricochet")]
    [InlineData(24211UL, 1, "FlickFlack")]
    public async Task InspectedNativeHistoriesCrossTheirReportedManualCardBlocker(ulong sourceSeed, int index, string id)
    {
        // Diagnostic reproduction only. Production sees detached public evidence;
        // it never receives this seed or uses a source recipe for inference.
        var prior = Prior; var recipe = prior.Draw(new Rng(sourceSeed, "nosl-native-tape-source-draw-v1"));
        await using var source = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(source);
        var root = source.Observe(); string before = PublicJson.Serialize(root);
        var condition = NativePublicCombatPrefixCondition.Create(root);
        var input = condition.Combats[index];
        var played = root.PublicEvidence!.Events.First(e => e.OwnerOrdinal == input.OwnerOrdinal
            && e.Payload is PublicCombatFact { FactKind: PublicCombatFactKind.CardPlayed } fact && fact.Cards.Single().Id == id);
        var reshuffles = NativePublicReshuffleCondition.Create(root);
        var audit = reshuffles.CombatAudits[index];
        Assert.True(audit.ThroughEventOrdinal >= played.EventOrdinal,
            $"{sourceSeed} C{index}: {audit.StopReason} at {audit.ThroughEventOrdinal}, before {played.EventOrdinal}");
        Assert.Equal("nosl.public-first-draw-cycle.v11", input.DrawPrefix!.CertificateVersion);
        Assert.Equal(before, PublicJson.Serialize(root));
        output.WriteLine($"{sourceSeed} C{index}: crossed {id}; initial={input.Shuffle!.DrawPrefixKeys.Length}; "
            + $"full-through={audit.ThroughEventOrdinal}; stop={audit.StopReason}; reshuffles={reshuffles.EligibleShuffleCount}");
        if (sourceSeed == 24205) Assert.Null(condition.Combats[2].Shuffle); // Pinpoint entry hook remains unsupported.
    }
}
