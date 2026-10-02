using Nosl.Contracts;
using Nosl.Objectives;
using Nosl.Worker;

namespace Nosl.Tests;

public class NativeRunPriorTests
{
    private static DecisionPacket Packet(int hp = 70)
    {
        var entry = new NativeEntryAssets("nosl.native-entry-assets.v1", 70, 70, 99, [], [], [null, null], 3, 2, 0, 0);
        return new("player_decision", new("nosl.public.v2", 70, 10, 1, hp, 70, 0, 3, 0,
            [], [], [], [], [], 0, [null, null], [], [], [],
            [new("combat_started", ""), new(NativeEntryAssets.EventKind, PublicJson.Serialize(entry)), new("player_turn", "1")], null),
            [new(0, "end_turn")]);
    }

    private sealed class ToyWorld(NativeRunRecipe recipe, DecisionPacket packet, bool cleanupFails = false) : ITeacherWorld
    {
        internal NativeRunRecipe Recipe { get; } = recipe;
        internal bool Disposed { get; private set; }
        public int StartHp => 70;
        public int StartMaxHp => 70;
        public string?[] StartPotions => [null, null];
        public double SettlementSeconds => 0;
        public DecisionPacket Observe() => packet;
        public Task<DecisionPacket> StepAsync(PublicAction action) => throw new NotSupportedException("Finite posterior fixture only");
        public Task<ITeacherWorld> ForkForContinuationAsync() => throw new NotSupportedException("Finite posterior fixture only");
        public Task<RolloutOutcome> RecordSettledAsync(string policyId, int lastPlayerTurn) => throw new NotSupportedException("Finite posterior fixture only");
        public ValueTask DisposeAsync()
        { Disposed = true; if (cleanupFails) throw new InvalidOperationException("cleanup failed"); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task FiniteSeedTimesFixedSlotPriorRetainsMultiplicityAndAbsentSlotMass()
    {
        // Six equally likely recipes; three match H, one exists but differs, two
        // are absent. Conditional H weights are seed11:2/3, seed22:1/3. A first-
        // matching-root scan would instead give each matching run equal weight.
        var prior = new NativeRunPrior { EligibleSlots = 2, FiniteSeedSupport = [11, 22, 33] };
        var target = Packet(); var created = new List<ToyWorld>();
        bool Match(NativeRunRecipe r) => r.Seed == 11 || r is { Seed: 22, Slot: 0 };
        Task<ITeacherWorld?> Open(NativeRunRecipe r, CancellationToken _)
        {
            if (r.Seed == 33) return Task.FromResult<ITeacherWorld?>(null);
            var w = new ToyWorld(r, Match(r) ? Packet() : Packet(69)); created.Add(w);
            return Task.FromResult<ITeacherWorld?>(w);
        }
        var exact = prior.EnumerateFiniteRecipes().Where(Match).ToArray();
        Assert.Equal(6, prior.EnumerateFiniteRecipes().Count());
        Assert.Equal(3, exact.Length); Assert.Equal(2, exact.Count(r => r.Seed == 11));
        var source = new NativeRunReplaySource(target, prior, open: Open);
        int seed11 = 0;
        for (ulong seed = 0; seed < 4096; seed++)
        {
            await using var world = (ToyWorld)await source.SampleWorldAsync(seed, 100);
            if (world.Recipe.Seed == 11) seed11++;
        }
        Assert.InRange(seed11 / 4096d, 2d / 3 - .035, 2d / 3 + .035);
        Assert.Contains(source.ProposalAudit, a => a.Status == "absent_under_declared_source_horizon");
        Assert.Contains(source.ProposalAudit, a => a.Status == "public_packet_mismatch");
        Assert.Equal(4096, source.ProposalAudit.Count(a => a.Status == "accepted"));
        Assert.All(created, w => Assert.True(w.Disposed));
    }

    [Fact]
    public async Task FailedComputationIsNeverSilentlyRejectedAndRetried()
    {
        var prior = new NativeRunPrior { EligibleSlots = 1, FiniteSeedSupport = [11] };
        foreach (Exception failure in new Exception[] { new OperationCanceledException("wall budget"), new InvalidOperationException("engine failed") })
        {
            int calls = 0;
            Task<ITeacherWorld?> Open(NativeRunRecipe _, CancellationToken __) { calls++; return Task.FromException<ITeacherWorld?>(failure); }
            var source = new NativeRunReplaySource(Packet(), prior, open: Open);
            Assert.Same(failure, await Record.ExceptionAsync(() => source.SampleWorldAsync(0, 100)));
            Assert.Equal(1, calls); Assert.Single(source.ProposalAudit);
            Assert.Equal(failure is OperationCanceledException ? "computation_cancelled" : "proposal_engine_error", source.ProposalAudit[0].Status);
        }
        var rejected = new NativeRunReplaySource(Packet(), prior, open: (_, _) => Task.FromResult<ITeacherWorld?>(null));
        await Assert.ThrowsAsync<PosteriorSamplingException>(() => rejected.SampleWorldAsync(0, 3));
        Assert.Equal(3, rejected.ProposalAudit.Length);
        int cleanupCalls = 0;
        var cleanupFailed = new NativeRunReplaySource(Packet(), prior, open: (recipe, _) =>
        {
            cleanupCalls++;
            return Task.FromResult<ITeacherWorld?>(new ToyWorld(recipe, Packet(69), cleanupFails: true));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanupFailed.SampleWorldAsync(0, 100));
        Assert.Equal(1, cleanupCalls);
        Assert.Single(cleanupFailed.ProposalAudit);
        Assert.Equal("proposal_cleanup_error", cleanupFailed.ProposalAudit[0].Status);
    }

    [Fact]
    public async Task PublicRootAndDeclaredPriorAreFrozenWithoutAnySourceAuditInput()
    {
        var support = new ulong[] { 11, 22 }; var prior = new NativeRunPrior { EligibleSlots = 2, FiniteSeedSupport = support };
        var target = Packet(); var first = new List<NativeRunRecipe>(); var second = new List<NativeRunRecipe>();
        Task<ITeacherWorld?> Open(List<NativeRunRecipe> seen, NativeRunRecipe recipe)
        { seen.Add(recipe); return Task.FromResult<ITeacherWorld?>(new ToyWorld(recipe, Packet())); }
        var a = new NativeRunReplaySource(target, prior, open: (r, _) => Open(first, r));
        var b = new NativeRunReplaySource(PublicJson.Read<DecisionPacket>(PublicJson.Serialize(target)), prior, open: (r, _) => Open(second, r));
        support[0] = 999; target.Actions[0] = new(99, "invalid"); target.Observation!.History[0] = new("invalid", "private audit mutation");
        for (ulong seed = 0; seed < 32; seed++)
        {
            await using var one = await a.SampleWorldAsync(seed, 1);
            await using var two = await b.SampleWorldAsync(seed, 1);
            Assert.Equal(PublicJson.Serialize(one.Observe()), PublicJson.Serialize(two.Observe()));
        }
        Assert.Equal(first, second); Assert.DoesNotContain(first, r => r.Seed == 999);
        Assert.Equal(PublicJson.Serialize(Packet()), PublicJson.Serialize(a.Observe()));
        Assert.Equal(a.Prior.Identity, b.Prior.Identity);
    }

    [Fact]
    public async Task ExactFiniteNativeEnumerationAgreesWithIndependentPublicAcceptance()
    {
        var prior = new NativeRunPrior
        {
            EligibleSlots = 1, FiniteSeedSupport = [11, 22],
            Execution = new(MaxFloors: 1, SourceDecisionHorizon: 64),
        };
        var packets = new Dictionary<NativeRunRecipe, DecisionPacket>();
        foreach (var recipe in prior.EnumerateFiniteRecipes())
        {
            await using var world = await NativeRunWorld.OpenAsync(prior.Execution, recipe.IndependentRunSeed, recipe.Slot);
            Assert.NotNull(world);
            packets[recipe] = world.Observe();
        }
        // Only the detached packet survives source disposal. The sampler has no
        // parameter for that source's seed, native objects, floor or audit trace.
        var target = packets[new(22, 0)];
        var matches = packets.Where(pair => PublicJson.Serialize(pair.Value) == PublicJson.Serialize(target))
            .Select(pair => pair.Key).ToHashSet();
        Assert.NotEmpty(matches);
        var source = new NativeRunReplaySource(target, prior);
        foreach (ulong seed in new ulong[] { 101, 102 })
        {
            await using var world = await source.SampleWorldAsync(seed, 16);
            Assert.Equal(PublicJson.Serialize(target), PublicJson.Serialize(world.Observe()));
            Assert.Contains(source.ProposalAudit.Last(a => a.Status == "accepted").Recipe, matches);
        }
        Assert.All(source.ProposalAudit.Where(a => a.Status == "public_packet_mismatch"),
            audit => Assert.DoesNotContain(audit.Recipe, matches));
    }
}
