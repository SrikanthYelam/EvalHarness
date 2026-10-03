using EvalHarness.Core;
using EvalHarness.Evaluators;

namespace EvalHarness.Tests;

public class VectorMathTests
{
    [Fact]
    public void Identical_vectors_have_similarity_one() =>
        Assert.Equal(1, VectorMath.CosineSimilarity([1, 2, 3], [1, 2, 3]), 6);

    [Fact]
    public void Orthogonal_vectors_have_similarity_zero() =>
        Assert.Equal(0, VectorMath.CosineSimilarity([1, 0], [0, 1]), 6);

    [Fact]
    public void Opposite_vectors_have_similarity_minus_one() =>
        Assert.Equal(-1, VectorMath.CosineSimilarity([1, 2], [-1, -2]), 6);

    [Fact]
    public void Similarity_ignores_magnitude() =>
        Assert.Equal(1, VectorMath.CosineSimilarity([1, 2, 3], [10, 20, 30]), 6);

    [Fact]
    public void Known_value() =>
        Assert.Equal(0.7071067, VectorMath.CosineSimilarity([1, 0], [1, 1]), 6);

    [Fact]
    public void Zero_vector_has_similarity_zero() =>
        Assert.Equal(0, VectorMath.CosineSimilarity([0, 0], [1, 1]));

    [Fact]
    public void Different_lengths_throw() =>
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([1, 2], [1, 2, 3]));
}

public class ExactMatchTests
{
    private static Task<EvaluatorResult> Run(string? actual, string expected) =>
        new ExactMatchEvaluator().EvaluateAsync(Build.Context(Build.Case(expected: expected), Build.Response(actual)), default);

    [Theory]
    [InlineData("18 days", "18 days")]
    [InlineData("18 DAYS", "18 days")]
    [InlineData("  18   days \n", "18 days")]
    [InlineData("18 days.", "18 days")]
    [InlineData("18 days!", "18 days?")]
    public async Task Matches_after_normalisation(string actual, string expected)
    {
        var result = await Run(actual, expected);
        Assert.Equal(EvaluatorStatus.Passed, result.Status);
        Assert.Equal(1, result.Score);
    }

    [Theory]
    [InlineData("19 days", "18 days")]
    [InlineData("18 days per year", "18 days")]
    [InlineData(null, "18 days")]
    [InlineData("", "18 days")]
    public async Task Does_not_match_different_text(string? actual, string expected)
    {
        var result = await Run(actual, expected);
        Assert.Equal(EvaluatorStatus.Failed, result.Status);
        Assert.Equal(0, result.Score);
    }
}

public class CosineSimilarityEvaluatorTests
{
    private static readonly Dictionary<string, float[]> Vectors = new()
    {
        ["expected"] = [1, 0],
        ["close"] = [1, 0.1f],
        ["far"] = [0, 1],
    };

    private static CosineSimilarityEvaluator Evaluator(double threshold = 0.8) =>
        new(new FakeEmbedder(t => Vectors[t]), threshold);

    private static EvaluationContext Context(string? answer) =>
        Build.Context(Build.Case(expected: "expected"), Build.Response(answer));

    [Fact]
    public async Task Similar_answer_passes_and_reports_score()
    {
        var result = await Evaluator().EvaluateAsync(Context("close"), default);
        Assert.Equal(EvaluatorStatus.Passed, result.Status);
        Assert.InRange(result.Score!.Value, 0.99, 1.0);
    }

    [Fact]
    public async Task Dissimilar_answer_fails()
    {
        var result = await Evaluator().EvaluateAsync(Context("far"), default);
        Assert.Equal(EvaluatorStatus.Failed, result.Status);
        Assert.Equal(0, result.Score!.Value, 6);
    }

    [Fact]
    public async Task Missing_answer_scores_zero_without_embedding()
    {
        var result = await Evaluator().EvaluateAsync(Context(null), default);
        Assert.Equal(EvaluatorStatus.Failed, result.Status);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public async Task Embedding_failure_propagates_for_the_runner_to_classify()
    {
        var evaluator = new CosineSimilarityEvaluator(new FakeEmbedder(_ => throw new InvalidOperationException("boom")), 0.8);
        await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(Context("close"), default));
    }
}

public class RetrievalMetricsTests
{
    private static ExpectedSource Doc(string name) => new() { DocumentName = name };

    [Fact]
    public void Ranks_are_one_based_positions_of_first_match()
    {
        var chunks = new[] { Build.Chunk(1, doc: "a.md"), Build.Chunk(2, doc: "b.md"), Build.Chunk(3, doc: "b.md") };
        var metrics = RetrievalMetrics.Compute([Doc("b.md"), Doc("z.md")], chunks);

        Assert.Equal(new int?[] { 2, null }, metrics.Ranks);
        Assert.Equal(2, metrics.BestRank);
    }

    [Fact]
    public void Recall_at_k_is_fraction_of_expected_sources_within_k()
    {
        var chunks = new[] { Build.Chunk(1, doc: "a.md"), Build.Chunk(2, doc: "b.md"), Build.Chunk(3, doc: "c.md") };
        var metrics = RetrievalMetrics.Compute([Doc("a.md"), Doc("c.md"), Doc("missing.md"), Doc("b.md")], chunks);

        Assert.Equal(0.25, metrics.RecallAt(1));
        Assert.Equal(0.75, metrics.RecallAt(3));
        Assert.Equal(0.75, metrics.RecallAt(5));
    }

    [Fact]
    public void Nothing_found_means_no_rank_and_zero_recall()
    {
        var metrics = RetrievalMetrics.Compute([Doc("x.md")], [Build.Chunk(1, doc: "a.md")]);
        Assert.Null(metrics.BestRank);
        Assert.Equal(0, metrics.RecallAt(5));
    }

    [Fact]
    public void Matches_by_document_id_name_chunk_id_and_text()
    {
        var chunk = new RetrievedChunk(1, "doc-7", "docs/Handbook.md", "chunk-9", "Receipts are required over $25.", 0.5);

        Assert.True(SourceMatcher.Matches(new ExpectedSource { DocumentId = "DOC-7" }, chunk));
        Assert.True(SourceMatcher.Matches(new ExpectedSource { DocumentName = "handbook.md" }, chunk));
        Assert.True(SourceMatcher.Matches(new ExpectedSource { ChunkId = "chunk-9" }, chunk));
        Assert.True(SourceMatcher.Matches(new ExpectedSource { ChunkContains = "over $25" }, chunk));
        Assert.False(SourceMatcher.Matches(new ExpectedSource { DocumentId = "doc-8" }, chunk));
        Assert.False(SourceMatcher.Matches(new ExpectedSource { ChunkId = "chunk-1" }, chunk));
    }

    [Fact]
    public void Every_identifier_set_on_the_expected_source_must_match()
    {
        var chunk = new RetrievedChunk(1, null, "handbook.md", "chunk-9", "some text", null);

        Assert.True(SourceMatcher.Matches(new ExpectedSource { DocumentName = "handbook.md", ChunkId = "chunk-9" }, chunk));
        Assert.False(SourceMatcher.Matches(new ExpectedSource { DocumentName = "handbook.md", ChunkId = "chunk-1" }, chunk));
        Assert.False(SourceMatcher.Matches(new ExpectedSource { DocumentId = "x" }, chunk)); // chunk has no document id
    }

    [Fact]
    public void Empty_expected_source_never_matches() =>
        Assert.False(SourceMatcher.Matches(new ExpectedSource(), Build.Chunk(1)));
}

public class RetrievalQualityEvaluatorTests
{
    private static Task<EvaluatorResult> Run(int k, double threshold, string expectedDoc, params RetrievedChunk[] chunks) =>
        new RetrievalQualityEvaluator(k, threshold).EvaluateAsync(
            Build.Context(Build.Case(sources: new ExpectedSource { DocumentName = expectedDoc }), Build.Response("A", chunks)), default);

    [Fact]
    public async Task Reports_recall_and_rank_when_found()
    {
        var result = await Run(5, 1.0, "b.md", Build.Chunk(1, doc: "a.md"), Build.Chunk(2, doc: "b.md"));

        Assert.Equal(EvaluatorStatus.Passed, result.Status);
        Assert.Equal(0, result.Metrics!["recall@1"]);
        Assert.Equal(1, result.Metrics["recall@3"]);
        Assert.Equal(1, result.Metrics["recall@5"]);
        Assert.Equal(2, result.Metrics["expectedSourceRank"]);
    }

    [Fact]
    public async Task Fails_when_expected_source_is_outside_k()
    {
        var result = await Run(1, 1.0, "b.md", Build.Chunk(1, doc: "a.md"), Build.Chunk(2, doc: "b.md"));
        Assert.Equal(EvaluatorStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Fails_and_explains_when_never_retrieved()
    {
        var result = await Run(5, 1.0, "b.md", Build.Chunk(1, doc: "a.md"));

        Assert.Equal(EvaluatorStatus.Failed, result.Status);
        Assert.False(result.Metrics!.ContainsKey("expectedSourceRank"));
        Assert.Contains("not retrieved", result.Explanation);
    }

    [Fact]
    public async Task Skips_when_the_test_lists_no_expected_sources()
    {
        var test = Build.Case() with { ExpectedSources = [] };
        var result = await new RetrievalQualityEvaluator(5, 1).EvaluateAsync(Build.Context(test), default);
        Assert.Equal(EvaluatorStatus.Skipped, result.Status);
    }
}
