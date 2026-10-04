namespace EvalHarness.Api;

public enum RunStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>Body of POST /runs. Only <see cref="Dataset"/> is required.</summary>
public sealed record RunRequest
{
    /// <summary>Dataset file name without .json (see GET /datasets).</summary>
    public string Dataset { get; init; } = "";

    /// <summary>Optional override of the RAG API base URL; must match the server's allowlist.</summary>
    public string? RagUrl { get; init; }

    /// <summary>Optional subset of evaluators to run (see GET /evaluators). Omitted runs the configured ones.</summary>
    public IReadOnlyList<string>? Evaluators { get; init; }

    public int? Parallelism { get; init; }

    /// <summary>Id of an earlier completed run to compare against for regression detection.</summary>
    public string? BaselineRunId { get; init; }
}

public sealed record RunProgress(int Completed, int Total);

/// <summary>
/// Status of a run. <see cref="Passed"/>, <see cref="ExitCode"/> and <see cref="Outcome"/> use the same meaning as
/// the CLI exit codes: 0 Passed, 1 QualityFailure, 3 Inconclusive, 130 Cancelled.
/// </summary>
public sealed record RunRecord
{
    public string Id { get; init; } = "";
    public RunStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Dataset { get; init; } = "";
    public string Target { get; init; } = "";
    public RunRequest Request { get; init; } = new();
    public RunProgress Progress { get; init; } = new(0, 0);
    public bool? Passed { get; init; }
    public int? ExitCode { get; init; }
    public string? Outcome { get; init; }
    public string? Error { get; init; }

    public bool IsFinished => Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
}
