using EvalHarness.Core;

namespace EvalHarness.Evaluators;

public static class SourceMatcher
{
    /// <summary>True when the chunk satisfies every identifier set on the expected source.</summary>
    public static bool Matches(ExpectedSource expected, RetrievedChunk chunk)
    {
        if (!expected.HasIdentifier) return false;
        if (Set(expected.DocumentId) && !Same(expected.DocumentId, chunk.DocumentId)) return false;
        if (Set(expected.ChunkId) && !Same(expected.ChunkId, chunk.ChunkId)) return false;
        if (Set(expected.DocumentName) && !Same(FileName(expected.DocumentName), FileName(chunk.DocumentName))) return false;
        if (Set(expected.ChunkContains) &&
            chunk.Text.IndexOf(expected.ChunkContains!, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    private static bool Set(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // "docs/handbook.md" and "handbook.md" name the same document.
    private static string? FileName(string? name) => name is null ? null : Path.GetFileName(name.Replace('\\', '/'));
}

public sealed record RetrievalMetrics(IReadOnlyList<int?> Ranks)
{
    /// <summary>Fraction of expected sources found within the first <paramref name="k"/> retrieved chunks.</summary>
    public double RecallAt(int k) => Ranks.Count == 0 ? 0 : Ranks.Count(r => r is not null && r <= k) / (double)Ranks.Count;

    /// <summary>1-based rank of the best-placed expected source, or null when none was retrieved.</summary>
    public int? BestRank => Ranks.Where(r => r is not null).Min();

    public static RetrievalMetrics Compute(IReadOnlyList<ExpectedSource> expected, IReadOnlyList<RetrievedChunk> retrieved)
    {
        var ranks = expected.Select(source =>
        {
            for (var i = 0; i < retrieved.Count; i++)
                if (SourceMatcher.Matches(source, retrieved[i])) return (int?)(i + 1);
            return null;
        }).ToList();
        return new RetrievalMetrics(ranks);
    }
}

/// <summary>
/// Did the retriever surface the expected source(s)? Reports Recall@1/3/5 and the rank of the expected source;
/// passes when Recall@K reaches the threshold. Independent of whether the answer was good.
/// </summary>
public sealed class RetrievalQualityEvaluator(int k, double threshold) : IEvaluator
{
    public string Name => EvaluatorNames.RetrievalQuality;

    public Task<EvaluatorResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken)
    {
        var expected = context.TestCase.ExpectedSources;
        if (expected.Count == 0) return Task.FromResult(EvaluatorResult.Skipped(Name, "The test case lists no expected sources."));

        var metrics = RetrievalMetrics.Compute(expected, context.Response.RetrievedChunks);
        var recall = metrics.RecallAt(k);
        var values = new Dictionary<string, double>
        {
            ["recall@1"] = metrics.RecallAt(1),
            ["recall@3"] = metrics.RecallAt(3),
            ["recall@5"] = metrics.RecallAt(5),
        };
        if (metrics.BestRank is { } rank) values["expectedSourceRank"] = rank;

        var missing = expected.Where((_, i) => metrics.Ranks[i] is null).ToList();
        var explanation = missing.Count == 0
            ? $"All expected sources retrieved (best rank {metrics.BestRank}; {context.Response.RetrievedChunks.Count} chunks returned)."
            : $"Expected source not retrieved: {string.Join(" | ", missing)} ({context.Response.RetrievedChunks.Count} chunks returned).";

        return Task.FromResult(EvaluatorResult.Of(Name, recall >= threshold, recall,
            $"Recall@{k} = {recall:0.00}. {explanation}", values));
    }
}
