using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EvalHarness.Core;
using EvalHarness.Mcp;
using ModelContextProtocol;

namespace EvalHarness.Tests;

public class RunSummariesTests
{
    private const string Sentinel = "SENSITIVE-CHUNK-TEXT-DO-NOT-LEAK";

    private static EvaluatorResult Ev(string name, EvaluatorStatus status, double? score = null, bool gating = true) =>
        new(name, status, score, "explanation") { Gating = gating };

    private static TestResult Problem(TestOutcome outcome, string? answer, params EvaluatorResult[] evaluators) =>
        Attempts.Make(outcome, 100, evaluators) with
        {
            Id = "q1", Answer = answer, RetrievedChunks = [Build.Chunk(1, Sentinel, "handbook.md", "c1"), Build.Chunk(2, Sentinel, "other.pdf", "c2")],
            FailureReason = "because",
        };

    [Theory]
    [InlineData(TestOutcome.ApiError, "x", "rag-api-error")]
    [InlineData(TestOutcome.EvaluatorError, "x", "evaluator-error")]
    public void Outages_and_judge_errors_are_named_for_what_they_are(TestOutcome outcome, string answer, string expected) =>
        Assert.Equal(expected, RunSummaries.LikelyCause(Problem(outcome, answer)));

    [Fact]
    public void Retrieval_miss_is_blamed_first_even_when_the_answer_is_also_empty_or_wrong()
    {
        var missed = Ev("retrieval-quality", EvaluatorStatus.Failed, 0);
        Assert.Equal("retrieval-miss", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, null, missed)));
        Assert.Equal("retrieval-miss", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, "wrong", missed, Ev("answer-correctness", EvaluatorStatus.Failed, 0))));
    }

    [Fact]
    public void A_refusal_with_the_right_chunk_retrieved_is_the_generator_declining()
    {
        var ok = Ev("retrieval-quality", EvaluatorStatus.Passed, 1);
        Assert.Equal("refused-despite-retrieval", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, null, ok, Ev("answer-correctness", EvaluatorStatus.Failed, 0))));
        Assert.Equal("refused-despite-retrieval", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, "   ", ok)));
    }

    [Fact]
    public void Unsupported_claims_are_distinguished_from_a_merely_wrong_answer()
    {
        var ok = Ev("retrieval-quality", EvaluatorStatus.Passed, 1);
        var wrong = Ev("answer-correctness", EvaluatorStatus.Failed, 0);

        Assert.Equal("unsupported-claims", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, "a", ok, wrong, Ev("faithfulness", EvaluatorStatus.Failed, 0.5))));
        Assert.Equal("wrong-answer-despite-retrieval", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, "a", ok, wrong)));
        Assert.Equal("other", RunSummaries.LikelyCause(Problem(TestOutcome.Failed, "a", ok)));
    }

    private static EvaluationRun Report(params TestResult[] results) =>
        Runs.Run(ResultAggregator(results), "0123456789abcdef0123456789abcdef", "sha", results);

    private static AggregateMetrics ResultAggregator(TestResult[] results) => EvalHarness.Runner.ResultAggregator.Aggregate(results, 5000);

    [Fact]
    public void Summary_has_the_verdict_metrics_and_only_the_problem_tests_with_no_chunk_text()
    {
        var passing = Attempts.Make(TestOutcome.Passed) with { Id = "fine" };
        var failing = Problem(TestOutcome.Failed, "wrong", Ev("retrieval-quality", EvaluatorStatus.Passed, 1), Ev("answer-correctness", EvaluatorStatus.Failed, 0));
        var report = Report(passing, failing);
        report = report with { QualityGates = new GateReport(false, [new GateCheck("passRate", GateKind.Quality, ">=", 0.5, 0.8, false)]) };

        var summary = RunSummaries.Summarize(report);
        var json = JsonSerializer.Serialize(summary);

        Assert.False(summary.Verdict.Passed);
        Assert.Equal(("QualityFailure", 1), (summary.Verdict.Outcome, summary.Verdict.ExitCode));
        Assert.Equal(new TestCounts(2, 1, 1, 0, 0, 0), summary.Tests);
        Assert.Equal(0.5, summary.Metrics.PassRate);
        Assert.Equal("passRate", Assert.Single(summary.Gates).Name);
        var problem = Assert.Single(summary.Problems);
        Assert.Equal(("q1", "wrong-answer-despite-retrieval"), (problem.Id, problem.LikelyCause));
        Assert.Equal(["handbook.md", "other.pdf"], problem.TopSources.Select(s => s.Document));
        Assert.DoesNotContain(Sentinel, json); // retrieved chunk text stays out of agent context
    }

    [Fact]
    public void Flaky_tests_that_passed_are_surfaced_but_stable_passes_are_not()
    {
        var flaky = Attempts.Make(TestOutcome.Passed) with { Id = "wobbly", Flaky = true, RepeatOutcomes = [TestOutcome.Passed, TestOutcome.Failed, TestOutcome.Passed] };
        var stable = Attempts.Make(TestOutcome.Passed) with { Id = "solid" };

        var summary = RunSummaries.Summarize(Report(flaky, stable));

        Assert.Equal(["wobbly"], summary.Problems.Select(p => p.Id));
        Assert.True(summary.Problems[0].Flaky);
        Assert.Equal(1, summary.Tests.Flaky);
        Assert.True(summary.Verdict.Passed);
    }

    [Fact]
    public void Problems_are_capped_and_the_overflow_is_counted()
    {
        var many = Enumerable.Range(0, 27).Select(i => Problem(TestOutcome.Failed, "a") with { Id = $"t{i}" }).ToArray();

        var summary = RunSummaries.Summarize(Report(many));

        Assert.Equal(RunSummaries.MaxProblems, summary.Problems.Count);
        Assert.Equal(7, summary.MoreProblems);
    }

    [Fact]
    public void Long_text_is_truncated()
    {
        var long300 = new string('x', 1000);
        var problem = RunSummaries.Summarize(Problem(TestOutcome.Failed, long300) with { Question = long300, ExpectedAnswer = long300, FailureReason = long300 });

        Assert.Equal(301, problem.Answer!.Length);
        Assert.Equal(201, problem.Question.Length);
        Assert.Equal(301, problem.Expected.Length);
        Assert.Equal(401, problem.Reason!.Length);
        Assert.EndsWith("…", problem.Answer);
    }

    [Fact]
    public void Settings_and_regression_are_carried_through()
    {
        var settings = new RunSettings("OpenAI/j", null, "abc123abc123", 5, 3, new Dictionary<string, string> { ["mode"] = "Vector" }, new Dictionary<string, EvaluatorSetting>());
        var baseline = Report(Attempts.Make(TestOutcome.Passed));
        var current = Report(Attempts.Make(TestOutcome.Passed));
        current = current with { Run = current.Run with { Settings = settings } };
        current = current with { Regression = EvalHarness.Runner.RegressionDetector.Compare(baseline, current, new RegressionOptions()) };

        var summary = RunSummaries.Summarize(current);

        Assert.Equal("Vector", summary.Settings!.RequestFields["mode"]);
        Assert.NotNull(summary.Regression);
        Assert.Contains(summary.Regression!.Warnings, w => w.Contains("no settings snapshot"));
    }
}

public class McpToolsTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Id2 = "fedcba9876543210fedcba9876543210";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string RunJson(string status = "Completed", bool? passed = true, int? exit = 0, string? outcome = "Passed", string id = Id, int completed = 2) =>
        JsonSerializer.Serialize(new
        {
            id, status, createdAt = DateTimeOffset.UtcNow, startedAt = DateTimeOffset.UtcNow, completedAt = (DateTimeOffset?)null,
            dataset = "sample", target = "http://rag.test/", progress = new { completed, total = 2 }, passed, exitCode = exit, outcome,
            error = (string?)null, isFinished = status is "Completed" or "Failed" or "Cancelled", request = new { dataset = "sample" },
        }, Web);

    private static EvalHarnessClient Client(StubHandler handler, string? apiKey = "secret-key", int pollMs = 5) =>
        new(new HttpClient(handler), new EvalHarnessOptions { BaseUrl = "http://eval.test:8090", ApiKey = apiKey })
        {
            PollInterval = TimeSpan.FromMilliseconds(pollMs),
        };

    private static StubHandler Json(HttpStatusCode status, string body) => StubHandler.Json(status, body);

    // ---- start_run ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_run_sends_the_api_key_and_only_the_fields_that_were_given()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) => { seen = req; return Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.Accepted, RunJson("Queued", null, null, null, completed: 0))); });

        var result = await EvalHarnessTools.StartRun(Client(handler), "sample", ragUrl: "  ", evaluators: [], repeats: 3,
            requestFields: new Dictionary<string, string> { ["mode"] = "Vector" }, baselineRunId: "");

        Assert.Equal("http://eval.test:8090/runs", seen!.RequestUri!.ToString());
        Assert.Equal("secret-key", seen.Headers.GetValues("X-Api-Key").Single());
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(["dataset", "repeats", "requestFields"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, body.RootElement.GetProperty("repeats").GetInt32());
        Assert.Equal("Vector", body.RootElement.GetProperty("requestFields").GetProperty("mode").GetString());

        using var output = JsonDocument.Parse(result);
        Assert.Equal(Id, output.RootElement.GetProperty("id").GetString());
        Assert.Equal("Queued", output.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("secret-key", result);
    }

    [Fact]
    public async Task Start_run_passes_every_option_through_when_given()
    {
        var handler = Json(HttpStatusCode.Accepted, RunJson("Queued", null, null, null));

        await EvalHarnessTools.StartRun(Client(handler), "sample", "http://rag.test:9000", ["exact-match"], 4, 2,
            new Dictionary<string, string> { ["topK"] = "7" }, Id2);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("http://rag.test:9000", body.RootElement.GetProperty("ragUrl").GetString());
        Assert.Equal("exact-match", body.RootElement.GetProperty("evaluators")[0].GetString());
        Assert.Equal(4, body.RootElement.GetProperty("parallelism").GetInt32());
        Assert.Equal(Id2, body.RootElement.GetProperty("baselineRunId").GetString());
    }

    // ---- errors the model will read ----------------------------------------------------------------------------

    [Fact]
    public async Task Without_an_api_key_no_request_is_sent_and_the_error_says_how_to_fix_it()
    {
        var handler = Json(HttpStatusCode.OK, "[]");

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.ListDatasets(Client(handler, apiKey: null), default));

        Assert.Contains("EVALHARNESS_API_KEY", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_without_echoing_it()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.ListDatasets(Client(Json(HttpStatusCode.Unauthorized, "{}")), default));

        Assert.Contains("401", ex.Message);
        Assert.Contains("EVALHARNESS_API_KEY", ex.Message);
        Assert.DoesNotContain("secret-key", ex.Message);
    }

    [Fact]
    public async Task A_validation_problem_passes_the_apis_title_and_details_to_the_model_so_it_can_correct_itself()
    {
        var problem = """{"title":"Invalid 'requestFields'.","status":400,"errors":["'rerank' is not an allowed request field.","Allowed: topK, mode."]}""";

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.StartRun(Client(Json(HttpStatusCode.BadRequest, problem)), "sample"));

        Assert.Contains("Invalid 'requestFields'", ex.Message);
        Assert.Contains("'rerank' is not an allowed request field", ex.Message);
        Assert.Contains("Allowed: topK, mode", ex.Message);
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task A_non_json_error_still_gives_a_status_and_path()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.ListDatasets(Client(Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>")), default));

        Assert.Contains("502", ex.Message);
        Assert.Contains("/datasets", ex.Message);
    }

    [Fact]
    public async Task A_response_that_is_not_an_evalharness_api_is_reported_clearly_not_as_a_crash()
    {
        var handler = Json(HttpStatusCode.OK, "<html>some other service</html>");

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.ListDatasets(Client(handler), default));

        Assert.Contains("could not be read", ex.Message);
        Assert.Contains("http://eval.test:8090", ex.Message);
    }

    [Fact]
    public async Task Both_compared_run_ids_are_checked_before_any_request_is_made()
    {
        var handler = Json(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.CompareRuns(Client(handler), Id, "nope"));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_unreachable_server_is_explained_with_its_address()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.ListDatasets(Client(handler), default));

        Assert.Contains("http://eval.test:8090", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef/cancel")]
    public async Task Run_ids_are_checked_before_they_become_part_of_a_url(string runId)
    {
        var handler = Json(HttpStatusCode.OK, RunJson());

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.GetRun(Client(handler), runId));

        Assert.Contains("32", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Run_ids_in_other_tools_are_checked_too()
    {
        var handler = Json(HttpStatusCode.OK, RunJson());

        await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.CancelRun(Client(handler), "x"));
        await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.StartRun(Client(handler), "sample", baselineRunId: "nope"));
        await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.CompareRuns(Client(handler), Id, "nope"));
        Assert.Equal(0, handler.Calls);
    }

    // ---- get_run -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_run_returns_immediately_by_default()
    {
        var handler = Json(HttpStatusCode.OK, RunJson("Running", null, null, null, completed: 1));

        var result = await EvalHarnessTools.GetRun(Client(handler), Id);

        Assert.Equal(1, handler.Calls);
        using var json = JsonDocument.Parse(result);
        Assert.Equal("Running", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("progress").GetProperty("completed").GetInt32());
    }

    [Fact]
    public async Task Get_run_can_wait_for_the_run_to_finish()
    {
        var handler = new StubHandler((_, call) => Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.OK,
            call < 3 ? RunJson(call == 1 ? "Queued" : "Running", null, null, null) : RunJson())));

        var result = await EvalHarnessTools.GetRun(Client(handler), Id, waitSeconds: 30);

        Assert.Equal(3, handler.Calls);
        using var json = JsonDocument.Parse(result);
        Assert.Equal("Completed", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("Passed", json.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Get_run_gives_back_the_current_status_when_the_wait_runs_out()
    {
        var handler = Json(HttpStatusCode.OK, RunJson("Running", null, null, null));

        var started = Stopwatch.StartNew();
        var result = await EvalHarnessTools.GetRun(Client(handler, pollMs: 200), Id, waitSeconds: 1);

        Assert.InRange(started.Elapsed.TotalSeconds, 0.5, 5);
        Assert.InRange(handler.Calls, 2, 6);
        Assert.Contains("Running", result);
    }

    [Fact]
    public async Task Get_run_stops_waiting_when_cancelled()
    {
        using var cts = new CancellationTokenSource(100);
        var handler = Json(HttpStatusCode.OK, RunJson("Running", null, null, null));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalHarnessTools.GetRun(Client(handler, pollMs: 50), Id, 60, cts.Token));
    }

    // ---- get_summary / compare_runs ----------------------------------------------------------------------------

    private static string ReportJson(string runId, double correctness = 1.0, bool failing = false)
    {
        // Ten tests, so the regression tolerance floor (one test's worth) is 0.1 and a big drop is a real regression.
        var results = Enumerable.Range(0, 10)
            .Select(i => Attempts.Make(TestOutcome.Passed, 100, Attempts.Judge(true, correctness)) with { Id = $"ok{i}" }).ToList();
        if (failing)
            results.Add(Attempts.Make(TestOutcome.Failed, 100,
                new EvaluatorResult("retrieval-quality", EvaluatorStatus.Failed, 0) { Gating = true }) with { Id = "bad", FailureReason = "missed" });
        var report = Runs.Run(EvalHarness.Runner.ResultAggregator.Aggregate(results, 1000), runId, "sha", [.. results]);
        return JsonSerializer.Serialize(report, EvalJson.Options);
    }

    private static StubHandler Router(Dictionary<string, (HttpStatusCode Status, string Body)> routes) =>
        new((req, _) =>
        {
            var key = $"{req.Method} {req.RequestUri!.AbsolutePath}";
            return Task.FromResult(routes.TryGetValue(key, out var r)
                ? StubHandler.JsonResponse(r.Status, r.Body)
                : StubHandler.JsonResponse(HttpStatusCode.NotFound, """{"title":"no such route in the stub"}"""));
        });

    [Fact]
    public async Task Get_summary_fetches_the_run_and_its_report_and_summarises_it()
    {
        var handler = Router(new()
        {
            [$"GET /runs/{Id}"] = (HttpStatusCode.OK, RunJson()),
            [$"GET /runs/{Id}/report"] = (HttpStatusCode.OK, ReportJson(Id, failing: true)),
        });

        var result = await EvalHarnessTools.GetSummary(Client(handler), Id);

        using var json = JsonDocument.Parse(result);
        Assert.Equal(Id, json.RootElement.GetProperty("runId").GetString());
        Assert.Equal("bad", json.RootElement.GetProperty("problems")[0].GetProperty("id").GetString());
        Assert.Equal("retrieval-miss", json.RootElement.GetProperty("problems")[0].GetProperty("likelyCause").GetString());
        Assert.Equal(11, json.RootElement.GetProperty("tests").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Get_summary_of_an_unfinished_run_says_to_wait_and_does_not_fetch_a_report()
    {
        var handler = Router(new() { [$"GET /runs/{Id}"] = (HttpStatusCode.OK, RunJson("Running", null, null, null, completed: 1)) });

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.GetSummary(Client(handler), Id));

        Assert.Contains("still Running", ex.Message);
        Assert.Contains("1/2", ex.Message);
        Assert.Contains("get_run", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Get_summary_of_a_run_that_never_produced_a_report_explains_why()
    {
        var handler = Router(new()
        {
            [$"GET /runs/{Id}"] = (HttpStatusCode.OK, RunJson("Cancelled", false, 130, "Cancelled")),
            [$"GET /runs/{Id}/report"] = (HttpStatusCode.NotFound, """{"title":"The run ended as Cancelled without a report."}"""),
        });

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.GetSummary(Client(handler), Id));

        Assert.Contains("ended as Cancelled without a report", ex.Message);
    }

    [Fact]
    public async Task Compare_runs_detects_a_regression_between_two_stored_reports()
    {
        var handler = Router(new()
        {
            [$"GET /runs/{Id}/report"] = (HttpStatusCode.OK, ReportJson(Id, correctness: 1.0)),
            [$"GET /runs/{Id2}/report"] = (HttpStatusCode.OK, ReportJson(Id2, correctness: 0.2)),
        });

        var result = await EvalHarnessTools.CompareRuns(Client(handler), Id, Id2);

        using var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("hasRegression").GetBoolean());
        var changed = json.RootElement.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("metric").GetString() == "answerCorrectness");
        Assert.True(changed.GetProperty("regressed").GetBoolean());
        Assert.Equal(-0.8, changed.GetProperty("delta").GetDouble(), 6);
    }

    [Fact]
    public async Task Compare_runs_of_identical_reports_finds_no_regression()
    {
        var handler = Router(new()
        {
            [$"GET /runs/{Id}/report"] = (HttpStatusCode.OK, ReportJson(Id)),
            [$"GET /runs/{Id2}/report"] = (HttpStatusCode.OK, ReportJson(Id2)),
        });

        using var json = JsonDocument.Parse(await EvalHarnessTools.CompareRuns(Client(handler), Id, Id2));

        Assert.False(json.RootElement.GetProperty("hasRegression").GetBoolean());
    }

    // ---- cancel / list -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_run_posts_to_the_cancel_endpoint()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler((req, _) => { seen = req; return Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.Accepted, RunJson("Running", null, null, null))); });

        await EvalHarnessTools.CancelRun(Client(handler), Id);

        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal($"/runs/{Id}/cancel", seen.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Cancelling_a_finished_run_surfaces_the_conflict()
    {
        var handler = Json(HttpStatusCode.Conflict, """{"title":"The run has already finished.","status":409}""");

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.CancelRun(Client(handler), Id));

        Assert.Contains("already finished", ex.Message);
    }

    [Fact]
    public async Task List_datasets_returns_the_names()
    {
        var result = await EvalHarnessTools.ListDatasets(Client(Json(HttpStatusCode.OK, """["acme-handbook","acme-hard"]""")), default);

        using var json = JsonDocument.Parse(result);
        Assert.Equal(["acme-handbook", "acme-hard"], json.RootElement.GetProperty("datasets").EnumerateArray().Select(d => d.GetString()));
    }
}

/// <summary>The MCP tools against the real EvalHarness API running in-process (no sockets), end to end.</summary>
public class McpAgainstRealApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _http;
    private readonly EvalHarnessClient _client;

    public McpAgainstRealApiTests()
    {
        _http = new HttpClient(_factory.Server.CreateHandler());
        _client = new EvalHarnessClient(_http, new EvalHarnessOptions { BaseUrl = "http://localhost", ApiKey = ApiFactory.Key })
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
        };
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
    }

    private static string Field(string json, params string[] path)
    {
        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement;
        foreach (var p in path) element = int.TryParse(p, out var index) ? element[index] : element.GetProperty(p);
        return element.ToString();
    }

    private async Task<string> RunToSummary(string? baseline = null, int repeats = 1)
    {
        var started = await EvalHarnessTools.StartRun(_client, "sample", repeats: repeats, baselineRunId: baseline);
        var id = Field(started, "id");
        var finished = await EvalHarnessTools.GetRun(_client, id, waitSeconds: 30);
        Assert.Equal("Completed", Field(finished, "status"));
        return await EvalHarnessTools.GetSummary(_client, id);
    }

    [Fact]
    public async Task Agent_workflow_lists_starts_waits_and_reads_a_passing_summary()
    {
        Assert.Equal("sample", Field(await EvalHarnessTools.ListDatasets(_client, default), "datasets", "0"));

        var summary = await RunToSummary();

        Assert.Equal("True", Field(summary, "verdict", "passed"));
        Assert.Equal("Passed", Field(summary, "verdict", "outcome"));
        Assert.Equal("2", Field(summary, "tests", "total"));
        Assert.Equal("1", Field(summary, "metrics", "recallAt5"));
        Assert.Equal("OpenAI/gpt-4o-mini", Field(summary, "settings", "judgeModel"));
        Assert.DoesNotContain("Chunk text", summary); // the fake RAG's retrieved chunk text never reaches the agent
    }

    [Fact]
    public async Task A_refusal_despite_retrieval_is_diagnosed_from_the_real_report()
    {
        _factory.Rag = (_, _) => Task.FromResult(ApiFactory.GoodAnswer with { Answer = null, Status = "InsufficientContext" });

        var summary = await RunToSummary();

        Assert.Equal("False", Field(summary, "verdict", "passed"));
        Assert.Equal("QualityFailure", Field(summary, "verdict", "outcome"));
        Assert.Equal("refused-despite-retrieval", Field(summary, "problems", "0", "likelyCause"));
        Assert.Equal("1", Field(summary, "metrics", "refusalRate"));
    }

    [Fact]
    public async Task A_rag_outage_is_inconclusive_not_a_failure()
    {
        _factory.Rag = (_, _) => throw new RagApiException("connection refused");

        var summary = await RunToSummary();

        Assert.Equal("Inconclusive", Field(summary, "verdict", "outcome"));
        Assert.Equal("rag-api-error", Field(summary, "problems", "0", "likelyCause"));
    }

    [Fact]
    public async Task Comparing_two_real_runs_finds_the_regression_and_the_retrieval_mode_difference()
    {
        _factory.Settings["Regression:AllowSingleTestVariance"] = "false";
        var first = Field(await EvalHarnessTools.StartRun(_client, "sample", requestFields: new() { ["mode"] = "Hybrid" }), "id");
        await EvalHarnessTools.GetRun(_client, first, waitSeconds: 30);
        _factory.JudgeScore = 1;
        var second = Field(await EvalHarnessTools.StartRun(_client, "sample", requestFields: new() { ["mode"] = "Vector" }), "id");
        await EvalHarnessTools.GetRun(_client, second, waitSeconds: 30);

        var comparison = await EvalHarnessTools.CompareRuns(_client, first, second);

        Assert.Equal("True", Field(comparison, "hasRegression"));
        Assert.Contains("mode=Hybrid", comparison);
        Assert.Contains("mode=Vector", comparison);
        Assert.DoesNotContain("\\u0027", comparison); // apostrophes stay readable for the model
    }

    [Fact]
    public async Task Api_validation_errors_reach_the_agent_as_actionable_messages()
    {
        var unknown = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.StartRun(_client, "no-such-dataset"));
        Assert.Contains("Unknown dataset", unknown.Message);
        Assert.Contains("sample", unknown.Message); // lists what is available

        var badField = await Assert.ThrowsAsync<McpException>(() =>
            EvalHarnessTools.StartRun(_client, "sample", requestFields: new() { ["rerank"] = "true" }));
        Assert.Contains("not an allowed request field", badField.Message);

        var disallowedUrl = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.StartRun(_client, "sample", ragUrl: "http://169.254.169.254/"));
        Assert.Contains("allowlist", disallowedUrl.Message);
    }

    [Fact]
    public async Task A_wrong_key_is_reported_as_such()
    {
        var wrong = new EvalHarnessClient(_http, new EvalHarnessOptions { BaseUrl = "http://localhost", ApiKey = "not-the-key" });

        var ex = await Assert.ThrowsAsync<McpException>(() => EvalHarnessTools.ListDatasets(wrong, default));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task A_running_run_can_be_cancelled_through_the_tools()
    {
        _factory.Rag = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ApiFactory.GoodAnswer; };
        var id = Field(await EvalHarnessTools.StartRun(_client, "sample"), "id");
        await Task.Delay(200);

        await EvalHarnessTools.CancelRun(_client, id);
        var finished = await EvalHarnessTools.GetRun(_client, id, waitSeconds: 30);

        Assert.Equal("Cancelled", Field(finished, "status"));
        Assert.Equal("Cancelled", Field(finished, "outcome"));
    }
}

/// <summary>Starts the real MCP server process and speaks the protocol to it over stdio.</summary>
public class McpServerProcessTests
{
    private static string ServerDll()
    {
        // .../tests/EvalHarness.Tests/bin/<Config>/net8.0/  ->  .../src/EvalHarness.Mcp/bin/<Config>/net8.0/EvalHarness.Mcp.dll
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = dir.Name;
        var config = dir.Parent!.Name;
        var root = dir.Parent.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, "src", "EvalHarness.Mcp", "bin", config, framework, "EvalHarness.Mcp.dll");
    }

    private static async Task<List<JsonElement>> Converse(IDictionary<string, string?> env, params string[] requests)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{ServerDll()}\"")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var (k, v) in env) start.Environment[k] = v;

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        foreach (var request in requests) await process.StandardInput.WriteLineAsync(request);

        // Responses carry the request ids; read until every request that has an id has been answered.
        var expected = requests.Count(r => r.Contains("\"id\""));
        var responses = new List<JsonElement>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (responses.Count < expected)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception($"Server closed stdout early. stderr: {await stderr}");
            // Every stdout line must be a JSON-RPC message; a stray log line would break the protocol.
            responses.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        return responses;
    }

    private const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    [Fact]
    public async Task The_server_lists_its_six_tools_with_descriptions_and_a_clean_stdout()
    {
        var responses = await Converse(
            new Dictionary<string, string?> { ["EVALHARNESS_API_KEY"] = "k", ["EVALHARNESS_URL"] = "http://localhost:1" },
            Initialize, Initialized, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = responses.Single(r => r.GetProperty("id").GetInt32() == 2).GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order().ToArray();
        Assert.Equal(["cancel_run", "compare_runs", "get_run", "get_summary", "list_datasets", "start_run"], names);
        Assert.All(tools.EnumerateArray(), t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString())));

        var start = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "start_run");
        var properties = start.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("dataset", properties);
        Assert.Contains("requestFields", properties);
        Assert.Contains("baselineRunId", properties);
        Assert.Equal(["dataset"], start.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()!));
    }

    [Fact]
    public async Task A_tool_error_comes_back_as_a_readable_tool_result_not_a_protocol_failure()
    {
        // No API key: the tool must answer with isError and tell the model what to fix.
        var responses = await Converse(
            new Dictionary<string, string?> { ["EVALHARNESS_API_KEY"] = null, ["EVALHARNESS_URL"] = "http://localhost:1" },
            Initialize, Initialized, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_datasets","arguments":{}}}""");

        var result = responses.Single(r => r.GetProperty("id").GetInt32() == 2).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("EVALHARNESS_API_KEY", result.GetProperty("content")[0].GetProperty("text").GetString());
    }
}
