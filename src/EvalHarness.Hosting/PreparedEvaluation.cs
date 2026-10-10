using EvalHarness.Core;
using EvalHarness.Evaluators;
using EvalHarness.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EvalHarness.Hosting;

/// <summary>
/// A fully wired evaluation for one configuration: validated settings, a RAG client, evaluators and a runner.
/// Shared by the CLI and the API so both behave identically. Creating it fails fast (with a
/// <see cref="ConfigurationException"/>) on bad settings or a missing API key, before any request is sent.
/// </summary>
public sealed class PreparedEvaluation : IAsyncDisposable
{
    public const int MaxRepeats = 20;

    private readonly ServiceProvider _services;
    private readonly EvaluationRunner _runner;
    private readonly RagApiOptions _ragOptions;
    private readonly QualityGateOptions _gates;
    private readonly RegressionOptions _regression;

    /// <summary>The RAG API location recorded in reports: scheme, host and path only (no credentials or query).</summary>
    public string Target { get; }

    /// <summary>What the report will record about how this evaluation is configured (no secrets).</summary>
    public RunSettings Settings { get; }

    /// <exception cref="ConfigurationException"/>
    public static PreparedEvaluation Create(IConfiguration config, Action<IServiceCollection>? configureServices = null)
    {
        var rag = Composition.Bind<RagApiOptions>(config, RagApiOptions.SectionName);
        var evaluation = Composition.Bind<EvaluationOptions>(config, EvaluationOptions.SectionName);
        if (rag.WaitForReadySeconds < 0)
            throw new ConfigurationException("RagApi:WaitForReadySeconds must be >= 0.");
        if (evaluation.MaxParallelism < 1)
            throw new ConfigurationException("Evaluation:MaxParallelism must be >= 1.");
        if (evaluation.Repeats is < 1 or > MaxRepeats)
            throw new ConfigurationException($"Evaluation:Repeats must be between 1 and {MaxRepeats}.");
        if (!Uri.TryCreate(rag.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new ConfigurationException($"RagApi:BaseUrl '{rag.BaseUrl}' is not a valid http(s) URL.");

        var services = Composition.Build(config, configureServices);
        try
        {
            // Resolving the runner builds the evaluators, which is where a missing API key surfaces.
            var runner = services.GetRequiredService<EvaluationRunner>();
            return new PreparedEvaluation(
                services, runner, rag,
                Composition.Bind<QualityGateOptions>(config, QualityGateOptions.SectionName),
                Composition.Bind<RegressionOptions>(config, RegressionOptions.SectionName),
                baseUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped),
                BuildSettings(evaluation, rag,
                    Composition.Bind<LlmOptions>(config, LlmOptions.SectionName),
                    Composition.Bind<EmbeddingOptions>(config, EmbeddingOptions.SectionName)));
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    private PreparedEvaluation(
        ServiceProvider services, EvaluationRunner runner, RagApiOptions rag, QualityGateOptions gates,
        RegressionOptions regression, string target, RunSettings settings)
    {
        (_services, _runner, _ragOptions, _gates, _regression, Target, Settings) =
            (services, runner, rag, gates, regression, target, settings);
    }

    private static RunSettings BuildSettings(EvaluationOptions evaluation, RagApiOptions rag, LlmOptions llm, EmbeddingOptions embedding)
    {
        var enabled = EvaluatorCatalog.All.Where(info => evaluation.SettingsFor(info.Name).Enabled).ToList();
        return new RunSettings(
            JudgeModel: enabled.Any(i => i.NeedsLlm) ? $"{llm.Provider}/{llm.Model}" : null,
            EmbeddingModel: enabled.Any(i => i.NeedsEmbeddings) ? $"{embedding.Provider}/{embedding.Model}" : null,
            PromptsFingerprint: JudgePrompts.Fingerprint,
            RetrievalK: evaluation.RetrievalK,
            Repeats: evaluation.Repeats,
            RequestFields: new SortedDictionary<string, string>(rag.RequestFields),
            Evaluators: enabled.ToDictionary(
                i => i.Name,
                i => new EvaluatorSetting(evaluation.SettingsFor(i.Name).Gating, evaluation.SettingsFor(i.Name).Threshold)));
    }

    /// <summary>
    /// Waits for the RAG API (when configured), runs the dataset, then applies the quality gates and, when a
    /// baseline is given, the regression check. Cancellation during the run returns a partial report flagged as
    /// cancelled; cancellation while waiting for the API throws.
    /// </summary>
    /// <exception cref="RagApiException">The RAG API did not become ready in time.</exception>
    public async Task<EvaluationRun> ExecuteAsync(
        Dataset dataset, EvaluationRun? baseline, CancellationToken cancellationToken, Action<TestResult>? onTestCompleted = null)
    {
        if (_ragOptions.WaitForReadySeconds > 0)
            await _services.GetRequiredService<IRagClient>()
                .WaitUntilReadyAsync(TimeSpan.FromSeconds(_ragOptions.WaitForReadySeconds), cancellationToken);

        var run = await _runner.RunAsync(dataset, Target, cancellationToken, onTestCompleted);
        run = run with { Run = run.Run with { Settings = Settings } }; // before the comparison, which reads it
        return run with
        {
            QualityGates = QualityGateEvaluator.Evaluate(run.Aggregate, _gates, run.Run.Evaluators),
            Regression = baseline is null ? null : RegressionDetector.Compare(baseline, run, _regression),
        };
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
