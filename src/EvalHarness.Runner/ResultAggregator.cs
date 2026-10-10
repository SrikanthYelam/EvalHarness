using EvalHarness.Core;

namespace EvalHarness.Runner;

public static class ResultAggregator
{
    /// <summary>
    /// Averages only count evaluator results that produced a verdict (passed or failed); skipped and errored
    /// results, and tests whose RAG call failed, never drag a quality metric down.
    /// </summary>
    public static AggregateMetrics Aggregate(IReadOnlyList<TestResult> results, double totalExecutionMs)
    {
        int Count(TestOutcome o) => results.Count(r => r.Outcome == o);
        var passed = Count(TestOutcome.Passed);
        var failed = Count(TestOutcome.Failed);

        // Tests where the RAG API actually responded; an empty answer there is a refusal, not an outage.
        var answered = results.Where(r => r.Outcome != TestOutcome.ApiError).ToList();
        var latencies = results.Where(r => r.Outcome != TestOutcome.ApiError && r.LatencyMs is not null)
            .Select(r => r.LatencyMs!.Value).Order().ToList();

        return new AggregateMetrics(
            TotalTests: results.Count,
            Passed: passed,
            Failed: failed,
            ApiErrors: Count(TestOutcome.ApiError),
            EvaluatorErrors: Count(TestOutcome.EvaluatorError),
            PassRate: passed + failed == 0 ? null : passed / (double)(passed + failed),
            ExactMatchRate: Average(Verdicts(results, EvaluatorNames.ExactMatch).Select(r => r.Score)),
            AverageCosineSimilarity: Average(Verdicts(results, EvaluatorNames.CosineSimilarity).Select(r => r.Score)),
            AnswerCorrectness: Average(Verdicts(results, EvaluatorNames.AnswerCorrectness).Select(r => r.Score)),
            Faithfulness: Average(Verdicts(results, EvaluatorNames.Faithfulness).Select(r => r.Score)),
            RetrievalRelevance: Average(Verdicts(results, EvaluatorNames.RetrievalRelevance).Select(r => r.Score)),
            RecallAt1: Average(RetrievalMetric(results, "recall@1")),
            RecallAt3: Average(RetrievalMetric(results, "recall@3")),
            RecallAt5: Average(RetrievalMetric(results, "recall@5")),
            MeanExpectedSourceRank: Average(RetrievalMetric(results, "expectedSourceRank")),
            AverageLatencyMs: Average(latencies.Select(l => (double?)l)),
            P95LatencyMs: latencies.Count == 0 ? null : latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1],
            TotalExecutionMs: totalExecutionMs,
            RefusalRate: answered.Count == 0 ? null : answered.Count(r => string.IsNullOrWhiteSpace(r.Answer)) / (double)answered.Count,
            FlakyTests: results.Count(r => r.Flaky));
    }

    private static IEnumerable<EvaluatorResult> Verdicts(IReadOnlyList<TestResult> results, string evaluator) =>
        results.SelectMany(r => r.EvaluatorResults)
            .Where(e => e.Evaluator == evaluator && (e.Status is EvaluatorStatus.Passed or EvaluatorStatus.Failed) && e.Score is not null);

    private static IEnumerable<double?> RetrievalMetric(IReadOnlyList<TestResult> results, string metric) =>
        Verdicts(results, EvaluatorNames.RetrievalQuality)
            .Select(e => e.Metrics is not null && e.Metrics.TryGetValue(metric, out var v) ? (double?)v : null);

    private static double? Average(IEnumerable<double?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }
}
