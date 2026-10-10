using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EvalHarness.Api;
using EvalHarness.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EvalHarness.Tests;

public class RagUrlPolicyTests
{
    private static RagUrlPolicy Policy(string allowed = "") => new("http://configured.test:8080", allowed);

    [Theory]
    [InlineData("http://configured.test:8080")]
    [InlineData("http://configured.test:8080/")]
    [InlineData("HTTP://CONFIGURED.TEST:8080/anything/below")]
    public void Configured_base_url_is_always_allowed(string url) => Assert.Null(Policy().Check(url));

    [Theory]
    [InlineData("http://configured.test:9090")]      // other port
    [InlineData("https://configured.test:8080")]     // other scheme
    [InlineData("http://evil.test:8080")]            // other host
    [InlineData("http://169.254.169.254/latest")]    // cloud metadata address
    [InlineData("http://localhost:8080")]
    public void Anything_else_is_rejected(string url) => Assert.NotNull(Policy().Check(url));

    [Fact]
    public void Allowlist_entries_extend_the_policy_and_accept_commas_or_semicolons()
    {
        var policy = Policy("https://rag.staging.example.com; http://host.docker.internal:8080 ,http://other.test");

        Assert.Null(policy.Check("https://rag.staging.example.com"));
        Assert.Null(policy.Check("http://host.docker.internal:8080/v2"));
        Assert.Null(policy.Check("http://other.test:80"));
        Assert.NotNull(policy.Check("https://rag.staging.example.com:8443"));
    }

    [Fact]
    public void Path_prefix_matches_whole_segments_only()
    {
        var policy = Policy("https://rag.example.com/api");

        Assert.Null(policy.Check("https://rag.example.com/api"));
        Assert.Null(policy.Check("https://rag.example.com/api/v2"));
        Assert.NotNull(policy.Check("https://rag.example.com/apiary"));
        Assert.NotNull(policy.Check("https://rag.example.com/other"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://configured.test:8080")]
    [InlineData("http://user:pw@configured.test:8080")]
    [InlineData("http://configured.test:8080/?key=1")]
    public void Malformed_or_credentialed_urls_are_rejected(string url) => Assert.NotNull(Policy().Check(url));

    [Fact]
    public void Malformed_allowlist_is_a_startup_error() =>
        Assert.Throws<InvalidOperationException>(() => Policy("http://ok.test, nonsense"));
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "test-key";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "evalharness-api-" + Guid.NewGuid().ToString("N"));
    public string DatasetsDir => Path.Combine(Root, "datasets");
    public string DataDir => Path.Combine(Root, "data");
    public Dictionary<string, string?> Settings { get; } = new();

    // Tests change these between runs; the fakes handed to the service read them on every call.
    public Func<string, CancellationToken, Task<RagResponse>> Rag { get; set; } = (_, _) => Task.FromResult(GoodAnswer);
    public int JudgeScore { get; set; } = 5;
    public bool JudgeSupported { get; set; } = true;

    /// <summary>When false only the RAG API is faked, so the real LLM/embedding clients (and their key check) are exercised.</summary>
    public bool FakeProviders { get; set; } = true;

    /// <summary>
    /// When set, runs use the real HTTP RAG client against this handler instead of the fake, so a test can inspect
    /// the exact request body that reaches the RAG API. Set before the first request (the host starts lazily).
    /// </summary>
    public HttpMessageHandler? RagHandler { get; set; }

    public static RagResponse GoodAnswer =>
        new("q", "18 days", "Answered", [new RetrievedChunk(1, null, "acme-handbook.md", "c1", "Chunk text", 0.9)], 25, 1);

    public ApiFactory(bool createDatasets = true)
    {
        Directory.CreateDirectory(DatasetsDir);
        if (createDatasets)
            File.WriteAllText(Path.Combine(DatasetsDir, "sample.json"), """
                { "name": "sample", "testCases": [
                  { "id": "a", "category": "c", "difficulty": "easy", "question": "How many days?", "expectedAnswer": "18 days",
                    "expectedSources": [{ "documentName": "acme-handbook.md" }] },
                  { "id": "b", "category": "c", "difficulty": "easy", "question": "Which portal?", "expectedAnswer": "The finance portal",
                    "expectedSources": [{ "documentName": "acme-handbook.md" }] } ] }
                """);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:Key"] = Key,
            ["Api:DatasetsDirectory"] = DatasetsDir,
            ["Api:DataDirectory"] = DataDir,
            ["Api:AllowedRagUrls"] = "http://allowed.test:9000",
        };
        foreach (var (k, v) in Settings) settings[k] = v;

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureServices(services => services.AddSingleton(new EvaluationServicesOverride(eval =>
        {
            if (RagHandler is { } handler)
                eval.AddHttpClient("IRagClient").ConfigurePrimaryHttpMessageHandler(() => handler);
            else
                eval.AddSingleton<IRagClient>(new FakeRag((q, ct) => Rag(q, ct)));
            if (!FakeProviders) return;
            eval.AddSingleton<ILlmClient>(new FakeLlm((system, _) => system.Contains("grader")
                ? $$"""{"score": {{JudgeScore}}, "explanation": "judged"}"""
                : $$"""{"claims":[{"claim":"c","supported":{{(JudgeSupported ? "true" : "false")}}}]}"""));
            eval.AddSingleton<IEmbeddingClient>(new FakeEmbedder(_ => [1f, 0f]));
        })));
    }

    public HttpClient AuthedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, Key);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

public class ApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private HttpClient? _client;
    private HttpClient Client => _client ??= _factory.AuthedClient();

    public void Dispose()
    {
        _client?.Dispose();
        _factory.Dispose();
    }

    private static readonly JsonSerializerOptions Json = EvalJson.Options;

    private async Task<(HttpResponseMessage Response, RunRecord? Run)> Post(object body, HttpClient? client = null)
    {
        var response = await (client ?? Client).PostAsJsonAsync("/runs", body, Json);
        var run = response.StatusCode == HttpStatusCode.Accepted ? await response.Content.ReadFromJsonAsync<RunRecord>(Json) : null;
        return (response, run);
    }

    private async Task<RunRecord> Wait(string id, Func<RunRecord, bool> until, HttpClient? client = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var run = await (client ?? Client).GetFromJsonAsync<RunRecord>($"/runs/{id}", Json);
            if (until(run!)) return run!;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting; last status {run!.Status}, progress {run.Progress}");
            await Task.Delay(20);
        }
    }

    private async Task<RunRecord> RunToEnd(object body)
    {
        var (response, run) = await Post(body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await Wait(run!.Id, r => r.IsFinished);
    }

    private async Task<string> ProblemTitle(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    // ---- auth --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_and_swagger_need_no_key()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Other_endpoints_reject_a_missing_or_wrong_key(string? key)
    {
        using var client = _factory.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, key);

        foreach (var path in new[] { "/runs", "/datasets", "/evaluators" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/runs", new { dataset = "sample" })).StatusCode);
    }

    [Fact]
    public async Task Correct_key_is_accepted() =>
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/runs")).StatusCode);

    [Fact]
    public async Task Service_refuses_to_start_without_a_key_unless_anonymous_access_is_explicit()
    {
        using var noKey = new ApiFactory();
        noKey.Settings["Api:Key"] = "";
        Assert.Throws<InvalidOperationException>(() => noKey.CreateClient());

        using var open = new ApiFactory();
        open.Settings["Api:Key"] = "";
        open.Settings["Api:AllowAnonymous"] = "true";
        using var client = open.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/runs")).StatusCode);
    }

    // ---- lifecycle ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_is_accepted_then_completes_with_progress_gates_and_report()
    {
        var (response, run) = await Post(new { dataset = "sample" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/runs/{run!.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal(2, run.Progress.Total);

        var done = await Wait(run.Id, r => r.IsFinished);
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.True(done.Passed);
        Assert.Equal(0, done.ExitCode);
        Assert.Equal("Passed", done.Outcome);
        Assert.Equal(new RunProgress(2, 2), done.Progress);
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.CompletedAt);

        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{run.Id}/report", Json);
        Assert.Equal(run.Id, report!.Run.RunId);
        Assert.Equal(2, report.Results.Count);
        Assert.True(report.QualityGates!.Passed);
        Assert.Equal(1.0, report.Aggregate.RecallAt5);
    }

    [Fact]
    public async Task Wrong_answers_complete_the_run_but_do_not_pass_it()
    {
        _factory.JudgeScore = 1;

        var done = await RunToEnd(new { dataset = "sample" });

        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.False(done.Passed);
        Assert.Equal(1, done.ExitCode);
        Assert.Equal("QualityFailure", done.Outcome);
    }

    [Fact]
    public async Task Rag_outage_is_inconclusive_not_a_quality_failure()
    {
        _factory.Rag = (_, _) => throw new RagApiException("connection refused");

        var done = await RunToEnd(new { dataset = "sample" });

        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Equal(3, done.ExitCode);
        Assert.Equal("Inconclusive", done.Outcome);
        Assert.False(done.Passed);
        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{done.Id}/report", Json);
        Assert.Equal(2, report!.Aggregate.ApiErrors);
        Assert.Equal(0, report.Aggregate.Failed);
    }

    [Fact]
    public async Task Evaluator_subset_and_parallelism_are_applied()
    {
        var done = await RunToEnd(new { dataset = "sample", evaluators = new[] { "exact-match", "retrieval-quality" }, parallelism = 2 });

        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{done.Id}/report", Json);
        Assert.Equal(["exact-match", "retrieval-quality"], report!.Run.Evaluators);
        Assert.Equal(2, report.Run.MaxParallelism);
    }

    [Fact]
    public async Task Allowed_rag_url_override_is_recorded_as_the_target()
    {
        var done = await RunToEnd(new { dataset = "sample", ragUrl = "http://allowed.test:9000" });
        Assert.Equal("http://allowed.test:9000/", done.Target);
        Assert.Equal("http://allowed.test:9000", done.Request.RagUrl);
    }

    [Fact]
    public async Task Without_an_override_the_configured_rag_url_is_the_target()
    {
        _factory.Settings["RagApi:BaseUrl"] = "http://configured.test:1234";
        var done = await RunToEnd(new { dataset = "sample" });
        Assert.Equal("http://configured.test:1234/", done.Target);
    }

    // ---- validation --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unknown_dataset_is_404_and_lists_the_available_ones()
    {
        var (response, _) = await Post(new { dataset = "nope" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("sample", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("..\\secret")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task Dataset_names_cannot_escape_the_datasets_folder(string name) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = name })).Response.StatusCode);

    [Fact]
    public async Task Invalid_options_are_rejected_up_front()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", evaluators = new[] { "nonsense" } })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", parallelism = 500 })).Response.StatusCode);
    }

    [Fact]
    public async Task Blank_optional_fields_mean_not_provided()
    {
        // What a form or template that pre-fills every field sends when nothing is filled in.
        var done = await RunToEnd(new { dataset = "sample", ragUrl = "", evaluators = Array.Empty<string>(), parallelism = 0, baselineRunId = "" });

        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Null(done.Request.RagUrl);
        Assert.Null(done.Request.Evaluators);
        Assert.Null(done.Request.Parallelism);
        Assert.Null(done.Request.BaselineRunId);
        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{done.Id}/report", Json);
        Assert.Equal(4, report!.Run.MaxParallelism); // the default, not 0
    }

    [Fact]
    public async Task Swagger_placeholder_values_are_still_rejected_rather_than_silently_ignored()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { dataset = "string" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", ragUrl = "string" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", evaluators = new[] { "string" } })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", baselineRunId = "string" })).Response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.test:9000")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://allowed.test:9000/?key=1")]
    [InlineData("not-a-url")]
    public async Task Rag_url_outside_the_allowlist_is_rejected(string url)
    {
        var (response, _) = await Post(new { dataset = "sample", ragUrl = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await Client.GetFromJsonAsync<RunRecord[]>("/runs", Json))!); // nothing was queued
    }

    [Fact]
    public async Task Malformed_body_is_400()
    {
        var response = await Client.PostAsync("/runs", new StringContent("{ nope", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_dataset_on_the_server_is_reported_as_a_server_error_with_the_problems()
    {
        File.WriteAllText(Path.Combine(_factory.DatasetsDir, "broken.json"), """{ "testCases": [ { "id": "x" } ] }""");

        var (response, _) = await Post(new { dataset = "broken" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("'question' is required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_provider_key_fails_the_request_instead_of_the_run()
    {
        // Real provider clients, no key anywhere: the failure must surface on POST.
        using var factory = new ApiFactory { FakeProviders = false };
        factory.Settings["Llm:ApiKeyEnvVar"] = "EVALHARNESS_UNSET_KEY";
        factory.Settings["Embedding:ApiKeyEnvVar"] = "EVALHARNESS_UNSET_KEY";
        using var client = factory.AuthedClient();

        var response = await client.PostAsJsonAsync("/runs", new { dataset = "sample" }, Json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("EVALHARNESS_UNSET_KEY", await response.Content.ReadAsStringAsync());
    }

    // ---- baseline / regression ---------------------------------------------------------------------------------

    [Fact]
    public async Task Baseline_run_enables_regression_detection()
    {
        _factory.Settings["Regression:AllowSingleTestVariance"] = "false"; // set before the host starts; the sample has 2 tests, so a 0.25 drop is under one test's worth
        var baseline = await RunToEnd(new { dataset = "sample" });
        _factory.JudgeScore = 4; // still passes every absolute gate, but correctness drops from 1.0 to 0.75

        var current = await RunToEnd(new { dataset = "sample", baselineRunId = baseline.Id });

        Assert.Equal(1, current.ExitCode);
        Assert.Equal("QualityFailure", current.Outcome);
        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{current.Id}/report", Json);
        Assert.True(report!.QualityGates!.Passed);
        Assert.True(report.Regression!.HasRegression);
        Assert.Equal(baseline.Id, report.Regression.BaselineRunId);
    }

    [Fact]
    public async Task Baseline_must_be_a_completed_run_that_exists()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", baselineRunId = new string('a', 32) })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", baselineRunId = "../../x" })).Response.StatusCode);
    }

    // ---- cancel / queue ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Running_run_can_be_cancelled_and_keeps_a_partial_report()
    {
        _factory.Rag = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ApiFactory.GoodAnswer; };
        var (_, run) = await Post(new { dataset = "sample" });
        await Wait(run!.Id, r => r.Status == RunStatus.Running);

        var cancel = await Client.PostAsync($"/runs/{run.Id}/cancel", null);
        var done = await Wait(run.Id, r => r.IsFinished);

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal(RunStatus.Cancelled, done.Status);
        Assert.Equal(130, done.ExitCode);
        Assert.False(done.Passed);
        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{run.Id}/report", Json);
        Assert.True(report!.Run.Cancelled);

        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/runs/{run.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Cancelling_runs_that_are_finishing_never_errors()
    {
        // Cancel requests race with runs completing (and disposing their token sources); none may surface as a 5xx.
        _factory.Settings["Api:MaxConcurrentRuns"] = "4";
        var ids = new List<string>();
        for (var i = 0; i < 20; i++) ids.Add((await Post(new { dataset = "sample", evaluators = new[] { "exact-match" } })).Run!.Id);

        await Task.WhenAll(ids.Select(async id =>
        {
            while (!(await Wait(id, _ => true)).IsFinished)
            {
                var response = await Client.PostAsync($"/runs/{id}/cancel", null);
                Assert.True((int)response.StatusCode < 500, $"cancel returned {(int)response.StatusCode}");
            }
        }));

        foreach (var id in ids) await Wait(id, r => r.IsFinished);
    }

    [Fact]
    public async Task Cancelling_an_unknown_run_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/runs/{new string('b', 32)}/cancel", null)).StatusCode);

    [Fact]
    public async Task Only_one_run_executes_at_a_time_and_the_queue_is_bounded()
    {
        _factory.Settings["Api:MaxConcurrentRuns"] = "1";
        _factory.Settings["Api:MaxQueuedRuns"] = "1";
        var gate = new TaskCompletionSource();
        _factory.Rag = async (_, _) => { await gate.Task; return ApiFactory.GoodAnswer; };

        var (_, first) = await Post(new { dataset = "sample" });
        await Wait(first!.Id, r => r.Status == RunStatus.Running);
        var (_, second) = await Post(new { dataset = "sample" });
        var third = await Post(new { dataset = "sample" });

        Assert.Equal(RunStatus.Queued, (await Client.GetFromJsonAsync<RunRecord>($"/runs/{second!.Id}", Json))!.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.Response.StatusCode);

        gate.SetResult();
        Assert.Equal(RunStatus.Completed, (await Wait(first.Id, r => r.IsFinished)).Status);
        Assert.Equal(RunStatus.Completed, (await Wait(second.Id, r => r.IsFinished)).Status);
    }

    [Fact]
    public async Task Queued_run_can_be_cancelled_before_it_starts()
    {
        _factory.Settings["Api:MaxConcurrentRuns"] = "1";
        var gate = new TaskCompletionSource();
        _factory.Rag = async (_, ct) => { await gate.Task.WaitAsync(ct); return ApiFactory.GoodAnswer; };
        var (_, first) = await Post(new { dataset = "sample" });
        await Wait(first!.Id, r => r.Status == RunStatus.Running);
        var (_, queued) = await Post(new { dataset = "sample" });

        await Client.PostAsync($"/runs/{queued!.Id}/cancel", null);
        var done = await Wait(queued.Id, r => r.IsFinished);

        Assert.Equal(RunStatus.Cancelled, done.Status);
        Assert.Null(done.StartedAt);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/runs/{queued.Id}/report")).StatusCode);

        gate.SetResult();
        await Wait(first.Id, r => r.IsFinished);
    }

    // ---- requestFields / repeats -------------------------------------------------------------------------------

    private const string RagJson = """{"answer":"18 days","status":"Answered","retrievedChunks":[{"id":"c1","sourceFile":"acme-handbook.md","text":"Chunk text","score":0.9}]}""";

    [Fact]
    public async Task Request_fields_reach_the_rag_api_request_body_and_are_recorded_in_the_report()
    {
        var stub = new StubHandler((_, _) => Task.FromResult(StubHandler.JsonResponse(HttpStatusCode.OK, RagJson)));
        _factory.RagHandler = stub;

        var done = await RunToEnd(new { dataset = "sample", evaluators = new[] { "exact-match", "retrieval-quality" }, requestFields = new Dictionary<string, string> { ["mode"] = "Vector", ["TOPK"] = "10" } });

        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Equal(2, stub.Calls);
        Assert.All(stub.Bodies, body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("Vector", json.RootElement.GetProperty("mode").GetString());
            Assert.Equal(10, json.RootElement.GetProperty("topK").GetInt32()); // sent as a number, in the allowlist's spelling
            Assert.True(json.RootElement.TryGetProperty("question", out _));
        });
        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{done.Id}/report", Json);
        Assert.Equal(new Dictionary<string, string> { ["mode"] = "Vector", ["topK"] = "10" }, report!.Run.Settings!.RequestFields);
    }

    [Theory]
    [InlineData("RagApi:BaseUrl")]   // a configuration path, not a request field
    [InlineData("question")]         // would overwrite the question being asked
    [InlineData("rerank")]           // simply not on the allowlist
    [InlineData("")]
    public async Task Request_fields_outside_the_allowlist_are_rejected(string key)
    {
        var (response, _) = await Post(new { dataset = "sample", requestFields = new Dictionary<string, string> { [key] = "x" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("topK", await response.Content.ReadAsStringAsync()); // tells the caller what is allowed
    }

    [Theory]
    [InlineData("a-value-that-is-much-too-long-a-value-that-is-much-too-long-a-value-that-is-much-too-long")]
    [InlineData("line1\nline2")]
    public async Task Request_field_values_are_validated(string value) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", requestFields = new Dictionary<string, string> { ["mode"] = value } })).Response.StatusCode);

    [Fact]
    public async Task Blank_request_field_values_mean_not_provided()
    {
        var done = await RunToEnd(new { dataset = "sample", requestFields = new Dictionary<string, string> { ["mode"] = "", ["topK"] = "  " } });
        Assert.Null(done.Request.RequestFields);
    }

    [Fact]
    public async Task The_allowed_request_fields_are_configurable()
    {
        _factory.Settings["Api:AllowedRequestFields"] = "rerank";

        Assert.Equal(HttpStatusCode.Accepted, (await Post(new { dataset = "sample", requestFields = new Dictionary<string, string> { ["rerank"] = "true" } })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", requestFields = new Dictionary<string, string> { ["mode"] = "Vector" } })).Response.StatusCode);
    }

    [Fact]
    public async Task Comparing_runs_with_different_retrieval_modes_warns_that_they_are_not_like_for_like()
    {
        _factory.Settings["Regression:AllowSingleTestVariance"] = "true";
        var hybrid = await RunToEnd(new { dataset = "sample", requestFields = new Dictionary<string, string> { ["mode"] = "Hybrid" } });

        var vector = await RunToEnd(new { dataset = "sample", baselineRunId = hybrid.Id, requestFields = new Dictionary<string, string> { ["mode"] = "Vector" } });

        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{vector.Id}/report", Json);
        Assert.Contains(report!.Regression!.Warnings, w => w.Contains("request fields") && w.Contains("mode=Hybrid") && w.Contains("mode=Vector"));
    }

    [Fact]
    public async Task Report_records_the_judge_and_embedding_models_and_prompt_fingerprint()
    {
        var done = await RunToEnd(new { dataset = "sample" });

        var settings = (await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{done.Id}/report", Json))!.Run.Settings!;
        Assert.Equal("OpenAI/gpt-4o-mini", settings.JudgeModel);
        Assert.Equal("OpenAI/text-embedding-3-small", settings.EmbeddingModel);
        Assert.Matches("^[0-9a-f]{12}$", settings.PromptsFingerprint);
        Assert.Equal(1, settings.Repeats);
    }

    [Fact]
    public async Task Repeats_ask_every_question_that_many_times_and_are_recorded()
    {
        var calls = 0;
        _factory.Rag = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(ApiFactory.GoodAnswer); };

        var done = await RunToEnd(new { dataset = "sample", repeats = 3 });

        Assert.Equal(6, calls); // 2 tests x 3 repeats
        Assert.Equal(new RunProgress(2, 2), done.Progress); // progress counts tests, not attempts
        var report = await Client.GetFromJsonAsync<EvaluationRun>($"/runs/{done.Id}/report", Json);
        Assert.Equal(3, report!.Run.Settings!.Repeats);
        Assert.All(report.Results, r => Assert.Equal(3, r.RepeatOutcomes!.Count));
        Assert.Equal(0, report.Aggregate.FlakyTests);
    }

    [Fact]
    public async Task Repeats_out_of_range_is_rejected_and_zero_means_not_provided()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", repeats = 21 })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { dataset = "sample", repeats = -1 })).Response.StatusCode);

        var done = await RunToEnd(new { dataset = "sample", repeats = 0 });
        Assert.Null(done.Request.Repeats);
    }

    // ---- queries -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Datasets_and_evaluators_are_listed()
    {
        Assert.Equal(["sample"], (await Client.GetFromJsonAsync<string[]>("/datasets"))!);

        var evaluators = await Client.GetFromJsonAsync<JsonElement>("/evaluators");
        Assert.Equal(EvaluatorNames.All.Count, evaluators.GetArrayLength());
    }

    [Fact]
    public async Task Runs_are_listed_newest_first_with_a_limit()
    {
        var first = await RunToEnd(new { dataset = "sample" });
        await Task.Delay(5);
        var second = await RunToEnd(new { dataset = "sample" });

        var all = await Client.GetFromJsonAsync<RunRecord[]>("/runs", Json);
        var one = await Client.GetFromJsonAsync<RunRecord[]>("/runs?limit=1", Json);

        Assert.Equal([second.Id, first.Id], all!.Select(r => r.Id));
        Assert.Equal([second.Id], one!.Select(r => r.Id));
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    public async Task Malformed_run_ids_are_404(string id)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/runs/{id}/report")).StatusCode);
    }

    [Fact]
    public async Task Report_of_a_run_in_progress_is_409()
    {
        var gate = new TaskCompletionSource();
        _factory.Rag = async (_, ct) => { await gate.Task.WaitAsync(ct); return ApiFactory.GoodAnswer; };
        var (_, run) = await Post(new { dataset = "sample" });

        Assert.Equal(HttpStatusCode.Conflict, (await Client.GetAsync($"/runs/{run!.Id}/report")).StatusCode);

        gate.SetResult();
        await Wait(run.Id, r => r.IsFinished);
    }

    // ---- persistence -------------------------------------------------------------------------------------------

    [Fact]
    public async Task History_and_reports_survive_a_restart_and_interrupted_runs_are_marked_failed()
    {
        var done = await RunToEnd(new { dataset = "sample" });

        // An in-flight record left behind by a process that died.
        var stranded = new RunRecord { Id = new string('c', 32), Status = RunStatus.Running, CreatedAt = DateTimeOffset.UtcNow, Dataset = "sample" };
        await File.WriteAllTextAsync(Path.Combine(_factory.DataDir, "runs", $"{stranded.Id}.run.json"), JsonSerializer.Serialize(stranded, Json));

        using var restarted = new ApiFactory(createDatasets: false);
        // Point the second instance at the first one's data.
        restarted.Settings["Api:DataDirectory"] = _factory.DataDir;
        restarted.Settings["Api:DatasetsDirectory"] = _factory.DatasetsDir;
        using var client = restarted.AuthedClient();

        var runs = await client.GetFromJsonAsync<RunRecord[]>("/runs", Json);
        Assert.Contains(runs!, r => r.Id == done.Id && r.Status == RunStatus.Completed);
        var interrupted = runs!.Single(r => r.Id == stranded.Id);
        Assert.Equal(RunStatus.Failed, interrupted.Status);
        Assert.Contains("restart", interrupted.Error);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/runs/{done.Id}/report")).StatusCode);

        // And the old report can serve as a baseline for a run on the new instance.
        var (response, _) = await Post(new { dataset = "sample", baselineRunId = done.Id }, client);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Report_file_uses_the_cli_format_so_it_works_as_a_cli_baseline()
    {
        var done = await RunToEnd(new { dataset = "sample" });

        var report = await EvalHarness.Reporting.JsonReportStore.ReadAsync(Path.Combine(_factory.DataDir, "runs", $"{done.Id}.report.json"));

        Assert.Equal(done.Id, report.Run.RunId);
        Assert.Equal(EvaluationRun.CurrentSchemaVersion, report.SchemaVersion);
    }
}
