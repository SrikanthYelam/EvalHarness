using EvalHarness.Core;
using EvalHarness.Runner;

namespace EvalHarness.Tests;

public class RunnerTests
{
    private static FakeRag Healthy() => new((q, _) => Task.FromResult(Build.Response("A")));

    private static Task<EvaluationRun> Run(IRagClient rag, IReadOnlyList<ConfiguredEvaluator> evaluators, Dataset dataset,
        int parallelism = 1, CancellationToken ct = default) =>
        new EvaluationRunner(rag, evaluators, parallelism).RunAsync(dataset, "http://rag.test", ct);

    [Fact]
    public async Task Passing_test_records_answer_chunks_and_evaluator_results()
    {
        var run = await Run(Healthy(), [Build.Passing("ok")], Build.Dataset(Build.Case("t1")));

        var result = Assert.Single(run.Results);
        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal("A", result.Answer);
        Assert.Single(result.RetrievedChunks);
        Assert.Equal("ok", Assert.Single(result.EvaluatorResults).Evaluator);
        Assert.Equal(1, run.Aggregate.Passed);
        Assert.False(run.Run.Cancelled);
        Assert.Equal("ds", run.Dataset.Name);
    }

    [Fact]
    public async Task Failing_gating_evaluator_fails_the_test_with_a_reason()
    {
        var run = await Run(Healthy(), [Build.Passing("ok"), Build.Failing("bad")], Build.Dataset(Build.Case()));

        var result = run.Results[0];
        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Contains("bad: wrong", result.FailureReason);
        Assert.Equal(1, run.Aggregate.Failed);
    }

    [Fact]
    public async Task Failing_non_gating_evaluator_is_informational_only()
    {
        var run = await Run(Healthy(), [Build.Passing("ok"), Build.Failing("bad", gating: false)], Build.Dataset(Build.Case()));

        Assert.Equal(TestOutcome.Passed, run.Results[0].Outcome);
        Assert.Equal(EvaluatorStatus.Failed, run.Results[0].EvaluatorResults[1].Status);
        Assert.False(run.Results[0].EvaluatorResults[1].Gating);
    }

    [Fact]
    public async Task Rag_api_failure_is_an_api_error_not_a_wrong_answer_and_does_not_stop_the_run()
    {
        var rag = new FakeRag((q, _) => q == "boom"
            ? throw new RagApiException("HTTP 503", 503)
            : Task.FromResult(Build.Response("A")));
        var dataset = Build.Dataset(Build.Case("a", "ok?"), Build.Case("b", "boom"), Build.Case("c", "ok too?"));
        var ran = 0;
        var evaluator = new ConfiguredEvaluator(new DelegateEvaluator("e", c => { Interlocked.Increment(ref ran); return EvaluatorResult.Of("e", true, 1, "fine"); }), true);

        var run = await Run(rag, [evaluator], dataset);

        Assert.Equal([TestOutcome.Passed, TestOutcome.ApiError, TestOutcome.Passed], run.Results.Select(r => r.Outcome));
        Assert.Contains("RAG API failure: HTTP 503", run.Results[1].FailureReason);
        Assert.Empty(run.Results[1].EvaluatorResults);
        Assert.Equal(2, ran); // evaluators are not run against a missing answer
        Assert.Equal((2, 0, 1), (run.Aggregate.Passed, run.Aggregate.Failed, run.Aggregate.ApiErrors));
        Assert.Equal(1.0, run.Aggregate.PassRate); // outage is excluded from the pass rate
    }

    [Fact]
    public async Task Unexpected_client_exception_is_also_contained_as_an_api_error()
    {
        var rag = new FakeRag((_, _) => throw new InvalidOperationException("bug in client"));
        var run = await Run(rag, [Build.Passing()], Build.Dataset(Build.Case()));
        Assert.Equal(TestOutcome.ApiError, run.Results[0].Outcome);
    }

    [Fact]
    public async Task Evaluator_exception_is_an_evaluator_error_not_a_wrong_answer()
    {
        var throwing = new ConfiguredEvaluator(new DelegateEvaluator("judge", _ => throw new InvalidOperationException("judge down")), true);

        var run = await Run(Healthy(), [Build.Passing("ok"), throwing], Build.Dataset(Build.Case()));

        var result = run.Results[0];
        Assert.Equal(TestOutcome.EvaluatorError, result.Outcome);
        var error = result.EvaluatorResults.Single(e => e.Evaluator == "judge");
        Assert.Equal(EvaluatorStatus.Error, error.Status);
        Assert.Contains("judge down", error.Error);
        Assert.Equal(EvaluatorStatus.Passed, result.EvaluatorResults.Single(e => e.Evaluator == "ok").Status);
        Assert.Equal(1, run.Aggregate.EvaluatorErrors);
        Assert.Null(run.Aggregate.PassRate);
    }

    [Fact]
    public async Task A_genuine_failure_wins_over_an_evaluator_error()
    {
        var throwing = new ConfiguredEvaluator(new DelegateEvaluator("judge", _ => throw new InvalidOperationException("x")), true);
        var run = await Run(Healthy(), [Build.Failing("bad"), throwing], Build.Dataset(Build.Case()));
        Assert.Equal(TestOutcome.Failed, run.Results[0].Outcome);
    }

    [Fact]
    public async Task Per_test_evaluator_list_restricts_which_evaluators_run()
    {
        var test = Build.Case() with { Evaluators = ["ok"] };

        var run = await Run(Healthy(), [Build.Passing("ok"), Build.Failing("bad")], Build.Dataset(test));

        Assert.Equal(TestOutcome.Passed, run.Results[0].Outcome);
        Assert.Equal(["ok"], run.Results[0].EvaluatorResults.Select(e => e.Evaluator));
    }

    [Fact]
    public async Task Results_keep_dataset_order_even_when_tests_finish_out_of_order()
    {
        var rag = new FakeRag(async (q, _) =>
        {
            await Task.Delay(q == "first" ? 80 : 1);
            return Build.Response("A");
        });
        var dataset = Build.Dataset(Build.Case("1", "first"), Build.Case("2", "second"), Build.Case("3", "third"));

        var run = await Run(rag, [Build.Passing()], dataset, parallelism: 3);

        Assert.Equal(["1", "2", "3"], run.Results.Select(r => r.Id));
    }

    [Fact]
    public async Task Parallelism_is_bounded_by_the_configured_limit()
    {
        var rag = new FakeRag(async (_, _) => { await Task.Delay(20); return Build.Response("A"); });
        var dataset = Build.Dataset(Enumerable.Range(0, 12).Select(i => Build.Case($"t{i}", $"q{i}")).ToArray());

        var run = await Run(rag, [Build.Passing()], dataset, parallelism: 3);

        Assert.Equal(12, run.Results.Count);
        Assert.InRange(rag.MaxConcurrency, 2, 3);
    }

    [Fact]
    public async Task Parallelism_of_one_is_sequential()
    {
        var rag = new FakeRag(async (_, _) => { await Task.Delay(5); return Build.Response("A"); });
        await Run(rag, [Build.Passing()], Build.Dataset(Enumerable.Range(0, 5).Select(i => Build.Case($"t{i}")).ToArray()), parallelism: 1);
        Assert.Equal(1, rag.MaxConcurrency);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_reports_completed_tests_only()
    {
        using var cts = new CancellationTokenSource();
        var rag = new FakeRag(async (q, ct) =>
        {
            if (q == "q2") { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return Build.Response("A");
        });
        var dataset = Build.Dataset(Enumerable.Range(1, 5).Select(i => Build.Case($"t{i}", $"q{i}")).ToArray());

        var run = await Run(rag, [Build.Passing()], dataset, parallelism: 1, ct: cts.Token);

        Assert.True(run.Run.Cancelled);
        Assert.Equal(["t1"], run.Results.Select(r => r.Id));
        Assert.Equal(2, rag.Calls); // q3..q5 never started
        Assert.Equal(ExitCodes.Cancelled, ExitCodes.For(run));
    }

    [Fact]
    public async Task Already_cancelled_token_runs_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var rag = Healthy();

        var run = await Run(rag, [Build.Passing()], Build.Dataset(Build.Case()), ct: cts.Token);

        Assert.True(run.Run.Cancelled);
        Assert.Empty(run.Results);
        Assert.Equal(0, rag.Calls);
    }
}

public class AggregationTests
{
    private static TestResult Result(string id, TestOutcome outcome, double? latency = 100, params EvaluatorResult[] evaluators) =>
        new(id, "c", "easy", "q", "a", outcome, "a", null, [], latency, 1, 5, evaluators, null);

    private static EvaluatorResult Score(string name, double score, EvaluatorStatus status = EvaluatorStatus.Passed, Dictionary<string, double>? metrics = null) =>
        new(name, status, score, null, metrics);

    [Fact]
    public void Counts_outcomes_and_pass_rate_over_evaluated_tests_only()
    {
        var agg = ResultAggregator.Aggregate(
        [
            Result("1", TestOutcome.Passed), Result("2", TestOutcome.Passed), Result("3", TestOutcome.Passed),
            Result("4", TestOutcome.Failed), Result("5", TestOutcome.ApiError, null), Result("6", TestOutcome.EvaluatorError),
        ], 1234);

        Assert.Equal(6, agg.TotalTests);
        Assert.Equal((3, 1, 1, 1), (agg.Passed, agg.Failed, agg.ApiErrors, agg.EvaluatorErrors));
        Assert.Equal(0.75, agg.PassRate);
        Assert.Equal(1234, agg.TotalExecutionMs);
    }

    [Fact]
    public void Averages_use_verdicts_only_and_ignore_skipped_and_errored_results()
    {
        var agg = ResultAggregator.Aggregate(
        [
            Result("1", TestOutcome.Passed, 100, Score("answer-correctness", 1.0), Score("faithfulness", 0.5), Score("cosine-similarity", 0.9), Score("exact-match", 1)),
            Result("2", TestOutcome.Failed, 100, Score("answer-correctness", 0.5, EvaluatorStatus.Failed), Score("faithfulness", 1.0), Score("cosine-similarity", 0.7), Score("exact-match", 0, EvaluatorStatus.Failed)),
            Result("3", TestOutcome.EvaluatorError, 100, EvaluatorResult.Errored("answer-correctness", "boom"), EvaluatorResult.Skipped("faithfulness", "n/a")),
        ], 0);

        Assert.Equal(0.75, agg.AnswerCorrectness);
        Assert.Equal(0.75, agg.Faithfulness);
        Assert.Equal(0.8, agg.AverageCosineSimilarity!.Value, 6);
        Assert.Equal(0.5, agg.ExactMatchRate);
        Assert.Null(agg.RetrievalRelevance); // never ran
    }

    [Fact]
    public void Recall_and_rank_come_from_retrieval_quality_metrics()
    {
        EvaluatorResult Retrieval(double r1, double r3, double r5, double? rank, EvaluatorStatus s = EvaluatorStatus.Passed)
        {
            var m = new Dictionary<string, double> { ["recall@1"] = r1, ["recall@3"] = r3, ["recall@5"] = r5 };
            if (rank is { } value) m["expectedSourceRank"] = value;
            return Score("retrieval-quality", r5, s, m);
        }

        var agg = ResultAggregator.Aggregate(
        [
            Result("1", TestOutcome.Passed, 100, Retrieval(1, 1, 1, 1)),
            Result("2", TestOutcome.Passed, 100, Retrieval(0, 1, 1, 3)),
            Result("3", TestOutcome.Failed, 100, Retrieval(0, 0, 0, null, EvaluatorStatus.Failed)),
            Result("4", TestOutcome.ApiError, null),
        ], 0);

        Assert.Equal(1 / 3.0, agg.RecallAt1!.Value, 6);
        Assert.Equal(2 / 3.0, agg.RecallAt3!.Value, 6);
        Assert.Equal(2 / 3.0, agg.RecallAt5!.Value, 6);
        Assert.Equal(2.0, agg.MeanExpectedSourceRank); // only tests where it was found
    }

    [Fact]
    public void Latency_average_and_p95_exclude_api_errors()
    {
        var results = Enumerable.Range(1, 20).Select(i => Result($"t{i}", TestOutcome.Passed, i * 10)).ToList();
        results.Add(Result("down", TestOutcome.ApiError, null));

        var agg = ResultAggregator.Aggregate(results, 0);

        Assert.Equal(105, agg.AverageLatencyMs);
        Assert.Equal(190, agg.P95LatencyMs); // nearest rank: 19th of 20
    }

    [Fact]
    public void Empty_run_has_null_metrics()
    {
        var agg = ResultAggregator.Aggregate([], 0);

        Assert.Equal(0, agg.TotalTests);
        Assert.Null(agg.PassRate);
        Assert.Null(agg.AverageLatencyMs);
        Assert.Null(agg.P95LatencyMs);
    }
}
