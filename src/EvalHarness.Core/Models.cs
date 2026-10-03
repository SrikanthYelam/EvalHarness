namespace EvalHarness.Core;

/// <summary>
/// Identifies a source the RAG system is expected to retrieve. Every identifier that is set must match
/// (document id, document name, chunk id, and/or text the chunk must contain); at least one is required.
/// </summary>
public sealed record ExpectedSource
{
    public string? DocumentId { get; init; }
    public string? DocumentName { get; init; }
    public string? ChunkId { get; init; }

    /// <summary>Useful when chunk ids are not stable between ingestions: a snippet the right chunk must contain.</summary>
    public string? ChunkContains { get; init; }

    public bool HasIdentifier =>
        !string.IsNullOrWhiteSpace(DocumentId) || !string.IsNullOrWhiteSpace(DocumentName) ||
        !string.IsNullOrWhiteSpace(ChunkId) || !string.IsNullOrWhiteSpace(ChunkContains);

    public override string ToString() =>
        string.Join(", ", new[]
        {
            DocumentId is null ? null : $"documentId={DocumentId}",
            DocumentName is null ? null : $"documentName={DocumentName}",
            ChunkId is null ? null : $"chunkId={ChunkId}",
            ChunkContains is null ? null : $"chunkContains=\"{ChunkContains}\"",
        }.Where(s => s is not null));
}

public sealed record TestCase
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Difficulty { get; init; } = "";
    public string Question { get; init; } = "";
    public string ExpectedAnswer { get; init; } = "";
    public IReadOnlyList<ExpectedSource> ExpectedSources { get; init; } = [];

    /// <summary>Optional: restrict this test to the named evaluators. Null runs every enabled evaluator.</summary>
    public IReadOnlyList<string>? Evaluators { get; init; }
}

public sealed record Dataset(
    string Name,
    string? Description,
    string Path,
    string Sha256,
    IReadOnlyList<TestCase> TestCases);

/// <summary>A chunk the RAG API returned, in retrieval order (Rank is 1-based).</summary>
public sealed record RetrievedChunk(
    int Rank,
    string? DocumentId,
    string? DocumentName,
    string? ChunkId,
    string Text,
    double? Score);

public sealed record RagResponse(
    string Question,
    string? Answer,
    string? Status,
    IReadOnlyList<RetrievedChunk> RetrievedChunks,
    double LatencyMs,
    int Attempts);

/// <summary>The RAG API could not produce a response (network, timeout, HTTP error, unusable payload).</summary>
public sealed class RagApiException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

public enum EvaluatorStatus { Passed, Failed, Skipped, Error }

public sealed record EvaluatorResult(
    string Evaluator,
    EvaluatorStatus Status,
    double? Score = null,
    string? Explanation = null,
    IReadOnlyDictionary<string, double>? Metrics = null,
    string? Error = null)
{
    /// <summary>Whether a failure of this evaluator fails the test case. Stamped by the runner from configuration.</summary>
    public bool Gating { get; init; }

    public static EvaluatorResult Of(string evaluator, bool passed, double score, string explanation,
        IReadOnlyDictionary<string, double>? metrics = null) =>
        new(evaluator, passed ? EvaluatorStatus.Passed : EvaluatorStatus.Failed, score, explanation, metrics);

    public static EvaluatorResult Skipped(string evaluator, string reason) =>
        new(evaluator, EvaluatorStatus.Skipped, Explanation: reason);

    public static EvaluatorResult Errored(string evaluator, string error) =>
        new(evaluator, EvaluatorStatus.Error, Error: error);
}

/// <summary>
/// Passed/Failed describe the RAG answer. ApiError (RAG API/infrastructure) and EvaluatorError (a judge,
/// embedding provider or evaluator bug) say nothing about answer quality.
/// </summary>
public enum TestOutcome { Passed, Failed, ApiError, EvaluatorError }

public sealed record TestResult(
    string Id,
    string Category,
    string Difficulty,
    string Question,
    string ExpectedAnswer,
    TestOutcome Outcome,
    string? Answer,
    string? AnswerStatus,
    IReadOnlyList<RetrievedChunk> RetrievedChunks,
    double? LatencyMs,
    int Attempts,
    double DurationMs,
    IReadOnlyList<EvaluatorResult> EvaluatorResults,
    string? FailureReason);

/// <summary>Rates and averages are null when no test contributed a value (e.g. the evaluator was disabled).</summary>
public sealed record AggregateMetrics(
    int TotalTests,
    int Passed,
    int Failed,
    int ApiErrors,
    int EvaluatorErrors,
    double? PassRate,
    double? ExactMatchRate,
    double? AverageCosineSimilarity,
    double? AnswerCorrectness,
    double? Faithfulness,
    double? RetrievalRelevance,
    double? RecallAt1,
    double? RecallAt3,
    double? RecallAt5,
    double? MeanExpectedSourceRank,
    double? AverageLatencyMs,
    double? P95LatencyMs,
    double TotalExecutionMs);

public sealed record RunInfo(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    double DurationMs,
    bool Cancelled,
    string HarnessVersion,
    string Target,
    int MaxParallelism,
    IReadOnlyList<string> Evaluators);

public sealed record DatasetInfo(string Name, string Path, string Sha256, int TestCount);

public sealed record EvaluationRun(
    int SchemaVersion,
    RunInfo Run,
    DatasetInfo Dataset,
    AggregateMetrics Aggregate,
    IReadOnlyList<TestResult> Results)
{
    public const int CurrentSchemaVersion = 1;

    public GateReport? QualityGates { get; init; }
    public RegressionReport? Regression { get; init; }
}

public enum GateKind
{
    /// <summary>The RAG system's quality is below the bar.</summary>
    Quality,

    /// <summary>The run is inconclusive (API/evaluator errors, or a metric could not be computed).</summary>
    Reliability,
}

public sealed record GateCheck(string Name, GateKind Kind, string Comparison, double? Actual, double Threshold, bool Passed);

public sealed record GateReport(bool Passed, IReadOnlyList<GateCheck> Checks);

public sealed record MetricComparison(
    string Metric,
    bool HigherIsBetter,
    double? Baseline,
    double? Current,
    double? Delta,
    string Allowed,
    bool Regressed);

public sealed record RegressionReport(
    bool HasRegression,
    string BaselineRunId,
    string CurrentRunId,
    IReadOnlyList<MetricComparison> Metrics,
    IReadOnlyList<string> RegressedTests,
    IReadOnlyList<string> ImprovedTests,
    IReadOnlyList<string> InconclusiveTests,
    IReadOnlyList<string> NewTests,
    IReadOnlyList<string> RemovedTests,
    IReadOnlyList<string> Warnings);
