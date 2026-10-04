using System.Numerics;
using Nosl.Contracts;
using Nosl.Worker;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Runs;

namespace Nosl.Tests;

public sealed class NativeNeowPotionTests
{
    private static NativeTapePrior Prior => new()
    {
        SchemaVersion = NativeTapePrior.MapVersion, EligibleCombats = 3, EligibleDecisionsPerCombat = 8,
        Execution = new(MaxFloors: 8, SourceDecisionHorizon: 1024,
            OutsideCombatScript: NaturalSourceCollector.BoundedEventScriptVersion,
            PublicContextProfile: PublicRunContext.Version, PublicEvidenceProfile: PublicRunEvidence.CompleteMapVersion,
            PublicMapObservationProfile: PublicMapObservationProfiles.CompleteGraphV1),
    };

    [Theory]
    [InlineData(24001UL, nameof(SkillPotion), nameof(StrengthPotion))]
    [InlineData(24003UL, nameof(WeakPotion), nameof(FyshOil))]
    public async Task ActualNativeFreshSourcePotionsConditionIndependentOpeningAndReplay(ulong seed, string first, string second)
    {
        var observed = await Source(seed);
        Assert.True(NativeNeowPotionCondition.TryCreate(observed, Prior, out var condition, out var reason), reason);
        Assert.Equal(new[] { first, second }, condition!.PotionIds);
        Assert.True(NativeNeowCondition.TryCreate(observed, Prior, out var neow, out reason), reason);
        var random = new Rng(73411);
        var proposal = new NativeNeowPotionProposal(condition, random.NextUnsignedLong, (words, _, _) => Force(words));
        using (Enter(neow!, proposal))
        using (proposal.EnterScope())
        await using (var world = await NativeRunWorld.OpenAsync(Prior.Execution, "independent-phial-proposal:" + seed, 0))
        {
            Assert.NotNull(world); proposal.ValidateCompletion();
            Assert.Equal(2, proposal.ConditionedPotionCount); Assert.Equal(condition.Envelope, proposal.NativeToProposalRatio);
            Assert.True(proposal.AcceptCorrection(() => throw new Exception("The root-fixed ratio cancels without another draw")));
            Assert.Equal(PublicJson.Serialize(observed.PublicEvidence!.Events.Take(5)),
                PublicJson.Serialize(world.Observe().PublicEvidence!.Events.Take(5)));
            var replay = new NativeNeowPotionProposal(condition, new Rng(73411).NextUnsignedLong, (words, _, _) => Force(words));
            using (Enter(neow!, replay))
            using (replay.EnterScope())
            await using (var fork = await world.ForkForContinuationAsync())
            {
                replay.ValidateCompletion();
                Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
                var policy = PublicContinuationPolicies.Create(PublicContinuationPolicies.ReviewedId);
                for (int i = 0; i < 12 && world.Observe().Status != "terminal_settled"; i++)
                {
                    var action = policy.Choose(world.Observe()); await world.StepAsync(action); await fork.StepAsync(action);
                    Assert.Equal(PublicJson.Serialize(world.Observe()), PublicJson.Serialize(fork.Observe()));
                }
            }
        }
        // The public certificate also applies to the older Rewards hybrid, but never the legacy full-state-only prior.
        Assert.True(NativeNeowPotionCondition.TryCreate(observed, Prior with { SchemaVersion = NativeTapePrior.RewardsVersion }, out _, out _));
        Assert.False(NativeNeowPotionCondition.TryCreate(observed, Prior with { SchemaVersion = NativeTapePrior.Version }, out _, out reason));
        Assert.Equal("neow_potions_require_explicit_hybrid_prior", reason);
    }

    [Theory]
    [InlineData("missing_capture")]
    [InlineData("foreign_rng")]
    [InlineData("changed_pool")]
    [InlineData("native_failure")]
    public async Task MissingBoundaryProvenanceDriftAndFailureNeverBecomeSuccessfulMass(string mode)
    {
        var root = await Source(24001);
        Assert.True(NativeNeowPotionCondition.TryCreate(root, Prior, out var condition, out _));
        Assert.True(NativeNeowCondition.TryCreate(root, Prior, out var neow, out _));
        int forceCalls = 0;
        var proposal = new NativeNeowPotionProposal(condition!, new Rng(555).NextUnsignedLong, (words, _, _) =>
        {
            forceCalls++;
            return mode == "native_failure" ? LabelRandomScope.Enter(_ => throw new InvalidDataException("fixture native draw failed")) : Force(words);
        });
        if (mode == "missing_capture")
        {
            Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
            Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0)); return;
        }
        using (Enter(neow!, proposal))
        using (LabelPhialHolsterScope.Enter(context => proposal.BeginGrant(mode switch
        {
            "foreign_rng" => context with { Rng = new Rng(9) },
            "changed_pool" => context with { Pool = context.Pool.Reverse().ToArray() },
            _ => context,
        })))
        {
            var error = await Record.ExceptionAsync(() => NativeRunWorld.OpenAsync(Prior.Execution, "phial-failure:" + mode, 0));
            Assert.NotNull(error);
            if (mode == "native_failure") Assert.IsType<InvalidDataException>(error);
            else { Assert.IsType<InvalidOperationException>(error); Assert.Equal(0, forceCalls); }
        }
        Assert.Equal(0, proposal.ConditionedPotionCount);
        Assert.Throws<InvalidOperationException>(proposal.ValidateCompletion);
        Assert.Throws<InvalidOperationException>(() => proposal.AcceptCorrection(() => 0));
    }

    [Fact]
    public async Task MissingSettlementCannotInferGrantsFromCurrentInventory()
    {
        var root = await Source(24001); var evidence = root.PublicEvidence!;
        var changed = root with { PublicEvidence = new(PublicRunEvidence.CompleteMapVersion, true,
            evidence.Events.SetItem(4, new(4, 0, new PublicOwnerEnded(PublicEvidenceOwnerOutcome.Completed)))) };
        Assert.False(NativeNeowPotionCondition.TryCreate(changed, Prior, out _, out var reason));
        Assert.Equal("neow_potions_public_settlement_missing", reason);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void FiniteNativeJointMassPreservesRarityWithoutReplacementAndUnobservedFuture(int bits)
    {
        NaturalSourceCollector.InitializeNativeModels();
        PotionModel[] pool = [ModelDb.Potion<SkillPotion>(), ModelDb.Potion<StrengthPotion>(),
            ModelDb.Potion<WeakPotion>(), ModelDb.Potion<FyshOil>(), ModelDb.Potion<SneckoOil>()];
        int domain = 1 << bits, matching = 0;
        string first = nameof(SkillPotion), second = nameof(StrengthPotion);
        PotionModel? Draw(IReadOnlyList<PotionModel> candidates, int rarityWord, int indexWord)
        {
            float rarity = (float)((double)rarityWord / domain);
            var r = rarity <= 0.1f ? PotionRarity.Rare : rarity <= 0.35f ? PotionRarity.Uncommon : PotionRarity.Common;
            var byRarity = candidates.Where(p => p.Rarity == r).ToArray();
            return byRarity.Length == 0 ? null : byRarity[(int)((double)indexWord / domain * byRarity.Length)];
        }
        for (int a = 0; a < domain; a++) for (int b = 0; b < domain; b++)
        for (int c = 0; c < domain; c++) for (int d = 0; d < domain; d++)
        {
            var p = Draw(pool, a, b);
            var q = Draw(pool.Where(x => p is null || x.Id != p.Id).ToArray(), c, d);
            if (p?.GetType().Name == first && q?.GetType().Name == second) matching++;
        }
        var p0 = NativeRewardResourceMath.PotionMass(pool, first, bits);
        var p1 = NativeRewardResourceMath.PotionMass(pool.Where(p => p.GetType().Name != first).ToArray(), second, bits);
        var joint = p0.Multiply(p1.Numerator, p1.Denominator);
        Assert.Equal(new ShuffleRational(matching, BigInteger.Pow(domain, 4)), joint);
        if (bits >= 3) Assert.NotEqual(p0.Multiply(p0.Numerator, p0.Denominator), joint);
        Assert.Equal(new ShuffleRational(0, 1), NativeRewardResourceMath.PotionMass(pool.Where(p => p.GetType().Name != first).ToArray(), first, bits));
        // Every matching five-cell world retains the same mass: q(w)*Z = p(w), with an untouched fifth cell.
        var proposalMass = new ShuffleRational(1, matching * domain);
        Assert.Equal(new ShuffleRational(1, BigInteger.Pow(domain, 5)), proposalMass.Multiply(joint.Numerator, joint.Denominator));
        var random = new Rng((ulong)bits);
        for (int trial = 0; trial < 20; trial++)
        {
            var a = NativeRewardResourceMath.Potion(pool, first, random.NextUnsignedLong, bits);
            var remaining = pool.Where(p => p.GetType().Name != first).ToArray();
            var b = NativeRewardResourceMath.Potion(remaining, second, random.NextUnsignedLong, bits);
            Assert.Equal(first, Draw(pool, (int)(a.RawWords[0] >> (64 - bits)), (int)(a.RawWords[1] >> (64 - bits)))!.GetType().Name);
            Assert.Equal(second, Draw(remaining, (int)(b.RawWords[0] >> (64 - bits)), (int)(b.RawWords[1] >> (64 - bits)))!.GetType().Name);
            Assert.Equal(joint, a.NativeToProposalRatio.Multiply(b.NativeToProposalRatio.Numerator, b.NativeToProposalRatio.Denominator));
        }
    }

    private static async Task<DecisionPacket> Source(ulong seed)
    {
        var prior = Prior.Freeze(); var recipe = prior.Draw(new Rng(seed, "nosl-native-tape-source-draw-v1"));
        await using var world = await NativeRunWorld.OpenLabelTapeAsync(prior.Execution, recipe, NativeLabelTape.ForDeclaredPrior(prior, recipe));
        Assert.NotNull(world); return PublicJson.Read<DecisionPacket>(PublicJson.Serialize(world.Observe()));
    }

    private static IDisposable Enter(NativeNeowCondition condition, NativeNeowPotionProposal proposal)
    {
        NativeNeowOpeningProposal? opening = null;
        return LabelRandomScope.EnterRewardProvenance(new NativeRewardsOracle(27193).Word, StateWord,
            beginShuffle: (_, items) => items.OfType<Type>().Any(type => type.Name == "PhialHolster")
                ? Force(condition.CreateProposal(items.Cast<Type>().ToArray(), new Rng(719).NextUnsignedLong, opening).Plan.RawWords) : null,
            beginNeowInitialOptions: context =>
            {
                proposal.AttachHypotheticalRun((RunState)context.Neow.Owner.RunState);
                opening = condition.CreateOpeningProposal(context.AllowedCurses, new Rng(471).NextUnsignedLong);
                int index = 0;
                return LabelRandomScope.Enter(state => opening.RawWords[index++] ?? StateWord(state));
            });
    }

    private static IDisposable Force(IReadOnlyList<ulong> words)
    { int index = 0; return LabelRandomScope.Enter(_ => words[index++]); }

    private static ulong StateWord(LabelRandomState state) => BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(
        new[] { state.State0, state.State1, state.State2, state.State3 }.SelectMany(BitConverter.GetBytes).ToArray()));
}
