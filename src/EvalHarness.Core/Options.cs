namespace EvalHarness.Core;

/// <summary>
/// How to talk to the RAG API. The defaults match a common response shape (see README); point the mapping properties at another
/// API's JSON shape to evaluate it without code changes. Paths are dot-separated property names.
/// </summary>
public sealed class RagApiOptions
{
    public const string SectionName = "RagApi";

    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string AskPath { get; set; } = "/ask";

    /// <summary>GET path polled by the wait-for-ready check. Any non-5xx response counts as ready.</summary>
    public string ReadinessPath { get; set; } = "/openapi/v1.json";

    /// <summary>Seconds to wait for the API to become ready before running. 0 skips the check.</summary>
    public int WaitForReadySeconds { get; set; }

    /// <summary>Per-attempt timeout.</summary>
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>Extra request headers, e.g. Authorization. Supply secrets through environment variables.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    public string QuestionField { get; set; } = "question";

    /// <summary>Extra top-level request fields, e.g. topK=5 or mode=Hybrid. Numbers and booleans are sent as such.</summary>
    public Dictionary<string, string> RequestFields { get; set; } = new();

    public string AnswerPath { get; set; } = "answer";
    public string? StatusPath { get; set; } = "status";
    public string ChunksPath { get; set; } = "retrievedChunks";
    public string? ChunkIdField { get; set; } = "id";
    public string? DocumentIdField { get; set; }
    public string? DocumentNameField { get; set; } = "sourceFile";
    public string? ChunkTextField { get; set; } = "text";
    public string? ChunkScoreField { get; set; } = "score";
}

/// <summary>Per-evaluator overrides. Unset fields fall back to <see cref="EvaluationOptions.Defaults"/>.</summary>
public sealed class EvaluatorSettings
{
    public bool? Enabled { get; set; }

    /// <summary>When true, a failure of this evaluator fails the test case.</summary>
    public bool? Gating { get; set; }

    /// <summary>Minimum score to pass. Meaning is evaluator specific.</summary>
    public double? Threshold { get; set; }
}

public sealed record ResolvedEvaluatorSettings(bool Enabled, bool Gating, double Threshold);

public sealed class EvaluationOptions
{
    public const string SectionName = "Evaluation";

    /// <summary>Test cases evaluated concurrently against the RAG API.</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>K used by retrieval-quality's pass criterion and by retrieval-relevance.</summary>
    public int RetrievalK { get; set; } = 5;

    public Dictionary<string, EvaluatorSettings> Evaluators { get; set; } = new();

    public static readonly IReadOnlyDictionary<string, ResolvedEvaluatorSettings> Defaults = new Dictionary<string, ResolvedEvaluatorSettings>
    {
        [EvaluatorNames.ExactMatch] = new(Enabled: true, Gating: false, Threshold: 1.0),
        [EvaluatorNames.CosineSimilarity] = new(Enabled: true, Gating: false, Threshold: 0.80),
        [EvaluatorNames.AnswerCorrectness] = new(Enabled: true, Gating: true, Threshold: 0.75),
        [EvaluatorNames.Faithfulness] = new(Enabled: true, Gating: true, Threshold: 0.80),
        [EvaluatorNames.RetrievalQuality] = new(Enabled: true, Gating: true, Threshold: 1.0),
        [EvaluatorNames.RetrievalRelevance] = new(Enabled: false, Gating: false, Threshold: 0.40),
    };

    /// <summary>Configured settings for the evaluator (name match is case-insensitive), field by field over the defaults.</summary>
    public ResolvedEvaluatorSettings SettingsFor(string name)
    {
        var defaults = Defaults[name];
        var configured = Evaluators.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        return configured is null
            ? defaults
            : new ResolvedEvaluatorSettings(
                configured.Enabled ?? defaults.Enabled, configured.Gating ?? defaults.Gating, configured.Threshold ?? defaults.Threshold);
    }
}

/// <summary>Provider settings shared by the judge LLM and the embedding model. Only OpenAI-compatible APIs ship today.</summary>
public class ProviderOptions
{
    public string Provider { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";

    /// <summary>Name of the environment variable holding the key. Keys never belong in config files.</summary>
    public string ApiKeyEnvVar { get; set; } = "OPENAI_API_KEY";

    /// <summary>Overrides ApiKeyEnvVar; intended for EVALHARNESS_Llm__ApiKey style environment configuration.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Upper bound on simultaneous provider calls, to stay under rate limits.</summary>
    public int MaxConcurrency { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 4;
    public int RetryBaseDelayMs { get; set; } = 1000;

    public string ResolveApiKey()
    {
        var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key))
            throw new ConfigurationException(
                $"No API key found for provider '{Provider}'. Set the {ApiKeyEnvVar} environment variable " +
                "(or run only evaluators that need no provider, e.g. --evaluators exact-match,retrieval-quality).");
        return key;
    }
}

public sealed class LlmOptions : ProviderOptions
{
    public const string SectionName = "Llm";
    public LlmOptions() => Model = "gpt-4o-mini";
}

public sealed class EmbeddingOptions : ProviderOptions
{
    public const string SectionName = "Embedding";
    public EmbeddingOptions() => Model = "text-embedding-3-small";
}

/// <summary>Absolute bars a single run must clear. Null disables a gate.</summary>
public sealed class QualityGateOptions
{
    public const string SectionName = "QualityGates";

    /// <summary>Over tests that were actually evaluated (errors excluded).</summary>
    public double? MinPassRate { get; set; } = 0.80;
    public double? MinAnswerCorrectness { get; set; } = 0.70;
    public double? MinFaithfulness { get; set; } = 0.80;
    public double? MinCosineSimilarity { get; set; }
    public double? MinRecallAt1 { get; set; }
    public double? MinRecallAt3 { get; set; }
    public double? MinRecallAt5 { get; set; } = 0.80;
    public double? MaxAverageLatencyMs { get; set; }

    /// <summary>Fraction of tests that may hit RAG API failures before the run is deemed inconclusive.</summary>
    public double? MaxApiErrorRate { get; set; } = 0.0;
    public double? MaxEvaluatorErrorRate { get; set; } = 0.0;
}

/// <summary>Acceptable degradation versus the baseline run.</summary>
public sealed class RegressionOptions
{
    public const string SectionName = "Regression";

    // Absolute drops tolerated on 0..1 metrics (0.05 = five percentage points).
    public double MaxPassRateDrop { get; set; } = 0.05;
    public double MaxAnswerCorrectnessDrop { get; set; } = 0.05;
    public double MaxFaithfulnessDrop { get; set; } = 0.05;
    public double MaxCosineSimilarityDrop { get; set; } = 0.05;
    public double MaxRecallAt1Drop { get; set; } = 0.05;
    public double MaxRecallAt3Drop { get; set; } = 0.05;
    public double MaxRecallAt5Drop { get; set; } = 0.05;

    /// <summary>Tolerated relative increase of average latency, in percent.</summary>
    public double MaxLatencyIncreasePercent { get; set; } = 25;

    /// <summary>Tests that passed in the baseline but fail now. Null reports them without gating.</summary>
    public int? MaxNewFailingTests { get; set; }
}
