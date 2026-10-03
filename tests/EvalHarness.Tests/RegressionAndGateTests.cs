using EvalHarness.Core;
using EvalHarness.Runner;

namespace EvalHarness.Tests;

internal static class Runs
{
    public static AggregateMetrics Metrics(
        double? passRate = 0.9, double? correctness = 0.9, double? faithfulness = 0.9, double? cosine = 0.9,
        double? r1 = 0.8, double? r3 = 0.9, double? r5 = 1.0, double? latency = 100,
        int total = 10, int apiErrors = 0, int evaluatorErrors = 0) =>
        new(total, 9, 1, apiErrors, evaluatorErrors, passRate, null, cosine, correctness, faithfulness, null, r1, r3, r5, 1.2, latency, latency, 1000);

    public static TestResult Test(string id, TestOutcome outcome) =>
        new(id, "c", "easy", "q", "a", outcome, "a", null, [], 10, 1, 1, [], null);

    public static EvaluationRun Run(AggregateMetrics? metrics = null, string runId = "run-00000000", string sha = "sha", params TestResult[] results) =>
        new(EvaluationRun.CurrentSchemaVersion,
            new RunInfo(runId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1000, false, "1.0.0", "http://rag", 1, ["e"]),
            new DatasetInfo("ds", "ds.json", sha, results.Length),
            metrics ?? Metrics(),
            results);
}

public class RegressionDetectorTests
{
    private static RegressionReport Compare(AggregateMetrics baseline, AggregateMetrics current, RegressionOptions? options = null) =>
        RegressionDetector.Compare(Runs.Run(baseline), Runs.Run(current, "run-11111111"), options ?? new RegressionOptions());

    private static MetricComparison Metric(RegressionReport r, string name) => r.Metrics.Single(m => m.Metric == name);

    [Fact]
    public void Identical_runs_have_no_regression()
    {
        var report = Compare(Runs.Metrics(), Runs.Metrics());

        Assert.False(report.HasRegression);
        Assert.All(report.Metrics, m => Assert.False(m.Regressed));
    }

    [Fact]
    public void Drop_within_the_allowance_is_not_a_regression()
    {
        var report = Compare(Runs.Metrics(correctness: 0.90), Runs.Metrics(correctness: 0.86), new RegressionOptions { MaxAnswerCorrectnessDrop = 0.05 });

        Assert.False(report.HasRegression);
        Assert.Equal(-0.04, Metric(report, "answerCorrectness").Delta!.Value, 6);
    }

    [Fact]
    public void Drop_beyond_the_allowance_is_a_regression()
    {
        var report = Compare(Runs.Metrics(correctness: 0.90), Runs.Metrics(correctness: 0.80));

        Assert.True(report.HasRegression);
        Assert.True(Metric(report, "answerCorrectness").Regressed);
        Assert.False(Metric(report, "faithfulness").Regressed);
    }

    [Fact]
    public void Drop_exactly_at_the_allowance_is_tolerated()
    {
        var report = Compare(Runs.Metrics(faithfulness: 0.90), Runs.Metrics(faithfulness: 0.85), new RegressionOptions { MaxFaithfulnessDrop = 0.05 });
        Assert.False(report.HasRegression);
    }

    [Theory]
    [InlineData("faithfulness")]
    [InlineData("recallAt1")]
    [InlineData("recallAt3")]
    [InlineData("recallAt5")]
    [InlineData("averageCosineSimilarity")]
    [InlineData("passRate")]
    public void Each_quality_metric_is_tracked(string metric)
    {
        var baseline = Runs.Metrics();
        var worse = metric switch
        {
            "faithfulness" => Runs.Metrics(faithfulness: 0.5),
            "recallAt1" => Runs.Metrics(r1: 0.1),
            "recallAt3" => Runs.Metrics(r3: 0.1),
            "recallAt5" => Runs.Metrics(r5: 0.1),
            "averageCosineSimilarity" => Runs.Metrics(cosine: 0.1),
            _ => Runs.Metrics(passRate: 0.1),
        };

        var report = Compare(baseline, worse);

        Assert.True(Metric(report, metric).Regressed);
        Assert.True(report.HasRegression);
    }

    [Fact]
    public void Improvement_is_never_a_regression()
    {
        var report = Compare(Runs.Metrics(correctness: 0.5, latency: 500), Runs.Metrics(correctness: 0.95, latency: 50));
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void Latency_regresses_by_relative_increase()
    {
        var options = new RegressionOptions { MaxLatencyIncreasePercent = 25 };

        Assert.False(Compare(Runs.Metrics(latency: 100), Runs.Metrics(latency: 120), options).HasRegression);
        Assert.True(Compare(Runs.Metrics(latency: 100), Runs.Metrics(latency: 130), options).HasRegression);
        Assert.False(Compare(Runs.Metrics(latency: 100), Runs.Metrics(latency: 40), options).HasRegression);
    }

    [Fact]
    public void Metric_missing_in_either_run_is_not_compared_and_warns_when_lost()
    {
        var report = Compare(Runs.Metrics(faithfulness: 0.9), Runs.Metrics(faithfulness: null));

        Assert.False(report.HasRegression);
        Assert.Contains(report.Warnings, w => w.Contains("faithfulness"));
    }

    [Fact]
    public void Test_level_changes_are_classified()
    {
        var baseline = Runs.Run(Runs.Metrics(), "run-00000000", "sha",
            Runs.Test("stays", TestOutcome.Passed), Runs.Test("breaks", TestOutcome.Passed), Runs.Test("fixed", TestOutcome.Failed),
            Runs.Test("outage", TestOutcome.Passed), Runs.Test("gone", TestOutcome.Passed));
        var current = Runs.Run(Runs.Metrics(), "run-11111111", "sha",
            Runs.Test("stays", TestOutcome.Passed), Runs.Test("breaks", TestOutcome.Failed), Runs.Test("fixed", TestOutcome.Passed),
            Runs.Test("outage", TestOutcome.ApiError), Runs.Test("fresh", TestOutcome.Passed));

        var report = RegressionDetector.Compare(baseline, current, new RegressionOptions());

        Assert.Equal(["breaks"], report.RegressedTests);
        Assert.Equal(["fixed"], report.ImprovedTests);
        Assert.Equal(["outage"], report.InconclusiveTests);
        Assert.Equal(["fresh"], report.NewTests);
        Assert.Equal(["gone"], report.RemovedTests);
        Assert.False(report.HasRegression); // newly failing tests are reported but not gated by default
    }

    [Fact]
    public void Newly_failing_tests_can_be_gated()
    {
        var baseline = Runs.Run(Runs.Metrics(), "run-00000000", "sha", Runs.Test("a", TestOutcome.Passed), Runs.Test("b", TestOutcome.Passed));
        var current = Runs.Run(Runs.Metrics(), "run-11111111", "sha", Runs.Test("a", TestOutcome.Failed), Runs.Test("b", TestOutcome.Failed));

        Assert.True(RegressionDetector.Compare(baseline, current, new RegressionOptions { MaxNewFailingTests = 1 }).HasRegression);
        Assert.False(RegressionDetector.Compare(baseline, current, new RegressionOptions { MaxNewFailingTests = 2 }).HasRegression);
        Assert.True(RegressionDetector.Compare(baseline, current, new RegressionOptions { MaxNewFailingTests = 0 }).HasRegression);
    }

    [Fact]
    public void Different_datasets_produce_a_warning()
    {
        var report = RegressionDetector.Compare(Runs.Run(sha: "one"), Runs.Run(sha: "two", runId: "run-11111111"), new RegressionOptions());
        Assert.Contains(report.Warnings, w => w.Contains("datasets differ"));
    }
}

public class QualityGateTests
{
    private static GateReport Evaluate(AggregateMetrics metrics, QualityGateOptions options, IReadOnlyCollection<string>? evaluators = null) =>
        QualityGateEvaluator.Evaluate(metrics, options, evaluators);

    private static QualityGateOptions Only(Action<QualityGateOptions> configure)
    {
        var options = new QualityGateOptions
        {
            MinPassRate = null, MinAnswerCorrectness = null, MinFaithfulness = null, MinRecallAt5 = null,
            MaxApiErrorRate = null, MaxEvaluatorErrorRate = null,
        };
        configure(options);
        return options;
    }

    [Fact]
    public void Run_meeting_every_gate_passes()
    {
        var report = Evaluate(Runs.Metrics(), new QualityGateOptions());

        Assert.True(report.Passed);
        Assert.All(report.Checks, c => Assert.True(c.Passed));
    }

    [Theory]
    [InlineData(0.85, true)]
    [InlineData(0.80, true)]
    [InlineData(0.79, false)]
    public void Minimum_gate_boundaries(double passRate, bool passes) =>
        Assert.Equal(passes, Evaluate(Runs.Metrics(passRate: passRate), Only(o => o.MinPassRate = 0.80)).Passed);

    [Fact]
    public void Below_threshold_is_a_quality_failure()
    {
        var report = Evaluate(Runs.Metrics(faithfulness: 0.5), Only(o => o.MinFaithfulness = 0.8));

        var check = Assert.Single(report.Checks);
        Assert.False(check.Passed);
        Assert.Equal(GateKind.Quality, check.Kind);
        Assert.Equal(0.5, check.Actual);
    }

    [Fact]
    public void Latency_gate_is_a_maximum()
    {
        var options = Only(o => o.MaxAverageLatencyMs = 200);

        Assert.True(Evaluate(Runs.Metrics(latency: 150), options).Passed);
        Assert.False(Evaluate(Runs.Metrics(latency: 250), options).Passed);
    }

    [Fact]
    public void Gate_on_a_metric_whose_evaluator_did_not_run_is_skipped_not_failed()
    {
        var metrics = Runs.Metrics(faithfulness: null, correctness: null);
        var options = Only(o => { o.MinFaithfulness = 0.8; o.MinAnswerCorrectness = 0.7; o.MinPassRate = 0.5; });

        var report = Evaluate(metrics, options, [EvaluatorNames.RetrievalQuality]);

        Assert.True(report.Passed);
        Assert.Equal(["passRate"], report.Checks.Select(c => c.Name));
    }

    [Fact]
    public void Gate_on_a_metric_whose_evaluator_ran_but_produced_nothing_is_inconclusive()
    {
        var report = Evaluate(Runs.Metrics(faithfulness: null), Only(o => o.MinFaithfulness = 0.8), [EvaluatorNames.Faithfulness]);

        Assert.Equal(GateKind.Reliability, Assert.Single(report.Checks).Kind);
        Assert.False(report.Passed);
    }

    [Fact]
    public void Disabled_gates_are_not_checked() =>
        Assert.Empty(Evaluate(Runs.Metrics(passRate: 0), Only(_ => { })).Checks);

    [Fact]
    public void Metric_that_could_not_be_computed_is_a_reliability_failure_not_a_quality_failure()
    {
        var report = Evaluate(Runs.Metrics(passRate: null), Only(o => o.MinPassRate = 0.8));

        var check = Assert.Single(report.Checks);
        Assert.False(check.Passed);
        Assert.Equal(GateKind.Reliability, check.Kind);
    }

    [Fact]
    public void Api_and_evaluator_error_rates_are_reliability_gates()
    {
        var report = Evaluate(Runs.Metrics(total: 10, apiErrors: 2, evaluatorErrors: 1),
            Only(o => { o.MaxApiErrorRate = 0.1; o.MaxEvaluatorErrorRate = 0.1; }));

        Assert.All(report.Checks, c => Assert.Equal(GateKind.Reliability, c.Kind));
        Assert.False(report.Checks.Single(c => c.Name == "apiErrorRate").Passed);   // 20% > 10%
        Assert.True(report.Checks.Single(c => c.Name == "evaluatorErrorRate").Passed); // 10% <= 10%
    }
}

public class ExitCodeTests
{
    private static EvaluationRun With(GateReport? gates = null, RegressionReport? regression = null, bool cancelled = false)
    {
        var run = Runs.Run();
        return run with { Run = run.Run with { Cancelled = cancelled }, QualityGates = gates, Regression = regression };
    }

    private static GateReport Gate(GateKind kind, bool passed) =>
        new(passed, [new GateCheck("g", kind, ">=", 0.5, 0.8, passed)]);

    private static RegressionReport Regression(bool regressed) =>
        new(regressed, "a", "b", [], [], [], [], [], [], []);

    [Fact]
    public void All_good_exits_zero() =>
        Assert.Equal(0, ExitCodes.For(With(Gate(GateKind.Quality, true), Regression(false))));

    [Fact]
    public void Failed_quality_gate_exits_one() =>
        Assert.Equal(1, ExitCodes.For(With(Gate(GateKind.Quality, false))));

    [Fact]
    public void Regression_exits_one() =>
        Assert.Equal(1, ExitCodes.For(With(Gate(GateKind.Quality, true), Regression(true))));

    [Fact]
    public void Reliability_failure_alone_exits_three_so_an_outage_is_not_read_as_bad_quality() =>
        Assert.Equal(3, ExitCodes.For(With(Gate(GateKind.Reliability, false))));

    [Fact]
    public void Quality_failure_takes_precedence_over_reliability_failure()
    {
        var gates = new GateReport(false, [new GateCheck("a", GateKind.Reliability, "<=", 1, 0, false), new GateCheck("b", GateKind.Quality, ">=", 0, 1, false)]);
        Assert.Equal(1, ExitCodes.For(With(gates)));
    }

    [Fact]
    public void Cancelled_run_exits_130() =>
        Assert.Equal(130, ExitCodes.For(With(Gate(GateKind.Quality, true), cancelled: true)));
}
