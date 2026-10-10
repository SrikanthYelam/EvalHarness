using System.Diagnostics;
using System.Reflection;
using EvalHarness.Core;
using Microsoft.Extensions.Logging;

namespace EvalHarness.Runner;

/// <summary>
/// Dataset -> RAG API -> evaluators -> aggregate. One test failing (API outage, judge error) never aborts the run;
/// it is recorded with the right <see cref="TestOutcome"/> and the run continues. Cancellation stops the run and
/// returns the tests completed so far, flagged as cancelled.
/// </summary>
public sealed class EvaluationRunner(
    IRagClient rag,
    IReadOnlyList<ConfiguredEvaluator> evaluators,
    int maxParallelism,
    ILogger<EvaluationRunner>? logger = null,
    int repeats = 1)
{
    /// <param name="onTestCompleted">Called (possibly concurrently) as each test finishes; used for progress reporting.</param>
    public async Task<EvaluationRun> RunAsync(
        Dataset dataset, string target, CancellationToken cancellationToken, Action<TestResult>? onTestCompleted = null)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var slots = new TestResult?[dataset.TestCases.Count];
        var cancelled = false;

        logger?.LogInformation("Starting evaluation of {Dataset}: {Count} tests, parallelism {Parallelism}, evaluators [{Evaluators}]",
            dataset.Name, slots.Length, maxParallelism, string.Join(", ", evaluators.Select(e => e.Evaluator.Name)));

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, slots.Length),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallelism), CancellationToken = cancellationToken },
                async (index, ct) =>
                {
                    var result = await RunRepeatedAsync(dataset.TestCases[index], ct);
                    slots[index] = result;
                    onTestCompleted?.Invoke(result);
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
            logger?.LogWarning("Evaluation cancelled; reporting the tests completed so far");
        }

        var results = slots.Where(r => r is not null).Select(r => r!).ToList();
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;
        var run = new RunInfo(
            Guid.NewGuid().ToString("N"), started, DateTimeOffset.UtcNow, elapsed, cancelled, HarnessVersion, target,
            maxParallelism, evaluators.Select(e => e.Evaluator.Name).ToList());

        return new EvaluationRun(
            EvaluationRun.CurrentSchemaVersion,
            run,
            new DatasetInfo(dataset.Name, dataset.Path, dataset.Sha256, dataset.TestCases.Count),
            ResultAggregator.Aggregate(results, elapsed),
            results);
    }

    /// <summary>Runs one test <c>repeats</c> times, one after another, and merges the attempts into a single result.</summary>
    private async Task<TestResult> RunRepeatedAsync(TestCase test, CancellationToken ct)
    {
        var attempts = new List<TestResult>(Math.Max(1, repeats));
        for (var i = 0; i < Math.Max(1, repeats); i++)
            attempts.Add(await RunTestAsync(test, ct));
        return TestResultMerger.Merge(attempts);
    }

    private async Task<TestResult> RunTestAsync(TestCase test, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        RagResponse response;
        try
        {
            response = await rag.AskAsync(test.Question, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("{TestId}: RAG API failure: {Error}", test.Id, ex.Message);
            return Result(test, TestOutcome.ApiError, null, [], stopwatch, $"RAG API failure: {ex.Message}");
        }

        var context = new EvaluationContext(test, response);
        var selected = evaluators.Where(e => test.Evaluators is null ||
                                             test.Evaluators.Contains(e.Evaluator.Name, StringComparer.OrdinalIgnoreCase));
        var evaluatorResults = await Task.WhenAll(selected.Select(e => RunEvaluatorAsync(e, context, ct)));

        var (outcome, reason) = OutcomeClassifier.Classify(evaluatorResults);

        logger?.LogInformation("{TestId}: {Outcome} ({LatencyMs:0}ms)", test.Id, outcome, response.LatencyMs);
        return Result(test, outcome, response, evaluatorResults, stopwatch, reason);
    }

    private async Task<EvaluatorResult> RunEvaluatorAsync(ConfiguredEvaluator configured, EvaluationContext context, CancellationToken ct)
    {
        var name = configured.Evaluator.Name;
        try
        {
            var result = await configured.Evaluator.EvaluateAsync(context, ct);
            return result with { Gating = configured.Gating };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("{TestId}: evaluator {Evaluator} failed: {Error}", context.TestCase.Id, name, ex.Message);
            return EvaluatorResult.Errored(name, $"{ex.GetType().Name}: {ex.Message}") with { Gating = configured.Gating };
        }
    }

    private static TestResult Result(
        TestCase test, TestOutcome outcome, RagResponse? response, IReadOnlyList<EvaluatorResult> evaluatorResults,
        Stopwatch stopwatch, string? reason) =>
        new(test.Id, test.Category, test.Difficulty, test.Question, test.ExpectedAnswer, outcome,
            response?.Answer, response?.Status, response?.RetrievedChunks ?? [], response?.LatencyMs, response?.Attempts ?? 0,
            stopwatch.Elapsed.TotalMilliseconds, evaluatorResults, reason);

    private static string HarnessVersion =>
        typeof(EvaluationRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";
}
