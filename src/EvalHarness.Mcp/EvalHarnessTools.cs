using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EvalHarness.Core;
using EvalHarness.Runner;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace EvalHarness.Mcp;

/// <summary>
/// MCP tools over the EvalHarness API, so an agent can start an evaluation of a RAG system, read the result and
/// compare it with an earlier run. Results are compact JSON summaries; the full report (with retrieved chunk text)
/// stays behind the API.
/// </summary>
[McpServerToolType]
public static class EvalHarnessTools
{
    public const int MaxWaitSeconds = 120;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // The output is read by a model, not embedded in HTML: keep apostrophes and such readable instead of escaping them as unicode.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool(Name = "list_datasets", ReadOnly = true), Description(
        "Lists the evaluation datasets the EvalHarness server can run (names to pass to start_run).")]
    public static async Task<string> ListDatasets(EvalHarnessClient client, CancellationToken cancellationToken) =>
        Serialize(new { datasets = await client.GetDatasetsAsync(cancellationToken) });

    [McpServerTool(Name = "start_run", ReadOnly = false, Destructive = false), Description(
        "Starts an asynchronous evaluation run of a RAG API against a dataset and returns immediately with a run id and status 'Queued'. " +
        "This spends LLM budget (judge and embedding calls), so start only runs you intend to read. " +
        "Poll with get_run (waitSeconds lets the server wait for you), then read the result with get_summary.")]
    public static async Task<string> StartRun(
        EvalHarnessClient client,
        [Description("Dataset name from list_datasets, e.g. 'acme-handbook'.")] string dataset,
        [Description("Optional RAG API base URL for this run; must be on the server's allowlist. Omit to use the server's default.")] string? ragUrl = null,
        [Description("Optional subset of evaluators: exact-match, cosine-similarity, answer-correctness, faithfulness, retrieval-quality, retrieval-relevance. Omit for the configured defaults.")] string[]? evaluators = null,
        [Description("Optional concurrent questions, 1 to 32.")] int? parallelism = null,
        [Description("Optional 1 to 20. Asks every question this many times and merges the attempts (majority verdict, mean score) to smooth LLM noise, at N times the cost. 3 is a good value for gating.")] int? repeats = null,
        [Description("Optional extra fields sent to the RAG API with every question, e.g. {\"mode\": \"Vector\", \"topK\": \"10\"}. Only names the server allows (by default topK and mode) are accepted. Use this to compare retrieval settings.")] Dictionary<string, string>? requestFields = null,
        [Description("Optional run id of an earlier completed run to compare against for regression detection.")] string? baselineRunId = null,
        CancellationToken cancellationToken = default)
    {
        var run = await client.StartRunAsync(new StartRunRequest
        {
            Dataset = dataset,
            RagUrl = Blank(ragUrl),
            Evaluators = evaluators is { Length: > 0 } ? evaluators : null,
            Parallelism = parallelism is > 0 ? parallelism : null,
            Repeats = repeats is > 0 ? repeats : null,
            RequestFields = requestFields is { Count: > 0 } ? requestFields : null,
            BaselineRunId = Blank(baselineRunId) is { } id ? EvalHarnessClient.RequireRunId(id, "baselineRunId") : null,
        }, cancellationToken);
        return Serialize(run);
    }

    [McpServerTool(Name = "get_run", ReadOnly = true), Description(
        "Returns a run's status and progress ('Queued', 'Running', 'Completed', 'Failed' or 'Cancelled'; progress counts tests). " +
        "Once finished it also gives passed, exitCode and outcome (Passed, QualityFailure, Inconclusive, Cancelled). " +
        "Set waitSeconds (up to 120) to wait for the run to finish instead of polling; the current status is returned when the time is up.")]
    public static async Task<string> GetRun(
        EvalHarnessClient client,
        [Description("The run id returned by start_run.")] string runId,
        [Description("Seconds to wait for the run to finish, 0 to 120. Default 0 returns immediately.")] int waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
        while (true)
        {
            var run = await client.GetRunAsync(runId, cancellationToken);
            if (run.IsFinished || DateTime.UtcNow + client.PollInterval > deadline) return Serialize(run);
            await Task.Delay(client.PollInterval, cancellationToken);
        }
    }

    [McpServerTool(Name = "get_summary", ReadOnly = true), Description(
        "Summarises a finished run: the verdict (passed / outcome), headline metrics (pass rate, answer correctness, faithfulness, cosine, " +
        "recall@1/3/5, refusal rate, latency), the run settings (models, prompt fingerprint, repeats, request fields), the quality gates, " +
        "the regression comparison if a baseline was used, and up to 20 problem tests. Each problem has a likelyCause " +
        "(retrieval-miss, refused-despite-retrieval, unsupported-claims, wrong-answer-despite-retrieval, rag-api-error, evaluator-error), " +
        "the question, expected and actual answer, evaluator scores and the top retrieved sources (names and ranks, not text). Flaky tests are marked.")]
    public static async Task<string> GetSummary(
        EvalHarnessClient client,
        [Description("The run id returned by start_run. The run must be finished.")] string runId,
        CancellationToken cancellationToken = default)
    {
        var run = await client.GetRunAsync(runId, cancellationToken);
        if (!run.IsFinished)
            throw new McpException($"Run {run.Id} is still {run.Status} ({run.Progress.Completed}/{run.Progress.Total} tests). Call get_run with waitSeconds first.");

        EvaluationRun report;
        try
        {
            report = await client.GetReportAsync(run.Id, cancellationToken);
        }
        catch (McpException) when (run.Status is "Failed" or "Cancelled")
        {
            // Runs that never started (or could not run) have no report; the record itself says why.
            throw new McpException($"Run {run.Id} ended as {run.Status} without a report. {run.Error}".Trim());
        }

        return Serialize(RunSummaries.Summarize(report));
    }

    [McpServerTool(Name = "compare_runs", ReadOnly = true), Description(
        "Compares two finished runs and reports which metrics regressed, which tests newly fail or newly pass, and warnings when the runs " +
        "are not like-for-like (different judge model, prompts, thresholds, retrieval request fields, repeats or dataset). " +
        "Uses the default regression tolerances (0.05 per metric, never smaller than one test's worth, +25% latency).")]
    public static async Task<string> CompareRuns(
        EvalHarnessClient client,
        [Description("The earlier, known-good run to compare against.")] string baselineRunId,
        [Description("The run being judged.")] string currentRunId,
        CancellationToken cancellationToken = default)
    {
        // Validate both ids before sending anything: a model-supplied id must never cost a request.
        var baselineId = EvalHarnessClient.RequireRunId(baselineRunId, "baselineRunId");
        var currentId = EvalHarnessClient.RequireRunId(currentRunId, "currentRunId");
        var baseline = await client.GetReportAsync(baselineId, cancellationToken);
        var current = await client.GetReportAsync(currentId, cancellationToken);
        return Serialize(RunSummaries.Summarize(RegressionDetector.Compare(baseline, current, new RegressionOptions())));
    }

    [McpServerTool(Name = "cancel_run", Destructive = true), Description(
        "Cancels a queued or running run. A running one keeps a partial report. Use this to stop an expensive run you started by mistake.")]
    public static async Task<string> CancelRun(
        EvalHarnessClient client,
        [Description("The run id returned by start_run.")] string runId,
        CancellationToken cancellationToken = default) =>
        Serialize(await client.CancelRunAsync(runId, cancellationToken));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
}
