using EvalHarness.Core;

namespace EvalHarness.Runner;

public static class QualityGateEvaluator
{
    /// <summary>
    /// A gate whose metric could not be computed is a Reliability failure (the run is inconclusive), never a
    /// Quality failure: an API outage must not read as "the RAG system got worse". A gate on a metric whose
    /// evaluator was not enabled for the run (e.g. via --evaluators) is skipped rather than failed.
    /// </summary>
    /// <param name="enabledEvaluators">Evaluators that ran; null means all of them.</param>
    public static GateReport Evaluate(AggregateMetrics a, QualityGateOptions o, IReadOnlyCollection<string>? enabledEvaluators = null)
    {
        var checks = new List<GateCheck>();
        bool Ran(string evaluator) => evaluator.Length == 0 || enabledEvaluators is null || enabledEvaluators.Contains(evaluator);
        void AtLeast(string name, double? threshold, double? actual, string evaluator)
        {
            if (threshold is null || !Ran(evaluator)) return;
            checks.Add(new GateCheck(name, actual is null ? GateKind.Reliability : GateKind.Quality, ">=", actual, threshold.Value,
                actual is not null && actual.Value + 1e-9 >= threshold.Value));
        }
        void AtMost(string name, GateKind kind, double? threshold, double? actual)
        {
            if (threshold is null) return;
            checks.Add(new GateCheck(name, actual is null ? GateKind.Reliability : kind, "<=", actual, threshold.Value,
                actual is not null && actual.Value <= threshold.Value + 1e-9));
        }

        AtLeast("passRate", o.MinPassRate, a.PassRate, evaluator: "");
        AtLeast("answerCorrectness", o.MinAnswerCorrectness, a.AnswerCorrectness, EvaluatorNames.AnswerCorrectness);
        AtLeast("faithfulness", o.MinFaithfulness, a.Faithfulness, EvaluatorNames.Faithfulness);
        AtLeast("averageCosineSimilarity", o.MinCosineSimilarity, a.AverageCosineSimilarity, EvaluatorNames.CosineSimilarity);
        AtLeast("recallAt1", o.MinRecallAt1, a.RecallAt1, EvaluatorNames.RetrievalQuality);
        AtLeast("recallAt3", o.MinRecallAt3, a.RecallAt3, EvaluatorNames.RetrievalQuality);
        AtLeast("recallAt5", o.MinRecallAt5, a.RecallAt5, EvaluatorNames.RetrievalQuality);
        AtMost("averageLatencyMs", GateKind.Quality, o.MaxAverageLatencyMs, a.AverageLatencyMs);

        double Rate(int errors) => a.TotalTests == 0 ? 0 : errors / (double)a.TotalTests;
        AtMost("apiErrorRate", GateKind.Reliability, o.MaxApiErrorRate, Rate(a.ApiErrors));
        AtMost("evaluatorErrorRate", GateKind.Reliability, o.MaxEvaluatorErrorRate, Rate(a.EvaluatorErrors));

        return new GateReport(checks.All(c => c.Passed), checks);
    }
}

/// <summary>Process exit codes, so CI can tell "quality dropped" from "the run could not be trusted".</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>A quality gate failed or a regression against the baseline was detected.</summary>
    public const int QualityFailure = 1;

    /// <summary>Bad arguments, invalid configuration or dataset.</summary>
    public const int UsageError = 2;

    /// <summary>RAG API or evaluator errors left the run inconclusive (and no quality gate failed).</summary>
    public const int Inconclusive = 3;

    public const int Cancelled = 130;

    public static int For(EvaluationRun run)
    {
        if (run.Run.Cancelled) return Cancelled;

        var qualityFailed = (run.QualityGates?.Checks.Any(c => c is { Passed: false, Kind: GateKind.Quality }) ?? false)
                            || (run.Regression?.HasRegression ?? false);
        if (qualityFailed) return QualityFailure;

        var inconclusive = run.QualityGates?.Checks.Any(c => c is { Passed: false, Kind: GateKind.Reliability }) ?? false;
        return inconclusive ? Inconclusive : Success;
    }
}
