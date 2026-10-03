using System.Text.Json;
using EvalHarness.Core;
using EvalHarness.Evaluators;
using EvalHarness.Reporting;
using EvalHarness.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EvalHarness.Cli;

/// <summary>
/// Non-interactive command dispatcher. Exit codes: 0 pass, 1 quality gate/regression failure, 2 usage/config/dataset
/// error, 3 inconclusive (RAG API or evaluator errors), 130 cancelled. See <see cref="ExitCodes"/>.
/// </summary>
public static class CliApp
{
    private const string Usage = """
        EvalHarness - evaluate a RAG Q&A API over HTTP.

        Usage:
          EvalHarness run --dataset <file> [--output <dir>] [--baseline <report.json>] [--rag-url <url>]
                          [--parallelism <n>] [--evaluators <a,b,...>] [--wait-for-ready <seconds>] [--config <file>]
              Run the dataset against the RAG API, write a JSON report, print a console report, apply quality gates
              and (with --baseline, or the Baseline setting / EVALHARNESS_Baseline) regression checks.
          EvalHarness compare --baseline <report.json> --current <report.json> [--output <file>] [--config <file>]
              Compare two saved reports. Exits 1 on regression.
          EvalHarness validate --dataset <file>
              Check a dataset file without calling any API.
          EvalHarness list-evaluators
              List the available evaluators and their defaults.

        Configuration: appsettings.json next to the executable, then --config <file>, then EVALHARNESS_* environment
        variables (e.g. EVALHARNESS_RagApi__BaseUrl), then command-line options.

        Exit codes: 0 pass | 1 quality gate failed or regression | 2 usage/config/dataset error |
                    3 inconclusive (RAG API or evaluator errors) | 130 cancelled
        """;

    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken,
        Action<IServiceCollection>? configureServices = null)
    {
        try
        {
            var arguments = Arguments.Parse(args);
            if (arguments.Help)
            {
                stdout.WriteLine(Usage);
                return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
            }

            return arguments.Command switch
            {
                "run" => await RunCommandAsync(arguments, stdout, stderr, cancellationToken, configureServices),
                "compare" => await CompareCommandAsync(arguments, stdout, cancellationToken),
                "validate" => Validate(arguments, stdout, stderr),
                "list-evaluators" => ListEvaluators(stdout),
                _ => throw new UsageException($"Unknown command '{arguments.Command}'."),
            };
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            stderr.WriteLine("Run 'EvalHarness --help' for usage.");
            return ExitCodes.UsageError;
        }
        catch (Exception ex) when (ex is ConfigurationException or DatasetValidationException or InvalidDataException
                                       or FileNotFoundException or DirectoryNotFoundException or FormatException)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static async Task<int> RunCommandAsync(
        Arguments args, TextWriter stdout, TextWriter stderr, CancellationToken ct, Action<IServiceCollection>? configureServices)
    {
        var datasetPath = args.Require("dataset");
        var overrides = new Dictionary<string, string?>();
        if (args.Get("rag-url") is { } url) overrides["RagApi:BaseUrl"] = url;
        if (args.Get("wait-for-ready") is { } wait) overrides["RagApi:WaitForReadySeconds"] = wait;
        if (args.Get("parallelism") is { } parallelism) overrides["Evaluation:MaxParallelism"] = parallelism;
        if (args.Get("evaluators") is { } list)
        {
            var selected = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = selected.Where(n => !EvaluatorNames.All.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                throw new UsageException($"Unknown evaluator(s): {string.Join(", ", unknown)}. See 'list-evaluators'.");
            foreach (var name in EvaluatorNames.All)
                overrides[$"Evaluation:Evaluators:{name}:Enabled"] =
                    selected.Contains(name, StringComparer.OrdinalIgnoreCase) ? "true" : "false";
        }

        var config = BuildConfiguration(args.Get("config"), overrides);
        var ragOptions = Composition.Bind<RagApiOptions>(config, RagApiOptions.SectionName);
        if (ragOptions.WaitForReadySeconds < 0 || Composition.Bind<EvaluationOptions>(config, EvaluationOptions.SectionName).MaxParallelism < 1)
            throw new UsageException("--wait-for-ready must be >= 0 and --parallelism must be >= 1.");
        if (!Uri.TryCreate(ragOptions.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new UsageException($"RagApi:BaseUrl '{ragOptions.BaseUrl}' is not a valid http(s) URL.");

        // Fail fast, before spending any LLM budget, on anything that would make the run unusable.
        var dataset = DatasetLoader.Load(datasetPath, EvaluatorNames.All);
        var baselinePath = args.Get("baseline") ?? config["Baseline"]; // config/env fallback lets Docker Compose supply it
        var baseline = string.IsNullOrWhiteSpace(baselinePath) ? null : await JsonReportStore.ReadAsync(baselinePath, ct);

        await using var services = Composition.Build(config, configureServices);
        var runner = services.GetRequiredService<EvaluationRunner>(); // throws ConfigurationException (e.g. missing API key)

        if (ragOptions.WaitForReadySeconds > 0)
        {
            try
            {
                await services.GetRequiredService<IRagClient>()
                    .WaitUntilReadyAsync(TimeSpan.FromSeconds(ragOptions.WaitForReadySeconds), ct);
            }
            catch (RagApiException ex)
            {
                stderr.WriteLine($"error: {ex.Message}");
                return ExitCodes.Inconclusive;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stderr.WriteLine("Cancelled while waiting for the RAG API.");
                return ExitCodes.Cancelled;
            }
        }

        var run = await runner.RunAsync(dataset, baseUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped), ct);
        run = run with
        {
            QualityGates = QualityGateEvaluator.Evaluate(run.Aggregate, Composition.Bind<QualityGateOptions>(config, QualityGateOptions.SectionName), run.Run.Evaluators),
            Regression = baseline is null
                ? null
                : RegressionDetector.Compare(baseline, run, Composition.Bind<RegressionOptions>(config, RegressionOptions.SectionName)),
        };

        // Written even for a cancelled run so partial results are not lost.
        var reportPath = await JsonReportStore.WriteToDirectoryAsync(run, args.Get("output") ?? "reports", CancellationToken.None);
        ConsoleReportWriter.Write(run, stdout);
        stdout.WriteLine();
        stdout.WriteLine($"JSON report: {reportPath}");
        return ExitCodes.For(run);
    }

    private static async Task<int> CompareCommandAsync(Arguments args, TextWriter stdout, CancellationToken ct)
    {
        var baselinePath = args.Require("baseline");
        var currentPath = args.Require("current");
        var config = BuildConfiguration(args.Get("config"), new Dictionary<string, string?>());

        var baseline = await JsonReportStore.ReadAsync(baselinePath, ct);
        var current = await JsonReportStore.ReadAsync(currentPath, ct);
        var report = RegressionDetector.Compare(baseline, current, Composition.Bind<RegressionOptions>(config, RegressionOptions.SectionName));

        ConsoleReportWriter.WriteRegression(report, stdout);
        if (args.Get("output") is { } output)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(output)) is { } dir) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, EvalJson.Options), ct);
            stdout.WriteLine($"Comparison written to {output}");
        }
        return report.HasRegression ? ExitCodes.QualityFailure : ExitCodes.Success;
    }

    private static int Validate(Arguments args, TextWriter stdout, TextWriter stderr)
    {
        var path = args.Require("dataset");
        try
        {
            var dataset = DatasetLoader.Load(path, EvaluatorNames.All);
            stdout.WriteLine($"OK: '{dataset.Name}' has {dataset.TestCases.Count} valid test cases (sha256 {dataset.Sha256[..12]}).");
            stdout.WriteLine("  Categories: " + string.Join(", ", dataset.TestCases.GroupBy(t => t.Category).Select(g => $"{g.Key} ({g.Count()})")));
            stdout.WriteLine("  Difficulty: " + string.Join(", ", dataset.TestCases.GroupBy(t => t.Difficulty.ToLowerInvariant()).Select(g => $"{g.Key} ({g.Count()})")));
            return ExitCodes.Success;
        }
        catch (DatasetValidationException ex)
        {
            stderr.WriteLine($"Dataset '{path}' is invalid:");
            foreach (var error in ex.Errors) stderr.WriteLine($"  - {error}");
            return ExitCodes.UsageError;
        }
    }

    private static int ListEvaluators(TextWriter stdout)
    {
        foreach (var info in EvaluatorCatalog.All)
        {
            var d = EvaluationOptions.Defaults[info.Name];
            var needs = info.NeedsLlm ? "needs LLM" : info.NeedsEmbeddings ? "needs embeddings" : "no provider";
            stdout.WriteLine($"{info.Name,-20} {(d.Enabled ? "enabled" : "disabled"),-9} {(d.Gating ? "gating" : "informational"),-14} {needs}");
            stdout.WriteLine($"    {info.Description}");
        }
        return ExitCodes.Success;
    }

    private static IConfiguration BuildConfiguration(string? configFile, Dictionary<string, string?> overrides)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);
        if (configFile is not null) builder.AddJsonFile(Path.GetFullPath(configFile), optional: false);
        return builder.AddEnvironmentVariables("EVALHARNESS_").AddInMemoryCollection(overrides).Build();
    }
}
