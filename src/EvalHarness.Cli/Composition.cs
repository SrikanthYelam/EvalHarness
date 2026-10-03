using EvalHarness.Core;
using EvalHarness.Core.Http;
using EvalHarness.Evaluators;
using EvalHarness.Evaluators.Providers;
using EvalHarness.RagClient;
using EvalHarness.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace EvalHarness.Cli;

/// <summary>Wires configuration, HTTP pipelines (with retries), providers and the runner.</summary>
public static class Composition
{
    public static T Bind<T>(IConfiguration config, string section) where T : new() =>
        config.GetSection(section).Get<T>() ?? new T();

    public static ServiceProvider Build(IConfiguration config, Action<IServiceCollection>? configure = null)
    {
        var rag = Bind<RagApiOptions>(config, RagApiOptions.SectionName);
        var evaluation = Bind<EvaluationOptions>(config, EvaluationOptions.SectionName);
        var llm = Bind<LlmOptions>(config, LlmOptions.SectionName);
        var embedding = Bind<EmbeddingOptions>(config, EmbeddingOptions.SectionName);

        var services = new ServiceCollection();
        services.AddSingleton(rag);
        services.AddSingleton(evaluation);
        services.AddSingleton(Bind<QualityGateOptions>(config, QualityGateOptions.SectionName));
        services.AddSingleton(Bind<RegressionOptions>(config, RegressionOptions.SectionName));

        services.AddLogging(logging =>
        {
            logging.AddConfiguration(config.GetSection("Logging"));
            // Reports go to stdout; logs go to stderr so stdout stays clean for CI to capture.
            if (string.Equals(config["Logging:Format"], "json", StringComparison.OrdinalIgnoreCase))
                logging.AddJsonConsole(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ");
            else
                logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            logging.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        services.AddHttpClient<IRagClient, HttpRagClient>((http, sp) =>
                new HttpRagClient(http, rag, sp.GetRequiredService<ILogger<HttpRagClient>>()))
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan) // per-attempt timeouts live in RetryHandler
            .AddHttpMessageHandler(sp => Retry(sp, rag.MaxRetries, rag.RetryBaseDelayMs, rag.TimeoutSeconds));

        services.AddHttpClient<OpenAiChatClient>(http => new OpenAiChatClient(http, RequireOpenAi(llm)))
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(sp => Retry(sp, llm.MaxRetries, llm.RetryBaseDelayMs, llm.TimeoutSeconds));
        services.AddHttpClient<OpenAiEmbeddingClient>(http => new OpenAiEmbeddingClient(http, RequireOpenAi(embedding)))
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(sp => Retry(sp, embedding.MaxRetries, embedding.RetryBaseDelayMs, embedding.TimeoutSeconds));

        // To support another provider, register your own ILlmClient / IEmbeddingClient instead of these.
        services.AddSingleton<ILlmClient>(sp =>
            new ConcurrencyLimitedLlmClient(sp.GetRequiredService<OpenAiChatClient>(), llm.MaxConcurrency));
        services.AddSingleton<IEmbeddingClient>(sp =>
            new ConcurrencyLimitedEmbeddingClient(sp.GetRequiredService<OpenAiEmbeddingClient>(), embedding.MaxConcurrency));

        services.AddSingleton(sp => new EvaluationRunner(
            sp.GetRequiredService<IRagClient>(),
            EvaluatorCatalog.Create(evaluation, () => sp.GetRequiredService<ILlmClient>(), () => sp.GetRequiredService<IEmbeddingClient>()),
            evaluation.MaxParallelism,
            sp.GetRequiredService<ILogger<EvaluationRunner>>()));

        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static T RequireOpenAi<T>(T options) where T : ProviderOptions =>
        string.Equals(options.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase)
            ? options
            : throw new ConfigurationException(
                $"Unsupported provider '{options.Provider}'. Only 'OpenAI' (and OpenAI-compatible endpoints via BaseUrl) ship today; " +
                "register your own ILlmClient/IEmbeddingClient to add another.");

    private static RetryHandler Retry(IServiceProvider sp, int maxRetries, int baseDelayMs, int timeoutSeconds) =>
        new(new RetryPolicy
        {
            MaxRetries = maxRetries,
            BaseDelay = TimeSpan.FromMilliseconds(baseDelayMs),
            AttemptTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        }, sp.GetRequiredService<ILoggerFactory>().CreateLogger<RetryHandler>());
}
