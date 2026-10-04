using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

/// <summary>Combat-start facts and the public decision shared by teacher sources and owned worlds.</summary>
internal interface ITeacherContext
{
    int StartHp { get; }
    int StartMaxHp { get; }
    string?[] StartPotions { get; }
    DecisionPacket Observe();
}

/// <summary>An independently owned continuation. Policy calls still receive public DTOs only.</summary>
internal interface ITeacherWorld : ITeacherContext, IAsyncDisposable
{
    double SettlementSeconds { get; }
    Task<DecisionPacket> StepAsync(PublicAction action);
    Task<ITeacherWorld> ForkForContinuationAsync();
    Task<RolloutOutcome> RecordSettledAsync(string policyId, int lastPlayerTurn);
}

/// <summary>A posterior source owns sampling and its applicable utility-support certificate.</summary>
internal interface ITeacherSource : ITeacherContext
{
    string PosteriorProfile { get; }
    string PriorWarning { get; }
    Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts);
    (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile);
}

/// <summary>Non-owning source adapter; the caller retains the original combat session.</summary>
internal sealed class CombatSessionTeacherSource(CombatSession session) : ITeacherSource
{
    public int StartHp => session.StartHp;
    public int StartMaxHp => session.StartMaxHp;
    public string?[] StartPotions => session.StartPotions;
    public DecisionPacket Observe() => session.Observe();
    public string PosteriorProfile => BeliefSampler.PosteriorProfileFor(session);
    public string PriorWarning => session.HasNativeProvenance
        ? "Certified native carry-in; conditional permutation and independent future RNG, not finite-source-seed inference; frozen public continuation"
        : "Declared setup prior and runtime capability limits apply; Q under a frozen public continuation, not Q-star";
    public async Task<ITeacherWorld> SampleWorldAsync(ulong seed, int maxAttempts)
        => new CombatSessionTeacherWorld(await BeliefSampler.SampleWorldAsync(session, seed, maxAttempts));
    public (double Lower, double Upper)? RankingSupport(ObjectiveProfile profile)
        => TeacherRanking.RestrictedSupport(session, profile);
}

/// <summary>Owns one sampled or forked session and records its existing settled facts unchanged.</summary>
internal sealed class CombatSessionTeacherWorld(CombatSession session) : ITeacherWorld
{
    public int StartHp => session.StartHp;
    public int StartMaxHp => session.StartMaxHp;
    public string?[] StartPotions => session.StartPotions;
    public double SettlementSeconds => session.SettlementSeconds;
    public DecisionPacket Observe() => session.Observe();
    public Task<DecisionPacket> StepAsync(PublicAction action) => session.StepAsync(action);
    public async Task<ITeacherWorld> ForkForContinuationAsync()
        => new CombatSessionTeacherWorld(await session.ForkForContinuationAsync());
    public async Task<RolloutOutcome> RecordSettledAsync(string policyId, int lastPlayerTurn)
        => RolloutRecorder.Settled(session, await session.SettleAsync(), policyId, lastPlayerTurn);
    public ValueTask DisposeAsync() => session.DisposeAsync();
}
