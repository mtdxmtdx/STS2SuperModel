using System.Collections.Immutable;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Characters;
using Sts2Sim.Core.Models.Monsters;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeCorpseSlugStartupTests
{
    private static NativeTapePrior Hybrid => new()
    {
        SchemaVersion = NativeTapePrior.RewardsVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.Version),
    };
    private static PublicEvidenceAssets Assets(NativeEntryAssets entry) => new(entry.Hp, entry.MaxHp,
        entry.Gold, entry.Deck, entry.Relics, entry.Potions.ToImmutableArray(), entry.MaxEnergy,
        entry.PotionSlots, entry.OrbSlots, entry.CardRemovalsUsed);

    private static DecisionPacket WithEvidence(DecisionPacket packet)
    {
        var entry = PublicJson.Read<NativeEntryAssets>(packet.Observation!.History[1].Detail);
        var recorder = new PublicRunEvidenceRecorder(new("Silent", 10, Assets(entry)));
        long owner = recorder.BeginOwner(PublicEvidenceOwnerKind.Combat, 0, 2);
        recorder.ObserveCombatDecision(owner, packet);
        return packet with { PublicEvidence = recorder.Capture() };
    }

    private static async Task<DecisionPacket> NativeOpening(string encounter = "CorpseSlugsWeak")
    {
        // Native encounter startup in an explicit lifecycle fixture, not a source-distribution claim.
        await using var session = await CombatSession.CreateAsync(new(Seed: "slug-startup-v2", Encounter: encounter));
        var packet = session.Observe();
        var entry = NativeEntryAssets.Capture(session.InitialAssets, session.StartHp, session.StartPotions);
        return WithEvidence(packet with { Observation = packet.Observation! with
        {
            History = [packet.Observation.History[0], new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)),
                .. packet.Observation.History.Skip(1)],
        }});
    }

    [Theory]
    [InlineData("CorpseSlugsWeak", 2)]
    [InlineData("CorpseSlugsNormal", 3)]
    public async Task NativeSelfPowersAreRetainedAndOnlyThePublicV2ProfileAccelerates(string encounter, int enemies)
    {
        var root = await NativeOpening(encounter);
        string before = PublicJson.Serialize(root);
        Assert.False(NativeInitialShuffleCondition.TryCreate(root, out _, out var legacyReason));
        Assert.Equal("uninterrupted_initial_draw_history_required", legacyReason);
        Assert.False(NativeInitialHpCondition.TryCreate(root, out _, out _));
        var condition = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(1, condition.EligibleShuffleCount);
        Assert.Equal(enemies, condition.EligibleHpCount);
        Assert.NotNull(condition.Combats[0].SlugHp);
        Assert.Null(condition.Combats[0].Hp);
        Assert.Equal(root.Observation!.Hand.Select(card => card.Id), condition.Combats[0].Shuffle!.DrawPrefixIds);
        Assert.Equal(enemies, root.PublicEvidence!.Events.Count(entry => entry.Payload is PublicCombatFact
            { FactKind: PublicCombatFactKind.PowerChanged, Model: "RavenousPower", Amount: 5 }));
        Assert.Equal(before, PublicJson.Serialize(root));

        var altered = WithEvidence(root with { Observation = root.Observation with
        {
            History = root.Observation.History.Select(entry => entry.Kind == "power_changed"
                ? entry with { Detail = entry.Detail.Replace("\"amount\":5", "\"amount\":6", StringComparison.Ordinal) } : entry).ToArray(),
        }});
        var mismatch = Assert.Throws<NativePublicConstraintMismatchException>(() =>
            new NativePublicPrefixConstraint(root.PublicEvidence).Check(altered.PublicEvidence));
        Assert.Contains("PowerChanged", mismatch.Message); // Facts still participate in full replay equality.
    }

    [Theory]
    [InlineData("unknown_power", "startup_power_not_certified")]
    [InlineData("target", "startup_power_not_certified")]
    [InlineData("source", "startup_power_not_certified")]
    [InlineData("amount", "startup_power_not_certified")]
    [InlineData("missing", "startup_power_roster_not_certified")]
    [InlineData("duplicate", "startup_power_roster_not_certified")]
    [InlineData("reordered", "startup_power_roster_not_certified")]
    [InlineData("interrupted", "uninterrupted_initial_draw_history_required")]
    [InlineData("unsafe_monster", "startup_monster_not_certified")]
    public async Task UnreviewedOrIncompleteStartupDisablesAcceleration(string change, string reason)
    {
        var root = await NativeOpening();
        var history = root.Observation!.History.ToList();
        var power = history[2];
        switch (change)
        {
            case "unknown_power": history[2] = power with { Detail = power.Detail.Replace("RavenousPower", "StrengthPower") }; break;
            case "target": history[2] = power with { Detail = power.Detail.Replace("CorpseSlug", "player") }; break;
            case "source": history[2] = power with { Detail = power.Detail.Replace("\"sourceSlot\":0", "\"sourceSlot\":1") }; break;
            case "amount": history[2] = power with { Detail = power.Detail.Replace("\"amount\":5", "\"amount\":4") }; break;
            case "missing": history.RemoveAt(2); break;
            case "duplicate": history.Insert(2, power); break;
            case "reordered": (history[2], history[3]) = (history[3], history[2]); break;
            case "interrupted": history.Insert(5, power); break;
            case "unsafe_monster": history[^1] = history[^1] with { Detail = history[^1].Detail.Replace("CorpseSlug", "ToughEgg") }; break;
        }
        root = WithEvidence(root with { Observation = root.Observation with { History = history.ToArray() } });
        var condition = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(0, condition.EligibleShuffleCount);
        Assert.Equal(0, condition.EligibleHpCount);
        Assert.Equal(reason, condition.Combats[0].ShuffleReason);
    }

    [Theory]
    [InlineData("missing", "corpse_slug_hp_complete_unslotted_roster_required")]
    [InlineData("replacement", "corpse_slug_hp_complete_unslotted_roster_required")]
    [InlineData("duplicate_hp", "corpse_slug_hp_distinct_native_range_required")]
    [InlineData("range", "corpse_slug_hp_distinct_native_range_required")]
    public async Task InvalidPublicHpRosterFallsBackWithoutLosingShuffle(string change, string reason)
    {
        var root = await NativeOpening();
        var enemies = root.Observation!.Enemies;
        root = WithEvidence(root with { Actions = [new(0, "end_turn")], Observation = root.Observation with { Enemies = change switch
        {
            "missing" => [enemies[0]],
            "replacement" => [enemies[0], enemies[1] with { Id = "SludgeSpinner" }],
            "duplicate_hp" => [enemies[0], enemies[1] with { MaxHp = enemies[0].MaxHp }],
            _ => [enemies[0], enemies[1] with { MaxHp = 30 }],
        } } });
        var condition = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(1, condition.EligibleShuffleCount);
        Assert.Equal(0, condition.EligibleHpCount);
        Assert.Equal(reason, condition.Combats[0].HpReason);
    }

    [Theory]
    [InlineData("CorpseSlugsWeak", 2)]
    [InlineData("CorpseSlugsNormal", 3)]
    public async Task NativeDuplicateModelsConditionByUnsortedCreationSlotAndConsumeEveryWord(string encounter, int count)
    {
        var root = await NativeOpening(encounter);
        var entry = PublicJson.Read<NativeEntryAssets>(root.Observation!.History[1].Detail);
        var condition = NativePublicCombatPrefixCondition.Create(root);
        var run = new RunState("slug-hp-native-proposal", ascensionLevel: 10);
        var player = Player.CreateForNewRun(ModelDb.Character<Silent>(), run);
        run.AddPlayer(player);
        var random = new Rng(54321);
        int forcedWords = 0;
        IDisposable Force(IReadOnlyList<ulong> words, string purpose)
        {
            int index = 0;
            return LabelRandomScope.Enter(_ => { forcedWords++; return words[index++]; });
        }
        var proposal = new NativePublicCombatPrefixProposal(condition, random.NextUnsignedLong, Force);
        proposal.AttachHypotheticalRun(run);
        proposal.CombatEntering(0, entry, run.Rng.Shuffle);
        var state = new CombatState(run);
        state.AddPlayerCreature(player.Creature);
        using var scope = LabelRandomScope.Enter(_ => throw new InvalidOperationException("Unexpected ordinary draw"),
            proposal.BeginShuffle, proposal.BeginMonsterHp);
        for (int slot = 0; slot < count; slot++)
            state.AddMonster((MonsterModel)ModelDb.Monster<CorpseSlug>().MutableClone(), CombatSide.Enemy);
        player.ResetCombatState();
        player.PopulateCombatState(run.Rng.Shuffle);
        Assert.Equal(root.Observation.Enemies.Select(enemy => enemy.MaxHp), state.Enemies.Select(enemy => enemy.MaxHp));
        Assert.Equal(count, run.Rng.Niche.Counter);
        Assert.Equal(count, proposal.ConditionedHpCount);
        Assert.Equal(1, proposal.ConditionedShuffleCount);
        Assert.Equal(count + entry.Deck.Length - 1, forcedWords);
        proposal.ValidateCompletion();
        _ = proposal.AcceptCorrection(() => ulong.MaxValue);
    }

    [Theory]
    [InlineData("sorted")]
    [InlineData("slot_name")]
    [InlineData("used_hp")]
    [InlineData("range")]
    [InlineData("foreign_monster")]
    public async Task NativeHpContextCannotSilentlyChangeItsSlotProof(string change)
    {
        var root = await NativeOpening();
        var condition = NativePublicCombatPrefixCondition.Create(root).Combats[0].SlugHp!;
        var run = new RunState("slug-hp-context", ascensionLevel: 10);
        var state = change == "sorted" ? new CombatState(run, ["left", "right"]) : new CombatState(run);
        int words = 0;
        using var scope = LabelRandomScope.Enter(_ => 0, beginMonsterHp: context =>
        {
            if (change == "used_hp") context = context with { UsedHp = [27] };
            if (change == "range") context = context with { MinHp = 26 };
            condition.CreateProposal(context, 0, () => { words++; return ulong.MaxValue; });
            return null;
        });
        MonsterModel model = change == "foreign_monster"
            ? (MonsterModel)ModelDb.Monster<SludgeSpinner>().MutableClone()
            : (MonsterModel)ModelDb.Monster<CorpseSlug>().MutableClone();
        var error = Record.Exception(() => state.AddMonster(model, CombatSide.Enemy, change == "slot_name" ? "left" : null));
        if (change == "foreign_monster") Assert.IsType<NativePublicConstraintMismatchException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, words);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void DuplicateSlugFiniteLawMatchesEveryDistinctPublicSlotTarget(int count)
    {
        const int bits = 4, domain = 1 << bits;
        foreach (int firstTarget in Enumerable.Range(27, 3))
        foreach (int secondTarget in Enumerable.Range(27, 3).Where(hp => hp != firstTarget))
        {
            int[] targets = count == 2 ? [firstTarget, secondTarget]
                : [firstTarget, secondTarget, 84 - firstTarget - secondTarget];
            int matches = 0, total = (int)Math.Pow(domain, count);
            for (int code = 0; code < total; code++)
            {
                int residual = code;
                var hp = new List<int>();
                for (int slot = 0; slot < count; slot++)
                {
                    var available = Enumerable.Range(27, 3).Except(hp).ToArray();
                    hp.Add(available[(residual % domain) * available.Length / domain]);
                    residual /= domain;
                }
                if (hp.SequenceEqual(targets)) matches++;
            }
            var ratio = new ShuffleRational(1, 1);
            for (int slot = 0; slot < count; slot++)
            {
                ulong envelope = NativeHpProposal.RootEnvelopeBucket(27, 29,
                    targets.Where((_, other) => other != slot), bits);
                var plan = NativeHpProposal.Create(27, 29, targets[slot], targets.Take(slot).Order().ToArray(),
                    envelope, () => ulong.MaxValue, bits)!;
                ratio = ratio.Multiply(plan.BucketSize, domain);
                int accepted = 0;
                for (ulong residue = 0; residue < envelope; residue++)
                    if (plan.AcceptCorrection(() => envelope + residue)) accepted++;
                Assert.Equal(plan.BucketSize, (ulong)accepted);
            }
            Assert.Equal(new ShuffleRational(matches, total), ratio);
        }
    }

    [Fact]
    public async Task ActualSlugHypothesisReplaysAllPublicFactsAndOwnedSettlement()
    {
        // Same-recipe integration and replay; no independent-posterior acceptance claim.
        var recipe = Hybrid.Draw(new Rng(11003, "nosl-native-tape-source-draw-v1")) with { DecisionIndex = 0 };
        DecisionPacket root;
        await using (var original = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, recipe,
            NativeLabelTape.ForDeclaredPrior(Hybrid, recipe)))
        { Assert.NotNull(original); root = original.Observe(); }
        Assert.All(root.Observation!.Enemies, enemy => Assert.Equal("CorpseSlug", enemy.Id));
        string before = PublicJson.Serialize(root);
        var combats = NativePublicCombatPrefixCondition.Create(root);
        Assert.Equal(2, combats.EligibleHpCount);
        Assert.Equal(1, combats.EligibleShuffleCount);
        var proposal = recipe with { ProposalSeed = 87931 };
        var tape = NativeLabelTape.ForDeclaredPrior(Hybrid, proposal, publicCombatCondition: combats,
            expectedPublicEvidence: root.PublicEvidence);
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(Hybrid.Execution, proposal, tape);
        Assert.NotNull(world);
        tape.ValidateProposalCompletion();
        Assert.Equal(2, tape.ConditionedPublicCombatHp);
        Assert.Equal(1, tape.ConditionedPublicCombatShuffles);
        Assert.Equal(before, PublicJson.Serialize(world.Observe()));
        var correction = new Rng(456789);
        _ = tape.AcceptCorrection(correction.NextUnsignedLong);
        await using var fork = await world.ForkForContinuationAsync();
        var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
        for (int i = 0; i < 200 && world.Observe().Status != "terminal_settled"; i++)
        {
            Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
            var action = policy.Choose(world.Observe());
            await world.StepAsync(action); await fork.StepAsync(action);
        }
        Assert.Equal("terminal_settled", world.Observe().Status);
        Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
        Assert.Equal(PublicJson.Serialize(await world.RecordSettledAsync(policy.Id, 0)),
            PublicJson.Serialize(await fork.RecordSettledAsync(policy.Id, 0)));
        Assert.Equal(before, PublicJson.Serialize(root));
    }
}
