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

        var b = baseline.Aggregate;
        var c = current.Aggregate;
        var metrics = new List<MetricComparison>
        {
            Higher("passRate", b.PassRate, c.PassRate, options.MaxPassRateDrop),
            Higher("answerCorrectness", b.AnswerCorrectness, c.AnswerCorrectness, options.MaxAnswerCorrectnessDrop),
            Higher("faithfulness", b.Faithfulness, c.Faithfulness, options.MaxFaithfulnessDrop),
            Higher("averageCosineSimilarity", b.AverageCosineSimilarity, c.AverageCosineSimilarity, options.MaxCosineSimilarityDrop),
            Higher("recallAt1", b.RecallAt1, c.RecallAt1, options.MaxRecallAt1Drop),
            Higher("recallAt3", b.RecallAt3, c.RecallAt3, options.MaxRecallAt3Drop),
            Higher("recallAt5", b.RecallAt5, c.RecallAt5, options.MaxRecallAt5Drop),
            Latency(b.AverageLatencyMs, c.AverageLatencyMs, options.MaxLatencyIncreasePercent),
        };
        warnings.AddRange(metrics.Where(m => m.Baseline is not null && m.Current is null)
            .Select(m => $"{m.Metric} was measured in the baseline but not in the current run, so it was not compared."));

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

    private static MetricComparison Higher(string name, double? baseline, double? current, double maxDrop)
    {
        double? delta = baseline is null || current is null ? null : current - baseline;
        var regressed = delta is not null && -delta.Value > maxDrop + Epsilon;
        return new MetricComparison(name, true, baseline, current, delta, $"drop <= {maxDrop.ToString("0.###", CultureInfo.InvariantCulture)}", regressed);
    }

    private static MetricComparison Latency(double? baseline, double? current, double maxIncreasePercent)
    {
        double? delta = baseline is null || current is null ? null : current - baseline;
        var regressed = delta is not null && baseline > 0 && delta.Value / baseline.Value * 100 > maxIncreasePercent + Epsilon;
        return new MetricComparison("averageLatencyMs", false, baseline, current, delta,
            $"increase <= {maxIncreasePercent.ToString("0.###", CultureInfo.InvariantCulture)}%", regressed);
    }
}
