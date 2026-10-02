using Nosl.Contracts;
using Nosl.Objectives;

namespace Nosl.Worker;

public sealed record PairPreference(int Preferred, int Other, double Weight);
public sealed record PairEstimate(int First, int Second, double MeanCostDifference, double Lower, double Upper);
public sealed record RankingEvidence(PairPreference[] Pairs, PairEstimate[] Intervals, string Method, string[] Limitations);

/// <summary>Fixed-N paired differences; an overlapping interval never becomes an equivalence proof.</summary>
public static class TeacherRanking
{
    public static RankingEvidence Evaluate(TeacherCandidate[] candidates, ObjectiveProfile profile,
        double? certifiedCostLower = null, double? certifiedCostUpper = null, double alpha = .05)
    {
        if (certifiedCostLower is not double lower || certifiedCostUpper is not double upper)
            return new([], [], "MASKED_NO_CERTIFIED_UTILITY_SUPPORT", ["Point value targets remain empirical expected-utility regression targets; no strong ranking inferred"]);
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower >= upper || alpha <= 0 || alpha >= 1)
            throw new ArgumentException("Invalid fixed-support ranking certificate");
        int comparisons = candidates.Length * (candidates.Length - 1) / 2;
        var pairs = new List<PairPreference>(); var intervals = new List<PairEstimate>();
        for (int i = 0; i < candidates.Length; i++) for (int j = i + 1; j < candidates.Length; j++)
        {
            var a = candidates[i].Outcomes.Select(x => ObjectiveEvaluator.Evaluate(x, profile).Cost).ToArray();
            var b = candidates[j].Outcomes.Select(x => ObjectiveEvaluator.Evaluate(x, profile).Cost).ToArray();
            if (a.Length != b.Length || a.Length == 0 || a.Concat(b).Any(x => x is null)) continue;
            if (a.Concat(b).Any(x => x < lower || x > upper)) throw new InvalidOperationException("Observed outcome contradicts utility support certificate");
            var delta = a.Zip(b, (x, y) => x!.Value - y!.Value).ToArray();
            var interval = FixedSampleEstimates.Mean(delta, delta.Length, lower - upper, upper - lower, alpha / comparisons);
            intervals.Add(new(i, j, delta.Average(), interval.Lower, interval.Upper));
            if (interval.Upper < 0) pairs.Add(new(i, j, 1 - alpha));
            else if (interval.Lower > 0) pairs.Add(new(j, i, 1 - alpha));
        }
        return new(pairs.ToArray(), intervals.ToArray(), "FIXED_N_PAIRED_HOEFFDING_BONFERRONI",
            ["Requires independent final-evaluation worlds and a proved utility support", "Weight is a loss weight, not a calibrated action win probability", "No equivalence set is inferred from overlapping uncertainty intervals"]);
    }

    // Only this finite mechanics family has this certificate. Content execution itself is
    // NOT capped here: broad states simply lack certified strong pair labels.
    public static (double Lower, double Upper)? RestrictedSupport(CombatSession session, ObjectiveProfile profile)
    {
        if (!BeliefSampler.UsesExchangeablePosterior(session)) return null;
        var o = session.Observe().Observation!;
        string[] cards = ["StrikeSilent","DefendSilent","Neutralize","Survivor","AscendersBane","Acrobatics","Backflip","Prepared","ThinkingAhead","DeadlyPoison","Slimed","CloakAndDagger","DaggerThrow","Dash","LegSweep","Blur","DodgeAndRoll","BladeDance","PoisonedStab","Slice","NoxiousFumes","DaggerSpray","Footwork","Shiv"];
        string[] relics = ["RingOfTheSnake","MeatOnTheBone","ChosenCheese"];
        if (o.Relics.Any(x => !relics.Contains(x)) || o.Hand.Concat(o.Discard).Concat(o.Exhaust).Concat(o.UnknownDraw.Select(x => x.Card)).Concat(o.KnownDraw.Select(x => x.Card)).Any(x => !cards.Contains(x.Id))) return null;
        // No supported card produces persistent inventory or maximum HP. Fruit Juice can
        // add at most five per initial bottle, Chosen Cheese once at true settlement.
        int maxHp = session.StartMaxHp + 5 * session.StartPotions.Count(x => x == "FruitJuice") + (o.Relics.Contains("ChosenCheese") ? 1 : 0);
        double resourceRange = profile.MaximumAbsoluteInventoryAdjustment + profile.MaximumAbsolutePermanentAdjustment;
        return (session.StartHp - maxHp - resourceRange,
            profile.DefeatCost + session.StartHp * (1 + profile.DownsideCoefficient) + resourceRange);
    }
}
