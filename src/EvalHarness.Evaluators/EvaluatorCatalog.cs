using EvalHarness.Core;

namespace EvalHarness.Evaluators;

public sealed record EvaluatorInfo(string Name, string Description, bool NeedsLlm, bool NeedsEmbeddings);

public static class EvaluatorCatalog
{
    public static readonly IReadOnlyList<EvaluatorInfo> All =
    [
        new(EvaluatorNames.ExactMatch, "Answer equals the expected answer after normalising case, whitespace and trailing punctuation.", false, false),
        new(EvaluatorNames.CosineSimilarity, "Cosine similarity of expected vs actual answer embeddings. A semantic signal, not correctness.", false, true),
        new(EvaluatorNames.AnswerCorrectness, "LLM judge compares the answer with the expected answer (score, pass/fail, explanation).", true, false),
        new(EvaluatorNames.Faithfulness, "LLM judge checks every claim in the answer against the retrieved context (hallucination detection).", true, false),
        new(EvaluatorNames.RetrievalQuality, "Was the expected source retrieved? Recall@1/3/5 and expected-source rank; no LLM needed.", false, false),
        new(EvaluatorNames.RetrievalRelevance, "Optional. LLM judge rates whether each retrieved chunk is relevant to the question.", true, false),
    ];

    /// <summary>
    /// Builds the enabled evaluators. Providers are created lazily, so a missing API key only matters when an
    /// evaluator that needs that provider is enabled.
    /// </summary>
    /// <exception cref="ConfigurationException"/>
    public static IReadOnlyList<ConfiguredEvaluator> Create(
        EvaluationOptions options, Func<ILlmClient> llm, Func<IEmbeddingClient> embeddings)
    {
        foreach (var name in options.Evaluators.Keys.Where(n => !EvaluatorNames.All.Contains(n, StringComparer.OrdinalIgnoreCase)))
            throw new ConfigurationException($"Unknown evaluator '{name}' in configuration. Known: {string.Join(", ", EvaluatorNames.All)}.");

        var evaluators = new List<ConfiguredEvaluator>();
        foreach (var name in EvaluatorNames.All)
        {
            var settings = options.SettingsFor(name);
            if (!settings.Enabled) continue;

            var threshold = settings.Threshold;
            IEvaluator evaluator = name switch
            {
                EvaluatorNames.ExactMatch => new ExactMatchEvaluator(),
                EvaluatorNames.CosineSimilarity => new CosineSimilarityEvaluator(embeddings(), threshold),
                EvaluatorNames.AnswerCorrectness => new AnswerCorrectnessEvaluator(llm(), threshold),
                EvaluatorNames.Faithfulness => new FaithfulnessEvaluator(llm(), threshold),
                EvaluatorNames.RetrievalQuality => new RetrievalQualityEvaluator(options.RetrievalK, threshold),
                EvaluatorNames.RetrievalRelevance => new RetrievalRelevanceEvaluator(llm(), options.RetrievalK, threshold),
                _ => throw new ConfigurationException($"Unknown evaluator '{name}'."),
            };
            evaluators.Add(new ConfiguredEvaluator(evaluator, settings.Gating));
        }
        return evaluators;
    }
}
