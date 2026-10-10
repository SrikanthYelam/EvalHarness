using EvalHarness.Core;

namespace EvalHarness.Runner;

/// <summary>How evaluator results become a test outcome. Shared by a single attempt and by merged repeats.</summary>
public static class OutcomeClassifier
{
    /// <summary>
    /// A failed gating evaluator makes the test Failed (a wrong answer). Otherwise any evaluator error makes it an
    /// EvaluatorError (inconclusive). A genuine failure wins over an error.
    /// </summary>
    public static (TestOutcome Outcome, string? Reason) Classify(IReadOnlyList<EvaluatorResult> results)
    {
        var failed = results.Where(r => r is { Gating: true, Status: EvaluatorStatus.Failed }).ToList();
        if (failed.Count > 0)
            return (TestOutcome.Failed, string.Join("; ", failed.Select(r => $"{r.Evaluator}: {r.Explanation}")));

        var errored = results.Where(r => r.Status == EvaluatorStatus.Error).ToList();
        if (errored.Count > 0)
            return (TestOutcome.EvaluatorError, string.Join("; ", errored.Select(r => $"{r.Evaluator}: {r.Error}")));

        return (TestOutcome.Passed, null);
    }
}

/// <summary>
/// Combines several attempts at the same test (see <c>Evaluation:Repeats</c>) into one result, so a single noisy
/// judge verdict or a one-off bad generation does not decide the test.
/// </summary>
/// <remarks>
/// Per evaluator: the verdict is the majority of the attempts that produced one (a tie fails, the conservative
/// choice) and the score is their mean. Attempts that errored, or whose RAG call failed, do not vote; they are kept
/// visible in <see cref="TestResult.RepeatOutcomes"/>. The test outcome is then derived from the merged evaluator
/// results exactly as for a single attempt. A test is <see cref="TestResult.Flaky"/> when its attempts reached
/// different verdicts.
/// </remarks>
public static class TestResultMerger
{
    public static TestResult Merge(IReadOnlyList<TestResult> attempts)
    {
        if (attempts.Count == 0) throw new ArgumentException("At least one attempt is required.", nameof(attempts));
        if (attempts.Count == 1) return attempts[0];

        var outcomes = attempts.Select(a => a.Outcome).ToList();
        var withResponse = attempts.Where(a => a.Outcome != TestOutcome.ApiError).ToList();

        // Every attempt hit an API failure: nothing to evaluate. Report the first failure.
        if (withResponse.Count == 0)
            return attempts[0] with
            {
                DurationMs = attempts.Sum(a => a.DurationMs),
                FailureReason = $"{attempts[0].FailureReason} (all {attempts.Count} attempts failed)",
                RepeatOutcomes = outcomes,
            };

        var merged = withResponse.SelectMany(a => a.EvaluatorResults).GroupBy(r => r.Evaluator)
            .Select(g => MergeEvaluator(g.ToList(), withResponse.Count))
            .ToList();
        var (outcome, reason) = OutcomeClassifier.Classify(merged);

        // Show the answer and chunks from an attempt that ended the way the merged test did.
        var representative = withResponse.FirstOrDefault(a => a.Outcome == outcome) ?? withResponse[0];
        var latencies = withResponse.Where(a => a.LatencyMs is not null).Select(a => a.LatencyMs!.Value).ToList();
        var verdicts = outcomes.Where(o => o is TestOutcome.Passed or TestOutcome.Failed).Distinct().Count();

        return representative with
        {
            Outcome = outcome,
            EvaluatorResults = merged,
            FailureReason = reason,
            LatencyMs = latencies.Count == 0 ? null : latencies.Average(),
            DurationMs = attempts.Sum(a => a.DurationMs),
            Attempts = attempts.Sum(a => a.Attempts),
            RepeatOutcomes = outcomes,
            Flaky = verdicts > 1,
        };
    }

    private static EvaluatorResult MergeEvaluator(IReadOnlyList<EvaluatorResult> results, int attemptCount)
    {
        var verdicts = results.Where(r => (r.Status is EvaluatorStatus.Passed or EvaluatorStatus.Failed) && r.Score is not null).ToList();
        if (verdicts.Count == 0) return results[0]; // skipped or errored in every attempt

        var passed = verdicts.Count(r => r.Status == EvaluatorStatus.Passed);
        var status = passed > verdicts.Count - passed ? EvaluatorStatus.Passed : EvaluatorStatus.Failed;
        var shown = verdicts.First(r => r.Status == status);

        var metrics = verdicts.Where(r => r.Metrics is not null).SelectMany(r => r.Metrics!)
            .GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.Average(m => m.Value));

        var summary = $"[{passed} of {verdicts.Count} runs passed; scores " +
                      string.Join(", ", verdicts.Select(r => r.Score!.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))) + "] ";
        return shown with
        {
            Status = status,
            Score = verdicts.Average(r => r.Score!.Value),
            Explanation = summary + shown.Explanation,
            Metrics = metrics.Count == 0 ? null : metrics,
            Gating = results.Any(r => r.Gating),
        };
    }
}
