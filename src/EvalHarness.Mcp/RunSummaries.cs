using EvalHarness.Core;
using EvalHarness.Runner;

namespace EvalHarness.Mcp;

public sealed record Verdict(bool Passed, string Outcome, int ExitCode, string Meaning);
public sealed record TestCounts(int Total, int Passed, int Failed, int ApiErrors, int EvaluatorErrors, int Flaky);
public sealed record GateSummary(string Name, string Kind, string Comparison, double? Actual, double Threshold, bool Passed);
public sealed record MetricChange(string Metric, double? Baseline, double? Current, double? Delta, string Allowed, bool Regressed);
public sealed record EvaluatorScore(string Name, string Status, double? Score, bool Gating);
public sealed record SourceRef(int Rank, string? Document, string? ChunkId, double? Score);

public sealed record MetricsSummary(
    double? PassRate, double? AnswerCorrectness, double? Faithfulness, double? CosineSimilarity, double? ExactMatchRate,
    double? RetrievalRelevance, double? RecallAt1, double? RecallAt3, double? RecallAt5, double? MeanExpectedSourceRank,
    double? RefusalRate, double? AverageLatencyMs, double? P95LatencyMs);

public sealed record RegressionSummary(
    bool HasRegression,
    string BaselineRunId,
    string CurrentRunId,
    IReadOnlyList<MetricChange> Metrics,
    IReadOnlyList<string> RegressedTests,
    IReadOnlyList<string> ImprovedTests,
    IReadOnlyList<string> InconclusiveTests,
    IReadOnlyList<string> NewTests,
    IReadOnlyList<string> RemovedTests,
    IReadOnlyList<string> Warnings);

/// <param name="LikelyCause">
/// Where the pipeline most likely went wrong, derived from the evaluator results: rag-api-error, evaluator-error,
/// retrieval-miss, refused-despite-retrieval, unsupported-claims, wrong-answer-despite-retrieval or other.
/// </param>
public sealed record ProblemSummary(
    string Id,
    string Category,
    string Difficulty,
    string Outcome,
    bool Flaky,
    string LikelyCause,
    string Question,
    string Expected,
    string? Answer,
    string? Reason,
    IReadOnlyList<EvaluatorScore> Evaluators,
    IReadOnlyList<SourceRef> TopSources);

public sealed record RunSummary(
    string RunId,
    string Dataset,
    string Target,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    bool Cancelled,
    Verdict Verdict,
    RunSettings? Settings,
    TestCounts Tests,
    MetricsSummary Metrics,
    IReadOnlyList<GateSummary> Gates,
    RegressionSummary? Regression,
    IReadOnlyList<ProblemSummary> Problems,
    int MoreProblems);

/// <summary>
/// Turns a full report into something an agent can reason about in one read. Retrieved chunk text is deliberately
/// left out (it can be large and may contain personal data); the ranks, document names and scores are kept, which
/// is what triage needs.
/// </summary>
public static class RunSummaries
{
    public const int MaxProblems = 20;

    public static RunSummary Summarize(EvaluationRun report)
    {
        var a = report.Aggregate;
        var exit = ExitCodes.For(report);

        var problems = report.Results.Where(r => r.Outcome != TestOutcome.Passed || r.Flaky).ToList();
        return new RunSummary(
            report.Run.RunId,
            report.Dataset.Name,
            report.Run.Target,
            report.Run.StartedAt,
            Math.Round(report.Run.DurationMs / 1000, 1),
            report.Run.Cancelled,
            new Verdict(exit == ExitCodes.Success, OutcomeFor(exit), exit, MeaningFor(exit)),
            report.Run.Settings,
            new TestCounts(a.TotalTests, a.Passed, a.Failed, a.ApiErrors, a.EvaluatorErrors, a.FlakyTests),
            new MetricsSummary(
                a.PassRate, a.AnswerCorrectness, a.Faithfulness, a.AverageCosineSimilarity, a.ExactMatchRate, a.RetrievalRelevance,
                a.RecallAt1, a.RecallAt3, a.RecallAt5, a.MeanExpectedSourceRank, a.RefusalRate, a.AverageLatencyMs, a.P95LatencyMs),
            report.QualityGates?.Checks.Select(c => new GateSummary(c.Name, c.Kind.ToString(), c.Comparison, c.Actual, c.Threshold, c.Passed)).ToList() ?? [],
            report.Regression is { } regression ? Summarize(regression) : null,
            problems.Take(MaxProblems).Select(Summarize).ToList(),
            Math.Max(0, problems.Count - MaxProblems));
    }

    public static RegressionSummary Summarize(RegressionReport r) => new(
        r.HasRegression, r.BaselineRunId, r.CurrentRunId,
        r.Metrics.Select(m => new MetricChange(m.Metric, m.Baseline, m.Current, m.Delta, m.Allowed, m.Regressed)).ToList(),
        r.RegressedTests, r.ImprovedTests, r.InconclusiveTests, r.NewTests, r.RemovedTests, r.Warnings);

    public static ProblemSummary Summarize(TestResult t) => new(
        t.Id, t.Category, t.Difficulty, t.Outcome.ToString(), t.Flaky, LikelyCause(t),
        Truncate(t.Question, 200)!, Truncate(t.ExpectedAnswer, 300)!, Truncate(t.Answer, 300), Truncate(t.FailureReason, 400),
        t.EvaluatorResults.Select(e => new EvaluatorScore(e.Evaluator, e.Status.ToString(), e.Score, e.Gating)).ToList(),
        t.RetrievedChunks.Take(5).Select(c => new SourceRef(c.Rank, c.DocumentName ?? c.DocumentId, c.ChunkId, c.Score)).ToList());

    /// <summary>
    /// Mirrors how the evaluators relate: a retrieval miss explains everything downstream, so it is checked first;
    /// only when the right chunk was retrieved do we blame the answer.
    /// </summary>
    public static string LikelyCause(TestResult t)
    {
        if (t.Outcome == TestOutcome.ApiError) return "rag-api-error";
        if (t.Outcome == TestOutcome.EvaluatorError) return "evaluator-error";

        bool Failed(string evaluator) => t.EvaluatorResults.Any(e => e.Evaluator == evaluator && e.Status == EvaluatorStatus.Failed);
        if (Failed(EvaluatorNames.RetrievalQuality)) return "retrieval-miss";
        if (string.IsNullOrWhiteSpace(t.Answer)) return "refused-despite-retrieval";
        if (Failed(EvaluatorNames.Faithfulness)) return "unsupported-claims";
        if (Failed(EvaluatorNames.AnswerCorrectness)) return "wrong-answer-despite-retrieval";
        return "other";
    }

    public static string OutcomeFor(int exitCode) => exitCode switch
    {
        ExitCodes.Success => "Passed",
        ExitCodes.QualityFailure => "QualityFailure",
        ExitCodes.Inconclusive => "Inconclusive",
        ExitCodes.Cancelled => "Cancelled",
        _ => "Failed",
    };

    private static string MeaningFor(int exitCode) => exitCode switch
    {
        ExitCodes.Success => "Every quality gate passed and there was no regression.",
        ExitCodes.QualityFailure => "A quality gate failed or a regression against the baseline was detected.",
        ExitCodes.Inconclusive => "RAG API or evaluator errors left the run inconclusive; this says nothing about answer quality.",
        ExitCodes.Cancelled => "The run was cancelled; results cover only the tests that completed.",
        _ => "The run failed.",
    };

    public static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "…";
}
