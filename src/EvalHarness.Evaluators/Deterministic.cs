using System.Globalization;
using System.Text;
using EvalHarness.Core;

namespace EvalHarness.Evaluators;

public static class VectorMath
{
    /// <summary>Cosine similarity in [-1, 1]. A zero vector has no direction, so its similarity is defined as 0.</summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException($"Vectors must have the same length (got {a.Length} and {b.Length}).");

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;
        return Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1, 1);
    }
}

public static class TextNormalizer
{
    /// <summary>Case-, whitespace- and trailing-punctuation-insensitive form used by exact match.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.Normalize(NormalizationForm.FormKC).Trim())
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
            if (pendingSpace && builder.Length > 0) builder.Append(' ');
            pendingSpace = false;
            builder.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }
        return builder.ToString().TrimEnd('.', '!', '?', ' ');
    }
}

public sealed class ExactMatchEvaluator : IEvaluator
{
    public string Name => EvaluatorNames.ExactMatch;

    public Task<EvaluatorResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken)
    {
        var matches = TextNormalizer.Normalize(context.Response.Answer) == TextNormalizer.Normalize(context.TestCase.ExpectedAnswer);
        return Task.FromResult(EvaluatorResult.Of(Name, matches, matches ? 1 : 0,
            matches ? "Answer equals the expected answer after normalization." : "Answer differs from the expected answer after normalization."));
    }
}

/// <summary>
/// Embeds the expected and actual answers and reports their cosine similarity. This is a semantic-closeness signal:
/// a wrong answer that reuses the question's wording (or flips one number) can still score high, so it should not be
/// the only gate. See the README.
/// </summary>
public sealed class CosineSimilarityEvaluator(IEmbeddingClient embeddings, double threshold) : IEvaluator
{
    public string Name => EvaluatorNames.CosineSimilarity;

    public async Task<EvaluatorResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken)
    {
        var actual = context.Response.Answer;
        if (string.IsNullOrWhiteSpace(actual))
            return EvaluatorResult.Of(Name, false, 0, "The RAG API returned no answer text to compare.");

        var expectedTask = embeddings.EmbedAsync(context.TestCase.ExpectedAnswer, cancellationToken);
        var actualTask = embeddings.EmbedAsync(actual, cancellationToken);
        var expectedVector = await expectedTask;
        var actualVector = await actualTask;
        var similarity = VectorMath.CosineSimilarity(expectedVector, actualVector);

        return EvaluatorResult.Of(Name, similarity >= threshold, similarity,
            $"Cosine similarity {similarity:0.000} (threshold {threshold:0.00}). A semantic signal only; it does not prove the answer is correct.");
    }
}
