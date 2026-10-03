using System.Globalization;
using EvalHarness.Core;
using EvalHarness.Runner;

namespace EvalHarness.Reporting;

public static class ConsoleReportWriter
{
    private static readonly Dictionary<string, string> ShortNames = new()
    {
        [EvaluatorNames.ExactMatch] = "exact",
        [EvaluatorNames.CosineSimilarity] = "cosine",
        [EvaluatorNames.AnswerCorrectness] = "correct",
        [EvaluatorNames.Faithfulness] = "faithful",
        [EvaluatorNames.RetrievalQuality] = "recall",
        [EvaluatorNames.RetrievalRelevance] = "relevance",
    };

    public static void Write(EvaluationRun run, TextWriter w)
    {
        var a = run.Aggregate;
        var exit = ExitCodes.For(run);

        w.WriteLine($"EvalHarness - dataset '{run.Dataset.Name}' ({run.Dataset.TestCount} tests), run {run.Run.RunId[..8]}");
        w.WriteLine($"Target: {run.Run.Target}   Parallelism: {run.Run.MaxParallelism}   Duration: {Duration(a.TotalExecutionMs)}");
        w.WriteLine($"Evaluators: {string.Join(", ", run.Run.Evaluators)}");
        w.WriteLine();
        w.WriteLine($"OVERALL: {Verdict(exit)} (exit code {exit})");
        if (run.Run.Cancelled) w.WriteLine("The run was cancelled; results cover only the tests that completed.");
        w.WriteLine();

        w.WriteLine($"Tests: {a.TotalTests} total | {a.Passed} passed | {a.Failed} failed (wrong answer) | " +
                    $"{a.ApiErrors} RAG API errors | {a.EvaluatorErrors} evaluator errors");
        w.WriteLine("Metrics");
        Row(w, "Pass rate (evaluated tests)", Pct(a.PassRate));
        Row(w, "Answer correctness", Num(a.AnswerCorrectness));
        Row(w, "Faithfulness", Num(a.Faithfulness));
        Row(w, "Cosine similarity (avg)", Num(a.AverageCosineSimilarity));
        Row(w, "Exact match rate", Num(a.ExactMatchRate));
        Row(w, "Retrieval relevance", Num(a.RetrievalRelevance));
        Row(w, "Recall@1 / @3 / @5", $"{Num(a.RecallAt1)} / {Num(a.RecallAt3)} / {Num(a.RecallAt5)}");
        Row(w, "Mean expected source rank", Num(a.MeanExpectedSourceRank));
        Row(w, "Latency avg / p95", $"{Ms(a.AverageLatencyMs)} / {Ms(a.P95LatencyMs)}");
        Row(w, "Total execution time", Duration(a.TotalExecutionMs));

        if (run.QualityGates is { } gates)
        {
            w.WriteLine();
            w.WriteLine("Quality gates");
            foreach (var c in gates.Checks)
                w.WriteLine($"  [{(c.Passed ? "PASS" : "FAIL")}] {c.Name} {c.Comparison} {Num(c.Threshold)}  (actual {Num(c.Actual)}){(c is { Passed: false, Kind: GateKind.Reliability } ? "  <- inconclusive, not a quality drop" : "")}");
        }

        if (run.Regression is { } reg) WriteRegression(reg, w);

        w.WriteLine();
        w.WriteLine("Tests");
        foreach (var r in run.Results)
            w.WriteLine($"  [{Label(r.Outcome),-9}] {r.Id,-22} {Scores(r)}");

        var problems = run.Results.Where(r => r.Outcome != TestOutcome.Passed).ToList();
        if (problems.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Failures");
            foreach (var r in problems)
            {
                w.WriteLine($"  {r.Id} [{Label(r.Outcome)}] ({r.Category}, {r.Difficulty}): {r.Question}");
                w.WriteLine($"      reason: {r.FailureReason}");
                if (r.Outcome != TestOutcome.ApiError)
                {
                    w.WriteLine($"      expected: {r.ExpectedAnswer}");
                    w.WriteLine($"      actual:   {r.Answer ?? "(no answer)"}");
                }
            }
        }
    }

    public static void WriteRegression(RegressionReport reg, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine($"Regression vs baseline run {reg.BaselineRunId[..8]}: {(reg.HasRegression ? "REGRESSION DETECTED" : "no regression")}");
        foreach (var m in reg.Metrics)
        {
            var delta = m.Delta is null ? "n/a" : (m.Delta >= 0 ? "+" : "") + m.Delta.Value.ToString("0.000", CultureInfo.InvariantCulture);
            w.WriteLine($"  [{(m.Regressed ? "REGRESSED" : "ok"),-9}] {m.Metric,-24} {Num(m.Baseline),8} -> {Num(m.Current),8}  ({delta}; allowed {m.Allowed})");
        }
        if (reg.RegressedTests.Count > 0) w.WriteLine($"  Newly failing tests: {string.Join(", ", reg.RegressedTests)}");
        if (reg.ImprovedTests.Count > 0) w.WriteLine($"  Newly passing tests: {string.Join(", ", reg.ImprovedTests)}");
        if (reg.InconclusiveTests.Count > 0) w.WriteLine($"  Passed before, errored now (inconclusive): {string.Join(", ", reg.InconclusiveTests)}");
        if (reg.NewTests.Count > 0) w.WriteLine($"  New tests: {string.Join(", ", reg.NewTests)}");
        if (reg.RemovedTests.Count > 0) w.WriteLine($"  Removed tests: {string.Join(", ", reg.RemovedTests)}");
        foreach (var warning in reg.Warnings) w.WriteLine($"  warning: {warning}");
    }

    private static string Verdict(int exit) => exit switch
    {
        ExitCodes.Success => "PASS",
        ExitCodes.QualityFailure => "FAIL (quality gates / regression)",
        ExitCodes.Inconclusive => "INCONCLUSIVE (RAG API or evaluator errors)",
        ExitCodes.Cancelled => "CANCELLED",
        _ => "FAIL",
    };

    private static string Label(TestOutcome o) => o switch
    {
        TestOutcome.Passed => "PASS",
        TestOutcome.Failed => "FAIL",
        TestOutcome.ApiError => "API ERROR",
        _ => "EVAL ERR",
    };

    private static string Scores(TestResult r)
    {
        if (r.Outcome == TestOutcome.ApiError) return "(no response)";
        return string.Join(" ", r.EvaluatorResults.Select(e =>
        {
            var name = ShortNames.GetValueOrDefault(e.Evaluator, e.Evaluator);
            return e.Status switch
            {
                EvaluatorStatus.Skipped => $"{name}=skip",
                EvaluatorStatus.Error => $"{name}=ERR",
                _ => $"{name}={Num(e.Score)}{(e.Status == EvaluatorStatus.Failed ? "!" : "")}",
            };
        }));
    }

    private static void Row(TextWriter w, string label, string value) => w.WriteLine($"  {label,-30} {value}");
    private static string Num(double? v) => v is null ? "n/a" : v.Value.ToString("0.000", CultureInfo.InvariantCulture);
    private static string Pct(double? v) => v is null ? "n/a" : (v.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    private static string Ms(double? v) => v is null ? "n/a" : v.Value.ToString("0", CultureInfo.InvariantCulture) + " ms";
    private static string Duration(double ms) => ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0} s";
}
