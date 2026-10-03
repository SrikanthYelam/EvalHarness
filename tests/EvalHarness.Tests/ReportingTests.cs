using System.Text.Json;
using EvalHarness.Core;
using EvalHarness.Reporting;
using EvalHarness.Runner;

namespace EvalHarness.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evalharness-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ReportingTests
{
    private static EvaluationRun SampleRun()
    {
        var failed = new TestResult("q-2", "expenses", "hard", "Receipt needed?", "Yes, over $25", TestOutcome.Failed,
            "No", "Answered", [Build.Chunk(1, "Receipts over $25")], 120, 2, 300,
            [new EvaluatorResult("answer-correctness", EvaluatorStatus.Failed, 0, "Contradicts expected answer") { Gating = true },
             new EvaluatorResult("retrieval-quality", EvaluatorStatus.Passed, 1, "found", new Dictionary<string, double> { ["recall@1"] = 1 })],
            "answer-correctness: Contradicts expected answer");
        var outage = new TestResult("q-3", "expenses", "easy", "Deadline?", "30 days", TestOutcome.ApiError,
            null, null, [], null, 4, 50, [], "RAG API failure: HTTP 503");
        var passed = Runs.Test("q-1", TestOutcome.Passed);
        var run = Runs.Run(ResultAggregator.Aggregate([passed, failed, outage], 4321), "abcdef0123456789", "sha", passed, failed, outage);
        return run with
        {
            QualityGates = QualityGateEvaluator.Evaluate(run.Aggregate, new QualityGateOptions()),
            Regression = RegressionDetector.Compare(run, run, new RegressionOptions()),
        };
    }

    [Fact]
    public async Task Json_report_round_trips()
    {
        using var dir = new TempDir();
        var run = SampleRun();

        await JsonReportStore.WriteAsync(run, dir.File("r.json"));
        var read = await JsonReportStore.ReadAsync(dir.File("r.json"));

        // Records compare lists by reference, so compare the serialised form instead.
        Assert.Equal(JsonSerializer.Serialize(run, EvalJson.Options), JsonSerializer.Serialize(read, EvalJson.Options));
        Assert.Equal(run.Aggregate, read.Aggregate);
        Assert.Equal(run.Results.Select(r => (r.Id, r.Outcome)), read.Results.Select(r => (r.Id, r.Outcome)));
        Assert.Equal(EvaluatorStatus.Failed, read.Results[1].EvaluatorResults[0].Status);
        Assert.True(read.Results[1].EvaluatorResults[0].Gating);
        Assert.Equal(1, read.Results[1].EvaluatorResults[1].Metrics!["recall@1"]);
        Assert.Equal(run.QualityGates!.Checks.Count, read.QualityGates!.Checks.Count);
        Assert.NotNull(read.Regression);
    }

    [Fact]
    public async Task Json_report_is_camel_case_with_string_enums_and_the_documented_sections()
    {
        using var dir = new TempDir();
        await JsonReportStore.WriteAsync(SampleRun(), dir.File("r.json"));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(dir.File("r.json")));
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("run", out var run));
        Assert.True(run.TryGetProperty("startedAt", out _));
        Assert.True(run.TryGetProperty("durationMs", out _));
        Assert.True(root.TryGetProperty("dataset", out _));
        Assert.True(root.GetProperty("aggregate").TryGetProperty("recallAt5", out _));
        Assert.Equal("Failed", root.GetProperty("results")[1].GetProperty("outcome").GetString());
        Assert.Equal("ApiError", root.GetProperty("results")[2].GetProperty("outcome").GetString());
        Assert.Contains("HTTP 503", root.GetProperty("results")[2].GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task Writing_to_a_directory_creates_a_timestamped_report_and_latest_json()
    {
        using var dir = new TempDir();

        var path = await JsonReportStore.WriteToDirectoryAsync(SampleRun(), Path.Combine(dir.Path, "nested", "reports"));

        Assert.StartsWith("eval-", Path.GetFileName(path));
        Assert.Contains("abcdef01", Path.GetFileName(path));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "latest.json")));
        Assert.Equal(await File.ReadAllTextAsync(path), await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "latest.json")));
    }

    [Fact]
    public async Task Reading_a_missing_or_foreign_file_is_a_clear_error()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("junk.json"), "not json");
        await File.WriteAllTextAsync(dir.File("v99.json"), """{"schemaVersion": 99, "run": null, "dataset": null, "aggregate": null, "results": []}""");

        await Assert.ThrowsAsync<InvalidDataException>(() => JsonReportStore.ReadAsync(dir.File("missing.json")));
        await Assert.ThrowsAsync<InvalidDataException>(() => JsonReportStore.ReadAsync(dir.File("junk.json")));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => JsonReportStore.ReadAsync(dir.File("v99.json")));
        Assert.Contains("schema version 99", ex.Message);
    }

    [Fact]
    public void Console_report_shows_overall_result_metrics_failures_and_reasons()
    {
        var writer = new StringWriter();

        ConsoleReportWriter.Write(SampleRun(), writer);
        var text = writer.ToString();

        Assert.Contains("OVERALL:", text);
        Assert.Contains("1 passed", text);
        Assert.Contains("1 failed (wrong answer)", text);
        Assert.Contains("1 RAG API errors", text);
        Assert.Contains("Answer correctness", text);
        Assert.Contains("Recall@1 / @3 / @5", text);
        Assert.Contains("Quality gates", text);
        Assert.Contains("q-2", text);
        Assert.Contains("reason: answer-correctness: Contradicts expected answer", text);
        Assert.Contains("reason: RAG API failure: HTTP 503", text);
        Assert.Contains("[API ERROR]", text);
        Assert.Contains("Regression vs baseline", text);
    }

    [Fact]
    public void Console_report_marks_cancelled_runs()
    {
        var run = SampleRun();
        var writer = new StringWriter();

        ConsoleReportWriter.Write(run with { Run = run.Run with { Cancelled = true } }, writer);

        Assert.Contains("CANCELLED", writer.ToString());
    }
}
