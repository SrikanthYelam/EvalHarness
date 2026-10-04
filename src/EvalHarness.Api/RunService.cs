using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EvalHarness.Core;
using EvalHarness.Hosting;
using EvalHarness.Runner;
using Microsoft.Extensions.Options;

namespace EvalHarness.Api;

/// <summary>Lets tests replace the RAG/LLM clients used by runs. Not used in production.</summary>
public sealed class EvaluationServicesOverride(Action<IServiceCollection> configure)
{
    public void Apply(IServiceCollection services) => configure(services);
}

/// <summary>Why a run could not be started; carries the HTTP status to return.</summary>
public sealed record StartFailure(int StatusCode, string Title, IReadOnlyList<string>? Details = null);

public sealed record StartResult(RunRecord? Run, StartFailure? Failure);

/// <summary>
/// Accepts runs, validates them up front (so mistakes fail the POST, not minutes later), queues them behind a
/// concurrency limit, runs them in the background and keeps the history. Each run gets its own wired evaluation,
/// which is how a per-request RAG URL works without touching the others.
/// </summary>
public sealed partial class RunService : IHostedService
{
    private readonly ApiOptions _options;
    private readonly IConfiguration _config;
    private readonly RunStore _store;
    private readonly ILogger<RunService> _logger;
    private readonly EvaluationServicesOverride? _servicesOverride;
    private readonly ConcurrentDictionary<string, RunRecord> _runs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<string, Task> _tasks = new();
    private readonly SemaphoreSlim _slots;
    private readonly object _admission = new();
    private volatile bool _stopping;

    public RunService(
        IOptions<ApiOptions> options, IConfiguration config, ILogger<RunService> logger,
        EvaluationServicesOverride? servicesOverride = null)
    {
        _options = options.Value;
        _config = config;
        _logger = logger;
        _servicesOverride = servicesOverride;
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRuns));
        _store = new RunStore(_options.DataDirectory);

        foreach (var record in _store.LoadRecords())
        {
            // A run that was queued or running when the process died will never finish.
            var restored = record.IsFinished
                ? record
                : record with
                {
                    Status = RunStatus.Failed, CompletedAt = DateTimeOffset.UtcNow, Passed = false,
                    Error = "Interrupted by a service restart.",
                };
            _runs[restored.Id] = restored;
            if (!ReferenceEquals(restored, record)) _store.SaveRecordAsync(restored).GetAwaiter().GetResult();
        }
    }

    public IReadOnlyList<RunRecord> List(int limit) =>
        _runs.Values.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit, 1, 500)).ToList();

    public RunRecord? Get(string id) => _runs.GetValueOrDefault(id);

    public Task<EvaluationRun>? GetReport(string id) =>
        _runs.TryGetValue(id, out var record) && record.IsFinished && _store.ReportExists(id) ? _store.ReadReportAsync(id) : null;

    public IReadOnlyList<string> Datasets() =>
        Directory.Exists(_options.DatasetsDirectory)
            ? Directory.EnumerateFiles(_options.DatasetsDirectory, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex DatasetNamePattern();

    public async Task<StartResult> SubmitAsync(RunRequest request)
    {
        static StartResult Fail(int status, string title, IReadOnlyList<string>? details = null) =>
            new(null, new StartFailure(status, title, details));

        // --- dataset ---------------------------------------------------------------------------------------------
        if (!DatasetNamePattern().IsMatch(request.Dataset ?? ""))
            return Fail(400, "'dataset' is required and may contain only letters, digits, '.', '_' and '-'.");
        var datasetPath = Path.Combine(_options.DatasetsDirectory, request.Dataset + ".json");
        if (!File.Exists(datasetPath))
            return Fail(404, $"Unknown dataset '{request.Dataset}'.", [$"Available: {string.Join(", ", Datasets())}"]);

        Dataset dataset;
        try
        {
            dataset = DatasetLoader.Load(datasetPath, EvaluatorNames.All);
        }
        catch (DatasetValidationException ex)
        {
            return Fail(500, $"Dataset '{request.Dataset}' on the server is invalid.", ex.Errors);
        }

        // --- request options -------------------------------------------------------------------------------------
        var overrides = new Dictionary<string, string?>();
        if (request.Evaluators is { } selected)
        {
            var unknown = selected.Where(n => !EvaluatorNames.All.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0 || selected.Count == 0)
                return Fail(400, "Invalid 'evaluators'.", [$"Unknown: {string.Join(", ", unknown)}. Known: {string.Join(", ", EvaluatorNames.All)}."]);
            foreach (var name in EvaluatorNames.All)
                overrides[$"Evaluation:Evaluators:{name}:Enabled"] =
                    selected.Contains(name, StringComparer.OrdinalIgnoreCase) ? "true" : "false";
        }

        if (request.Parallelism is { } parallelism)
        {
            if (parallelism is < 1 or > 32) return Fail(400, "'parallelism' must be between 1 and 32.");
            overrides["Evaluation:MaxParallelism"] = parallelism.ToString();
        }

        if (request.RagUrl is { } ragUrl)
        {
            var policy = new RagUrlPolicy(_config["RagApi:BaseUrl"] ?? "", _options.AllowedRagUrls);
            if (policy.Check(ragUrl) is { } reason) return Fail(400, reason);
            overrides["RagApi:BaseUrl"] = ragUrl;
        }

        EvaluationRun? baseline = null;
        if (request.BaselineRunId is { } baselineId)
        {
            if (!RunStore.IsValidId(baselineId) || Get(baselineId) is not { Status: RunStatus.Completed } || !_store.ReportExists(baselineId))
                return Fail(400, $"'baselineRunId' {baselineId} is not a completed run with a report.");
            baseline = await _store.ReadReportAsync(baselineId);
        }

        // --- wiring (fails fast on bad config or a missing API key) ----------------------------------------------
        var runConfig = new ConfigurationBuilder().AddConfiguration(_config).AddInMemoryCollection(overrides).Build();
        PreparedEvaluation evaluation;
        try
        {
            evaluation = PreparedEvaluation.Create(runConfig, _servicesOverride is null ? null : (Action<IServiceCollection>)_servicesOverride.Apply);
        }
        catch (ConfigurationException ex)
        {
            return Fail(500, $"Server configuration error: {ex.Message}");
        }

        // --- admission -------------------------------------------------------------------------------------------
        var id = RunStore.NewId();
        var record = new RunRecord
        {
            Id = id, Status = RunStatus.Queued, CreatedAt = DateTimeOffset.UtcNow, Dataset = dataset.Name,
            Target = evaluation.Target, Request = request, Progress = new RunProgress(0, dataset.TestCases.Count),
        };
        var cts = new CancellationTokenSource();
        lock (_admission)
        {
            if (_stopping || _runs.Values.Count(r => r.Status == RunStatus.Queued) >= _options.MaxQueuedRuns)
            {
                cts.Dispose();
                evaluation.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return Fail(429, _stopping ? "The service is shutting down." : "Too many queued runs; try again later.");
            }
            _runs[id] = record;
            _cancellations[id] = cts;
        }

        await _store.SaveRecordAsync(record);
        _tasks[id] = Task.Run(() => ExecuteAsync(id, dataset, baseline, evaluation, cts));
        return new StartResult(record, null);
    }

    /// <returns>Null when the run does not exist; otherwise the record and whether the cancel request was accepted.</returns>
    public (RunRecord Run, bool Accepted)? Cancel(string id)
    {
        if (Get(id) is not { } record) return null;
        if (record.IsFinished) return (record, false);
        if (_cancellations.TryGetValue(id, out var cts)) TryCancel(cts);
        return (record, true);
    }

    private async Task ExecuteAsync(
        string id, Dataset dataset, EvaluationRun? baseline, PreparedEvaluation evaluation, CancellationTokenSource cts)
    {
        try
        {
            try
            {
                await _slots.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                await FinishAsync(id, RunStatus.Cancelled, exitCode: ExitCodes.Cancelled,
                    error: _stopping ? "Cancelled because the service was shutting down." : null);
                return;
            }

            try
            {
                Update(id, r => r with { Status = RunStatus.Running, StartedAt = DateTimeOffset.UtcNow });
                _logger.LogInformation("Run {RunId} started: dataset {Dataset}, target {Target}", id, dataset.Name, evaluation.Target);

                var run = await evaluation.ExecuteAsync(dataset, baseline, cts.Token,
                    _ => Update(id, r => r with { Progress = r.Progress with { Completed = r.Progress.Completed + 1 } }));

                // The report carries the same id as the run, so GET /runs/{id}/report and the record agree.
                run = run with { Run = run.Run with { RunId = id } };
                await _store.SaveReportAsync(id, run);

                var exit = ExitCodes.For(run);
                await FinishAsync(id, run.Run.Cancelled ? RunStatus.Cancelled : RunStatus.Completed, exit,
                    error: run.Run.Cancelled && _stopping ? "Cancelled because the service was shutting down." : null);
                _logger.LogInformation("Run {RunId} finished: {Outcome}", id, OutcomeFor(exit));
            }
            catch (RagApiException ex)
            {
                // E.g. the RAG API never became ready: the run is inconclusive, not a quality failure.
                await FinishAsync(id, RunStatus.Failed, ExitCodes.Inconclusive, ex.Message);
            }
            catch (OperationCanceledException)
            {
                await FinishAsync(id, RunStatus.Cancelled, ExitCodes.Cancelled,
                    _stopping ? "Cancelled because the service was shutting down." : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run {RunId} failed unexpectedly", id);
                await FinishAsync(id, RunStatus.Failed, exitCode: null, error: $"Internal error: {ex.Message}");
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            await evaluation.DisposeAsync();
            _cancellations.TryRemove(id, out _);
            cts.Dispose();
            _tasks.TryRemove(id, out _);
        }
    }

    /// <summary>A run can finish, and dispose its token source, at the same moment someone cancels it.</summary>
    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    private async Task FinishAsync(string id, RunStatus status, int? exitCode, string? error)
    {
        var record = Update(id, r => r with
        {
            Status = status,
            CompletedAt = DateTimeOffset.UtcNow,
            ExitCode = exitCode,
            Passed = exitCode == ExitCodes.Success,
            Outcome = exitCode is { } code ? OutcomeFor(code) : "Failed",
            Error = error,
        });
        await _store.SaveRecordAsync(record);
    }

    private RunRecord Update(string id, Func<RunRecord, RunRecord> change) => _runs.AddOrUpdate(id, _ => throw new KeyNotFoundException(id), (_, r) => change(r));

    private static string OutcomeFor(int exitCode) => exitCode switch
    {
        ExitCodes.Success => "Passed",
        ExitCodes.QualityFailure => "QualityFailure",
        ExitCodes.Inconclusive => "Inconclusive",
        ExitCodes.Cancelled => "Cancelled",
        _ => "Failed",
    };

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>On shutdown, stop runs and let them write their partial reports before the process exits.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        foreach (var cts in _cancellations.Values) TryCancel(cts);
        var pending = Task.WhenAll(_tasks.Values);
        await Task.WhenAny(pending, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }
}
