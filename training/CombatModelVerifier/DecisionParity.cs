internal sealed record DecisionParityResult(
    int SampleCount,
    double Top1AgreementRate,
    double Top3SetAgreementRate,
    double RankingAgreement,
    bool TieBreakDeterministic,
    int NearTieSampleCount,
    int TieSampleCount,
    long DiscordantPairs,
    long ComparedPairs)
{
    public bool Passed => Top1AgreementRate == 1.0 && Top3SetAgreementRate == 1.0 &&
                          RankingAgreement == 1.0 && TieBreakDeterministic;
}

internal static class DecisionParity
{
    public const double TieTolerance = 1e-4;
    public const double NearTieThreshold = 1e-3;

    public static int[] CanonicalRanking(IReadOnlyList<double> scores, IReadOnlyList<string> actionIds,
        IReadOnlyList<bool> legal, double tieTolerance = TieTolerance)
    {
        var ordered = Enumerable.Range(0, scores.Count).Where(index => legal[index])
            .OrderByDescending(index => scores[index])
            .ThenBy(index => actionIds[index], StringComparer.Ordinal)
            .ToArray();
        var result = new List<int>(ordered.Length);
        var cursor = 0;
        while (cursor < ordered.Length)
        {
            var anchor = scores[ordered[cursor]];
            var end = cursor + 1;
            while (end < ordered.Length && anchor - scores[ordered[end]] < tieTolerance) end++;
            result.AddRange(ordered[cursor..end].OrderBy(index => actionIds[index], StringComparer.Ordinal));
            cursor = end;
        }
        return result.ToArray();
    }

    public static DecisionParityResult Compare(double[,] reference, double[,] candidate,
        IReadOnlyList<string[]> actionIds, bool[,] legalMask, double tieTolerance = TieTolerance)
    {
        var samples = reference.GetLength(0);
        var actions = reference.GetLength(1);
        if (candidate.GetLength(0) != samples || candidate.GetLength(1) != actions ||
            legalMask.GetLength(0) != samples || legalMask.GetLength(1) != actions || actionIds.Count != samples)
            throw new InvalidOperationException("decision parity shapes differ");
        var top1 = 0;
        var top3 = 0;
        var nearTies = 0;
        var ties = 0;
        long concordant = 0;
        long discordant = 0;
        var deterministic = true;
        for (var sample = 0; sample < samples; sample++)
        {
            var ids = actionIds[sample];
            var legalCount = Enumerable.Range(0, actions).Count(index => legalMask[sample, index]);
            if (ids.Length != legalCount)
                throw new InvalidOperationException($"sample {sample} action ids {ids.Length} != legal actions {legalCount}");
            var paddedIds = ids.Concat(Enumerable.Range(ids.Length, actions - ids.Length).Select(index => $"<PAD:{index}>")).ToArray();
            var legal = Enumerable.Range(0, actions).Select(index => legalMask[sample, index]).ToArray();
            var referenceScores = Enumerable.Range(0, actions).Select(index => reference[sample, index]).ToArray();
            var candidateScores = Enumerable.Range(0, actions).Select(index => candidate[sample, index]).ToArray();
            var expected = CanonicalRanking(referenceScores, paddedIds, legal, tieTolerance);
            var actual = CanonicalRanking(candidateScores, paddedIds, legal, tieTolerance);
            deterministic &= expected.SequenceEqual(CanonicalRanking(referenceScores, paddedIds, legal, tieTolerance));
            deterministic &= actual.SequenceEqual(CanonicalRanking(candidateScores, paddedIds, legal, tieTolerance));
            if (expected.Length > 0 && actual.Length > 0 && expected[0] == actual[0]) top1++;
            if (expected.Take(3).ToHashSet().SetEquals(actual.Take(3))) top3++;
            var actualPositions = actual.Select((action, position) => (action, position)).ToDictionary(item => item.action, item => item.position);
            for (var left = 0; left < expected.Length; left++)
            for (var right = left + 1; right < expected.Length; right++)
            {
                if (actualPositions[expected[left]] < actualPositions[expected[right]]) concordant++;
                else discordant++;
            }
            var legalScores = expected.Select(index => referenceScores[index]).OrderByDescending(value => value).ToArray();
            if (legalScores.Length >= 2)
            {
                var margin = legalScores[0] - legalScores[1];
                if (margin < NearTieThreshold) nearTies++;
                if (margin < tieTolerance) ties++;
            }
        }
        var pairs = concordant + discordant;
        return new DecisionParityResult(samples, (double)top1 / Math.Max(samples, 1),
            (double)top3 / Math.Max(samples, 1), (double)(concordant - discordant) / Math.Max(pairs, 1),
            deterministic, nearTies, ties, discordant, pairs);
    }
}
