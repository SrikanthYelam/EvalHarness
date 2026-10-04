using System.Text.Json;
using EvalHarness.Cli;
using EvalHarness.Hosting;
using EvalHarness.Core;
using EvalHarness.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EvalHarness.Tests;

public class CliTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private const string Dataset = """
        { "name": "cli-test", "testCases": [
          { "id": "a", "category": "c", "difficulty": "easy", "question": "How many days?", "expectedAnswer": "18 days",
            "expectedSources": [{ "documentName": "acme-handbook.md" }] },
          { "id": "b", "category": "c", "difficulty": "easy", "question": "Which portal?", "expectedAnswer": "The finance portal",
            "expectedSources": [{ "documentName": "acme-handbook.md" }] }
        ] }
        """;

    private string WriteDataset(string content = Dataset)
    {
        var path = _dir.File("dataset.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static RagResponse Answer(string answer, string doc = "acme-handbook.md") =>
        new("q", answer, "Answered", [new RetrievedChunk(1, null, doc, "c1", "Chunk text", 0.9)], 25, 1);

    private static Func<string, CancellationToken, Task<RagResponse>> GoodRag => (_, _) => Task.FromResult(Answer("18 days"));

    private static FakeLlm Judge(int correctnessScore = 5, bool supported = true) => new((system, _) =>
        system.Contains("grader")
            ? $$"""{"score": {{correctnessScore}}, "explanation": "judged"}"""
            : $$"""{"claims":[{"claim":"c","supported":{{(supported ? "true" : "false")}}}]}""");

    private async Task<(int Exit, string Out, string Err)> Invoke(
        string[] args, Func<string, CancellationToken, Task<RagResponse>>? rag = null, FakeLlm? llm = null, CancellationToken ct = default,
        IRagClient? ragClient = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, stdout, stderr, ct, services =>
        {
            services.AddSingleton<IRagClient>(ragClient ?? new FakeRag(rag ?? GoodRag));
            services.AddSingleton<ILlmClient>(llm ?? Judge());
            services.AddSingleton<IEmbeddingClient>(new FakeEmbedder(_ => [1f, 0f]));
        });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string[] RunArgs(params string[] extra) =>
        ["run", "--dataset", WriteDataset(), "--output", _dir.File("reports"), .. extra];

    private async Task<EvaluationRun> LatestReport() => await JsonReportStore.ReadAsync(_dir.File("reports/latest.json"));

    // ---- validate / list / usage -------------------------------------------------------------------------------

    [Fact]
    public async Task Validate_accepts_a_good_dataset()
    {
        var (exit, output, _) = await Invoke(["validate", "--dataset", WriteDataset()]);

        Assert.Equal(0, exit);
        Assert.Contains("OK: 'cli-test' has 2 valid test cases", output);
    }

    [Fact]
    public async Task Validate_lists_every_problem_and_exits_two()
    {
        var (exit, _, err) = await Invoke(["validate", "--dataset", WriteDataset("""{ "testCases": [ { "id": "x" }, { "id": "x" } ] }""")]);

        Assert.Equal(2, exit);
        Assert.Contains("duplicate id", err);
        Assert.Contains("'question' is required", err);
    }

    [Fact]
    public async Task List_evaluators_shows_all_of_them()
    {
        var (exit, output, _) = await Invoke(["list-evaluators"]);

        Assert.Equal(0, exit);
        foreach (var name in EvaluatorNames.All) Assert.Contains(name, output);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("run")]                                         // missing --dataset
    [InlineData("run --dataset")]                               // option without value
    [InlineData("run --dataset x.json --nope 1")]               // unknown option
    [InlineData("compare --baseline only-one.json")]            // missing --current
    public async Task Bad_usage_exits_two(string commandLine)
    {
        var (exit, _, err) = await Invoke(commandLine.Split(' '));

        Assert.Equal(2, exit);
        Assert.Contains("error:", err);
    }

    [Fact]
    public async Task Help_prints_usage()
    {
        var (exit, output, _) = await Invoke(["--help"]);
        Assert.Equal(0, exit);
        Assert.Contains("Exit codes", output);
    }

    // ---- run ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_with_good_answers_passes_writes_a_report_and_exits_zero()
    {
        var (exit, output, _) = await Invoke(RunArgs());

        Assert.Equal(0, exit);
        Assert.Contains("OVERALL: PASS", output);
        var report = await LatestReport();
        Assert.Equal(2, report.Aggregate.Passed);
        Assert.Equal(1.0, report.Aggregate.RecallAt1);
        Assert.Equal(1.0, report.Aggregate.AnswerCorrectness);
        Assert.Equal(1.0, report.Aggregate.AverageCosineSimilarity);
        Assert.Equal(25, report.Aggregate.AverageLatencyMs);
        Assert.True(report.QualityGates!.Passed);
    }

    [Fact]
    public async Task Run_with_a_wrong_answer_exits_one()
    {
        var (exit, output, _) = await Invoke(RunArgs(), llm: Judge(correctnessScore: 1));

        Assert.Equal(1, exit);
        Assert.Contains("FAIL", output);
        var report = await LatestReport();
        Assert.Equal(2, report.Aggregate.Failed);
    }

    [Fact]
    public async Task Hallucinated_answer_fails_through_faithfulness()
    {
        var (exit, _, _) = await Invoke(RunArgs(), llm: Judge(supported: false));

        Assert.Equal(1, exit);
        Assert.All((await LatestReport()).Results, r => Assert.Contains("faithfulness", r.FailureReason));
    }

    [Fact]
    public async Task Rag_api_outage_is_inconclusive_exit_three_not_a_quality_failure()
    {
        var (exit, output, _) = await Invoke(RunArgs(), rag: (_, _) => throw new RagApiException("connection refused"));

        Assert.Equal(3, exit);
        Assert.Contains("INCONCLUSIVE", output);
        var report = await LatestReport();
        Assert.Equal(2, report.Aggregate.ApiErrors);
        Assert.Equal(0, report.Aggregate.Failed);
        Assert.All(report.Results, r => Assert.Equal(TestOutcome.ApiError, r.Outcome));
    }

    [Fact]
    public async Task Judge_outage_is_inconclusive_exit_three()
    {
        var llm = new FakeLlm((_, _) => throw new InvalidOperationException("judge unavailable"));

        var (exit, _, _) = await Invoke(RunArgs(), llm: llm);

        Assert.Equal(3, exit);
        Assert.All((await LatestReport()).Results, r => Assert.Equal(TestOutcome.EvaluatorError, r.Outcome));
    }

    [Fact]
    public async Task Evaluators_option_limits_the_run_and_needs_no_llm_or_key()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // No provider overrides: creating an LLM or embedding client without a key would be a configuration error.
        var config = _dir.File("nokey.json");
        await File.WriteAllTextAsync(config, """{ "Llm": { "ApiKeyEnvVar": "EVALHARNESS_UNSET_KEY" }, "Embedding": { "ApiKeyEnvVar": "EVALHARNESS_UNSET_KEY" } }""");

        var exit = await CliApp.RunAsync(
            ["run", "--dataset", WriteDataset(), "--output", _dir.File("reports"), "--config", config, "--evaluators", "exact-match,retrieval-quality"],
            stdout, stderr, default, services => services.AddSingleton<IRagClient>(new FakeRag(GoodRag)));

        Assert.Equal(0, exit);
        var report = await LatestReport();
        Assert.Equal(["exact-match", "retrieval-quality"], report.Run.Evaluators);
        Assert.Null(report.Aggregate.AnswerCorrectness);
    }

    [Fact]
    public async Task Missing_api_key_for_an_enabled_llm_evaluator_is_a_configuration_error()
    {
        var config = _dir.File("nokey.json");
        await File.WriteAllTextAsync(config, """{ "Llm": { "ApiKeyEnvVar": "EVALHARNESS_UNSET_KEY" }, "Embedding": { "ApiKeyEnvVar": "EVALHARNESS_UNSET_KEY" } }""");
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(RunArgs("--config", config), new StringWriter(), stderr, default,
            services => services.AddSingleton<IRagClient>(new FakeRag(GoodRag)));

        Assert.Equal(2, exit);
        Assert.Contains("EVALHARNESS_UNSET_KEY", stderr.ToString());
    }

    [Fact]
    public async Task Invalid_dataset_exits_two_without_calling_the_rag_api()
    {
        var rag = new FakeRag(GoodRag);

        var (exit, _, err) = await Invoke(["run", "--dataset", WriteDataset("{}"), "--output", _dir.File("reports")], ragClient: rag);

        Assert.Equal(2, exit);
        Assert.Contains("testCases", err);
        Assert.Equal(0, rag.Calls);
    }

    [Fact]
    public async Task Unknown_evaluator_name_exits_two()
    {
        var (exit, _, err) = await Invoke(RunArgs("--evaluators", "exact-match,nonsense"));
        Assert.Equal(2, exit);
        Assert.Contains("nonsense", err);
    }

    [Fact]
    public async Task Rag_url_option_must_be_a_valid_http_url()
    {
        var (exit, _, err) = await Invoke(RunArgs("--rag-url", "ftp://nope"));
        Assert.Equal(2, exit);
        Assert.Contains("not a valid http(s) URL", err);
    }

    [Fact]
    public async Task Rag_url_option_is_recorded_in_the_report_without_credentials_or_query()
    {
        await Invoke(RunArgs("--rag-url", "http://user:secret@rag.example:9000/api?key=abc"));

        var target = (await LatestReport()).Run.Target;
        Assert.Equal("http://rag.example:9000/api", target);
    }

    [Fact]
    public async Task Rag_api_that_never_becomes_ready_exits_three_without_running()
    {
        var (exit, _, err) = await Invoke(RunArgs("--wait-for-ready", "5"), ragClient: new NeverReadyRag());

        Assert.Equal(3, exit);
        Assert.Contains("not ready", err);
        Assert.False(File.Exists(_dir.File("reports/latest.json")));
    }

    private sealed class NeverReadyRag : IRagClient
    {
        public Task<RagResponse> AskAsync(string question, CancellationToken cancellationToken) => throw new InvalidOperationException("should not be called");
        public Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) => throw new RagApiException("The RAG API was not ready after 5s");
    }

    [Fact]
    public async Task Cancelled_run_writes_a_partial_report_and_exits_130()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (exit, output, _) = await Invoke(RunArgs(), ct: cts.Token);

        Assert.Equal(130, exit);
        Assert.Contains("CANCELLED", output);
        Assert.True((await LatestReport()).Run.Cancelled);
    }

    // ---- regression --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Run_against_a_baseline_detects_regression_and_exits_one_even_when_absolute_gates_pass()
    {
        // Baseline: perfect. Current: judge scores 4/5 (0.75) -> still passes every absolute gate, but correctness dropped 0.25.
        await Invoke(RunArgs());
        var baselinePath = _dir.File("baseline.json");
        File.Copy(_dir.File("reports/latest.json"), baselinePath);

        var (exit, output, _) = await Invoke(RunArgs("--baseline", baselinePath), llm: Judge(correctnessScore: 4));

        Assert.Equal(1, exit);
        Assert.Contains("REGRESSION DETECTED", output);
        Assert.Contains("answerCorrectness", output);
        var report = await LatestReport();
        Assert.True(report.QualityGates!.Passed);
        Assert.True(report.Regression!.HasRegression);
    }

    [Fact]
    public async Task Run_against_an_equal_baseline_passes()
    {
        await Invoke(RunArgs());
        var baselinePath = _dir.File("baseline.json");
        File.Copy(_dir.File("reports/latest.json"), baselinePath);

        var (exit, output, _) = await Invoke(RunArgs("--baseline", baselinePath));

        Assert.Equal(0, exit);
        Assert.Contains("no regression", output);
    }

    [Fact]
    public async Task Baseline_can_come_from_configuration_instead_of_the_command_line()
    {
        await Invoke(RunArgs());
        var baselinePath = _dir.File("baseline.json");
        File.Copy(_dir.File("reports/latest.json"), baselinePath);
        var config = _dir.File("baseline-config.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { Baseline = baselinePath }));

        var (exit, output, _) = await Invoke(RunArgs("--config", config), llm: Judge(correctnessScore: 1));

        Assert.Equal(1, exit);
        Assert.Contains("Regression vs baseline", output);
    }

    [Fact]
    public async Task Missing_baseline_exits_two_before_running()
    {
        var rag = new FakeRag(GoodRag);

        var (exit, _, err) = await Invoke(RunArgs("--baseline", _dir.File("nope.json")), ragClient: rag);

        Assert.Equal(2, exit);
        Assert.Contains("nope.json", err);
        Assert.Equal(0, rag.Calls);
    }

    [Fact]
    public async Task Compare_command_gates_on_regression()
    {
        await Invoke(RunArgs());
        var good = _dir.File("good.json");
        File.Copy(_dir.File("reports/latest.json"), good);
        await Invoke(RunArgs(), llm: Judge(correctnessScore: 1));
        var bad = _dir.File("bad.json");
        File.Copy(_dir.File("reports/latest.json"), bad);
        var comparison = _dir.File("out/comparison.json");

        var regressed = await Invoke(["compare", "--baseline", good, "--current", bad, "--output", comparison]);
        var steady = await Invoke(["compare", "--baseline", good, "--current", good]);
        var improved = await Invoke(["compare", "--baseline", bad, "--current", good]);

        Assert.Equal(1, regressed.Exit);
        Assert.Contains("REGRESSION DETECTED", regressed.Out);
        Assert.Equal(0, steady.Exit);
        Assert.Equal(0, improved.Exit);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(comparison));
        Assert.True(json.RootElement.GetProperty("hasRegression").GetBoolean());
    }

    [Fact]
    public async Task Compare_with_a_missing_report_exits_two()
    {
        var (exit, _, err) = await Invoke(["compare", "--baseline", _dir.File("a.json"), "--current", _dir.File("b.json")]);
        Assert.Equal(2, exit);
        Assert.Contains("a.json", err);
    }

    // ---- configuration -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Parallelism_option_is_applied()
    {
        var rag = new FakeRag(async (_, _) => { await Task.Delay(30); return Answer("18 days"); });
        var many = Enumerable.Range(0, 8).Select(i => $$"""{ "id": "t{{i}}", "category": "c", "difficulty": "easy", "question": "q{{i}}", "expectedAnswer": "a", "expectedSources": [{ "documentName": "acme-handbook.md" }] }""");
        var dataset = WriteDataset($$"""{ "testCases": [ {{string.Join(",", many)}} ] }""");

        var (exit, _, _) = await Invoke(["run", "--dataset", dataset, "--output", _dir.File("reports"), "--parallelism", "2"], ragClient: rag);

        Assert.Equal(0, exit);
        Assert.InRange(rag.MaxConcurrency, 2, 2);
        Assert.Equal(2, (await LatestReport()).Run.MaxParallelism);
    }

    [Fact]
    public async Task Config_file_can_override_quality_gates()
    {
        var config = _dir.File("strict.json");
        await File.WriteAllTextAsync(config, """{ "QualityGates": { "MaxAverageLatencyMs": 1 } }""");

        var (exit, _, _) = await Invoke(RunArgs("--config", config)); // fake latency is 25ms

        Assert.Equal(1, exit);
    }

    // The other tests replace the LLM/embedding clients with fakes; these exercise the real DI wiring.
    [Fact]
    public void Real_provider_clients_resolve_from_the_container_when_a_key_is_configured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:ApiKey"] = "sk-test", ["Embedding:ApiKey"] = "sk-test",
        }).Build();

        using var services = Composition.Build(config);

        Assert.NotNull(services.GetRequiredService<ILlmClient>());
        Assert.NotNull(services.GetRequiredService<IEmbeddingClient>());
        Assert.NotNull(services.GetRequiredService<IRagClient>());
        Assert.NotNull(services.GetRequiredService<EvalHarness.Runner.EvaluationRunner>()); // default evaluators incl. LLM ones
    }

    [Fact]
    public void Real_provider_clients_report_a_missing_key_as_a_configuration_error()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:ApiKeyEnvVar"] = "EVALHARNESS_UNSET_KEY", ["Embedding:ApiKeyEnvVar"] = "EVALHARNESS_UNSET_KEY",
        }).Build();
        using var services = Composition.Build(config);

        Assert.Throws<ConfigurationException>(() => services.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public void Unsupported_provider_is_a_configuration_error()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Provider"] = "Mystery", ["Llm:ApiKey"] = "sk-test",
        }).Build();
        using var services = Composition.Build(config);

        var ex = Assert.Throws<ConfigurationException>(() => services.GetRequiredService<ILlmClient>());
        Assert.Contains("Mystery", ex.Message);
    }

    [Fact]
    public void Shipped_appsettings_bind_to_the_option_classes()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();

        var evaluation = Composition.Bind<EvaluationOptions>(config, "Evaluation");
        var rag = Composition.Bind<RagApiOptions>(config, "RagApi");

        foreach (var name in EvaluatorNames.All) Assert.True(evaluation.Evaluators.ContainsKey(name), name);
        Assert.True(evaluation.SettingsFor(EvaluatorNames.AnswerCorrectness).Gating);
        Assert.False(evaluation.SettingsFor(EvaluatorNames.RetrievalRelevance).Enabled);
        Assert.Equal("/ask", rag.AskPath);
    }
}
