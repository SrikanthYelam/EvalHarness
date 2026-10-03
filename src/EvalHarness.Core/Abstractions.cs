namespace EvalHarness.Core;

public sealed record EvaluationContext(TestCase TestCase, RagResponse Response);

/// <summary>
/// Scores one aspect of a RAG response. Implementations may throw when they cannot produce a verdict
/// (e.g. the judge LLM is unreachable); the runner records that as an evaluator error, not a wrong answer.
/// </summary>
public interface IEvaluator
{
    string Name { get; }
    Task<EvaluatorResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken);
}

/// <summary>An evaluator plus how the runner should treat it.</summary>
public sealed record ConfiguredEvaluator(IEvaluator Evaluator, bool Gating);

/// <summary>The only place the harness talks to the RAG system under test.</summary>
public interface IRagClient
{
    /// <exception cref="RagApiException">The API could not produce a usable response.</exception>
    Task<RagResponse> AskAsync(string question, CancellationToken cancellationToken);

    /// <exception cref="RagApiException">The API was not ready before the timeout.</exception>
    Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Generates a JSON object from a prompt. Implementations hide the LLM provider.</summary>
public interface ILlmClient
{
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}

public static class EvaluatorNames
{
    public const string ExactMatch = "exact-match";
    public const string CosineSimilarity = "cosine-similarity";
    public const string AnswerCorrectness = "answer-correctness";
    public const string Faithfulness = "faithfulness";
    public const string RetrievalQuality = "retrieval-quality";
    public const string RetrievalRelevance = "retrieval-relevance";

    public static readonly IReadOnlyList<string> All =
        [ExactMatch, CosineSimilarity, AnswerCorrectness, Faithfulness, RetrievalQuality, RetrievalRelevance];
}

/// <summary>Invalid or incomplete configuration (missing API key, unknown provider, ...). Not a run failure.</summary>
public sealed class ConfigurationException(string message) : Exception(message);
