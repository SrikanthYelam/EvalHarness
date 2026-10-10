using System.Globalization;
using EvalHarness.Core;

namespace EvalHarness.Runner;

public static class RegressionDetector
{
    private const double Epsilon = 1e-9;

    public static RegressionReport Compare(EvaluationRun baseline, EvaluationRun current, RegressionOptions options)
    {
        var warnings = new List<string>();
        if (baseline.Dataset.Sha256 != current.Dataset.Sha256)
            warnings.Add("The datasets differ (content hash mismatch); aggregate metrics may not be like-for-like.");
        if (baseline.Run.Cancelled) warnings.Add("The baseline run was cancelled and only covers part of the dataset.");
        if (current.Run.Cancelled) warnings.Add("The current run was cancelled and only covers part of the dataset.");
        warnings.AddRange(SettingsWarnings(baseline.Run.Settings, current.Run.Settings));

        var b = baseline.Aggregate;
        var c = current.Aggregate;

        // One test flipping moves any mean-over-tests metric by up to 1/n; smaller allowances only flag noise.
        var evaluated = c.Passed + c.Failed > 0 ? c.Passed + c.Failed : c.TotalTests;
        var oneTest = options.AllowSingleTestVariance && evaluated > 0 ? 1.0 / evaluated : 0;

        var metrics = new List<MetricComparison>
        {
            Higher("passRate", b.PassRate, c.PassRate, options.MaxPassRateDrop, oneTest),
            Higher("answerCorrectness", b.AnswerCorrectness, c.AnswerCorrectness, options.MaxAnswerCorrectnessDrop, oneTest),
            Higher("faithfulness", b.Faithfulness, c.Faithfulness, options.MaxFaithfulnessDrop, oneTest),
            Higher("averageCosineSimilarity", b.AverageCosineSimilarity, c.AverageCosineSimilarity, options.MaxCosineSimilarityDrop, oneTest),
            Higher("recallAt1", b.RecallAt1, c.RecallAt1, options.MaxRecallAt1Drop, oneTest),
            Higher("recallAt3", b.RecallAt3, c.RecallAt3, options.MaxRecallAt3Drop, oneTest),
            Higher("recallAt5", b.RecallAt5, c.RecallAt5, options.MaxRecallAt5Drop, oneTest),
            Latency(b.AverageLatencyMs, c.AverageLatencyMs, options.MaxLatencyIncreasePercent),
        };
        warnings.AddRange(metrics.Where(m => m.Baseline is not null && m.Current is null)
            .Select(m => $"{m.Metric} was measured in the baseline but not in the current run, so it was not compared."));
        if (c.FlakyTests > 0)
            warnings.Add($"{c.FlakyTests} test(s) in the current run were flaky (their repeats disagreed); their verdicts are majority votes.");

        var baselineById = baseline.Results.ToDictionary(r => r.Id);
        var currentById = current.Results.ToDictionary(r => r.Id);
        var regressed = new List<string>();
        var improved = new List<string>();
        var inconclusive = new List<string>();
        foreach (var result in current.Results)
        {
            if (!baselineById.TryGetValue(result.Id, out var before)) continue;
            if (before.Outcome == TestOutcome.Passed && result.Outcome == TestOutcome.Failed) regressed.Add(result.Id);
            else if (before.Outcome == TestOutcome.Failed && result.Outcome == TestOutcome.Passed) improved.Add(result.Id);
            else if (before.Outcome == TestOutcome.Passed && result.Outcome is TestOutcome.ApiError or TestOutcome.EvaluatorError)
                inconclusive.Add(result.Id);
        }

        var tooManyFailures = options.MaxNewFailingTests is { } max && regressed.Count > max;
        return new RegressionReport(
            metrics.Any(m => m.Regressed) || tooManyFailures,
            baseline.Run.RunId,
            current.Run.RunId,
            metrics,
            regressed,
            improved,
            inconclusive,
            current.Results.Where(r => !baselineById.ContainsKey(r.Id)).Select(r => r.Id).ToList(),
            baseline.Results.Where(r => !currentById.ContainsKey(r.Id)).Select(r => r.Id).ToList(),
            warnings);
    }

    /// <summary>
    /// Differences that make two reports not like-for-like. These are warnings, not failures: a deliberate judge
    /// upgrade is legitimate, but it must never go unnoticed.
    /// </summary>
    public static IReadOnlyList<string> SettingsWarnings(RunSettings? baseline, RunSettings? current)
    {
        if (baseline is null || current is null)
            return [baseline is null
                ? "The baseline has no settings snapshot (it was written by an older version), so changes to the judge, thresholds or request fields cannot be detected."
                : "The current run has no settings snapshot."];

        var warnings = new List<string>();
        void Differs(string what, object? was, object? now)
        {
            if (!Equals(was, now))
                warnings.Add($"{what} differs: baseline '{was ?? "none"}', current '{now ?? "none"}'. Scores may not be comparable.");
        }

        Differs("Judge model", baseline.JudgeModel, current.JudgeModel);
        Differs("Embedding model", baseline.EmbeddingModel, current.EmbeddingModel);
        Differs("Judge prompts", baseline.PromptsFingerprint, current.PromptsFingerprint);
        Differs("Retrieval K", baseline.RetrievalK, current.RetrievalK);
        Differs("Repeats", baseline.Repeats, current.Repeats);
        Differs("RAG request fields", Describe(baseline.RequestFields), Describe(current.RequestFields));

        foreach (var name in baseline.Evaluators.Keys.Union(current.Evaluators.Keys).Order())
        {
            var inBaseline = baseline.Evaluators.GetValueOrDefault(name);
            var inCurrent = current.Evaluators.GetValueOrDefault(name);
            if (inBaseline is null || inCurrent is null)
                warnings.Add($"Evaluator '{name}' ran only in the {(inBaseline is null ? "current run" : "baseline")}.");
            else if (inBaseline != inCurrent)
                warnings.Add($"Evaluator '{name}' settings differ: baseline {Describe(inBaseline)}, current {Describe(inCurrent)}.");
        }

        return warnings;
    }

    private static string Describe(IReadOnlyDictionary<string, string> fields) =>
        fields.Count == 0 ? "" : string.Join(", ", fields.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));

    private static string Describe(EvaluatorSetting s) =>
        $"{(s.Gating ? "gating" : "informational")}, threshold {s.Threshold.ToString("0.###", CultureInfo.InvariantCulture)}";

    private static MetricComparison Higher(string name, double? baseline, double? current, double maxDrop, double oneTest)
    {
        var allowed = Math.Max(maxDrop, oneTest);
        double? delta = baseline is null || current is null ? null : current - baseline;
        var regressed = delta is not null && -delta.Value > allowed + Epsilon;
        var text = $"drop <= {allowed.ToString("0.###", CultureInfo.InvariantCulture)}" + (oneTest > maxDrop + Epsilon ? " (one test)" : "");
        return new MetricComparison(name, true, baseline, current, delta, text, regressed);
    }

    private static MetricComparison Latency(double? baseline, double? current, double maxIncreasePercent)
    {
        double? delta = baseline is null || current is null ? null : current - baseline;
        var regressed = delta is not null && baseline > 0 && delta.Value / baseline.Value * 100 > maxIncreasePercent + Epsilon;
        return new MetricComparison("averageLatencyMs", false, baseline, current, delta,
            $"increase <= {maxIncreasePercent.ToString("0.###", CultureInfo.InvariantCulture)}%", regressed);
    }
}
